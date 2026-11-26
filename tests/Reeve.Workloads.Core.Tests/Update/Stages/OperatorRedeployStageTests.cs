using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Reeve.Pipeline;
using Reeve.Workloads.Abstractions;
using Reeve.Workloads.Abstractions.Domain.Entities;
using Reeve.Workloads.Abstractions.Update;
using Reeve.Workloads.Core.Update.Stages;
using Reeve.Workloads.Providers.Manual;
using Xunit;
using static Reeve.Workloads.Core.Tests.Update.WorkloadUpdatePlanBuilder;

namespace Reeve.Workloads.Core.Tests.Update.Stages;

public sealed class OperatorRedeployStageTests
{
    private static readonly Guid Instance = InstanceGuid;

    private sealed record Rig(
        OperatorRedeployStage Stage,
        WorkloadUpdateStageContext Context,
        FakeObservedWorkloadVersionReader Observed,
        SpyIssueRegistry Issues,
        FakePresenceContributor Presence);

    private static async Task<Rig> ManualAsync()
    {
        var plan = Plan(Instance, [Item("gateway", Instance, "sha-new", "sha-old", ManualProvider.Id)]);
        return await RigAsync(plan, ManualProvider.Id);
    }

    private static async Task<Rig> RigAsync(WorkloadUpdatePlan plan, Reeve.Workloads.Abstractions.Contracts.WorkloadProviderId provider)
    {
        var harness = new OrchestratorHarness(plan, providerKind: provider);
        var intent = await harness.StageManualAsync(plan);
        var stage = new OperatorRedeployStage(
            harness.Registry,
            harness.ObservedVersions,
            harness.Presence.Dispatch(),
            new SpyRepairsFactory(harness.Issues),
            harness.Clock,
            NullLogger<OperatorRedeployStage>.Instance);
        return new Rig(stage, WorkloadUpdateStageContexts.For(intent, plan), harness.ObservedVersions, harness.Issues,
            harness.Presence);
    }

    [Fact]
    public async Task A_managed_plan_passes_at_once_without_reading_any_sidecar()
    {
        var plan = Plan(Instance, [Item("gateway", Instance, "sha-new", "sha-old")]);
        var rig = await RigAsync(plan, Reeve.Workloads.Providers.Kubernetes.KubernetesProvider.Id);
        rig.Observed.VersionsByInstance[Instance] = "2.5.0";

        var outcome = await rig.Stage.UpAsync(rig.Context, CancellationToken.None);

        outcome.Kind.Should().Be(StageOutcomeKind.Done);
        rig.Observed.Reads.Should().Be(0);
        rig.Issues.Raised.Should().BeEmpty();
    }

    private static readonly Guid Bridge = Guid.Parse("22222222-2222-2222-2222-222222222222");

    private static Task<Rig> MixedAsync()
    {
        var plan = Plan(Instance,
        [
            Item("gateway", Instance, "sha-new", "sha-old", Reeve.Workloads.Providers.Docker.DockerProvider.Id),
            Item("bridge", Bridge, "sha-b-new", "sha-b-old", ManualProvider.Id),
        ]);
        return RigAsync(plan, Reeve.Workloads.Providers.Docker.DockerProvider.Id);
    }

    [Fact]
    public async Task A_mixed_plan_parks_on_its_manual_workload_until_the_operator_redeploys_it()
    {
        var rig = await MixedAsync();
        rig.Observed.VersionsByInstance[Bridge] = "2.5.0";

        var outcome = await rig.Stage.UpAsync(rig.Context, CancellationToken.None);

        outcome.Kind.Should().Be(StageOutcomeKind.Working, "the operator must redeploy the manual sidecar whatever else the group holds");
        rig.Issues.Raised.Should().ContainSingle().Which.Code.Should().Be("workload.bridge.version_skew",
            "the Docker workload is Reeve's to apply and is not waited on");
        rig.Context.RequireInternals().Intent.Steps.Single(step => step.WorkloadId == "gateway")
            .Message.Should().NotBe("awaiting operator sidecar redeploy (manual)");
    }

    [Fact]
    public async Task A_mixed_plan_passes_once_its_manual_sidecar_reports_the_target()
    {
        var rig = await MixedAsync();
        rig.Observed.VersionsByInstance[Bridge] = rig.Context.ToVersion;

        var outcome = await rig.Stage.UpAsync(rig.Context, CancellationToken.None);

        outcome.Kind.Should().Be(StageOutcomeKind.Done);
        rig.Observed.Reads.Should().Be(1, "only the manual workload's sidecar is read");
    }

