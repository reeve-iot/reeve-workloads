using Reeve.Workloads.Abstractions;
using Reeve.Workloads.Abstractions.Contracts;

namespace Reeve.Workloads.Providers.Docker;

internal sealed class DockerHostAddressContributor : IWorkloadHostAddressContributor
{
    public WorkloadProviderId Provider => DockerProvider.Id;

    public string? AddressOf(HostEndpoint endpoint)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        return $"{endpoint.Name}:{endpoint.Port}";
    }
}
