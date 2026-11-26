using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Reeve.Workloads.Abstractions;
using Reeve.Workloads.Abstractions.Domain.Entities;
using Reeve.Workloads.Abstractions.Providers;
using Reeve.Workloads.Core.Providers;
using Reeve.Workloads.Core.Telemetry;
using Reeve.Workloads.Core.Update;
using Reeve.Workloads.Core.Watch;

namespace Reeve.Workloads.Core;

internal sealed class WorkloadReconcilerUnit : IWorkloadEngineUnit, IWorkloadReconcileTrigger
{
    public const string Key = "workload-reconciler";

    private readonly IServiceScopeFactory scopeFactory;
    private readonly IWorkloadClusterBus clusterBus;
    private readonly IWorkloadClock clock;
    private readonly WorkloadGroupGate groupGate;
    private readonly WorkloadReconcilerOptions options;
    private readonly ILogger<WorkloadReconcilerUnit> logger;

    private readonly WorkloadReconcileQueue queue;

    private readonly object triggeredLock = new();
    private readonly HashSet<Task> triggeredPasses = new();

    private CancellationTokenSource? cts;
    private Task[] workers = Array.Empty<Task>();
    private Task? bootPass;
    private Task? periodicLoop;
    private IDisposable? busSubscription;
    private bool started;

    public WorkloadReconcilerUnit(
        IServiceScopeFactory scopeFactory,
        IWorkloadClusterBus clusterBus,
        IWorkloadClock clock,
        WorkloadGroupGate groupGate,
        IOptions<WorkloadReconcilerOptions> options,
        ILogger<WorkloadReconcilerUnit> logger)
    {
        this.scopeFactory = scopeFactory;
        this.clusterBus = clusterBus;
        this.clock = clock;
        this.groupGate = groupGate;
        this.options = options.Value;
        this.logger = logger;
        queue = new WorkloadReconcileQueue(clock, this.options);

        WorkloadsMetrics.ObserveQueue(() => (queue.Count, queue.SettlingCount));
    }

    string IWorkloadEngineUnit.Key => Key;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (started)
        {
            return;
        }

        await ResumeOpenIntentsAsync(cancellationToken).ConfigureAwait(false);

        cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var token = cts.Token;

        workers = Enumerable.Range(0, options.ReconcileWorkerCount)
            .Select(index => RunWorkerAsync(index, token))
            .ToArray();

        busSubscription = clusterBus.Subscribe<WorkloadReconcileRequestMessage>(
            WorkloadClusterChannels.ReconcileRequest,
            (msg, ct) => OnReconcileRequestAsync(msg, ct));

        bootPass = Task.Run(() => RunBootPassAsync(token), CancellationToken.None);
        periodicLoop = RunPeriodicLoopAsync(bootPass, token);

        started = true;

