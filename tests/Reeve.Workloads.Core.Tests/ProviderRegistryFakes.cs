using NSubstitute;
using Reeve.Workloads.Abstractions.Contracts;
using Reeve.Workloads.Abstractions.Providers;
using Reeve.Workloads.Core.Providers;
using Reeve.Workloads.Providers.Docker;
using Reeve.Workloads.Providers.Kubernetes;
using Reeve.Workloads.Providers.Manual;

namespace Reeve.Workloads.Core.Tests;

internal static class ProviderRegistryFakes
{
    public static WorkloadProviderRegistry RegistryWith(params WorkloadProviderId[] ids)
        => new([.. ids.Select(Provider)]);

    public static WorkloadProviderRegistry FirstPartyRegistry()
        => RegistryWith(KubernetesProvider.Id, DockerProvider.Id, ManualProvider.Id);

    private static IWorkloadProvider Provider(WorkloadProviderId id)
    {
        var provider = Substitute.For<IWorkloadProvider>();
        provider.Descriptor.Returns(ShippedProviders.For(id));
        return provider;
    }
}
