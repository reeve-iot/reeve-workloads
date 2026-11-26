using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Reeve.Workloads.Abstractions;
using Reeve.Workloads.Abstractions.Contracts;
using Reeve.Workloads.Abstractions.Providers;
using Reeve.Workloads.Core.Watch;
using Reeve.Workloads.Providers.Docker;
using Reeve.Workloads.Providers.Kubernetes;
using Xunit;

namespace Reeve.Workloads.Core.Tests;

public sealed class WorkloadOrchestratorSettingsChangesTests
{
    private readonly SwitchableSettingsSource source = new() { Document = new WorkloadOrchestratorSettings { Provider = KubernetesProvider.Id } };
    private readonly IWorkloadReconcileNudge nudge = Substitute.For<IWorkloadReconcileNudge>();
    private readonly WorkloadOrchestratorSettingsCache cache = new(Substitute.For<IWorkloadClock>());
    private readonly RecordingSource kubernetes = new(KubernetesProvider.Id);
    private readonly RecordingSource docker = new(DockerProvider.Id);
    private readonly IWorkloadReconcileTrigger trigger = Substitute.For<IWorkloadReconcileTrigger>();
    private readonly WorkloadWatchUnit unit;
    private readonly WorkloadOrchestratorSettingsChanges changes;

    public WorkloadOrchestratorSettingsChangesTests()
    {
        var services = new ServiceCollection();
        services.AddScoped<IWorkloadOrchestratorSettingsService>(_ => new EffectiveFromSource(source));
        services.AddScoped(_ => WatchTestInstances.BoundTo([]));
        unit = new WorkloadWatchUnit(
            trigger,
            [kubernetes, docker],
            services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(),
            Options.Create(new WorkloadReconcilerOptions { WatchEnabled = true }),
            Options.Create(new WorkloadWatchOptions { DebounceMilliseconds = 0 }),
            NullLogger<WorkloadWatchUnit>.Instance,
            NullLogger<WorkloadWatchCoalescer>.Instance);
        changes = new WorkloadOrchestratorSettingsChanges(cache, nudge, unit);
    }

    [Fact]
    public async Task A_moved_provider_restarts_the_running_watch_on_the_new_provider_without_a_leader_change()
    {
        await unit.StartAsync(CancellationToken.None);
        kubernetes.StartCount.Should().Be(1);

        source.Document = new WorkloadOrchestratorSettings { Provider = DockerProvider.Id };
        await changes.ChangedAsync();

        kubernetes.StopCount.Should().Be(1);
        docker.StartCount.Should().Be(1);
        await unit.StopAsync(CancellationToken.None);
        docker.StopCount.Should().Be(1);
    }

