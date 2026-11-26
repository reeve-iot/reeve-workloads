using Reeve.Workloads.Abstractions;
using System.Reflection;
using FluentAssertions;
using Reeve.Pipeline;
using Reeve.Workloads.Abstractions.Domain.Entities;
using Reeve.Workloads.Abstractions.Domain.Entities.Documents;
using Reeve.Workloads.Abstractions.Providers;
using Reeve.Workloads.Abstractions.Update;
using Reeve.Workloads.Core.Update;
using Reeve.Workloads.Core.Update.Stages;
using Xunit;
using static Reeve.Workloads.Core.Tests.Update.WorkloadUpdatePlanBuilder;

namespace Reeve.Workloads.Core.Tests.Update;

public sealed class WorkloadUpdateOrchestratorWalkTests
{
    private static readonly Guid Instance = InstanceGuid;

    private static WorkloadUpdatePlan OneWorkload() => Plan(Instance, [Item("gateway", Instance, "sha-new", "sha-old")]);

    [Fact]
    public async Task Every_tick_saves_the_journal_with_the_row()
    {
        var h = new OrchestratorHarness(OneWorkload());
        var intent = await h.StageManualAsync(OneWorkload());
        var saves = h.Repo.UpdateCallCount;

        await h.Orchestrator.AdvanceAsync(intent);

        h.Repo.UpdateCallCount.Should().Be(saves + 2, "the entry before the call, the outcome after it");
        h.Repo.SavedJournals[^2]!.Entries.Should().ContainSingle().Which.Should().Match<StageEntry>(entry =>
            entry.StageId == WorkloadUpdateStages.Params && entry.State == StageEntryState.Working);
        var journal = intent.ReadStageJournal();
        journal.Verdict.Should().Be(WorkloadDocumentVerdict.Value);
        journal.Value!.Entries.Should().ContainSingle()
            .Which.Should().Match<StageEntry>(entry =>
                entry.StageId == WorkloadUpdateStages.Params && entry.State == StageEntryState.Done);
    }

    [Fact]
    public async Task A_tick_whose_stage_is_still_working_is_saved_too()
    {
        var h = new OrchestratorHarness(OneWorkload());
        var intent = await h.StageManualAsync(OneWorkload());
        h.Gate.DefaultVerdict = WorkloadHealthGateVerdict.Pending;
        await h.Orchestrator.AdvanceAsync(intent);
        await h.Orchestrator.AdvanceAsync(intent);
        await h.Orchestrator.AdvanceAsync(intent);
        var saves = h.Repo.UpdateCallCount;

        await h.Orchestrator.AdvanceAsync(intent);
        h.Repo.UpdateCallCount.Should().Be(saves + 2, "entering the gate saves its entry first");
        saves = h.Repo.UpdateCallCount;

        var result = await h.Orchestrator.AdvanceAsync(intent);

        result.Progressed.Should().BeFalse();
        h.Repo.UpdateCallCount.Should().Be(saves + 1, "a stage asked again is not entered again");
    }

    [Fact]
    public async Task A_working_stage_keeps_its_budget_across_a_failover()
    {
        var h = new OrchestratorHarness(OneWorkload());
        var intent = await h.StageManualAsync(OneWorkload());
        h.Gate.DefaultVerdict = WorkloadHealthGateVerdict.Pending;
        await h.Orchestrator.WalkAsync(intent);
        intent.Phase.Should().Be(WorkloadUpdatePhase.HealthGate);

        var takeover = new OrchestratorHarness(OneWorkload(), now: h.Clock.UtcNow + h.Options.HealthGateMaximumWait + TimeSpan.FromSeconds(1));
        takeover.Gate.DefaultVerdict = WorkloadHealthGateVerdict.Pending;

        await takeover.Orchestrator.AdvanceAsync(intent);

        intent.Phase.Should().Be(WorkloadUpdatePhase.RollingBack, "the gate's budget ran out while the first leader held it");
    }

