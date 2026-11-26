using FluentAssertions;
using static Reeve.Workloads.Core.Tests.Update.WorkloadUpdatePlanBuilder;
using Reeve.Workloads.Abstractions.Domain.Entities;
using Reeve.Workloads.Abstractions.Update;
using Xunit;

namespace Reeve.Workloads.Core.Tests.Update;

public sealed class WorkloadUpdateOrchestratorCertificationTests
{
    private static readonly Guid Instance = InstanceGuid;

    private static WorkloadUpdatePlanItem SessionlessCandidate() => Item("discovery-host", Instance, "sha-new", "sha-old") with
    {
        Certification = WorkloadHealthGateCertification.AppliedSpec,
        PreviousCertification = WorkloadHealthGateCertification.HostEvidence,
    };

    [Fact]
    public async Task Forward_gate_is_ticked_with_the_candidate_certification()
    {
        var plan = Plan(Instance, [SessionlessCandidate()]);
        var h = new OrchestratorHarness(plan);
        var intent = await h.StageManualAsync(plan);

        await h.Orchestrator.AdvanceAsync(intent);
        await h.Orchestrator.AdvanceAsync(intent);
        await h.Orchestrator.AdvanceAsync(intent);
        h.Gate.Requests.Clear();
        await h.Orchestrator.AdvanceAsync(intent);

        h.Gate.Requests.Should().ContainSingle().Which.Certification.Should().Be(
            WorkloadHealthGateCertification.AppliedSpec,
            "the forward gate certifies the incoming sidecar, so the CANDIDATE's health contract decides what evidence can exist");
    }

    [Fact]
    public async Task Rollback_health_re_tick_is_ticked_with_the_previous_certification()
    {
        var plan = Plan(Instance, [SessionlessCandidate()]);
        var h = new OrchestratorHarness(plan);
        var intent = await h.StageManualAsync(plan);

        await h.Orchestrator.AdvanceAsync(intent);
        await h.Orchestrator.AdvanceAsync(intent);

        h.Gate.VerdictsByWorkload["discovery-host"] = new Queue<WorkloadHealthGateVerdict>([WorkloadHealthGateVerdict.Failed]);
        h.Gate.DefaultVerdict = WorkloadHealthGateVerdict.Passed;

        var result = await h.Orchestrator.WalkAsync(intent);
        result.Phase.Should().Be(WorkloadUpdatePhase.RolledBack);

        h.Gate.Requests.Last().Certification.Should().Be(
            WorkloadHealthGateCertification.HostEvidence,
            "a rollback re-certifies module N, whose manifest — not the candidate's — says what evidence it produces");
    }

    [Fact]
    public void Plan_items_default_to_the_strictest_certification()
    {
        var item = Item("gateway", Instance, "sha-new", "sha-old");

        item.Certification.Should().Be(WorkloadHealthGateCertification.HostEvidence);
        item.PreviousCertification.Should().Be(WorkloadHealthGateCertification.HostEvidence);
    }
}
