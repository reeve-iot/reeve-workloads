using System.ComponentModel.DataAnnotations;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace Reeve.Workloads.Abstractions.Contracts;

/// <summary>The orchestrator settings the ENGINE owns — served by the host in plaintext (the host protects the
/// secrets at rest) and returned to the API with them masked. Everything is optional, so a fresh install is all-defaults.</summary>
/// <remarks>
/// Each provider's own section lives in the package that ships it, read through its codec, and this record names no provider at all.
/// </remarks>
public sealed record WorkloadOrchestratorSettings
{
    /// <summary>The explicitly-selected active provider, or null to auto-detect through the registered
    /// <see cref="Providers.IWorkloadProviderAutoDetect"/> chain.</summary>
    [JsonPropertyName("provider")]
    public WorkloadProviderId? Provider { get; init; }

    /// <summary>Every provider's own configuration, keyed by provider id. The engine never looks inside one.</summary>
    /// <remarks>
    /// The shape under a key belongs to the provider registered for that id, which parses it into whatever
    /// record it defined and declares — through its <see cref="Providers.IWorkloadProviderSettingsCodec"/> —
    /// which of its fields are secret and what makes a submission invalid. An id with no codec is
    /// neither validated, masked nor named as secret.
    /// </remarks>
    [JsonPropertyName("providers")]
    public IReadOnlyDictionary<WorkloadProviderId, JsonElement> Providers { get; init; }
        = new Dictionary<WorkloadProviderId, JsonElement>();

    /// <summary>The provider ids this installation has configured, or null for every provider compiled into the build.</summary>
    /// <remarks>
    /// Host state, not part of the wire document: the host fills it when its source serves the document. Auto-detect
    /// probes only these ids and the explicit target must be one of them. A selection the document declares is always
    /// explicit, so auto-detect over a declared list happens only when the list is managed and the selection comes
    /// from the database. An empty set leaves auto-detect nothing to choose from, so the effective provider is empty.
    /// </remarks>
    [JsonIgnore]
    public IReadOnlyCollection<WorkloadProviderId>? ConfiguredProviders { get; init; }

    /// <summary>Permit workloads whose image is not digest-pinned. Default false.</summary>
    /// <remarks>A supply-chain POLICY over what this installation will run, not a trait of one runtime; it applies to every provider.</remarks>
    [JsonPropertyName("allowUnpinnedImages")]
    public bool AllowUnpinnedImages { get; init; }

    /// <summary>Registry mirror rewrite rules (digest-preserving). The rewrite LOGIC is a later task; here we only carry it.</summary>
    [JsonPropertyName("mirrors")]
    public IReadOnlyList<RegistryMirror> Mirrors { get; init; } = [];

    /// <summary>Global reconciliation kill-switch: status reads continue, all mutations queue. The
    /// per-workload observe-only flag lives on the WorkloadInstance entity, not here.</summary>
    [JsonPropertyName("reconciliationPaused")]
    public bool ReconciliationPaused { get; init; }

    /// <summary>This provider's configuration entry, or an <see cref="JsonValueKind.Undefined"/> element when
    /// the operator has configured none — which a provider reads as "all defaults", never as an error.</summary>
    /// <remarks>A helper rather than a convention each provider re-implements: the shipped three and a
    /// third-party package resolve their entry through the same call, so there is one place where "absent" and
    /// "explicit null" are decided to mean the same thing.</remarks>
    public JsonElement Entry(WorkloadProviderId provider)
        => Providers.TryGetValue(provider, out var element) && element.ValueKind is not JsonValueKind.Null
            ? element
            : default;

    /// <summary>These settings with <paramref name="providerSettings"/> serialized under
    /// <paramref name="provider"/>, replacing whatever was there.</summary>
    /// <remarks>The write-side partner of <see cref="Entry"/>. Without it every caller composing settings in
    /// code — a provider package, a host bootstrapping a fresh install, a test — repeats the same
    /// dictionary-copy-and-serialize, and gets to decide independently whether an existing entry is replaced.</remarks>
    [RequiresUnreferencedCode(WithProviderReflectionMessage)]
    [RequiresDynamicCode(WithProviderReflectionMessage)]
    public WorkloadOrchestratorSettings WithProvider<TSettings>(WorkloadProviderId provider, TSettings providerSettings)
        => WithProviderEntry(provider, JsonSerializer.SerializeToElement(providerSettings));