    [Theory]
    [InlineData(nameof(WorkloadUpdatePhase.ApplyingSidecar))]
    [InlineData(nameof(WorkloadUpdatePhase.HealthGate))]
    [InlineData(nameof(WorkloadUpdatePhase.SwappingModule))]
    public async Task A_row_whose_stage_journal_does_not_account_for_its_phase_is_left_alone(string phaseName)
    {
        var phase = Enum.Parse<WorkloadUpdatePhase>(phaseName);
        var h = new OrchestratorHarness(OneWorkload());
        var intent = await h.StageManualAsync(OneWorkload());
        foreach (var step in new[] { WorkloadUpdatePhase.ApplyingSidecar, WorkloadUpdatePhase.HealthGate, WorkloadUpdatePhase.SwappingModule })
        {
            intent.AdvanceTo(step, h.Clock.UtcNow);
            if (step == phase)
            {
                break;
            }
        }

        var saves = h.Repo.UpdateCallCount;

        var result = await h.Orchestrator.AdvanceAsync(intent);

        result.Progressed.Should().BeFalse();
        intent.Phase.Should().Be(phase);
        h.Provider.AppliedInOrder.Should().BeEmpty("no stage runs on a row whose position is unknown");
        h.Effects.CommitInstalledVersionCount.Should().Be(0);
        h.Repo.UpdateCallCount.Should().Be(saves);
    }

    [Fact]
    public async Task A_rolling_back_row_whose_stage_journal_is_unreadable_is_ended_without_a_walk()
    {
        var h = new OrchestratorHarness(OneWorkload());
        var intent = await h.StageManualAsync(OneWorkload());
        intent.AdvanceTo(WorkloadUpdatePhase.ApplyingSidecar, h.Clock.UtcNow);
        intent.Fail("apply failed", h.Clock.UtcNow);
        Set(intent, nameof(WorkloadUpdateIntent.StageJournalDocument), new PipelineJournalDocument { Unreadable = true });

        var result = await h.Orchestrator.AdvanceAsync(intent);

        intent.Outcome.Should().Be(WorkloadUpdateOutcome.RollbackFailed);
        intent.Error.Should().Contain("The rollback could not be walked");
        intent.Steps.Should().OnlyContain(step => step.Progress == WorkloadUpdateStepProgress.Failed);
        h.Provider.AppliedInOrder.Should().BeEmpty("nothing is restored without a journal to walk back");
        result.IsTerminal.Should().BeTrue();
    }

    [Fact]
    public async Task An_update_abandoned_before_its_first_tick_rolls_back()
    {
        var h = new OrchestratorHarness(OneWorkload());
        var intent = await h.StageManualAsync(OneWorkload());
        intent.ReadStageJournal().Verdict.Should().Be(WorkloadDocumentVerdict.Value, "staging writes the journal");
        h.Planner.Override = () => throw new InvalidOperationException("manifest store down");
        (await h.Orchestrator.AdvanceAsync(intent)).Detail.Should().Be("plan unavailable");
        intent.Abandon("operator gave up", h.Clock.UtcNow);
        h.Planner.Override = null;

        await h.Orchestrator.WalkAsync(intent);

        intent.Outcome.Should().Be(WorkloadUpdateOutcome.RolledBack);
        h.Provider.AppliedInOrder.Should().BeEmpty();
    }

    [Fact]
    public async Task A_plan_the_host_refused_stays_and_names_the_refusal()
    {
        var h = new OrchestratorHarness(OneWorkload());
        var intent = await h.StageManualAsync(OneWorkload());
        var phase = intent.Phase;
        h.Planner.Override = () => throw new WorkloadHostRefusedException("broker credentials are not provisioned");

        var result = await h.Orchestrator.AdvanceAsync(intent);

        result.Detail.Should().Be("plan unavailable: broker credentials are not provisioned");
        intent.Phase.Should().Be(phase);
        h.Provider.AppliedInOrder.Should().BeEmpty();
    }

