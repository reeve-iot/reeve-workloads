using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Reeve.Workloads.Abstractions;
using Reeve.Workloads.Abstractions.Domain.Entities;
using Reeve.Workloads.Abstractions.Update;

namespace Reeve.Workloads.Core.Update;

internal sealed class WorkloadUpdateWalkUnit : IWorkloadEngineUnit, IWorkloadUpdateWalkTrigger
{
    public const string Key = "workload-update-walk";

    private readonly IServiceScopeFactory scopeFactory;
    private readonly IWorkloadClusterBus clusterBus;
    private readonly WorkloadGroupGate gate;
    private readonly WorkloadUpdateWalkOptions options;
    private readonly ILogger<WorkloadUpdateWalkUnit> logger;
    private readonly TimeProvider time;
    private readonly object wakeLock = new();

    private CancellationTokenSource? cts;
    private Task? loop;
    private IDisposable? busSubscription;
    private SemaphoreSlim? wakeSignal;

    public WorkloadUpdateWalkUnit(
        IServiceScopeFactory scopeFactory,
        IWorkloadClusterBus clusterBus,
        WorkloadGroupGate gate,
        IOptions<WorkloadUpdateWalkOptions> options,
        ILogger<WorkloadUpdateWalkUnit> logger)
        : this(scopeFactory, clusterBus, gate, options, logger, TimeProvider.System)
    {
    }

    public WorkloadUpdateWalkUnit(
        IServiceScopeFactory scopeFactory,
        IWorkloadClusterBus clusterBus,
        WorkloadGroupGate gate,
        IOptions<WorkloadUpdateWalkOptions> options,
        ILogger<WorkloadUpdateWalkUnit> logger,
        TimeProvider timeProvider)
    {
        this.scopeFactory = scopeFactory;
        this.clusterBus = clusterBus;
        this.gate = gate;
        this.options = options.Value;
        this.logger = logger;
        time = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    }

    string IWorkloadEngineUnit.Key => Key;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (loop is not null)
        {
            return Task.CompletedTask;
        }

        cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var token = cts.Token;
        lock (wakeLock)
        {
            wakeSignal = new SemaphoreSlim(0, 1);
        }

        busSubscription = clusterBus.Subscribe<WorkloadReconcileRequestMessage>(
            WorkloadClusterChannels.ReconcileRequest,
            OnReconcileRequestAsync);

        loop = Task.Run(() => RunAsync(token), CancellationToken.None);

        logger.LogInformation(
            "Workload update walk started (working tick {WorkingTick}, idle fallback {IdleFallback}).",
            options.WorkingTickInterval, options.IdleFallbackInterval);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (cts is null)
        {
            return;
        }

        await cts.CancelAsync().ConfigureAwait(false);
        busSubscription?.Dispose();
        busSubscription = null;
        lock (wakeLock)
        {
            wakeSignal = null;
        }

        if (loop is not null)
        {
            try { await loop.ConfigureAwait(false); } catch (OperationCanceledException) { }
        }

