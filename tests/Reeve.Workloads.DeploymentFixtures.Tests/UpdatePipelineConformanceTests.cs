using FluentAssertions;
using Reeve.Pipeline;
using Reeve.Workloads.Abstractions.Update;

namespace Reeve.Workloads.DeploymentFixtures.Tests;

public sealed class UpdatePipelineConformanceTests
{
    private const string HostStage = "stage.host.plugin-roll";

    [Fact]
    public void The_library_stages_of_a_composed_pipeline_are_in_their_declared_order()
    {
        var pipeline = Resolve(Library().Append(Irreversible(HostStage, After(WorkloadUpdateStages.CommitVersion))));

        UpdatePipelineConformance.CoreStagesOutOfOrder(pipeline).Should().BeEmpty();
    }

    [Fact]
    public void A_library_stage_the_pipeline_lacks_is_named()
    {
        var pipeline = Resolve(Library(without: WorkloadUpdateStages.OperatorRedeploy));

        UpdatePipelineConformance.CoreStagesOutOfOrder(pipeline).Should().Equal(WorkloadUpdateStages.OperatorRedeploy);
    }

    [Fact]
    public void A_library_stage_that_runs_before_one_declared_ahead_of_it_is_named()
    {
        var stages = Library()
            .Select(stage => stage.Id == WorkloadUpdateStages.OperatorRedeploy
                ? Reversible(stage.Id, After(WorkloadUpdateStages.HealthGate))
                : stage.Id == WorkloadUpdateStages.HealthGate
                    ? Reversible(stage.Id, After(WorkloadUpdateStages.Sidecar))
                    : stage.Id == WorkloadUpdateStages.CommitVersion
                        ? Reversible(stage.Id, After(WorkloadUpdateStages.OperatorRedeploy))
                        : stage);

        UpdatePipelineConformance.CoreStagesOutOfOrder(Resolve(stages))
            .Should().Equal(WorkloadUpdateStages.HealthGate);
    }

    [Fact]
    public void The_irreversible_stages_are_named_in_walk_order()
    {
        var pipeline = Resolve(Library()
            .Append(Irreversible("stage.host.a-migrate", After(WorkloadUpdateStages.CommitVersion, 2)))
            .Append(Reversible("stage.host.m-check", After(WorkloadUpdateStages.CommitVersion, 3)))
            .Append(Irreversible("stage.host.z-reload", After(WorkloadUpdateStages.CommitVersion, 1))));

        UpdatePipelineConformance.IrreversibleStages(pipeline)
            .Should().Equal("stage.host.z-reload", "stage.host.a-migrate", WorkloadUpdateStages.CommitParams);
    }

    [Fact]
    public void The_library_alone_has_its_point_of_no_return_at_the_parameter_commit()
        => UpdatePipelineConformance.IrreversibleStages(Resolve(Library())).Should().Equal(
            [WorkloadUpdateStages.CommitParams],
            "the parameter commit tears down the workloads the new version no longer declares");

    private static StagePlacement After(string anchor, int ordinal = 0) => StagePlacement.After(anchor, ordinal);

    private static IEnumerable<IStage<WorkloadUpdateStageContext>> Library(string? without = null)
    {
        IStage<WorkloadUpdateStageContext>[] stages =
        [
            Reversible(WorkloadUpdateStages.Params, StagePlacement.First),
            Reversible(WorkloadUpdateStages.Sidecar, After(WorkloadUpdateStages.Params)),
            Reversible(WorkloadUpdateStages.OperatorRedeploy, After(WorkloadUpdateStages.Sidecar)),
            Reversible(WorkloadUpdateStages.HealthGate, After(WorkloadUpdateStages.OperatorRedeploy)),
            Reversible(WorkloadUpdateStages.CommitVersion, After(WorkloadUpdateStages.HealthGate)),
            Irreversible(WorkloadUpdateStages.CommitParams, After(WorkloadUpdateStages.CommitVersion, WorkloadUpdateStages.CommitParamsOrdinal)),
        ];

        return without is null
            ? stages
            : stages
                .Where(stage => stage.Id != without)
                .Select(stage => stage.Id == WorkloadUpdateStages.HealthGate
                    ? Reversible(stage.Id, After(WorkloadUpdateStages.Sidecar))
                    : stage);
    }

    private static Pipeline<WorkloadUpdateStageContext> Resolve(IEnumerable<IStage<WorkloadUpdateStageContext>> stages)
        => Pipeline<WorkloadUpdateStageContext>.Resolve(stages);

    private static Stub Reversible(string id, StagePlacement placement) => new(id, placement, StageReversibility.Reversible);

    private static Stub Irreversible(string id, StagePlacement placement)
        => new(id, placement, StageReversibility.IrreversibleOnceEntered("a certified N+1 may have forward-migrated data"));

    private sealed class Stub(string id, StagePlacement placement, StageReversibility reversibility) : IStage<WorkloadUpdateStageContext>
    {
        public string Id => id;

        public StagePlacement Placement => placement;

        public StageReversibility Reversibility => reversibility;

        public StageBudget Budget => StageBudget.None;

        public ValueTask<StageOutcome> UpAsync(WorkloadUpdateStageContext context, CancellationToken cancellationToken)
            => ValueTask.FromResult(StageOutcome.Done);

        public ValueTask<StageOutcome> DownAsync(WorkloadUpdateStageContext context, CancellationToken cancellationToken)
            => ValueTask.FromResult(StageOutcome.Done);
    }
}
