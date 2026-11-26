using Reeve.Workloads.Abstractions;
using Reeve.Workloads.Abstractions.Contracts;

namespace Reeve.Workloads.Providers.Kubernetes;

internal sealed class KubernetesHostAddressContributor : IWorkloadHostAddressContributor
{
    private readonly IInClusterEnvironment environment;

    public KubernetesHostAddressContributor(IInClusterEnvironment environment)
    {
        this.environment = environment ?? throw new ArgumentNullException(nameof(environment));
    }

    public WorkloadProviderId Provider => KubernetesProvider.Id;

    public string? AddressOf(HostEndpoint endpoint)
    {
        ArgumentNullException.ThrowIfNull(endpoint);

        var ns = endpoint.Namespace ?? environment.Namespace;
        return string.IsNullOrWhiteSpace(ns)
            ? null
            : $"{endpoint.Name}.{ns.Trim()}.svc.cluster.local:{endpoint.Port}";
    }
}
