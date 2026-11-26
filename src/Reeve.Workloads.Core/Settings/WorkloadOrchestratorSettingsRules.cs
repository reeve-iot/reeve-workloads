using System.Text.Json;
using Reeve.Workloads.Abstractions;
using Reeve.Workloads.Abstractions.Contracts;
using Reeve.Workloads.Abstractions.Providers;

namespace Reeve.Workloads.Core;

/// <summary>The pure rules over a settings document. It knows no provider by name: each provider's entry is
/// validated, masked and resolved by that provider's <see cref="IWorkloadProviderSettingsCodec"/>.</summary>
public sealed class WorkloadOrchestratorSettingsRules : IWorkloadOrchestratorSettingsRules
{
    private readonly ICompiledProviderCatalog catalog;
    private readonly IReadOnlyDictionary<WorkloadProviderId, IWorkloadProviderSettingsCodec> codecs;

    public WorkloadOrchestratorSettingsRules(ICompiledProviderCatalog catalog, IEnumerable<IWorkloadProviderSettingsCodec> codecs)
    {
        this.catalog = catalog;
        var byProvider = new Dictionary<WorkloadProviderId, IWorkloadProviderSettingsCodec>();
        foreach (var codec in codecs)
        {
            byProvider[codec.Provider] = codec;
        }

        this.codecs = byProvider;
    }

    public IReadOnlyList<WorkloadSettingsProblem> Validate(WorkloadOrchestratorSettings document)
    {
        ArgumentNullException.ThrowIfNull(document);

        var problems = new List<WorkloadSettingsProblem>();

        if (document.Provider is { } provider && !catalog.IsCompiledIn(provider))
        {
            var info = catalog.GetProviders().FirstOrDefault(p => p.Provider == provider);
            var reason = string.IsNullOrWhiteSpace(info?.UnavailableReason)
                ? "it is not compiled into this build."
                : info!.UnavailableReason;
            problems.Add(new WorkloadSettingsProblem($"The '{provider}' provider cannot be selected: {reason}"));
        }

        for (var i = 0; i < document.Mirrors.Count; i++)
        {
            var mirror = document.Mirrors[i];
            if (string.IsNullOrWhiteSpace(mirror.MatchRegistry))
            {
                problems.Add(new WorkloadSettingsProblem($"mirrors[{i}].matchRegistry must not be empty."));
            }

            if (string.IsNullOrWhiteSpace(mirror.MirrorRegistry))
            {
                problems.Add(new WorkloadSettingsProblem($"mirrors[{i}].mirrorRegistry must not be empty."));
            }
        }

        foreach (var (configured, entry) in document.Providers)
        {
            if (codecs.TryGetValue(configured, out var codec))
            {
                var result = codec.Validate(entry);
                if (!result.IsValid)
                {
                    problems.Add(new WorkloadSettingsProblem(result.Message ?? $"The '{configured}' provider rejected its settings."));
                }
            }
        }

        return problems;
    }

    public WorkloadOrchestratorSettings Mask(WorkloadOrchestratorSettings document)
    {
        ArgumentNullException.ThrowIfNull(document);
        return document with { Providers = MapEntries(document, (codec, entry) => codec.Mask(entry)) };
    }

    public WorkloadOrchestratorSettings ResolveMasked(WorkloadOrchestratorSettings submitted, WorkloadOrchestratorSettings stored)
    {
        ArgumentNullException.ThrowIfNull(submitted);
        ArgumentNullException.ThrowIfNull(stored);

        return submitted with
        {
            Providers = MapEntries(submitted, (codec, entry) =>
            {
                var prior = stored.Entry(codec.Provider);
                return codec.ResolveMasked(entry, prior.ValueKind is JsonValueKind.Undefined ? null : prior);
            }),
        };
    }

    public IReadOnlyList<WorkloadSecretField> Secrets(WorkloadOrchestratorSettings document)
    {
        ArgumentNullException.ThrowIfNull(document);

        var secrets = new List<WorkloadSecretField>();
        foreach (var (provider, entry) in document.Providers)
        {
            if (codecs.TryGetValue(provider, out var codec))
            {
                secrets.AddRange(codec.SecretFields(entry).Select(f => new WorkloadSecretField(provider, f.Path, f.Value)));
            }
        }

        return secrets;
    }

    private Dictionary<WorkloadProviderId, JsonElement> MapEntries(
        WorkloadOrchestratorSettings document, Func<IWorkloadProviderSettingsCodec, JsonElement, JsonElement> map)
    {
        var mapped = new Dictionary<WorkloadProviderId, JsonElement>(document.Providers.Count);
        foreach (var (provider, entry) in document.Providers)
        {
            mapped[provider] = codecs.TryGetValue(provider, out var codec) ? map(codec, entry) : entry;
        }

        return mapped;
    }
}
