using Reeve.Workloads.Abstractions.Contracts;
using Reeve.Workloads.Abstractions.Providers;
using Reeve.Workloads.Providers.Docker;
using Reeve.Workloads.Providers.Kubernetes;
using Reeve.Workloads.Providers.Manual;

namespace Reeve.Workloads.Core.Tests;

internal static class ShippedProviders
{
    public static WorkloadProviderDescriptor For(WorkloadProviderId id)
        => id == KubernetesProvider.Id ? KubernetesProvider.Descriptor
            : id == DockerProvider.Id ? DockerProvider.Descriptor
            : id == ManualProvider.Id ? ManualProvider.Descriptor
            : new WorkloadProviderDescriptor { Id = id };
}