        cts.Dispose();
        cts = null;
        loop = null;
    }

    public void Wake()
    {
        lock (wakeLock)
        {
            if (wakeSignal is { CurrentCount: 0 } signal)
            {
                signal.Release();
            }
        }
    }

    private Task OnReconcileRequestAsync(WorkloadReconcileRequestMessage message, CancellationToken cancellationToken)
    {
        if (WorkloadUpdateNudgeReasons.WakesTheWalk(message))
        {
            Wake();
        }

        return Task.CompletedTask;
    }

    private async Task RunAsync(CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                var anyOpen = await TickAsync(token).ConfigureAwait(false);
                await WaitAsync(anyOpen ? options.WorkingTickInterval : options.IdleFallbackInterval, token)
                    .ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
    }

    private async Task WaitAsync(TimeSpan interval, CancellationToken token)
    {
        SemaphoreSlim? signal;
        lock (wakeLock)
        {
            signal = wakeSignal;
        }

        if (signal is null)
        {
            token.ThrowIfCancellationRequested();
            return;
        }

        using var waiting = CancellationTokenSource.CreateLinkedTokenSource(token);
        var elapsed = Task.Delay(interval, time, waiting.Token);
        var woken = signal.WaitAsync(waiting.Token);
        await Task.WhenAny(elapsed, woken).ConfigureAwait(false);
        await waiting.CancelAsync().ConfigureAwait(false);

        try { await Task.WhenAll(elapsed, woken).ConfigureAwait(false); } catch (OperationCanceledException) { }
        token.ThrowIfCancellationRequested();
    }

    internal async Task<bool> TickAsync(CancellationToken token)
    {
        IReadOnlyList<(Guid Intent, Guid Group)> open;
        try
        {
            var scope = scopeFactory.CreateAsyncScope();
            await using (scope.ConfigureAwait(false))
            {
                var intents = await scope.ServiceProvider.GetRequiredService<IWorkloadUpdateIntentRepository>()
                    .GetOpenIntentsAsync(token).ConfigureAwait(false);
                open = [.. intents.Select(intent => (intent.Id, intent.GroupInstanceId))];
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Loading open update intents for the walk failed; retrying on the working tick.");
            return true;
        }

        var anyOpen = false;
        foreach (var (intentId, group) in open)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                anyOpen |= await WalkOneAsync(intentId, group, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Taking group {GroupId} to walk update intent {IntentId} failed; retrying on the working tick.",
                    group, intentId);
                anyOpen = true;
            }
        }

        return anyOpen;
    }

    private async Task<bool> WalkOneAsync(Guid intentId, Guid group, CancellationToken token)
    {
        using var held = await gate.EnterToWalkAsync(group, token).ConfigureAwait(false);

        string walkFault;
        var walking = new Walking();
        try
        {
            var (open, fault) = await WalkHeldAsync(intentId, group, walking, token).ConfigureAwait(false);
            if (fault is null)
            {
                return open;
            }

            walkFault = fault;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Walking update intent {IntentId} (group {GroupId}) threw; retrying on the working tick.",
                intentId, group);
            walkFault = $"the walk threw {ex.GetType().Name}: {ex.Message}";
        }

        await RecordWalkFaultAsync(intentId, walkFault, walking, token).ConfigureAwait(false);
        return true;
    }

    private sealed class Walking
    {
        public WorkloadUpdatePhase? Found { get; set; }

        public WorkloadUpdateIntent? Intent { get; set; }

        public bool MovedByAnotherWriter(WorkloadUpdateIntent stored)
            => Found is { } found && Intent is { } walked
                && ((stored.Phase != found && stored.Phase != walked.Phase)
                    || (stored.Phase == WorkloadUpdatePhase.RollingBack && found != WorkloadUpdatePhase.RollingBack));
    }

    private async Task<(bool Open, string? WalkFault)> WalkHeldAsync(
        Guid intentId, Guid group, Walking walking, CancellationToken token)
    {
        var scope = scopeFactory.CreateAsyncScope();
        await using var scopeLifetime = scope.ConfigureAwait(false);
        var intent = await scope.ServiceProvider.GetRequiredService<IWorkloadUpdateIntentRepository>()
            .GetByIdAsync(intentId, token).ConfigureAwait(false);
        if (intent is null || intent.IsTerminal)
        {
            return (false, null);
        }

        walking.Found = intent.Phase;
        walking.Intent = intent;
        var result = await scope.ServiceProvider.GetRequiredService<WorkloadUpdateOrchestrator>()
            .WalkAsync(intent, token).ConfigureAwait(false);
        logger.LogDebug(
            "Walked update intent {IntentId} (group {GroupId}): {Phase} ({Progressed}) — {Detail}.",
            intent.Id, group, result.Phase, result.Progressed ? "advanced" : "stayed", result.Detail);

        return (!result.IsTerminal, result.IsTerminal ? null : result.WalkFault);
    }

    private async Task RecordWalkFaultAsync(Guid intentId, string walkFault, Walking walking, CancellationToken token)
    {
        try
        {
            var scope = scopeFactory.CreateAsyncScope();
            await using var scopeLifetime = scope.ConfigureAwait(false);
            var repository = scope.ServiceProvider.GetRequiredService<IWorkloadUpdateIntentRepository>();
            var intent = await repository.GetByIdAsync(intentId, token).ConfigureAwait(false);
            if (intent is null || intent.IsTerminal)
            {
                return;
            }

            if (walking.MovedByAnotherWriter(intent))
            {
                logger.LogInformation(
                    "Update intent {IntentId} was moved to {Phase} by another writer while it was walked; that is not a walk fault.",
                    intentId, intent.Phase);
                return;
            }

            if (intent.RecordWalkFault(walkFault, scope.ServiceProvider.GetRequiredService<IWorkloadClock>().UtcNow))
            {
                await repository.UpdateAsync(intent, token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Recording the walk fault of update intent {IntentId} failed; the next tick tries again.",
                intentId);
        }
    }
}
