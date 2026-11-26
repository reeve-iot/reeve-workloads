using FluentAssertions;
using static Reeve.Workloads.Core.Tests.Update.WorkloadUpdatePlanBuilder;
using Reeve.Workloads.Abstractions;
using Reeve.Workloads.Abstractions.Contracts;
using Reeve.Workloads.Abstractions.Domain.Entities;
using Reeve.Workloads.Abstractions.Update;
using Xunit;
using Reeve.Workloads.Providers.Manual;

namespace Reeve.Workloads.Core.Tests.Update;

public sealed class WorkloadUpdateOrchestratorManualSwapParkTests
{
    private static readonly Guid Instance = InstanceGuid;

    private static async Task<(OrchestratorHarness H, WorkloadUpdateIntent Intent)> StagedManualAsync()
    {
        var plan = Plan(Instance, [Item("gateway", Instance, "sha-new", "sha-old", ManualProvider.Id)]);
        var h = new OrchestratorHarness(plan, providerKind: ManualProvider.Id);
        var intent = await h.StageManualAsync(plan);
        return (h, intent);
    }

    [Fact]
    public async Task Manual_update_parks_with_a_skew_warning_when_the_sidecar_has_not_caught_up()
    {
        var (h, intent) = await StagedManualAsync();
        h.ObservedVersions.VersionsByInstance[Instance] = "2.5.0";

        await h.Orchestrator.WalkAsync(intent);
        var r = await h.Orchestrator.WalkAsync(intent);

        intent.CurrentStage.Should().Be(WorkloadUpdateStages.OperatorRedeploy);
        r.Phase.Should().Be(WorkloadUpdatePhase.ApplyingSidecar, "the operator applying the sidecar is part of the apply");
        r.Progressed.Should().BeFalse();
        h.Effects.CommitInstalledVersionCount.Should().Be(0, "the module version must NOT commit while the manual sidecar is behind");
        h.HostStage.Calls.Should().BeEmpty("the host's reload is not reached while the manual sidecar is behind");
        h.Rows.EnsureCount.Should().Be(0, "the rows go with the version commit, which waits for the operator's redeploy");
        h.Issues.Raised.Where(i => i.Code.Contains("version_skew")).Should().NotBeEmpty()
            .And.OnlyContain(i => i.Severity == WorkloadDiagnosticSeverity.Warning, "raised again on every parked tick");
    }

    [Fact]
    public async Task The_gate_is_not_asked_about_a_manual_sidecar_the_operator_has_not_redeployed()
    {
        var (h, intent) = await StagedManualAsync();
        h.ObservedVersions.VersionsByInstance[Instance] = "2.5.0";
        h.Gate.DefaultVerdict = WorkloadHealthGateVerdict.Failed;

        for (var walks = 0; walks < 5; walks++)
        {
            await h.Orchestrator.WalkAsync(intent);
        }

        h.Gate.Requests.Should().BeEmpty(
            "only the old sidecar runs, which no gate can certify as the new one: gating it would wait out the gate's "
            + "timeout and roll back before the operator is asked to redeploy");
        intent.Outcome.Should().Be(WorkloadUpdateOutcome.InProgress);
        h.Issues.Raised.Should().Contain(i => i.Code.Contains("version_skew"));
    }

    [Fact]
    public async Task A_mixed_group_parks_on_its_connected_manual_workload_before_the_gate()
    {
        var bridge = Guid.Parse("22222222-2222-2222-2222-222222222222");
        var plan = Plan(Instance,
        [
            Item("gateway", Instance, "sha-new", "sha-old", Reeve.Workloads.Providers.Docker.DockerProvider.Id),
            Item("bridge", bridge, "sha-b-new", "sha-b-old", ManualProvider.Id),
        ]);
        var h = new OrchestratorHarness(plan, providerKind: Reeve.Workloads.Providers.Docker.DockerProvider.Id);
        var intent = await h.StageManualAsync(plan);
        h.ObservedVersions.VersionsByInstance[bridge] = "2.5.0";

        for (var walks = 0; walks < 5; walks++)
        {
            await h.Orchestrator.WalkAsync(intent);
        }

        intent.CurrentStage.Should().Be(WorkloadUpdateStages.OperatorRedeploy);
        h.Gate.Requests.Should().BeEmpty("the manual workload still runs its old sidecar, which the gate cannot certify");
        h.Issues.Raised.Should().Contain(i => i.Code == "workload.bridge.version_skew");
        h.Issues.Raised.Should().NotContain(i => i.Code == "workload.gateway.version_skew");

        h.ObservedVersions.VersionsByInstance[bridge] = plan.ToVersion;
        var r = await h.Orchestrator.WalkAsync(intent);

        r.Phase.Should().Be(WorkloadUpdatePhase.Completed);
        h.Gate.Requests.Select(request => request.Reference.WorkloadId).Should().BeEquivalentTo(["gateway", "bridge"]);
    }

    [Fact]
    public async Task Manual_update_parks_when_there_is_no_live_sidecar_session_at_all()
    {
        var (h, intent) = await StagedManualAsync();

        var r = await h.Orchestrator.WalkAsync(intent);

        intent.CurrentStage.Should().Be(WorkloadUpdateStages.OperatorRedeploy);
        r.Phase.Should().Be(WorkloadUpdatePhase.ApplyingSidecar);
        h.Effects.CommitInstalledVersionCount.Should().Be(0);
        h.Issues.Raised.Should().Contain(i => i.Code.Contains("version_skew"));
    }

    [Fact]
    public async Task Once_the_sidecar_reports_the_target_the_gate_certifies_it_and_the_update_completes()
    {
        var (h, intent) = await StagedManualAsync();
        h.ObservedVersions.VersionsByInstance[Instance] = "2.5.0";
        await h.Orchestrator.WalkAsync(intent);
        h.Gate.Requests.Should().BeEmpty();

        h.ObservedVersions.VersionsByInstance[Instance] = "2.6.0";
        var r = await h.Orchestrator.WalkAsync(intent);

        r.Phase.Should().Be(WorkloadUpdatePhase.Completed, "once the sidecar catches up the manual update completes");
        h.Gate.Requests.Should().ContainSingle("the redeployed sidecar is the one the gate certifies")
            .Which.ExpectedSpecHash.Should().Be("sha-new");
        h.Effects.CommitInstalledVersionCount.Should().Be(1, "the module version commits once the manual sidecar is at target");
        h.Rows.EnsureCount.Should().Be(1, "and the rows land with it");
        h.Issues.Cleared.Should().Contain(c => c.Contains("version_skew"), "the skew warning clears once the update proceeds");
    }

    [Fact]
    public async Task A_managed_provider_swap_is_never_gated_on_the_observed_sidecar_version()
    {
        var plan = Plan(Instance, [Item("gateway", Instance, "sha-new", "sha-old")]);
        var h = new OrchestratorHarness(plan);
        var intent = await h.StageManualAsync(plan);
        await h.Orchestrator.AdvanceAsync(intent);
        await h.Orchestrator.AdvanceAsync(intent);
        await h.Orchestrator.AdvanceAsync(intent);
        h.ObservedVersions.VersionsByInstance[Instance] = "2.5.0";

        var r = await h.Orchestrator.WalkAsync(intent);

        r.Phase.Should().Be(WorkloadUpdatePhase.Completed, "the managed swap path is not gated on the observed sidecar version");
        h.Effects.CommitInstalledVersionCount.Should().Be(1);
    }
}
