namespace Reeve.Workloads.Abstractions.Update;

/// <summary>Nudges the leader's update walk and workload reconciler promptly after an update intent is staged or
/// abandoned. Defined in Application so the initiator does not depend on the cluster bus directly.</summary>
public interface IWorkloadUpdateReconcileNudge
{
    /// <summary>Requests a reconcile for <paramref name="pluginInstanceId"/> (best-effort; the walk's idle fallback is the backstop).</summary>
    Task NudgeAsync(Guid pluginInstanceId, string reason, CancellationToken cancellationToken = default);
}
