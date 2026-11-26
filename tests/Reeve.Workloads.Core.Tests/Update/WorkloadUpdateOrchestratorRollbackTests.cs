using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Reeve.Pipeline;
using static Reeve.Workloads.Core.Tests.Update.WorkloadUpdatePlanBuilder;
using Reeve.Workloads.Abstractions;
using Reeve.Workloads.Abstractions.Contracts;
using Reeve.Workloads.Abstractions.Domain.Entities;
using Reeve.Workloads.Abstractions.Providers;
using Reeve.Workloads.Abstractions.Update;
using Reeve.Workloads.Core.Tests.Groups;
using Reeve.Workloads.Core.Update;
using Xunit;

namespace Reeve.Workloads.Core.Tests.Update;

public sealed class WorkloadUpdateOrchestratorRollbackTests
{
    private static readonly Guid Instance = InstanceGuid;

    [Fact]
    public async Task Cohort_member_holds_on_a_transient_plan_failure_and_writes_no_decision()
    {
        var pair = await PairAsync("2.5.0");
        pair.H.Planner.Override = () => throw new InvalidOperationException("manifest unavailable");

        var firstResult = await pair.Orchestrator.AdvanceAsync(pair.First);
        var secondResult = await pair.Orchestrator.AdvanceAsync(pair.Second);

        firstResult.Detail.Should().Be("plan unavailable");
        secondResult.Detail.Should().Be("plan unavailable");
        pair.First.Phase.Should().Be(WorkloadUpdatePhase.StagingParams, "spec 7.2: an unavailable plan holds, it does not veto");
        pair.Second.Phase.Should().Be(WorkloadUpdatePhase.StagingParams);
        pair.First.ReadStageJournal().Value.Direction.Should().Be(PipelineDirection.Forward);
        pair.H.Repo.CohortDecisionSaveCount.Should().Be(0);
        pair.H.Planner.BuildCount.Should().Be(2, "each tick tries the plan again");
    }

    [Fact]
    public async Task A_definitive_plan_refusal_before_the_commit_rolls_back_a_member_with_nothing_to_undo_without_a_plan()
    {
        var pair = await PairAsync("2.5.0");
        pair.H.Planner.Override = () => throw new WorkloadHostRefusedException("the host refuses this version");

        var first = await pair.Orchestrator.AdvanceAsync(pair.First);
        var second = await pair.Orchestrator.AdvanceAsync(pair.Second);

        first.Phase.Should().Be(WorkloadUpdatePhase.RolledBack, "an empty journal rolls back nothing, through the one gate");
        second.Phase.Should().Be(WorkloadUpdatePhase.RolledBack, "the sibling reads the first member's ended decision and is vetoed");
        pair.First.Error.Should().Contain("cannot be planned");
        pair.H.Repo.CohortDecisionSaveCount.Should().Be(2, "one fenced decision per member");
        pair.H.Planner.BuildCount.Should().Be(1, "rolling back nothing needs no plan, so nothing builds one after the refusal");
        pair.Group.InstalledVersion.Should().Be("2.5.0");
    }

