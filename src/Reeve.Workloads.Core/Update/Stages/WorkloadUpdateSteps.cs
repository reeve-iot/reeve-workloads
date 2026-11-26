using Reeve.Workloads.Abstractions.Domain.Entities;
using Reeve.Workloads.Abstractions.Update;

namespace Reeve.Workloads.Core.Update.Stages;

internal static class WorkloadUpdateSteps
{
    public static WorkloadUpdateIntentStep? Find(WorkloadUpdateIntent intent, string workloadId)
        => intent.Steps.FirstOrDefault(step => string.Equals(step.WorkloadId, workloadId, StringComparison.Ordinal));

    public static WorkloadUpdateIntentStep Ensure(WorkloadUpdateIntent intent, string workloadId, DateTimeOffset now)
    {
        var existing = Find(intent, workloadId);
        if (existing is not null)
        {
            return existing;
        }

        var created = WorkloadUpdateIntentStep.Create(Guid.NewGuid(), intent.Id, workloadId, now);
        intent.AddStep(created);
        return created;
    }

    public static string ExpectedHashFor(WorkloadUpdateIntentStep step, WorkloadUpdatePlanItem item)
        => !string.IsNullOrEmpty(step.AppliedSpecHash) ? step.AppliedSpecHash! : item.TargetSpec.SpecHash;
}
