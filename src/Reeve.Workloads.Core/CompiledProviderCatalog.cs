using Reeve.Workloads.Abstractions;
using Reeve.Workloads.Abstractions.Contracts;
using Reeve.Workloads.Abstractions.Providers;

namespace Reeve.Workloads.Core;

/// <summary>Implementation of <see cref="ICompiledProviderCatalog"/>: whatever is REGISTERED.</summary>
public sealed class CompiledProviderCatalog : ICompiledProviderCatalog
{
    private readonly IWorkloadProviderRegistry registry;

    public CompiledProviderCatalog(IWorkloadProviderRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        this.registry = registry;
    }

    public IReadOnlyList<WorkloadProviderInfo> GetProviders()
        => [.. registry.Descriptors.Select(d => new WorkloadProviderInfo
        {
            Provider = d.Id,
            DisplayName = d.Label,
            CompiledIn = true,
        })];

    public bool IsCompiledIn(WorkloadProviderId provider)
        => registry.TryResolve(provider) is not null;
}
