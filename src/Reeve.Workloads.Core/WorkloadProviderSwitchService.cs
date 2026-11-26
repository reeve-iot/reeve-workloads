using Microsoft.Extensions.Logging;
using Reeve.Workloads.Abstractions;
using Reeve.Workloads.Abstractions.Contracts;
using Reeve.Workloads.Abstractions.Domain.Entities;
using Reeve.Workloads.Abstractions.Providers;

namespace Reeve.Workloads.Core;

/// <summary>Disowns a managed workload without deleting its runtime/data, via two guarded commits
/// (Managed → Switching → ExternallyManaged) so no reconcile pass ever sees a both- or neither-owned instant.</summary>
public sealed class WorkloadProviderSwitchService : IWorkloadProviderSwitchService
{
    private const string SwitchLockKeyPrefix = "workload-provider-switch:";

    private const string AuditAction = "workloads.provider.switched-to-manual";
    private const string AuditEntityType = "workload-instance";

    private readonly IWorkloadInstanceRepository repository;
    private readonly IWorkloadProviderRegistry providerRegistry;
    private readonly IActiveWorkloadIntentProvider intentProvider;
    private readonly IWorkloadSwitchTakeoverRenderer takeoverRenderer;
    private readonly IWorkloadManagedOwnershipReleaser ownershipReleaser;
    private readonly IWorkloadLockProvider lockProvider;
    private readonly IWorkloadReconcileNudge reconcileNudge;
    private readonly IWorkloadAuditSink auditSink;
    private readonly IWorkloadClock clock;
    private readonly ILogger<WorkloadProviderSwitchService> logger;

    /// <summary>The constructor taking the bus alone, which reaches peer replicas and no reconciler in this
    /// process. Kept for source compatibility; the container picks the greedier one below.</summary>
    public WorkloadProviderSwitchService(
        IWorkloadInstanceRepository repository,
        IWorkloadProviderRegistry providerRegistry,
        IActiveWorkloadIntentProvider intentProvider,
        IWorkloadSwitchTakeoverRenderer takeoverRenderer,
        IWorkloadManagedOwnershipReleaser ownershipReleaser,
        IWorkloadLockProvider lockProvider,
        IWorkloadClusterBus clusterBus,
        IWorkloadAuditSink auditSink,
        IWorkloadClock clock,
        ILogger<WorkloadProviderSwitchService> logger)
        : this(repository, providerRegistry, intentProvider, takeoverRenderer, ownershipReleaser, lockProvider,
            clusterBus, NoWorkloadReconcileTrigger.Instance, auditSink, clock, logger)
    {
    }

    /// <summary>Takes the local trigger as well, because a bus skips the publishing replica's own messages — a
    /// single-replica host that only published waited for the drift pass.</summary>
    public WorkloadProviderSwitchService(
        IWorkloadInstanceRepository repository,
        IWorkloadProviderRegistry providerRegistry,
        IActiveWorkloadIntentProvider intentProvider,
        IWorkloadSwitchTakeoverRenderer takeoverRenderer,
        IWorkloadManagedOwnershipReleaser ownershipReleaser,
        IWorkloadLockProvider lockProvider,
        IWorkloadClusterBus clusterBus,
        IWorkloadReconcileTrigger reconcileTrigger,
        IWorkloadAuditSink auditSink,
        IWorkloadClock clock,
        ILogger<WorkloadProviderSwitchService> logger)
    {
        this.repository = repository;
        this.providerRegistry = providerRegistry ?? throw new ArgumentNullException(nameof(providerRegistry));
        this.intentProvider = intentProvider;
        this.takeoverRenderer = takeoverRenderer;
        this.ownershipReleaser = ownershipReleaser;
        this.lockProvider = lockProvider;
        this.reconcileNudge = new WorkloadReconcileNudge(clusterBus, reconcileTrigger);
        this.auditSink = auditSink;
        this.clock = clock;
        this.logger = logger;
    }

