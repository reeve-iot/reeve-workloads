using FluentAssertions;
using Reeve.Pipeline;
using Reeve.Workloads.Abstractions.Domain.Entities;
using Reeve.Workloads.Abstractions.Update;
using Reeve.Workloads.Core.Update.Stages;

namespace Reeve.Workloads.Core.Tests.Update.Stages;

public sealed class ParamsStageCohortTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Single_intent_still_finishes_without_staging_or_writing()
    {
        var context = await StageContextFactory.BuildAsync();
        var version = context.RequireInternals().Intent.Version;
        var effects = new RecordingEffects(new CallLog());

        var outcome = await new ParamsStage(effects).UpAsync(context, CancellationToken.None);

        outcome.Kind.Should().Be(StageOutcomeKind.Done);
        context.RequireInternals().Intent.Version.Should().Be(version);
        effects.RestoreCount.Should().Be(0);
    }

    [Fact]
    public async Task Cohort_member_waits_for_lower_ordinals_then_stages_target_once()
    {
        var plan = StageContextFactory.DefaultPlan();
        var cohortId = Guid.NewGuid();
        var first = Intent(plan.GroupInstanceId, cohortId, 0, 2);
        var current = Intent(Guid.NewGuid(), cohortId, 1, 2);
        var group = WorkloadGroup.Record(plan.GroupId, plan.SourceKey, "1.0.0", Now);
        var reading = WorkloadUpdateCohortReading.Create(group, [first, current]);
        var context = WorkloadUpdateStageContexts.For(current, plan, reading, group, Now);
        var stage = new ParamsStage(new RecordingEffects(new CallLog()));

        var waiting = await stage.UpAsync(context, CancellationToken.None);
        waiting.Kind.Should().Be(StageOutcomeKind.Working);
        waiting.Detail.Should().Contain(first.GroupInstanceId.ToString());
        current.TargetSpecsDocument.Should().BeNull();

        first.RecordStageJournal(new PipelineJournal(
            [new StageEntry(WorkloadUpdateStages.CommitVersion, Now, PipelineDirection.Forward, StageEntryState.Working, null)],
            PipelineDirection.Forward, PipelineState.Running));
        reading = WorkloadUpdateCohortReading.Create(group, [first, current]);
        context = WorkloadUpdateStageContexts.For(current, plan, reading, group, Now);
        var passed = await stage.UpAsync(context, CancellationToken.None);
        var versionAfterTurn = current.Version;
        var snapshot = current.TargetSpecsDocument;
        var replay = await stage.UpAsync(context, CancellationToken.None);

        passed.Kind.Should().Be(StageOutcomeKind.Done);
        replay.Kind.Should().Be(StageOutcomeKind.Done);
        snapshot.Should().NotBeNull();
        current.TargetSpecsDocument.Should().BeSameAs(snapshot);
        current.Version.Should().Be(versionAfterTurn);
    }

    private static WorkloadUpdateIntent Intent(Guid instance, Guid cohortId, int ordinal, int size)
    {
        var intent = WorkloadUpdateIntent.Create(Guid.NewGuid(), instance, "1.0.0", "2.0.0", Now,
            new WorkloadUpdateCohortPlace(cohortId, ordinal, size));
        intent.StageSnapshots(new WorkloadUpdateSnapshots { PreviousSpecs = new Dictionary<string, Reeve.Workloads.Abstractions.Domain.DesiredSpec.WorkloadDesiredSpec>() }, Now);
        return intent;
    }
}
