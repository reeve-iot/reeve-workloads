using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Reeve.Pipeline;
using Reeve.Workloads.Abstractions;
using Reeve.Workloads.Abstractions.Update;
using Reeve.Workloads.Core;
using Reeve.Workloads.DependencyInjection;
using Xunit;

namespace Reeve.Workloads.Core.Tests.Hosting;

public sealed class ReeveWorkloadsRunExtensionsTests
{
    [Fact]
    public async Task It_refuses_to_start_when_it_was_asked_to_migrate_and_nothing_can()
    {
        var log = new CallLog();
        await using var provider = Compose(log);

        var act = () => provider.RunReeveWorkloadsAsync();

        var failure = (await act.Should().ThrowAsync<InvalidOperationException>()).Subject.Single();

        failure.Message.Should().Contain(nameof(IWorkloadsSchemaMigrator));
        failure.Message.Should().Contain("AddEntityFrameworkStores");
        failure.Message.Should().Contain("runMigrations: false");

        log.Entries.Should().BeEmpty("an engine must not contend for a leadership over a schema nobody vouched for");
    }

    [Fact]
    public async Task It_names_AddEngine_when_the_engine_was_never_composed()
    {
        await using var provider = new ServiceCollection().BuildServiceProvider();

        var act = () => provider.RunReeveWorkloadsAsync(runMigrations: false);

        var failure = (await act.Should().ThrowAsync<InvalidOperationException>()).Subject.Single();

        failure.Message.Should().Contain("AddEngine");
        failure.Message.Should().NotContain(nameof(WorkloadsEngineStartSignal), "the host cannot name an internal type, so telling it one is telling it nothing");
    }

    [Fact]
    public async Task It_refuses_to_start_when_a_host_stage_is_placed_after_the_parameter_commit()
    {
        var log = new CallLog();
        var services = Collection(
            log, hostStage: new DoubleStage("stage.host.too-late", StagePlacement.After(WorkloadUpdateStages.CommitParams)));
        services.AddSingleton<IWorkloadsSchemaMigrator>(new RecordingSchemaMigrator(log));
        await using var provider = services.BuildServiceProvider();

        var act = () => provider.RunReeveWorkloadsAsync();

        var failure = (await act.Should().ThrowAsync<InvalidOperationException>()).Subject.Single();
        failure.Message.Should().Contain("stage.host.too-late");
        failure.InnerException.Should().BeOfType<PipelineDefinitionException>();

        log.Entries.Should().BeEmpty();
        provider.GetRequiredService<WorkloadsEngineStartSignal>().WasStarted.Should().BeFalse();
    }

