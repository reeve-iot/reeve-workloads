using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Reeve.Workloads.Abstractions;
using Reeve.Workloads.Abstractions.Contracts;
using Reeve.Workloads.Abstractions.Contracts.Deployment;
using Reeve.Workloads.Abstractions.Domain;
using Reeve.Workloads.Abstractions.Domain.DesiredSpec;
using Reeve.Workloads.Abstractions.Domain.Entities;
using Reeve.Workloads.Abstractions.Providers;
using Reeve.Workloads.Core;
using Reeve.Workloads.Domain;
using Reeve.Workloads.Providers.Kubernetes;
using Reeve.Workloads.Providers.Manual;

namespace Reeve.Workloads.Core.Tests;

public sealed class WorkloadOperationsServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 10, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid InstanceId = Guid.Parse("8e1a4b60-9d2c-4f75-b3a8-1c6e5d0f7a29");
    private static readonly Guid GroupInstanceId = Guid.Parse("f0e1d2c3-b4a5-4968-8776-5a4b3c2d1e0f");

    private readonly IWorkloadInstanceRepository repository = Substitute.For<IWorkloadInstanceRepository>();
    private readonly IActiveWorkloadIntentProvider intentProvider = Substitute.For<IActiveWorkloadIntentProvider>();
    private readonly IWorkloadClusterBus clusterBus = Substitute.For<IWorkloadClusterBus>();
    private readonly RecordingReconcileTrigger localTrigger = new();
    private readonly IWorkloadClock clock = Substitute.For<IWorkloadClock>();

    private int writes;

    [Fact]
    public void Scan_desired_spec_uses_the_marker_aware_reference_projection()
    {
        const string binding = "ref:vault:password";
        var instance = Seed();
        instance.SetResolvedParameters(new Dictionary<string, string?> { ["password"] = binding }, Now);
        instance.SetReferenceRolloutState(
            "{\"hashPinnedBindings\":{\"password\":{\"binding\":\"ref:vault:password\",\"isVersionPinned\":false}}}", Now);
        var inputs = ProjectionInputs(new Dictionary<string, string?> { ["password"] = binding });

        var projected = WorkloadOperationsService.BuildDesiredSpec(instance, inputs);
        var literalBaseline = WorkloadOperationsService.BuildDesiredSpec(instance,
            inputs with { ParameterReferencesForHash = new Dictionary<string, string?>() });

        projected.SpecHash.Should().Be(literalBaseline.SpecHash);
    }

    private static WorkloadReconcileInputs ProjectionInputs(IReadOnlyDictionary<string, string?> references)
        => new()
        {
            WorkloadManifest = new WorkloadManifest
            {
                Id = "border-router", DisplayName = "Border router",
                Image = new WorkloadImage { Ref = "registry.example/router", Digest = "sha256:" + new string('a', 64) },
                Kind = WorkloadKind.SingleNode,
                Health = new WorkloadHealth { Mode = WorkloadHealthMode.Container },
            },
            ResolvedParameters = new Dictionary<string, string?>(),
            ParameterReferencesForHash = references,
            Settings = new WorkloadOrchestratorSettings(),
            Bootstrap = new WorkloadBootstrapContext { InstanceId = InstanceId, SecretName = "secret" },
            EffectiveProvider = KubernetesProvider.Descriptor,
            Namer = DefaultWorkloadResourceNamer.Instance,
            InstallationId = "test",
        };

    public WorkloadOperationsServiceTests()
    {
        clock.UtcNow.Returns(Now);
        repository.When(r => r.UpdateAsync(Arg.Any<WorkloadInstance>(), Arg.Any<CancellationToken>()))
            .Do(_ => writes++);
    }

    private WorkloadOperationsService CreateService() => new(
        repository, intentProvider, new WorkloadReconcileNudge(clusterBus, localTrigger), clock,
        NullLogger<WorkloadOperationsService>.Instance,
        Substitute.For<IWorkloadReconcileInputsProvider>(),
        ProviderRegistryFakes.FirstPartyRegistry());

    private WorkloadInstance Seed(WorkloadInstanceState state = WorkloadInstanceState.Healthy)
    {
        var instance = WorkloadInstance.Create(
            InstanceId, GroupInstanceId, "acme.thread", "border-router", Now - TimeSpan.FromDays(1));
        instance.TransitionTo(WorkloadInstanceState.PendingConfiguration, WorkloadStateReason.ConfigRequired, null, Now);
        instance.TransitionTo(WorkloadInstanceState.Deploying, WorkloadStateReason.Provisioning, null, Now);
        instance.TransitionTo(WorkloadInstanceState.Healthy, WorkloadStateReason.HealthObserved, null, Now);
        instance.SetProvider(KubernetesProvider.Id, Now);
        instance.SetAppliedSpecHash("sha256:applied", Now);
        if (state != WorkloadInstanceState.Healthy)
        {
            instance.TransitionTo(state, WorkloadStateReason.HealthLost, null, Now);
        }

        repository.GetByIdAsync(InstanceId, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<WorkloadInstance?>(instance));
        return instance;
    }

    private Task<WorkloadOperationResult> InvokeAsync(string command)
    {
        var service = CreateService();
        return command switch
        {
            "restart" => service.RestartAsync(InstanceId),
            "redeploy" => service.RedeployAsync(InstanceId),
            "pause" => service.PauseAsync(InstanceId),
            "resume" => service.ResumeAsync(InstanceId),
            _ => throw new ArgumentOutOfRangeException(nameof(command), command, "unknown command"),
        };
    }

    private async Task AssertNothingPersistedAsync()
    {
        writes.Should().Be(0, "a refused command must not write");
        await clusterBus.DidNotReceiveWithAnyArgs().PublishAsync(default!, default(object)!, default);
    }

    [Theory]
    [InlineData("restart")]
    [InlineData("redeploy")]
    [InlineData("pause")]
    [InlineData("resume")]
    public async Task An_unknown_instance_is_not_found(string command)
    {
        repository.GetByIdAsync(InstanceId, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<WorkloadInstance?>(null));

        (await InvokeAsync(command)).Outcome.Should().Be(WorkloadOperationOutcome.NotFound);
        await AssertNothingPersistedAsync();
    }

    [Theory]
    [InlineData("restart")]
    [InlineData("redeploy")]
    [InlineData("pause")]
    [InlineData("resume")]
    public async Task An_active_update_intent_blocks_every_command_and_echoes_the_intent_id(string command)
    {
        var intentId = Guid.Parse("0a1b2c3d-4e5f-4061-8273-849506a7b8c9");
        Seed(WorkloadInstanceState.Suspended);
        intentProvider.GetActiveIntentIdAsync(GroupInstanceId, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<Guid?>(intentId));

        var result = await InvokeAsync(command);

        result.Outcome.Should().Be(WorkloadOperationOutcome.Conflict);
        result.ActiveIntentId.Should().Be(intentId);
        await AssertNothingPersistedAsync();
    }

    [Theory]
    [InlineData("restart", WorkloadInstanceState.ExternallyManaged)]
    [InlineData("redeploy", WorkloadInstanceState.ExternallyManaged)]
    [InlineData("pause", WorkloadInstanceState.ExternallyManaged)]
    [InlineData("resume", WorkloadInstanceState.ExternallyManaged)]
    [InlineData("redeploy", WorkloadInstanceState.Switching)]
    public async Task An_ownership_frozen_instance_refuses_every_command(
        string command, WorkloadInstanceState frozen)
    {
        var instance = Seed();
        instance.TransitionTo(frozen, WorkloadStateReason.ExternalHealthObserved, null, Now);

        var result = await InvokeAsync(command);

        result.Outcome.Should().Be(WorkloadOperationOutcome.NotMutable);
        result.BlockingState.Should().Be(frozen, "the operator has to be told what is blocking");
        instance.State.Should().Be(frozen);
        await AssertNothingPersistedAsync();
    }

    [Theory]
    [InlineData("restart")]
    [InlineData("redeploy")]
    [InlineData("pause")]
    public async Task A_manual_workload_is_told_reeve_cannot_control_its_runtime(string command)
    {
        var instance = Seed();
        instance.SetProvider(ManualProvider.Id, Now);
        instance.TransitionTo(WorkloadInstanceState.ExternallyManaged, WorkloadStateReason.ExternalHealthObserved, null, Now);

        var result = await InvokeAsync(command);

        result.Outcome.Should().Be(WorkloadOperationOutcome.ProviderCannotControlRuntime,
            "the operator runs it, so the command would only change the state shown");
        result.Error.Should().Contain(ManualProvider.Descriptor.Label).And.Contain(command);
        instance.State.Should().Be(WorkloadInstanceState.ExternallyManaged);
        instance.AppliedSpecHash.Should().Be("sha256:applied");
        await AssertNothingPersistedAsync();
    }

    [Fact]
    public async Task A_paused_manual_workload_can_still_be_resumed()
    {
        var instance = Seed();
        instance.SetProvider(ManualProvider.Id, Now);
        instance.TransitionTo(WorkloadInstanceState.Suspended, WorkloadStateReason.OperatorPaused, null, Now);

        var result = await InvokeAsync("resume");

        result.Outcome.Should().Be(WorkloadOperationOutcome.Success, "a paused manual instance must have a way out");
        instance.State.Should().Be(WorkloadInstanceState.Deploying);
    }

    [Theory]
    [InlineData("redeploy", WorkloadInstanceState.Terminating)]
    [InlineData("restart", WorkloadInstanceState.Terminating)]
    [InlineData("redeploy", WorkloadInstanceState.Unknown)]
    [InlineData("restart", WorkloadInstanceState.Unknown)]
    public async Task An_instance_that_forbids_mutations_refuses_the_command(
        string command, WorkloadInstanceState state)
    {
        var instance = Seed(state);

        var result = await InvokeAsync(command);

        result.Outcome.Should().Be(WorkloadOperationOutcome.NotMutable);
        result.BlockingState.Should().Be(state);
        instance.State.Should().Be(state);
        instance.AppliedSpecHash.Should().Be("sha256:applied");
        await AssertNothingPersistedAsync();
    }

    [Fact]
    public async Task Resuming_an_instance_that_is_not_suspended_is_refused_rather_than_thrown()
    {
        var instance = Seed(WorkloadInstanceState.Failed);

        var result = await InvokeAsync("resume");

        result.Outcome.Should().Be(WorkloadOperationOutcome.NotMutable);
        result.BlockingState.Should().Be(WorkloadInstanceState.Failed);
        instance.State.Should().Be(WorkloadInstanceState.Failed);
        await AssertNothingPersistedAsync();
    }

    [Theory]
    [InlineData("restart")]
    [InlineData("redeploy")]
    [InlineData("pause")]
    [InlineData("resume")]
    public async Task A_row_the_host_holds_refuses_every_operator_command(string command)
    {
        var instance = SeedHeld();
        var (state, reason) = (instance.State, instance.StateReason);

        var result = await InvokeAsync(command);

        result.Outcome.Should().Be(WorkloadOperationOutcome.NotMutable);
        result.BlockingState.Should().Be(state);
        instance.State.Should().Be(state);
        instance.StateReason.Should().Be(reason, "the hold keeps the reason only the host's resume clears");
        await AssertNothingPersistedAsync();
    }

    [Fact]
    public async Task An_operator_pause_is_not_a_host_hold_and_resumes()
    {
        var instance = Seed();
        instance.TransitionTo(WorkloadInstanceState.Suspended, WorkloadStateReason.OperatorPaused, null, Now);

        var result = await InvokeAsync("resume");

        result.Outcome.Should().Be(WorkloadOperationOutcome.Success);
        instance.State.Should().Be(WorkloadInstanceState.Deploying);
    }

    private WorkloadInstance SeedHeld()
    {
        var instance = Seed();
        instance.TransitionTo(WorkloadInstanceState.Suspended, WorkloadStateReason.SuspendedByHost, null, Now);
        return instance;
    }

    [Fact]
    public async Task A_restart_bumps_the_pod_template_nonce_and_a_redeploy_deliberately_does_not()
    {
        var restarted = Seed();
        var generationBefore = restarted.RestartGeneration;

        var restart = await InvokeAsync("restart");

        restart.Outcome.Should().Be(WorkloadOperationOutcome.Success);
        restarted.RestartGeneration.Should().Be(generationBefore + 1);
        restarted.AppliedSpecHash.Should().BeNull("both commands force a fresh apply");

        var redeployed = Seed();
        var redeployGenerationBefore = redeployed.RestartGeneration;

        var redeploy = await InvokeAsync("redeploy");

        redeploy.Outcome.Should().Be(WorkloadOperationOutcome.Success);
        redeployed.RestartGeneration.Should().Be(redeployGenerationBefore,
            "a redeploy re-applies the spec; it does not force an unchanged template to roll");
        redeployed.AppliedSpecHash.Should().BeNull();
    }

    [Theory]
    [InlineData("restart", "workload-restart")]
    [InlineData("redeploy", "workload-redeploy")]
    public async Task A_redeploying_command_moves_to_deploying_and_nudges_under_its_own_reason(
        string command, string expectedReason)
    {
        var instance = Seed();

        var result = await InvokeAsync(command);

        result.ResultingState.Should().Be(WorkloadInstanceState.Deploying);
        instance.State.Should().Be(WorkloadInstanceState.Deploying);
        instance.StateReason.Should().Be(WorkloadStateReason.Redeploying);
        writes.Should().Be(1);
        await clusterBus.Received(1).PublishAsync(
            WorkloadClusterChannels.ReconcileRequest,
            Arg.Is<WorkloadReconcileRequestMessage>(m => m.InstanceId == InstanceId && m.Reason == expectedReason),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Pausing_suspends_the_instance_and_leaves_the_applied_spec_hash_intact()
    {
        var instance = Seed();

        var result = await InvokeAsync("pause");

        result.Outcome.Should().Be(WorkloadOperationOutcome.Success);
        instance.State.Should().Be(WorkloadInstanceState.Suspended);
        instance.StateReason.Should().Be(WorkloadStateReason.OperatorPaused);
        instance.AppliedSpecHash.Should().Be("sha256:applied");
        await clusterBus.Received(1).PublishAsync(
            WorkloadClusterChannels.ReconcileRequest,
            Arg.Is<WorkloadReconcileRequestMessage>(m => m.Reason == "workload-pause"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Resuming_a_suspended_instance_redeploys_it_and_clears_the_applied_spec_hash()
    {
        var instance = Seed(WorkloadInstanceState.Suspended);

        var result = await InvokeAsync("resume");

        result.Outcome.Should().Be(WorkloadOperationOutcome.Success);
        instance.State.Should().Be(WorkloadInstanceState.Deploying);
        instance.StateReason.Should().Be(WorkloadStateReason.OperatorResumed);
        instance.AppliedSpecHash.Should().BeNull("a resume re-applies, as the host resume does, so an object removed during the pause is recreated");
        await clusterBus.Received(1).PublishAsync(
            WorkloadClusterChannels.ReconcileRequest,
            Arg.Is<WorkloadReconcileRequestMessage>(m => m.Reason == "workload-resume"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Setting_observe_only_on_an_unknown_instance_is_not_found()
    {
        repository.GetByIdAsync(InstanceId, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<WorkloadInstance?>(null));

        (await CreateService().SetObserveOnlyAsync(InstanceId, observeOnly: true)).Outcome
            .Should().Be(WorkloadOperationOutcome.NotFound);
        await AssertNothingPersistedAsync();
    }

    [Fact]
    public async Task An_active_update_intent_blocks_setting_observe_only()
    {
        var intentId = Guid.Parse("11223344-5566-4778-899a-bbccddeeff00");
        Seed();
        intentProvider.GetActiveIntentIdAsync(GroupInstanceId, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<Guid?>(intentId));

        var result = await CreateService().SetObserveOnlyAsync(InstanceId, observeOnly: true);

        result.Outcome.Should().Be(WorkloadOperationOutcome.Conflict);
        result.ActiveIntentId.Should().Be(intentId);
        await AssertNothingPersistedAsync();
    }

    [Theory]
    [InlineData(WorkloadInstanceState.Unknown)]
    [InlineData(WorkloadInstanceState.Terminating)]
    public async Task Observe_only_can_be_flipped_on_an_instance_that_forbids_every_other_mutation(
        WorkloadInstanceState state)
    {
        var instance = Seed(state);

        var result = await CreateService().SetObserveOnlyAsync(InstanceId, observeOnly: true);

        result.Outcome.Should().Be(WorkloadOperationOutcome.Success);
        instance.ObserveOnly.Should().BeTrue();
        instance.State.Should().Be(state, "a flag flip must not move the instance");
        result.ResultingState.Should().Be(state);
    }

    [Theory]
    [InlineData(true, "workload-observe-only-on")]
    [InlineData(false, "workload-observe-only-off")]
    public async Task Each_observe_only_direction_nudges_under_its_own_reason(bool observeOnly, string expectedReason)
    {
        Seed();

        await CreateService().SetObserveOnlyAsync(InstanceId, observeOnly);

        await clusterBus.Received(1).PublishAsync(
            WorkloadClusterChannels.ReconcileRequest,
            Arg.Is<WorkloadReconcileRequestMessage>(m => m.InstanceId == InstanceId && m.Reason == expectedReason),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Re_asserting_the_current_observe_only_value_is_idempotent_rather_than_ignored()
    {
        var instance = Seed();
        instance.SetObserveOnly(true, Now);

        var result = await CreateService().SetObserveOnlyAsync(InstanceId, observeOnly: true);

        result.Outcome.Should().Be(WorkloadOperationOutcome.Success);
        instance.ObserveOnly.Should().BeTrue();
        writes.Should().Be(1);
    }
}
