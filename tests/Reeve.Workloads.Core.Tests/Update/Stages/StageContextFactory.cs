using Reeve.Workloads.Abstractions.Domain.Entities;
using Reeve.Workloads.Abstractions.Update;
using Reeve.Workloads.Core.Update.Stages;

namespace Reeve.Workloads.Core.Tests.Update.Stages;

internal static class StageContextFactory
{
    public static async Task<WorkloadUpdateStageContext> BuildAsync(
        WorkloadUpdatePlan? plan = null,
        Action<WorkloadUpdateIntent>? arrange = null)
    {
        plan ??= DefaultPlan();
        var harness = new OrchestratorHarness(plan);
        var intent = await harness.StageManualAsync(plan);
        arrange?.Invoke(intent);

        return WorkloadUpdateStageContexts.For(intent, plan);
    }

    public static Task<WorkloadUpdateStageContext> BuildCompensatingAsync(WorkloadUpdatePlan? plan = null)
        => BuildAsync(plan, intent => intent.Fail("the candidate failed its gate", intent.CreatedAtUtc));

    public static WorkloadUpdatePlan PlanWithDistinctSides()
    {
        var plan = DefaultPlan();
        var first = plan.Workloads[0] with
        {
            CurrentExtensions = ReeveTestExtensions.Pair(apiSchema: "schema-previous"),
            CandidateExtensions = ReeveTestExtensions.Pair(apiSchema: "schema-candidate"),
            Certification = WorkloadHealthGateCertification.ProviderReady,
            PreviousCertification = WorkloadHealthGateCertification.HostEvidence,
        };
        return plan with { Workloads = [first, .. plan.Workloads.Skip(1)] };
    }

    public static WorkloadUpdatePlan DefaultPlan() =>
        WorkloadUpdatePlanBuilder.Plan(
            WorkloadUpdatePlanBuilder.InstanceGuid,
            [
                WorkloadUpdatePlanBuilder.Item("alpha", WorkloadUpdatePlanBuilder.InstanceGuid, "target-alpha", "previous-alpha"),
                WorkloadUpdatePlanBuilder.Item("beta", WorkloadUpdatePlanBuilder.InstanceGuid, "target-beta", previousHash: null),
            ]);
}
