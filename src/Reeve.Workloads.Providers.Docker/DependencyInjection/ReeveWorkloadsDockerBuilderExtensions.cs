using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Reeve.Workloads.Abstractions;
using Reeve.Workloads.Abstractions.Network;
using Reeve.Workloads.Abstractions.Preview;
using Reeve.Workloads.Abstractions.Providers;
using Reeve.Workloads.Abstractions.Rendering;
using Reeve.Workloads.Core.Network;
using Reeve.Workloads.Core.Watch;
using Reeve.Workloads.Providers.Docker;
using Reeve.Workloads.Providers.Docker.Orphans;
using Reeve.Workloads.Providers.Docker.Rendering;
using Reeve.Workloads.Providers.Docker.Watch;

namespace Reeve.Workloads.DependencyInjection;

/// <summary>The Docker provider: the full collaboration graph, the compose renderer, orphan detection and the
/// events watch source.</summary>
public static class ReeveWorkloadsDockerBuilderExtensions
{
    /// <summary>Registers the Docker provider. Registration is not activation: the provider only runs when the
    /// orchestrator settings select it.</summary>
    /// <param name="builder">The builder returned by <c>AddReeveWorkloads</c>.</param>
    public static ReeveWorkloadsBuilder AddDockerProvider(this ReeveWorkloadsBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder
            .Add<IDockerClientFactory, DockerClientFactory>(ServiceLifetime.Singleton)
            .Add<IDockerImagePuller, DockerImagePuller>(ServiceLifetime.Singleton)
            .Add<IDockerNetworkManager, DockerNetworkManager>(ServiceLifetime.Singleton)
            .Add<IDockerVolumeGroupOwnership, DockerVolumeGroupOwnership>(ServiceLifetime.Singleton)
            .Add<IDockerVolumeManager, DockerVolumeManager>(ServiceLifetime.Singleton)
            .Add<IDockerContainerManager, DockerContainerManager>(ServiceLifetime.Singleton)
            .Add<IDockerPrivilegeRenderer, DockerPrivilegeRenderer>(ServiceLifetime.Singleton)
            .Add<IDockerResourceRenderer, DockerResourceRenderer>(ServiceLifetime.Singleton)
            .Add<IDockerDeploymentValidator, DockerDeploymentValidator>(ServiceLifetime.Singleton)
            .Add<IDockerDaemonGate, DockerDaemonGate>(ServiceLifetime.Singleton)
            .Add<IDockerJournaledRecreate, DockerJournaledRecreate>(ServiceLifetime.Singleton)
            .Add<IDockerLogReader, DockerLogReader>(ServiceLifetime.Singleton)
            .Add<IDockerRollbackCoordinator, DockerRollbackCoordinator>(ServiceLifetime.Singleton)
            .Add<IDockerStatusMapper, DockerStatusMapper>(ServiceLifetime.Singleton)
            .Add<IDockerDeviceDiscoveryProbe, DockerDeviceDiscoveryProbe>(ServiceLifetime.Singleton)
            .AddEnumerable<IWorkloadProviderSettingsCodec, DockerProviderSettingsCodec>(ServiceLifetime.Singleton)
            .AddEnumerable<IWorkloadProviderAutoDetect, DockerAutoDetect>(ServiceLifetime.Singleton)
            .AddEnumerable<IWorkloadResourceNamer, DockerResourceNamer>(ServiceLifetime.Singleton)
            .AddSelf<DockerPreflight>(ServiceLifetime.Singleton)
            .AddEnumerable<IWorkloadProvider, DockerWorkloadProvider>(ServiceLifetime.Singleton)
            .AddEnumerable<IWorkloadProviderProbe, DockerWorkloadProviderProbe>(ServiceLifetime.Singleton)
            .AddEnumerable<IWorkloadHostAddressContributor, DockerHostAddressContributor>(ServiceLifetime.Singleton)
            .AddEnumerable<INetworkAttachmentSource, DockerNetworkAttachmentSource>(ServiceLifetime.Singleton)
            .AddEnumerable<IWorkloadArtifactRenderer, ComposeArtifactRenderer>(ServiceLifetime.Singleton)
            .Add<IDockerSocketUriReader, SettingsDockerSocketUriReader>(ServiceLifetime.Scoped)
            .AddEnumerable<IRetainedVolumeSchemeDeleter, DockerRetainedVolumeDeleter>(ServiceLifetime.Scoped)
            .AddSelf<DockerWorkloadOrphanService>(ServiceLifetime.Scoped)
            .Forward<IDockerWorkloadOrphanService, DockerWorkloadOrphanService>(ServiceLifetime.Scoped)
            .Add<IWorkloadDeviceDiscoveryService, WorkloadDeviceDiscoveryService>(ServiceLifetime.Scoped);

        if (builder.WatchEnabled())
        {
            builder.AddEnumerable<IWorkloadWatchSource, WorkloadDockerEventSource>(ServiceLifetime.Singleton);
        }

        return builder;
    }
}
