using Reeve.Workloads.Abstractions.Domain.Entities;
using Reeve.Workloads.Abstractions.Update;
using Reeve.Pipeline;

namespace Reeve.Workloads.Core.Update.Stages;

internal static class WorkloadUpdateStageContexts
{
    public static WorkloadUpdateStageContext For(
        WorkloadUpdateIntent intent,
        WorkloadUpdatePlan plan,
        WorkloadUpdateCohortReading? cohortReading = null,
        WorkloadGroup? trackedGroup = null,
        DateTimeOffset nowUtc = default,
        Pipeline<WorkloadUpdateStageContext>? pipeline = null)
    {
        ArgumentNullException.ThrowIfNull(intent);
        ArgumentNullException.ThrowIfNull(plan);

        return new WorkloadUpdateStageContext
        {
            IntentId = intent.Id,
            GroupInstanceId = plan.GroupInstanceId,
            GroupId = plan.GroupId,
            SourceKey = plan.SourceKey,
            FromVersion = plan.FromVersion,
            ToVersion = plan.ToVersion,
            Cohort = intent.CohortId is { } cohortId && intent.CohortOrdinal is { } ordinal && intent.CohortSize is { } size
                ? new WorkloadUpdateCohortPlace(cohortId, ordinal, size)
                : null,
            Workloads = [.. plan.Workloads.Select(item => new WorkloadUpdateStageWorkload
            {
                WorkloadId = item.WorkloadId,
                PreviousSpec = item.PreviousSpec,
                TargetSpec = item.TargetSpec,
            })],
            Internals = new WorkloadUpdateStageInternals
            {
                Intent = intent,
                Plan = plan,
                CohortReading = cohortReading,
                TrackedGroup = trackedGroup,
                NowUtc = nowUtc,
                Pipeline = pipeline,
            },
        };
    }
}
