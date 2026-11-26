using System.Text.Json;
using Reeve.Workloads.Abstractions.Contracts;

namespace Reeve.Workloads.Abstractions.Providers;

/// <summary>The redaction sentinel the library emits for a secret on read, and recognises on write as "keep the
/// stored secret". One literal for every provider: an operator who re-posts a settings form must not have to
/// know which provider chose which spelling.</summary>
public static class WorkloadSecretMask
{
    /// <summary>The sentinel. Same literal the flat-form config masking convention uses.</summary>
    public const string Sentinel = "***";

    /// <summary>True when <paramref name="value"/> is the sentinel — i.e. "preserve what is stored".</summary>
    public static bool IsSentinel(string? value) => string.Equals(value, Sentinel, StringComparison.Ordinal);
}

/// <summary>A codec's verdict on an operator's submission. Invalid carries a message that NAMES the field, so
/// the settings page can point at it; the library prefixes nothing and interprets nothing.</summary>
public readonly record struct WorkloadProviderSettingsValidation(bool IsValid, string? Message)
{
    /// <summary>The single valid result.</summary>
    public static WorkloadProviderSettingsValidation Valid { get; } = new(true, null);

    /// <summary>Rejects the submission with an operator-facing <paramref name="message"/>.</summary>
    public static WorkloadProviderSettingsValidation Invalid(string message) => new(false, message);
}

/// <summary>The rules for one provider's entry in <see cref="WorkloadOrchestratorSettings.Providers"/>: what is
/// valid, which fields are secret, how a masked secret resolves, and which fields place the entry. Ships WITH the
/// provider package; the library never looks inside an entry.</summary>
/// <remarks>
/// Every method is handed a <see cref="JsonElement"/> because that is the only shape the library can hold: the
/// entry's schema belongs to the provider, which parses it into whatever record it defined. A provider with no
/// secrets and no rules needs no codec at all. Entries are plaintext: the host protects them at rest.
/// </remarks>
public interface IWorkloadProviderSettingsCodec
{
    /// <summary>The provider whose entry this codec owns.</summary>
    WorkloadProviderId Provider { get; }

    /// <summary>Rejects a document's entry with a message naming the field. A <see cref="WorkloadSecretMask.Sentinel"/>
    /// that is still in the entry has no secret to stand for and is invalid.</summary>
    WorkloadProviderSettingsValidation Validate(JsonElement entry);

    /// <summary>What an operator may read back: every declared secret replaced by
    /// <see cref="WorkloadSecretMask.Sentinel"/>.</summary>
    JsonElement Mask(JsonElement entry);

    /// <summary>The submitted entry with each masked secret replaced by the one <paramref name="stored"/> holds.</summary>
    /// <exception cref="WorkloadOrchestratorSettingsValidationException">A secret is masked and nothing is stored
    /// to keep; the message names the field.</exception>
    JsonElement ResolveMasked(JsonElement submitted, JsonElement? stored);

    /// <summary>The non-empty secrets in the entry, each as a JSON pointer from the entry to the string.</summary>
    IReadOnlyList<WorkloadSecretPath> SecretFields(JsonElement entry);

    /// <summary>Where the workloads of this entry live, as named values: when one differs between two documents the
    /// entry moved to another place while the workloads it holds stay behind.</summary>
    /// <remarks>
    /// A location is the RESOLVED target, not the credential bytes that reach it. Kubernetes reports the API server
    /// of the effective context, the namespace and the connection mode; a rotated token, certificate or CA, or a
    /// renamed context that resolves to the same server, is not a relocation. Docker reports the socket URI. Never
    /// put a secret here. Known limit: a new address for the same cluster counts as a relocation, because the library
    /// cannot tell the two clusters are one. An entry that cannot be resolved reports an empty value and is a shape
    /// error for <see cref="Validate"/>, not a relocation.
    /// </remarks>
    IReadOnlyDictionary<string, string> Placement(JsonElement entry);
}

/// <summary>A secret string inside one provider's entry.</summary>
/// <param name="Path">A JSON pointer (RFC 6901) from the entry to the string.</param>
/// <param name="Value">The value as the entry holds it.</param>
public readonly record struct WorkloadSecretPath(string Path, string Value);
