using Reeve.Workloads.Abstractions.Domain.Entities;

namespace Reeve.Workloads.Abstractions;

/// <summary>Assembles the durable <see cref="WorkloadReconcileInputs"/> the reconcile engine needs for one
/// <see cref="WorkloadInstance"/>; a seam that keeps the engine pure/testable and isolates the I/O in one Infrastructure default.</summary>
/// <remarks>PUBLIC. A host does not SUPPLY this — the library composes it
/// from IWorkloadGroupManifestSource and IWorkloadModuleStore — but reading it is the only way to get the exact
/// inputs the reconciler used. A host projecting sidecar-facing config re-derives a drifting parallel answer
/// otherwise, and hands the sidecar a configuration the reconciler never applied.</remarks>
public interface IWorkloadReconcileInputsProvider
{
    /// <summary>The reconcile inputs for <paramref name="instance"/>; <see cref="WorkloadReconcileInputsResult.NotDeclared"/>
    /// when the installed manifest no longer declares it, and <see cref="WorkloadReconcileInputsResult.HostRefused"/>
    /// when the host refused its bootstrap. The engine no-ops on either and logs a refusal. Every mutating reader
    /// refuses by name; the two reads with no result type (the resource-limits read and the relocate plan) answer
    /// null and log the reason.</summary>
    Task<WorkloadReconcileInputsResult> GetInputsAsync(WorkloadInstance instance, CancellationToken cancellationToken = default);
}
