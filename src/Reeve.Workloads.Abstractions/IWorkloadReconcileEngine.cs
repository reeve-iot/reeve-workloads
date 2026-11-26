using Reeve.Workloads.Abstractions.Domain.Entities;

namespace Reeve.Workloads.Abstractions;

/// <summary>Converges ONE <see cref="WorkloadInstance"/> toward its desired spec, driving the state machine
/// from provider status + health and persisting the result. The seam the leader-only scheduler depends on.</summary>
public interface IWorkloadReconcileEngine
{
    /// <summary>Reconciles one instance and persists the outcome. A provider-unreachable fault becomes a
    /// <see cref="WorkloadReconcileAction.MarkedUnknown"/> outcome, never a throw, so the pass keeps going.</summary>
    Task<WorkloadReconcileOutcome> ReconcileInstanceAsync(
        WorkloadInstance instance, CancellationToken cancellationToken = default);
}
