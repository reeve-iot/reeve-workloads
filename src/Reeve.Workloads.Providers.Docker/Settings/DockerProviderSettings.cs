using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using System.Text.Json.Serialization;
using Reeve.Workloads.Abstractions.Contracts;

namespace Reeve.Workloads.Providers.Docker;

/// <summary>Docker provider settings. A key of the entry this record does not own is the host's: the codec
/// keeps it in the stored row without reading it.</summary>
public sealed record DockerProviderSettings
{
    /// <summary>Docker daemon socket/endpoint URI; null ⇒ platform default (e.g. <c>unix:///var/run/docker.sock</c>).</summary>
    [JsonPropertyName("socketUri")]
    public string? SocketUri { get; init; }

    /// <summary>Docker network to attach workloads to; null ⇒ default.</summary>
    [JsonPropertyName("networkName")]
    public string? NetworkName { get; init; }

    /// <summary>Per-registry credentials feeding <c>X-Registry-Auth</c> on docker pulls. Each entry's
    /// <see cref="RegistryCredential.Token"/> is a secret (masked on read, protected at rest by the host).</summary>
    [JsonPropertyName("registryCredentials")]
    public IReadOnlyList<RegistryCredential> RegistryCredentials { get; init; } = [];

    /// <summary>This provider's entry, or all-defaults when the operator has configured none.</summary>
    public static DockerProviderSettings From(WorkloadOrchestratorSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return From(settings.Entry(DockerProvider.Id));
    }

    internal static DockerProviderSettings From(JsonElement entry)
        => entry.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null
            ? new DockerProviderSettings()
            : entry.Deserialize<DockerProviderSettings>() ?? new DockerProviderSettings();
}
