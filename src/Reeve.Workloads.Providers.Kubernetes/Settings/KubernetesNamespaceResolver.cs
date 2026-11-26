using Microsoft.Extensions.Configuration;
using Reeve.Workloads.Abstractions.Contracts;

namespace Reeve.Workloads.Providers.Kubernetes;

internal sealed class KubernetesNamespaceResolver
{
    private readonly IInClusterEnvironment environment;
    private readonly IConfiguration configuration;

    public KubernetesNamespaceResolver(IInClusterEnvironment environment, IConfiguration configuration)
    {
        this.environment = environment ?? throw new ArgumentNullException(nameof(environment));
        this.configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
    }

    public string? Resolve(KubernetesProviderSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        if (!string.IsNullOrWhiteSpace(settings.Namespace))
        {
            return settings.Namespace.Trim();
        }

        var inCluster = KubernetesConnectionResolver.Resolve(configuration, settings).Mode == KubernetesConnectionKind.InCluster;
        return inCluster && !string.IsNullOrWhiteSpace(environment.Namespace)
            ? environment.Namespace.Trim()
            : null;
    }

    public string? Resolve(WorkloadOrchestratorSettings settings)
        => Resolve(KubernetesProviderSettings.From(settings));
}