    public async Task<WorkloadProviderSwitchResult> SwitchToManualAsync(
        Guid workloadInstanceId,
        Guid? actorUserId,
        CancellationToken cancellationToken = default)
    {
        var leaderLock = await lockProvider
            .TryAcquireAsync(SwitchLockKeyPrefix + workloadInstanceId.ToString("N"), cancellationToken)
            .ConfigureAwait(false);
        if (leaderLock is null)
        {
            logger.LogInformation(
                "workloads.switch: instance={InstanceId} not performed on this replica — the per-instance switch lock is held elsewhere (leader-only).",
                workloadInstanceId);
            return WorkloadProviderSwitchResult.NotLeader();
        }

        await using var leaderLockLifetime = leaderLock.ConfigureAwait(false);

        return await ExecuteSwitchAsync(workloadInstanceId, actorUserId, cancellationToken).ConfigureAwait(false);
    }

    private async Task<WorkloadProviderSwitchResult> ExecuteSwitchAsync(
        Guid workloadInstanceId,
        Guid? actorUserId,
        CancellationToken cancellationToken)
    {
        var instance = await repository.GetByIdAsync(workloadInstanceId, cancellationToken).ConfigureAwait(false);
        if (instance is null)
        {
            return WorkloadProviderSwitchResult.NotFound();
        }

        var activeIntentId = await intentProvider
            .GetActiveIntentIdAsync(instance.GroupInstanceId, cancellationToken)
            .ConfigureAwait(false);
        if (activeIntentId is { } intentId)
        {
            logger.LogInformation(
                "workloads.switch: instance={InstanceId} rejected (409) — update intent {IntentId} is active.",
                instance.Id, intentId);
            return WorkloadProviderSwitchResult.Conflict(intentId);
        }

        if (instance.State is not (WorkloadInstanceState.Healthy or WorkloadInstanceState.Degraded))
        {
            logger.LogInformation(
                "workloads.switch: instance={InstanceId} not switchable in state {State} (only a managed-running Healthy/Degraded instance may switch).",
                instance.Id, instance.State);
            return WorkloadProviderSwitchResult.NotSwitchable(instance.State);
        }

        if (instance.Provider is not { } originalProvider || !providerRegistry.Describe(originalProvider).Materializes)
        {
            logger.LogInformation(
                "workloads.switch: instance={InstanceId} not switchable — no bound managed provider to release (provider={Provider}).",
                instance.Id, instance.Provider);
            return WorkloadProviderSwitchResult.NotSwitchable(instance.State);
        }

        var originalState = instance.State;
        var originalReason = instance.StateReason;

        var now = clock.UtcNow;
        instance.TransitionTo(
            WorkloadInstanceState.Switching,
            originalReason,
            "Managed→manual provider switch in progress — Reeve is releasing ownership (runtime/data retained).",
            now);
        await repository.UpdateAsync(instance, cancellationToken).ConfigureAwait(false);

        try
        {
            await takeoverRenderer.RenderTakeoverAsync(instance, cancellationToken).ConfigureAwait(false);

            await ownershipReleaser.ReleaseAsync(instance, originalProvider, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            await UnwindToManagedAsync(instance, originalState, originalReason).ConfigureAwait(false);
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex,
                "workloads.switch: instance={InstanceId} in-band disown failure — unwinding Switching → {OriginalState} (runtime/data untouched).",
                instance.Id, originalState);
            await UnwindToManagedAsync(instance, originalState, originalReason).ConfigureAwait(false);
            return WorkloadProviderSwitchResult.Failed(
                "The managed→manual switch could not be completed and was rolled back — the instance remains under Reeve management. Retry once the cause is resolved.");
        }

        await CommitSwitchExitAsync(
            instance,
            WorkloadInstanceState.ExternallyManaged,
            WorkloadStateReason.ExternalHealthObserved,
            "Disowned to manual operator management — Reeve released ownership (State → ExternallyManaged); the original provider and the runtime/data are retained.").ConfigureAwait(false);

        await WriteAuditAsync(instance, originalProvider, actorUserId).ConfigureAwait(false);

        await NudgeReconcileAsync(instance.Id, cancellationToken).ConfigureAwait(false);

        logger.LogInformation(
            "workloads.switch: instance={InstanceId} actor={ActorUserId} disowned to manual operator management (state ExternallyManaged; provider {Provider} PRESERVED, ownership released, runtime retained).",
            instance.Id, actorUserId, originalProvider);

        return WorkloadProviderSwitchResult.Success();
    }