    [Fact]
    public async Task An_update_abandoned_before_any_stage_ran_rolls_back_without_a_plan()
    {
        var h = new OrchestratorHarness(OneWorkload());
        var intent = await h.StageManualAsync(OneWorkload());
        h.Planner.Override = () => throw new InvalidOperationException("manifest store down");
        intent.Abandon("operator gave up", h.Clock.UtcNow);
        var builds = h.Planner.BuildCount;

        var r = await h.Orchestrator.AdvanceAsync(intent);

        r.IsTerminal.Should().BeTrue();
        intent.Outcome.Should().Be(WorkloadUpdateOutcome.RolledBack);
        intent.Steps.Should().OnlyContain(step => step.Progress == WorkloadUpdateStepProgress.Undone);
        h.Planner.BuildCount.Should().Be(builds, "nothing to undo needs no plan");
        h.Provider.AppliedInOrder.Should().BeEmpty();
    }

    [Fact]
    public async Task A_journal_ahead_of_its_phase_is_left_untouched()
    {
        var h = new OrchestratorHarness(OneWorkload());
        var intent = await h.StageManualAsync(OneWorkload());
        intent.RecordStageJournal(new PipelineJournal(
            [
                new StageEntry(WorkloadUpdateStages.Params, h.Clock.UtcNow, PipelineDirection.Forward, StageEntryState.Done, null),
                new StageEntry(WorkloadUpdateStages.Sidecar, h.Clock.UtcNow, PipelineDirection.Forward, StageEntryState.Done, null),
            ],
            PipelineDirection.Forward,
            PipelineState.Running));
        var saves = h.Repo.UpdateCallCount;

        var r = await h.Orchestrator.AdvanceAsync(intent);

        r.Progressed.Should().BeFalse();
        r.Detail.Should().Contain("ahead of its phase");
        h.Repo.UpdateCallCount.Should().Be(saves);
        h.Provider.AppliedInOrder.Should().BeEmpty();
    }

    [Fact]
    public async Task An_unreadable_journal_is_never_read_as_a_fresh_start()
    {
        var h = new OrchestratorHarness(OneWorkload());
        var intent = await h.StageManualAsync(OneWorkload());
        var unreadable = new PipelineJournalDocument { Unreadable = true };
        Set(intent, nameof(WorkloadUpdateIntent.StageJournalDocument), unreadable);
        var saves = h.Repo.UpdateCallCount;

        var result = await h.Orchestrator.AdvanceAsync(intent);

        result.Progressed.Should().BeFalse();
        h.Repo.UpdateCallCount.Should().Be(saves);
        intent.StageJournalDocument.Should().BeSameAs(unreadable);
    }

