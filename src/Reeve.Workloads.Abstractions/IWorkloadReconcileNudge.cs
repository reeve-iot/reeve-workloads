namespace Reeve.Workloads.Abstractions;

/// <summary>Asks for a reconcile pass after a write: hints the peer replicas over
/// <see cref="IWorkloadClusterBus"/> AND delivers to THIS replica's reconciler.</summary>
/// <remarks>Publishing alone cannot do it. <see cref="IWorkloadClusterBus.Subscribe{T}"/>'s contract is that a
/// bus skips the publishing replica's own messages, so a write that only publishes is never seen by a
/// reconciler in its own process — and on the shipped single-replica default that is the only reconciler there
/// is. Every such write waited for the drift pass instead, up to five minutes.
/// <para>Still hint-grade on both halves: the bus swallows a broker fault, and the local half is a no-op off
/// the leader. The periodic drift pass remains the correctness backstop.</para></remarks>
public interface IWorkloadReconcileNudge
{
    /// <summary>Asks for a reconcile of one instance.</summary>
    Task NudgeInstanceAsync(Guid instanceId, string reason, CancellationToken cancellationToken = default);

    /// <summary>Asks for a full pass over every instance — for a write whose subject is not one instance id.</summary>
    Task NudgeAllAsync(string reason, CancellationToken cancellationToken = default);
}
