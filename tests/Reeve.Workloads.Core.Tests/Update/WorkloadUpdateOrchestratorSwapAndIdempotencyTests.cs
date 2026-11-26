using Reeve.Workloads.Abstractions.Domain;
using Reeve.Workloads.Abstractions.Domain.Entities.Documents;
using System.Reflection;
using FluentAssertions;
using static Reeve.Workloads.Core.Tests.Update.WorkloadUpdatePlanBuilder;
using Reeve.Workloads.Abstractions;
using Reeve.Workloads.Abstractions.Domain.Entities;
using Reeve.Workloads.Abstractions.Update;
using Reeve.Workloads.Core.Update;
using Xunit;

namespace Reeve.Workloads.Core.Tests.Update;

public sealed class WorkloadUpdateOrchestratorSwapAndIdempotencyTests
{
    private static readonly Guid Instance = InstanceGuid;

    private static async Task<(OrchestratorHarness H, WorkloadUpdateIntentStep Step, WorkloadUpdateIntent Intent)>
        AtOneGateTickAsync()
    {
        var plan = Plan(Instance, [Item("gateway", Instance, "sha-new", "sha-old")]);
        var h = new OrchestratorHarness(plan);
        var intent = await h.StageManualAsync(plan);
        await h.Orchestrator.AdvanceAsync(intent);
        await h.Orchestrator.AdvanceAsync(intent);
        await h.Orchestrator.AdvanceAsync(intent);
        h.Gate.DefaultVerdict = WorkloadHealthGateVerdict.Pending;
        await h.Orchestrator.AdvanceAsync(intent);
        return (h, intent.Steps.Single(), intent);
    }

    [Fact]
    public async Task The_resumed_gate_takes_the_clock_the_document_holds()
    {
        var (h, step, intent) = await AtOneGateTickAsync();
        Set(step, nameof(WorkloadUpdateIntentStep.GateClockStateDocument), new GateClockStateDocument
        {
            AccumulatedPause = TimeSpan.FromSeconds(41),
        });

        await h.Orchestrator.AdvanceAsync(intent);

        h.Gate.Requests[^1].PriorClockState.AccumulatedPause.Should().Be(TimeSpan.FromSeconds(41));
    }

    [Fact]
    public async Task A_lost_clock_document_restarts_the_window()
    {
        var (h, step, intent) = await AtOneGateTickAsync();
        Set(step, nameof(WorkloadUpdateIntentStep.GateClockStateDocument), null);

        await h.Orchestrator.AdvanceAsync(intent);

        h.Gate.Requests[^1].PriorClockState.Should().Be(new WorkloadGateClockState());
    }

    private static void Set(object entity, string property, object? value)
        => entity.GetType().GetProperty(property, BindingFlags.Instance | BindingFlags.Public)!.SetValue(entity, value);

    private static async Task<(OrchestratorHarness H, WorkloadUpdateIntent Intent)> AtSwappingModuleAsync()
    {
        var plan = Plan(Instance, [Item("gateway", Instance, "sha-new", "sha-old")]);
        var h = new OrchestratorHarness(plan);
        var intent = await h.StageManualAsync(plan);
        await h.Orchestrator.AdvanceAsync(intent);
        await h.Orchestrator.AdvanceAsync(intent);
        await h.Orchestrator.AdvanceAsync(intent);
        await h.Orchestrator.AdvanceAsync(intent);
        intent.Phase.Should().Be(WorkloadUpdatePhase.SwappingModule);
        return (h, intent);
    }

