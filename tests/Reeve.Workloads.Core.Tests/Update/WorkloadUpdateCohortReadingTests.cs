using FluentAssertions;
using Reeve.Pipeline;
using Reeve.Workloads.Abstractions.Domain.Entities;
using Reeve.Workloads.Abstractions.Update;
using Xunit;

namespace Reeve.Workloads.Core.Tests.Update;

public sealed class WorkloadUpdateCohortReadingTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid CohortId = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");

    private static WorkloadUpdateIntent Member(int ordinal, int size = 2)
        => WorkloadUpdateIntent.Create(Guid.NewGuid(), Guid.NewGuid(), "1.0.0", "2.0.0", Now,
            new WorkloadUpdateCohortPlace(CohortId, ordinal, size));

    private static void Record(WorkloadUpdateIntent member, string stage, PipelineDirection direction = PipelineDirection.Forward)
        => member.RecordStageJournal(new PipelineJournal(
            [new StageEntry(stage, Now, direction, StageEntryState.Working, null)], direction, PipelineState.Running));

    [Fact]
    public void Passed_means_the_forward_commit_version_entry_was_saved()
    {
        var passed = Member(0);
        var notPassed = Member(1);
        Record(passed, WorkloadUpdateStages.CommitVersion);
        Record(notPassed, WorkloadUpdateStages.HealthGate);
        var reading = WorkloadUpdateCohortReading.Create(WorkloadGroup.Record("alpha", "source", "1.0.0", Now), [passed, notPassed]);

        reading.Passed(passed).Should().BeTrue();
        reading.Passed(notPassed).Should().BeFalse();
    }

    [Fact]
    public void TurnOf_waits_for_every_lower_ordinal_and_names_the_nearest_blocker()
    {
        var first = Member(0, 3);
        var second = Member(1, 3);
        var third = Member(2, 3);
        Record(first, WorkloadUpdateStages.CommitVersion);
        Record(second, WorkloadUpdateStages.Sidecar);
        var reading = WorkloadUpdateCohortReading.Create(WorkloadGroup.Record("alpha", "source", "1.0.0", Now), [first, second, third]);

        reading.TurnOf(second).Should().BeTrue();
        reading.TurnOf(third).Should().BeFalse();
        reading.NearestBlockingMember(third).Should().BeSameAs(second);
        reading.BarrierOpen.Should().BeFalse();
    }

    [Fact]
    public void Barrier_requires_every_member_and_the_declared_size()
    {
        var onlyRead = Member(0);
        Record(onlyRead, WorkloadUpdateStages.CommitVersion);
        var reading = WorkloadUpdateCohortReading.Create(WorkloadGroup.Record("alpha", "source", "1.0.0", Now), [onlyRead]);

        reading.BarrierOpen.Should().BeFalse("a truncated read must not open the barrier");
        reading.Failing.Should().BeTrue();
    }

    [Fact]
    public void Third_group_version_is_diverged_and_failing()
    {
        var member = Member(0, 1);
        var group = WorkloadGroup.Record("alpha", "source", "1.0.0", Now);
        group.MoveTo("3.0.0", Now.AddMinutes(1));
        var reading = WorkloadUpdateCohortReading.Create(group, [member]);

        reading.Diverged.Should().BeTrue();
        reading.Failing.Should().BeTrue();
        reading.Committed.Should().BeFalse();
    }

    [Fact]
    public void An_unaccounted_member_makes_the_cohort_fail_closed()
    {
        var member = Member(0, 1);
        var reading = WorkloadUpdateCohortReading.Create(
            WorkloadGroup.Record("alpha", "source", "1.0.0", Now), [member], new HashSet<Guid> { member.Id });

        reading.Failing.Should().BeTrue();
    }

    [Fact]
    public void Committed_excludes_failing_even_when_a_member_is_compensating()
    {
        var member = Member(0, 1);
        Record(member, WorkloadUpdateStages.Params, PipelineDirection.Compensating);
        var group = WorkloadGroup.Record("alpha", "source", "1.0.0", Now);
        group.MoveTo("2.0.0", Now.AddMinutes(1));
        var reading = WorkloadUpdateCohortReading.Create(group, [member]);

        reading.Committed.Should().BeTrue();
        reading.Failing.Should().BeFalse();
    }

    [Fact]
    public void A_cohort_is_failing_when_only_a_sibling_compensates()
    {
        var healthy = Member(0, 2);
        var turned = Member(1, 2);
        Record(healthy, WorkloadUpdateStages.CommitVersion);
        Record(turned, WorkloadUpdateStages.HealthGate, PipelineDirection.Compensating);
        var reading = WorkloadUpdateCohortReading.Create(WorkloadGroup.Record("alpha", "source", "1.0.0", Now), [healthy, turned]);

        reading.Diverged.Should().BeFalse();
        reading.Members.Count.Should().Be(reading.Size, "no row is missing");
        reading.Failing.Should().BeTrue("a compensating member alone fails the cohort");
        reading.FailingMember.Should().BeSameAs(turned);
    }

    [Fact]
    public void A_healthy_cohort_names_no_failing_member()
    {
        var first = Member(0, 2);
        var second = Member(1, 2);
        Record(first, WorkloadUpdateStages.CommitVersion);
        Record(second, WorkloadUpdateStages.HealthGate);
        var reading = WorkloadUpdateCohortReading.Create(WorkloadGroup.Record("alpha", "source", "1.0.0", Now), [first, second]);

        reading.Failing.Should().BeFalse();
        reading.FailingMember.Should().BeNull();
    }
}
