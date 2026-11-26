using Reeve.Workloads.Abstractions.Domain.Network;

namespace Reeve.Workloads.Abstractions;

/// <summary>What the host judged before a write to a running module: the requirements fingerprint it compared with
/// what its operator accepted, and the network configuration it accepted for a workload.</summary>
/// <remarks>Optimistic concurrency on facts, not a consent gate. The library recomputes the fingerprint when it
/// writes; when it differs, nothing is written and the host decides again.</remarks>
public sealed record WorkloadWritePrecondition
{
    /// <summary>The fingerprint <see cref="IWorkloadRequirementsService"/> reported for the same write.</summary>
    public required string RequirementsFingerprint { get; init; }

    /// <summary>The network configuration accepted for a workload, by workload id, recorded in the same write. A
    /// parameter edit pins only its own workload, and an update only a workload that already has a row.</summary>
    public IReadOnlyDictionary<string, WorkloadAttachmentPin> AttachmentPins { get; init; } =
        new Dictionary<string, WorkloadAttachmentPin>(StringComparer.Ordinal);
}
