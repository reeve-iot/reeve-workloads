using FluentAssertions;
using Reeve.Pipeline;
using Reeve.Workloads.Abstractions.Domain.Entities;
using Reeve.Workloads.Abstractions.Update;

namespace Reeve.Workloads.Abstractions.Tests;

public sealed class WorkloadUpdateIntentPositionTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 23, 12, 0, 0, TimeSpan.Zero);

    private static WorkloadUpdateIntent NewIntent()
        => WorkloadUpdateIntent.Create(Guid.NewGuid(), Guid.NewGuid(), "2.5.0", "2.6.0", Now);

    private static PipelineJournal Journal(PipelineDirection direction, params (string Stage, StageEntryState State)[] entries)
        => new(
            entries.Select((entry, i) => new StageEntry(entry.Stage, Now.AddSeconds(i), PipelineDirection.Forward, entry.State, null)).ToList(),
            direction,
            PipelineState.Running);

    [Fact]
    public void A_new_update_is_going_forward_has_called_no_stage_and_has_not_ended()
    {
        var intent = NewIntent();

        intent.Outcome.Should().Be(WorkloadUpdateOutcome.InProgress);
        intent.Direction.Should().Be(WorkloadUpdateDirection.Forward);
        intent.CurrentStage.Should().BeNull();
    }

    [Fact]
    public void The_current_stage_is_the_one_the_walk_last_called()
    {
        var intent = NewIntent();
        intent.RecordStageJournal(Journal(PipelineDirection.Forward,
            (WorkloadUpdateStages.Params, StageEntryState.Done),
            (WorkloadUpdateStages.Sidecar, StageEntryState.Done),
            (WorkloadUpdateStages.HealthGate, StageEntryState.Working)));

        intent.CurrentStage.Should().Be(WorkloadUpdateStages.HealthGate);
        intent.Direction.Should().Be(WorkloadUpdateDirection.Forward);
    }

    [Fact]
    public void An_update_that_failed_forward_is_rolling_back()
    {
        var intent = NewIntent();
        intent.AdvanceTo(WorkloadUpdatePhase.ApplyingSidecar, Now);
        intent.Fail("apply failed", Now);

        intent.Direction.Should().Be(WorkloadUpdateDirection.RollingBack);
        intent.Outcome.Should().Be(WorkloadUpdateOutcome.InProgress);
    }

    [Fact]
    public void A_completed_update_says_so()
    {
        var intent = NewIntent();
        intent.AdvanceTo(WorkloadUpdatePhase.ApplyingSidecar, Now);
        intent.AdvanceTo(WorkloadUpdatePhase.HealthGate, Now);
        intent.AdvanceTo(WorkloadUpdatePhase.SwappingModule, Now);
        intent.Complete(Now);

        intent.Outcome.Should().Be(WorkloadUpdateOutcome.Completed);
        intent.Direction.Should().Be(WorkloadUpdateDirection.Forward);
    }

    [Fact]
    public void A_rolled_back_update_came_backwards()
    {
        var intent = NewIntent();
        intent.AdvanceTo(WorkloadUpdatePhase.ApplyingSidecar, Now);
        intent.Fail("apply failed", Now);
        intent.AdvanceTo(WorkloadUpdatePhase.RolledBack, Now);

        intent.Outcome.Should().Be(WorkloadUpdateOutcome.RolledBack);
        intent.Direction.Should().Be(WorkloadUpdateDirection.RollingBack);
    }

    [Fact]
    public void A_failure_past_the_point_of_no_return_failed_going_forward()
    {
        var intent = NewIntent();
        intent.AdvanceTo(WorkloadUpdatePhase.ApplyingSidecar, Now);
        intent.AdvanceTo(WorkloadUpdatePhase.HealthGate, Now);
        intent.AdvanceTo(WorkloadUpdatePhase.SwappingModule, Now);
        intent.RecordStageJournal(Journal(PipelineDirection.Forward, ("stage.host.plugin-roll", StageEntryState.Failed)));
        intent.MarkRollbackFailed("the host stage failed and cannot be unwound", Now);

        intent.Outcome.Should().Be(WorkloadUpdateOutcome.RollbackFailed);
        intent.Direction.Should().Be(WorkloadUpdateDirection.Forward);
    }

    [Fact]
    public void A_rollback_that_failed_came_backwards()
    {
        var intent = NewIntent();
        intent.AdvanceTo(WorkloadUpdatePhase.ApplyingSidecar, Now);
        intent.Fail("apply failed", Now);
        intent.RecordStageJournal(Journal(PipelineDirection.Compensating, (WorkloadUpdateStages.Sidecar, StageEntryState.Failed)));
        intent.MarkRollbackFailed("the previous version never came back", Now);

        intent.Outcome.Should().Be(WorkloadUpdateOutcome.RollbackFailed);
        intent.Direction.Should().Be(WorkloadUpdateDirection.RollingBack);
    }

    [Fact]
    public void A_failed_rollback_with_no_journal_is_read_as_one_that_came_backwards()
    {
        var intent = NewIntent();
        intent.AdvanceTo(WorkloadUpdatePhase.ApplyingSidecar, Now);
        intent.Fail("apply failed", Now);
        intent.MarkRollbackFailed("gave up", Now);

        intent.Direction.Should().Be(WorkloadUpdateDirection.RollingBack);
        intent.CurrentStage.Should().BeNull();
    }
}