    [Fact]
    public async Task A_host_stage_that_keeps_working_holds_the_update_and_never_rolls_it_back()
    {
        var (h, intent) = await AtSwappingModuleAsync();
        h.HostStage.KeepWorking();

        var r1 = await h.Orchestrator.WalkAsync(intent);
        h.Clock.Advance(TimeSpan.FromHours(1));
        var r2 = await h.Orchestrator.WalkAsync(intent);

        r2.Phase.Should().Be(WorkloadUpdatePhase.SwappingModule, "past the version commit an update never rolls back");
        intent.ReadStageJournal().Value!.Entries[^1].Should().Match<Reeve.Pipeline.StageEntry>(entry =>
            entry.StageId == ScriptedHostStage.StageId && entry.State == Reeve.Pipeline.StageEntryState.Working);
        h.Effects.CommitParamsCount.Should().Be(0, "the last-known-good moves only once every stage has finished");

        h.HostStage.DefaultOutcome = Reeve.Pipeline.StageOutcome.Done;
        var r3 = await h.Orchestrator.WalkAsync(intent);
        r3.Phase.Should().Be(WorkloadUpdatePhase.Completed);
        h.Effects.CommitParamsCount.Should().Be(1);
    }

    [Fact]
    public async Task The_version_is_committed_before_the_host_stage_runs_and_the_parameters_after_it()
    {
        var (h, intent) = await AtSwappingModuleAsync();

        var r = await h.Orchestrator.WalkAsync(intent);

        r.Phase.Should().Be(WorkloadUpdatePhase.Completed);
        h.Effects.CommitInstalledVersionCount.Should().Be(1);
        var commitVersion = h.Log.IndexOf("commit-installed-version:2.6.0");
        var hostStage = h.Log.IndexOf("host-stage:2.6.0");
        var commitParams = h.Log.IndexOf("commit-params");
        commitVersion.Should().BeGreaterThanOrEqualTo(0);
        hostStage.Should().BeGreaterThan(commitVersion, "the host reloads to a version the library already reads as installed");
        commitParams.Should().BeGreaterThan(hostStage, "nothing completes before the host's stage has finished");
    }

    [Fact]
    public async Task A_reversible_stage_after_the_commit_that_fails_unwinds_the_update()
    {
        var plan = Plan(Instance, [Item("gateway", Instance, "sha-new", "sha-old")]);
        var h = new OrchestratorHarness(plan);
        var check = new FailingReversibleStage(h.Log);
        var orchestrator = h.OrchestratorWith(h.Gate, h.Clock, h.Options, check);
        var intent = await h.StageManualAsync(plan);

        for (var walks = 0; walks < 10 && !intent.IsTerminal; walks++)
        {
            await orchestrator.WalkAsync(intent);
        }

        intent.Phase.Should().Be(WorkloadUpdatePhase.RolledBack);
        intent.Error.Should().Contain(FailingReversibleStage.Refusal);
        check.Downs.Should().Be(1, "the failed stage is compensated too: it may have applied something");
        h.HostStage.Calls.Should().BeEmpty("the irreversible stage behind the failure is never entered");
        h.Log.IndexOf("commit-installed-version:2.5.0").Should().BeGreaterThan(
            h.Log.IndexOf("commit-installed-version:2.6.0"), "the version commit is put back on the way down");
        h.Effects.CommitParamsCount.Should().Be(0);
    }

    [Fact]
    public async Task An_unwind_past_the_commit_restores_where_the_update_started_not_what_the_rows_say_now()
    {
        var plan = Plan(Instance, [Item("gateway", Instance, "sha-new", "sha-old")]);
        var h = new OrchestratorHarness(plan);
        h.Planner.Override = () =>
        {
            var from = h.Effects.InstalledVersion;
            var previousHash = from == "2.5.0" ? "sha-old" : "sha-new";
            return Plan(Instance, [Item("gateway", Instance, "sha-new", previousHash)]) with { FromVersion = from };
        };
        var orchestrator = h.OrchestratorWith(h.Gate, h.Clock, h.Options, new FailingReversibleStage(h.Log));
        var intent = await h.StageManualAsync(plan);

        for (var walks = 0; walks < 10 && !intent.IsTerminal; walks++)
        {
            await orchestrator.WalkAsync(intent);
        }

        intent.Outcome.Should().Be(WorkloadUpdateOutcome.RolledBack);
        h.Planner.LastOrigin.Should().Be(new WorkloadUpdatePlanOrigin("2.5.0"),
            "the planner is told where the update started, for what it reads off that version's manifest");
        h.Effects.InstalledVersion.Should().Be("2.5.0", "the row is put back where the update found it");
        var committed = h.Log.IndexOf("commit-installed-version:2.6.0");
        h.Log.Entries.Skip(committed + 1).Should().Contain(entry => entry.Contains("sha-old"),
            "the sidecar is restored to the previous spec")
            .And.NotContain(entry => entry.Contains("sha-new"), "nothing re-applies the candidate on the way down");
    }

