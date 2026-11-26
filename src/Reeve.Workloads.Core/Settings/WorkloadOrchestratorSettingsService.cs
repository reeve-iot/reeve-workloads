using Reeve.Workloads.Abstractions;
using Reeve.Workloads.Abstractions.Contracts;
using Reeve.Workloads.Abstractions.Providers;

namespace Reeve.Workloads.Core;

/// <summary>Reads the <c>workload-orchestrator</c> settings the host serves, masked for the API and plaintext for
/// the provider plane, with the effective provider from the registered <see cref="IWorkloadProviderAutoDetect"/>
/// chain.</summary>
public sealed class WorkloadOrchestratorSettingsService : IWorkloadOrchestratorSettingsService, IWorkloadOrchestratorSecretReader
{
    /// <summary>The redaction sentinel emitted for secret values on read. Kept as an alias of the vocabulary's own
    /// literal so a host that referenced it still compiles.</summary>
    public const string MaskSentinel = WorkloadSecretMask.Sentinel;

    private readonly IWorkloadOrchestratorSettingsSource source;
    private readonly IWorkloadOrchestratorSettingsRules rules;
    private readonly ICompiledProviderCatalog catalog;
    private readonly IReadOnlyDictionary<WorkloadProviderId, IWorkloadProviderProbe> probes;
    private readonly EffectiveProviderResolver resolver;

    public WorkloadOrchestratorSettingsService(
        IWorkloadOrchestratorSettingsSource source,
        IWorkloadOrchestratorSettingsRules rules,
        ICompiledProviderCatalog catalog,
        IEnumerable<IWorkloadProviderProbe> probes,
        IEnumerable<IWorkloadProviderAutoDetect> autoDetects,
        WorkloadOrchestratorSettingsCache? cache = null)
    {
        this.source = source;
        this.rules = rules;
        this.catalog = catalog;
        var byProvider = new Dictionary<WorkloadProviderId, IWorkloadProviderProbe>();
        foreach (var probe in probes)
        {
            byProvider[probe.Provider] = probe;
        }

        this.probes = byProvider;
        resolver = new EffectiveProviderResolver(catalog, autoDetects, cache);
    }

    public async Task<WorkloadOrchestratorSettingsResponse> GetAsync(CancellationToken cancellationToken = default)
    {
        var document = await source.GetAsync(cancellationToken).ConfigureAwait(false);
        var (effective, autoDetected) = await resolver.ResolveAsync(document, cancellationToken).ConfigureAwait(false);

        return new WorkloadOrchestratorSettingsResponse
        {
            Settings = rules.Mask(document),
            EffectiveProvider = effective,
            AutoDetected = autoDetected,
            Providers = catalog.GetProviders(),
        };
    }

    /// <inheritdoc />
    /// <remarks>INTERNAL provider-plane use only — returns real plaintext secrets; never feed this into an API response. <see cref="GetAsync"/> stays the masked API read path.</remarks>
    public async Task<WorkloadOrchestratorSettings> GetDecryptedSettingsAsync(CancellationToken cancellationToken = default)
        => await source.GetAsync(cancellationToken).ConfigureAwait(false);

    public async Task<WorkloadProviderProbeResult> TestAsync(WorkloadProviderId? requestedProvider, CancellationToken cancellationToken = default)
    {
        var document = await source.GetAsync(cancellationToken).ConfigureAwait(false);
        var target = requestedProvider ?? (await resolver.ResolveAsync(document, cancellationToken).ConfigureAwait(false)).Provider;

        if (!probes.TryGetValue(target, out var probe))
        {
            return new WorkloadProviderProbeResult
            {
                Provider = target,
                ProbeAvailable = false,
                Message = $"No capability probe is registered for the '{target}' provider in this build.",
            };
        }

        return await probe.ProbeAsync(document, cancellationToken).ConfigureAwait(false);
    }
}
