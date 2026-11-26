using System.Text.Json;
using System.Text.Json.Serialization;
using Reeve.Workloads.Abstractions.Contracts;

namespace Acme.Workloads.Nomad;

public sealed record NomadProviderSettings
{
    [JsonPropertyName("address")]
    public string? Address { get; init; }

    [JsonPropertyName("namespace")]
    public string? Namespace { get; init; }

    [JsonPropertyName("token")]
    public string? Token { get; init; }

    public static NomadProviderSettings From(WorkloadOrchestratorSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        if (!settings.Providers.TryGetValue(NomadProvider.Id, out var element)
            || element.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
        {
            return new NomadProviderSettings();
        }

        return element.Deserialize<NomadProviderSettings>() ?? new NomadProviderSettings();
    }
}
