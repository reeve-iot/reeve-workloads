using FluentAssertions;
using Reeve.Pipeline;
using Reeve.Workloads.Abstractions.Update;
using Reeve.Workloads.Core.Update;
using Reeve.Workloads.Core.Update.Stages;
using Xunit;

namespace Reeve.Workloads.Core.Tests.Update.Stages;

public sealed class CoreStagesResolveTests
{
    private static readonly WorkloadUpdateOrchestratorOptions Options = new();

    private static IReadOnlyList<IStage<WorkloadUpdateStageContext>> CoreStages()
    {
        var log = new CallLog();
        var effects = new RecordingEffects(log);
        var clock = new MutableClock(DateTimeOffset.UnixEpoch);
        return
        [
            new ParamsStage(effects),
            new SidecarStage(new FakeProviderRegistry(new RecordingProvider(
                Reeve.Workloads.Providers.Kubernetes.KubernetesProvider.Id, log)), new ScriptedHealthGate(log), clock, Options,
                NSubstitute.Substitute.For<Reeve.Workloads.Abstractions.IWorkloadInstanceRepository>(),
                new Reeve.Workloads.Core.UnpinnedReferenceRolloutGuard(new Microsoft.AspNetCore.DataProtection.EphemeralDataProtectionProvider()), new Reeve.Workloads.Abstractions.NoCredentialPort()),
            new HealthGateStage(new ScriptedHealthGate(log), clock, Options),
            new OperatorRedeployStage(
                new FakeProviderRegistry(new RecordingProvider(
                    Reeve.Workloads.Providers.Kubernetes.KubernetesProvider.Id, log)),
                new FakeObservedWorkloadVersionReader(),
                new Reeve.Workloads.Core.Health.WorkloadHealthContributorDispatch([]),
                new SpyRepairsFactory(new SpyIssueRegistry()),
                clock,
                Microsoft.Extensions.Logging.Abstractions.NullLogger<OperatorRedeployStage>.Instance),
            new CommitVersionStage(effects, new RecordingRowEffects(new CallLog())),
            new CommitParamsStage(effects, new RecordingRowEffects(new CallLog())),
        ];
    }

    [Fact]
    public void The_six_stages_resolve_to_the_order_the_library_declares()
    {
        var pipeline = WorkloadUpdatePipeline.Resolve(CoreStages());

        pipeline.Order.Should().Equal(WorkloadUpdateStages.Core);
    }

    [Fact]
    public void A_host_stage_after_the_version_commit_lands_between_the_two_commits()
    {
        var stages = CoreStages().ToList();
        stages.Add(new HostDouble("stage.host.plugin-roll", StagePlacement.After(WorkloadUpdateStages.CommitVersion)));

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
    public void Only_the_last_of_the_six_makes_the_walk_forward_only()
    {
        var pipeline = WorkloadUpdatePipeline.Resolve(CoreStages());

        pipeline.FirstIrreversibleStageId.Should().Be(WorkloadUpdateStages.CommitParams,
            "it tears down the workloads the candidate drops, which no rollback can bring back");
    }

    private sealed record HostDouble(string Id, StagePlacement Placement) : IStage<WorkloadUpdateStageContext>
    {
        public StageReversibility Reversibility => StageReversibility.Reversible;

        public StageBudget Budget => StageBudget.None;

        public ValueTask<StageOutcome> UpAsync(WorkloadUpdateStageContext context, CancellationToken cancellationToken)
            => ValueTask.FromResult(StageOutcome.Done);

        public ValueTask<StageOutcome> DownAsync(WorkloadUpdateStageContext context, CancellationToken cancellationToken)
            => ValueTask.FromResult(StageOutcome.Done);
    }
}
