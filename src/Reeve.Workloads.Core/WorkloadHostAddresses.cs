using Reeve.Workloads.Abstractions;
using Reeve.Workloads.Abstractions.Contracts;

namespace Reeve.Workloads.Core;

internal sealed class WorkloadHostAddresses : IWorkloadHostAddresses
{
    private readonly Dictionary<WorkloadProviderId, IWorkloadHostAddressContributor> byProvider = [];

    public WorkloadHostAddresses(IEnumerable<IWorkloadHostAddressContributor> contributors)
    {
        ArgumentNullException.ThrowIfNull(contributors);

        foreach (var contributor in contributors)
        {
            byProvider[contributor.Provider] = contributor;
        }
    }

    public string? AddressOf(WorkloadProviderId provider, HostEndpoint endpoint)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        return byProvider.TryGetValue(provider, out var contributor) ? contributor.AddressOf(endpoint) : null;
    }
}
