using Reeve.Workloads.Abstractions.Domain.Entities.Documents;
using Reeve.Workloads.Abstractions.Domain;
using System.Reflection;
using FluentAssertions;
using static Reeve.Workloads.Core.Tests.Update.WorkloadUpdatePlanBuilder;
using Reeve.Workloads.Abstractions;
using Reeve.Workloads.Abstractions.Contracts;
using Reeve.Workloads.Abstractions.Domain.Entities;
using Reeve.Workloads.Abstractions.Providers;
using Reeve.Workloads.Abstractions.Update;
using Xunit;

namespace Reeve.Workloads.Core.Tests.Update;

public sealed class WorkloadUpdateOrchestratorHappyPathTests
{
    private static readonly Guid Instance = InstanceGuid;

    [Fact]
    public async Task Every_tick_rebuilds_the_plan_with_the_parameters_the_intent_carries()
    {
        var plan = Plan(Instance, new[] { Item("gateway", Instance, "sha-gw-new", "sha-gw-old") });
        var h = new OrchestratorHarness(plan);
        var submitted = new Dictionary<string, string?> { ["serialPort"] = "/dev/ttyACM1" };

        var intent = await h.StageManualAsync(plan, submitted);
        h.Planner.Forget();

        await h.Orchestrator.AdvanceAsync(intent);

        h.Planner.LastSubmittedParameters.Should().BeEquivalentTo(submitted,
            "a tick that rebuilds without them renders the stored values and silently drops the edit");
    }

    [Fact]
    public async Task Every_tick_rebuilds_the_plan_with_the_parameters_the_document_holds()
    {
        var plan = Plan(Instance, [Item("gateway", Instance, "sha-gw-new", "sha-gw-old")]);
        var h = new OrchestratorHarness(plan);
        var intent = await h.StageManualAsync(
            plan, new Dictionary<string, string?> { ["serialPort"] = "/dev/ttyACM1" });
        Set(intent, nameof(WorkloadUpdateIntent.OperatorParametersDocument), new WorkloadStringMapDocument
        {
            Entries = [new WorkloadStringPairDocument { Key = "serialPort", Value = "/dev/FROM-DOCUMENT" }],
        });

        await h.Orchestrator.AdvanceAsync(intent);

        h.Planner.LastSubmittedParameters.Should().NotBeNull()
            .And.Contain("serialPort", "/dev/FROM-DOCUMENT");
    }

    private static void Set(object entity, string property, object? value)
        => entity.GetType().GetProperty(property, BindingFlags.Instance | BindingFlags.Public)!.SetValue(entity, value);

    [Fact]
    public async Task Every_re_plan_replays_the_operators_adopt_decision()
    {
        var plan = Plan(Instance, [Item("gateway", Instance, "sha-gw-new", "sha-gw-old")]);
        var h = new OrchestratorHarness(plan);

        var intent = await h.StageManualAsync(plan, adoptDeclaredResourceWorkloadIds: ["gateway"]);
        intent.GetAdoptedResourceWorkloadIds().Should().BeEquivalentTo(["gateway"]);

        await h.Orchestrator.AdvanceAsync(intent);

        h.Planner.LastAdoptedResourceWorkloadIds.Should().BeEquivalentTo(["gateway"],
            "the re-plan must render the adopted target, not the one the operator gave up");
    }

    [Fact]
    public async Task Full_happy_path_walks_every_phase_in_order()
    {
        var items = new[]
        {
            Item("gateway", Instance, "sha-gw-new", "sha-gw-old"),
            Item("metrics", Instance, "sha-mx-new", "sha-mx-old"),
        };
        var plan = Plan(Instance, items);
        var h = new OrchestratorHarness(plan);

        var intent = await h.StageManualAsync(plan);
        intent.Phase.Should().Be(WorkloadUpdatePhase.StagingParams);
        intent.Steps.Should().HaveCount(2);
        intent.TargetSpecsDocument.Should().NotBeNull("snapshots are staged at StagingParams");
        intent.PreviousSpecsDocument.Should().NotBeNull();

        var r1 = await h.Orchestrator.AdvanceAsync(intent);
        r1.Phase.Should().Be(WorkloadUpdatePhase.ApplyingSidecar);

        var r2 = await h.Orchestrator.AdvanceAsync(intent);
        r2.Phase.Should().Be(WorkloadUpdatePhase.ApplyingSidecar, "the operator-redeploy stage comes next");
        h.Provider.AppliedInOrder.Should().Equal("gateway", "metrics");
        intent.SidecarAppliedAtUtc.Should().NotBeNull();

        var redeploy = await h.Orchestrator.AdvanceAsync(intent);
        redeploy.Phase.Should().Be(WorkloadUpdatePhase.HealthGate, "a managed plan does not wait for a redeploy");

        h.Gate.DefaultVerdict = WorkloadHealthGateVerdict.Pending;
        var r3 = await h.Orchestrator.AdvanceAsync(intent);
        r3.Phase.Should().Be(WorkloadUpdatePhase.HealthGate);
        r3.Progressed.Should().BeFalse();

        h.Gate.DefaultVerdict = WorkloadHealthGateVerdict.Passed;
        var r4 = await h.Orchestrator.AdvanceAsync(intent);
        r4.Phase.Should().Be(WorkloadUpdatePhase.SwappingModule);

        h.HostStage.Enqueue(Reeve.Pipeline.StageOutcome.Working("waiting on replica-b"));
        var r5 = await h.Orchestrator.WalkAsync(intent);
        r5.Phase.Should().Be(WorkloadUpdatePhase.SwappingModule);
        intent.ReadStageJournal().Value!.Entries[^1].StageId.Should().Be(ScriptedHostStage.StageId);

        var r6 = await h.Orchestrator.WalkAsync(intent);
        r6.Phase.Should().Be(WorkloadUpdatePhase.Completed);
        intent.CompletedAtUtc.Should().NotBeNull();

        var commitIdx = h.Log.IndexOf("commit-installed-version:2.6.0");
        var hostStageIdx = h.Log.IndexOf("host-stage:2.6.0");
        commitIdx.Should().BeGreaterThanOrEqualTo(0);
        hostStageIdx.Should().BeGreaterThan(commitIdx, "InstalledVersion MUST be committed BEFORE the host's stage (ordering contract)");

        var commitParamsIdx = h.Log.IndexOf("commit-params");
        commitParamsIdx.Should().BeGreaterThan(hostStageIdx, "params/profile commit only at Completed, after the host's stage");

        intent.Steps.Should().OnlyContain(s => s.State == WorkloadUpdateStepState.Done);
        h.Issues.Cleared.Should().BeEmpty();
    }

