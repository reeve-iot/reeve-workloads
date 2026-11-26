using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using static Reeve.Workloads.Core.Tests.Update.WorkloadUpdatePlanBuilder;
using Reeve.Pipeline;
using Reeve.Workloads.Abstractions;
using Reeve.Workloads.Abstractions.Contracts;
using Reeve.Workloads.Abstractions.Domain.Entities;
using Reeve.Workloads.Abstractions.Providers;
using Reeve.Workloads.Abstractions.Update;
using Reeve.Workloads.Core.Update;
using Reeve.Workloads.Core.Tests.Groups;
using Xunit;

namespace Reeve.Workloads.Core.Tests.Update;

public sealed class WorkloadUpdateAbandonServiceTests
{
    private static readonly Guid Instance = InstanceGuid;
    private static readonly Guid Actor = Guid.Parse("33333333-3333-3333-3333-333333333333");
    private static readonly DateTimeOffset Now = new(2026, 8, 14, 9, 0, 0, TimeSpan.Zero);

    private sealed class Fixture
    {
        public InMemoryIntentRepository Repo { get; } = new();
        public RecordingNudge Nudge { get; } = new();
        public RecordingAuditWriter Audit { get; } = new();
        public MutableClock Clock { get; } = new(Now);
        public WorkloadUpdateAbandonService Service { get; }

        public Fixture()
        {
            Service = new WorkloadUpdateAbandonService(
                Repo, Nudge, Audit, Clock, new OrchestratorHarness(Plan(Instance, [Item("gateway", Instance, "sha-new", "sha-old")])).Pipeline,
                NullLogger<WorkloadUpdateAbandonService>.Instance,
                NSubstitute.Substitute.For<IWorkloadGroupRepository>(), NSubstitute.Substitute.For<IWorkloadInstanceRepository>());
        }

        public async Task<WorkloadUpdateIntent> SeedAsync(WorkloadUpdatePhase phase, params string[] workloadIds)
        {
            var intent = WorkloadUpdateIntent.Create(Guid.NewGuid(), Instance, "2.5.0", "2.6.0", Now.AddMinutes(-30));
            var t = Now.AddMinutes(-29);
            foreach (var step in PathTo(phase))
            {
                intent.AdvanceTo(step, t);
                t = t.AddSeconds(1);
            }

            foreach (var id in workloadIds.Length == 0 ? ["gateway"] : workloadIds)
            {
                var step = WorkloadUpdateIntentStep.Create(Guid.NewGuid(), intent.Id, id, Now.AddMinutes(-29));
                step.Advance(WorkloadUpdatePhase.HealthGate, WorkloadUpdateStepState.GateWaiting, "awaiting gate", Now.AddMinutes(-29));
                intent.AddStep(step);
            }

            var before = phase switch
            {
                WorkloadUpdatePhase.ApplyingSidecar => ThroughTheCommit[..1],
                WorkloadUpdatePhase.HealthGate => ThroughTheCommit[..3],
                WorkloadUpdatePhase.SwappingModule => ThroughTheCommit[..4],
                _ => [],
            };
            if (phase is not WorkloadUpdatePhase.RollingBack)
            {
                intent.RecordStageJournal(new PipelineJournal(
                    before.Select(id => new StageEntry(id, t, PipelineDirection.Forward, StageEntryState.Done, null)).ToList(),
                    PipelineDirection.Forward,
                    PipelineState.Running));
            }

            await Repo.AddAsync(intent);
            return intent;
        }

        public Task<WorkloadUpdateIntent> SeedWalkedAsync(params string[] entered)
            => SeedWalkedAsync(WorkloadUpdatePhase.SwappingModule, entered);

        public async Task<WorkloadUpdateIntent> SeedWalkedAsync(WorkloadUpdatePhase phase, params string[] entered)
        {
            var intent = await SeedAsync(phase);
            var at = Now.AddMinutes(-20);
            intent.RecordStageJournal(new PipelineJournal(
                entered.Select((id, i) => new StageEntry(
                    id, at.AddSeconds(i), PipelineDirection.Forward,
                    i == entered.Length - 1 ? StageEntryState.Working : StageEntryState.Done, null)).ToList(),
                PipelineDirection.Forward,
                PipelineState.Running));
            return intent;
        }

