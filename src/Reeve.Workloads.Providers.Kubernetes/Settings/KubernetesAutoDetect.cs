using System.Text.Json;
using Reeve.Workloads.Abstractions.Contracts;
using Reeve.Workloads.Abstractions.Providers;

namespace Reeve.Workloads.Providers.Kubernetes;

internal sealed class KubernetesAutoDetect : IWorkloadProviderAutoDetect
{
    private readonly IInClusterEnvironment environment;

    public KubernetesAutoDetect(IInClusterEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(environment);
        this.environment = environment;
    }

    public WorkloadProviderId Provider => KubernetesProvider.Id;

    public int Priority => IWorkloadProviderAutoDetect.InClusterPriority;

    public Task<bool> IsAvailableAsync(JsonElement providerSettings, CancellationToken cancellationToken = default)
        => Task.FromResult(environment.IsInCluster);
}