    [Fact]
    public async Task A_rollback_restores_the_recorded_previous_spec_the_drift_pass_converges_on()
    {
        var plan = Plan(Instance, [Item("gateway", Instance, "sha-new", "sha-old")]);
        var h = new OrchestratorHarness(plan);
        var intent = await h.StageManualAsync(plan);
        h.Planner.Override = () => Plan(Instance, [Item("gateway", Instance, "sha-new", "sha-rerendered")]);
        h.Gate.VerdictsByWorkload["gateway"] = new Queue<WorkloadHealthGateVerdict>([WorkloadHealthGateVerdict.Failed]);

        for (var walks = 0; walks < 10 && !intent.IsTerminal; walks++)
        {
            await h.Orchestrator.WalkAsync(intent);
        }

        intent.Outcome.Should().Be(WorkloadUpdateOutcome.RolledBack);
        h.Log.Entries.Should().Contain(entry => entry.StartsWith("apply:gateway:sha-old"))
            .And.NotContain(entry => entry.Contains("sha-rerendered"));
    }

    [Fact]
    public async Task An_abandon_that_arrives_past_the_point_of_no_return_ends_the_walk_without_unwinding_it()
    {
        var (h, intent) = await AtSwappingModuleAsync();
        h.HostStage.KeepWorking();
        await h.Orchestrator.WalkAsync(intent);
        intent.Abandon("operator gave up", h.Clock.UtcNow);

        var r = await h.Orchestrator.AdvanceAsync(intent);

        h.HostStage.Downs.Should().Be(0);
        r.IsTerminal.Should().BeTrue();
        intent.Outcome.Should().Be(WorkloadUpdateOutcome.RollbackFailed);
        intent.Error.Should().Contain(ScriptedHostStage.IrreversibleReason).And.Contain("nothing was undone");
        intent.Steps.Should().OnlyContain(step => step.Progress == WorkloadUpdateStepProgress.Failed);
        intent.ReadStageJournal().Value!.Direction.Should().Be(Reeve.Pipeline.PipelineDirection.Forward);
        intent.Direction.Should().Be(WorkloadUpdateDirection.Forward, "no rollback ran");
        h.Effects.CommitInstalledVersionCount.Should().Be(1, "the committed version is not put back under the host stage");
    }

    [Fact]
    public async Task The_host_stage_is_handed_the_plans_source_key_and_group()
    {
        var (h, intent) = await AtSwappingModuleAsync();

        await h.Orchestrator.WalkAsync(intent);

        var plan = Plan(Instance, [Item("gateway", Instance, "sha-new", "sha-old")]);
        h.HostStage.Calls.Should().ContainSingle().Which.Should().Match<WorkloadUpdateStageContext>(context =>
            context.SourceKey == plan.SourceKey && context.GroupId == plan.GroupId && context.ToVersion == "2.6.0");
        h.Effects.LastCommittedGroupId.Should().Be(plan.GroupId);
    }

    [Fact]
    public async Task Re_ticking_applying_sidecar_does_not_re_apply_an_already_applied_workload()
    {
        var plan = Plan(Instance, [Item("gateway", Instance, "sha-new", "sha-old")]);
        var h = new OrchestratorHarness(plan);
        var intent = await h.StageManualAsync(plan);
        await h.Orchestrator.AdvanceAsync(intent);
        await h.Orchestrator.AdvanceAsync(intent);
        var stampedAt = intent.SidecarAppliedAtUtc;

        h.Clock.Advance(TimeSpan.FromSeconds(30));
        h.Gate.DefaultVerdict = WorkloadHealthGateVerdict.Pending;
        await h.Orchestrator.AdvanceAsync(intent);
        intent.SidecarAppliedAtUtc.Should().Be(stampedAt, "SidecarAppliedAtUtc is stamped exactly once");
    }

