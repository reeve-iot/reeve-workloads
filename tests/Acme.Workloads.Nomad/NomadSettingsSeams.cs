using System.Text.Json;
using Reeve.Workloads.Abstractions;
using Reeve.Workloads.Abstractions.Contracts;
using Reeve.Workloads.Abstractions.Providers;

namespace Acme.Workloads.Nomad;

public sealed class NomadProviderSettingsCodec : IWorkloadProviderSettingsCodec
{
    public WorkloadProviderId Provider => NomadProvider.Id;

    public WorkloadProviderSettingsValidation Validate(JsonElement entry)
    {
        var submitted = Parse(entry);

        if (!string.IsNullOrWhiteSpace(submitted.Address)
            && !Uri.TryCreate(submitted.Address, UriKind.Absolute, out _))
        {
            return WorkloadProviderSettingsValidation.Invalid("nomad.address must be an absolute URL.");
        }

        if (WorkloadSecretMask.IsSentinel(submitted.Token))
        {
            return WorkloadProviderSettingsValidation.Invalid(
                "nomad.token sent the masked value but has no stored secret to preserve — supply the real token.");
        }

        return WorkloadProviderSettingsValidation.Valid;
    }

    public JsonElement ResolveMasked(JsonElement submitted, JsonElement? stored)
    {
        var settings = Parse(submitted);
        if (!WorkloadSecretMask.IsSentinel(settings.Token))
        {
            return submitted;
        }

        var prior = Parse(stored).Token;
        return string.IsNullOrEmpty(prior)
            ? throw new WorkloadOrchestratorSettingsValidationException(
                "nomad.token sent the masked value but has no stored secret to preserve — supply the real token.")
            : JsonSerializer.SerializeToElement(settings with { Token = prior });
    }

    public JsonElement Mask(JsonElement entry)
    {
        var settings = Parse(entry);
        return JsonSerializer.SerializeToElement(settings with
        {
            Token = string.IsNullOrEmpty(settings.Token) ? settings.Token : WorkloadSecretMask.Sentinel,
        });
    }

    public IReadOnlyList<WorkloadSecretPath> SecretFields(JsonElement entry)
        => Parse(entry).Token is { Length: > 0 } token ? [new WorkloadSecretPath("/token", token)] : [];

    public IReadOnlyDictionary<string, string> Placement(JsonElement entry)
        => new Dictionary<string, string> { ["address"] = Parse(entry).Address ?? string.Empty };

    private static NomadProviderSettings Parse(JsonElement? entry)
        => entry is { ValueKind: not (JsonValueKind.Undefined or JsonValueKind.Null) } element
            ? element.Deserialize<NomadProviderSettings>() ?? new NomadProviderSettings()
            : new NomadProviderSettings();
}

public sealed class NomadAutoDetect : IWorkloadProviderAutoDetect
{
    public WorkloadProviderId Provider => NomadProvider.Id;

    public int Priority => IWorkloadProviderAutoDetect.LocalRuntimePriority;

    public Task<bool> IsAvailableAsync(JsonElement providerSettings, CancellationToken cancellationToken = default)
        => Task.FromResult(true);
}