    [Fact]
    public async Task A_journal_working_in_a_stage_this_build_lacks_is_ended_where_it_stands()
    {
        var h = new OrchestratorHarness(OneWorkload());
        var intent = await h.StageManualAsync(OneWorkload());
        foreach (var phase in (WorkloadUpdatePhase[])[WorkloadUpdatePhase.ApplyingSidecar, WorkloadUpdatePhase.HealthGate, WorkloadUpdatePhase.SwappingModule])
        {
            intent.AdvanceTo(phase, h.Clock.UtcNow);
        }

        intent.RecordStageJournal(new PipelineJournal(
            [new StageEntry("stage.host.gone", h.Clock.UtcNow, PipelineDirection.Forward, StageEntryState.Working, null)],
            PipelineDirection.Forward,
            PipelineState.Running));

        var result = await h.Orchestrator.AdvanceAsync(intent);

        result.IsTerminal.Should().BeTrue();
        intent.Outcome.Should().Be(WorkloadUpdateOutcome.RollbackFailed);
        intent.Error.Should().Contain("'stage.host.gone'");
        h.HostStage.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task A_failure_past_the_point_of_no_return_ends_the_walk_saying_why()
    {
        var h = new OrchestratorHarness(OneWorkload());
        var orchestrator = h.OrchestratorWith(h.Gate, h.Clock, h.Options,
            new FailingStage("stage.host.after-the-swap", StagePlacement.After("stage.host.plugin-roll"), "the host refused"));
        var intent = await h.StageManualAsync(OneWorkload());

        await orchestrator.WalkAsync(intent);

        intent.Phase.Should().Be(WorkloadUpdatePhase.RollbackFailed);
        intent.Error.Should().Contain("the host refused").And.Contain("irreversible");
        intent.Steps.Should().OnlyContain(step => step.State == WorkloadUpdateStepState.Failed);
    }

    [Fact]
    public async Task One_walk_hands_each_finished_stage_to_the_next_and_stops_on_one_still_working()
    {
        var h = new OrchestratorHarness(OneWorkload());
        var intent = await h.StageManualAsync(OneWorkload());
        h.Gate.DefaultVerdict = WorkloadHealthGateVerdict.Pending;

        var result = await h.Orchestrator.WalkAsync(intent);

        result.Progressed.Should().BeTrue("two stages finished before the gate began to wait");
        result.Phase.Should().Be(WorkloadUpdatePhase.HealthGate);
        h.Provider.AppliedInOrder.Should().Equal("gateway");
        h.Gate.Requests.Should().ContainSingle("a working stage is asked once per walk, never spun on");
    }

    [Fact]
    public async Task A_walk_that_could_not_move_reports_that_it_stayed()
    {
        var h = new OrchestratorHarness(OneWorkload());
        var intent = await h.StageManualAsync(OneWorkload());
        h.Gate.DefaultVerdict = WorkloadHealthGateVerdict.Pending;
        await h.Orchestrator.WalkAsync(intent);

        var result = await h.Orchestrator.WalkAsync(intent);

        result.Progressed.Should().BeFalse();
    }

    private static void Set(object entity, string property, object? value)
        => entity.GetType().GetProperty(property, BindingFlags.Instance | BindingFlags.Public)!.SetValue(entity, value);

    [Fact]
    public async Task Entering_the_point_of_no_return_is_warned_once()
    {
        var h = new OrchestratorHarness(OneWorkload());
        var intent = await h.StageManualAsync(OneWorkload());
        h.HostStage.KeepWorking();

        await h.Orchestrator.WalkAsync(intent);
        await h.Orchestrator.WalkAsync(intent);

        h.OrchestratorLog.Entries.Where(e => e.Level == Microsoft.Extensions.Logging.LogLevel.Warning)
            .Should().ContainSingle(e => e.Text.Contains("can no longer be rolled back"))
            .Which.Text.Should().Contain("'stage.host.plugin-roll'").And.Contain("a certified N+1 may have forward-migrated data");
    }

    [Fact]
    public async Task A_walk_to_the_end_warns_once()
    {
        var h = new OrchestratorHarness(OneWorkload());
        var intent = await h.StageManualAsync(OneWorkload());

        for (var walks = 0; walks < 10 && !intent.IsTerminal; walks++)
        {
            await h.Orchestrator.WalkAsync(intent);
        }

        intent.Outcome.Should().Be(WorkloadUpdateOutcome.Completed);
        h.OrchestratorLog.Entries.Where(e => e.Text.Contains("can no longer be rolled back")).Should().ContainSingle();
    }

    [Theory]
    [InlineData("stage.sidecar")]
    [InlineData("stage.commit-version")]
    public async Task A_stage_whose_entry_loses_to_an_abandon_is_never_called(string stageId)
    {
        var h = new OrchestratorHarness(OneWorkload());
        var intent = await h.StageManualAsync(OneWorkload());
        h.Repo.FailUpdate = row => row.ReadStageJournal().Value is { Entries: [.., { State: StageEntryState.Working } last] }
            && last.StageId == stageId
                ? new WorkloadConcurrencyConflictException("an abandon saved first")
                : null;

        var walk = async () => await h.Orchestrator.WalkAsync(intent);

        await walk.Should().ThrowAsync<WorkloadConcurrencyConflictException>();
        if (stageId == WorkloadUpdateStages.Sidecar)
        {
            h.Provider.AppliedInOrder.Should().BeEmpty();
        }
        else
        {
            h.Effects.CommitInstalledVersionCount.Should().Be(0);
        }
    }

    private static async Task<(OrchestratorHarness H, WorkloadUpdateIntent Intent)> AbandonedInTheSidecarAsync()
    {
        var h = new OrchestratorHarness(OneWorkload());
        var intent = await h.StageManualAsync(OneWorkload());
        h.Provider.ApplyBehaviour["gateway"] = spec => spec.SpecHash == "sha-new"
            ? WorkloadApplyResult.Failed(WorkloadApplyReason.ProviderUnreachable, "provider unreachable")
            : WorkloadApplyResult.Applied(spec.SpecHash);
        await h.Orchestrator.WalkAsync(intent);
        intent.ReadStageJournal().Value!.Entries[^1].Should().Match<StageEntry>(entry =>
            entry.StageId == WorkloadUpdateStages.Sidecar && entry.State == StageEntryState.Working);
        intent.Abandon("operator gave up", h.Clock.UtcNow);
        return (h, intent);
    }

    private static bool IsTheRestore(string entry) => entry.StartsWith("apply:gateway:sha-old", StringComparison.Ordinal);

    [Fact]
    public async Task An_abandons_turn_is_saved_before_the_first_down_runs()
    {
        var (h, intent) = await AbandonedInTheSidecarAsync();
        PipelineJournal? savedWhenRestored = null;
        h.Provider.ApplyBehaviour["gateway"] = spec =>
        {
            savedWhenRestored ??= h.Repo.SavedJournals[^1];
            return WorkloadApplyResult.Applied(spec.SpecHash);
        };

        await h.Orchestrator.AdvanceAsync(intent);

        h.Log.Entries.Should().Contain(entry => IsTheRestore(entry), "the sidecar's Down restored the previous spec");
        savedWhenRestored!.Direction.Should().Be(PipelineDirection.Compensating, "the turn was saved before the Down");
    }

    [Fact]
    public async Task An_abandons_turn_that_loses_its_save_undoes_nothing()
    {
        var (h, intent) = await AbandonedInTheSidecarAsync();
        h.Repo.FailUpdate = row => row.ReadStageJournal().Value is { Direction: PipelineDirection.Compensating }
            ? new WorkloadConcurrencyConflictException("the second abandon saved first")
            : null;

        var tick = async () => await h.Orchestrator.AdvanceAsync(intent);

        await tick.Should().ThrowAsync<WorkloadConcurrencyConflictException>();
        h.Log.Entries.Should().NotContain(entry => IsTheRestore(entry), "no Down ran before the turn was on the row");
        h.Log.Entries.Should().NotContain("restore-params");
    }

    [Fact]
    public async Task Entering_an_irreversible_stage_after_one_this_build_does_not_register_still_warns()
    {
        var h = new OrchestratorHarness(OneWorkload());
        var shared = new ReversibleHostStage("stage.host.a-shared");
        var orchestrator = h.OrchestratorWith(h.Gate, h.Clock, h.Options, shared);
        var intent = await h.StageManualAsync(OneWorkload());
        foreach (var phase in (WorkloadUpdatePhase[])[WorkloadUpdatePhase.ApplyingSidecar, WorkloadUpdatePhase.HealthGate, WorkloadUpdatePhase.SwappingModule])
        {
            intent.AdvanceTo(phase, h.Clock.UtcNow);
        }

        intent.RecordStageJournal(new PipelineJournal(
            [
                .. ((string[])[WorkloadUpdateStages.Params, WorkloadUpdateStages.Sidecar, WorkloadUpdateStages.OperatorRedeploy,
                    WorkloadUpdateStages.HealthGate, WorkloadUpdateStages.CommitVersion, "stage.host.retired", shared.Id])
                    .Select(id => new StageEntry(id, h.Clock.UtcNow, PipelineDirection.Forward, StageEntryState.Done, null)),
            ],
            PipelineDirection.Forward,
            PipelineState.Running));

        await orchestrator.AdvanceAsync(intent);

        h.HostStage.Calls.Should().ContainSingle("the irreversible host stage was entered and called");
        h.OrchestratorLog.Entries.Should().ContainSingle(e => e.Text.Contains("can no longer be rolled back"));
    }

    private sealed class ReversibleHostStage(string id) : IStage<WorkloadUpdateStageContext>
    {
        public string Id => id;
        public StagePlacement Placement => StagePlacement.After(WorkloadUpdateStages.CommitVersion, -1);
        public StageReversibility Reversibility => StageReversibility.Reversible;
        public StageBudget Budget => StageBudget.None;
        public ValueTask<StageOutcome> UpAsync(WorkloadUpdateStageContext context, CancellationToken cancellationToken)
            => ValueTask.FromResult(StageOutcome.Done);
        public ValueTask<StageOutcome> DownAsync(WorkloadUpdateStageContext context, CancellationToken cancellationToken)
            => ValueTask.FromResult(StageOutcome.Done);
    }

    [Fact]
    public async Task A_vetoed_turn_before_the_swap_ends_the_walk_instead_of_throwing()
    {
        var h = new OrchestratorHarness(OneWorkload());
        var intent = await h.StageManualAsync(OneWorkload());
        intent.AdvanceTo(WorkloadUpdatePhase.ApplyingSidecar, h.Clock.UtcNow);
        intent.RecordStageJournal(new PipelineJournal(
            [
                new StageEntry(WorkloadUpdateStages.Params, h.Clock.UtcNow, PipelineDirection.Forward, StageEntryState.Done, null),
                new StageEntry("stage.host.gone", h.Clock.UtcNow, PipelineDirection.Forward, StageEntryState.Done, null),
                new StageEntry(WorkloadUpdateStages.Sidecar, h.Clock.UtcNow, PipelineDirection.Forward, StageEntryState.Working, null),
            ],
            PipelineDirection.Forward,
            PipelineState.Running));
        h.Clock.Advance(h.Options.RollbackTimeout + TimeSpan.FromMinutes(1));
        h.Provider.ApplyBehaviour["gateway"] = _ => Reeve.Workloads.Abstractions.Providers.WorkloadApplyResult.Failed(
            Reeve.Workloads.Abstractions.Providers.WorkloadApplyReason.ProviderUnreachable);

        await h.Orchestrator.AdvanceAsync(intent);

        intent.Outcome.Should().Be(WorkloadUpdateOutcome.RollbackFailed);
        intent.Error.Should().Contain("'stage.host.gone'");
    }

    [Fact]
    public async Task A_forward_phase_over_a_compensating_journal_is_left_untouched()
    {
        var h = new OrchestratorHarness(OneWorkload());
        var intent = await h.StageManualAsync(OneWorkload());
        intent.RecordStageJournal(new PipelineJournal(
            [new StageEntry(WorkloadUpdateStages.Params, h.Clock.UtcNow, PipelineDirection.Forward, StageEntryState.Done, null)],
            PipelineDirection.Compensating,
            PipelineState.Running));
        var saves = h.Repo.UpdateCallCount;

        var r = await h.Orchestrator.AdvanceAsync(intent);

        r.Progressed.Should().BeFalse();
        r.Detail.Should().Contain("Compensating");
        h.Repo.UpdateCallCount.Should().Be(saves);
    }

    [Fact]
    public async Task Entering_the_point_of_no_return_is_not_warned_when_its_entry_is_not_saved()
    {
        var h = new OrchestratorHarness(OneWorkload());
        var intent = await h.StageManualAsync(OneWorkload());
        h.Repo.FailUpdate = row => row.ReadStageJournal().Value is { Entries: [.., { StageId: ScriptedHostStage.StageId }] }
            ? new WorkloadConcurrencyConflictException("an abandon saved first")
            : null;

        var walk = async () => await h.Orchestrator.WalkAsync(intent);

        await walk.Should().ThrowAsync<WorkloadConcurrencyConflictException>();
        h.HostStage.Calls.Should().BeEmpty();
        h.OrchestratorLog.Entries.Should().NotContain(e => e.Text.Contains("can no longer be rolled back"));
    }

    [Fact]
    public async Task An_irreversible_stage_is_entered_on_the_row_before_it_is_first_called()
    {
        var h = new OrchestratorHarness(OneWorkload());
        var intent = await h.StageManualAsync(OneWorkload());
        Reeve.Pipeline.PipelineJournal? savedWhenCalled = null;
        h.HostStage.OnCall = () => savedWhenCalled ??= h.Repo.SavedJournals[^1];

        await h.Orchestrator.WalkAsync(intent);

        savedWhenCalled.Should().NotBeNull();
        savedWhenCalled!.Entries[^1].Should().Match<StageEntry>(entry =>
            entry.StageId == ScriptedHostStage.StageId && entry.State == StageEntryState.Working);
        Reeve.Pipeline.PipelineRunner<WorkloadUpdateStageContext>.CanCompensate(savedWhenCalled, h.Pipeline, out _)
            .Should().BeFalse("an abandon reading this save is refused");
        intent.Outcome.Should().Be(WorkloadUpdateOutcome.Completed);
        h.HostStage.Calls.Should().ContainSingle("entering it first does not call it twice");
    }

    [Fact]
    public async Task An_irreversible_stage_still_working_keeps_the_entry_it_was_entered_under()
    {
        var h = new OrchestratorHarness(OneWorkload());
        var intent = await h.StageManualAsync(OneWorkload());
        h.HostStage.KeepWorking();
        await h.Orchestrator.WalkAsync(intent);
        var entered = intent.ReadStageJournal().Value!.Entries.Single(entry => entry.StageId == ScriptedHostStage.StageId);

        h.Clock.Advance(TimeSpan.FromMinutes(1));
        await h.Orchestrator.WalkAsync(intent);

        intent.ReadStageJournal().Value!.Entries.Where(entry => entry.StageId == ScriptedHostStage.StageId)
            .Should().ContainSingle().Which.EnteredAtUtc.Should().Be(entered.EnteredAtUtc);
        h.HostStage.Calls.Should().HaveCount(2);
    }

    [Fact]
    public async Task An_abandon_over_a_stage_this_build_does_not_register_is_ended_not_unwound()
    {
        var h = new OrchestratorHarness(OneWorkload());
        var intent = await h.StageManualAsync(OneWorkload());
        h.Gate.DefaultVerdict = WorkloadHealthGateVerdict.Pending;
        await h.Orchestrator.WalkAsync(intent);
        var walked = intent.ReadStageJournal().Value!;
        intent.RecordStageJournal(new PipelineJournal(
            [.. walked.Entries.Where(entry => entry.State == StageEntryState.Done),
                new StageEntry("stage.host.newer-build", h.Clock.UtcNow, PipelineDirection.Forward, StageEntryState.Working, null)],
            PipelineDirection.Forward,
            PipelineState.Running));
        intent.Abandon("operator gave up", h.Clock.UtcNow);
        var applied = h.Provider.AppliedInOrder.Count;

        var r = await h.Orchestrator.AdvanceAsync(intent);

        r.IsTerminal.Should().BeTrue();
        intent.Outcome.Should().Be(WorkloadUpdateOutcome.RollbackFailed);
        intent.Error.Should().Contain("'stage.host.newer-build'");
        intent.ReadStageJournal().Value!.Direction.Should().Be(PipelineDirection.Forward);
        h.Provider.AppliedInOrder.Should().HaveCount(applied, "nothing is restored");
    }

    [Fact]
    public async Task A_walk_that_stops_short_of_the_point_of_no_return_warns_nothing()
    {
        var h = new OrchestratorHarness(OneWorkload());
        var intent = await h.StageManualAsync(OneWorkload());
        h.Gate.DefaultVerdict = WorkloadHealthGateVerdict.Pending;

        await h.Orchestrator.WalkAsync(intent);

        h.OrchestratorLog.Entries.Should().NotContain(e => e.Text.Contains("can no longer be rolled back"));
    }

    private sealed record FailingStage(string Id, StagePlacement Placement, string Reason) : IStage<WorkloadUpdateStageContext>
    {
        public StageReversibility Reversibility => StageReversibility.Reversible;

        public StageBudget Budget => StageBudget.None;

        public ValueTask<StageOutcome> UpAsync(WorkloadUpdateStageContext context, CancellationToken cancellationToken)
            => ValueTask.FromResult(StageOutcome.Failed(Reason));

        public ValueTask<StageOutcome> DownAsync(WorkloadUpdateStageContext context, CancellationToken cancellationToken)
            => ValueTask.FromResult(StageOutcome.Done);
    }
}