    /// <summary>These settings with <paramref name="providerSettings"/> serialized through
    /// <paramref name="settingsTypeInfo"/> under <paramref name="provider"/>, replacing whatever was there.</summary>
    /// <remarks>The trim- and NativeAOT-safe form of <see cref="WithProvider{TSettings}(WorkloadProviderId, TSettings)"/>:
    /// pass the <see cref="JsonTypeInfo{T}"/> of a source-generated <see cref="JsonSerializerContext"/>.</remarks>
    public WorkloadOrchestratorSettings WithProvider<TSettings>(
        WorkloadProviderId provider, TSettings providerSettings, JsonTypeInfo<TSettings> settingsTypeInfo)
    {
        ArgumentNullException.ThrowIfNull(settingsTypeInfo);
        return WithProviderEntry(provider, JsonSerializer.SerializeToElement(providerSettings, settingsTypeInfo));
    }

    private const string WithProviderReflectionMessage =
        "Serializes TSettings by reflection. Trimmed and NativeAOT code passes a source-generated JsonTypeInfo<TSettings> instead.";

    private WorkloadOrchestratorSettings WithProviderEntry(WorkloadProviderId provider, JsonElement entry)
        => this with
        {
            Providers = new Dictionary<WorkloadProviderId, JsonElement>(Providers)
            {
                [provider] = entry,
            },
        };
}

/// <summary>A registry mirror rewrite rule. Digest-preserving; the rewrite logic itself is a later task.</summary>
public sealed record RegistryMirror
{
    /// <summary>The registry host to match, e.g. <c>docker.io</c>.</summary>
    [JsonPropertyName("matchRegistry")]
    public required string MatchRegistry { get; init; }

    /// <summary>The mirror registry host to rewrite to, e.g. <c>mirror.example.com</c>.</summary>
    [JsonPropertyName("mirrorRegistry")]
    public required string MirrorRegistry { get; init; }
}

/// <summary>The registry auth scheme a <see cref="RegistryCredential"/> uses. <see cref="Basic"/> sits at
/// ordinal 0 so an absent <c>authType</c> on a persisted credential deserializes to <c>basic</c> unchanged.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<RegistryAuthType>))]
public enum RegistryAuthType
{
    /// <summary><c>basic</c>: HTTP Basic auth (username:token), the default (htpasswd dev registry).</summary>
    [JsonStringEnumMemberName("basic")]
    Basic = 0,

    /// <summary><c>bearer</c>: OAuth2 bearer-token registry auth (Docker registry token flow).</summary>
    [JsonStringEnumMemberName("bearer")]
    Bearer = 1,
}

/// <summary>A per-registry credential. <see cref="Token"/> is a secret: masked on read, protected at rest by the host;
/// a submission with the mask sentinel resolves to the stored secret through <c>ResolveMasked</c>.</summary>
public sealed record RegistryCredential
{
    /// <summary>Host (glob) pattern this credential applies to, e.g. <c>registry.example.com:5000</c>.</summary>
    [JsonPropertyName("hostPattern")]
    public required string HostPattern { get; init; }

    /// <summary>Registry username.</summary>
    [JsonPropertyName("username")]
    public required string Username { get; init; }

    /// <summary>Registry token/password. SECRET — masked on read, protected at rest by the host.</summary>
    [JsonPropertyName("token")]
    public required string Token { get; init; }

    /// <summary>The auth scheme for this credential. Defaults to <see cref="RegistryAuthType.Basic"/> so a
    /// persisted credential with no <c>authType</c> loads unchanged. Not a secret.</summary>
    [JsonPropertyName("authType")]
    public RegistryAuthType AuthType { get; init; } = RegistryAuthType.Basic;
}

/// <summary>One provider's build-availability descriptor. <see cref="CompiledIn"/> false means the provider
/// is known (so the UI can render a card) but not usable; <see cref="UnavailableReason"/> explains why.</summary>
public sealed record WorkloadProviderInfo
{
    [JsonPropertyName("provider")]
    public required WorkloadProviderId Provider { get; init; }

    /// <summary>Operator-facing name for the provider-selection UI; the id when the provider declares none.</summary>
    [JsonPropertyName("displayName")]
    public string? DisplayName { get; init; }

    /// <summary>True when this provider is registered in the running backend and selectable.</summary>
    [JsonPropertyName("compiledIn")]
    public required bool CompiledIn { get; init; }

