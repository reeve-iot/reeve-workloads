using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Reeve.Workloads.Abstractions.Contracts;

namespace Reeve.Workloads.Abstractions;

/// <summary>Read seam surfacing a workload instance's live volume status: each declared volume mapped to
/// its live PVC. Kubernetes-only — other providers return a non-applicable result gracefully, never a crash.</summary>
public interface IWorkloadVolumeStatusReader
{
    /// <summary>Reads live volume status for <paramref name="instanceId"/>; null means unknown instance (404).
    /// A forbidden/unreachable read degrades the affected volume to <see cref="WorkloadVolumeResizeState.Unknown"/>, never a false "stable";
    /// a claim that does not exist reads <see cref="WorkloadVolumeResizeState.Missing"/>.</summary>
    Task<WorkloadVolumeStatusResult?> ReadVolumeStatusAsync(Guid instanceId, CancellationToken cancellationToken = default);
}

/// <summary>Result of <see cref="IWorkloadVolumeStatusReader.ReadVolumeStatusAsync"/>, mapped by the
/// controller to the <see cref="WorkloadVolumeStatusResponse"/> wire shape.</summary>
public sealed record WorkloadVolumeStatusResult
{
    /// <summary>False when volume live-status is not applicable (non-Kubernetes provider / workload no longer declared).</summary>
    public required bool ProviderApplicable { get; init; }

    /// <summary>The per-declared-volume live status (empty when not applicable or the workload declares no volumes).</summary>
    public required IReadOnlyList<WorkloadVolumeStatus> Volumes { get; init; }

    /// <summary>Human-readable explanation for the non-applicable case, else null.</summary>
    public string? Message { get; init; }

    /// <summary>Builds a non-applicable result (docker/manual/undeclared): empty list + an explanatory message.</summary>
    public static WorkloadVolumeStatusResult NotApplicable(string message) => new()
    {
        ProviderApplicable = false,
        Volumes = [],
        Message = message,
    };

    /// <summary>Builds an applicable result carrying the mapped per-volume live status.</summary>
    public static WorkloadVolumeStatusResult Applicable(IReadOnlyList<WorkloadVolumeStatus> volumes) => new()
    {
        ProviderApplicable = true,
        Volumes = volumes,
    };
}
