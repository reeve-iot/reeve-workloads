using Reeve.Pipeline;
using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Reeve.Workloads.Abstractions;
using Reeve.Workloads.Abstractions.Contracts;
using Reeve.Workloads.Abstractions.Contracts.Deployment;
using Reeve.Workloads.Abstractions.Domain.Entities;
using Reeve.Workloads.Abstractions.Domain.Network;
using Reeve.Workloads.Abstractions.Providers;
using Reeve.Workloads.Abstractions.Update;
using Reeve.Workloads.Core;
using Reeve.Workloads.Core.Update;
using Reeve.Workloads.DependencyInjection;
using Reeve.Workloads.EntityFrameworkCore.Persistence;
using Reeve.Workloads.Testing;

namespace Reeve.Workloads.OffMarketplace.Tests;

internal sealed class OffMarketplaceHost : IAsyncDisposable
{
    private static readonly IConfiguration Configuration = new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Workloads:Reconciler:HealthBarrierTimeoutSeconds"] = "1",
            ["Workloads:Reconciler:HealthBarrierDriveSeconds"] = "1",
            ["Workloads:Reconciler:HealthBarrierPollMilliseconds"] = "50",
        })
        .Build();

    public static readonly TimeSpan EngineWaitBudget = TimeSpan.FromSeconds(5);

    private static readonly TimeSpan TeardownBudget = TimeSpan.FromSeconds(30);

    private readonly IHost host;

    public OffMarketplaceHost(IWorkloadLeaderElection? election = null)
    {
        var builder = Host.CreateEmptyApplicationBuilder(new HostApplicationBuilderSettings());
        builder.Configuration.AddConfiguration(Configuration);
        Composition = Compose(builder.Services, Manifests, Provider, Swaps, election, EngineUnit, Logs);
        host = builder.Build();
        Swaps.ReadInstalledVersion = InstalledVersionOfAsync;
    }

    public EngineRunMarkerUnit EngineUnit { get; } = new();

    public RecordingLogs Logs { get; } = new();

    public static WorkloadProviderDescriptor Descriptor { get; } =
        new() { Id = WorkloadProviderId.Parse("in-memory"), Materializes = true };

    public ReeveWorkloadsBuilder Composition { get; }

    public const string SourceKey = "local";

    public InMemoryGroupManifests Manifests { get; } = new();

    public InMemoryWorkloadProvider Provider { get; } = new(Descriptor);

    public RecordingReloadStage Swaps { get; } = new();

    public AsyncServiceScope CreateScope() => host.Services.CreateAsyncScope();

    public Task StartApplicationAsync() => host.StartAsync();

    public async Task StartEngineAndApplicationAsync()
    {
        EnsureTheMarkerRunsLast();
        await RunEngineAsync();
        await StartApplicationAsync();
        await EngineUnit.AwaitStartedAsync(EngineWaitBudget);
    }

    public Task RunEngineAsync() => host.Services.RunReeveWorkloadsAsync(runMigrations: false);

    public Task NudgeEngineAsync()
        => host.Services.GetRequiredService<IWorkloadReconcileTrigger>()
            .RequestAllAsync(WorkloadReconcileReasons.Presence, CancellationToken.None);

    public bool ApplicationWasAskedToStop
        => host.Services.GetRequiredService<IHostApplicationLifetime>().ApplicationStopping.IsCancellationRequested;

    public async Task<int> WaitForApplyAsync(Guid workloadInstanceId)
    {
        var elapsed = System.Diagnostics.Stopwatch.StartNew();
        while (true)
        {
            var applies = Provider.ApplyCountFor(workloadInstanceId);
            if (applies > 0 || elapsed.Elapsed >= EngineWaitBudget)
            {
                return applies;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(20));
        }
    }

    public async Task<WorkloadMaterializeResult> InstallAsync(
        Guid groupInstanceId,
        string groupId,
        string version,
        IReadOnlyDictionary<string, WorkloadAttachmentPin>? pins = null,
        bool suppressReconcileNudge = false)
    {
        await using var scope = CreateScope();
        var read = await scope.ServiceProvider.GetRequiredService<IWorkloadRequirementsService>()
            .ForInstallAsync(new WorkloadInstallRequirementsQuery { GroupId = groupId, SourceKey = SourceKey, Version = version });
        return await scope.ServiceProvider.GetRequiredService<IWorkloadLifecycleService>().MaterializeAsync(new WorkloadGroupInstall
        {
            GroupInstanceId = groupInstanceId,
            GroupId = groupId,
            SourceKey = SourceKey,
            Version = version,
            AttachmentPins = pins ?? new Dictionary<string, WorkloadAttachmentPin>(StringComparer.Ordinal),
            RequirementsFingerprint = read.Read?.Fingerprint
                ?? throw new InvalidOperationException($"No requirements to judge: {read.Status} {read.Message}"),
            SuppressReconcileNudge = suppressReconcileNudge,
        });
    }

    public async Task<WorkloadWritePrecondition> JudgeUpdateAsync(Guid groupInstanceId, string version)
    {
        await using var scope = CreateScope();
        var read = await scope.ServiceProvider.GetRequiredService<IWorkloadRequirementsService>()
            .ForUpdateAsync(groupInstanceId, version);
        return new WorkloadWritePrecondition
        {
            RequirementsFingerprint = read.Read?.Fingerprint
                ?? throw new InvalidOperationException($"No requirements to judge: {read.Status} {read.Message}"),
        };
    }

    public async Task<WorkloadUpdateInitiationResult> UpdateAsync(
        Guid groupInstanceId, string version, WorkloadWritePrecondition? judged = null)
    {
        var precondition = judged ?? await JudgeUpdateAsync(groupInstanceId, version);
        await using var scope = CreateScope();
        return await scope.ServiceProvider.GetRequiredService<IWorkloadUpdateInitiator>()
            .InitiateAsync(new WorkloadUpdateInitiationRequest
            {
                PluginInstanceId = groupInstanceId,
                CandidateVersion = version,
                Precondition = precondition,
            });
    }

    public async Task<WorkloadUpdateCohortInitiationResult> UpdateCohortAsync(
        string version, IReadOnlyList<Guid> groupInstanceIds)
    {
        var members = new List<WorkloadUpdateCohortMember>();
        foreach (var id in groupInstanceIds)
        {
            members.Add(new WorkloadUpdateCohortMember
            {
                GroupInstanceId = id,
                Precondition = await JudgeUpdateAsync(id, version),
            });
        }

        await using var scope = CreateScope();
        return await scope.ServiceProvider.GetRequiredService<IWorkloadUpdateCohortInitiator>()
            .InitiateCohortAsync(new WorkloadUpdateCohortInitiationRequest
            {
                CandidateVersion = version,
                Members = members,
            });
    }

    public async Task<IReadOnlyList<WorkloadUpdateOutcome?>> DriveCohortAsync(
        IReadOnlyList<Guid> groupInstanceIds, int maxPasses, Action<Guid>? runtime = null)
    {
        for (var pass = 0; pass < maxPasses; pass++)
        {
            await RunLeaderPassAsync();
            foreach (var id in groupInstanceIds)
            {
                foreach (var instance in await InstancesOfAsync(id))
                {
                    if (Provider.IsTracked(instance.Id))
                    {
                        if (runtime is null) Provider.SimulateReady(instance.Id);
                        else runtime(instance.Id);
                    }
                }
            }

            await using var scope = CreateScope();
            var repository = scope.ServiceProvider.GetRequiredService<IWorkloadUpdateIntentRepository>();
            var outcomes = new List<WorkloadUpdateOutcome?>();
            foreach (var id in groupInstanceIds)
                outcomes.Add((await repository.GetLatestForInstanceAsync(id, CancellationToken.None))?.Outcome);
            if (outcomes.All(outcome => outcome is not (null or WorkloadUpdateOutcome.InProgress))) return outcomes;
        }

        await using var finalScope = CreateScope();
        var finalRepository = finalScope.ServiceProvider.GetRequiredService<IWorkloadUpdateIntentRepository>();
        var finalOutcomes = new List<WorkloadUpdateOutcome?>();
        foreach (var id in groupInstanceIds)
            finalOutcomes.Add((await finalRepository.GetLatestForInstanceAsync(id, CancellationToken.None))?.Outcome);
        return finalOutcomes;
    }

    public async Task RunLeaderPassAsync()
    {
        await host.Services.GetRequiredService<WorkloadUpdateWalkUnit>().TickAsync(CancellationToken.None);
        await host.Services.GetRequiredService<WorkloadReconcilerUnit>()
            .ReconcileAllAsync(WorkloadReconcileTrigger.Timer, CancellationToken.None);
    }

    public async Task<IReadOnlyList<Reeve.Workloads.Abstractions.Domain.Entities.WorkloadInstance>> InstancesOfAsync(
        Guid groupInstanceId)
    {
        await using var scope = CreateScope();
        return await scope.ServiceProvider.GetRequiredService<IWorkloadInstanceRepository>()
            .ListByPluginInstanceAsync(groupInstanceId, CancellationToken.None);
    }

    public async Task<WorkloadUpdateOutcome?> DriveUpdateAsync(
        Guid groupInstanceId, int maxPasses, Action<Guid>? runtime = null)
    {
        runtime ??= Provider.SimulateReady;
        WorkloadUpdateOutcome? outcome = null;
        for (var pass = 0; pass < maxPasses; pass++)
        {
            await RunLeaderPassAsync();
            foreach (var instance in await InstancesOfAsync(groupInstanceId))
            {
                if (Provider.IsTracked(instance.Id))
                {
                    runtime(instance.Id);
                }
            }

            await using var scope = CreateScope();
            outcome = (await scope.ServiceProvider.GetRequiredService<IWorkloadUpdateIntentRepository>()
                .GetLatestForInstanceAsync(groupInstanceId, CancellationToken.None))?.Outcome;
            if (outcome is not (null or WorkloadUpdateOutcome.InProgress))
            {
                break;
            }
        }

        return outcome;
    }

    public async Task<WorkloadReconcileOutcome> ReconcileAsync(Guid workloadInstanceId)
    {
        await using var scope = CreateScope();
        var instance = await scope.ServiceProvider.GetRequiredService<IWorkloadInstanceRepository>()
            .GetByIdAsync(workloadInstanceId, CancellationToken.None);
        return await scope.ServiceProvider.GetRequiredService<IWorkloadReconcileEngine>()
            .ReconcileInstanceAsync(instance!, CancellationToken.None);
    }

    public async Task StoreBelowTheEntityAsync(
        Guid workloadInstanceId,
        WorkloadInstanceState state,
        WorkloadStateReason? reason = null)
    {
        await using var scope = CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<IWorkloadInstanceRepository>();
        var instance = await repository.GetByIdAsync(workloadInstanceId, CancellationToken.None);
        var entity = typeof(Reeve.Workloads.Abstractions.Domain.Entities.WorkloadInstance);
        entity.GetProperty(nameof(Reeve.Workloads.Abstractions.Domain.Entities.WorkloadInstance.State))!
            .SetValue(instance, state);
        if (reason is { } stored)
        {
            entity.GetProperty(nameof(Reeve.Workloads.Abstractions.Domain.Entities.WorkloadInstance.StateReason))!
                .SetValue(instance, stored);
        }

        await repository.UpdateAsync(instance!, CancellationToken.None);
    }

    public async Task<string?> InstalledVersionOfAsync(string groupId)
    {
        await using var scope = CreateScope();
        return (await scope.ServiceProvider.GetRequiredService<IWorkloadGroupRepository>()
            .GetAsync(groupId, CancellationToken.None))?.InstalledVersion;
    }

    public async ValueTask DisposeAsync()
    {
        var drained = true;
        var stop = host.StopAsync();

        try
        {
            await stop.WaitAsync(TeardownBudget);
        }
        catch (TimeoutException) when (!stop.IsCompleted)
        {
            drained = false;
        }
        finally
        {
            await ((IAsyncDisposable)host).DisposeAsync();
        }

        if (!drained)
        {
            throw new TimeoutException(
                $"The workloads engine did not drain within {TeardownBudget} of the host being stopped. A unit's "
                + "StopAsync never returned, or the runner never reached one. Nothing after this point in the "
                + "fact ran, and any failure the fact had already recorded has been replaced by this one.");
        }
    }

    private void EnsureTheMarkerRunsLast()
    {
        var units = host.Services.GetServices<IWorkloadEngineUnit>().ToList();
        if (units.Count == 0 || !ReferenceEquals(units[^1], EngineUnit))
        {
            throw new InvalidOperationException(
                $"The marker unit is no longer the last of the {units.Count} engine units this host registers, so "
                + "waiting for it no longer means the engine has started all of them. Something now registers an "
                + "IWorkloadEngineUnit after AddEngine() - most likely a package extension called from Compose. "
                + "Move the marker's registration after it.");
        }
    }

    private static ReeveWorkloadsBuilder Compose(
        IServiceCollection services,
        InMemoryGroupManifests manifests,
        InMemoryWorkloadProvider provider,
        RecordingReloadStage swaps,
        IWorkloadLeaderElection? election,
        EngineRunMarkerUnit engineUnit,
        RecordingLogs logs)
    {
        var database = Guid.NewGuid().ToString("n");
        services.AddLogging(logging => logging.AddProvider(logs));
        services.AddDataProtection().UseEphemeralDataProtectionProvider();
        services.AddSingleton<IWorkloadProvider>(provider);
        services.AddSingleton<IStage<WorkloadUpdateStageContext>>(swaps);

        var composed = services
            .AddReeveWorkloads(Configuration, ports =>
            {
                ports
                    .Use<IWorkloadGroupManifestSource>(_ => manifests, ServiceLifetime.Singleton)
                    .Use<IWorkloadOrchestratorSettingsSource>(_ => new InMemoryWorkloadOrchestratorSettingsSource(), ServiceLifetime.Singleton);

                if (election is not null)
                {
                    ports.Use<IWorkloadLeaderElection>(_ => election, ServiceLifetime.Singleton);
                }
            })
            .AddEngine()
            .AddEntityFrameworkStores(options => options.UseInMemoryDatabase(database));

        composed.Services.AddSingleton<IWorkloadEngineUnit>(engineUnit);
        return composed;
    }
}

