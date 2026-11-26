using Microsoft.Extensions.Logging;
using Reeve.Workloads.Abstractions;
using Reeve.Workloads.Abstractions.Contracts;
using Reeve.Workloads.Abstractions.Contracts.Deployment;
using Reeve.Workloads.Abstractions.Domain.DesiredSpec;
using Reeve.Workloads.Abstractions.Domain.Entities;
using Reeve.Workloads.Abstractions.Providers;
using Reeve.Workloads.Domain.DesiredSpec;

namespace Reeve.Workloads.Core;

internal sealed class WorkloadOperationsService : IWorkloadOperationsService
{
    private readonly IWorkloadInstanceRepository repository;
    private readonly IActiveWorkloadIntentProvider intentProvider;
    private readonly IWorkloadReconcileNudge reconcileNudge;
    private readonly IWorkloadClock clock;
    private readonly ILogger<WorkloadOperationsService> logger;
    private readonly IWorkloadReconcileInputsProvider inputsProvider;
    private readonly IWorkloadProviderRegistry providerRegistry;

    private const string ScanNodesReadableCheckId = "scan-nodes-readable";

    public WorkloadOperationsService(
        IWorkloadInstanceRepository repository,
        IActiveWorkloadIntentProvider intentProvider,
        IWorkloadReconcileNudge reconcileNudge,
        IWorkloadClock clock,
        ILogger<WorkloadOperationsService> logger,
        IWorkloadReconcileInputsProvider inputsProvider,
        IWorkloadProviderRegistry providerRegistry)
    {
        this.repository = repository;
        this.intentProvider = intentProvider;
        this.reconcileNudge = reconcileNudge ?? throw new ArgumentNullException(nameof(reconcileNudge));
        this.clock = clock;
        this.logger = logger;
        this.inputsProvider = inputsProvider;
        this.providerRegistry = providerRegistry;
    }

    public Task<WorkloadOperationResult> RestartAsync(Guid workloadInstanceId, CancellationToken cancellationToken = default)
        => RedeployInternalAsync(workloadInstanceId, "workload-restart", "restart", bumpRestartNonce: true, cancellationToken);

    public Task<WorkloadOperationResult> RedeployAsync(Guid workloadInstanceId, CancellationToken cancellationToken = default)
        => RedeployInternalAsync(workloadInstanceId, "workload-redeploy", "redeploy", bumpRestartNonce: false, cancellationToken);

    public Task<WorkloadOperationResult> PauseAsync(Guid workloadInstanceId, CancellationToken cancellationToken = default)
        => TransitionAsync(
            workloadInstanceId,
            WorkloadInstanceState.Suspended,
            WorkloadStateReason.OperatorPaused,
            message: "Paused by operator.",
            clearAppliedSpecHash: false,
            bumpRestartNonce: false,
            reason: "workload-pause",
            cancellationToken,
            runtimeCommand: "pause");

    public Task<WorkloadOperationResult> ResumeAsync(Guid workloadInstanceId, CancellationToken cancellationToken = default)
        => TransitionAsync(
            workloadInstanceId,
            WorkloadInstanceState.Deploying,
            WorkloadStateReason.OperatorResumed,
            message: "Resumed by operator.",
            clearAppliedSpecHash: true,
            bumpRestartNonce: false,
            reason: "workload-resume",
            cancellationToken,
            requiredCurrentState: WorkloadInstanceState.Suspended);

    public async Task<WorkloadOperationResult> SetObserveOnlyAsync(
        Guid workloadInstanceId, bool observeOnly, CancellationToken cancellationToken = default)
    {
        var instance = await repository.GetByIdAsync(workloadInstanceId, cancellationToken).ConfigureAwait(false);
        if (instance is null)
        {
            return WorkloadOperationResult.NotFound();
        }

        var activeIntentId = await intentProvider
            .GetActiveIntentIdAsync(instance.GroupInstanceId, cancellationToken)
            .ConfigureAwait(false);
        if (activeIntentId is { } intentId)
        {
            return WorkloadOperationResult.Conflict(intentId);
        }

        var now = clock.UtcNow;
        instance.SetObserveOnly(observeOnly, now);
        await repository.UpdateAsync(instance, cancellationToken).ConfigureAwait(false);

        await NudgeReconcileAsync(instance.Id, observeOnly ? "workload-observe-only-on" : "workload-observe-only-off", cancellationToken)
            .ConfigureAwait(false);

        logger.LogInformation(
            "workloads.operation: instance={InstanceId} command=observe-only observeOnly={ObserveOnly} → state={State} (flag flip, no transition)",
            instance.Id,
            observeOnly,
            instance.State);

        return WorkloadOperationResult.Success(instance.State);
    }

