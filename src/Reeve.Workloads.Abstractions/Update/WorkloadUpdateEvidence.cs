using Reeve.Workloads.Abstractions.Domain;

namespace Reeve.Workloads.Abstractions.Update;

/// <summary>Rollback diagnostics captured BEFORE teardown — the last ~200 previous-container log lines and
/// recent provider status events of the failed N+1 workloads, so rollback cannot destroy the only diagnostics. Serialized onto the intent's <c>EvidenceJson</c>.</summary>
public sealed record WorkloadUpdateEvidence(
    string ToVersion,
    DateTimeOffset CapturedAtUtc,
    IReadOnlyList<WorkloadEvidenceCapture> Workloads);

/// <summary>One workload's captured evidence (logs + events), or a <see cref="Note"/> when capture was not possible.</summary>
public sealed record WorkloadEvidenceCapture(
    string WorkloadId,
    IReadOnlyList<string> PreviousContainerLogTail,
    IReadOnlyList<string> RecentEvents,
    string? Note);
