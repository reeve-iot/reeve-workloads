using Reeve.Workloads.Domain;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Reeve.Workloads.Abstractions;
using Reeve.Workloads.Abstractions.Contracts;
using Reeve.Workloads.Abstractions.Contracts.Deployment;
using Reeve.Workloads.Abstractions.Domain.DesiredSpec;
using Reeve.Workloads.Abstractions.Domain.Entities;
using Reeve.Workloads.Core;
using Reeve.Workloads.Domain.DesiredSpec;
using Reeve.Workloads.Providers.Kubernetes;

using Reeve.Workloads.Abstractions.Providers;
using Reeve.Workloads.Tests.Shared;

namespace Reeve.Workloads.Providers.Kubernetes.Tests;

public sealed class WorkloadVolumeResizeServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 10, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid InstanceId = Guid.Parse("2a7c9e11-8b4d-4f36-a0c5-7d3e6f9b1a24");
    private static readonly Guid GroupInstanceId = Guid.Parse("d4e5f607-1829-4a3b-8c5d-6e7f8091a2b3");
    private const string WorkloadId = "border-router";
    private const string VolumeId = "state";
    private const string DeclaredSize = "512Mi";

    private static readonly string OverrideKey =
        WorkloadDesiredSpecFactory.VolumeSizeOverrideParameterPrefix + VolumeId;

    private readonly IWorkloadInstanceRepository repository = Substitute.For<IWorkloadInstanceRepository>();
    private readonly IActiveWorkloadIntentProvider intentProvider = Substitute.For<IActiveWorkloadIntentProvider>();
    private readonly FakeInputsProvider inputsProvider = new();
    private readonly IWorkloadClusterBus clusterBus = Substitute.For<IWorkloadClusterBus>();
    private readonly IWorkloadReconcileTrigger localTrigger = Substitute.For<IWorkloadReconcileTrigger>();
    private readonly IWorkloadClock clock = Substitute.For<IWorkloadClock>();

    private int writes;

    public WorkloadVolumeResizeServiceTests()
    {
        clock.UtcNow.Returns(Now);
        inputsProvider.Manifest = Manifest();
        repository.When(r => r.UpdateAsync(Arg.Any<WorkloadInstance>(), Arg.Any<CancellationToken>()))
            .Do(_ => writes++);
    }

    private WorkloadVolumeResizeService CreateService() => new(
        repository, intentProvider, inputsProvider, new WorkloadReconcileNudge(clusterBus, localTrigger), clock,
        NullLogger<WorkloadVolumeResizeService>.Instance, StandInRegistry());

    private static IWorkloadProviderRegistry StandInRegistry()
    {
        var registry = Substitute.For<IWorkloadProviderRegistry>();
        registry.Describe(Arg.Any<WorkloadProviderId>())
            .Returns(call => StandInProviders.For(call.Arg<WorkloadProviderId>()));
        return registry;
    }

    private WorkloadInstance Seed(
        WorkloadInstanceState state = WorkloadInstanceState.Healthy,
        IReadOnlyDictionary<string, string?>? parameters = null)
    {
        var instance = WorkloadInstance.Create(
            InstanceId, GroupInstanceId, "acme.thread", WorkloadId, Now - TimeSpan.FromDays(1));
        instance.TransitionTo(WorkloadInstanceState.PendingConfiguration, WorkloadStateReason.ConfigRequired, null, Now);
        instance.TransitionTo(WorkloadInstanceState.Deploying, WorkloadStateReason.Provisioning, null, Now);
        instance.TransitionTo(WorkloadInstanceState.Healthy, WorkloadStateReason.HealthObserved, null, Now);
        instance.SetProvider(KubernetesProvider.Id, Now);
        instance.SetAppliedSpecHash("sha256:applied", Now);
        if (parameters is not null)
        {
            instance.SetResolvedParameters(parameters, Now);
        }

        if (state != WorkloadInstanceState.Healthy)
        {
            instance.TransitionTo(state, WorkloadStateReason.HealthLost, null, Now);
        }

        repository.GetByIdAsync(InstanceId, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<WorkloadInstance?>(instance));
        return instance;
    }

    private static WorkloadManifest Manifest() => new()
    {
        Id = WorkloadId,
        DisplayName = "Border Router",
        Image = new WorkloadImage { Ref = "registry.example/thread-br", Digest = "sha256:" + new string('a', 64) },
        Kind = WorkloadKind.SingleNode,
        Health = new WorkloadHealth { Mode = WorkloadHealthMode.Http, HeartbeatSeconds = 30 },
        Volumes =
        [
            new WorkloadVolume
            {
                Id = VolumeId,
                MountPath = "/data",
                Size = DeclaredSize,
                Criticality = VolumeCriticality.Critical,
                Description = "Thread dataset",
            },
        ],
    };

    private Task<WorkloadVolumeResizeResult> ResizeAsync(string newSize = "1Gi", string volumeId = VolumeId)
        => CreateService().RequestResizeAsync(InstanceId, volumeId, newSize, actorUserId: null);

    private async Task AssertNothingPersistedAsync()
    {
        writes.Should().Be(0, "a refused resize must not persist the override");
        await clusterBus.DidNotReceiveWithAnyArgs().PublishAsync(default!, default(object)!, default);
    }

    [Fact]
    public async Task An_unknown_instance_is_not_found()
    {
        repository.GetByIdAsync(InstanceId, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<WorkloadInstance?>(null));

        (await ResizeAsync()).Outcome.Should().Be(WorkloadVolumeResizeOutcome.NotFound);
        await AssertNothingPersistedAsync();
    }

    [Theory]
    [InlineData(WorkloadInstanceState.Switching)]
    [InlineData(WorkloadInstanceState.ExternallyManaged)]
    public async Task An_ownership_frozen_instance_is_refused(WorkloadInstanceState state)
    {
        var instance = Seed();
        instance.TransitionTo(state, WorkloadStateReason.ExternalHealthObserved, null, Now);

        var result = await ResizeAsync();

        result.Outcome.Should().Be(WorkloadVolumeResizeOutcome.Rejected);
        await AssertNothingPersistedAsync();
    }

    [Fact]
    public async Task A_connected_manual_instance_is_told_resize_does_not_apply_not_that_it_is_disowned()
    {
        var instance = Seed();
        instance.SetProvider(StandInProviders.ManualId, Now);
        instance.TransitionTo(WorkloadInstanceState.ExternallyManaged, WorkloadStateReason.ExternalHealthObserved, null, Now);
        inputsProvider.EffectiveProvider = StandInProviders.Manual;

        var result = await ResizeAsync();

        result.Outcome.Should().Be(WorkloadVolumeResizeOutcome.NotApplicable);
        await AssertNothingPersistedAsync();
    }

    [Fact]
    public async Task A_host_held_instance_is_refused_and_keeps_its_hold()
    {
        var instance = Seed();
        instance.TransitionTo(WorkloadInstanceState.Suspended, WorkloadStateReason.SuspendedByHost, "withdrawn", Now);

        var result = await ResizeAsync();

        result.Outcome.Should().Be(WorkloadVolumeResizeOutcome.Rejected);
        instance.IsHeldByHost.Should().BeTrue("an operator cannot lift the host's hold");
        instance.AppliedSpecHash.Should().Be("sha256:applied");
        await AssertNothingPersistedAsync();
    }

    [Theory]
    [InlineData(WorkloadInstanceState.Terminating)]
    [InlineData(WorkloadInstanceState.Unknown)]
    public async Task An_instance_that_forbids_mutations_is_refused(WorkloadInstanceState state)
    {
        var instance = Seed(state);

        var result = await ResizeAsync();

        result.Outcome.Should().Be(WorkloadVolumeResizeOutcome.Rejected);
        instance.State.Should().Be(state, "a refused resize must not move the instance at all");
        instance.AppliedSpecHash.Should().Be("sha256:applied");
        await AssertNothingPersistedAsync();
    }

    [Fact]
    public async Task A_workload_that_is_no_longer_declared_is_not_found()
    {
        Seed();
        inputsProvider.Manifest = null;

        (await ResizeAsync()).Outcome.Should().Be(WorkloadVolumeResizeOutcome.NotFound);
        await AssertNothingPersistedAsync();
    }

    [Fact]
    public async Task A_host_refusal_refuses_the_resize_with_the_hosts_reason()
    {
        Seed();
        inputsProvider.Refusal = "host says no";

        var result = await ResizeAsync();

        result.Outcome.Should().Be(WorkloadVolumeResizeOutcome.HostRefused);
        result.Error.Should().Contain("host says no");
        await AssertNothingPersistedAsync();
    }

    [Theory]
    [InlineData("docker")]
    [InlineData("manual")]
    public async Task A_non_kubernetes_instance_is_told_the_feature_does_not_apply_before_any_volume_lookup(
        string providerId)
    {
        var provider = StandInProviders.For(WorkloadProviderId.Parse(providerId));
        Seed();
        inputsProvider.EffectiveProvider = provider;

        var result = await ResizeAsync(volumeId: "no-such-volume");

        result.Outcome.Should().Be(WorkloadVolumeResizeOutcome.NotApplicable);
        result.Error.Should().Contain($"the effective provider is {provider.Label})")
            .And.NotContain("WorkloadProviderDescriptor {")
            .And.NotContain("DisplayName =");
        await AssertNothingPersistedAsync();
    }

    [Fact]
    public async Task An_undeclared_volume_id_is_not_found()
    {
        Seed();

        var result = await ResizeAsync(volumeId: "no-such-volume");

        result.Outcome.Should().Be(WorkloadVolumeResizeOutcome.VolumeNotFound);
        await AssertNothingPersistedAsync();
    }

    [Fact]
    public async Task An_active_update_intent_serializes_the_resize_and_echoes_the_intent_id()
    {
        var intentId = Guid.Parse("5e4d3c2b-1a09-4f8e-9d7c-6b5a4938271f");
        Seed();
        intentProvider.GetActiveIntentIdAsync(GroupInstanceId, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<Guid?>(intentId));

        var result = await ResizeAsync();

        result.Outcome.Should().Be(WorkloadVolumeResizeOutcome.Conflict);
        result.ActiveIntentId.Should().Be(intentId);
        await AssertNothingPersistedAsync();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("abc")]
    [InlineData("10 Gigs")]
    [InlineData("-5Gi")]
    [InlineData("0")]
    [InlineData(null)]
    public async Task A_size_that_is_not_a_kubernetes_quantity_is_rejected(string? newSize)
    {
        Seed();

        var result = await ResizeAsync(newSize!);

        result.Outcome.Should().Be(WorkloadVolumeResizeOutcome.Rejected);
        result.Error.Should().Contain("not a valid storage size");
        await AssertNothingPersistedAsync();
    }

    [Fact]
    public async Task A_size_beyond_the_single_volume_cap_is_rejected()
    {
        Seed();

        var result = await ResizeAsync("65Ti");

        result.Outcome.Should().Be(WorkloadVolumeResizeOutcome.Rejected);
        result.Error.Should().Contain("maximum single-volume size");
        await AssertNothingPersistedAsync();
    }

    [Fact]
    public async Task A_size_exactly_at_the_cap_is_accepted()
    {
        Seed();

        (await ResizeAsync("64Ti")).Outcome.Should().Be(WorkloadVolumeResizeOutcome.Success);
    }

    [Fact]
    public async Task A_shrink_is_refused_and_persists_nothing()
    {
        Seed();

        var result = await ResizeAsync("256Mi");

        result.Outcome.Should().Be(WorkloadVolumeResizeOutcome.Rejected);
        result.Error.Should().Contain("only be resized UP");
        await AssertNothingPersistedAsync();
    }

    [Fact]
    public async Task Requesting_the_size_already_in_force_is_an_idempotent_no_op()
    {
        var instance = Seed();

        var result = await ResizeAsync(DeclaredSize);

        result.Outcome.Should().Be(WorkloadVolumeResizeOutcome.NoChange);
        result.RequestedSize.Should().Be(DeclaredSize);
        instance.State.Should().Be(WorkloadInstanceState.Healthy);
        await AssertNothingPersistedAsync();
    }

    [Fact]
    public async Task The_same_size_spelled_differently_is_still_a_no_op()
    {
        Seed(parameters: new Dictionary<string, string?>(StringComparer.Ordinal) { [OverrideKey] = "1Gi" });

        (await ResizeAsync("1024Mi")).Outcome.Should().Be(WorkloadVolumeResizeOutcome.NoChange);
        await AssertNothingPersistedAsync();
    }

    [Fact]
    public async Task The_current_size_is_the_recorded_override_and_not_the_manifest_declaration()
    {
        Seed(parameters: new Dictionary<string, string?>(StringComparer.Ordinal) { [OverrideKey] = "4Gi" });

        var result = await ResizeAsync("1Gi");

        result.Outcome.Should().Be(WorkloadVolumeResizeOutcome.Rejected);
        result.Error.Should().Contain("4Gi", "the operator has to be told what it is currently set to");
        await AssertNothingPersistedAsync();
    }

    [Fact]
    public async Task A_corrupted_existing_override_does_not_block_a_resize()
    {
        Seed(parameters: new Dictionary<string, string?>(StringComparer.Ordinal) { [OverrideKey] = "not-a-quantity" });

        (await ResizeAsync("1Gi")).Outcome.Should().Be(WorkloadVolumeResizeOutcome.Success);
    }

    [Fact]
    public async Task An_upward_resize_records_the_override_clears_the_hash_and_redeploys()
    {
        var instance = Seed(parameters: new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["hostPath"] = "/srv/thread",
        });

        var result = await ResizeAsync("2Gi");

        result.Outcome.Should().Be(WorkloadVolumeResizeOutcome.Success);
        result.RequestedSize.Should().Be("2Gi");
        instance.GetResolvedParameters().Should().Contain(new KeyValuePair<string, string?>(OverrideKey, "2Gi"));
        instance.GetResolvedParameters().Should().Contain(new KeyValuePair<string, string?>("hostPath", "/srv/thread"),
            "an unrelated parameter must survive a resize");
        instance.AppliedSpecHash.Should().BeNull();
        instance.State.Should().Be(WorkloadInstanceState.Deploying);
        instance.StateReason.Should().Be(WorkloadStateReason.Redeploying);
        writes.Should().Be(1);
        await clusterBus.Received(1).PublishAsync(
            WorkloadClusterChannels.ReconcileRequest,
            Arg.Is<WorkloadReconcileRequestMessage>(m => m.InstanceId == InstanceId),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_degraded_instance_can_still_be_resized()
    {
        Seed(WorkloadInstanceState.Degraded);

        (await ResizeAsync("2Gi")).Outcome.Should().Be(WorkloadVolumeResizeOutcome.Success);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public async Task A_blank_volume_id_is_refused_as_an_argument_error(string? volumeId)
    {
        var act = async () => await CreateService().RequestResizeAsync(InstanceId, volumeId!, "1Gi", null);

        await act.Should().ThrowAsync<ArgumentException>();
    }

    private sealed class FakeInputsProvider : IWorkloadReconcileInputsProvider
    {
        public WorkloadManifest? Manifest { get; set; }

        public WorkloadProviderDescriptor EffectiveProvider { get; set; } = KubernetesProvider.Descriptor;

        public string? Refusal { get; set; }

        public Task<WorkloadReconcileInputsResult> GetInputsAsync(
            WorkloadInstance instance, CancellationToken cancellationToken = default)
            => ReconcileInputsAnswers.Of(Manifest is null
                ? null
                : new WorkloadReconcileInputs
                {
                    Namer = DefaultWorkloadResourceNamer.Instance,
                    WorkloadManifest = Manifest,
                    ResolvedParameters = instance.GetResolvedParameters(),
                    Settings = new WorkloadOrchestratorSettings(),
                    Bootstrap = new WorkloadBootstrapContext { InstanceId = instance.Id, SecretName = "reeve-wl-secret" },
                    EffectiveProvider = EffectiveProvider,
                    InstallationId = "test-install",
                }, Refusal);
    }
}