        private static IEnumerable<WorkloadUpdatePhase> PathTo(WorkloadUpdatePhase target) => target switch
        {
            WorkloadUpdatePhase.StagingParams => [],
            WorkloadUpdatePhase.ApplyingSidecar => [WorkloadUpdatePhase.ApplyingSidecar],
            WorkloadUpdatePhase.HealthGate => [WorkloadUpdatePhase.ApplyingSidecar, WorkloadUpdatePhase.HealthGate],
            WorkloadUpdatePhase.SwappingModule =>
                [WorkloadUpdatePhase.ApplyingSidecar, WorkloadUpdatePhase.HealthGate, WorkloadUpdatePhase.SwappingModule],
            WorkloadUpdatePhase.Completed =>
                [WorkloadUpdatePhase.ApplyingSidecar, WorkloadUpdatePhase.HealthGate, WorkloadUpdatePhase.SwappingModule, WorkloadUpdatePhase.Completed],
            WorkloadUpdatePhase.RollingBack =>
                [WorkloadUpdatePhase.ApplyingSidecar, WorkloadUpdatePhase.HealthGate, WorkloadUpdatePhase.RollingBack],
            WorkloadUpdatePhase.RolledBack =>
                [WorkloadUpdatePhase.ApplyingSidecar, WorkloadUpdatePhase.HealthGate, WorkloadUpdatePhase.RollingBack, WorkloadUpdatePhase.RolledBack],
            WorkloadUpdatePhase.RollbackFailed =>
                [WorkloadUpdatePhase.ApplyingSidecar, WorkloadUpdatePhase.HealthGate, WorkloadUpdatePhase.RollingBack, WorkloadUpdatePhase.RollbackFailed],
            _ => throw new ArgumentOutOfRangeException(nameof(target)),
        };
    }


    [Theory]
    [InlineData(nameof(WorkloadUpdatePhase.StagingParams))]
    [InlineData(nameof(WorkloadUpdatePhase.ApplyingSidecar))]
    [InlineData(nameof(WorkloadUpdatePhase.HealthGate))]
    public async Task Abandoning_a_pre_swap_intent_starts_a_rollback_and_is_not_finished(string fromName)
    {
        var from = Enum.Parse<WorkloadUpdatePhase>(fromName);

        var f = new Fixture();
        var intent = await f.SeedAsync(from);

        var result = await f.Service.AbandonAsync(intent.Id, Actor);

        result.Status.Should().Be(WorkloadUpdateAbandonStatus.Abandoned);
        result.Phase.Should().Be(nameof(WorkloadUpdatePhase.RollingBack));
        result.Direction.Should().Be(WorkloadUpdateDirection.RollingBack);
        result.Outcome.Should().Be(WorkloadUpdateOutcome.InProgress);
        result.IsTerminal.Should().BeFalse("RollingBack still needs the orchestrator to restore the previous version");
        result.Message.Should().Contain("not finished");
        intent.Phase.Should().Be(WorkloadUpdatePhase.RollingBack);
        intent.Error.Should().Contain(Actor.ToString()).And.Contain(from.ToString());
        f.Repo.UpdateCallCount.Should().Be(1);
    }

    [Fact]
    public async Task Abandoning_a_pre_swap_intent_leaves_the_step_rows_to_the_orchestrator()
    {
        var f = new Fixture();
        var intent = await f.SeedAsync(WorkloadUpdatePhase.HealthGate, "gateway", "metrics");

        await f.Service.AbandonAsync(intent.Id, Actor);

        intent.Steps.Should().OnlyContain(s => s.State == WorkloadUpdateStepState.GateWaiting);
    }

    [Fact]
    public async Task A_successful_abandon_nudges_the_reconciler()
    {
        var f = new Fixture();
        var intent = await f.SeedAsync(WorkloadUpdatePhase.HealthGate);

        await f.Service.AbandonAsync(intent.Id, Actor);

        f.Nudge.Nudges.Should().ContainSingle().Which.Instance.Should().Be(Instance);
    }

