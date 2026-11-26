using Reeve.Workloads.Abstractions;

namespace Reeve.Workloads.Core.Update;

internal static class WorkloadUpdateNudgeReasons
{
    public const string Initiated = "workload-update-initiated";

    public const string Abandoned = "update-abandoned";

    public static bool WakesTheWalk(WorkloadReconcileRequestMessage message)
        => message.InstanceId is null
           && (message.Reason.StartsWith(Initiated + ":", StringComparison.Ordinal)
               || message.Reason.StartsWith(Abandoned + ":", StringComparison.Ordinal));
}