    [Fact]
    public async Task A_definitive_plan_refusal_before_the_commit_turns_a_walk_that_entered_a_reversible_stage()
    {
        var pair = await PairAsync("2.5.0", firstStage: WorkloadUpdateStages.Sidecar);
        pair.H.Planner.Override = () => throw new WorkloadHostRefusedException("the host refuses this version");

        var result = await pair.Orchestrator.AdvanceAsync(pair.First);

        result.Phase.Should().Be(WorkloadUpdatePhase.RollingBack);
        pair.First.ReadStageJournal().Value.Direction.Should().Be(PipelineDirection.Compensating);
        pair.H.Repo.CohortDecisionSaveCount.Should().Be(1);
        pair.H.Repo.SavedJournals.Last()!.Direction.Should().Be(PipelineDirection.Compensating,
            "the turn is on the row, fenced, although the plan its unwind needs cannot be built");
        pair.H.HostStage.Downs.Should().Be(0);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_plan_failure_after_the_commit_never_unwinds_the_member(bool definitive)
    {
        var pair = await PairAsync("2.6.0", firstStage: WorkloadUpdateStages.CommitVersion);
        pair.H.Planner.Override = () => definitive
            ? throw new WorkloadHostRefusedException("the host refuses this version")
            : throw new InvalidOperationException("manifest unavailable");

        var result = await pair.Orchestrator.AdvanceAsync(pair.First);

        result.Detail.Should().StartWith("plan unavailable");
        pair.First.Phase.Should().Be(WorkloadUpdatePhase.SwappingModule, "the cohort's commit landed; one instance cannot be moved back alone");
        pair.First.ReadStageJournal().Value.Direction.Should().Be(PipelineDirection.Forward);
        pair.H.Repo.CohortDecisionSaveCount.Should().Be(0);
        pair.Group.InstalledVersion.Should().Be("2.6.0");
    }

    [Fact]
    public async Task A_definitive_plan_refusal_before_the_commit_holds_a_member_past_an_irreversible_stage()
    {
        var pair = await PairAsync("2.5.0", firstStage: ScriptedHostStage.StageId);
        pair.H.Planner.Override = () => throw new WorkloadHostRefusedException("the host refuses this version");
        var phaseBefore = pair.First.Phase;

        var result = await pair.Orchestrator.AdvanceAsync(pair.First);

        result.IsTerminal.Should().BeFalse("the cohort is not committed, but the member is past a stage that cannot be undone");
        result.Detail.Should().StartWith("plan unavailable");
        pair.First.Phase.Should().Be(phaseBefore);
        pair.First.Outcome.Should().Be(WorkloadUpdateOutcome.InProgress);
        pair.First.ReadStageJournal().Value.Direction.Should().Be(PipelineDirection.Forward);
        pair.H.Repo.CohortDecisionSaveCount.Should().Be(0, "the gate refuses the unwind, so no decision is written");
        pair.H.HostStage.Downs.Should().Be(0);
        pair.Group.InstalledVersion.Should().Be("2.5.0");
    }

    [Fact]
    public async Task A_veto_turns_a_member_that_entered_a_reversible_stage_on_the_row_before_a_plan_fault_returns()
    {
        var pair = await PairAsync("2.5.0", firstStage: WorkloadUpdateStages.Sidecar);
        pair.Second.Fail("gate failed", DateTimeOffset.UtcNow);
        pair.Second.MarkRollbackFailed("gate failed", DateTimeOffset.UtcNow);
        pair.H.Planner.Override = () => throw new InvalidOperationException("manifest unavailable");

        var result = await pair.Orchestrator.AdvanceAsync(pair.First);

        result.Detail.Should().Be("plan unavailable");
        pair.First.Phase.Should().Be(WorkloadUpdatePhase.RollingBack);
        pair.First.ReadStageJournal().Value.Direction.Should().Be(PipelineDirection.Compensating);
        pair.H.Repo.CohortDecisionSaveCount.Should().Be(1);
        pair.H.Repo.SavedJournals.Last()!.Direction.Should().Be(PipelineDirection.Compensating,
            "the fenced decision saved the turn, not the forward journal it was asked on");
    }

    [Fact]
    public async Task A_veto_goes_through_the_gate_and_never_unwinds_past_an_irreversible_stage()
    {
        var pair = await PairAsync("2.5.0", firstStage: ScriptedHostStage.StageId);
        pair.Second.Fail("gate failed", DateTimeOffset.UtcNow);
        pair.Second.MarkRollbackFailed("gate failed", DateTimeOffset.UtcNow);

        var result = await pair.Orchestrator.AdvanceAsync(pair.First);

        result.IsTerminal.Should().BeTrue();
        pair.First.Outcome.Should().Be(WorkloadUpdateOutcome.RollbackFailed);
        pair.H.HostStage.Downs.Should().Be(0, "an irreversible stage is never unwound, the veto included");
        pair.H.Planner.BuildCount.Should().Be(0, "ending a walk that cannot be unwound needs no plan");
        pair.H.Provider.AppliedInOrder.Should().BeEmpty();
        pair.H.Repo.CohortDecisionSaveCount.Should().Be(1);
    }

    [Fact]
    public async Task A_single_intent_whose_rollback_cannot_plan_is_not_turned_or_saved()
    {
        var plan = Plan(Instance, [Item("gateway", Instance, "sha-new", "sha-old")]);
        var h = new OrchestratorHarness(plan);
        var intent = await h.StageManualAsync(plan);
        await h.Orchestrator.AdvanceAsync(intent);
        intent.Abandon("operator gave up", h.Clock.UtcNow).Should().Be(WorkloadUpdateAbandonOutcome.RollbackRequested);
        h.Planner.Override = () => throw new InvalidOperationException("manifest unavailable");
        var savesBefore = h.Repo.UpdateCallCount;

        var result = await h.Orchestrator.AdvanceAsync(intent);

        result.Detail.Should().Be("plan unavailable");
        intent.Phase.Should().Be(WorkloadUpdatePhase.RollingBack);
        intent.ReadStageJournal().Value.Direction.Should().Be(PipelineDirection.Forward,
            "a single intent turns after its plan is built, as it always did");
        h.Repo.UpdateCallCount.Should().Be(savesBefore);
        h.Repo.CohortDecisionSaveCount.Should().Be(0);
    }

    private sealed record Pair(
        OrchestratorHarness H,
        WorkloadUpdateOrchestrator Orchestrator,
        WorkloadUpdateIntent First,
        WorkloadUpdateIntent Second,
        WorkloadGroup Group);

    private static async Task<Pair> PairAsync(string installedVersion, string? firstStage = null)
    {
        var plan = Plan(Instance, [Item("gateway", Instance, "sha-new", "sha-old")]);
        var h = new OrchestratorHarness(plan);
        h.Repo.SupportsCohorts = true;
        var cohortId = Guid.NewGuid();
        var secondId = Guid.Parse("bbbbbbbb-2222-2222-2222-222222222222");
        var first = CohortMember(cohortId, Instance, 0, 2, firstStage);
        var second = CohortMember(cohortId, secondId, 1, 2);
        await h.Repo.AddAsync(first);
        await h.Repo.AddAsync(second);
        var instances = new FakeInstances();
        instances.Instances.Add(WorkloadInstance.Create(Guid.NewGuid(), Instance, "zwave", "gateway", h.Clock.UtcNow));
        instances.Instances.Add(WorkloadInstance.Create(Guid.NewGuid(), secondId, "zwave", "gateway", h.Clock.UtcNow));
        var group = WorkloadGroup.Record("zwave", "source", "2.5.0", h.Clock.UtcNow);
        if (installedVersion != "2.5.0")
        {
            group.MoveTo(installedVersion, h.Clock.UtcNow.AddMinutes(1));
        }

        var orchestrator = new WorkloadUpdateOrchestrator(h.Repo, h.Planner, h.Pipeline, h.Clock,
            h.OrchestratorLog, h.Lifetime, new TestGroupRepository(group), instances);
        return new Pair(h, orchestrator, first, second, group);
    }

    private static WorkloadUpdateIntent CohortMember(Guid cohortId, Guid instanceId, int ordinal, int size, string? stage = null)
    {
        var now = DateTimeOffset.UtcNow;
        var intent = WorkloadUpdateIntent.Create(Guid.NewGuid(), instanceId, "2.5.0", "2.6.0",
            now, new WorkloadUpdateCohortPlace(cohortId, ordinal, size));
        if (stage is null)
        {
            intent.RecordStageJournal(PipelineJournal.New());
            return intent;
        }

        intent.AdvanceTo(WorkloadUpdatePhase.ApplyingSidecar, now);
        var entries = new List<StageEntry>();
        if (stage != WorkloadUpdateStages.Sidecar)
        {
            intent.AdvanceTo(WorkloadUpdatePhase.HealthGate, now);
            intent.AdvanceTo(WorkloadUpdatePhase.SwappingModule, now);
        }

        if (stage == ScriptedHostStage.StageId)
        {
            entries.Add(new StageEntry(WorkloadUpdateStages.CommitVersion, now, PipelineDirection.Forward, StageEntryState.Done, null));
        }

        entries.Add(new StageEntry(stage, now, PipelineDirection.Forward, StageEntryState.Working, null));
        intent.RecordStageJournal(new PipelineJournal(entries, PipelineDirection.Forward, PipelineState.Running));
        return intent;
    }

    private static async Task<(OrchestratorHarness H, WorkloadUpdateIntent Intent)> AtHealthGateAsync(params WorkloadUpdatePlanItem[] items)
    {
        var plan = Plan(Instance, items);
        var h = new OrchestratorHarness(plan);
        var intent = await h.StageManualAsync(plan);
        await h.Orchestrator.AdvanceAsync(intent);
        await h.Orchestrator.AdvanceAsync(intent);
        h.Provider.AppliedInOrder.Clear();
        h.Log.Entries.ToString();
        return (h, intent);
    }

    [Fact]
    public async Task Gate_failure_rolls_back_capturing_evidence_before_teardown_in_reverse_order()
    {
        var (h, intent) = await AtHealthGateAsync(
            Item("gateway", Instance, "sha-gw-new", "sha-gw-old"),
            Item("metrics", Instance, "sha-mx-new", "sha-mx-old"));

        h.Gate.VerdictsByWorkload["gateway"] = new Queue<WorkloadHealthGateVerdict>([WorkloadHealthGateVerdict.Passed]);
        h.Gate.VerdictsByWorkload["metrics"] = new Queue<WorkloadHealthGateVerdict>([WorkloadHealthGateVerdict.Failed]);
        h.Gate.DefaultVerdict = WorkloadHealthGateVerdict.Passed;

        var r = await h.Orchestrator.WalkAsync(intent);

        r.Phase.Should().Be(WorkloadUpdatePhase.RolledBack);
        intent.Phase.Should().Be(WorkloadUpdatePhase.RolledBack);
        intent.CompletedAtUtc.Should().NotBeNull();

        var firstEvidenceIdx = h.Log.IndexOfFirst(e => e.StartsWith("logs:"));
        firstEvidenceIdx.Should().BeGreaterThanOrEqualTo(0, "previous-container logs are captured as evidence");
        var firstRestoreApplyIdx = h.Log.IndexOfFirst(e => e.StartsWith("apply:") && e.Contains("old"));
        firstRestoreApplyIdx.Should().BeGreaterThan(firstEvidenceIdx, "evidence MUST be captured before any teardown/re-apply (A3)");
        intent.EvidenceDocument.Should().NotBeNull();

        h.Provider.AppliedInOrder.Should().Equal("metrics", "gateway");

        h.Effects.RestoreCount.Should().Be(1);

        intent.Steps.Should().OnlyContain(s => s.State == WorkloadUpdateStepState.RolledBack);
    }

    [Fact]
    public async Task Failing_cohort_veto_turns_a_waiting_member_under_the_group_fence()
    {
        var plan = Plan(Instance, [Item("gateway", Instance, "sha-new", "sha-old")]);
        var h = new OrchestratorHarness(plan);
        var cohortId = Guid.NewGuid();
        var failed = WorkloadUpdateIntent.Create(Guid.NewGuid(), Guid.NewGuid(), "2.5.0", "2.6.0", DateTimeOffset.UtcNow,
            new WorkloadUpdateCohortPlace(cohortId, 0, 2));
        failed.RecordStageJournal(PipelineJournal.New());
        failed.Fail("gate failed", DateTimeOffset.UtcNow);
        failed.MarkRollbackFailed("gate failed", DateTimeOffset.UtcNow);
        await h.Repo.AddAsync(failed);
        var waiting = WorkloadUpdateIntent.Create(Guid.NewGuid(), Instance, "2.5.0", "2.6.0", DateTimeOffset.UtcNow,
            new WorkloadUpdateCohortPlace(cohortId, 1, 2));
        waiting.RecordStageJournal(PipelineJournal.New());
        await h.Repo.AddAsync(waiting);
        var group = new TestGroupRepository(WorkloadGroup.Record("zwave", "source", "2.5.0", DateTimeOffset.UtcNow));
        var instances = new FakeInstances();
        instances.Instances.Add(WorkloadInstance.Create(Guid.NewGuid(), Instance, "zwave", "gateway", DateTimeOffset.UtcNow));
        var orchestrator = new WorkloadUpdateOrchestrator(h.Repo, h.Planner, h.Pipeline, h.Clock,
            h.OrchestratorLog, h.Lifetime, group, instances);

        await orchestrator.AdvanceAsync(waiting);

        waiting.Phase.Should().Be(WorkloadUpdatePhase.RolledBack);
        waiting.Error.Should().Contain(failed.GroupInstanceId.ToString());
        h.Repo.CohortDecisionSaveCount.Should().Be(1,
            "the veto saves nothing of its own: ending the nothing-to-undo rollback is the one fenced decision (7.2)");
        h.Planner.BuildCount.Should().Be(0, "rolling back nothing needs no plan");
    }

    private sealed class TestGroupRepository(WorkloadGroup group) : IWorkloadGroupRepository
    {
        public Task<WorkloadGroup?> GetAsync(string groupId, CancellationToken cancellationToken = default)
            => Task.FromResult<WorkloadGroup?>(group.IsGroup(groupId) ? group : null);
        public Task<bool> TryAddAsync(WorkloadGroup group, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
        public Task UpdateAsync(WorkloadGroup group, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }

    [Fact]
    public async Task Rollback_reapply_force_adopts_conflicts_so_the_restore_cannot_wedge()
    {
        var (h, intent) = await AtHealthGateAsync(Item("gateway", Instance, "sha-new", "sha-old"));
        h.Provider.ApplyForce.Clear();

        h.Gate.VerdictsByWorkload["gateway"] = new Queue<WorkloadHealthGateVerdict>([WorkloadHealthGateVerdict.Failed]);
        h.Gate.DefaultVerdict = WorkloadHealthGateVerdict.Passed;

        var r = await h.Orchestrator.WalkAsync(intent);
        r.Phase.Should().Be(WorkloadUpdatePhase.RolledBack);

        h.Provider.ApplyForce.Should().NotBeEmpty("the previous spec is re-applied during rollback");
        h.Provider.ApplyForce.Should().OnlyContain(x => x.Force,
            "the rollback restore must force-conflicts so a foreign field manager cannot wedge it into RollbackFailed");
    }

    [Fact]
    public async Task Evidence_capture_requests_200_previous_container_log_lines()
    {
        var (h, intent) = await AtHealthGateAsync(Item("gateway", Instance, "sha-new", "sha-old"));
        h.Gate.VerdictsByWorkload["gateway"] = new Queue<WorkloadHealthGateVerdict>([WorkloadHealthGateVerdict.Failed]);

        await h.Orchestrator.WalkAsync(intent);

        h.Log.Entries.Should().Contain("logs:gateway:tail=200:prev=True",
            "the rollback evidence is the last 200 previous-container log lines");
    }

    [Fact]
    public async Task Apply_failure_drives_rollback_never_a_silent_completed()
    {
        var plan = Plan(Instance, [Item("gateway", Instance, "sha-new", "sha-old")]);
        var h = new OrchestratorHarness(plan);
        h.Provider.ApplyBehaviour["gateway"] = spec => spec.SpecHash == "sha-new"
            ? WorkloadApplyResult.Failed(WorkloadApplyReason.Rejected, "immutable field")
            : WorkloadApplyResult.Applied(spec.SpecHash);
        h.Gate.DefaultVerdict = WorkloadHealthGateVerdict.Passed;

        var intent = await h.StageManualAsync(plan);
        await h.Orchestrator.AdvanceAsync(intent);
        var r = await h.Orchestrator.WalkAsync(intent);

        r.Phase.Should().Be(WorkloadUpdatePhase.RolledBack, "a rejected apply must roll back, never silently Complete (A4)");
        intent.EvidenceDocument.Should().NotBeNull("evidence is captured on every abandon-N+1 path");
        intent.Error.Should().Contain("Apply");
    }

    [Fact]
    public async Task Transient_apply_unreachable_stays_put_and_does_not_roll_back()
    {
        var plan = Plan(Instance, [Item("gateway", Instance, "sha-new", "sha-old")]);
        var h = new OrchestratorHarness(plan);
        h.Provider.ApplyBehaviour["gateway"] = _ => WorkloadApplyResult.Failed(WorkloadApplyReason.ProviderUnreachable, "api down");

        var intent = await h.StageManualAsync(plan);
        await h.Orchestrator.AdvanceAsync(intent);
        var r = await h.Orchestrator.AdvanceAsync(intent);

        r.Phase.Should().Be(WorkloadUpdatePhase.ApplyingSidecar, "a merely-unreachable provider is transient — retry, never roll back");
        intent.Phase.Should().Be(WorkloadUpdatePhase.ApplyingSidecar);
    }

    [Fact]
    public async Task Rollback_timeout_terminalizes_to_rollback_failed_with_evidence_intact()
    {
        var options = new WorkloadUpdateOrchestratorOptions { RollbackTimeout = TimeSpan.FromMinutes(5) };
        var plan = Plan(Instance, [Item("gateway", Instance, "sha-new", "sha-old")]);
        var h = new OrchestratorHarness(plan, options: options);

        var intent = await h.StageManualAsync(plan);
        await h.Orchestrator.AdvanceAsync(intent);
        await h.Orchestrator.AdvanceAsync(intent);

        h.Gate.VerdictsByWorkload["gateway"] = new Queue<WorkloadHealthGateVerdict>([WorkloadHealthGateVerdict.Failed]);
        h.Gate.DefaultVerdict = WorkloadHealthGateVerdict.Pending;

        var r1 = await h.Orchestrator.WalkAsync(intent);
        r1.Phase.Should().Be(WorkloadUpdatePhase.RollingBack);
        intent.EvidenceDocument.Should().NotBeNull();

        h.Clock.Advance(TimeSpan.FromMinutes(6));
        var r2 = await h.Orchestrator.WalkAsync(intent);

        r2.Phase.Should().Be(WorkloadUpdatePhase.RollbackFailed);
        intent.Phase.Should().Be(WorkloadUpdatePhase.RollbackFailed);
        intent.CompletedAtUtc.Should().NotBeNull();
        intent.EvidenceDocument.Should().NotBeNull("evidence must survive into RollbackFailed (A3)");
        intent.Steps.Should().OnlyContain(s => s.State == WorkloadUpdateStepState.Failed);
    }

    [Fact]
    public async Task All_or_nothing_rolls_back_workload_A_even_though_only_B_failed()
    {
        var (h, intent) = await AtHealthGateAsync(
            Item("a-first", Instance, "sha-a-new", "sha-a-old"),
            Item("b-second", Instance, "sha-b-new", "sha-b-old"));

        h.Gate.VerdictsByWorkload["a-first"] = new Queue<WorkloadHealthGateVerdict>([WorkloadHealthGateVerdict.Passed]);
        h.Gate.VerdictsByWorkload["b-second"] = new Queue<WorkloadHealthGateVerdict>([WorkloadHealthGateVerdict.Failed]);
        h.Gate.DefaultVerdict = WorkloadHealthGateVerdict.Passed;

        await h.Orchestrator.WalkAsync(intent);

        h.Provider.AppliedInOrder.Should().Equal("b-second", "a-first");
        intent.Phase.Should().Be(WorkloadUpdatePhase.RolledBack);
    }

    [Fact]
    public async Task Added_workload_with_no_previous_spec_is_torn_down_on_rollback()
    {
        var (h, intent) = await AtHealthGateAsync(
            Item("gateway", Instance, "sha-gw-new", "sha-gw-old"),
            Item("metrics", Instance, "sha-mx-new", previousHash: null));

        h.Gate.VerdictsByWorkload["gateway"] = new Queue<WorkloadHealthGateVerdict>([WorkloadHealthGateVerdict.Failed]);
        h.Gate.DefaultVerdict = WorkloadHealthGateVerdict.Passed;

        await h.Orchestrator.WalkAsync(intent);

        h.Provider.TornDownInOrder.Should().Contain("metrics", "an added workload has no previous spec to restore — tear it down");
        h.Provider.AppliedInOrder.Should().Contain("gateway", "the existing workload IS restored");
        intent.Phase.Should().Be(WorkloadUpdatePhase.RolledBack);
    }

    [Fact]
    public async Task Undisturbed_workload_rollback_accepts_existing_live_session()
    {
        var options = new WorkloadUpdateOrchestratorOptions
        {
            HealthGateTimeout = TimeSpan.FromMinutes(5),
            RollbackTimeout = TimeSpan.FromMinutes(10),
        };
        var plan = Plan(Instance, [Item("gateway", Instance, "sha-gw-new", "sha-gw-old")]);

        var clock = new MutableClock(new DateTimeOffset(2026, 7, 6, 12, 0, 0, TimeSpan.Zero));
        var oldSession = new LiveSession(
            SpecHash: "sha-gw-old",
            ConnectedAtUtc: clock.UtcNow.AddMinutes(-30));

        var (h, orchestrator) = BuildOrchestratorWithRealisticGate(plan, clock, oldSession, options);

        var intent = await h.StageManualAsync(plan);
        await orchestrator.AdvanceAsync(intent);
        await orchestrator.AdvanceAsync(intent);

        (await orchestrator.AdvanceAsync(intent)).Phase.Should().Be(WorkloadUpdatePhase.HealthGate);

        clock.Advance(TimeSpan.FromMinutes(6));
        await orchestrator.AdvanceAsync(intent);

        clock.Advance(TimeSpan.FromMinutes(20));
        var r = await orchestrator.WalkAsync(intent);

        r.Phase.Should().Be(WorkloadUpdatePhase.RolledBack,
            "a healthy, undisturbed workload's already-live OLD session must satisfy the rollback health wait");
        intent.Phase.Should().Be(WorkloadUpdatePhase.RolledBack);
        intent.Phase.Should().NotBe(WorkloadUpdatePhase.RollbackFailed,
            "the safety net must not report failure on a healthy rollback");
        intent.CompletedAtUtc.Should().NotBeNull();
        intent.Steps.Should().OnlyContain(s => s.State == WorkloadUpdateStepState.RolledBack);
    }

    [Fact]
    public async Task Forward_apply_still_rejects_a_stale_pre_apply_session()
    {
        var options = new WorkloadUpdateOrchestratorOptions
        {
            HealthGateTimeout = TimeSpan.FromMinutes(5),
            RollbackTimeout = TimeSpan.FromMinutes(10),
        };
        var plan = Plan(Instance, [Item("gateway", Instance, "sha-gw-new", "sha-gw-old")]);

        var clock = new MutableClock(new DateTimeOffset(2026, 7, 6, 12, 0, 0, TimeSpan.Zero));

        var preApplySession = new LiveSession(
            SpecHash: "sha-gw-new",
            ConnectedAtUtc: clock.UtcNow.AddMinutes(-30));

        var (h, orchestrator) = BuildOrchestratorWithRealisticGate(plan, clock, preApplySession, options);

        var intent = await h.StageManualAsync(plan);
        await orchestrator.AdvanceAsync(intent);
        await orchestrator.AdvanceAsync(intent);

        var r = await orchestrator.AdvanceAsync(intent);

        r.Phase.Should().Be(WorkloadUpdatePhase.HealthGate,
            "a pre-apply (outgoing-sidecar) session must NOT satisfy the forward gate — the time gate is not weakened");
        intent.Phase.Should().Be(WorkloadUpdatePhase.HealthGate);
        intent.Phase.Should().NotBe(WorkloadUpdatePhase.SwappingModule,
            "the forward gate must never pass on a pre-apply session");
    }

    private readonly record struct LiveSession(string SpecHash, DateTimeOffset ConnectedAtUtc);

    private static (OrchestratorHarness Harness, WorkloadUpdateOrchestrator Orchestrator) BuildOrchestratorWithRealisticGate(
        WorkloadUpdatePlan plan,
        MutableClock clock,
        LiveSession liveSession,
        WorkloadUpdateOrchestratorOptions options)
    {
        var h = new OrchestratorHarness(plan, now: clock.UtcNow, options: options);
        var realisticGate = new RealisticHealthGate(clock, liveSession);
        var orchestrator = h.OrchestratorWith(realisticGate, clock, options);
        return (h, orchestrator);
    }

    private sealed class RealisticHealthGate(MutableClock clock, LiveSession session) : IWorkloadUpdateHealthGate
    {
        private DateTimeOffset? _rollbackClockStartedAtUtc;

        public Task<WorkloadHealthGateTickResult> EvaluateTickAsync(WorkloadHealthGateTickRequest request, CancellationToken cancellationToken = default)
        {
            var now = clock.UtcNow;
            var rollbackMode = request.ApplyTimestampUtc == DateTimeOffset.MinValue;

            var postApply = rollbackMode || session.ConnectedAtUtc > request.ApplyTimestampUtc;
            var hashMatches = string.Equals(session.SpecHash, request.ExpectedSpecHash, StringComparison.Ordinal);

            if (postApply && hashMatches)
            {
                return Result(WorkloadHealthGateVerdict.Passed);
            }

            var startedAt = rollbackMode
                ? (_rollbackClockStartedAtUtc ??= now)
                : request.ApplyTimestampUtc;

            return now - startedAt > request.Timeout
                ? Result(WorkloadHealthGateVerdict.Failed, "Timeout")
                : Result(WorkloadHealthGateVerdict.Pending);
        }

        private static Task<WorkloadHealthGateTickResult> Result(WorkloadHealthGateVerdict verdict, string? failureReason = null)
            => Task.FromResult(new WorkloadHealthGateTickResult
            {
                Verdict = verdict,
                FailureReason = failureReason,
                Detail = null,
                NextClockState = new WorkloadGateClockState(),
            });
    }
}
