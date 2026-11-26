using Reeve.Workloads.Abstractions.Contracts.Deployment;
using Reeve.Workloads.Abstractions.Domain;

namespace Reeve.Workloads.Abstractions;

/// <summary>Loads the durable materialization context for one module: its id and the declared deployment workloads
/// at the installed version. Kept as a seam so the lifecycle service stays unit-testable.</summary>
/// <remarks>PUBLIC for the same reason as IWorkloadReconcileInputsProvider: the library composes it, a host reads it.
/// The module row joined with the manifest source is the answer the lifecycle service acts on, and a host answering
/// it itself gets a different one.</remarks>
public interface IWorkloadDeploymentContextSource
{
    /// <summary>The deployment context for <paramref name="groupId"/>, or null when it declares no
    /// <c>deployment</c> section at the installed version.</summary>
    Task<WorkloadDeploymentContext?> GetForGroupAsync(
        string groupId,
        CancellationToken cancellationToken = default);
}

/// <summary>Durable materialization inputs for one module: its id and its declared deployment workloads.</summary>
public sealed record WorkloadDeploymentContext
{
    /// <summary>The module id (used to stamp <c>GroupId</c> + resource names).</summary>
    public required string GroupId { get; init; }

    /// <summary>The declared deployment manifest at the installed version. Its <c>Workloads[]</c> drive materialize.</summary>
    public required DeploymentManifest Deployment { get; init; }
}