    [Fact]
    public async Task A_precommit_cohort_abandon_is_fenced_abandon_and_names_the_cohort_rollback()
    {
        var f = new Fixture();
        var intent = WorkloadUpdateIntent.Create(Guid.NewGuid(), Instance, "2.5.0", "2.6.0", Now,
            new WorkloadUpdateCohortPlace(Guid.NewGuid(), 0, 1));
        intent.RecordStageJournal(PipelineJournal.New());
        await f.Repo.AddAsync(intent);
        var instances = new FakeInstances();
        instances.Instances.Add(WorkloadInstance.Create(Guid.NewGuid(), Instance, "zwave", "gateway", Now));
        var service = new WorkloadUpdateAbandonService(f.Repo, f.Nudge, f.Audit, f.Clock,
            new OrchestratorHarness(Plan(Instance, [Item("gateway", Instance, "sha-new", "sha-old")])).Pipeline,
            NullLogger<WorkloadUpdateAbandonService>.Instance, new FakeGroups("zwave", "2.5.0"), instances);

        var result = await service.AbandonAsync(intent.Id, Actor);

        result.Status.Should().Be(WorkloadUpdateAbandonStatus.Abandoned);
        result.Message.Should().Contain("cohort is being rolled back");
        f.Repo.CohortDecisionSaveCount.Should().Be(1);
    }

    [Fact]
    public async Task Committed_cohort_abandon_is_refused_without_a_fenced_write()
    {
        var f = new Fixture();
        var intent = WorkloadUpdateIntent.Create(Guid.NewGuid(), Instance, "2.5.0", "2.6.0", Now,
            new WorkloadUpdateCohortPlace(Guid.NewGuid(), 0, 1));
        intent.AdvanceTo(WorkloadUpdatePhase.ApplyingSidecar, Now);
        intent.AdvanceTo(WorkloadUpdatePhase.HealthGate, Now);
        intent.AdvanceTo(WorkloadUpdatePhase.SwappingModule, Now);
        intent.RecordStageJournal(new PipelineJournal(
            [new StageEntry(WorkloadUpdateStages.CommitVersion, Now, PipelineDirection.Forward, StageEntryState.Working, null)],
            PipelineDirection.Forward, PipelineState.Running));
        await f.Repo.AddAsync(intent);
        var instances = new FakeInstances();
        instances.Instances.Add(WorkloadInstance.Create(Guid.NewGuid(), Instance, "zwave", "gateway", Now));
        var group = new FakeGroups("zwave", "2.6.0");
        var service = new WorkloadUpdateAbandonService(f.Repo, f.Nudge, f.Audit, f.Clock,
            new OrchestratorHarness(Plan(Instance, [Item("gateway", Instance, "sha-new", "sha-old")])).Pipeline,
            NullLogger<WorkloadUpdateAbandonService>.Instance, group, instances);

        var result = await service.AbandonAsync(intent.Id, Actor);

        result.Status.Should().Be(WorkloadUpdateAbandonStatus.PastThePointOfNoReturn);
        result.Message.Should().Contain("cohort's version commit already landed");
        f.Repo.CohortDecisionSaveCount.Should().Be(0);
    }

