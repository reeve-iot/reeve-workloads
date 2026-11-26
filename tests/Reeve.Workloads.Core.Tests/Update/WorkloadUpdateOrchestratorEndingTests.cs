using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Reeve.Pipeline;
using Reeve.Workloads.Abstractions;
using Reeve.Workloads.Abstractions.Domain.Entities;
using Reeve.Workloads.Abstractions.Update;
using Reeve.Workloads.Core.Tests.Groups;
using Reeve.Workloads.Core.Update;
using Reeve.Workloads.Core.Update.Stages;
using Xunit;
using static Reeve.Workloads.Core.Tests.Update.WorkloadUpdatePlanBuilder;

namespace Reeve.Workloads.Core.Tests.Update;

public sealed class WorkloadUpdateOrchestratorEndingTests
{
    private static readonly Guid Instance = InstanceGuid;

    private static WorkloadUpdatePlan OneWorkload() => Plan(Instance, [Item("gateway", Instance, "sha-new", "sha-old")]);

    private static StageEntry Done(string stageId, DateTimeOffset at)
        => new(stageId, at, PipelineDirection.Forward, StageEntryState.Done, null);

    private static async Task<(OrchestratorHarness H, WorkloadUpdateIntent Intent)> PastAStageThisBuildLacksAsync()
    {
        var h = new OrchestratorHarness(OneWorkload());
        var intent = await h.StageManualAsync(OneWorkload());
        foreach (var phase in (WorkloadUpdatePhase[])[WorkloadUpdatePhase.ApplyingSidecar, WorkloadUpdatePhase.HealthGate, WorkloadUpdatePhase.SwappingModule])
        {
            intent.AdvanceTo(phase, h.Clock.UtcNow);
        }

        intent.RecordStageJournal(new PipelineJournal(
            [
                .. ((string[])[WorkloadUpdateStages.Params, WorkloadUpdateStages.Sidecar, WorkloadUpdateStages.OperatorRedeploy,
                    WorkloadUpdateStages.HealthGate, WorkloadUpdateStages.CommitVersion, "stage.host.newer"])
                    .Select(id => Done(id, h.Clock.UtcNow)),
            ],
            PipelineDirection.Forward,
            PipelineState.Running));
        return (h, intent);
    }

    [Fact]
    public async Task A_forward_walk_at_a_stage_this_build_does_not_register_is_ended_where_it_stands()
    {
        var (h, intent) = await PastAStageThisBuildLacksAsync();

        var result = await h.Orchestrator.AdvanceAsync(intent);

        result.IsTerminal.Should().BeTrue();
        intent.Outcome.Should().Be(WorkloadUpdateOutcome.RollbackFailed);
        intent.Error.Should().Contain("'stage.host.newer'").And.Contain("nothing was undone").And.Contain("redeploy");
        intent.Steps.Should().OnlyContain(step => step.State == WorkloadUpdateStepState.Failed);
        var journal = intent.ReadStageJournal().Value!;
        journal.State.Should().Be(PipelineState.Failed);
        journal.Direction.Should().Be(PipelineDirection.Forward, "an ending is not an unwind");
        h.Repo.SavedJournals.Should().NotContain(
            saved => saved!.Entries.Any(entry => entry.StageId == "stage.host.newer" && entry.State == StageEntryState.Working),
            "the pre-save enters only a stage this build can call");
        h.Repo.SavedJournals[^1]!.State.Should().Be(PipelineState.Failed, "the ending is on the row");
    }