    private async Task UnwindToManagedAsync(
        WorkloadInstance instance,
        WorkloadInstanceState originalState,
        WorkloadStateReason originalReason)
    {
        await CommitSwitchExitAsync(
            instance,
            originalState,
            originalReason,
            "Managed→manual switch rolled back (in-band failure) — the instance remains under Reeve management (runtime/data untouched).").ConfigureAwait(false);
    }

    private async Task CommitSwitchExitAsync(
        WorkloadInstance instance, WorkloadInstanceState state, WorkloadStateReason reason, string message)
    {
        instance.TransitionTo(state, reason, message, clock.UtcNow);
        try
        {
            await repository.UpdateAsync(instance, CancellationToken.None).ConfigureAwait(false);
            return;
        }
        catch (WorkloadConcurrencyConflictException)
        {
            var stored = await repository.ReloadAsync(instance.Id, CancellationToken.None).ConfigureAwait(false);
            if (stored is not { State: WorkloadInstanceState.Switching })
            {
                throw;
            }

            var held = stored.IsHostHoldPending;
            var now = clock.UtcNow;
            stored.TransitionTo(state, reason, message, now);
            if (held)
            {
                stored.TransitionTo(
                    WorkloadInstanceState.Suspended,
                    state == WorkloadInstanceState.ExternallyManaged
                        ? WorkloadStateReason.SuspendedByHostExternallyManaged
                        : WorkloadStateReason.SuspendedByHost,
                    "Suspended by the host.",
                    now);
            }

            await repository.UpdateAsync(stored, CancellationToken.None).ConfigureAwait(false);
        }
    }

    private async Task WriteAuditAsync(WorkloadInstance instance, WorkloadProviderId fromProvider, Guid? actorUserId)
    {
        try
        {
            await auditSink.WriteAsync(
                new WorkloadAuditEntry(
                    ActorUserId: actorUserId,
                    Action: AuditAction,
                    EntityType: AuditEntityType,
                    EntityId: instance.Id,
                    Metadata: new
                    {
                        instanceId = instance.Id,
                        pluginInstanceId = instance.GroupInstanceId,
                        pluginTypeId = instance.GroupId,
                        workloadId = instance.WorkloadId,
                        provider = fromProvider.ToString(),
                        toState = WorkloadInstanceState.ExternallyManaged.ToString(),
                    }),
                CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex,
                "workloads.switch: failed to write the switch audit entry for instance {InstanceId} (switch already committed).",
                instance.Id);
        }
    }

    private async Task NudgeReconcileAsync(Guid instanceId, CancellationToken cancellationToken)
    {
        try
        {
            await reconcileNudge
                .NudgeInstanceAsync(instanceId, "workload-provider-switch", cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex,
                "workloads.switch: failed to deliver the reconcile hint for instance {InstanceId} (switch already committed; the periodic drift pass will converge).",
                instanceId);
        }
    }
}

/// <summary>The managed→manual provider switch service.</summary>
public interface IWorkloadProviderSwitchService
{
    /// <summary>Disowns the instance without deleting it. The host authorizes the caller before it calls.</summary>
    Task<WorkloadProviderSwitchResult> SwitchToManualAsync(
        Guid workloadInstanceId,
        Guid? actorUserId,
        CancellationToken cancellationToken = default);
}

/// <summary>The typed outcome of a managed→manual provider switch.</summary>
public enum WorkloadProviderSwitchOutcome
{
    /// <summary>The switch completed — the instance is <see cref="WorkloadInstanceState.ExternallyManaged"/> on the manual provider.</summary>
    Success = 0,

    /// <summary>The instance id is unknown.</summary>
    NotFound = 1,

    /// <summary>An update intent is active on the instance's plugin — reject (409), echoing <see cref="WorkloadProviderSwitchResult.ActiveIntentId"/>.</summary>
    Conflict = 3,

