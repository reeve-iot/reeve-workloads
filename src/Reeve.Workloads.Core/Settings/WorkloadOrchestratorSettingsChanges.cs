using Reeve.Workloads.Abstractions;
using Reeve.Workloads.Core.Watch;

namespace Reeve.Workloads.Core;

internal sealed class WorkloadOrchestratorSettingsChanges(
    WorkloadOrchestratorSettingsCache cache,
    IWorkloadReconcileNudge nudge,
    WorkloadWatchUnit watchUnit) : IWorkloadOrchestratorSettingsChanges
{
    public async Task ChangedAsync(CancellationToken cancellationToken = default)
    {
        cache.Invalidate();
        try
        {
            await watchUnit.RestartIfProviderMovedAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            await nudge.NudgeAllAsync(WorkloadReconcileReasons.SettingsChanged, cancellationToken).ConfigureAwait(false);
        }
    }
}
