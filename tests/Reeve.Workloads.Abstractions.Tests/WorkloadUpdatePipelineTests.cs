using FluentAssertions;
using Reeve.Pipeline;
using Reeve.Workloads.Abstractions.Update;
using Xunit;

namespace Reeve.Workloads.Abstractions.Tests;

public sealed class WorkloadUpdatePipelineTests
{
    private sealed record Stage(string Id, StagePlacement Placement) : IStage<WorkloadUpdateStageContext>
    {
        public StageReversibility Reversibility => StageReversibility.Reversible;

        public StageBudget Budget => StageBudget.None;

        public ValueTask<StageOutcome> UpAsync(WorkloadUpdateStageContext context, CancellationToken cancellationToken)
            => ValueTask.FromResult(StageOutcome.Done);

        public ValueTask<StageOutcome> DownAsync(WorkloadUpdateStageContext context, CancellationToken cancellationToken)
            => ValueTask.FromResult(StageOutcome.Done);
    }

    private static List<IStage<WorkloadUpdateStageContext>> CoreStages() =>
    [
        new Stage(WorkloadUpdateStages.Params, StagePlacement.First),
        new Stage(WorkloadUpdateStages.Sidecar, StagePlacement.After(WorkloadUpdateStages.Params)),
        new Stage(WorkloadUpdateStages.OperatorRedeploy, StagePlacement.After(WorkloadUpdateStages.Sidecar)),
        new Stage(WorkloadUpdateStages.HealthGate, StagePlacement.After(WorkloadUpdateStages.OperatorRedeploy)),
        new Stage(WorkloadUpdateStages.CommitVersion, StagePlacement.After(WorkloadUpdateStages.HealthGate)),
        new Stage(
            WorkloadUpdateStages.CommitParams,
            StagePlacement.After(WorkloadUpdateStages.CommitVersion, WorkloadUpdateStages.CommitParamsOrdinal)),
    ];

    [Fact]
    public void The_core_stages_alone_resolve_to_the_declared_order()
    {
        var pipeline = WorkloadUpdatePipeline.Resolve(CoreStages());

        pipeline.Order.Should().Equal(WorkloadUpdateStages.Core);
    }

    [Fact]
    public void A_host_stage_after_the_version_commit_resolves_between_the_two_commits()
    {
        var stages = CoreStages();
        stages.Add(new Stage("stage.host.plugin-roll", StagePlacement.After(WorkloadUpdateStages.CommitVersion)));

        var pipeline = WorkloadUpdatePipeline.Resolve(stages);

        pipeline.Order.Should().Equal(
            WorkloadUpdateStages.Params,
            WorkloadUpdateStages.Sidecar,
            WorkloadUpdateStages.OperatorRedeploy,
            WorkloadUpdateStages.HealthGate,
            WorkloadUpdateStages.CommitVersion,
            "stage.host.plugin-roll",
            WorkloadUpdateStages.CommitParams);
    }

    [Fact]
    public void A_host_stage_before_the_first_core_stage_is_refused()
    {
        var stages = CoreStages();
        stages.Add(new Stage("stage.host.too-early", StagePlacement.Before(WorkloadUpdateStages.Params)));

        var resolve = () => WorkloadUpdatePipeline.Resolve(stages);

        resolve.Should().Throw<PipelineDefinitionException>();
    }

    [Theory]
    [InlineData(WorkloadUpdateStages.Params)]
    [InlineData(WorkloadUpdateStages.Sidecar)]
    [InlineData(null)]
    public void A_host_stage_before_the_version_commit_is_refused_and_named(string? anchor)
    {
        var stages = CoreStages();
        stages.Add(new Stage("stage.host.early", anchor is null
            ? StagePlacement.Before(WorkloadUpdateStages.CommitVersion)
            : StagePlacement.After(anchor)));

        var resolve = () => WorkloadUpdatePipeline.Resolve(stages);

        resolve.Should().Throw<PipelineDefinitionException>()
            .WithMessage("*stage.host.early*")
            .WithMessage($"*{WorkloadUpdateStages.CommitVersion}*");
    }

    [Fact]
    public void A_host_stage_after_the_last_core_stage_is_refused_and_named()
    {
        var stages = CoreStages();
        stages.Add(new Stage("stage.host.too-late", StagePlacement.After(WorkloadUpdateStages.CommitParams)));

        var resolve = () => WorkloadUpdatePipeline.Resolve(stages);

        resolve.Should().Throw<PipelineDefinitionException>()
            .WithMessage("*stage.host.too-late*")
            .WithMessage($"*{WorkloadUpdateStages.CommitParams}*");
    }

    [Fact]
    public void A_host_stage_that_takes_the_head_placement_is_refused_too()
    {
        var stages = CoreStages();
        stages.RemoveAll(stage => stage.Id == WorkloadUpdateStages.Params);
        stages.Add(new Stage("stage.host.usurper", StagePlacement.First));
        stages.Add(new Stage(WorkloadUpdateStages.Params, StagePlacement.After("stage.host.usurper")));

        var resolve = () => WorkloadUpdatePipeline.Resolve(stages);

        resolve.Should().Throw<PipelineDefinitionException>().WithMessage("*stage.host.usurper*");
    }
}