    private Task<WorkloadOperationResult> RedeployInternalAsync(
        Guid workloadInstanceId, string reason, string runtimeCommand, bool bumpRestartNonce, CancellationToken cancellationToken)
        => TransitionAsync(
            workloadInstanceId,
            WorkloadInstanceState.Deploying,
            WorkloadStateReason.Redeploying,
            message: "Redeploying by operator request.",
            clearAppliedSpecHash: true,
            bumpRestartNonce: bumpRestartNonce,
            reason: reason,
            cancellationToken,
            runtimeCommand: runtimeCommand);

    private async Task<WorkloadOperationResult> TransitionAsync(
        Guid workloadInstanceId,
        WorkloadInstanceState target,
        WorkloadStateReason stateReason,
        string message,
        bool clearAppliedSpecHash,
        bool bumpRestartNonce,
        string reason,
        CancellationToken cancellationToken,
        WorkloadInstanceState? requiredCurrentState = null,
        string? runtimeCommand = null)
    {
        var instance = await repository.GetByIdAsync(workloadInstanceId, cancellationToken).ConfigureAwait(false);
        if (instance is null)
        {
            return WorkloadOperationResult.NotFound();
        }

        var activeIntentId = await intentProvider
            .GetActiveIntentIdAsync(instance.GroupInstanceId, cancellationToken)
            .ConfigureAwait(false);
        if (activeIntentId is { } intentId)
        {
            return WorkloadOperationResult.Conflict(intentId);
        }

        if (instance.IsOwnershipReleased(providerRegistry) && !instance.IsHeldByHost)
        {
            logger.LogInformation(
                "workloads.operation: instance={InstanceId} command={Reason} REJECTED — Reeve released ownership of the instance ({State}); a disowned/mid-switch instance is re-adopted first, not driven by a generic lifecycle command.",
                instance.Id, reason, instance.State);
            return WorkloadOperationResult.NotMutable(instance.State);
        }

        if (instance.IsHeldByHost)
        {
            logger.LogInformation(
                "workloads.operation: instance={InstanceId} command={Reason} REJECTED — the host holds it suspended; only the host resumes it.",
                instance.Id, reason);
            return WorkloadOperationResult.NotMutable(instance.State);
        }

        if (!instance.CanMutate)
        {
            return WorkloadOperationResult.NotMutable(instance.State);
        }

        if (runtimeCommand is not null
            && instance.Provider is { } bound
            && providerRegistry.Describe(bound) is { Materializes: false } handApplied)
        {
            logger.LogInformation(
                "workloads.operation: instance={InstanceId} command={Reason} REJECTED — provider '{Provider}' materialises nothing, so Reeve cannot {Command} the operator's runtime; nothing written.",
                instance.Id, reason, handApplied.Label, runtimeCommand);
            return WorkloadOperationResult.ProviderCannotControlRuntime(handApplied.Label, runtimeCommand);
        }

        if (requiredCurrentState is { } required && instance.State != required)
        {
            logger.LogInformation(
                "workloads.operation: instance={InstanceId} command={Reason} REJECTED — the command applies to a {Required} instance and this one is {State}.",
                instance.Id, reason, required, instance.State);
            return WorkloadOperationResult.NotMutable(instance.State);
        }

        if (target != instance.State && !WorkloadInstanceStateMachine.IsValidTransition(instance.State, target))
        {
            return WorkloadOperationResult.NotMutable(instance.State);
        }

        var now = clock.UtcNow;
        if (clearAppliedSpecHash)
        {
            instance.SetAppliedSpecHash(null, now);
        }

        if (bumpRestartNonce)
        {
            instance.MarkRestart(now);
        }

        instance.TransitionTo(target, stateReason, message, now);
        await repository.UpdateAsync(instance, cancellationToken).ConfigureAwait(false);

        await NudgeReconcileAsync(instance.Id, reason, cancellationToken).ConfigureAwait(false);

        logger.LogInformation(
            "workloads.operation: instance={InstanceId} command={Reason} → state={State} reason={StateReason}",
            instance.Id,
            reason,
            instance.State,
            stateReason.ToString());

        return WorkloadOperationResult.Success(instance.State);
    }