internal sealed record LogEntry(LogLevel Level, string Text);

internal sealed class RecordingLogs : ILoggerProvider
{
    private readonly ConcurrentQueue<LogEntry> entries = new();

    public IReadOnlyList<LogEntry> Entries => entries.ToList();

    public ILogger CreateLogger(string categoryName) => new Sink(entries);

    public void Dispose()
    {
    }

    private sealed class Sink(ConcurrentQueue<LogEntry> entries) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull
            => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Warning;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (IsEnabled(logLevel))
            {
                entries.Enqueue(new LogEntry(logLevel, formatter(state, exception)));
            }
        }
    }
}

internal sealed class EngineRunMarkerUnit : IWorkloadEngineUnit
{
    private readonly TaskCompletionSource started = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private int starts;

    public string Key => "offmarketplace-test-marker";

    public int Starts => Volatile.Read(ref starts);

    public Task Started => started.Task;

    public async Task AwaitStartedAsync(TimeSpan budget)
    {
        try
        {
            await Started.WaitAsync(budget);
        }
        catch (TimeoutException)
        {
            throw new TimeoutException(
                $"Waited {budget} for the engine to start its units; it started them {Starts} time(s). Either "
                + "nothing ran the engine, or it never won a leadership to run them under.");
        }
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref starts);
        started.TrySetResult();
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

internal sealed class InMemoryGroupManifests : IWorkloadGroupManifestSource
{
    private readonly Dictionary<(string Module, string Version), DeploymentManifest> published = [];