    [Fact]
    public async Task Ending_a_committed_cohort_member_saves_under_the_group_fence()
    {
        var plan = OneWorkload();
        var h = new OrchestratorHarness(plan);
        var intent = WorkloadUpdateIntent.Create(Guid.NewGuid(), Instance, "2.5.0", "2.6.0", h.Clock.UtcNow,
            new WorkloadUpdateCohortPlace(Guid.NewGuid(), 0, 1));
        intent.StageSnapshots(new WorkloadUpdateSnapshots
        {
            TargetSpecs = plan.TargetSpecs,
            PreviousSpecs = plan.PreviousSpecs,
        }, h.Clock.UtcNow);
        intent.AdvanceTo(WorkloadUpdatePhase.ApplyingSidecar, h.Clock.UtcNow);
        intent.AdvanceTo(WorkloadUpdatePhase.HealthGate, h.Clock.UtcNow);
        intent.AdvanceTo(WorkloadUpdatePhase.SwappingModule, h.Clock.UtcNow);
        intent.RecordStageJournal(new PipelineJournal(
            [.. ((string[])[WorkloadUpdateStages.Params, WorkloadUpdateStages.Sidecar, WorkloadUpdateStages.OperatorRedeploy,
                WorkloadUpdateStages.HealthGate, WorkloadUpdateStages.CommitVersion, "stage.host.newer"])
                .Select(id => Done(id, h.Clock.UtcNow))],
            PipelineDirection.Forward, PipelineState.Running));
        await h.Repo.AddAsync(intent);
        var groups = new TestGroupRepository(WorkloadGroup.Record("zwave", "source", "2.6.0", h.Clock.UtcNow));
        var instances = new FakeInstances();
        instances.Instances.Add(WorkloadInstance.Create(Guid.NewGuid(), Instance, "zwave", "gateway", h.Clock.UtcNow));
        var orchestrator = new WorkloadUpdateOrchestrator(h.Repo, h.Planner, h.Pipeline, h.Clock,
            h.OrchestratorLog, h.Lifetime, groups, instances);

        var result = await orchestrator.AdvanceAsync(intent);

        result.IsTerminal.Should().BeTrue();
        intent.Outcome.Should().Be(WorkloadUpdateOutcome.RollbackFailed);
        h.Repo.CohortDecisionSaveCount.Should().Be(1);
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
    public async Task A_compensating_walk_at_a_stage_this_build_does_not_register_is_ended_where_it_stands()
    {
        var h = new OrchestratorHarness(OneWorkload());
        var intent = await h.StageManualAsync(OneWorkload());
        intent.AdvanceTo(WorkloadUpdatePhase.ApplyingSidecar, h.Clock.UtcNow);
        intent.Fail("the sidecar never became healthy", h.Clock.UtcNow);
        intent.RecordStageJournal(new PipelineJournal(
            [
                Done(WorkloadUpdateStages.Params, h.Clock.UtcNow),
                Done("stage.host.gone", h.Clock.UtcNow),
            ],
            PipelineDirection.Compensating,
            PipelineState.Running));

        var result = await h.Orchestrator.AdvanceAsync(intent);

        result.IsTerminal.Should().BeTrue();
        intent.Outcome.Should().Be(WorkloadUpdateOutcome.RollbackFailed);
        intent.Error.Should().Contain("'stage.host.gone'")
            .And.Contain("nothing further was undone", "a compensating walk may already have undone some stages");
        var journal = intent.ReadStageJournal().Value!;
        journal.State.Should().Be(PipelineState.Failed);
        journal.Direction.Should().Be(PipelineDirection.Compensating);
    }

    [Fact]
    public async Task A_stopping_replica_leaves_a_walk_at_a_stage_this_build_does_not_register_standing()
    {
        var (h, intent) = await PastAStageThisBuildLacksAsync();
        h.Lifetime.FireStopping();
        var saves = h.Repo.UpdateCallCount;

        var result = await h.Orchestrator.AdvanceAsync(intent);

        result.Progressed.Should().BeFalse();
        result.Detail.Should().Contain("stopping").And.Contain("stage.host.newer");
        intent.IsTerminal.Should().BeFalse();
        h.Repo.UpdateCallCount.Should().Be(saves);
    }

    [Fact]
    public async Task A_stopping_replica_does_not_enter_an_irreversible_stage()
    {
        var h = new OrchestratorHarness(OneWorkload());
        var intent = await h.StageManualAsync(OneWorkload());
        h.Lifetime.FireStopping();

        await h.Orchestrator.WalkAsync(intent);

        h.HostStage.Calls.Should().BeEmpty();
        var journal = intent.ReadStageJournal().Value!;
        journal.Entries.Should().NotContain(entry => entry.StageId == ScriptedHostStage.StageId);
        journal.Entries[^1].Should().Match<StageEntry>(entry =>
            entry.StageId == WorkloadUpdateStages.CommitVersion && entry.State == StageEntryState.Done);
        intent.IsTerminal.Should().BeFalse();

        var saves = h.Repo.UpdateCallCount;
        var again = await h.Orchestrator.AdvanceAsync(intent);

        again.Progressed.Should().BeFalse();
        again.Detail.Should().Contain("stopping").And.Contain(ScriptedHostStage.StageId);
        h.Repo.UpdateCallCount.Should().Be(saves, "a stopping replica saves nothing at the boundary");
    }

    [Fact]
    public async Task A_replica_that_is_not_stopping_enters_the_irreversible_stage()
    {
        var h = new OrchestratorHarness(OneWorkload());
        var intent = await h.StageManualAsync(OneWorkload());

        await h.Orchestrator.WalkAsync(intent);

        h.HostStage.Calls.Should().ContainSingle();
    }

    [Fact]
    public async Task A_stopping_replica_still_enters_a_reversible_stage()
    {
        var h = new OrchestratorHarness(OneWorkload());
        var intent = await h.StageManualAsync(OneWorkload());
        h.Lifetime.FireStopping();

        await h.Orchestrator.AdvanceAsync(intent);

        intent.ReadStageJournal().Value!.Entries.Should().ContainSingle()
            .Which.StageId.Should().Be(WorkloadUpdateStages.Params);
    }

    [Fact]
    public async Task A_stopping_replica_still_calls_an_irreversible_stage_it_already_entered()
    {
        var h = new OrchestratorHarness(OneWorkload());
        var intent = await h.StageManualAsync(OneWorkload());
        h.HostStage.KeepWorking();
        await h.Orchestrator.WalkAsync(intent);
        h.HostStage.Calls.Should().ContainSingle();
        h.Lifetime.FireStopping();

        await h.Orchestrator.AdvanceAsync(intent);

        h.HostStage.Calls.Should().HaveCount(2);
    }

    [Fact]
    public async Task An_abandon_across_a_stage_this_build_does_not_register_is_ended()
    {
        var (h, intent) = await PastAStageThisBuildLacksAsync();
        intent.Abandon("operator gave up", h.Clock.UtcNow);

        var result = await h.Orchestrator.AdvanceAsync(intent);

        result.IsTerminal.Should().BeTrue();
        intent.Outcome.Should().Be(WorkloadUpdateOutcome.RollbackFailed);
        intent.Error.Should().Contain("'stage.host.newer'");
    }

    [Fact]
    public async Task A_stopping_replica_leaves_an_abandon_across_a_stage_this_build_does_not_register_standing()
    {
        var (h, intent) = await PastAStageThisBuildLacksAsync();
        intent.Abandon("operator gave up", h.Clock.UtcNow);
        h.Lifetime.FireStopping();
        var saves = h.Repo.UpdateCallCount;

        var result = await h.Orchestrator.AdvanceAsync(intent);

        result.Progressed.Should().BeFalse();
        result.Detail.Should().Contain("stopping");
        intent.IsTerminal.Should().BeFalse();
        h.Repo.UpdateCallCount.Should().Be(saves);
    }

    [Fact]
    public async Task A_stopping_replica_still_ends_an_abandon_barred_by_a_known_irreversible_stage()
    {
        var h = new OrchestratorHarness(OneWorkload());
        var intent = await h.StageManualAsync(OneWorkload());
        h.HostStage.KeepWorking();
        await h.Orchestrator.WalkAsync(intent);
        intent.Abandon("operator gave up", h.Clock.UtcNow);
        h.Lifetime.FireStopping();

        var result = await h.Orchestrator.AdvanceAsync(intent);

        result.IsTerminal.Should().BeTrue();
        intent.Outcome.Should().Be(WorkloadUpdateOutcome.RollbackFailed);
    }
}
