using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Reeve.Workloads.Abstractions;
using Reeve.Workloads.Abstractions.Network;
using Reeve.Workloads.Abstractions.Providers;
using Reeve.Workloads.Abstractions.Rendering;
using Reeve.Workloads.Core.Adoption;
using Reeve.Workloads.Core.Network;
using Reeve.Workloads.Core.Relocate;
using Reeve.Workloads.Core.Watch;
using Reeve.Workloads.Providers.Kubernetes;
using Reeve.Workloads.Providers.Kubernetes.Adoption;
using Reeve.Workloads.Providers.Kubernetes.Relocate;
using Reeve.Workloads.Providers.Kubernetes.Rendering;
using Reeve.Workloads.Providers.Kubernetes.Watch;

namespace Reeve.Workloads.DependencyInjection;

/// <summary>The Kubernetes provider and everything that only makes sense with it: adoption, relocate, PVC
/// status and resize, the manifest artifact renderer and the list-watch source.</summary>
public static class ReeveWorkloadsKubernetesBuilderExtensions
{
    /// <summary>Registers the Kubernetes provider. Registration is not activation: the provider only runs when
    /// the orchestrator settings select it.</summary>
    /// <param name="builder">The builder returned by <c>AddReeveWorkloads</c>.</param>
    public static ReeveWorkloadsBuilder AddKubernetesProvider(this ReeveWorkloadsBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder
            .Add<IKubernetesClientFactory, KubernetesClientFactory>(ServiceLifetime.Singleton)
            .Add<IKubernetesObjectClient, KubernetesObjectClient>(ServiceLifetime.Singleton)
            .AddSelf<KubernetesWorkloadRenderer>(ServiceLifetime.Singleton)
            .AddSelf<UnsupportedPodPatchApplier>(ServiceLifetime.Singleton)
            .AddSelf<KubernetesPatchRevalidator>(ServiceLifetime.Singleton)
            .AddSelf<KubernetesWorkloadPreflightChecker>(ServiceLifetime.Singleton)
            .Add<IKubernetesPodLogSource, KubernetesPodLogSource>(ServiceLifetime.Singleton)
            .Add<IKubernetesLogReader, KubernetesLogReader>(ServiceLifetime.Singleton)
            .Add<IKubernetesClusterInspector, KubernetesClusterInspector>(ServiceLifetime.Singleton)
            .Add<IInClusterEnvironment, InClusterEnvironment>(ServiceLifetime.Singleton)
            .AddSelf<KubernetesNamespaceResolver>(ServiceLifetime.Singleton)
            .AddEnumerable<IWorkloadProviderSettingsCodec, KubernetesProviderSettingsCodec>(ServiceLifetime.Singleton)
            .AddEnumerable<IWorkloadProviderAutoDetect, KubernetesAutoDetect>(ServiceLifetime.Singleton)
            .AddEnumerable<IWorkloadProvider, KubernetesWorkloadProvider>(ServiceLifetime.Singleton)
            .AddEnumerable<IWorkloadProviderProbe, KubernetesWorkloadProviderProbe>(ServiceLifetime.Singleton)
            .AddEnumerable<IWorkloadHostAddressContributor, KubernetesHostAddressContributor>(ServiceLifetime.Singleton)
            .AddEnumerable<INetworkAttachmentSource, KubernetesNetworkAttachmentSource>(ServiceLifetime.Singleton)
            .AddEnumerable<IWorkloadArtifactRenderer, KubernetesArtifactRenderer>(ServiceLifetime.Singleton)
            .AddEnumerable<IWorkloadArtifactRenderer, HelmValuesArtifactRenderer>(ServiceLifetime.Singleton)
            .Add<IWorkloadAdoptionService, KubernetesWorkloadAdoptionService>(ServiceLifetime.Scoped)
            .Add<IKubernetesPvcInspector, KubernetesPvcInspector>(ServiceLifetime.Scoped)
            .Add<IWorkloadRelocateService, KubernetesWorkloadRelocateService>(ServiceLifetime.Scoped)
            .Add<IWorkloadVolumeStatusReader, KubernetesWorkloadVolumeStatusReader>(ServiceLifetime.Scoped)
            .Add<IWorkloadVolumeResizeService, WorkloadVolumeResizeService>(ServiceLifetime.Scoped)
            .AddEnumerable<IRetainedVolumeSchemeDeleter, KubernetesRetainedVolumeDeleter>(ServiceLifetime.Scoped);

        if (builder.WatchEnabled())
        {
            builder.AddEnumerable<IWorkloadWatchSource, WorkloadKubernetesWatchSource>(ServiceLifetime.Singleton);
        }

        return builder;
    }
}
