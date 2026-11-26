using Reeve.Workloads.Abstractions.Providers;

namespace Reeve.Workloads.Providers.Manual;

internal static class ManualStatusMapper
{
    public static WorkloadStatus Map(bool live)
    {
        if (!live)
        {
            return new WorkloadStatus
            {
                Phase = WorkloadProviderPhase.Pending,
                ProviderReady = false,
                Message =
                    "Manual mode: nothing is live for this workload yet — awaiting the operator to deploy the " +
                    "rendered artifacts.",
            };
        }

        return new WorkloadStatus
        {
            Phase = WorkloadProviderPhase.Ready,
            ProviderReady = true,
            Message =
                "Manual mode: the operator-deployed workload is live — it is externally managed (Reeve observes " +
                "health, it does not manage the runtime).",
        };
    }
}
