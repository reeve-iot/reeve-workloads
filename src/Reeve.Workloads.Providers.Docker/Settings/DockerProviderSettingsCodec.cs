using System.Text.Json;
using Reeve.Workloads.Abstractions;
using Reeve.Workloads.Abstractions.Contracts;
using Reeve.Workloads.Abstractions.Providers;

namespace Reeve.Workloads.Providers.Docker;

internal sealed class DockerProviderSettingsCodec : IWorkloadProviderSettingsCodec
{
    private static readonly IReadOnlySet<string> OwnedKeys = ProviderSettingsPassThrough.OwnedKeys<DockerProviderSettings>();

    public WorkloadProviderId Provider => DockerProvider.Id;

    public WorkloadProviderSettingsValidation Validate(JsonElement entry)
    {
        var submitted = DockerProviderSettings.From(entry);

        var credentials = submitted.RegistryCredentials;
        for (var i = 0; i < credentials.Count; i++)
        {
            var credential = credentials[i];
            if (string.IsNullOrWhiteSpace(credential.HostPattern))
            {
                return WorkloadProviderSettingsValidation.Invalid($"docker.registryCredentials[{i}].hostPattern must not be empty.");
            }

            if (string.IsNullOrWhiteSpace(credential.Username))
            {
                return WorkloadProviderSettingsValidation.Invalid($"docker.registryCredentials[{i}].username must not be empty.");
            }

            if (string.IsNullOrWhiteSpace(credential.Token))
            {
                return WorkloadProviderSettingsValidation.Invalid($"docker.registryCredentials[{i}].token must not be empty.");
            }

            if (WorkloadSecretMask.IsSentinel(credential.Token))
            {
                return WorkloadProviderSettingsValidation.Invalid(
                    $"docker.registryCredentials for host '{credential.HostPattern}' sent the masked token but has no stored secret to preserve — supply the real token.");
            }
        }

        return WorkloadProviderSettingsValidation.Valid;
    }

    public JsonElement ResolveMasked(JsonElement submitted, JsonElement? stored)
    {
        var settings = DockerProviderSettings.From(submitted);
        var priorByHost = PriorTokensByHost(stored);

        var resolved = settings.RegistryCredentials
            .Select((c, i) => WorkloadSecretMask.IsSentinel(c.Token)
                ? c with
                {
                    Token = !string.IsNullOrWhiteSpace(c.HostPattern) && priorByHost.TryGetValue(c.HostPattern, out var prior) && !string.IsNullOrEmpty(prior)
                        ? prior
                        : throw new WorkloadOrchestratorSettingsValidationException(
                            string.IsNullOrWhiteSpace(c.HostPattern)
                            ? $"docker.registryCredentials[{i}].hostPattern must not be empty."
                            : $"docker.registryCredentials for host '{c.HostPattern}' sent the masked token but has no stored secret to preserve — supply the real token."),
                }
                : c)
            .ToArray();

        return ProviderSettingsPassThrough.Merge(
            JsonSerializer.SerializeToElement(settings with { RegistryCredentials = resolved }), submitted, stored, OwnedKeys);
    }

    public JsonElement Mask(JsonElement entry)
    {
        var settings = DockerProviderSettings.From(entry);
        var masked = JsonSerializer.SerializeToElement(settings with
        {
            RegistryCredentials =
            [
                .. settings.RegistryCredentials.Select(c => c with
                {
                    Token = string.IsNullOrEmpty(c.Token) ? string.Empty : WorkloadSecretMask.Sentinel,
                }),
            ],
        });
        return ProviderSettingsPassThrough.Carry(masked, entry, OwnedKeys);
    }

    public IReadOnlyList<WorkloadSecretPath> SecretFields(JsonElement entry)
        => [.. DockerProviderSettings.From(entry).RegistryCredentials
            .Select((c, i) => (c, i))
            .Where(x => !string.IsNullOrEmpty(x.c.Token))
            .Select(x => new WorkloadSecretPath($"/registryCredentials/{x.i}/token", x.c.Token))];

    public IReadOnlyDictionary<string, string> Placement(JsonElement entry)
        => new Dictionary<string, string>
        {
            ["socketUri"] = DockerProviderSettings.From(entry).SocketUri is { } socket && !string.IsNullOrWhiteSpace(socket)
                ? socket
                : DockerClientFactory.PlatformDefaultEndpoint().ToString(),
        };

    private static Dictionary<string, string> PriorTokensByHost(JsonElement? stored)
        => stored is { } s
            ? DockerProviderSettings.From(s).RegistryCredentials
                .GroupBy(c => c.HostPattern, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.First().Token, StringComparer.Ordinal)
            : new Dictionary<string, string>(StringComparer.Ordinal);
}