    public void Publish(string groupId, string version, DeploymentManifest manifest)
        => published[(groupId.ToLowerInvariant(), version)] = manifest;

    public Task<AvailableGroupManifestRead> GetAvailableAsync(string groupId, CancellationToken cancellationToken = default)
        => Task.FromResult(new AvailableGroupManifestRead
        {
            HighestAvailable = published
                .Where(p => p.Key.Module == groupId.ToLowerInvariant())
                .OrderByDescending(p => p.Key.Version, StringComparer.Ordinal)
                .Select(p => p.Value)
                .FirstOrDefault(),
        });

    public Task<GroupManifestRead> GetAtVersionAsync(
        string sourceKey, string groupId, string version, CancellationToken cancellationToken = default)
        => Task.FromResult(Find(groupId, version) is { } manifest
            ? new GroupManifestRead(manifest, Refused: false)
            : GroupManifestRead.Absent);

    private DeploymentManifest? Find(string groupId, string version)
        => published.GetValueOrDefault((groupId.ToLowerInvariant(), version));
}

internal sealed class RecordingReloadStage : IStage<WorkloadUpdateStageContext>
{
    public string Id => "stage.host.plugin-roll";

    public StagePlacement Placement => StagePlacement.After(WorkloadUpdateStages.CommitVersion);

    public StageReversibility Reversibility
        => StageReversibility.IrreversibleOnceEntered("a certified N+1 may have forward-migrated data");

    public StageBudget Budget => StageBudget.None;

    public Func<string, Task<string?>> ReadInstalledVersion { get; set; } = _ => Task.FromResult<string?>(null);

    public List<(string SourceKey, string GroupId, string TargetVersion, string? RowVersion)> Initiated { get; } = [];

    public async ValueTask<StageOutcome> UpAsync(WorkloadUpdateStageContext context, CancellationToken cancellationToken)
    {
        Initiated.Add((context.SourceKey, context.GroupId, context.ToVersion, await ReadInstalledVersion(context.GroupId)));
        return StageOutcome.Done;
    }

    public ValueTask<StageOutcome> DownAsync(WorkloadUpdateStageContext context, CancellationToken cancellationToken)
        => ValueTask.FromResult(StageOutcome.Failed("a reload to a version that may have migrated data is never unwound"));
}