    private Task NudgeReconcileAsync(Guid instanceId, string reason, CancellationToken cancellationToken)
        => reconcileNudge.NudgeInstanceAsync(instanceId, reason, cancellationToken);

    public async Task<WorkloadOperationResult> ScanAsync(Guid workloadInstanceId, CancellationToken cancellationToken = default)
    {
        var instance = await repository.GetByIdAsync(workloadInstanceId, cancellationToken).ConfigureAwait(false);
        if (instance is null)
        {
            return WorkloadOperationResult.NotFound();
        }

        var activeIntentId = await intentProvider
            .GetActiveIntentIdAsync(instance.GroupInstanceId, cancellationToken)
            .ConfigureAwait(false);
        if (activeIntentId is { } intentId)
        {
            return WorkloadOperationResult.Conflict(intentId);
        }

        if (instance.IsOwnershipReleased(providerRegistry) || !instance.CanMutate)
        {
            return WorkloadOperationResult.NotMutable(instance.State);
        }

        if (instance.State == WorkloadInstanceState.Suspended)
        {
            logger.LogInformation(
                "workloads.scan: instance={InstanceId} refused — the instance is SUSPENDED ({StateReason}); the engine's "
                + "fan-out branch will not run for a held instance, so no run-id is minted and no marker is set. Resume it first.",
                instance.Id, instance.StateReason.ToString());
            return WorkloadOperationResult.NotMutable(instance.State);
        }

        var answer = await inputsProvider.GetInputsAsync(instance, cancellationToken).ConfigureAwait(false);
        if (answer.Refusal is { } refusal)
        {
            logger.LogInformation(
                "workloads.scan: instance={InstanceId} refused — the host refused its bootstrap: {Refusal}. No run-id "
                + "minted, no marker set.",
                instance.Id, refusal);
            return WorkloadOperationResult.HostRefused(refusal);
        }

        if (answer.Inputs is not { } inputs)
        {
            return WorkloadOperationResult.NotMutable(instance.State);
        }

        if (inputs.WorkloadManifest.Kind != WorkloadKind.ScanPerNode)
        {
            return WorkloadOperationResult.NotMutable(instance.State);
        }

        var providerKind = instance.Provider ?? inputs.EffectiveProvider.Id;
        var provider = providerRegistry.TryResolve(providerKind);
        if (provider is null)
        {
            return WorkloadOperationResult.NotMutable(instance.State);
        }

        var declared = providerRegistry.Describe(providerKind);
        if (!declared.Materializes)
        {
            logger.LogInformation(
                "workloads.scan: instance={InstanceId} refused — provider '{Provider}' materialises nothing at "
                + "runtime, so it can create no per-node Job; ProviderCannotScan, no run-id minted, marker left unset, "
                + "provider not consulted.",
                instance.Id, declared.Label);
            return WorkloadOperationResult.ProviderCannotScan(declared.Label);
        }

        if (instance.HasPendingScanRun
            || await IsScanInFlightAsync(instance, provider, cancellationToken).ConfigureAwait(false))
        {
            logger.LogInformation(
                "workloads.scan: instance={InstanceId} refused — a scan run is already in progress (pending-marker or ≥1 active Job); no new run-id minted.",
                instance.Id);
            return WorkloadOperationResult.ScanAlreadyInProgress();
        }

        WorkloadDesiredSpec desired;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            desired = BuildDesiredSpec(instance, inputs);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex,
                "workloads.scan: instance={InstanceId} refused — the desired spec cannot be computed from an out-of-policy stored parameter or an unresolvable scan image; returning NotMutable, marker left unset (mirrors the engine's ClassifyComputeFailure config-problem hold).",
                instance.Id);
            return WorkloadOperationResult.NotMutable(instance.State);
        }

        var preflight = await provider.PreflightAsync(desired, cancellationToken).ConfigureAwait(false);
        if (preflight.HasBlockingError)
        {
            var blockingErrors = preflight.Checks
                .Where(c => !c.Passed && c.Severity == PreflightSeverity.Error)
                .ToList();

            if (blockingErrors.Any(c => c.Id == ScanNodesReadableCheckId))
            {
                logger.LogWarning(
                    "workloads.scan: instance={InstanceId} preflight reported a blocking nodes-readability error (403) — failing the scan trigger loud as NodesUnreadable, marker left unset.",
                    instance.Id);
                return WorkloadOperationResult.NodesUnreadable();
            }

            var blocking = blockingErrors[0];
            logger.LogWarning(
                "workloads.scan: instance={InstanceId} preflight reported a blocking error '{CheckId}' (not the nodes-403) — failing the scan trigger loud as PreflightFailed, marker left unset: {CheckMessage}",
                instance.Id, blocking.Id, blocking.Message);
            return WorkloadOperationResult.PreflightFailed(blocking.Message, blocking.FixSnippet);
        }

        var eligible = preflight.ReadyNodeNames ?? Array.Empty<string>();
        var ineligible = preflight.IneligibleNodeNames ?? Array.Empty<string>();
        if (eligible.Count == 0)
        {
            if (ineligible.Count > 0)
            {
                logger.LogWarning(
                    "workloads.scan: instance={InstanceId} found 0 eligible Ready nodes at trigger time — the cluster is NOT empty: all {IneligibleCount} Ready node(s) were skipped as INELIGIBLE by the node-eligibility selector ({IneligibleNodes}). This is a node-eligibility CONFIGURATION problem — label those nodes for the attachment's parent interface, or widen the selector. NoTargets, pending-run marker left UNSET.",
                    instance.Id, ineligible.Count, string.Join(", ", ineligible));
            }
            else
            {
                logger.LogInformation(
                    "workloads.scan: instance={InstanceId} found 0 eligible Ready nodes at trigger time — NoTargets, pending-run marker left UNSET.",
                    instance.Id);
            }

            return WorkloadOperationResult.NoTargets();
        }

        var now = clock.UtcNow;
        var runId = MintRunId(now);
        instance.BeginScanRun(runId, now);
        await repository.UpdateAsync(instance, cancellationToken).ConfigureAwait(false);
        await NudgeReconcileAsync(instance.Id, "workload-scan-trigger", cancellationToken).ConfigureAwait(false);

        logger.LogInformation(
            "workloads.scan: instance={InstanceId} initiated scan run runId={RunId} over {NodeCount} eligible Ready node(s) — marker set, leader nudged.",
            instance.Id, runId, eligible.Count);

        if (ineligible.Count > 0)
        {
            logger.LogInformation(
                "workloads.scan: instance={InstanceId} runId={RunId} — {IneligibleCount} Ready node(s) were skipped as INELIGIBLE by the node-eligibility selector and get NO Job: {IneligibleNodes}.",
                instance.Id, runId, ineligible.Count, string.Join(", ", ineligible));
        }

        return WorkloadOperationResult.Success(instance.State);
    }

    private static async Task<bool> IsScanInFlightAsync(
        WorkloadInstance instance, IWorkloadProvider provider, CancellationToken cancellationToken)
    {
        var status = await provider.GetStatusAsync(WorkloadRef.FromInstance(instance), cancellationToken).ConfigureAwait(false);
        return status.Phase == WorkloadProviderPhase.Running;
    }

    internal static WorkloadDesiredSpec BuildDesiredSpec(WorkloadInstance instance, WorkloadReconcileInputs inputs)
    {
        var resolvedParameters = WorkloadParameterResolver.Resolve(
            inputs.WorkloadManifest, inputs.ResolvedParameters, inputs.EffectiveProvider);

        return WorkloadDesiredSpecFactory.ComputeWithParameterReferences(
            inputs.WorkloadManifest,
            inputs.Settings,
            resolvedParameters,
            UnpinnedReferenceRolloutGuard.ProjectReferencesForHash(instance, inputs.ParameterReferencesForHash),
            inputs.Bootstrap,
            inputs.EffectiveProvider,
            inputs.Namer,
            instance.GroupInstanceId,
            instance.GroupId,
            instance.Id,
            inputs.InstallationId,
            inputs.ProductImages);
    }

    private static string MintRunId(DateTimeOffset now)
        => $"scan-{now.UtcDateTime:yyyyMMddHHmmssfff}";
}
