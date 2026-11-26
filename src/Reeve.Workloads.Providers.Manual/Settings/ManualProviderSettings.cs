using System.Text.Json;
using Reeve.Workloads.Abstractions.Contracts;
using Reeve.Workloads.Abstractions.Providers;

namespace Reeve.Workloads.Providers.Manual;

/// <summary>Manual provider settings. The provider reads none of its entry: whatever an operator sets there is
/// the host's, and the codec keeps it in the stored row without reading it.</summary>
public sealed record ManualProviderSettings
{
    /// <summary>This provider's entry, or all-defaults when the operator has configured none.</summary>
    public static ManualProviderSettings From(WorkloadOrchestratorSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return From(settings.Entry(ManualProvider.Id));
    }

    internal static ManualProviderSettings From(JsonElement entry)
        => entry.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null
            ? new ManualProviderSettings()
            : entry.Deserialize<ManualProviderSettings>() ?? new ManualProviderSettings();
}

internal sealed class ManualProviderSettingsCodec : IWorkloadProviderSettingsCodec
{
    private static readonly IReadOnlySet<string> OwnedKeys = ProviderSettingsPassThrough.OwnedKeys<ManualProviderSettings>();

    public WorkloadProviderId Provider => ManualProvider.Id;

    public WorkloadProviderSettingsValidation Validate(JsonElement entry)
        => WorkloadProviderSettingsValidation.Valid;

    public JsonElement ResolveMasked(JsonElement submitted, JsonElement? stored)
        => ProviderSettingsPassThrough.Merge(Owned(submitted), submitted, stored, OwnedKeys);

    public JsonElement Mask(JsonElement entry)
        => ProviderSettingsPassThrough.Carry(Owned(entry), entry, OwnedKeys);

    public IReadOnlyList<WorkloadSecretPath> SecretFields(JsonElement entry) => [];

    public IReadOnlyDictionary<string, string> Placement(JsonElement entry) => new Dictionary<string, string>();

    private static JsonElement Owned(JsonElement entry) => JsonSerializer.SerializeToElement(ManualProviderSettings.From(entry));
}