    private sealed class FakeGroups(string id, string? version) : IWorkloadGroupRepository
    {
        private readonly WorkloadGroup _group = WorkloadGroup.Record(id, "source", version, Now);
        public Task<WorkloadGroup?> GetAsync(string groupId, CancellationToken cancellationToken = default)
            => Task.FromResult<WorkloadGroup?>(string.Equals(groupId, id, StringComparison.OrdinalIgnoreCase) ? _group : null);
        public Task<bool> TryAddAsync(WorkloadGroup group, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
        public Task UpdateAsync(WorkloadGroup group, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }

    [Fact]
    public async Task A_successful_abandon_writes_an_audit_entry_naming_both_phases()
    {
        var f = new Fixture();
        var intent = await f.SeedAsync(WorkloadUpdatePhase.HealthGate);

        await f.Service.AbandonAsync(intent.Id, Actor);

        var entry = f.Audit.Entries.Should().ContainSingle().Subject;
        entry.Action.Should().Be(WorkloadUpdateAbandonService.AuditAction);
        entry.ActorUserId.Should().Be(Actor);
        entry.EntityId.Should().Be(intent.Id);
        entry.Metadata.Should().NotBeNull();
        entry.Metadata!.ToString().Should().Contain("HealthGate").And.Contain("RollingBack");
    }


    [Fact]
    public async Task Abandoning_a_rolling_back_intent_terminalizes_it_and_settles_every_step()
    {
        var f = new Fixture();
        var intent = await f.SeedAsync(WorkloadUpdatePhase.RollingBack, "gateway", "metrics");

        var result = await f.Service.AbandonAsync(intent.Id, Actor);

        result.Status.Should().Be(WorkloadUpdateAbandonStatus.Abandoned);
        result.Phase.Should().Be(nameof(WorkloadUpdatePhase.RollbackFailed));
        result.Outcome.Should().Be(WorkloadUpdateOutcome.RollbackFailed);
        result.IsTerminal.Should().BeTrue();
        result.Message.Should().Contain("NOT restored");
        intent.Steps.Should().OnlyContain(s =>
            s.State == WorkloadUpdateStepState.Failed && s.Step == WorkloadUpdatePhase.RollbackFailed);
    }

    [Fact]
    public async Task Terminalizing_frees_the_instance_for_a_fresh_intent()
    {
        var f = new Fixture();
        var intent = await f.SeedAsync(WorkloadUpdatePhase.RollingBack);

        await f.Service.AbandonAsync(intent.Id, Actor);

        (await f.Repo.GetActiveIntentIdAsync(Instance)).Should().BeNull();
        var next = WorkloadUpdateIntent.Create(Guid.NewGuid(), Instance, "2.5.0", "2.6.1", Now);
        await f.Repo.Invoking(r => r.AddAsync(next)).Should().NotThrowAsync();
    }


    private static readonly string[] ThroughTheCommit =
    [
        WorkloadUpdateStages.Params, WorkloadUpdateStages.Sidecar, WorkloadUpdateStages.OperatorRedeploy,
        WorkloadUpdateStages.HealthGate, WorkloadUpdateStages.CommitVersion,
    ];

    [Fact]
    public async Task A_walk_that_entered_an_irreversible_stage_is_refused_with_the_engines_reason()
    {
        var f = new Fixture();
        var intent = await f.SeedWalkedAsync([.. ThroughTheCommit, ScriptedHostStage.StageId]);
        var versionBefore = intent.Version;

        var result = await f.Service.AbandonAsync(intent.Id, Actor);

        result.Status.Should().Be(WorkloadUpdateAbandonStatus.PastThePointOfNoReturn);
        result.Phase.Should().Be(nameof(WorkloadUpdatePhase.SwappingModule));
        result.Direction.Should().Be(WorkloadUpdateDirection.Forward);
        result.CurrentStage.Should().Be(ScriptedHostStage.StageId, "a refusal names where the walk stands");
        result.Message.Should().Contain($"'{ScriptedHostStage.StageId}'").And.Contain(ScriptedHostStage.IrreversibleReason)
            .And.EndWith($"its walk stands at stage '{ScriptedHostStage.StageId}'.", "the refusal names the stage the status shows");
        intent.Phase.Should().Be(WorkloadUpdatePhase.SwappingModule);
        intent.Error.Should().BeNull();
        intent.Version.Should().Be(versionBefore);
        f.Repo.UpdateCallCount.Should().Be(0);
        f.Audit.Entries.Should().BeEmpty();
        f.Nudge.Nudges.Should().BeEmpty();
    }

    [Fact]
    public async Task A_walk_past_the_point_of_no_return_failing_for_a_minute_is_abandoned_and_the_walk_ends_it()
    {
        var h = new OrchestratorHarness(Plan(Instance, [Item("gateway", Instance, "sha-new", "sha-old")]), Now);
        var service = new WorkloadUpdateAbandonService(h.Repo, new RecordingNudge(), new RecordingAuditWriter(), h.Clock,
            h.Pipeline, NullLogger<WorkloadUpdateAbandonService>.Instance,
            NSubstitute.Substitute.For<IWorkloadGroupRepository>(), NSubstitute.Substitute.For<IWorkloadInstanceRepository>());
        var intent = WorkloadUpdateIntent.Create(Guid.NewGuid(), Instance, "2.5.0", "2.6.0", Now.AddMinutes(-30));
        intent.AdvanceTo(WorkloadUpdatePhase.ApplyingSidecar, Now.AddMinutes(-29));
        intent.AdvanceTo(WorkloadUpdatePhase.HealthGate, Now.AddMinutes(-28));
        intent.AdvanceTo(WorkloadUpdatePhase.SwappingModule, Now.AddMinutes(-27));
        intent.RecordStageJournal(new PipelineJournal(
            [.. ((string[])[.. ThroughTheCommit, ScriptedHostStage.StageId]).Select((id, i) => new StageEntry(id,
                Now.AddMinutes(-20), PipelineDirection.Forward,
                id == ScriptedHostStage.StageId ? StageEntryState.Working : StageEntryState.Done, null))],
            PipelineDirection.Forward, PipelineState.Running));
        await h.Repo.AddAsync(intent);
        h.Planner.Override = () => throw new InvalidOperationException("manifest store down");
        intent.RecordWalkFault("plan unavailable: manifest store down", Now - WorkloadUpdateAbandonService.WalkFaultEndsAfter);

        var result = await service.AbandonAsync(intent.Id, Actor);

        result.Status.Should().Be(WorkloadUpdateAbandonStatus.Abandoned);
        result.IsTerminal.Should().BeFalse("the leader's walk ends it, where the stage is called");
        result.Message.Should().Contain("manifest store down").And.Contain("without undoing anything")
            .And.Contain("not finished yet");
        intent.Phase.Should().Be(WorkloadUpdatePhase.RollingBack);
        intent.ReadStageJournal().Value!.Direction.Should().Be(PipelineDirection.Forward, "the walk takes the turn");

        var walked = await h.Orchestrator.AdvanceAsync(intent);

        walked.IsTerminal.Should().BeTrue();
        intent.Outcome.Should().Be(WorkloadUpdateOutcome.RollbackFailed);
        intent.Direction.Should().Be(WorkloadUpdateDirection.Forward, "no rollback ran");
        intent.Error.Should().Contain("manifest store down").And.Contain("nothing was undone")
            .And.Contain(ScriptedHostStage.IrreversibleReason)
            .And.Contain($"Stage '{ScriptedHostStage.StageId}' was still working and may have taken effect");
        intent.WalkFault.Should().BeNull("a settled update has no walk to fail");
        h.HostStage.Calls.Should().BeEmpty();
        h.HostStage.Downs.Should().Be(0);
        h.Planner.BuildCount.Should().Be(0, "the ending needs no plan, which is what kept failing");
    }

    [Fact]
    public void Recording_the_same_walk_fault_again_does_not_move_the_row()
    {
        var intent = WorkloadUpdateIntent.Create(Guid.NewGuid(), Instance, "2.5.0", "2.6.0", Now);
        intent.RecordWalkFault("plan unavailable: manifest store down", Now).Should().BeTrue();
        var version = intent.Version;

        intent.RecordWalkFault("plan unavailable: manifest store down", Now.AddSeconds(5)).Should().BeFalse();
        intent.Version.Should().Be(version);
        intent.RecordWalkFault("plan unavailable: other", Now.AddSeconds(10)).Should().BeTrue();

        intent.WalkFault.Should().Be("plan unavailable: other");
        intent.WalkFaultSinceUtc.Should().Be(Now, "the run of failures began with the first one");
        intent.Version.Should().Be(version + 1);
    }

    [Fact]
    public async Task A_walk_past_the_point_of_no_return_failing_for_less_than_a_minute_is_refused_and_told_when()
    {
        var f = new Fixture();
        var intent = await f.SeedWalkedAsync([.. ThroughTheCommit, ScriptedHostStage.StageId]);
        var since = Now.AddSeconds(-20);
        intent.RecordWalkFault("plan unavailable: manifest store down", since);
        var versionBefore = intent.Version;

        var result = await f.Service.AbandonAsync(intent.Id, Actor);

        result.Status.Should().Be(WorkloadUpdateAbandonStatus.PastThePointOfNoReturn);
        result.Message.Should().Contain($"failing since {since:u}").And.Contain("manifest store down")
            .And.Contain($"{since + WorkloadUpdateAbandonService.WalkFaultEndsAfter:u}")
            .And.EndWith($"its walk stands at stage '{ScriptedHostStage.StageId}'.");
        result.Message.Should().NotContain("keeps going on its own");
        intent.Phase.Should().Be(WorkloadUpdatePhase.SwappingModule);
        intent.Version.Should().Be(versionBefore);
        f.Repo.UpdateCallCount.Should().Be(0);
    }

    [Fact]
    public async Task A_failing_walk_before_the_point_of_no_return_is_still_rolled_back()
    {
        var f = new Fixture();
        var intent = await f.SeedWalkedAsync(WorkloadUpdatePhase.HealthGate,
            WorkloadUpdateStages.Params, WorkloadUpdateStages.Sidecar, WorkloadUpdateStages.OperatorRedeploy,
            WorkloadUpdateStages.HealthGate);
        intent.RecordWalkFault("plan unavailable: manifest store down", Now.AddMinutes(-5));

        var result = await f.Service.AbandonAsync(intent.Id, Actor);

        result.Status.Should().Be(WorkloadUpdateAbandonStatus.Abandoned);
        result.IsTerminal.Should().BeFalse("nothing irreversible ran, so the abandon asks for the rollback");
        intent.Phase.Should().Be(WorkloadUpdatePhase.RollingBack);
    }

    [Fact]
    public async Task A_walk_that_entered_a_stage_this_build_does_not_register_is_ended_where_it_stands()
    {
        var f = new Fixture();
        var intent = await f.SeedWalkedAsync([.. ThroughTheCommit, "stage.host.newer-build"]);

        var result = await f.Service.AbandonAsync(intent.Id, Actor);

        result.Status.Should().Be(WorkloadUpdateAbandonStatus.Abandoned);
        result.IsTerminal.Should().BeTrue();
        result.Outcome.Should().Be(WorkloadUpdateOutcome.RollbackFailed);
        result.Direction.Should().Be(WorkloadUpdateDirection.Forward, "no rollback ran");
        result.Message.Should().Contain("'stage.host.newer-build'").And.Contain("nothing was undone");
        intent.Error.Should().Contain("'stage.host.newer-build'");
        intent.Steps.Should().OnlyContain(step => step.Progress == WorkloadUpdateStepProgress.Failed);
        f.Repo.UpdateCallCount.Should().Be(1, "turned and ended in one save");
        f.Audit.Entries.Should().ContainSingle();
    }

    [Theory]
    [InlineData(StageEntryState.Working)]
    [InlineData(StageEntryState.Done)]
    public async Task A_walk_at_a_stage_no_replica_registers_is_ended_where_it_stands(StageEntryState state)
    {
        var f = new Fixture();
        var intent = await f.SeedAsync(WorkloadUpdatePhase.SwappingModule);
        intent.RecordStageJournal(new PipelineJournal(
            [
                .. ThroughTheCommit.Select(id => new StageEntry(id, Now.AddMinutes(-25), PipelineDirection.Forward, StageEntryState.Done, null)),
                new StageEntry("stage.host.gone", Now.AddMinutes(-20), PipelineDirection.Forward, state, null),
            ],
            PipelineDirection.Forward,
            PipelineState.Running));

        var result = await f.Service.AbandonAsync(intent.Id, Actor);

        result.Status.Should().Be(WorkloadUpdateAbandonStatus.Abandoned);
        result.IsTerminal.Should().BeTrue();
        result.Outcome.Should().Be(WorkloadUpdateOutcome.RollbackFailed);
        result.Message.Should().Contain("'stage.host.gone'").And.Contain("nothing was undone");
        f.Repo.UpdateCallCount.Should().Be(1);
    }

    [Fact]
    public async Task A_walk_past_a_stage_this_build_does_not_register_is_refused_while_this_build_carries_it_on()
    {
        var f = new Fixture();
        var intent = await f.SeedAsync(WorkloadUpdatePhase.SwappingModule);
        intent.RecordStageJournal(new PipelineJournal(
            [
                .. ThroughTheCommit.Select(id => new StageEntry(id, Now.AddMinutes(-25), PipelineDirection.Forward, StageEntryState.Done, null)),
                new StageEntry("stage.host.retired", Now.AddMinutes(-22), PipelineDirection.Forward, StageEntryState.Done, null),
                new StageEntry(WorkloadUpdateStages.CommitParams, Now.AddMinutes(-20), PipelineDirection.Forward, StageEntryState.Working, null),
            ],
            PipelineDirection.Forward,
            PipelineState.Running));

        var result = await f.Service.AbandonAsync(intent.Id, Actor);

        result.Status.Should().Be(WorkloadUpdateAbandonStatus.PastThePointOfNoReturn);
        result.Message.Should().Contain("'stage.host.retired'")
            .And.EndWith($"its walk stands at stage '{WorkloadUpdateStages.CommitParams}'.");
        intent.Phase.Should().Be(WorkloadUpdatePhase.SwappingModule);
        f.Repo.UpdateCallCount.Should().Be(0);
    }

    [Fact]
    public async Task A_rollback_the_walk_refused_to_unwind_can_still_be_given_up()
    {
        var f = new Fixture();
        var intent = await f.SeedWalkedAsync([.. ThroughTheCommit, ScriptedHostStage.StageId]);
        intent.Abandon("abandoned before the walk entered the host stage", Now.AddMinutes(-1));

        var result = await f.Service.AbandonAsync(intent.Id, Actor);

        result.Status.Should().Be(WorkloadUpdateAbandonStatus.Abandoned);
        result.IsTerminal.Should().BeTrue();
        intent.Phase.Should().Be(WorkloadUpdatePhase.RollbackFailed);
    }

    [Fact]
    public async Task A_walk_past_the_commit_that_entered_nothing_irreversible_is_rolled_back()
    {
        var f = new Fixture();
        var intent = await f.SeedWalkedAsync(ThroughTheCommit);

        var result = await f.Service.AbandonAsync(intent.Id, Actor);

        result.Status.Should().Be(WorkloadUpdateAbandonStatus.Abandoned);
        result.Phase.Should().Be(nameof(WorkloadUpdatePhase.RollingBack));
        result.IsTerminal.Should().BeFalse();
        intent.Phase.Should().Be(WorkloadUpdatePhase.RollingBack);
    }

    [Fact]
    public async Task An_update_parked_on_the_operators_redeploy_can_be_abandoned()
    {
        var f = new Fixture();
        var intent = await f.SeedWalkedAsync(WorkloadUpdatePhase.ApplyingSidecar,
            WorkloadUpdateStages.Params, WorkloadUpdateStages.Sidecar, WorkloadUpdateStages.OperatorRedeploy);

        var result = await f.Service.AbandonAsync(intent.Id, Actor);

        result.Status.Should().Be(WorkloadUpdateAbandonStatus.Abandoned);
        intent.Phase.Should().Be(WorkloadUpdatePhase.RollingBack);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_row_nothing_accounts_for_is_ended_where_it_stands(bool unreadable)
    {
        var f = new Fixture();
        var intent = await f.SeedAsync(WorkloadUpdatePhase.ApplyingSidecar);
        if (unreadable)
        {
            typeof(WorkloadUpdateIntent).GetProperty(nameof(WorkloadUpdateIntent.StageJournalDocument))!
                .SetValue(intent, new Reeve.Workloads.Abstractions.Domain.Entities.Documents.PipelineJournalDocument { Unreadable = true });
        }
        else
        {
            intent.RecordStageJournal(new PipelineJournal(
                [.. ((string[])[WorkloadUpdateStages.Params, WorkloadUpdateStages.Sidecar, WorkloadUpdateStages.HealthGate])
                    .Select(id => new StageEntry(id, Now, PipelineDirection.Forward, StageEntryState.Done, null))],
                PipelineDirection.Forward,
                PipelineState.Running));
        }

        var result = await f.Service.AbandonAsync(intent.Id, Actor);

        result.Status.Should().Be(WorkloadUpdateAbandonStatus.Abandoned);
        result.IsTerminal.Should().BeTrue();
        result.Outcome.Should().Be(WorkloadUpdateOutcome.RollbackFailed);
        result.Message.Should().Contain(unreadable ? "unreadable" : "ahead of its phase").And.Contain("nothing was undone");
        f.Repo.UpdateCallCount.Should().Be(1);
    }

    [Theory]
    [InlineData(nameof(WorkloadUpdatePhase.Completed))]
    [InlineData(nameof(WorkloadUpdatePhase.RolledBack))]
    [InlineData(nameof(WorkloadUpdatePhase.RollbackFailed))]
    public async Task An_already_settled_intent_is_refused(string fromName)
    {
        var from = Enum.Parse<WorkloadUpdatePhase>(fromName);

        var f = new Fixture();
        var intent = await f.SeedAsync(from);
        var versionBefore = intent.Version;

        var result = await f.Service.AbandonAsync(intent.Id, Actor);

        result.Status.Should().Be(WorkloadUpdateAbandonStatus.AlreadyTerminal);
        result.Phase.Should().Be(fromName);
        intent.Version.Should().Be(versionBefore);
        f.Repo.UpdateCallCount.Should().Be(0);
        f.Audit.Entries.Should().BeEmpty();
    }

    [Fact]
    public async Task An_unknown_intent_is_not_found()
    {
        var f = new Fixture();

        var result = await f.Service.AbandonAsync(Guid.NewGuid(), Actor);

        result.Status.Should().Be(WorkloadUpdateAbandonStatus.NotFound);
        result.IntentId.Should().BeNull();
        f.Repo.UpdateCallCount.Should().Be(0);
    }

    [Fact]
    public async Task A_failing_nudge_does_not_fail_the_abandon()
    {
        var f = new Fixture();
        f.Nudge.Throw = new InvalidOperationException("bus down");
        var intent = await f.SeedAsync(WorkloadUpdatePhase.HealthGate);

        var result = await f.Service.AbandonAsync(intent.Id, Actor);

        result.Status.Should().Be(WorkloadUpdateAbandonStatus.Abandoned);
        intent.Phase.Should().Be(WorkloadUpdatePhase.RollingBack);
    }

    [Fact]
    public async Task A_failing_audit_write_does_not_fail_the_abandon()
    {
        var f = new Fixture();
        f.Audit.Throw = new InvalidOperationException("audit store down");
        var intent = await f.SeedAsync(WorkloadUpdatePhase.HealthGate);

        var result = await f.Service.AbandonAsync(intent.Id, Actor);

        result.Status.Should().Be(WorkloadUpdateAbandonStatus.Abandoned);
        intent.Phase.Should().Be(WorkloadUpdatePhase.RollingBack);
        f.Nudge.Nudges.Should().ContainSingle("the nudge must still run after a lost audit row");
    }

    [Fact]
    public async Task Abandoning_a_wedged_health_gate_really_restores_the_previous_version()
    {
        var plan = Plan(Instance, [
            Item("gateway", Instance, "sha-gw-new", "sha-gw-old"),
            Item("metrics", Instance, "sha-mx-new", "sha-mx-old"),
        ]);
        var h = new OrchestratorHarness(plan);
        var intent = await h.StageManualAsync(plan);
        h.Gate.DefaultVerdict = WorkloadHealthGateVerdict.Pending;
        await h.Orchestrator.AdvanceAsync(intent);
        await h.Orchestrator.AdvanceAsync(intent);
        await h.Orchestrator.AdvanceAsync(intent);
        intent.Phase.Should().Be(WorkloadUpdatePhase.HealthGate);
        h.Provider.AppliedInOrder.Clear();

        var service = new WorkloadUpdateAbandonService(
            h.Repo, new RecordingNudge(), new RecordingAuditWriter(), h.Clock, h.Pipeline,
            NullLogger<WorkloadUpdateAbandonService>.Instance,
            NSubstitute.Substitute.For<IWorkloadGroupRepository>(), NSubstitute.Substitute.For<IWorkloadInstanceRepository>());

        var result = await service.AbandonAsync(intent.Id, Actor);
        result.Phase.Should().Be(nameof(WorkloadUpdatePhase.RollingBack));

        h.Gate.DefaultVerdict = WorkloadHealthGateVerdict.Passed;
        await h.Orchestrator.WalkAsync(intent);

        intent.Phase.Should().Be(WorkloadUpdatePhase.RolledBack);
        intent.EvidenceDocument.Should().NotBeNull("the update captures evidence before any teardown, abandon or not");
        h.Provider.AppliedInOrder.Should().Equal("metrics", "gateway");
        (await h.Repo.GetActiveIntentIdAsync(Instance)).Should().BeNull();
    }

    [Fact]
    public async Task Abandoning_twice_walks_the_wedged_rollback_to_a_terminal_state()
    {
        var plan = Plan(Instance, [Item("gateway", Instance, "sha-gw-new", "sha-gw-old")]);
        var h = new OrchestratorHarness(plan);
        var intent = await h.StageManualAsync(plan);
        h.Gate.DefaultVerdict = WorkloadHealthGateVerdict.Pending;
        await h.Orchestrator.AdvanceAsync(intent);
        await h.Orchestrator.AdvanceAsync(intent);

        var service = new WorkloadUpdateAbandonService(
            h.Repo, new RecordingNudge(), new RecordingAuditWriter(), h.Clock, h.Pipeline,
            NullLogger<WorkloadUpdateAbandonService>.Instance,
            NSubstitute.Substitute.For<IWorkloadGroupRepository>(), NSubstitute.Substitute.For<IWorkloadInstanceRepository>());

        (await service.AbandonAsync(intent.Id, Actor)).Phase.Should().Be(nameof(WorkloadUpdatePhase.RollingBack));
        await h.Orchestrator.AdvanceAsync(intent);
        intent.Phase.Should().Be(WorkloadUpdatePhase.RollingBack);

        var second = await service.AbandonAsync(intent.Id, Actor);

        second.Phase.Should().Be(nameof(WorkloadUpdatePhase.RollbackFailed));
        second.IsTerminal.Should().BeTrue();
        intent.EvidenceDocument.Should().NotBeNull("the evidence captured on the way in survives the terminalization");
        (await h.Repo.GetActiveIntentIdAsync(Instance)).Should().BeNull();
    }
}
