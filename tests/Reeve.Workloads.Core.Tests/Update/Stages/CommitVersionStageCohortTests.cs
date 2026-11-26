using FluentAssertions;
using NSubstitute;
using Reeve.Pipeline;
using Reeve.Workloads.Abstractions;
using Reeve.Workloads.Abstractions.Domain.Entities;
using Reeve.Workloads.Abstractions.Update;
using Reeve.Workloads.Core.Update.Stages;

namespace Reeve.Workloads.Core.Tests.Update.Stages;

public sealed class CommitVersionStageCohortTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Up_rereads_members_and_commits_once_when_the_fresh_read_opens_the_barrier()
    {
        var plan = StageContextFactory.DefaultPlan();
        var cohortId = Guid.NewGuid();
        var first = Member(plan.GroupInstanceId, cohortId, 0, 2, WorkloadUpdateStages.CommitVersion, plan);
        var firstAtTheTick = Member(plan.GroupInstanceId, cohortId, 0, 2, WorkloadUpdateStages.HealthGate, plan);
        var current = Member(Guid.NewGuid(), cohortId, 1, 2, WorkloadUpdateStages.CommitVersion, plan);
        var group = WorkloadGroup.Record(plan.GroupId, plan.SourceKey, plan.FromVersion, Now);
        var stale = WorkloadUpdateCohortReading.Create(group, [firstAtTheTick, current]);
        stale.BarrierOpen.Should().BeFalse("the tick read the other member still at its gate");
        var context = WorkloadUpdateStageContexts.For(current, plan, stale, group, Now);
        var repo = Substitute.For<IWorkloadUpdateIntentRepository>();
        repo.GetCohortAsync(cohortId, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<WorkloadUpdateIntent>>([first, current]));
        var log = new CallLog();
        var effects = new RecordingEffects(log);
        var rows = new RecordingRowEffects(log);
        var stage = new CommitVersionStage(effects, rows, repo);

        var outcome = await stage.UpAsync(context, CancellationToken.None);

        outcome.Kind.Should().Be(StageOutcomeKind.Done);
        await repo.Received(1).GetCohortAsync(cohortId, Arg.Any<CancellationToken>());
        effects.CommitInstalledVersionCount.Should().Be(1);
        effects.LastCommittedGroupId.Should().Be(plan.GroupId);
        log.Entries.Should().Contain($"commit-installed-version:{plan.ToVersion}");
        rows.EnsureCount.Should().Be(1);
    }

    [Fact]
    public async Task Up_fails_naming_the_member_that_compensates_in_the_fresh_read_not_the_ticks()
    {
        var plan = StageContextFactory.DefaultPlan();
        var cohortId = Guid.NewGuid();
        var siblingId = Guid.Parse("cccccccc-3333-3333-3333-333333333333");
        var sibling = Member(siblingId, cohortId, 0, 3, WorkloadUpdateStages.CommitVersion, plan);
        var siblingTurned = Member(siblingId, cohortId, 0, 3, WorkloadUpdateStages.CommitVersion, plan);
        siblingTurned.RecordStageJournal(new PipelineJournal(
            [new StageEntry(WorkloadUpdateStages.CommitVersion, Now, PipelineDirection.Forward, StageEntryState.Working, null)],
            PipelineDirection.Compensating, PipelineState.Running));
        var bystander = Member(Guid.NewGuid(), cohortId, 1, 3, WorkloadUpdateStages.CommitVersion, plan);
        var current = Member(Guid.NewGuid(), cohortId, 2, 3, WorkloadUpdateStages.CommitVersion, plan);
        var group = WorkloadGroup.Record(plan.GroupId, plan.SourceKey, plan.FromVersion, Now);
        var tickReading = WorkloadUpdateCohortReading.Create(group, [sibling, bystander, current]);
        tickReading.Failing.Should().BeFalse("the tick read every member going forward");
        var context = WorkloadUpdateStageContexts.For(current, plan, tickReading, group, Now);
        var repo = Substitute.For<IWorkloadUpdateIntentRepository>();
        repo.GetCohortAsync(cohortId, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<WorkloadUpdateIntent>>([siblingTurned, bystander, current]));
        var effects = new RecordingEffects(new CallLog());
        var rows = new RecordingRowEffects(new CallLog());

        var outcome = await new CommitVersionStage(effects, rows, repo).UpAsync(context, CancellationToken.None);

        outcome.Kind.Should().Be(StageOutcomeKind.Failed);
        outcome.Detail.Should().Contain(siblingId.ToString(), "the message names the member that actually failed");
        effects.CommitInstalledVersionCount.Should().Be(0);
        rows.EnsureCount.Should().Be(0);
    }

    [Fact]
    public async Task Barrier_waits_after_ensuring_this_members_rows()
    {
        var plan = StageContextFactory.DefaultPlan();
        var cohortId = Guid.NewGuid();
        var first = Member(plan.GroupInstanceId, cohortId, 0, 2, WorkloadUpdateStages.CommitVersion, plan);
        var current = Member(Guid.NewGuid(), cohortId, 1, 2, WorkloadUpdateStages.HealthGate, plan);
        var group = WorkloadGroup.Record(plan.GroupId, plan.SourceKey, plan.FromVersion, Now);
        var tickReading = WorkloadUpdateCohortReading.Create(group, [first, current]);
        var context = WorkloadUpdateStageContexts.For(current, plan, tickReading, group, Now);
        var repo = Substitute.For<IWorkloadUpdateIntentRepository>();
        repo.GetCohortAsync(cohortId, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<WorkloadUpdateIntent>>([first, current]));
        var effects = new RecordingEffects(new CallLog());
        var rows = new RecordingRowEffects(new CallLog());
        var stage = new CommitVersionStage(effects, rows, repo);

        var outcome = await stage.UpAsync(context, CancellationToken.None);

        outcome.Kind.Should().Be(StageOutcomeKind.Working);
        outcome.Detail.Should().Contain("waiting for 1 member");
        rows.EnsureCount.Should().Be(1);
        effects.CommitInstalledVersionCount.Should().Be(0);
    }

    [Fact]
    public async Task Up_is_done_when_the_group_row_already_reads_the_cohort_target()
    {
        var plan = StageContextFactory.DefaultPlan();
        var cohortId = Guid.NewGuid();
        var member = Member(plan.GroupInstanceId, cohortId, 0, 1, WorkloadUpdateStages.CommitVersion, plan);
        var group = WorkloadGroup.Record(plan.GroupId, plan.SourceKey, plan.FromVersion, Now);
        group.MoveTo(plan.ToVersion, Now.AddMinutes(1));
        var context = WorkloadUpdateStageContexts.For(member, plan,
            WorkloadUpdateCohortReading.Create(group, [member]), group, Now);
        var repo = Substitute.For<IWorkloadUpdateIntentRepository>();
        repo.GetCohortAsync(cohortId, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<WorkloadUpdateIntent>>([member]));
        var effects = new RecordingEffects(new CallLog());
        var rows = new RecordingRowEffects(new CallLog());

        var outcome = await new CommitVersionStage(effects, rows, repo).UpAsync(context, CancellationToken.None);

        outcome.Kind.Should().Be(StageOutcomeKind.Done);
        rows.EnsureCount.Should().Be(1);
        effects.CommitInstalledVersionCount.Should().Be(0);
    }

    [Fact]
    public async Task Up_fails_when_another_writer_diverged_the_group_version()
    {
        var plan = StageContextFactory.DefaultPlan();
        var cohortId = Guid.NewGuid();
        var member = Member(plan.GroupInstanceId, cohortId, 0, 1, WorkloadUpdateStages.CommitVersion, plan);
        var group = WorkloadGroup.Record(plan.GroupId, plan.SourceKey, plan.FromVersion, Now);
        group.MoveTo("3.0.0", Now.AddMinutes(1));
        var context = WorkloadUpdateStageContexts.For(member, plan,
            WorkloadUpdateCohortReading.Create(group, [member]), group, Now);
        var repo = Substitute.For<IWorkloadUpdateIntentRepository>();
        repo.GetCohortAsync(cohortId, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<WorkloadUpdateIntent>>([member]));
        var effects = new RecordingEffects(new CallLog());
        var rows = new RecordingRowEffects(new CallLog());

        var outcome = await new CommitVersionStage(effects, rows, repo).UpAsync(context, CancellationToken.None);

        outcome.Kind.Should().Be(StageOutcomeKind.Failed);
        outcome.Detail.Should().Contain("moved to '3.0.0'");
        rows.EnsureCount.Should().Be(0);
        effects.CommitInstalledVersionCount.Should().Be(0);
    }

    [Fact]
    public async Task Down_refuses_after_commit_and_never_moves_group_or_removes_rows()
    {
        var plan = StageContextFactory.DefaultPlan();
        var cohortId = Guid.NewGuid();
        var member = Member(plan.GroupInstanceId, cohortId, 0, 1, WorkloadUpdateStages.CommitVersion, plan);
        var group = WorkloadGroup.Record(plan.GroupId, plan.SourceKey, plan.FromVersion, Now);
        group.MoveTo(plan.ToVersion, Now.AddMinutes(1));
        var reading = WorkloadUpdateCohortReading.Create(group, [member]);
        var context = WorkloadUpdateStageContexts.For(member, plan, reading, group, Now);
        var repo = Substitute.For<IWorkloadUpdateIntentRepository>();
        repo.GetCohortAsync(cohortId, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<WorkloadUpdateIntent>>([member]));
        var effects = new RecordingEffects(new CallLog());
        var rows = new RecordingRowEffects(new CallLog());
        var stage = new CommitVersionStage(effects, rows, repo);

        var outcome = await stage.DownAsync(context, CancellationToken.None);

        outcome.Kind.Should().Be(StageOutcomeKind.Failed);
        outcome.Detail.Should().Contain("one instance cannot be moved back alone");
        effects.CommitInstalledVersionCount.Should().Be(0);
        rows.RemoveCount.Should().Be(0);
    }

    [Fact]
    public async Task Down_before_commit_removes_this_members_rows_without_writing_the_group()
    {
        var plan = StageContextFactory.DefaultPlan();
        var cohortId = Guid.NewGuid();
        var member = Member(plan.GroupInstanceId, cohortId, 0, 1, WorkloadUpdateStages.CommitVersion, plan);
        var group = WorkloadGroup.Record(plan.GroupId, plan.SourceKey, plan.FromVersion, Now);
        var context = WorkloadUpdateStageContexts.For(member, plan,
            WorkloadUpdateCohortReading.Create(group, [member]), group, Now);
        var repo = Substitute.For<IWorkloadUpdateIntentRepository>();
        repo.GetCohortAsync(cohortId, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<WorkloadUpdateIntent>>([member]));
        var effects = new RecordingEffects(new CallLog());
        var rows = new RecordingRowEffects(new CallLog());

        var outcome = await new CommitVersionStage(effects, rows, repo).DownAsync(context, CancellationToken.None);

        outcome.Kind.Should().Be(StageOutcomeKind.Done);
        rows.RemoveCount.Should().Be(1);
        effects.CommitInstalledVersionCount.Should().Be(0);
        group.InstalledVersion.Should().Be(plan.FromVersion);
    }

    private static WorkloadUpdateIntent Member(Guid instance, Guid cohortId, int ordinal, int size, string stage, Reeve.Workloads.Abstractions.Update.WorkloadUpdatePlan plan)
    {
        var intent = WorkloadUpdateIntent.Create(Guid.NewGuid(), instance, plan.FromVersion, plan.ToVersion, Now,
            new WorkloadUpdateCohortPlace(cohortId, ordinal, size));
        intent.RecordStageJournal(new PipelineJournal(
            [new StageEntry(stage, Now, PipelineDirection.Forward, StageEntryState.Working, null)],
            PipelineDirection.Forward, PipelineState.Running));
        return intent;
    }
}
