using Reeve.Workloads.Abstractions;
using Reeve.Workloads.Abstractions.Update;

namespace Reeve.Workloads.Core.Update;

/// <summary>Asks for the same reconcile pass the lifecycle/operations path asks for, so a freshly-staged update
/// intent converges promptly. Hint-grade + best-effort, and always a full pass (<c>InstanceId: null</c>) since the plugin-instance id given is not a workload-instance id.</summary>
public sealed class WorkloadUpdateReconcileNudge : IWorkloadUpdateReconcileNudge
{
    private readonly IWorkloadReconcileNudge _nudge;
    private readonly IWorkloadUpdateWalkTrigger _walk;

    /// <summary>The constructor taking the bus alone, which reaches peer replicas and no reconciler in this
    /// process. Kept for source compatibility; the container picks the greediest one below.</summary>
    public WorkloadUpdateReconcileNudge(IWorkloadClusterBus clusterBus)
        : this(clusterBus, NoWorkloadReconcileTrigger.Instance)
    {
    }

    /// <summary>Takes the local trigger as well, because a bus skips the publishing replica's own messages — a
    /// single-replica host that only published waited for the drift pass.</summary>
    public WorkloadUpdateReconcileNudge(IWorkloadClusterBus clusterBus, IWorkloadReconcileTrigger trigger)
        : this(clusterBus, trigger, NoWorkloadUpdateWalkTrigger.Instance)
    {
    }

    /// <summary>Wakes the update walk in this process as well. The walk runs on its own loop, not
    /// in the reconcile pass the other two halves ask for; without this an update initiated on the leader would
    /// start when the walk's idle fallback came round. A peer's walk hears the bus half.</summary>
    public WorkloadUpdateReconcileNudge(
        IWorkloadClusterBus clusterBus,
        IWorkloadReconcileTrigger trigger,
        IWorkloadUpdateWalkTrigger walk)
    {
        _nudge = new WorkloadReconcileNudge(clusterBus, trigger);
        _walk = walk ?? throw new ArgumentNullException(nameof(walk));
    }

    /// <inheritdoc />
    public Task NudgeAsync(Guid pluginInstanceId, string reason, CancellationToken cancellationToken = default)
    {
        _walk.Wake();

        return _nudge.NudgeAllAsync($"{reason}:{pluginInstanceId:D}", cancellationToken);
    }
}

internal sealed class NoWorkloadUpdateWalkTrigger : IWorkloadUpdateWalkTrigger
{
    public static readonly NoWorkloadUpdateWalkTrigger Instance = new();

    public void Wake()
    {
    }
}