    /// <summary>Operator-facing reason the provider is unavailable, when <see cref="CompiledIn"/> is false; null otherwise.</summary>
    [JsonPropertyName("unavailableReason")]
    public string? UnavailableReason { get; init; }
}

/// <summary>GET response: the current settings (secrets masked), the effective/auto-detected provider,
/// and the compiled-provider catalog.</summary>
public sealed record WorkloadOrchestratorSettingsResponse
{
    /// <summary>Current settings with all secrets masked (never plaintext).</summary>
    [JsonPropertyName("settings")]
    public required WorkloadOrchestratorSettings Settings { get; init; }

    /// <summary>The effective provider: the explicit <see cref="WorkloadOrchestratorSettings.Provider"/> when
    /// set, else the auto-detect chain's answer. Empty only when this build registers no provider at all.</summary>
    [JsonPropertyName("effectiveProvider")]
    public required WorkloadProviderId EffectiveProvider { get; init; }

    /// <summary>True when <see cref="EffectiveProvider"/> came from auto-detect (no explicit provider set).</summary>
    [JsonPropertyName("autoDetected")]
    public required bool AutoDetected { get; init; }

    /// <summary>The providers registered in this backend build, in registration order.</summary>
    [JsonPropertyName("providers")]
    public required IReadOnlyList<WorkloadProviderInfo> Providers { get; init; }
}

/// <summary>PUT request: the full settings to persist. A secret field a provider's codec declares, sent as
/// the mask sentinel, is preserved from the stored secret rather than overwritten with the literal mask.</summary>
public sealed record UpdateWorkloadOrchestratorSettingsRequest
{
    [JsonPropertyName("settings")]
    public required WorkloadOrchestratorSettings Settings { get; init; }
}

/// <summary>Result of <c>POST …/test</c>: a provider capability probe. Every field is optional and
/// provider-specific. No probe registered ⇒ <see cref="ProbeAvailable"/> is false (a 200, not an error).</summary>
public sealed record WorkloadProviderProbeResult
{
    /// <summary>The provider that was probed (the requested override, or the effective provider).</summary>
    [JsonPropertyName("provider")]
    public required WorkloadProviderId Provider { get; init; }

    /// <summary>False when no probe implementation is compiled into this build for <see cref="Provider"/>.</summary>
    [JsonPropertyName("probeAvailable")]
    public required bool ProbeAvailable { get; init; }

    /// <summary>Human-readable summary (e.g. "probe not available in this build", or a probe's status line).</summary>
    [JsonPropertyName("message")]
    public string? Message { get; init; }

    /// <summary>k8s: whether the ServiceAccount can list nodes.</summary>
    [JsonPropertyName("canListNodes")]
    public bool? CanListNodes { get; init; }

    /// <summary>k8s: discovered StorageClass names.</summary>
    [JsonPropertyName("storageClasses")]
    public IReadOnlyList<string>? StorageClasses { get; init; }

    /// <summary>k8s: Pod Security Admission enforcement labels on the target namespace.</summary>
    [JsonPropertyName("psaEnforcementLabels")]
    public IReadOnlyDictionary<string, string>? PsaEnforcementLabels { get; init; }

    /// <summary>k8s: whether the API server is reachable.</summary>
    [JsonPropertyName("apiServerReachable")]
    public bool? ApiServerReachable { get; init; }

    /// <summary>True only when the connection itself failed (bad/untrusted kubeconfig, or a 401) — distinct from
    /// <see cref="ApiServerReachable"/> (a reachable-but-401 server) and a 403 (reachable, just under-privileged).</summary>
    [JsonPropertyName("connectionFailed")]
    public bool? ConnectionFailed { get; init; }

    /// <summary>docker: the daemon's reported API version.</summary>
    [JsonPropertyName("dockerApiVersion")]
    public string? DockerApiVersion { get; init; }

    /// <summary>Registry pull-auth probe outcome (per configured registryCredentials).</summary>
    [JsonPropertyName("registryPullAuthResult")]
    public string? RegistryPullAuthResult { get; init; }

    /// <summary>The probe's failing checks, one line each as <c>id: message</c>, blocking ones included. Null when
    /// the probe reports none this way; empty when it has nothing to report.</summary>
    [JsonPropertyName("warnings")]
    public IReadOnlyList<string>? Warnings { get; init; }
}