    [Fact]
    public async Task Re_ticking_a_completed_update_does_not_re_commit_params_or_call_the_host_stage_again()
    {
        var (h, intent) = await AtSwappingModuleAsync();

        await h.Orchestrator.WalkAsync(intent);
        h.Effects.CommitParamsCount.Should().Be(1);
        var calls = h.HostStage.Calls.Count;

        await h.Orchestrator.WalkAsync(intent);
        h.Effects.CommitParamsCount.Should().Be(1, "params/profile commit exactly once, at Completed");
        h.HostStage.Calls.Should().HaveCount(calls);
    }

    [Fact]
    public async Task Resumed_intent_mid_health_gate_continues_off_its_persisted_clock_state()
    {
        var plan = Plan(Instance, [Item("gateway", Instance, "sha-new", "sha-old")]);
        var h = new OrchestratorHarness(plan);
        var intent = await h.StageManualAsync(plan);
        await h.Orchestrator.AdvanceAsync(intent);
        await h.Orchestrator.AdvanceAsync(intent);
        await h.Orchestrator.AdvanceAsync(intent);

        h.Gate.DefaultVerdict = WorkloadHealthGateVerdict.Pending;

        await h.Orchestrator.AdvanceAsync(intent);
        await h.Orchestrator.AdvanceAsync(intent);
        await h.Orchestrator.AdvanceAsync(intent);

        var step = intent.Steps.Single();
        step.GateClockStateDocument.Should().NotBeNull();
        ScriptedHealthGate.ParseTicks(step).Should().Be(3,
            "the per-workload gate clock is threaded across ticks (survives a leader failover mid-gate)");

        var h2 = new OrchestratorHarness(plan);
        h2.Gate.DefaultVerdict = WorkloadHealthGateVerdict.Pending;
        var r = await h2.Orchestrator.AdvanceAsync(intent);
        r.Phase.Should().Be(WorkloadUpdatePhase.HealthGate);
        h2.Gate.Requests.Should().ContainSingle()
            .Which.PriorClockState.Should().NotBe(new WorkloadGateClockState(),
                "the resumed leader feeds the persisted clock back into the gate");
        ScriptedHealthGate.ParseTicks(intent.Steps.Single()).Should().Be(4,
            "the resumed tick continues the elapsed-clock count from the persisted state");
    }
}

internal sealed class FailingReversibleStage(CallLog log) : Reeve.Pipeline.IStage<WorkloadUpdateStageContext>
{
    public const string Refusal = "the host's check refused the candidate";

    public string Id => "stage.host.a-reversible-check";

    public Reeve.Pipeline.StagePlacement Placement => Reeve.Pipeline.StagePlacement.After(WorkloadUpdateStages.CommitVersion);

    public Reeve.Pipeline.StageReversibility Reversibility => Reeve.Pipeline.StageReversibility.Reversible;

    public Reeve.Pipeline.StageBudget Budget => Reeve.Pipeline.StageBudget.None;

    public int Downs { get; private set; }

    public ValueTask<Reeve.Pipeline.StageOutcome> UpAsync(WorkloadUpdateStageContext context, CancellationToken cancellationToken)
    {
        log.Add("reversible-check:up");
        return ValueTask.FromResult(Reeve.Pipeline.StageOutcome.Failed(Refusal));
    }

    public ValueTask<Reeve.Pipeline.StageOutcome> DownAsync(WorkloadUpdateStageContext context, CancellationToken cancellationToken)
    {
        Downs++;
        log.Add("reversible-check:down");
        return ValueTask.FromResult(Reeve.Pipeline.StageOutcome.Done);
    }
}