    [Theory]
    [InlineData("two certifiers")]
    [InlineData("overlapping contributors")]
    public async Task It_refuses_to_start_on_seams_that_overlap(string overlap)
    {
        var log = new CallLog();
        var services = Collection(log);
        services.AddSingleton<IWorkloadsSchemaMigrator>(new RecordingSchemaMigrator(log));
        if (overlap == "two certifiers")
        {
            services.AddScoped<Reeve.Workloads.Core.HealthGate.IRolloutCertifier>(_ => new RecordingRolloutCertifier());
            services.AddScoped<Reeve.Workloads.Core.HealthGate.IRolloutCertifier>(_ => new RecordingRolloutCertifier());
        }
        else
        {
            services.AddScoped<IWorkloadHealthContributor, FakeHostHealthContributor>();
            services.AddScoped<IWorkloadHealthContributor, FakeHostHealthContributor>();
        }

        await using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });

        var act = () => provider.RunReeveWorkloadsAsync();

        await act.Should().ThrowAsync<InvalidOperationException>();
        log.Entries.Should().BeEmpty("nothing is migrated, resolved or started for a composition that cannot run");
        provider.GetRequiredService<WorkloadsEngineStartSignal>().WasStarted.Should().BeFalse();
    }

    [Fact]
    public async Task One_certifier_and_one_contributor_per_mode_start_the_engine()
    {
        var log = new CallLog();
        var services = Collection(log);
        services.AddScoped<Reeve.Workloads.Core.HealthGate.IRolloutCertifier>(
            _ => new RecordingRolloutCertifier(RecordingRolloutCertifier.SessionMode));
        services.AddScoped<IWorkloadHealthContributor, FakeHostHealthContributor>();
        services.AddScoped<IWorkloadHealthContributor, ProviderReadyHealthContributor>();
        await using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });

        await provider.RunReeveWorkloadsAsync(runMigrations: false);

        await log.WaitForAsync("start:a").Within();
    }

    [Fact]
    public async Task A_host_stage_between_the_two_commits_starts_the_engine()
    {
        var log = new CallLog();
        await using var provider = Collection(
                log, hostStage: new DoubleStage("stage.host.plugin-roll", StagePlacement.After(WorkloadUpdateStages.CommitVersion)))
            .BuildServiceProvider();

        await provider.RunReeveWorkloadsAsync(runMigrations: false);

        await log.WaitForAsync("start:a").Within();
    }

    [Fact]
    public async Task It_migrates_before_the_engine_starts()
    {
        var log = new CallLog();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var services = Collection(log);
        services.AddSingleton<IWorkloadsSchemaMigrator>(new RecordingSchemaMigrator(log, release.Task));
        await using var provider = services.BuildServiceProvider();

        var run = provider.RunReeveWorkloadsAsync();
        await log.WaitForAsync("migrate").Within();

        log.Entries.Should().Equal("migrate");
        provider.GetRequiredService<FakeLeaderElection>().Attempts.Should().Be(0);

        release.SetResult();
        await run.Within();
        await log.WaitForAsync("start:a").Within();

        log.Before("resolve-runner").Should().Equal("migrate");
    }

    [Fact]
    public async Task It_resolves_the_migrator_inside_a_scope()
    {
        var log = new CallLog();
        var services = Collection(log);
        services.AddScoped<IWorkloadsSchemaMigrator>(_ => new RecordingSchemaMigrator(log));

        await using var provider = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateScopes = true,
        });

        await provider.RunReeveWorkloadsAsync();

        log.Entries.Should().Contain("migrate");
    }

    [Fact]
    public async Task It_never_asks_for_a_migrator_when_the_host_declined_migrations()
    {
        var log = new CallLog();
        var services = Collection(log);
        var resolutions = 0;
        services.AddSingleton<IWorkloadsSchemaMigrator>(_ =>
        {
            Interlocked.Increment(ref resolutions);
            return new RecordingSchemaMigrator(log);
        });

        await using var provider = services.BuildServiceProvider();

        await provider.RunReeveWorkloadsAsync(runMigrations: false);
        await log.WaitForAsync("start:a").Within();

        resolutions.Should().Be(0);
        log.Entries.Should().NotContain("migrate");
    }

    [Fact]
    public async Task A_failed_migration_leaves_the_next_call_free_to_start_the_engine()
    {
        var log = new CallLog();
        var services = Collection(log);
        services.AddSingleton<IWorkloadsSchemaMigrator>(new FailingOnceMigrator(log));
        var logs = new RecordingLoggerFactory();
        services.AddSingleton<ILoggerFactory>(logs);
        await using var provider = services.BuildServiceProvider();

        var first = () => provider.RunReeveWorkloadsAsync();
        await first.Should().ThrowAsync<InvalidOperationException>();

        await provider.RunReeveWorkloadsAsync();
        await log.WaitForAsync("start:a").Within();

        log.Entries.Should().ContainInOrder("migrate:1", "migrate:2", "resolve-runner");
        provider.GetRequiredService<FakeLeaderElection>().Attempts.Should().Be(1);

        logs.Entries.Should().NotContain(e => e.Level == LogLevel.Warning);
    }

    [Fact]
    public async Task A_failed_start_leaves_the_guard_armed()
    {
        var log = new CallLog();
        await using var provider = Collection(log, withRunner: false).BuildServiceProvider();

        var act = () => provider.RunReeveWorkloadsAsync(runMigrations: false);
        await act.Should().ThrowAsync<InvalidOperationException>();

        provider.GetRequiredService<WorkloadsEngineStartSignal>().WasStarted.Should().BeFalse();
    }

    [Fact]
    public async Task It_starts_one_engine_when_it_is_called_twice()
    {
        var log = new CallLog();
        var logs = new RecordingLoggerFactory();
        await using var provider = Compose(log, logs);

        await provider.RunReeveWorkloadsAsync(runMigrations: false);
        await provider.RunReeveWorkloadsAsync(runMigrations: false);
        await log.WaitForAsync("start:a").Within();

        provider.GetRequiredService<FakeLeaderElection>().Attempts.Should().Be(1);
        log.Entries.Count(e => e == "start:a").Should().Be(1);

        var warning = logs.Entries.Should().ContainSingle(e => e.Level == LogLevel.Warning).Subject;
        warning.Text.Should().Contain("RunReeveWorkloadsAsync");
    }

    [Fact]
    public async Task It_returns_before_the_engine_has_won_the_leadership()
    {
        var log = new CallLog();
        var services = Collection(log, election: new BlockingLeaderElection());
        await using var provider = services.BuildServiceProvider();

        await provider.RunReeveWorkloadsAsync(runMigrations: false).Within();

        await provider.GetRequiredService<WorkloadsEngineRunner>().StopAsync(CancellationToken.None).Within();
    }

    [Fact]
    public async Task It_gives_the_engine_the_callers_token_to_run_until()
    {
        var log = new CallLog();
        await using var provider = Compose(log);
        using var shutdown = new CancellationTokenSource();

        await provider.RunReeveWorkloadsAsync(runMigrations: false, shutdown.Token);
        await log.WaitForAsync("start:a").Within();

        await shutdown.CancelAsync();

        await log.WaitForAsync("resign").Within();
        log.Before("resign").Should().ContainInOrder("start:a", "stop:a");
    }

    [Fact]
    public async Task It_marks_the_start_signal_so_the_guard_leaves_the_application_alone()
    {
        var log = new CallLog();
        await using var provider = Compose(log);

        await provider.RunReeveWorkloadsAsync(runMigrations: false);

        using var lifetime = new FakeHostApplicationLifetime();
        var lifecycle = new WorkloadsEngineLifecycle(
            provider.GetRequiredService<WorkloadsEngineStartSignal>(),
            provider.GetRequiredService<WorkloadsEngineRunner>(),
            lifetime,
            NullLogger<WorkloadsEngineLifecycle>.Instance);

        await lifecycle.StartAsync(CancellationToken.None);
        lifetime.FireStarted();

        lifetime.StopRequests.Should().Be(0);

        await lifecycle.StopAsync(CancellationToken.None);
    }

    private static ServiceProvider Compose(CallLog log, ILoggerFactory? loggerFactory = null)
        => Collection(log, loggerFactory: loggerFactory).BuildServiceProvider();

    private static ServiceCollection Collection(
        CallLog log,
        IWorkloadLeaderElection? election = null,
        ILoggerFactory? loggerFactory = null,
        bool withRunner = true,
        IStage<WorkloadUpdateStageContext>? hostStage = null)
    {
        var services = new ServiceCollection();

        foreach (var id in WorkloadUpdateStages.Core)
        {
            services.AddScoped<IStage<WorkloadUpdateStageContext>>(_ => new DoubleStage(id, CorePlacement(id)));
        }

        if (hostStage is not null)
        {
            services.AddScoped(_ => hostStage);
        }

        services.AddScoped(sp => WorkloadUpdatePipeline.Resolve(sp.GetServices<IStage<WorkloadUpdateStageContext>>()));

        if (loggerFactory is not null)
        {
            services.AddSingleton(loggerFactory);
        }

        var fake = new FakeLeaderElection(log, new FakeLeadership("first", log));
        services.AddSingleton(fake);
        services.AddSingleton(election ?? fake);
        services.AddSingleton<IWorkloadEngineUnit>(new FakeEngineUnit("a", log));
        services.AddSingleton(Options.Create(new WorkloadsEngineHostingOptions()));
        services.AddSingleton<ILogger<WorkloadsEngineRunner>>(NullLogger<WorkloadsEngineRunner>.Instance);
        services.AddSingleton(new WorkloadsEngineStartSignal());

        if (withRunner)
        {
            services.AddSingleton(sp =>
            {
                log.Record("resolve-runner");
                return new WorkloadsEngineRunner(
                    sp.GetRequiredService<IWorkloadLeaderElection>(),
                    sp.GetServices<IWorkloadEngineUnit>(),
                    sp.GetRequiredService<IOptions<WorkloadsEngineHostingOptions>>(),
                    sp.GetRequiredService<ILogger<WorkloadsEngineRunner>>(),
                    new ImmediateTimeProvider());
            });
        }

        return services;
    }

    private static StagePlacement CorePlacement(string id)
    {
        var index = WorkloadUpdateStages.Core.ToList().IndexOf(id);
        return index switch
        {
            0 => StagePlacement.First,
            _ when id == WorkloadUpdateStages.CommitParams => StagePlacement.After(
                WorkloadUpdateStages.Core[index - 1], WorkloadUpdateStages.CommitParamsOrdinal),
            _ => StagePlacement.After(WorkloadUpdateStages.Core[index - 1]),
        };
    }

    private sealed record DoubleStage(string Id, StagePlacement Placement) : IStage<WorkloadUpdateStageContext>
    {
        public StageReversibility Reversibility => StageReversibility.Reversible;

        public StageBudget Budget => StageBudget.None;

        public ValueTask<StageOutcome> UpAsync(WorkloadUpdateStageContext context, CancellationToken cancellationToken)
            => throw new InvalidOperationException("Starting the engine must not run a stage.");

        public ValueTask<StageOutcome> DownAsync(WorkloadUpdateStageContext context, CancellationToken cancellationToken)
            => throw new InvalidOperationException("Starting the engine must not run a stage.");
    }

    private sealed class FailingOnceMigrator : IWorkloadsSchemaMigrator
    {
        private readonly CallLog log;
        private int calls;

        public FailingOnceMigrator(CallLog log) => this.log = log;

        public Task MigrateAsync(CancellationToken cancellationToken = default)
        {
            var call = Interlocked.Increment(ref calls);
            log.Record($"migrate:{call}");

            return call == 1
                ? Task.FromException(new InvalidOperationException("the database is not accepting connections yet"))
                : Task.CompletedTask;
        }
    }
}

internal sealed class BlockingLeaderElection : IWorkloadLeaderElection
{
    public Task<IWorkloadLeadership?> TryAcquireAsync(CancellationToken cancellationToken)
        => new TaskCompletionSource<IWorkloadLeadership?>().Task.WaitAsync(cancellationToken);
}
