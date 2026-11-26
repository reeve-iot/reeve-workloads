using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using k8s;
using k8s.Autorest;
using k8s.Models;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Reeve.Workloads.Abstractions;
using Reeve.Workloads.Abstractions.Contracts;
using Reeve.Workloads.Abstractions.Contracts.Deployment;
using Reeve.Workloads.Abstractions.Domain;
using Reeve.Workloads.Abstractions.Domain.DesiredSpec;
using Reeve.Workloads.Abstractions.Domain.Entities;
using Reeve.Workloads.Domain;
using Reeve.Workloads.Domain.DesiredSpec;
using Reeve.Workloads.Providers.Kubernetes;
using Reeve.Workloads.Providers.Kubernetes.Relocate;
using Xunit;

using Reeve.Workloads.Abstractions.Providers;
using Reeve.Workloads.Tests.Shared;

namespace Reeve.Workloads.Providers.Kubernetes.Tests;

public sealed class WorkloadVolumeStatusReaderTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UnixEpoch;
    private const string PluginTypeId = "acme.thread";
    private const string WorkloadId = "border-router";
    private const string Namespace = "reeve-workloads";

    private readonly InMemoryRepository repository = new();
    private readonly FakeInputsProvider inputsProvider = new();
    private readonly FakePvcInspector pvcInspector = new();
    private readonly FakeClientFactory clientFactory = new();

    [Fact]
    public void Volume_status_desired_spec_uses_the_marker_aware_reference_projection()
    {
        const string binding = "ref:vault:password";
        var instance = SeedInstance();
        instance.SetResolvedParameters(new Dictionary<string, string?> { ["password"] = binding }, Now);
        instance.SetReferenceRolloutState(
            "{\"hashPinnedBindings\":{\"password\":{\"binding\":\"ref:vault:password\",\"isVersionPinned\":false}}}", Now);
        var inputs = ProjectionInputs(instance, new Dictionary<string, string?> { ["password"] = binding });

        var projected = KubernetesWorkloadVolumeStatusReader.ComputeDesiredSpec(instance, inputs);
        var literalBaseline = KubernetesWorkloadVolumeStatusReader.ComputeDesiredSpec(instance,
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

    private KubernetesWorkloadVolumeStatusReader CreateReader()
        => new(repository, inputsProvider, pvcInspector, clientFactory,
            new KubernetesNamespaceResolver(new InClusterEnvironment(_ => false, ""), new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build()),
            NullLogger<KubernetesWorkloadVolumeStatusReader>.Instance);

    private static string PvcName(WorkloadInstance instance, string volumeId)
        => DefaultWorkloadResourceNamer.Instance.VolumeName(
               new WorkloadNamingSubject
               {
                   GroupId = PluginTypeId,
                   WorkloadId = WorkloadId,
                   Inst8 = instance.Inst8,
               },
               volumeId);


    [Fact]
    public async Task Maps_each_declared_volume_to_its_live_pvc_status_by_resource_name()
    {
        var instance = SeedInstance();
        SeedManifestWithTwoVolumes();
        pvcInspector.Statuses[PvcName(instance, "state")] = new PvcLiveStatus
        {
            Name = PvcName(instance, "state"),
            ResizeState = WorkloadVolumeResizeState.Resizing,
            Phase = "Bound",
            RequestedSize = "2Gi",
            ActualCapacity = "1Gi",
            StorageClassName = "local-path",
            ResizeMessage = "expanding",
        };
        pvcInspector.Statuses[PvcName(instance, "cache")] = new PvcLiveStatus
        {
            Name = PvcName(instance, "cache"),
            ResizeState = WorkloadVolumeResizeState.Stable,
            Phase = "Bound",
            RequestedSize = "1Gi",
            ActualCapacity = "1Gi",
            StorageClassName = "local-path",
        };

        var result = await CreateReader().ReadVolumeStatusAsync(instance.Id, CancellationToken.None);

        result.Should().NotBeNull();
        result!.ProviderApplicable.Should().BeTrue();
        result.Volumes.Should().HaveCount(2);

        var state = result.Volumes.Single(v => v.VolumeId == "state");
        state.MountPath.Should().Be("/data");
        state.ResizeState.Should().Be(WorkloadVolumeResizeState.Resizing);
        state.RequestedSize.Should().Be("2Gi");
        state.ActualCapacity.Should().Be("1Gi");
        state.ResizeMessage.Should().Be("expanding");

        var cache = result.Volumes.Single(v => v.VolumeId == "cache");
        cache.MountPath.Should().Be("/cache");
        cache.ResizeState.Should().Be(WorkloadVolumeResizeState.Stable);
        cache.ActualCapacity.Should().Be("1Gi");
    }

    [Fact]
    public async Task A_403_on_one_volume_maps_to_Unknown_with_the_declared_size_as_a_fallback_never_a_false_stable()
    {
        var instance = SeedInstance();
        SeedManifestWithCriticalVolume();
        pvcInspector.Statuses[PvcName(instance, "state")] = new PvcLiveStatus
        {
            Name = PvcName(instance, "state"),
            ResizeState = WorkloadVolumeResizeState.Unknown,
        };

        var result = await CreateReader().ReadVolumeStatusAsync(instance.Id, CancellationToken.None);

        var vol = result!.Volumes.Should().ContainSingle().Subject;
        vol.ResizeState.Should().Be(WorkloadVolumeResizeState.Unknown);
        vol.ResizeState.Should().NotBe(WorkloadVolumeResizeState.Stable);
        vol.ActualCapacity.Should().BeNull("the capacity was not readable — never fabricated");
        vol.RequestedSize.Should().Be("512Mi", "the declared manifest size is a safe fallback for the requested size");
    }

    [Fact]
    public async Task A_claim_the_API_server_answers_404_for_reads_Missing_not_Unknown()
    {
        var instance = SeedInstance();
        SeedManifestWithCriticalVolume();
        var core = Substitute.For<ICoreV1Operations>();
        core.ReadNamespacedPersistentVolumeClaimWithHttpMessagesAsync(
                PvcName(instance, "state"), Namespace, Arg.Any<bool?>(),
                Arg.Any<IReadOnlyDictionary<string, IReadOnlyList<string>>>(), Arg.Any<CancellationToken>())
            .Returns<Task<HttpOperationResponse<V1PersistentVolumeClaim>>>(_ => throw new HttpOperationException("not found")
            {
                Response = new HttpResponseMessageWrapper(new HttpResponseMessage(HttpStatusCode.NotFound), "{}"),
            });
        clientFactory.Client = Substitute.For<IKubernetes>();
        clientFactory.Client.CoreV1.Returns(core);
        var reader = new KubernetesWorkloadVolumeStatusReader(repository, inputsProvider,
            new KubernetesPvcInspector(NullLogger<KubernetesPvcInspector>.Instance), clientFactory,
            new KubernetesNamespaceResolver(new InClusterEnvironment(_ => false, ""), new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build()),
            NullLogger<KubernetesWorkloadVolumeStatusReader>.Instance);

        var result = await reader.ReadVolumeStatusAsync(instance.Id, CancellationToken.None);

        var vol = result!.Volumes.Should().ContainSingle().Subject;
        vol.ResizeState.Should().Be(WorkloadVolumeResizeState.Missing, "a 404 is a known absence, unlike a forbidden read");
        vol.ActualCapacity.Should().BeNull();
        vol.RequestedSize.Should().Be("512Mi", "the declared size is what the recreated claim requests");
    }

    [Fact]
    public async Task A_workload_with_no_volumes_returns_an_applicable_empty_list()
    {
        var instance = SeedInstance();
        SeedManifestWithNoVolumes();

        var result = await CreateReader().ReadVolumeStatusAsync(instance.Id, CancellationToken.None);

        result!.ProviderApplicable.Should().BeTrue();
        result.Volumes.Should().BeEmpty();
    }

    [Fact]
    public async Task A_docker_instance_returns_not_applicable_gracefully_with_no_crash()
    {
        var instance = SeedInstance();
        SeedManifestWithCriticalVolume();
        inputsProvider.EffectiveProvider = StandInProviders.Docker;

        var result = await CreateReader().ReadVolumeStatusAsync(instance.Id, CancellationToken.None);

        result!.ProviderApplicable.Should().BeFalse();
        result.Volumes.Should().BeEmpty();
        result.Message.Should().NotBeNullOrEmpty();
    }

    [Theory]
    [InlineData("docker", "Docker")]
    [InlineData("manual", "Manual")]
    [InlineData("acme-edge", "acme-edge")]
    public async Task A_non_kubernetes_instance_is_told_the_effective_provider_by_its_label(string id, string label)
    {
        var instance = SeedInstance();
        SeedManifestWithCriticalVolume();
        inputsProvider.EffectiveProvider = StandInProviders.For(WorkloadProviderId.Parse(id));

        var result = await CreateReader().ReadVolumeStatusAsync(instance.Id, CancellationToken.None);

        result!.ProviderApplicable.Should().BeFalse();
        result.Message.Should().Contain($"the effective provider is {label})")
            .And.NotContain("WorkloadProviderDescriptor {")
            .And.NotContain("DisplayName =");
    }

    [Fact]
    public async Task An_undeclared_workload_returns_not_applicable()
    {
        var instance = SeedInstance();

        var result = await CreateReader().ReadVolumeStatusAsync(instance.Id, CancellationToken.None);

        result!.ProviderApplicable.Should().BeFalse();
        result.Volumes.Should().BeEmpty();
        result.Message.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task A_host_refusal_returns_not_applicable_with_the_hosts_reason()
    {
        var instance = SeedInstance();
        SeedManifestWithTwoVolumes();
        inputsProvider.Refusal = "host says no";

        var result = await CreateReader().ReadVolumeStatusAsync(instance.Id, CancellationToken.None);

        result!.ProviderApplicable.Should().BeFalse();
        result.Volumes.Should().BeEmpty();
        result.Message.Should().Contain("host says no");
    }

    [Fact]
    public async Task A_client_build_fault_degrades_every_volume_to_Unknown_never_a_500_or_false_stable()
    {
        var instance = SeedInstance();
        SeedManifestWithTwoVolumes();
        clientFactory.Fault = new InvalidOperationException("remote kubeconfig build failed");

        var result = await CreateReader().ReadVolumeStatusAsync(instance.Id, CancellationToken.None);

        result!.ProviderApplicable.Should().BeTrue("the read still applies — it just could not reach the cluster");
        result.Volumes.Should().HaveCount(2);
        result.Volumes.Should().OnlyContain(v => v.ResizeState == WorkloadVolumeResizeState.Unknown,
            "a client-build fault degrades honestly — never a false no-expansion");
        result.Volumes.Should().OnlyContain(v => v.ActualCapacity == null);
    }

    [Fact]
    public async Task No_resolvable_namespace_degrades_every_volume_to_Unknown_without_building_a_client()
    {
        var instance = SeedInstance();
        SeedManifestWithTwoVolumes();
        inputsProvider.SettingsNamespace = null;

        var result = await CreateReader().ReadVolumeStatusAsync(instance.Id, CancellationToken.None);

        result!.ProviderApplicable.Should().BeTrue();
        result.Volumes.Should().HaveCount(2);
        result.Volumes.Should().OnlyContain(v => v.ResizeState == WorkloadVolumeResizeState.Unknown);
        clientFactory.Calls.Should().Be(0, "there is no namespace to read the claims in");
    }

    [Fact]
    public async Task An_unknown_instance_returns_null()
    {
        var result = await CreateReader().ReadVolumeStatusAsync(Guid.NewGuid(), CancellationToken.None);

        result.Should().BeNull("an unknown instance maps to a 404 at the controller");
    }

    [Fact]
    public async Task A_persisted_resize_override_flows_into_requestedSize_and_a_lagging_capacity_shows_resizing()
    {
        var instance = SeedInstance();
        instance.SetResolvedParameters(
            new Dictionary<string, string?> { [$"{WorkloadDesiredSpecFactory.VolumeSizeOverrideParameterPrefix}state"] = "2Gi" }, Now);
        SeedManifestWithCriticalVolume();
        pvcInspector.Statuses[PvcName(instance, "state")] = new PvcLiveStatus
        {
            Name = PvcName(instance, "state"),
            ResizeState = WorkloadVolumeResizeState.FileSystemResizePending,
            Phase = "Bound",
            RequestedSize = "2Gi",
            ActualCapacity = "512Mi",
            ResizeMessage = "Waiting for user to (re-)start a pod to finish file system resize of volume on node.",
        };

        var vol = (await CreateReader().ReadVolumeStatusAsync(instance.Id, CancellationToken.None))!.Volumes.Single();

        vol.RequestedSize.Should().Be("2Gi");
        vol.ActualCapacity.Should().Be("512Mi");
        vol.ResizeState.Should().NotBe(WorkloadVolumeResizeState.Stable, "the operator can SEE the resize is in flight — not swallowed into a log");
    }

    [Fact]
    public async Task A_resize_override_is_visible_as_the_fallback_requestedSize_even_when_the_live_read_is_forbidden()
    {
        var instance = SeedInstance();
        instance.SetResolvedParameters(
            new Dictionary<string, string?> { [$"{WorkloadDesiredSpecFactory.VolumeSizeOverrideParameterPrefix}state"] = "8Gi" }, Now);
        SeedManifestWithCriticalVolume();
        pvcInspector.Statuses[PvcName(instance, "state")] = new PvcLiveStatus
        {
            Name = PvcName(instance, "state"),
            ResizeState = WorkloadVolumeResizeState.Unknown,
        };

        var vol = (await CreateReader().ReadVolumeStatusAsync(instance.Id, CancellationToken.None))!.Volumes.Single();

        vol.ResizeState.Should().Be(WorkloadVolumeResizeState.Unknown);
        vol.RequestedSize.Should().Be("8Gi", "the requested resize is surfaced from the desired override, not the stale 512Mi manifest size");
    }

    [Fact]
    public async Task A_readable_but_unapplied_upward_resize_reports_the_desired_target_and_a_non_stable_state()
    {
        var instance = SeedInstance();
        instance.SetResolvedParameters(
            new Dictionary<string, string?> { [$"{WorkloadDesiredSpecFactory.VolumeSizeOverrideParameterPrefix}state"] = "8Gi" }, Now);
        SeedManifestWithCriticalVolume();
        pvcInspector.Statuses[PvcName(instance, "state")] = new PvcLiveStatus
        {
            Name = PvcName(instance, "state"),
            ResizeState = WorkloadVolumeResizeState.Stable,
            Phase = "Bound",
            RequestedSize = "512Mi",
            ActualCapacity = "512Mi",
        };

        var vol = (await CreateReader().ReadVolumeStatusAsync(instance.Id, CancellationToken.None))!.Volumes.Single();

        vol.ResizeState.Should().NotBe(WorkloadVolumeResizeState.Stable,
            "a refused/pending upward resize is not 'no resize in progress' — the operator must SEE it");
        vol.ResizeState.Should().Be(WorkloadVolumeResizeState.ResizeRequested);
        vol.RequestedSize.Should().Be("8Gi", "the operator's requested target is surfaced, not the un-applied live 512Mi");
        vol.ActualCapacity.Should().Be("512Mi", "the actual live capacity is honest — it has not grown");
    }


    private WorkloadInstance SeedInstance()
    {
        var instance = WorkloadInstance.Create(Guid.NewGuid(), Guid.NewGuid(), PluginTypeId, WorkloadId, Now);
        instance.SetProvider(KubernetesProvider.Id, Now);
        instance.TransitionTo(WorkloadInstanceState.PendingConfiguration, WorkloadStateReason.ConfigRequired, null, Now);
        instance.TransitionTo(WorkloadInstanceState.Deploying, WorkloadStateReason.Provisioning, null, Now);
        instance.TransitionTo(WorkloadInstanceState.Healthy, WorkloadStateReason.HealthObserved, null, Now);
        repository.Add(instance);
        return instance;
    }

    private void SeedManifestWithCriticalVolume()
        => inputsProvider.Manifest = Manifest(new[]
        {
            new WorkloadVolume { Id = "state", MountPath = "/data", Size = "512Mi", Criticality = VolumeCriticality.Critical, Description = "Thread dataset" },
        });

    private void SeedManifestWithTwoVolumes()
        => inputsProvider.Manifest = Manifest(new[]
        {
            new WorkloadVolume { Id = "state", MountPath = "/data", Size = "512Mi", Criticality = VolumeCriticality.Critical, Description = "Thread dataset" },
            new WorkloadVolume { Id = "cache", MountPath = "/cache", Size = "1Gi", Criticality = VolumeCriticality.Cache, Description = "cache" },
        });

    private void SeedManifestWithNoVolumes()
        => inputsProvider.Manifest = Manifest(Array.Empty<WorkloadVolume>());

    private static WorkloadManifest Manifest(WorkloadVolume[] volumes) => new()
    {
        Id = WorkloadId,
        DisplayName = "Border Router",
        Image = new WorkloadImage { Ref = "registry.example/thread-br", Digest = "sha256:" + new string('a', 64) },
        Kind = WorkloadKind.SingleNode,
        Health = new WorkloadHealth { Mode = WorkloadHealthMode.Http, HeartbeatSeconds = 30 },
        Volumes = volumes,
    };


    private sealed class FakePvcInspector : IKubernetesPvcInspector
    {
        public Dictionary<string, PvcLiveStatus> Statuses { get; } = new(StringComparer.Ordinal);

        public Task<PvcLiveStatus?> ReadPvcLiveStatusAsync(IKubernetes client, string ns, string pvcName, CancellationToken ct)
            => Task.FromResult(Statuses.TryGetValue(pvcName, out var s) ? s : null);

        public Task<PvcBinding?> ReadPvcBindingAsync(IKubernetes client, string ns, string pvcName, CancellationToken ct)
            => Task.FromResult<PvcBinding?>(null);

        public Task<bool> DeletePvcAsync(IKubernetes client, string ns, string pvcName, CancellationToken ct)
            => Task.FromResult(true);
    }

    private sealed class FakeInputsProvider : IWorkloadReconcileInputsProvider
    {
        public WorkloadManifest? Manifest { get; set; }
        public WorkloadProviderDescriptor EffectiveProvider { get; set; } = KubernetesProvider.Descriptor;

        public string? Refusal { get; set; }

        public string? SettingsNamespace { get; set; } = Namespace;

        public Task<WorkloadReconcileInputsResult> GetInputsAsync(WorkloadInstance instance, CancellationToken cancellationToken = default)
            => ReconcileInputsAnswers.Of(Manifest is null
                ? null
                : new WorkloadReconcileInputs
                {
                    Namer = DefaultWorkloadResourceNamer.Instance,
                    WorkloadManifest = Manifest,
                    ResolvedParameters = instance.GetResolvedParameters(),
                    Settings = new WorkloadOrchestratorSettings().WithProvider(KubernetesProvider.Id, new KubernetesProviderSettings { Namespace = SettingsNamespace }),
                    Bootstrap = new WorkloadBootstrapContext { InstanceId = instance.Id, SecretName = "reeve-wl-secret" },
                    EffectiveProvider = EffectiveProvider,
                    InstallationId = "test-install",
                }, Refusal);
    }

    private sealed class FakeClientFactory : IKubernetesClientFactory
    {
        private Exception? CountedFault()
        {
            Calls++;
            return Fault;
        }

        public Exception? Fault { get; set; }

        public int Calls { get; private set; }

        public IKubernetes? Client { get; set; }

        public Task<IKubernetes> CreateClientAsync(CancellationToken cancellationToken = default)
            => CountedFault() is not null
                ? Task.FromException<IKubernetes>(Fault)
                : Task.FromResult(Client ?? Substitute.For<IKubernetes>());
    }

    private sealed class InMemoryRepository : IWorkloadInstanceRepository
    {
        private readonly Dictionary<Guid, WorkloadInstance> store = new();

        public void Add(WorkloadInstance instance) => store[instance.Id] = instance;

        public Task<WorkloadInstance?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
            => Task.FromResult(store.TryGetValue(id, out var i) ? i : null);

        public Task<WorkloadInstance?> GetByWorkloadAsync(Guid pluginInstanceId, string workloadId, CancellationToken cancellationToken = default)
            => Task.FromResult<WorkloadInstance?>(store.Values.FirstOrDefault(i => i.GroupInstanceId == pluginInstanceId && i.WorkloadId == workloadId));

        public Task<IReadOnlyList<WorkloadInstance>> ListByPluginInstanceAsync(Guid pluginInstanceId, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<WorkloadInstance>>(store.Values.Where(i => i.GroupInstanceId == pluginInstanceId).ToList());

        public Task<IReadOnlyList<WorkloadInstance>> ListByStateAsync(WorkloadInstanceState state, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<WorkloadInstance>>(store.Values.Where(i => i.State == state).ToList());

        public Task<IReadOnlyList<WorkloadInstance>> ListAllAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<WorkloadInstance>>(store.Values.ToList());

        public Task<IReadOnlyDictionary<WorkloadInstanceState, int>> CountByStateAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyDictionary<WorkloadInstanceState, int>>(
                store.Values.GroupBy(i => i.State).ToDictionary(g => g.Key, g => g.Count()));

        public Task AddAsync(WorkloadInstance instance, CancellationToken cancellationToken = default) { Add(instance); return Task.CompletedTask; }
        public Task UpdateAsync(WorkloadInstance instance, CancellationToken cancellationToken = default) { Add(instance); return Task.CompletedTask; }

        public Task<bool> TrySetRestoreBindingUnresolvedAsync(Guid workloadInstanceId, DateTimeOffset nowUtc, CancellationToken cancellationToken = default)
        {
            if (!store.TryGetValue(workloadInstanceId, out var instance)) { return Task.FromResult(false); }
            instance.SetRestoreBindingUnresolved(true, nowUtc);
            return Task.FromResult(true);
        }

        public Task DeleteAsync(WorkloadInstance instance, CancellationToken cancellationToken = default) { store.Remove(instance.Id); return Task.CompletedTask; }
    }
}
