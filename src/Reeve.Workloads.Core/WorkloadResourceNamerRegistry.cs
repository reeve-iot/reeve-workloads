using Reeve.Workloads.Abstractions.Contracts;
using Reeve.Workloads.Abstractions.Providers;
using Reeve.Workloads.Domain;

namespace Reeve.Workloads.Core;

/// <summary>Implementation of <see cref="IWorkloadResourceNamerRegistry"/>: whatever namers the referenced
/// provider packages registered, and the library's default for every provider that registered none.</summary>
/// <remarks>Total by construction. A provider with no namer is the common case — the Kubernetes arm ships none,
/// because the default namer IS the scheme its resources already carry — and a workload that could not be named
/// at all could not be materialized, so there is no "no namer" answer to give.</remarks>
public sealed class WorkloadResourceNamerRegistry : IWorkloadResourceNamerRegistry
{
    private readonly IReadOnlyDictionary<WorkloadProviderId, IWorkloadResourceNamer> byProvider;

    public WorkloadResourceNamerRegistry(IEnumerable<IWorkloadResourceNamer> namers)
    {
        ArgumentNullException.ThrowIfNull(namers);

        var map = new Dictionary<WorkloadProviderId, IWorkloadResourceNamer>();
        foreach (var namer in namers)
        {
            if (!namer.Provider.IsEmpty)
            {
                map[namer.Provider] = namer;
            }
        }

        byProvider = map;
    }

    /// <inheritdoc />
    public IWorkloadResourceNamer For(WorkloadProviderId provider)
        => byProvider.TryGetValue(provider, out var namer) ? namer : DefaultWorkloadResourceNamer.Instance;
}
