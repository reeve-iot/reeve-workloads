using Reeve.Workloads.Domain;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using k8s;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Reeve.Workloads.Abstractions;
using Reeve.Workloads.Abstractions.Contracts;
using Reeve.Workloads.Abstractions.Contracts.Deployment;
using Reeve.Workloads.Abstractions.Domain.Entities;
using Reeve.Workloads.Providers.Kubernetes;
using Reeve.Workloads.Providers.Kubernetes.Relocate;
using Xunit;

using Reeve.Workloads.Abstractions.Providers;
using Reeve.Workloads.Tests.Shared;

namespace Reeve.Workloads.Providers.Kubernetes.Tests;

public sealed class KubernetesRetainedVolumeDeleterTests
{
    private const string Namespace = "reeve-workloads";
    private const string PvcName = "reeve-wl-zwave-zwave-js-state-abc12345";
    private static readonly DateTimeOffset Now = DateTimeOffset.UnixEpoch;

    private readonly FakePvcInspector pvcInspector = new();
    private readonly FakeSettingsService settingsService = new();

    private KubernetesRetainedVolumeDeleter CreateDeleter()
    {
        var services = new ServiceCollection();
        services.AddScoped<IWorkloadOrchestratorSettingsService>(_ => settingsService);
        var scopeFactory = services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();

        return new KubernetesRetainedVolumeDeleter(
            new FakeClientFactory(),
            pvcInspector,
            scopeFactory,
            new KubernetesNamespaceResolver(new InClusterEnvironment(_ => false, ""), new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build()),
            NullLogger<KubernetesRetainedVolumeDeleter>.Instance);
    }

    private static RetainedVolume KubernetesRow(string resourceName = PvcName) => RetainedVolume.Record(
        Guid.NewGuid(), "zwave", "zwave-js", "state", resourceName, "1Gi",
        VolumeCriticality.Critical, "Z-Wave bonds", KubernetesProvider.Id,
        RetainedVolumeSchemeId.Kubernetes, Guid.NewGuid(), Guid.NewGuid(), Now);

    private static RetainedVolume DockerRow() => RetainedVolume.Record(
        Guid.NewGuid(), "zwave", "zwave-js", "state", "reeve-zwave-state-abc12345", "1Gi",
        VolumeCriticality.Critical, "Z-Wave bonds", StandInProviders.DockerId,
        RetainedVolumeSchemeId.Docker, Guid.NewGuid(), Guid.NewGuid(), Now);

    [Fact]
    public async Task Delete_succeeds_on_a_k8s_scheme_row_with_a_kubernetes_effective_provider()
    {
        settingsService.EffectiveProvider = KubernetesProvider.Id;
        pvcInspector.DeleteResult = true;

        var freed = await CreateDeleter().TryDeletePhysicalVolumeAsync(KubernetesRow());

        freed.Should().BeTrue("a k8s-scheme PVC on a Kubernetes provider is deleted and its storage freed");
        pvcInspector.DeletedNamespace.Should().Be(Namespace);
        pvcInspector.DeletedName.Should().Be(PvcName);
    }

    [Fact]
    public async Task Delete_of_an_already_absent_pvc_is_idempotent_true()
    {
        settingsService.EffectiveProvider = KubernetesProvider.Id;
        pvcInspector.DeleteResult = true;

        var freed = await CreateDeleter().TryDeletePhysicalVolumeAsync(KubernetesRow());

        freed.Should().BeTrue("an already-absent PVC (404) is idempotent-true (already freed)");
    }

    [Fact]
    public async Task Delete_returns_false_when_the_api_server_is_unreachable()
    {
        settingsService.EffectiveProvider = KubernetesProvider.Id;
        pvcInspector.DeleteResult = false;

        var freed = await CreateDeleter().TryDeletePhysicalVolumeAsync(KubernetesRow());

        freed.Should().BeFalse("an unreachable API server is not a freed state — leave the row active for a retry");
    }

    [Fact]
    public async Task Delete_returns_false_when_the_effective_provider_is_not_kubernetes()
    {
        settingsService.EffectiveProvider = StandInProviders.ManualId;

        var freed = await CreateDeleter().TryDeletePhysicalVolumeAsync(KubernetesRow());

        freed.Should().BeFalse(
            "a k8s-scheme retained PVC cannot be freed under a non-Kubernetes effective provider — fail-closed so " +
            "purge returns 409 instead of falsely reporting the storage freed while the PVC is stranded");
        pvcInspector.DeletedName.Should().BeNull("nothing must be deleted when the provider cannot address the scheme");
    }

    [Fact]
    public async Task Delete_returns_false_for_a_docker_scheme_row_with_no_owning_k8s_deleter()
    {
        settingsService.EffectiveProvider = KubernetesProvider.Id;

        var freed = await CreateDeleter().TryDeletePhysicalVolumeAsync(DockerRow());

        freed.Should().BeFalse("a docker-scheme row has no owning k8s deleter — fail-closed, do not report success");
        pvcInspector.DeletedName.Should().BeNull("a docker-scheme name is never deleted by the k8s deleter");
    }

    private sealed class FakePvcInspector : IKubernetesPvcInspector
    {
        public bool DeleteResult { get; set; } = true;
        public string? DeletedNamespace { get; private set; }
        public string? DeletedName { get; private set; }

        public Task<PvcBinding?> ReadPvcBindingAsync(IKubernetes client, string ns, string pvcName, CancellationToken ct)
            => Task.FromResult<PvcBinding?>(null);

        public Task<PvcLiveStatus?> ReadPvcLiveStatusAsync(IKubernetes client, string ns, string pvcName, CancellationToken ct)
            => Task.FromResult<PvcLiveStatus?>(null);

        public Task<bool> DeletePvcAsync(IKubernetes client, string ns, string pvcName, CancellationToken ct)
        {
            DeletedNamespace = ns;
            DeletedName = pvcName;
            return Task.FromResult(DeleteResult);
        }
    }

    private sealed class FakeClientFactory : IKubernetesClientFactory
    {
        public Task<IKubernetes> CreateClientAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(Substitute.For<IKubernetes>());
    }

    private sealed class FakeSettingsService : IWorkloadOrchestratorSettingsService
    {
        public WorkloadProviderId EffectiveProvider { get; set; } = KubernetesProvider.Id;

        public Task<WorkloadOrchestratorSettingsResponse> GetAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(new WorkloadOrchestratorSettingsResponse
            {
                Settings = new WorkloadOrchestratorSettings()
                    .WithProvider(KubernetesProvider.Id, new KubernetesProviderSettings { Namespace = Namespace }),
                EffectiveProvider = EffectiveProvider,
                AutoDetected = false,
                Providers = Array.Empty<WorkloadProviderInfo>(),
            });


        public Task<WorkloadProviderProbeResult> TestAsync(
            WorkloadProviderId? requestedProvider, CancellationToken cancellationToken = default)
            => throw new NotSupportedException("not used by the deleter tests");
    }
}
