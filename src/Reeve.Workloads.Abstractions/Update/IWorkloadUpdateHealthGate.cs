using System.Text.Json;
using Reeve.Workloads.Abstractions.Contracts.Deployment;
using Reeve.Workloads.Abstractions.Domain;
using Reeve.Workloads.Abstractions.Domain.Entities;
using Reeve.Workloads.Abstractions.Providers;

namespace Reeve.Workloads.Abstractions.Update;

/// <summary>Application-level seam so the orchestrator (which must not reference Infrastructure) never sees the
/// Infrastructure HealthGate types. The clock state round-trips as opaque JSON so it survives a leader failover mid-gate.</summary>
public interface IWorkloadUpdateHealthGate
{
    /// <summary>Runs one gate tick for a workload and returns the verdict + the opaque next clock state to persist.</summary>
    Task<WorkloadHealthGateTickResult> EvaluateTickAsync(WorkloadHealthGateTickRequest request, CancellationToken cancellationToken = default);
}

/// <summary>One workload's gate-tick inputs, projected by the orchestrator from the intent + plan item.
/// Carries only Application-visible types; the Infrastructure adapter turns it into a <c>HealthGateConfig</c>.</summary>
public sealed record WorkloadHealthGateTickRequest
{
    /// <summary>The workload's runtime ref (host evidence and provider status are gathered for its instance).</summary>
    public required WorkloadRef Reference { get; init; }

    /// <summary>The workload-instance id the certifier is asked about.</summary>
    public required Guid InstanceId { get; init; }

    /// <summary>The applied N+1 spec hash the host's evidence must match — evidence from the NEW rollout.</summary>
    public required string ExpectedSpecHash { get; init; }

    /// <summary>The spec hash of the workload the update replaces, on the forward path; null on an unwind, for an
    /// added workload, and when it equals <see cref="ExpectedSpecHash"/>. A session reporting this hash is the
    /// outgoing workload still answering, so the gate keeps waiting for the new one instead of failing.</summary>
    public string? OutgoingSpecHash { get; init; }

    /// <summary>The instant the sidecar spec was applied — only a candidate observed strictly after this counts.</summary>
    public required DateTimeOffset ApplyTimestampUtc { get; init; }

    /// <summary>The candidate version the workload must report; null ⇒ not checked.</summary>
    public string? ToVersion { get; init; }

    /// <summary>The extensions of the side that runs now, handed to the certifier. Never null.</summary>
    public required WorkloadExtensions CurrentExtensions { get; init; }

    /// <summary>The extensions of the side being certified — the candidate's on the forward path, the current
    /// side's on rollback.</summary>
    public required WorkloadExtensions CandidateExtensions { get; init; }

    /// <summary>The (pause-excluded) gate timeout budget — default 5 min, per-plugin overridable.</summary>
    public required TimeSpan Timeout { get; init; }

    /// <summary>Evidence this workload can be certified by — the CANDIDATE's manifest side on the forward path,
    /// module N's on rollback. Required: no default may select host evidence.</summary>
    public required WorkloadHealthGateCertification Certification { get; init; }

    /// <summary>The prior persisted clock state, or the pristine initial state on the first tick.</summary>
    public WorkloadGateClockState PriorClockState { get; init; } = new();

}

/// <summary>The outcome of one <see cref="IWorkloadUpdateHealthGate.EvaluateTickAsync"/> tick.</summary>
public sealed record WorkloadHealthGateTickResult
{
    /// <summary>The gate verdict this tick.</summary>
    public required WorkloadHealthGateVerdict Verdict { get; init; }

    /// <summary>A stable machine-readable failure reason token when <see cref="Verdict"/> is Failed; otherwise null (diagnostics).</summary>
    public string? FailureReason { get; init; }

    /// <summary>Human-readable detail for diagnostics / Repairs text.</summary>
    public string? Detail { get; init; }

    /// <summary>The clock state to persist and feed into the next tick. An unset one is the pristine initial state.</summary>
    public WorkloadGateClockState NextClockState { get; init; } = new();

}

/// <summary>What a workload can be certified BY, derived from the manifest's <c>health.mode</c> + <c>kind</c>.
/// A mode whose evidence no certifier requires, or a <c>scan-per-node</c> workload, may never produce any — gating it
/// on evidence anyway parks the update in HealthGate PERMANENTLY.</summary>
public enum WorkloadHealthGateCertification
{
    /// <summary>A health mode the registered certifier requires — the full conjunction, including a post-apply candidate.</summary>
    HostEvidence = 0,

    /// <summary>Any other mode — provider-ready certifies; a candidate that answers is still checked, never required.</summary>
    ProviderReady = 1,

    /// <summary><c>kind: scan-per-node</c> — nothing is observable between runs; a successful apply certifies.</summary>
    AppliedSpec = 2,
}

/// <summary>The three gate verdicts (mirrors the Infrastructure <c>HealthGateVerdict</c>).</summary>
public enum WorkloadHealthGateVerdict
{
    /// <summary>Neither passed nor failed yet — keep ticking (the phase stays HealthGate).</summary>
    Pending = 0,

    /// <summary>The full conjunction is satisfied against the NEW rollout — the gate's stage is done.</summary>
    Passed = 1,

    /// <summary>The gate failed — the orchestrator transitions to RollingBack.</summary>
    Failed = 2,
}
