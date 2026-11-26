using Reeve.Workloads.Domain;
using FluentAssertions;
using k8s;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Reeve.Workloads.Abstractions;
using Reeve.Workloads.Core;
using Reeve.Workloads.Abstractions.Contracts;
using Reeve.Workloads.Abstractions.Contracts.Deployment;
using Reeve.Workloads.Abstractions.Domain.DesiredSpec;
using Reeve.Workloads.Abstractions.Domain.Entities;
using Reeve.Workloads.Core.Relocate;
using Reeve.Workloads.Providers.Kubernetes;
using Reeve.Workloads.Providers.Kubernetes.Relocate;

using Reeve.Workloads.Abstractions.Providers;
using Reeve.Workloads.Tests.Shared;

namespace Reeve.Workloads.Providers.Kubernetes.Tests;

public sealed class KubernetesWorkloadRelocateServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 10, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Relocation_desired_spec_uses_the_marker_aware_reference_projection()
    {
        const string binding = "ref:vault:password";
        var instance = Seed();
        instance.SetResolvedParameters(new Dictionary<string, string?> { ["password"] = binding }, Now);
        instance.SetReferenceRolloutState(
            "{\"hashPinnedBindings\":{\"password\":{\"binding\":\"ref:vault:password\",\"isVersionPinned\":false}}}", Now);
        var inputs = ProjectionInputs(instance, new Dictionary<string, string?> { ["password"] = binding });

        var projected = KubernetesWorkloadRelocateService.ComputeDesiredSpec(instance, inputs);
        var literalBaseline = KubernetesWorkloadRelocateService.ComputeDesiredSpec(instance,
            inputs with { ParameterReferencesForHash = new Dictionary<string, string?>() });

        projected.SpecHash.Should().Be(literalBaseline.SpecHash);
    }

    private static WorkloadReconcileInputs ProjectionInputs(WorkloadInstance instance,
        IReadOnlyDictionary<string, string?> references)
        => new()
        {
            Namer = DefaultWorkloadResourceNamer.Instance,
            WorkloadManifest = Manifest(Array.Empty<WorkloadVolume>()),
            ResolvedParameters = instance.GetResolvedParameters(),
            ParameterReferencesForHash = references,
            Settings = new WorkloadOrchestratorSettings().WithProvider(KubernetesProvider.Id,
                new KubernetesProviderSettings { Namespace = Namespace }),
            Bootstrap = new WorkloadBootstrapContext { InstanceId = instance.Id, SecretName = "secret" },
            EffectiveProvider = KubernetesProvider.Descriptor,
            InstallationId = "test-install",
        };
    private static readonly Guid InstanceId = Guid.Parse("6b2f8d10-3c4e-4a7b-8d91-5e6f7a8b9c0d");
    private static readonly Guid GroupInstanceId = Guid.Parse("c1d2e3f4-a5b6-4788-9900-1a2b3c4d5e6f");
    private const string WorkloadId = "border-router";
    private const string Namespace = "reeve-workloads";
    private const string PvcName = "state";

    private readonly InMemoryRepository repository = new();
    private readonly FakeInputsProvider inputsProvider = new();
    private readonly FakePvcInspector pvcInspector = new();
    private readonly FakeClientFactory clientFactory = new();
    private readonly IKubernetesClusterInspector clusterInspector = Substitute.For<IKubernetesClusterInspector>();
    private readonly IWorkloadClusterBus clusterBus = Substitute.For<IWorkloadClusterBus>();
    private readonly IWorkloadReconcileTrigger localTrigger = Substitute.For<IWorkloadReconcileTrigger>();
    private readonly IWorkloadClock clock = Substitute.For<IWorkloadClock>();

    public KubernetesWorkloadRelocateServiceTests()
    {
        clock.UtcNow.Returns(Now);
        inputsProvider.Manifest = Manifest();
        clusterInspector.InspectAsync(Arg.Any<IKubernetes>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new KubernetesClusterInspection
            {
                ApiReachable = true,
                Nodes =
                [
                    new ClusterNodeInfo { Name = "node-a", Labels = new Dictionary<string, string> { ["kubernetes.io/hostname"] = "node-a" } },
                    new ClusterNodeInfo { Name = "node-b", Labels = new Dictionary<string, string> { ["kubernetes.io/hostname"] = "node-b" } },
                ],
                StorageClasses =
                [
                    new ClusterStorageClassInfo { Name = "local-path", Provisioner = "rancher.io/local-path", VolumeBindingMode = "WaitForFirstConsumer", IsDefault = true },
                    new ClusterStorageClassInfo { Name = "ceph-rbd", Provisioner = "rbd.csi.ceph.com", VolumeBindingMode = "Immediate" },
                ],
            }));
        clusterBus.PublishAsync(
                WorkloadClusterChannels.ReconcileRequest,
                Arg.Any<WorkloadReconcileRequestMessage>(),
                Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                callInfo.ArgAt<CancellationToken>(2).ThrowIfCancellationRequested();
                return Task.CompletedTask;
            });
        localTrigger.RequestInstanceAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                callInfo.ArgAt<CancellationToken>(2).ThrowIfCancellationRequested();
                return Task.CompletedTask;
            });
    }

    private KubernetesWorkloadRelocateService CreateService(
        ILogger<KubernetesWorkloadRelocateService>? logger = null) => new(
        repository, inputsProvider, clusterInspector, pvcInspector, clientFactory,
        new WorkloadReconcileNudge(clusterBus, localTrigger), clock,
        new KubernetesNamespaceResolver(new InClusterEnvironment(_ => false, ""), new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build()),
        logger ?? NullLogger<KubernetesWorkloadRelocateService>.Instance, StandInRegistry());

    private static IWorkloadProviderRegistry StandInRegistry()
    {
        var registry = Substitute.For<IWorkloadProviderRegistry>();
        registry.Describe(Arg.Any<WorkloadProviderId>())
            .Returns(call => StandInProviders.For(call.Arg<WorkloadProviderId>()));
        return registry;
    }

    private WorkloadInstance Seed(WorkloadInstanceState state = WorkloadInstanceState.Healthy)
    {
        var instance = WorkloadInstance.Create(
            InstanceId, GroupInstanceId, "acme.thread", WorkloadId, Now - TimeSpan.FromDays(1));
        instance.TransitionTo(WorkloadInstanceState.PendingConfiguration, WorkloadStateReason.ConfigRequired, null, Now);
        instance.TransitionTo(WorkloadInstanceState.Deploying, WorkloadStateReason.Provisioning, null, Now);
        instance.TransitionTo(WorkloadInstanceState.Healthy, WorkloadStateReason.HealthObserved, null, Now);
        instance.SetProvider(KubernetesProvider.Id, Now);
        instance.SetAppliedSpecHash("sha256:applied", Now);
        if (state != WorkloadInstanceState.Healthy)
        {
            instance.TransitionTo(state, WorkloadStateReason.HealthLost, null, Now);
        }

        repository.Add(instance);
        return instance;
    }

    private static WorkloadManifest Manifest(WorkloadVolume[]? volumes = null) => new()
    {
        Id = WorkloadId,
        DisplayName = "Border Router",
        Image = new WorkloadImage { Ref = "registry.example/thread-br", Digest = "sha256:" + new string('a', 64) },
        Kind = WorkloadKind.SingleNode,
        Health = new WorkloadHealth { Mode = WorkloadHealthMode.Http, HeartbeatSeconds = 30 },
        Volumes = volumes ?? [CriticalVolume],
    };

    private static readonly WorkloadVolume CriticalVolume = new()
    {
        Id = PvcName,
        MountPath = "/data",
        Size = "512Mi",
        Criticality = VolumeCriticality.Critical,
        Description = "Thread dataset",
    };

    private void DeclareNoVolumes() => inputsProvider.Manifest = Manifest([]);

    private static Dictionary<string, string> ToNodeB() => new(StringComparer.Ordinal)
    {
        ["kubernetes.io/hostname"] = "node-b",
    };

    private async Task<string> BindPlannedPvcToNodeAAsync()
    {
        await PlanAsync();
        var pvcName = pvcInspector.Read.Distinct().Single();
        pvcInspector.Bindings[pvcName] = new PvcBinding
        {
            Name = pvcName,
            Bound = true,
            Phase = "Bound",
            BoundNode = "node-a",
        };
        return pvcName;
    }

    private Task<RelocatePlan?> PlanAsync(IReadOnlyDictionary<string, string>? selector = null)
        => CreateService().PlanRelocateAsync(InstanceId, selector ?? ToNodeB(), CancellationToken.None);

    private Task<RelocateResult> ExecuteAsync(
        IReadOnlyDictionary<string, string>? selector = null,
        CancellationToken cancellationToken = default)
        => CreateService().ExecuteRelocateAsync(InstanceId, selector ?? ToNodeB(), cancellationToken);

    [Fact]
    public async Task An_unknown_instance_yields_no_plan_and_a_not_found_execute()
    {
        (await PlanAsync()).Should().BeNull();
        (await ExecuteAsync()).Outcome.Should().Be(RelocateOutcome.NotFound);
    }

    [Fact]
    public async Task A_workload_that_is_no_longer_declared_yields_no_plan_and_a_not_found_execute()
    {
        Seed();
        inputsProvider.Manifest = null;

        (await PlanAsync()).Should().BeNull();
        (await ExecuteAsync()).Outcome.Should().Be(RelocateOutcome.NotFound);
    }

    private async Task AssertNothingMutatedAsync(WorkloadInstance instance)
    {
        instance.GetResolvedParameters().Should().BeEmpty("no placement may be written under a refusal");
        instance.State.Should().Be(WorkloadInstanceState.Healthy);
        instance.AppliedSpecHash.Should().Be("sha256:applied");
        repository.Updates.Should().Be(0);
        await clusterBus.DidNotReceiveWithAnyArgs().PublishAsync(default!, default(object)!, default);
    }

    [Fact]
    public async Task A_host_refusal_at_the_provider_check_refuses_the_execute_with_the_hosts_reason()
    {
        var instance = Seed();
        inputsProvider.Refusal = "host says no";

        var result = await ExecuteAsync();

        result.Outcome.Should().Be(RelocateOutcome.HostRefused);
        result.Error.Should().Contain("host says no");
        inputsProvider.InputsAsked.Should().Be(1, "nothing is planned after the refusal");
        await AssertNothingMutatedAsync(instance);
    }

    [Fact]
    public async Task A_host_refusal_at_the_plan_build_refuses_the_execute_with_the_hosts_reason()
    {
        var instance = Seed();
        DeclareNoVolumes();
        inputsProvider.Refusal = "host says no";
        inputsProvider.RefuseFromAsk = 2;

        var result = await ExecuteAsync();

        result.Outcome.Should().Be(RelocateOutcome.HostRefused);
        result.Error.Should().Contain("host says no");
        await AssertNothingMutatedAsync(instance);
    }

    [Fact]
    public async Task A_host_refusal_at_the_parameter_write_refuses_before_anything_is_written()
    {
        var instance = Seed();
        DeclareNoVolumes();
        inputsProvider.Refusal = "host says no";
        inputsProvider.RefuseFromAsk = 3;

        var result = await ExecuteAsync();

        result.Outcome.Should().Be(RelocateOutcome.HostRefused);
        result.Error.Should().Contain("host says no");
        inputsProvider.InputsAsked.Should().Be(3, "the refusal under test is the third ask's");
        await AssertNothingMutatedAsync(instance);
    }

    [Fact]
    public async Task A_workload_gone_by_the_parameter_write_is_not_found_and_nothing_is_written()
    {
        var instance = Seed();
        DeclareNoVolumes();
        inputsProvider.UndeclaredFromAsk = 3;

        var result = await ExecuteAsync();

        result.Outcome.Should().Be(RelocateOutcome.NotFound);
        inputsProvider.InputsAsked.Should().Be(3);
        await AssertNothingMutatedAsync(instance);
    }

    [Fact]
    public async Task A_host_refusal_yields_no_plan_and_logs_the_reason()
    {
        Seed();
        inputsProvider.Refusal = "host says no";
        var logger = new RecordingLogger();

        var plan = await CreateService(logger).PlanRelocateAsync(InstanceId, ToNodeB(), CancellationToken.None);

        plan.Should().BeNull();
        logger.Records.Should().Contain(r => r.Level == LogLevel.Warning && r.Message.Contains("host says no"));
    }

    [Theory]
    [InlineData(WorkloadInstanceState.Unknown)]
    [InlineData(WorkloadInstanceState.Terminating)]
    public async Task An_instance_that_cannot_mutate_is_refused_before_anything_is_planned(
        WorkloadInstanceState state)
    {
        Seed(state);

        var result = await ExecuteAsync();

        result.Outcome.Should().Be(RelocateOutcome.NotMutable);
        inputsProvider.InputsAsked.Should().Be(0, "a refusal this early must not even read the manifest");
    }

    [Theory]
    [InlineData(WorkloadInstanceState.Switching)]
    [InlineData(WorkloadInstanceState.ExternallyManaged)]
    public async Task An_ownership_frozen_instance_is_refused_because_its_runtime_is_operator_owned(
        WorkloadInstanceState state)
    {
        var instance = Seed(WorkloadInstanceState.Healthy);
        instance.TransitionTo(state, WorkloadStateReason.ExternalHealthObserved, null, Now);

        var result = await ExecuteAsync();

        result.Outcome.Should().Be(RelocateOutcome.NotMutable);
        repository.Updates.Should().Be(0);
    }

    [Fact]
    public async Task A_host_held_instance_is_refused_and_keeps_its_hold()
    {
        var instance = Seed(WorkloadInstanceState.Healthy);
        instance.TransitionTo(WorkloadInstanceState.Suspended, WorkloadStateReason.SuspendedByHost, "withdrawn", Now);

        var result = await ExecuteAsync();

        result.Outcome.Should().Be(RelocateOutcome.NotMutable);
        instance.IsHeldByHost.Should().BeTrue("an operator cannot lift the host's hold");
        repository.Updates.Should().Be(0);
    }

    [Theory]
    [InlineData("docker")]
    [InlineData("manual")]
    public async Task A_non_kubernetes_instance_is_refused_and_gets_no_plan(string providerId)
    {
        var provider = StandInProviders.For(WorkloadProviderId.Parse(providerId));
        Seed();
        inputsProvider.EffectiveProvider = provider;

        (await PlanAsync()).Should().BeNull();
        var result = await ExecuteAsync();

        result.Outcome.Should().Be(RelocateOutcome.RelocateNotSupportedByProvider);
        repository.Updates.Should().Be(0);
    }

    [Fact]
    public async Task A_connected_manual_instance_is_told_relocate_does_not_apply_not_that_it_is_disowned()
    {
        var instance = Seed();
        instance.SetProvider(StandInProviders.ManualId, Now);
        instance.TransitionTo(WorkloadInstanceState.ExternallyManaged, WorkloadStateReason.ExternalHealthObserved, null, Now);
        inputsProvider.EffectiveProvider = StandInProviders.Manual;

        var result = await ExecuteAsync();

        result.Outcome.Should().Be(RelocateOutcome.RelocateNotSupportedByProvider);
        repository.Updates.Should().Be(0);
    }

    [Theory]
    [InlineData("docker", "Docker")]
    [InlineData("manual", "Manual")]
    public async Task A_non_kubernetes_refusal_logs_the_provider_by_its_label(string providerId, string label)
    {
        Seed();
        inputsProvider.EffectiveProvider = StandInProviders.For(WorkloadProviderId.Parse(providerId));
        var logger = new RecordingLogger();
        var service = CreateService(logger);

        await service.PlanRelocateAsync(InstanceId, ToNodeB(), CancellationToken.None);
        await service.ExecuteRelocateAsync(InstanceId, ToNodeB(), CancellationToken.None);

        var warnings = logger.Records.Where(r => r.Level == LogLevel.Warning).Select(r => r.Message).ToList();
        warnings.Should().HaveCount(2).And.OnlyContain(m => m.Contains($"effective provider is {label},"));
        warnings.Should().OnlyContain(m => !m.Contains("WorkloadProviderDescriptor {") && !m.Contains("DisplayName ="));
    }

    [Fact]
    public async Task A_placement_that_cannot_be_expressed_is_refused()
    {
        var instance = Seed();
        var lossy = new Dictionary<string, string>(StringComparer.Ordinal) { ["disk"] = "ssd,fast" };

        var plan = await PlanAsync(lossy);
        var result = await ExecuteAsync(lossy);

        plan!.SelectorRepresentable.Should().BeFalse();
        result.Outcome.Should().Be(RelocateOutcome.SelectorNotRepresentable);
        await AssertNothingMutatedAsync(instance);
    }

    [Fact]
    public async Task A_retained_claim_bound_off_the_new_selector_refuses_the_relocate()
    {
        var instance = Seed();
        instance.SetResolvedParameters(
            new Dictionary<string, string?> { ["existingClaim." + PvcName] = "retained-state" }, Now);
        var pvcName = await BindPlannedPvcToNodeAAsync();

        var result = await ExecuteAsync();

        pvcName.Should().Be("retained-state");
        result.Outcome.Should().Be(RelocateOutcome.AdoptedClaimNotRelocatable);
        result.Plan!.AdoptedVolumesBlockingRelocate.Should().ContainSingle().Which.BoundNode.Should().Be("node-a");
        instance.State.Should().Be(WorkloadInstanceState.Healthy);
        repository.Updates.Should().Be(0);
    }

    [Fact]
    public async Task A_relocate_away_from_a_node_bound_volume_reports_it_redeploys_and_deletes_nothing()
    {
        var instance = Seed();
        var pvcName = await BindPlannedPvcToNodeAAsync();

        var plan = await PlanAsync();
        var result = await ExecuteAsync();

        plan!.NodeBoundVolumes.Should().ContainSingle().Which.Should().BeEquivalentTo(new
        {
            VolumeId = PvcName,
            PvcName = pvcName,
            BoundNode = "node-a",
            Criticality = VolumeCriticality.Critical,
            Description = "Thread dataset",
        });
        result.Outcome.Should().Be(RelocateOutcome.Relocated);
        result.RedeployTriggered.Should().BeTrue();
        result.Plan!.NodeBoundVolumes.Should().ContainSingle();
        pvcInspector.Deleted.Should().BeEmpty("relocate never deletes a PVC");
        instance.AppliedSpecHash.Should().BeNull("the reconciler re-applies only when the hash no longer matches");
        instance.State.Should().Be(WorkloadInstanceState.Deploying);
        instance.StateReason.Should().Be(WorkloadStateReason.Redeploying);
        instance.GetResolvedParameters().Values.Should().Contain(
            "kubernetes.io/hostname=node-b", "the placement has to reach the parameters the resolver reads");
        await clusterBus.Received(1).PublishAsync(
            WorkloadClusterChannels.ReconcileRequest,
            Arg.Is<WorkloadReconcileRequestMessage>(m => m.InstanceId == InstanceId),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_volume_whose_claim_does_not_exist_yet_is_not_reported()
    {
        Seed();

        var plan = await PlanAsync();

        plan!.NodeBoundVolumes.Should().BeEmpty();
        plan.UnverifiedVolumes.Should().BeEmpty("a claim that does not exist yet is created where the pod lands");
        pvcInspector.Read.Should().NotBeEmpty("the binding was asked for");
    }

    [Fact]
    public async Task A_volume_whose_binding_cannot_be_read_is_reported_as_unverified()
    {
        Seed();
        await PlanAsync();
        var pvcName = pvcInspector.Read.Distinct().Single();
        pvcInspector.Bindings[pvcName] = new PvcBinding
        {
            Name = pvcName,
            Bound = true,
            Phase = "Bound",
            BindingUnreadable = true,
        };

        var result = await ExecuteAsync();

        result.Outcome.Should().Be(RelocateOutcome.Relocated);
        result.Plan!.NodeBoundVolumes.Should().BeEmpty();
        result.Plan.UnverifiedVolumes.Should().ContainSingle().Which.Should().BeEquivalentTo(new
        {
            VolumeId = PvcName,
            PvcName = pvcName,
            Criticality = VolumeCriticality.Critical,
            Description = "Thread dataset",
        });
    }

    [Fact]
    public async Task A_relocate_with_no_namespace_reports_nothing_and_still_relocates()
    {
        Seed();
        await BindPlannedPvcToNodeAAsync();
        inputsProvider.SettingsNamespace = null;

        var result = await ExecuteAsync();

        result.Outcome.Should().Be(RelocateOutcome.Relocated);
        result.Plan!.NodeBoundVolumes.Should().BeEmpty();
        result.Plan.UnverifiedVolumes.Should().ContainSingle().Which.VolumeId.Should().Be(PvcName);
        pvcInspector.Deleted.Should().BeEmpty();
    }

    [Fact]
    public async Task A_client_that_cannot_be_built_reports_nothing_and_still_relocates()
    {
        var instance = Seed();
        await BindPlannedPvcToNodeAAsync();
        clientFactory.Fault = new HttpRequestException("kubeconfig host unreachable");

        var result = await ExecuteAsync();

        result.Outcome.Should().Be(RelocateOutcome.Relocated);
        result.Plan!.NodeBoundVolumes.Should().BeEmpty();
        result.Plan.UnverifiedVolumes.Should().ContainSingle().Which.VolumeId.Should().Be(PvcName);
        instance.State.Should().Be(WorkloadInstanceState.Deploying);
    }

    [Fact]
    public async Task Request_cancellation_after_the_placement_write_does_not_cancel_the_nudge()
    {
        Seed();
        using var requestCts = new CancellationTokenSource();
        repository.OnUpdate = requestCts.Cancel;

        var result = await ExecuteAsync(cancellationToken: requestCts.Token);

        result.Outcome.Should().Be(RelocateOutcome.Relocated);
        await clusterBus.Received(1).PublishAsync(
            WorkloadClusterChannels.ReconcileRequest,
            Arg.Is<WorkloadReconcileRequestMessage>(m => m.InstanceId == InstanceId),
            Arg.Is<CancellationToken>(token => !token.IsCancellationRequested));
    }

    [Fact]
    public async Task A_lost_update_race_propagates_and_nudges_nothing()
    {
        Seed();
        repository.UpdateFault = new WorkloadConcurrencyConflictException("another writer advanced the instance");

        var execute = () => ExecuteAsync();

        await execute.Should().ThrowAsync<WorkloadConcurrencyConflictException>();
        await clusterBus.DidNotReceiveWithAnyArgs().PublishAsync(default!, default(object)!, default);
    }

    [Fact]
    public async Task An_instance_already_deploying_keeps_its_state_and_only_refreshes_the_reason()
    {
        var instance = Seed(WorkloadInstanceState.Degraded);
        instance.TransitionTo(WorkloadInstanceState.Deploying, WorkloadStateReason.Redeploying, null, Now);
        DeclareNoVolumes();

        var result = await ExecuteAsync();

        result.Outcome.Should().Be(RelocateOutcome.Relocated);
        instance.State.Should().Be(WorkloadInstanceState.Deploying);
        instance.StateReason.Should().Be(WorkloadStateReason.Redeploying);
    }

    [Fact]
    public async Task A_missing_selector_is_refused_by_both_entry_points_before_anything_is_read()
    {
        Seed();
        var service = CreateService();

        var plan = async () => await service.PlanRelocateAsync(InstanceId, null!, CancellationToken.None);
        await plan.Should().ThrowAsync<ArgumentNullException>();
        inputsProvider.InputsAsked.Should().Be(0);

        var execute = async () => await service.ExecuteRelocateAsync(InstanceId, null!, CancellationToken.None);
        await execute.Should().ThrowAsync<ArgumentNullException>();
        inputsProvider.InputsAsked.Should().Be(0);
    }

    [Fact]
    public async Task A_network_volume_with_no_class_and_no_cluster_default_is_not_deleted_by_a_relocate()
    {
        var instance = Seed();
        inputsProvider.Manifest = Manifest([OtaImagesVolume]);
        inputsProvider.DefaultStorageClassName = "csi-rbd-sc";
        clusterInspector.InspectAsync(Arg.Any<IKubernetes>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new KubernetesClusterInspection
            {
                ApiReachable = true,
                Nodes =
                [
                    new ClusterNodeInfo { Name = "node-a", Labels = new Dictionary<string, string> { ["kubernetes.io/hostname"] = "node-a" } },
                    new ClusterNodeInfo { Name = "node-b", Labels = new Dictionary<string, string> { ["kubernetes.io/hostname"] = "node-b", [VfLabel] = "true" } },
                ],
                StorageClasses =
                [
                    new ClusterStorageClassInfo { Name = "csi-rbd-sc", Provisioner = "rbd.csi.ceph.com", VolumeBindingMode = "Immediate" },
                    new ClusterStorageClassInfo { Name = "nfs-csi", Provisioner = "nfs.csi.k8s.io", VolumeBindingMode = "Immediate" },
                ],
            }));
        var selector = new Dictionary<string, string>(StringComparer.Ordinal) { [VfLabel] = "true" };

        var plan = await PlanAsync(selector);
        var result = await ExecuteAsync(selector);

        plan!.NodeBoundVolumes.Should().BeEmpty("a volume nothing proves node-bound is assumed to follow the pod");
        result.Outcome.Should().Be(RelocateOutcome.Relocated);
        pvcInspector.Deleted.Should().BeEmpty("relocate never deletes a PVC");
        instance.State.Should().Be(WorkloadInstanceState.Deploying);
    }

    private const string VfLabel = "feature.node.kubernetes.io/vlan100-pod-vfs";

    private static readonly WorkloadVolume OtaImagesVolume = new()
    {
        Id = "ota-images",
        MountPath = "/ota",
        Size = "1Gi",
        Criticality = VolumeCriticality.Cache,
    };

    private sealed class RecordingLogger : ILogger<KubernetesWorkloadRelocateService>
    {
        public List<(LogLevel Level, string Message)> Records { get; } = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
            => Records.Add((logLevel, formatter(state, exception)));
    }

    private sealed class FakeClientFactory : IKubernetesClientFactory
    {
        public Exception? Fault { get; set; }

        public Task<IKubernetes> CreateClientAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Fault is not null
                ? Task.FromException<IKubernetes>(Fault)
                : Task.FromResult(Substitute.For<IKubernetes>());
        }
    }

    private sealed class FakePvcInspector : IKubernetesPvcInspector
    {
        public Dictionary<string, PvcBinding> Bindings { get; } = new(StringComparer.Ordinal);

        public List<string> Read { get; } = [];

        public List<string> Deleted { get; } = [];

        public Task<PvcLiveStatus?> ReadPvcLiveStatusAsync(IKubernetes client, string ns, string pvcName, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult<PvcLiveStatus?>(null);
        }

        public Task<PvcBinding?> ReadPvcBindingAsync(IKubernetes client, string ns, string pvcName, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            Read.Add(pvcName);
            return Task.FromResult(Bindings.TryGetValue(pvcName, out var b) ? b : null);
        }

        public Task<bool> DeletePvcAsync(IKubernetes client, string ns, string pvcName, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            Deleted.Add(pvcName);
            return Task.FromResult(true);
        }
    }

    private sealed class FakeInputsProvider : IWorkloadReconcileInputsProvider
    {
        public WorkloadManifest? Manifest { get; set; }

        public WorkloadProviderDescriptor EffectiveProvider { get; set; } = KubernetesProvider.Descriptor;

        public string? SettingsNamespace { get; set; } = Namespace;

        public string? DefaultStorageClassName { get; set; }

        public int InputsAsked { get; private set; }

        public string? Refusal { get; set; }

        public int RefuseFromAsk { get; set; } = 1;

        public int UndeclaredFromAsk { get; set; } = int.MaxValue;

        public Task<WorkloadReconcileInputsResult> GetInputsAsync(
            WorkloadInstance instance, CancellationToken cancellationToken = default)
        {
            InputsAsked++;
            if (Refusal is { } refusal && InputsAsked >= RefuseFromAsk)
            {
                return Task.FromResult(WorkloadReconcileInputsResult.HostRefused(refusal));
            }

            return ReconcileInputsAnswers.Of(Manifest is null || InputsAsked >= UndeclaredFromAsk
                ? null
                : new WorkloadReconcileInputs
                {
                    Namer = DefaultWorkloadResourceNamer.Instance,
                    WorkloadManifest = Manifest,
                    ResolvedParameters = instance.GetResolvedParameters(),
                    Settings = new WorkloadOrchestratorSettings()
                        .WithProvider(KubernetesProvider.Id, new KubernetesProviderSettings
                        {
                            Namespace = SettingsNamespace,
                            DefaultStorageClassName = DefaultStorageClassName,
                        }),
                    Bootstrap = new WorkloadBootstrapContext { InstanceId = instance.Id, SecretName = "reeve-wl-secret" },
                    EffectiveProvider = EffectiveProvider,
                    InstallationId = "test-install",
                });
        }
    }

    private sealed class InMemoryRepository : IWorkloadInstanceRepository
    {
        private readonly Dictionary<Guid, WorkloadInstance> store = [];

        public void Add(WorkloadInstance instance) => store[instance.Id] = instance;

        public Task<WorkloadInstance?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
            => Task.FromResult(store.TryGetValue(id, out var i) ? i : null);

        public Task<WorkloadInstance?> GetByWorkloadAsync(Guid pluginInstanceId, string workloadId, CancellationToken cancellationToken = default)
            => Task.FromResult(store.Values.FirstOrDefault(i => i.GroupInstanceId == pluginInstanceId && i.WorkloadId == workloadId));

        public Task<IReadOnlyList<WorkloadInstance>> ListByPluginInstanceAsync(Guid pluginInstanceId, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<WorkloadInstance>>(store.Values.Where(i => i.GroupInstanceId == pluginInstanceId).ToList());

        public Task<IReadOnlyList<WorkloadInstance>> ListByStateAsync(WorkloadInstanceState state, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<WorkloadInstance>>(store.Values.Where(i => i.State == state).ToList());

        public Task<IReadOnlyList<WorkloadInstance>> ListAllAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<WorkloadInstance>>(store.Values.ToList());

        public Task<IReadOnlyDictionary<WorkloadInstanceState, int>> CountByStateAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyDictionary<WorkloadInstanceState, int>>(
                store.Values.GroupBy(i => i.State).ToDictionary(g => g.Key, g => g.Count()));

        public Task AddAsync(WorkloadInstance instance, CancellationToken cancellationToken = default)
        {
            Add(instance);
            return Task.CompletedTask;
        }

        public int Updates { get; private set; }
        public Exception? UpdateFault { get; set; }
        public Action? OnUpdate { get; set; }

        public Task UpdateAsync(WorkloadInstance instance, CancellationToken cancellationToken = default)
        {
            Updates++;
            if (UpdateFault is { } fault)
            {
                throw fault;
            }

            Add(instance);
            OnUpdate?.Invoke();
            return Task.CompletedTask;
        }

        public Task<bool> TrySetRestoreBindingUnresolvedAsync(Guid workloadInstanceId, DateTimeOffset nowUtc, CancellationToken cancellationToken = default)
        {
            if (!store.TryGetValue(workloadInstanceId, out var instance))
            {
                return Task.FromResult(false);
            }

            instance.SetRestoreBindingUnresolved(true, nowUtc);
            return Task.FromResult(true);
        }

        public Task DeleteAsync(WorkloadInstance instance, CancellationToken cancellationToken = default)
        {
            store.Remove(instance.Id);
            return Task.CompletedTask;
        }
    }
}
