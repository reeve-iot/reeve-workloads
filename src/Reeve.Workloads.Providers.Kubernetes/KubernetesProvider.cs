using Reeve.Workloads.Abstractions.Contracts;
using Reeve.Workloads.Abstractions.Providers;

namespace Reeve.Workloads.Providers.Kubernetes;

/// <summary>The identity and declared traits of the provider this package ships.</summary>
/// <remarks>Declared here, not in Abstractions: the vocabulary names no provider, so a third-party package declares itself exactly the way this one does. A row written under this id still reads in a
/// build without this package — <see cref="WorkloadProviderId.Parse"/> needs no list to accept it.</remarks>
public static class KubernetesProvider
{
    /// <summary>In-cluster orchestration through the Kubernetes API.</summary>
    public static WorkloadProviderId Id { get; } = WorkloadProviderId.Parse("kubernetes");

    /// <summary>The library's defaults are this provider's traits, so only what an operator reads, the batch status
    /// read, cluster service addresses, user namespaces and privileged device passthrough are declared.</summary>
    public static WorkloadProviderDescriptor Descriptor { get; } = new()
    {
        Id = Id,
        DisplayName = "Kubernetes",
        LocationNoun = "cluster",
        ReadsStatusInBatches = true,
        SupportsUserNamespaces = true,
        DevicePassthroughRequiresPrivileged = true,
    };
}