        logger.LogInformation(
            "Workload reconciler started ({Workers} worker(s), max {Settling} settling, periodic pass every {Interval}, watchEnabled={WatchEnabled}); the boot pass runs in the background.",
            options.ReconcileWorkerCount, options.MaxConcurrentSettling, options.StatusResyncInterval, options.WatchEnabled);
    }

    internal Task BootPassCompletion => bootPass ?? Task.CompletedTask;

    private async Task RunBootPassAsync(CancellationToken token)
    {
        try
        {
            await ReconcileAllAsync(WorkloadReconcileTrigger.Boot, token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Workload reconciler boot pass faulted; the periodic loop remains running to retry.");
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (cts is null)
        {
            return;
        }

        started = false;
        await cts.CancelAsync().ConfigureAwait(false);

        busSubscription?.Dispose();
        busSubscription = null;

        foreach (var loop in new[] { bootPass, periodicLoop })
        {
            if (loop is not null)
            {
                try { await loop.ConfigureAwait(false); } catch (OperationCanceledException) { }
            }
        }

        Task[] pending;
        lock (triggeredLock)
        {
            pending = triggeredPasses.ToArray();
        }
        foreach (var pass in pending)
        {
            try { await pass.ConfigureAwait(false); } catch (OperationCanceledException) { } catch { }
        }

        queue.Clear();
        foreach (var worker in workers)
        {
            try { await worker.ConfigureAwait(false); } catch (OperationCanceledException) { } catch { }
        }

        workers = Array.Empty<Task>();
        bootPass = null;
        periodicLoop = null;
        cts.Dispose();
        cts = null;
    }

    private async Task ResumeOpenIntentsAsync(CancellationToken cancellationToken)
    {
        try
        {
            var scope = scopeFactory.CreateAsyncScope();
            await using var scopeLifetime = scope.ConfigureAwait(false);
            var resumer = scope.ServiceProvider.GetRequiredService<IWorkloadIntentResumer>();
            await resumer.ResumeOpenIntentsAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Resuming open workload update intents at boot failed; continuing to the boot reconcile pass.");
        }
    }

    private async Task RunPeriodicLoopAsync(Task boot, CancellationToken token)
    {
        await boot.ConfigureAwait(false);
        using var timer = new PeriodicTimer(options.StatusResyncInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(token).ConfigureAwait(false))
            {
                try
                {
                    await RunPeriodicPassAsync(token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Workload reconciler periodic pass faulted; continuing on the next tick.");
                }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
    }

    private Task OnReconcileRequestAsync(WorkloadReconcileRequestMessage message, CancellationToken cancellationToken)
    {
        if (message.InstanceId is { } instanceId)
        {
            queue.Enqueue(instanceId, WorkloadReconcileTrigger.BusNudge);
            return Task.CompletedTask;
        }

        if (string.Equals(message.Reason, WorkloadReconcileReasons.SettingsChanged, StringComparison.Ordinal))
        {
            OffloadTriggeredPass(async ct =>
            {
                await RefreshWatchAfterSettingsChangeAsync(ct).ConfigureAwait(false);
                await ReconcileAllAsync(WorkloadReconcileTrigger.BusNudge, ct).ConfigureAwait(false);
            });
            return Task.CompletedTask;
        }

        OffloadTriggeredPass(ct => ReconcileAllAsync(WorkloadReconcileTrigger.BusNudge, ct));
        return Task.CompletedTask;
    }

    private async Task RefreshWatchAfterSettingsChangeAsync(CancellationToken cancellationToken)
    {
        try
        {
            var scope = scopeFactory.CreateAsyncScope();
            await using var scopeLifetime = scope.ConfigureAwait(false);
            await scope.ServiceProvider.GetRequiredService<IWorkloadWatchedProviderRefresh>()
                .RefreshAfterSettingsChangeAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Re-resolving the watched providers after a settings change on another replica failed.");
        }
    }

    internal static WorkloadReconcileTrigger AttributionFor(string reason)
        => reason switch
        {
            WorkloadReconcileReasons.Presence => WorkloadReconcileTrigger.Presence,
            WorkloadReconcileReasons.Watch or WorkloadReconcileReasons.WatchResync => WorkloadReconcileTrigger.Watch,
            _ => WorkloadReconcileTrigger.LocalNudge,
        };

    public Task RequestInstanceAsync(Guid instanceId, string reason, CancellationToken cancellationToken = default)
    {
        if (!started)
        {
            return Task.CompletedTask;
        }

        var attribution = AttributionFor(reason);
        logger.LogDebug("Reconcile nudge for instance {InstanceId} (reason {Reason}, {Trigger}).", instanceId, reason, attribution);

        queue.Enqueue(instanceId, attribution);
        return Task.CompletedTask;
    }

    public Task RequestAllAsync(string reason, CancellationToken cancellationToken = default)
    {
        if (!started)
        {
            return Task.CompletedTask;
        }

        var attribution = AttributionFor(reason);
        logger.LogDebug("Reconcile nudge for all instances (reason {Reason}, {Trigger}).", reason, attribution);
        OffloadTriggeredPass(ct => ReconcileAllAsync(attribution, ct));
        return Task.CompletedTask;
    }

    private void OffloadTriggeredPass(Func<CancellationToken, Task> pass)
    {
        var token = cts?.Token ?? CancellationToken.None;
        if (token.IsCancellationRequested)
        {
            return;
        }

        var task = Task.Run(async () =>
        {
            try
            {
                await pass(token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Triggered workload reconcile pass faulted.");
            }
        }, CancellationToken.None);

        lock (triggeredLock)
        {
            triggeredPasses.Add(task);
        }
        _ = task.ContinueWith(t =>
        {
            lock (triggeredLock)
            {
                triggeredPasses.Remove(t);
            }
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    internal async Task WaitForTriggeredPassesAsync()
    {
        while (true)
        {
            Task[] pending;
            lock (triggeredLock)
            {
                pending = triggeredPasses.ToArray();
            }
            if (pending.Length == 0)
            {
                return;
            }
            try { await Task.WhenAll(pending).ConfigureAwait(false); } catch { }
        }
    }

    internal int QueueDepth => queue.Count;

    internal async Task WaitForQueueAsync()
    {
        await BootPassCompletion.ConfigureAwait(false);
        await WaitForTriggeredPassesAsync().ConfigureAwait(false);
        await DrainAsync(CancellationToken.None).ConfigureAwait(false);
    }

    internal async Task ReconcileAllAsync(WorkloadReconcileTrigger trigger, CancellationToken cancellationToken)
    {
        var started = Stopwatch.GetTimestamp();
        try
        {
            var scope = scopeFactory.CreateAsyncScope();
            await using (scope.ConfigureAwait(false))
            {
                var repository = scope.ServiceProvider.GetRequiredService<IWorkloadInstanceRepository>();
                var references = await repository.ListRefsAsync(cancellationToken).ConfigureAwait(false);

                if (scope.ServiceProvider.GetService<WorkloadFleetStatusCache>() is { } fleetStatus)
                {
                    await fleetStatus
                        .PrimeAsync(
                            scope.ServiceProvider.GetRequiredService<IWorkloadProviderRegistry>(),
                            references,
                            cancellationToken)
                        .ConfigureAwait(false);
                }

                foreach (var reference in StableOrder(references))
                {
                    queue.Enqueue(reference.Id, trigger);
                }
            }

            await DrainAsync(cancellationToken).ConfigureAwait(false);
            WorkloadsMetrics.PassCompleted(trigger, Stopwatch.GetElapsedTime(started));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    internal async Task ReconcileOneAsync(Guid instanceId, WorkloadReconcileTrigger trigger, CancellationToken cancellationToken)
    {
        queue.Enqueue(instanceId, trigger);
        try
        {
            await DrainAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private async Task DrainAsync(CancellationToken cancellationToken)
    {
        while (await queue.DequeueAsync(cancellationToken, untilQuiescent: true).ConfigureAwait(false) is { } item)
        {
            await RunAndRecordAsync(item, -1, cancellationToken).ConfigureAwait(false);
        }
    }

    internal async Task RunPeriodicPassAsync(CancellationToken cancellationToken)
    {
        try
        {
            var scope = scopeFactory.CreateAsyncScope();
            await using (scope.ConfigureAwait(false))
            {
                await new WorkloadStrandedSwitchUnwind(
                        scope.ServiceProvider.GetRequiredService<IWorkloadInstanceRepository>(), clock, logger)
                    .UnwindAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return;
        }

        await ReconcileAllAsync(WorkloadReconcileTrigger.Timer, cancellationToken).ConfigureAwait(false);
    }

    private async Task<WorkloadReconcileOutcome?> ReconcileInstanceCoreAsync(
        IServiceProvider scopedServices,
        IWorkloadReconcileEngine engine,
        WorkloadInstance instance,
        WorkloadReconcileTrigger trigger,
        CancellationToken cancellationToken)
    {
        try
        {
            if (instance.State == WorkloadInstanceState.Terminating)
            {
                var lifecycle = scopedServices.GetRequiredService<IWorkloadLifecycleService>();
                var torndown = await lifecycle.CompleteTeardownAsync(instance, cancellationToken).ConfigureAwait(false);
                if (instance.Provider is { } bound)
                {
                    await scopedServices.GetRequiredService<IWorkloadWatchedProviderRefresh>()
                        .RefreshAfterTeardownAttemptAsync(bound, cancellationToken).ConfigureAwait(false);
                }

                logger.LogDebug(
                    "Terminating workload instance {InstanceId} ({Trigger}) delegated to teardown — {Result}.",
                    instance.Id, trigger, torndown ? "completed (row removed)" : "retry-pending");
                return null;
            }

            var outcome = await engine.ReconcileInstanceAsync(instance, cancellationToken).ConfigureAwait(false);
            if (instance.Provider is { } provider)
            {
                await scopedServices.GetRequiredService<IWorkloadWatchedProviderRefresh>()
                    .RefreshIfUnwatchedAsync(provider, cancellationToken).ConfigureAwait(false);
            }

            return outcome;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
    }

    private static bool RequiresHealthBarrier(WorkloadReconcileOutcome? outcome, WorkloadInstance instance)
        => outcome is { Action: WorkloadReconcileAction.Applied } && !instance.HasPendingScanRun;

    private async Task RunWorkerAsync(int index, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            WorkloadReconcileWorkItem item;
            try
            {
                if (await queue.DequeueAsync(token).ConfigureAwait(false) is not { } taken)
                {
                    return;
                }

                item = taken;
            }
            catch (OperationCanceledException)
            {
                return;
            }

            try
            {
                await RunAndRecordAsync(item, index, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                return;
            }
        }
    }

    private async Task RunAndRecordAsync(WorkloadReconcileWorkItem item, int worker, CancellationToken token)
    {
        using var span = WorkloadsTelemetry.Source.StartActivity(WorkloadsTelemetry.ReconcileSpan);
        span?.SetTag("workload.instance_id", item.InstanceId);
        span?.SetTag("workload.trigger", item.Trigger.ToString());
        var started = Stopwatch.GetTimestamp();
        try
        {
            var completion = await RunWorkItemAsync(item, token).ConfigureAwait(false);
            var settle = queue.Complete(item, completion);
            LogSettle(item, settle);
            Record(item, Outcome(completion, settle), started, span);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            throw;
        }
        catch (WorkloadConcurrencyConflictException conflict)
        {
            queue.Complete(item, new WorkloadReconcileCompletion { RequeueAfter = options.RetryBackoffBase });
            logger.LogDebug(
                "Reconcile of workload instance {InstanceId} lost a write race ({Reason}); re-reading in {RetryIn}.",
                item.InstanceId, conflict.Message, options.RetryBackoffBase);
            WorkloadsMetrics.ReconcileFailed(item.Trigger, "conflict");
            Record(item, "conflict", started, span);
        }
        catch (Exception ex)
        {
            var retryIn = queue.Fault(item);
            logger.LogError(ex,
                "Reconcile of workload instance {InstanceId} ({Trigger}) threw (worker {Worker}); retrying in {RetryIn}.",
                item.InstanceId, item.Trigger, worker, retryIn);
            WorkloadsMetrics.ReconcileFailed(item.Trigger, "fault");
            span?.SetStatus(ActivityStatusCode.Error, ex.Message);
            Record(item, "fault", started, span);
        }
    }

    private void Record(WorkloadReconcileWorkItem item, string outcome, long started, Activity? span)
    {
        span?.SetTag("workload.outcome", outcome);
        WorkloadsMetrics.Reconciled(item.Trigger, outcome, Stopwatch.GetElapsedTime(started));
    }

    private static string Outcome(WorkloadReconcileCompletion completion, WorkloadSettleResult settle) => settle switch
    {
        WorkloadSettleResult.TimedOut => "barrier-timeout",
        WorkloadSettleResult.Settling => "settling",
        WorkloadSettleResult.Settled => "settled",
        _ => completion.Applied ? "applied" : "noop",
    };

    private async Task<WorkloadReconcileCompletion> RunWorkItemAsync(WorkloadReconcileWorkItem item, CancellationToken token)
    {
        var scope = scopeFactory.CreateAsyncScope();
        await using var scopeLifetime = scope.ConfigureAwait(false);
        var engine = scope.ServiceProvider.GetRequiredService<IWorkloadReconcileEngine>();
        var repository = scope.ServiceProvider.GetRequiredService<IWorkloadInstanceRepository>();

        if (item.BarrierPoll)
        {
            var state = await repository.TryGetStateAsync(item.InstanceId, token).ConfigureAwait(false);
            if (state is null)
            {
                LogUnknownInstance(item);

                return new WorkloadReconcileCompletion { Healthy = true };
            }

            return new WorkloadReconcileCompletion { Healthy = state == WorkloadInstanceState.Healthy };
        }

        var walkEpoch = groupGate.WalkEpoch;
        var instance = await repository.GetByIdAsync(item.InstanceId, token).ConfigureAwait(false);
        if (instance is null)
        {
            LogUnknownInstance(item);
            return new WorkloadReconcileCompletion { Healthy = false };
        }

        WorkloadReconcileOutcome? outcome;
        using (await groupGate.EnterToReconcileAsync(instance.GroupInstanceId, token).ConfigureAwait(false))
        {
            if (groupGate.WalkEpoch != walkEpoch)
            {
                instance = await repository.ReloadAsync(item.InstanceId, token).ConfigureAwait(false);
                if (instance is null)
                {
                    LogUnknownInstance(item);
                    return new WorkloadReconcileCompletion { Healthy = false };
                }
            }

            outcome = await ReconcileInstanceCoreAsync(scope.ServiceProvider, engine, instance, item.Trigger, token)
                .ConfigureAwait(false);
        }

        return new WorkloadReconcileCompletion
        {
            Applied = RequiresHealthBarrier(outcome, instance),
            Healthy = instance.State == WorkloadInstanceState.Healthy,
            RequeueAfter = outcome?.RequeueAfter,
        };
    }

    private void LogUnknownInstance(WorkloadReconcileWorkItem item)
        => logger.LogDebug(
            "Reconcile request for unknown workload instance {InstanceId} ({Trigger}) — ignored.", item.InstanceId, item.Trigger);

    private void LogSettle(WorkloadReconcileWorkItem item, WorkloadSettleResult settle)
    {
        if (settle == WorkloadSettleResult.TimedOut)
        {
            WorkloadsMetrics.BarrierTimedOut();
            logger.LogWarning(
                "Workload instance {InstanceId} did not reach Healthy within the {Timeout} stagger barrier; releasing its slot (Repairs escalation is separate).",
                item.InstanceId, options.HealthBarrierTimeout);
        }
        else if (settle == WorkloadSettleResult.Settling)
        {
            logger.LogDebug(
                "Workload instance {InstanceId} applied and is settling; re-queued in {Poll}.",
                item.InstanceId, options.HealthBarrierPollInterval);
        }
    }

    private static IReadOnlyList<WorkloadInstanceRef> StableOrder(IReadOnlyList<WorkloadInstanceRef> references)
        => references
            .OrderBy(i => i.GroupInstanceId)
            .ThenBy(i => i.WorkloadId, StringComparer.Ordinal)
            .ThenBy(i => i.Id)
            .ToList();
}

/// <summary>Which trigger drove a reconcile pass (diagnostics only).</summary>
public enum WorkloadReconcileTrigger
{
    Boot = 0,
    Timer = 1,
    BusNudge = 2,
    Presence = 3,

    /// <summary>A Kubernetes list-watch or Docker events stream nudged the reconciler. Distinct so a
    /// watch-driven pass is attributable in logs/telemetry apart from the other triggers.</summary>
    Watch = 4,

    /// <summary>A write path in THIS process asked for the pass — an install, a suspend, a parameter edit.
    /// Distinct from <see cref="BusNudge"/>, which is the same request arriving from another replica.</summary>
    LocalNudge = 5,
}