    [Fact]
    public async Task An_unmoved_provider_leaves_the_watch_alone()
    {
        await unit.StartAsync(CancellationToken.None);

        await changes.ChangedAsync();

        kubernetes.StartCount.Should().Be(1);
        kubernetes.StopCount.Should().Be(0);
        docker.StartCount.Should().Be(0);
        await unit.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task A_replica_that_does_not_run_the_watch_starts_nothing()
    {
        source.Document = new WorkloadOrchestratorSettings { Provider = DockerProvider.Id };

        await changes.ChangedAsync();

        docker.StartCount.Should().Be(0);
        kubernetes.StartCount.Should().Be(0);
    }

    [Fact]
    public async Task A_change_asks_for_a_reconcile_of_every_instance_whether_or_not_the_provider_moved()
    {
        await unit.StartAsync(CancellationToken.None);

        await changes.ChangedAsync();

        await nudge.Received(1).NudgeAllAsync(WorkloadReconcileReasons.SettingsChanged, Arg.Any<CancellationToken>());
        await unit.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task A_change_drops_the_auto_detect_answers_so_the_next_read_probes_again()
    {
        var clock = Substitute.For<IWorkloadClock>();
        clock.UtcNow.Returns(DateTimeOffset.UnixEpoch);
        var ownCache = new WorkloadOrchestratorSettingsCache(clock);
        var probes = 0;
        Task<bool> Probe(CancellationToken _)
        {
            probes++;
            return Task.FromResult(true);
        }

        await ownCache.IsProviderAvailableAsync(DockerProvider.Id, default, Probe, CancellationToken.None);
        await ownCache.IsProviderAvailableAsync(DockerProvider.Id, default, Probe, CancellationToken.None);
        probes.Should().Be(1);

        await new WorkloadOrchestratorSettingsChanges(ownCache, nudge, unit).ChangedAsync();
        await ownCache.IsProviderAvailableAsync(DockerProvider.Id, default, Probe, CancellationToken.None);

        probes.Should().Be(2);
    }

    [Fact]
    public async Task After_a_move_the_new_source_feeds_reconciles()
    {
        await unit.StartAsync(CancellationToken.None);

        source.Document = new WorkloadOrchestratorSettings { Provider = DockerProvider.Id };
        await changes.ChangedAsync();
        var drained = unit.WaitForNextDrainAsync(TimeSpan.FromSeconds(5));
        docker.Sink!.InstanceDirty(Guid.NewGuid());
        await drained;

        await trigger.Received(1).RequestInstanceAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        await unit.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task A_failing_nudge_does_not_stop_the_watch_from_moving()
    {
        await unit.StartAsync(CancellationToken.None);
        source.Document = new WorkloadOrchestratorSettings { Provider = DockerProvider.Id };
        nudge.NudgeAllAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(Task.FromException(new InvalidOperationException("bus down")));

        var act = () => changes.ChangedAsync();

        await act.Should().ThrowAsync<InvalidOperationException>();
        docker.StartCount.Should().Be(1);
        await unit.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task A_move_to_a_provider_with_no_watch_source_stops_the_old_one_and_starts_nothing()
    {
        await unit.StartAsync(CancellationToken.None);
        source.Document = new WorkloadOrchestratorSettings { Provider = WorkloadProviderId.Parse("manual") };

        await changes.ChangedAsync();

        kubernetes.StopCount.Should().Be(1);
        docker.StartCount.Should().Be(0);
        await unit.StopAsync(CancellationToken.None);
        kubernetes.StopCount.Should().Be(1);
    }

    [Fact]
    public async Task A_source_that_fails_to_start_after_a_move_leaves_the_unit_running()
    {
        await unit.StartAsync(CancellationToken.None);
        docker.ThrowOnStart = true;
        source.Document = new WorkloadOrchestratorSettings { Provider = DockerProvider.Id };

        await changes.ChangedAsync();

        kubernetes.StopCount.Should().Be(1);
        await unit.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task A_provider_that_cannot_be_resolved_leaves_the_watch_where_it_is()
    {
        await unit.StartAsync(CancellationToken.None);
        source.Document = new WorkloadOrchestratorSettings();

        await changes.ChangedAsync();

        kubernetes.StopCount.Should().Be(0);
        docker.StartCount.Should().Be(0);
        await unit.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task A_watch_that_could_not_resolve_its_provider_at_start_starts_once_it_resolves()
    {
        source.Document = new WorkloadOrchestratorSettings();
        await unit.StartAsync(CancellationToken.None);
        kubernetes.StartCount.Should().Be(0);

        source.Document = new WorkloadOrchestratorSettings { Provider = KubernetesProvider.Id };
        await changes.ChangedAsync();

        kubernetes.StartCount.Should().Be(1);
        await unit.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task A_stopped_watch_is_not_restarted_by_a_change()
    {
        await unit.StartAsync(CancellationToken.None);
        await unit.StopAsync(CancellationToken.None);
        source.Document = new WorkloadOrchestratorSettings { Provider = DockerProvider.Id };

        await changes.ChangedAsync();

        docker.StartCount.Should().Be(0);
    }

    [Fact]
    public async Task A_watch_that_is_switched_off_stays_inert()
    {
        var inert = new WorkloadWatchUnit(
            trigger,
            [docker],
            new ServiceCollection().AddScoped<IWorkloadOrchestratorSettingsService>(_ => new EffectiveFromSource(source))
                .AddScoped(_ => WatchTestInstances.BoundTo([]))
                .BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(),
            Options.Create(new WorkloadReconcilerOptions { WatchEnabled = false }),
            Options.Create(new WorkloadWatchOptions { DebounceMilliseconds = 0 }),
            NullLogger<WorkloadWatchUnit>.Instance,
            NullLogger<WorkloadWatchCoalescer>.Instance);
        await inert.StartAsync(CancellationToken.None);
        source.Document = new WorkloadOrchestratorSettings { Provider = DockerProvider.Id };

        await new WorkloadOrchestratorSettingsChanges(cache, nudge, inert).ChangedAsync();

        docker.StartCount.Should().Be(0);
    }

    private WorkloadWatchUnit UnitOn(
        IWorkloadOrchestratorSettingsService settings,
        bool watchEnabled = true,
        IWorkloadWatchSource[]? sources = null,
        ILogger<WorkloadWatchUnit>? logger = null)
        => new(
            trigger,
            sources ?? [],
            new ServiceCollection().AddScoped(_ => settings).AddScoped(_ => WatchTestInstances.BoundTo([]))
                .BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(),
            Options.Create(new WorkloadReconcilerOptions { WatchEnabled = watchEnabled }),
            Options.Create(new WorkloadWatchOptions { DebounceMilliseconds = 0 }),
            logger ?? NullLogger<WorkloadWatchUnit>.Instance,
            NullLogger<WorkloadWatchCoalescer>.Instance);

    private sealed class CapturingLogger : ILogger<WorkloadWatchUnit>
    {
        public List<LogLevel> Levels { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull
            => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => Levels.Add(logLevel);
    }

    [Fact]
    public async Task A_source_that_fails_to_start_does_not_stop_the_others()
    {
        var broken = new RecordingSource(KubernetesProvider.Id) { ThrowOnStart = true };
        var second = new RecordingSource(KubernetesProvider.Id);
        var own = UnitOn(new EffectiveFromSource(source), true, [broken, second]);

        await own.StartAsync(CancellationToken.None);

        second.StartCount.Should().Be(1);
        await own.StopAsync(CancellationToken.None);
        broken.StopCount.Should().Be(0);
        second.StopCount.Should().Be(1);
    }

    [Fact]
    public async Task A_start_cancelled_while_a_source_starts_leaves_the_remaining_sources_unstarted()
    {
        using var caller = new CancellationTokenSource();
        var first = new RecordingSource(KubernetesProvider.Id)
        {
            OnStart = () =>
            {
                caller.Cancel();
                throw new OperationCanceledException(caller.Token);
            },
        };
        var second = new RecordingSource(KubernetesProvider.Id);
        var own = UnitOn(new EffectiveFromSource(source), true, [first, second]);

        await own.StartAsync(caller.Token);

        first.StartCount.Should().Be(1);
        second.StartCount.Should().Be(0);
        await own.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task A_source_that_throws_on_stop_does_not_keep_the_others_running_or_the_unit_from_starting_again()
    {
        var first = new RecordingSource(KubernetesProvider.Id) { ThrowOnStop = true };
        var second = new RecordingSource(KubernetesProvider.Id);
        var own = UnitOn(new EffectiveFromSource(source), true, [first, second]);
        await own.StartAsync(CancellationToken.None);

        await own.StopAsync(CancellationToken.None);
        await own.StartAsync(CancellationToken.None);

        first.StopCount.Should().Be(1);
        second.StopCount.Should().Be(1);
        first.StartCount.Should().Be(2);
        second.StartCount.Should().Be(2);
        await own.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Stopping_cancels_the_token_the_sources_were_started_with()
    {
        var running = new RecordingSource(KubernetesProvider.Id);
        var own = UnitOn(new EffectiveFromSource(source), true, [running]);
        await own.StartAsync(CancellationToken.None);
        running.LastToken.IsCancellationRequested.Should().BeFalse();

        await own.StopAsync(CancellationToken.None);

        running.LastToken.IsCancellationRequested.Should().BeTrue();
    }

    [Fact]
    public async Task A_resolution_cancelled_with_the_unit_leaves_the_running_sources_in_place()
    {
        using var caller = new CancellationTokenSource();
        var running = new RecordingSource(KubernetesProvider.Id);
        var other = new RecordingSource(DockerProvider.Id);
        var logger = new CapturingLogger();
        var settings = Substitute.For<IWorkloadOrchestratorSettingsService>();
        var calls = 0;
        settings.GetAsync(Arg.Any<CancellationToken>()).Returns(_ =>
        {
            if (++calls == 1)
            {
                return Task.FromResult(new WorkloadOrchestratorSettingsResponse
                {
                    Settings = new WorkloadOrchestratorSettings(),
                    EffectiveProvider = KubernetesProvider.Id,
                    AutoDetected = false,
                    Providers = [],
                });
            }

            caller.Cancel();
            return Task.FromCanceled<WorkloadOrchestratorSettingsResponse>(caller.Token);
        });
        var own = UnitOn(settings, true, [running, other], logger);
        await own.StartAsync(caller.Token);

        await own.RestartIfProviderMovedAsync(CancellationToken.None);

        running.StopCount.Should().Be(0);
        other.StartCount.Should().Be(0);
        logger.Levels.Should().NotContain(LogLevel.Error);
    }

    [Fact]
    public async Task A_resolution_that_fails_logs_an_error_and_starts_no_source()
    {
        var logger = new CapturingLogger();
        var settings = Substitute.For<IWorkloadOrchestratorSettingsService>();
        settings.GetAsync(Arg.Any<CancellationToken>()).Returns<Task<WorkloadOrchestratorSettingsResponse>>(_ => throw new InvalidOperationException("db down"));
        var running = new RecordingSource(KubernetesProvider.Id);
        var own = UnitOn(settings, true, [running], logger);

        await own.StartAsync(CancellationToken.None);

        running.StartCount.Should().Be(0);
        logger.Levels.Should().Contain(LogLevel.Error);
        await own.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task A_start_cancelled_while_the_provider_resolves_starts_no_source()
    {
        using var caller = new CancellationTokenSource();
        var running = new RecordingSource(KubernetesProvider.Id);
        var settings = Substitute.For<IWorkloadOrchestratorSettingsService>();
        settings.GetAsync(Arg.Any<CancellationToken>()).Returns(_ =>
        {
            caller.Cancel();
            return Task.FromResult(new WorkloadOrchestratorSettingsResponse
            {
                Settings = new WorkloadOrchestratorSettings(),
                EffectiveProvider = KubernetesProvider.Id,
                AutoDetected = false,
                Providers = [],
            });
        });
        var own = UnitOn(settings, true, [running]);

        await own.StartAsync(caller.Token);

        running.StartCount.Should().Be(0);
        await own.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Waiting_for_a_drain_on_a_unit_that_never_started_completes_at_once()
    {
        var own = UnitOn(new EffectiveFromSource(source));

        await own.WaitForNextDrainAsync(TimeSpan.FromMilliseconds(1));
    }

    [Fact]
    public async Task Start_a_move_and_stop_log_at_information_and_a_failed_source_logs_an_error()
    {
        var logger = new CapturingLogger();
        var k8s = new RecordingSource(KubernetesProvider.Id);
        var dockerSource = new RecordingSource(DockerProvider.Id) { ThrowOnStop = true };
        var own = UnitOn(new EffectiveFromSource(source), true, [k8s, dockerSource], logger);
        await own.StartAsync(CancellationToken.None);
        logger.Levels.Should().Contain(LogLevel.Information).And.Contain(LogLevel.Debug);
        logger.Levels.Clear();

        source.Document = new WorkloadOrchestratorSettings { Provider = DockerProvider.Id };
        await own.RestartIfProviderMovedAsync(CancellationToken.None);
        logger.Levels.Should().Contain(LogLevel.Information);
        logger.Levels.Clear();
        source.Document = new WorkloadOrchestratorSettings { Provider = KubernetesProvider.Id };
        await own.RestartIfProviderMovedAsync(CancellationToken.None);
        logger.Levels.Should().Contain(LogLevel.Error);
        await own.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task An_inert_unit_logs_that_it_started_inert()
    {
        var logger = new CapturingLogger();
        var own = UnitOn(new EffectiveFromSource(source), watchEnabled: false, logger: logger);

        await own.StartAsync(CancellationToken.None);

        logger.Levels.Should().Contain(LogLevel.Information);
    }

    [Fact]
    public async Task A_source_that_fails_to_start_logs_an_error()
    {
        var logger = new CapturingLogger();
        var broken = new RecordingSource(KubernetesProvider.Id) { ThrowOnStart = true };
        var own = UnitOn(new EffectiveFromSource(source), true, [broken], logger);

        await own.StartAsync(CancellationToken.None);

        logger.Levels.Should().Contain(LogLevel.Error);
        await own.StopAsync(CancellationToken.None);
    }

    private sealed class EffectiveFromSource(SwitchableSettingsSource source) : IWorkloadOrchestratorSettingsService
    {
        public async Task<WorkloadOrchestratorSettingsResponse> GetAsync(CancellationToken cancellationToken = default)
        {
            var document = await source.GetAsync(cancellationToken);
            return new WorkloadOrchestratorSettingsResponse
            {
                Settings = document,
                EffectiveProvider = document.Provider!.Value,
                AutoDetected = false,
                Providers = [],
            };
        }

        public Task<WorkloadProviderProbeResult> TestAsync(WorkloadProviderId? requestedProvider, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }

    private sealed class RecordingSource(WorkloadProviderId provider) : IWorkloadWatchSource
    {
        public WorkloadProviderId Provider { get; } = provider;

        public int StartCount { get; private set; }

        public int StopCount { get; private set; }

        public IWorkloadWatchSink? Sink { get; private set; }

        public bool ThrowOnStart { get; set; }

        public bool ThrowOnStop { get; set; }

        public Action? OnStart { get; set; }

        public CancellationToken LastToken { get; private set; }

        public Task StartAsync(IWorkloadWatchSink sink, CancellationToken cancellationToken)
        {
            StartCount++;
            LastToken = cancellationToken;
            OnStart?.Invoke();
            if (ThrowOnStart)
            {
                throw new InvalidOperationException("cannot connect");
            }

            Sink = sink;
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken)
        {
            StopCount++;
            return ThrowOnStop ? throw new InvalidOperationException("cannot stop") : Task.CompletedTask;
        }
    }
}