    [Fact]
    public async Task ApplyingSidecar_records_the_provider_applied_hash_as_the_gate_expected_hash()
    {
        var items = new[] { Item("gateway", Instance, "sha-declared", "sha-old") };
        var plan = Plan(Instance, items);
        var h = new OrchestratorHarness(plan);
        h.Provider.ApplyBehaviour["gateway"] = _ => WorkloadApplyResult.Applied("sha-actually-applied");

        var intent = await h.StageManualAsync(plan);
        await h.Orchestrator.AdvanceAsync(intent);
        await h.Orchestrator.AdvanceAsync(intent);
        await h.Orchestrator.AdvanceAsync(intent);

        h.Gate.DefaultVerdict = WorkloadHealthGateVerdict.Passed;
        await h.Orchestrator.AdvanceAsync(intent);

        h.Gate.Requests.Should().ContainSingle()
            .Which.ExpectedSpecHash.Should().Be("sha-actually-applied",
                "the HealthGate certifies the hash the provider actually applied (A5), not the declared target hash");
    }

    [Fact]
    public async Task Apply_stays_put_when_no_provider_is_registered()
    {
        var items = new[] { Item("gateway", Instance, "sha-new", "sha-old") };
        var plan = Plan(Instance, items);
        var h = new OrchestratorHarness(plan);
        h.Registry.Provider = null;

        var intent = await h.StageManualAsync(plan);
        await h.Orchestrator.AdvanceAsync(intent);
        var r = await h.Orchestrator.AdvanceAsync(intent);

        r.Phase.Should().Be(WorkloadUpdatePhase.ApplyingSidecar, "no provider ⇒ cannot apply ⇒ stay + retry, NEVER a false failure");
        r.Progressed.Should().BeFalse();
        intent.Phase.Should().Be(WorkloadUpdatePhase.ApplyingSidecar);
    }

    [Fact]
    public async Task Terminal_intent_is_a_noop_advance()
    {
        var items = new[] { Item("gateway", Instance, "sha-new", "sha-old") };
        var plan = Plan(Instance, items);
        var h = new OrchestratorHarness(plan);
        var intent = await h.StageManualAsync(plan);

        await h.Orchestrator.AdvanceAsync(intent);
        await h.Orchestrator.AdvanceAsync(intent);
        await h.Orchestrator.AdvanceAsync(intent);
        await h.Orchestrator.WalkAsync(intent);
        intent.Phase.Should().Be(WorkloadUpdatePhase.Completed);

        var updatesBefore = h.Repo.UpdateCallCount;
        var r = await h.Orchestrator.AdvanceAsync(intent);
        r.Progressed.Should().BeFalse();
        r.Phase.Should().Be(WorkloadUpdatePhase.Completed);
        h.Repo.UpdateCallCount.Should().Be(updatesBefore, "a terminal intent advance must not write");
    }

    [Fact]
    public async Task ApplyingSidecar_force_adopts_conflicts_so_a_stray_field_manager_cannot_wedge_the_update()
    {
        var items = new[] { Item("gateway", Instance, "sha-gw-new", "sha-gw-old") };
        var plan = Plan(Instance, items);
        var h = new OrchestratorHarness(plan);

        var intent = await h.StageManualAsync(plan);
        await h.Orchestrator.AdvanceAsync(intent);
        await h.Orchestrator.AdvanceAsync(intent);

        h.Provider.ApplyForce.Should().NotBeEmpty();
        h.Provider.ApplyForce.Should().OnlyContain(x => x.Force,
            "the update orchestrator must force-conflicts on its forward apply so a foreign field manager cannot 409 the update");
    }
}
