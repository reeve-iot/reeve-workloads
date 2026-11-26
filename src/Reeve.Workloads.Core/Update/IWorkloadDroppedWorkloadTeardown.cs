using Reeve.Workloads.Abstractions.Contracts.Deployment;
using Reeve.Workloads.Abstractions.Domain.Entities;

namespace Reeve.Workloads.Core.Update;

/// <summary>Starts the teardown of a workload that the version an update completes no longer declares, the way an
/// uninstall that keeps the data does: its volumes are retained and recorded from the declaration it was deployed
/// with, its credentials are revoked, and the reconciler's teardown pass removes the runtime and the row. A row Reeve
/// does not own (disowned, mid-switch) loses its record and credentials only; its runtime is the operator's.</summary>
internal interface IWorkloadDroppedWorkloadTeardown
{
    /// <summary>Begins the teardown of <paramref name="instance"/>. <paramref name="lastDeclaration"/> is the
    /// workload as the version it runs declares it, or null when none does, for the reason
    /// <paramref name="whyUndeclared"/> gives (its volumes are then kept on the runtime with no record, and the log
    /// says why). Throws when a credential of a row Reeve does not own could not be revoked; the caller retries.</summary>
    Task BeginTeardownAsync(
        WorkloadInstance instance,
        WorkloadManifest? lastDeclaration,
        string whyUndeclared,
        string droppingVersion,
        CancellationToken cancellationToken = default);
}