    /// <summary>The instance is not in a managed-running state a switch may begin from (e.g. already Switching / not deployed) — reject (409).</summary>
    NotSwitchable = 4,

    /// <summary>This replica does not hold the per-instance switch lock — the switch was NOT performed here (leader-only).</summary>
    NotLeader = 5,

    /// <summary>An in-band failure unwound the switch — the instance remains under Reeve management (runtime/data intact).</summary>
    Failed = 6,
}

/// <summary>The result of <see cref="IWorkloadProviderSwitchService.SwitchToManualAsync"/>.</summary>
public sealed record WorkloadProviderSwitchResult
{
    public required WorkloadProviderSwitchOutcome Outcome { get; init; }

    /// <summary>The active update-intent id for a <see cref="WorkloadProviderSwitchOutcome.Conflict"/>; null otherwise.</summary>
    public Guid? ActiveIntentId { get; init; }

    /// <summary>The blocking lifecycle state for a <see cref="WorkloadProviderSwitchOutcome.NotSwitchable"/>; null otherwise.</summary>
    public WorkloadInstanceState? BlockingState { get; init; }

    /// <summary>A human-readable failure message for <see cref="WorkloadProviderSwitchOutcome.Failed"/>; null otherwise.</summary>
    public string? Error { get; init; }

    public static WorkloadProviderSwitchResult Success() => new() { Outcome = WorkloadProviderSwitchOutcome.Success };

    public static WorkloadProviderSwitchResult NotFound() => new() { Outcome = WorkloadProviderSwitchOutcome.NotFound };

    public static WorkloadProviderSwitchResult Conflict(Guid activeIntentId) =>
        new() { Outcome = WorkloadProviderSwitchOutcome.Conflict, ActiveIntentId = activeIntentId };

    public static WorkloadProviderSwitchResult NotSwitchable(WorkloadInstanceState blockingState) =>
        new() { Outcome = WorkloadProviderSwitchOutcome.NotSwitchable, BlockingState = blockingState };

    public static WorkloadProviderSwitchResult NotLeader() => new() { Outcome = WorkloadProviderSwitchOutcome.NotLeader };

    public static WorkloadProviderSwitchResult Failed(string error) =>
        new() { Outcome = WorkloadProviderSwitchOutcome.Failed, Error = error };
}

/// <summary>Renders the manual-mode deployment artifacts an operator needs, invoked BEFORE Reeve releases managed
/// ownership so the operator is never left without the manifests.</summary>
public interface IWorkloadSwitchTakeoverRenderer
{
    /// <summary>Renders the manual-takeover artifacts for the switching instance.</summary>
    Task RenderTakeoverAsync(WorkloadInstance instance, CancellationToken cancellationToken = default);
}

/// <summary>The default no-op renderer, registered until a real one is wired.</summary>
public sealed class NoOpWorkloadSwitchTakeoverRenderer : IWorkloadSwitchTakeoverRenderer
{
    public Task RenderTakeoverAsync(WorkloadInstance instance, CancellationToken cancellationToken = default)
        => Task.CompletedTask;
}

/// <summary>Inert provider-side cleanup of Reeve's managed claim (k8s: SSA label strip; docker: nothing). The
/// runtime keeps running regardless, but an in-band throw unwinds the switch.</summary>
public interface IWorkloadManagedOwnershipReleaser
{
    /// <summary>Releases managed ownership on the original <paramref name="provider"/> without deleting the
    /// runtime or its data.</summary>
    Task ReleaseAsync(WorkloadInstance instance, WorkloadProviderId provider, CancellationToken cancellationToken = default);
}

/// <summary>The default no-op releaser: correct for docker, a placeholder for the Kubernetes label strip. The DB
/// State flip is the authoritative release, so this never leaves the instance double-owned.</summary>
public sealed class NoOpWorkloadManagedOwnershipReleaser : IWorkloadManagedOwnershipReleaser
{
    public Task ReleaseAsync(WorkloadInstance instance, WorkloadProviderId provider, CancellationToken cancellationToken = default)
        => Task.CompletedTask;
}
