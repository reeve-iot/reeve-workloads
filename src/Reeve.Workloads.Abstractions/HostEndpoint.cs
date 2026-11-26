using Reeve.Workloads.Abstractions.Contracts;

namespace Reeve.Workloads.Abstractions;

/// <summary>A host service a workload has to reach, named the way the host deploys it: the Service or container
/// name, the port it listens on, and, on a runtime with namespaces, the namespace it lives in.</summary>
/// <param name="Name">The Service name on Kubernetes, the container hostname on a Docker network.</param>
/// <param name="Port">The port the service listens on, as a workload dials it.</param>
/// <param name="Namespace">The namespace the service lives in. Null means the namespace of the pod the library
/// itself runs in, on a runtime that has one.</param>
public sealed record HostEndpoint(string Name, int Port, string? Namespace = null);

/// <summary>One provider's answer to "at what <c>host:port</c> does a workload of mine reach this host
/// service?". Ships with the provider and is registered with it, so a third-party provider answers for itself.</summary>
public interface IWorkloadHostAddressContributor
{
    /// <summary>The provider this contributor answers for.</summary>
    WorkloadProviderId Provider { get; }

    /// <summary>The <c>host:port</c> a workload on this runtime dials to reach <paramref name="endpoint"/>, or null
    /// when this runtime cannot say. Pure and total: the same endpoint always gives the same answer, and it
    /// never throws for an endpoint it cannot place.</summary>
    string? AddressOf(HostEndpoint endpoint);
}

/// <summary>The library's lookup over the registered <see cref="IWorkloadHostAddressContributor"/>s. It knows
/// no scheme and no path: the host composes its own URL around the address.</summary>
public interface IWorkloadHostAddresses
{
    /// <summary>The <c>host:port</c> a workload on <paramref name="provider"/> dials to reach
    /// <paramref name="endpoint"/>, or null when that provider registered no contributor or its contributor
    /// cannot say.</summary>
    string? AddressOf(WorkloadProviderId provider, HostEndpoint endpoint);
}