    [Fact]
    public async Task A_manual_sidecar_behind_the_target_parks_with_a_skew_warning()
    {
        var rig = await ManualAsync();
        rig.Observed.VersionsByInstance[Instance] = "2.5.0";

        var outcome = await rig.Stage.UpAsync(rig.Context, CancellationToken.None);

        outcome.Kind.Should().Be(StageOutcomeKind.Working);
        outcome.Detail.Should().Contain(rig.Context.ToVersion, "the operator is told what to redeploy TO");
        rig.Issues.Raised.Should().ContainSingle()
            .Which.Should().Match<(string Code, WorkloadDiagnosticSeverity Severity, string Message)>(issue =>
                issue.Code == "workload.gateway.version_skew" && issue.Severity == WorkloadDiagnosticSeverity.Warning);
        rig.Context.RequireInternals().Intent.Steps.Single(step => step.WorkloadId == "gateway")
            .Should().Match<WorkloadUpdateIntentStep>(step => step.Step == WorkloadUpdatePhase.ApplyingSidecar
                && step.Progress == WorkloadUpdateStepProgress.Working
                && step.Message == "awaiting operator sidecar redeploy (manual)",
                "the read model shows the workload as waiting on the operator's apply");
    }

    [Fact]
    public async Task No_live_sidecar_session_at_all_parks()
    {
        var rig = await ManualAsync();

        var outcome = await rig.Stage.UpAsync(rig.Context, CancellationToken.None);

        outcome.Kind.Should().Be(StageOutcomeKind.Working);
    }

    [Fact]
    public async Task A_version_read_that_faults_parks()
    {
        var rig = await ManualAsync();
        rig.Observed.VersionsByInstance[Instance] = rig.Context.ToVersion;
        rig.Observed.Throw = new InvalidOperationException("session store is down");

        var outcome = await rig.Stage.UpAsync(rig.Context, CancellationToken.None);

        outcome.Kind.Should().Be(StageOutcomeKind.Working);
    }

    [Fact]
    public async Task A_sidecar_at_the_target_version_still_running_an_earlier_rendering_parks()
    {
        var rig = await ManualAsync();
        rig.Observed.VersionsByInstance[Instance] = rig.Context.ToVersion;
        rig.Presence.SpecHashByInstance[Instance] = "sha-first-run";

        var outcome = await rig.Stage.UpAsync(rig.Context, CancellationToken.None);

        outcome.Kind.Should().Be(StageOutcomeKind.Working,
            "a re-run with new parameters keeps the version, so only the rendering tells the operator has not redeployed it");
        rig.Issues.Raised.Should().ContainSingle().Which.Code.Should().Be("workload.gateway.version_skew");
    }

    [Fact]
    public async Task A_sidecar_at_the_target_version_and_rendering_passes()
    {
        var rig = await ManualAsync();
        rig.Observed.VersionsByInstance[Instance] = rig.Context.ToVersion;
        rig.Presence.SpecHashByInstance[Instance] = "sha-new";

        var outcome = await rig.Stage.UpAsync(rig.Context, CancellationToken.None);

        outcome.Kind.Should().Be(StageOutcomeKind.Done);
    }

    [Fact]
    public async Task Under_a_library_owned_health_mode_the_version_alone_decides()
    {
        var item = Item("gateway", Instance, "sha-new", "sha-old", ManualProvider.Id);
        var plan = Plan(Instance,
        [
            item with
            {
                TargetSpec = item.TargetSpec with
                {
                    Health = new Reeve.Workloads.Abstractions.Contracts.Deployment.WorkloadHealth
                    {
                        Mode = new Reeve.Workloads.Abstractions.Contracts.Deployment.WorkloadHealthMode("container"),
                    },
                },
            },
        ]);
        var rig = await RigAsync(plan, ManualProvider.Id);
        rig.Observed.VersionsByInstance[Instance] = rig.Context.ToVersion;
        rig.Presence.SpecHashByInstance[Instance] = "sha-first-run";

        var outcome = await rig.Stage.UpAsync(rig.Context, CancellationToken.None);

        outcome.Kind.Should().Be(StageOutcomeKind.Done,
            "a library-owned mode has no presence evidence, so asking for it would park the update for ever");
    }

    [Fact]
    public async Task A_manual_sidecar_at_the_target_passes_and_clears_the_warning()
    {
        var rig = await ManualAsync();
        rig.Observed.VersionsByInstance[Instance] = rig.Context.ToVersion;

        var outcome = await rig.Stage.UpAsync(rig.Context, CancellationToken.None);

        outcome.Kind.Should().Be(StageOutcomeKind.Done);
        rig.Issues.Raised.Should().BeEmpty();
        rig.Issues.Cleared.Should().Contain("workload.gateway.version_skew");
    }

    [Fact]
    public async Task Unwinding_clears_the_warning()
    {
        var rig = await ManualAsync();

        var outcome = await rig.Stage.DownAsync(rig.Context, CancellationToken.None);

        outcome.Kind.Should().Be(StageOutcomeKind.Done);
        rig.Issues.Cleared.Should().Contain("workload.gateway.version_skew");
    }

    [Fact]
    public async Task It_waits_without_limit_and_can_be_unwound_between_the_gate_and_the_version_commit()
    {
        var rig = await ManualAsync();

        rig.Stage.Id.Should().Be(WorkloadUpdateStages.OperatorRedeploy);
        rig.Stage.Placement.Should().Be(StagePlacement.After(WorkloadUpdateStages.Sidecar));
        rig.Stage.Budget.Limit.Should().BeNull();
        rig.Stage.Reversibility.IsReversible.Should().BeTrue();
    }
}
