using Reeve.Workloads.Abstractions;

namespace Reeve.Workloads.Core;

internal sealed class WorkloadReconcileNudge(IWorkloadClusterBus clusterBus, IWorkloadReconcileTrigger trigger)
    : IWorkloadReconcileNudge
{
    private readonly IWorkloadClusterBus clusterBus = clusterBus ?? throw new ArgumentNullException(nameof(clusterBus));
    private readonly IWorkloadReconcileTrigger trigger = trigger ?? throw new ArgumentNullException(nameof(trigger));

    public async Task NudgeInstanceAsync(Guid instanceId, string reason, CancellationToken cancellationToken = default)
    {
        await clusterBus.PublishAsync(
            WorkloadClusterChannels.ReconcileRequest,
            new WorkloadReconcileRequestMessage(instanceId, reason),
            cancellationToken).ConfigureAwait(false);

        await trigger.RequestInstanceAsync(instanceId, reason, cancellationToken).ConfigureAwait(false);
    }

    public async Task NudgeAllAsync(string reason, CancellationToken cancellationToken = default)
    {
        await clusterBus.PublishAsync(
            WorkloadClusterChannels.ReconcileRequest,
            new WorkloadReconcileRequestMessage(InstanceId: null, reason),
            cancellationToken).ConfigureAwait(false);

        await trigger.RequestAllAsync(reason, cancellationToken).ConfigureAwait(false);
    }
}

internal sealed class NoWorkloadReconcileTrigger : IWorkloadReconcileTrigger
{
    public static readonly NoWorkloadReconcileTrigger Instance = new();

    public Task RequestInstanceAsync(Guid instanceId, string reason, CancellationToken cancellationToken = default)
        => Task.CompletedTask;

    public Task RequestAllAsync(string reason, CancellationToken cancellationToken = default)
        => Task.CompletedTask;
}
