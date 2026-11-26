using Reeve.Workloads.Abstractions;
using Reeve.Workloads.Abstractions.Contracts;
using Reeve.Workloads.Abstractions.Providers;

namespace Reeve.Workloads.Core;

internal sealed class EffectiveProviderResolver
{
    private readonly ICompiledProviderCatalog catalog;
    private readonly IReadOnlyList<IWorkloadProviderAutoDetect> autoDetects;
    private readonly WorkloadOrchestratorSettingsCache? cache;

    public EffectiveProviderResolver(
        ICompiledProviderCatalog catalog,
        IEnumerable<IWorkloadProviderAutoDetect> autoDetects,
        WorkloadOrchestratorSettingsCache? cache)
    {
        this.catalog = catalog;
        this.autoDetects = [.. autoDetects.OrderBy(d => d.Priority).ThenBy(d => d.Provider)];
        this.cache = cache;
    }

    public async Task<(WorkloadProviderId Provider, bool AutoDetected)> ResolveAsync(
        WorkloadOrchestratorSettings settings,
        CancellationToken cancellationToken)
    {
        if (settings.Provider is { } chosen)
        {
            return (chosen, false);
        }

        var configured = settings.ConfiguredProviders;

        foreach (var detect in autoDetects)
        {
            if (!catalog.IsCompiledIn(detect.Provider) || (configured is not null && !configured.Contains(detect.Provider)))
            {
                continue;
            }

            var entry = settings.Entry(detect.Provider);
            var available = cache is null
                ? await detect.IsAvailableAsync(entry, cancellationToken).ConfigureAwait(false)
                : await cache.IsProviderAvailableAsync(
                    detect.Provider,
                    entry,
                    ct => detect.IsAvailableAsync(entry, ct),
                    cancellationToken).ConfigureAwait(false);
            if (available)
            {
                return (detect.Provider, true);
            }
        }

        return (catalog.GetProviders()
            .Where(p => configured is null || configured.Contains(p.Provider))
            .FirstOrDefault()?.Provider ?? default, true);
    }
}
