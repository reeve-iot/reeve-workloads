using Reeve.Workloads.Abstractions.Domain.Entities;
using Reeve.Workloads.Abstractions.Domain.Network;

namespace Reeve.Workloads.Abstractions;

/// <summary>The result of one per-instance reconcile pass. Never carries an exception — a
/// provider-unreachable fault is reflected as a <see cref="WorkloadReconcileAction.MarkedUnknown"/> action, not a throw.</summary>
public sealed record WorkloadReconcileOutcome
{
    public required Guid InstanceId { get; init; }

    /// <summary>What the pass did (drove an apply, drove a state change, no-op'd, deferred, …).</summary>
    public required WorkloadReconcileAction Action { get; init; }

    /// <summary>The machine-readable reason for the action (drives logging / diagnostics).</summary>
    public required WorkloadReconcileReason Reason { get; init; }

    /// <summary>The instance state AFTER the pass (unchanged from entry when the pass was a no-op).</summary>
    public required WorkloadInstanceState ResultingState { get; init; }

    /// <summary>True when the desired spec differs from the last-applied and could not be applied (paused/observe-only).</summary>
    public bool PendingApply { get; init; }

    /// <summary>Human-readable free-text companion (never null-checked by callers; logging only).</summary>
    public string? Message { get; init; }

    /// <summary>Ask the scheduler to come back after this long. Null means "no opinion" — the scheduler's own
    /// rules (settling, dirty, backoff) decide. Lets the engine say so instead of the scheduler inferring it.</summary>
    public TimeSpan? RequeueAfter { get; init; }

    public static WorkloadReconcileOutcome NoOp(
        Guid instanceId, WorkloadReconcileReason reason, WorkloadInstanceState state, string? message = null) => new()
    {
        InstanceId = instanceId,
        Action = WorkloadReconcileAction.NoOp,
        Reason = reason,
        ResultingState = state,
        Message = message,
    };
}

/// <summary>What a reconcile pass did.</summary>
public enum WorkloadReconcileAction
{
    /// <summary>Nothing changed (no drift and no state change, or the pass was gated off).</summary>
    NoOp = 0,

    /// <summary>The desired spec was applied to the provider (drift correction).</summary>
    Applied = 1,

    /// <summary>The lifecycle state was transitioned (health/status re-evaluation) — with or without an apply.</summary>
    Transitioned = 2,

    /// <summary>The provider was unreachable — the instance was marked <see cref="WorkloadInstanceState.Unknown"/>.</summary>
    MarkedUnknown = 3,

    /// <summary>Drift exists but mutation is gated (reconciliation paused / observe-only) — recorded as pending.</summary>
    DeferredMutation = 4,
}

/// <summary>The machine-readable reason for a reconcile action.</summary>
public enum WorkloadReconcileReason
{
    /// <summary>The workload is no longer declared (uninstalled / manifest lacks the workloadId) — no-op.</summary>
    WorkloadNotDeclared = 0,

    /// <summary>No provider is registered for the effective/bound provider  — no-op.</summary>
    NoProviderAvailable = 1,

    /// <summary>The desired spec was applied (drift corrected).</summary>
    Provisioned = 2,

    /// <summary>Mutation was deferred because reconciliation is globally paused (kill-switch).</summary>
    ReconciliationPaused = 3,

    /// <summary>Mutation was deferred because the instance is observe-only.</summary>
    ObserveOnly = 4,

    /// <summary>The provider was unreachable — the instance is <see cref="WorkloadInstanceState.Unknown"/>.</summary>
    ProviderUnreachable = 5,

    /// <summary>The instance is <see cref="WorkloadInstanceState.Unknown"/> and cannot mutate — status only.</summary>
    UnknownNoMutation = 6,

    /// <summary>Health/status was re-evaluated and the state converged (or stayed put).</summary>
    HealthEvaluated = 7,

    /// <summary>A required parameter is unresolved, or spec compute failed on a fixable config problem — the
    /// instance holds PendingConfiguration until the operator fixes it.</summary>
    ConfigurationRequired = 12,

    /// <summary>Spec compute threw an unexpected (non-config) fault — marked Failed rather than crashing the
    /// pass. Re-evaluated every resync.</summary>
    DesiredSpecComputeFailed = 13,

    /// <summary>Deferred because the instance is operator-paused — distinct from the global
    /// <see cref="ReconciliationPaused"/> kill-switch; the health pass must never auto-resume this.</summary>
    OperatorPaused = 14,

    /// <summary>Apply refused: pinned mode or names differ, the pin is unreadable, an attachment is missing, or a non-Manual
    /// provider reported the network not applicable. A re-pin releases only the first two; a missing one clears itself.</summary>
    AttachmentConfigDrifted = 15,

    /// <summary>The apply was refused because checking whether the attachments exist faulted (transport error,
    /// 5xx, RBAC). RETRYABLE, and kept distinct from <see cref="AttachmentConfigDrifted"/> so a flaky call is never
    /// mistaken for a missing attachment.</summary>
    AttachmentExistenceUnverifiable = 16,

    /// <summary>Deferred because the host suspended the instance. Only the host's resume lifts it.</summary>
    SuspendedByHost = 17,

    /// <summary>The host refused the workload's bootstrap, so there is no spec to apply. Nothing is changed or
    /// saved; the next pass asks the host again. <see cref="WorkloadReconcileOutcome.Message"/> carries the
    /// reason.</summary>
    HostRefusedBootstrap = 18,

    /// <summary>A password reference could not be resolved because the host's secret store was unavailable. The
    /// instance keeps its state and nothing is applied or saved; the next pass resolves again.
    /// <see cref="WorkloadReconcileOutcome.Message"/> carries the reason.</summary>
    ParameterReferenceUnavailable = 19,
}
