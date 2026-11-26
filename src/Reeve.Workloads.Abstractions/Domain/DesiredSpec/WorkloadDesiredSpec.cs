using System.Text.Json.Serialization;
using Reeve.Workloads.Abstractions.Contracts;
using Reeve.Workloads.Abstractions.Contracts.Deployment;

namespace Reeve.Workloads.Abstractions.Domain.DesiredSpec;

/// <summary>The fully-resolved, provider-agnostic desired spec for one workload instance. Immutable; the reconciler
/// drifts on <see cref="SpecHash"/>, the canonical hash of the materialization-relevant subset.</summary>
public sealed record WorkloadDesiredSpec
{
    [JsonPropertyName("pluginInstanceId")]
    public required Guid GroupInstanceId { get; init; }
    [JsonPropertyName("pluginTypeId")]
    public required string GroupId { get; init; }
    [JsonPropertyName("workloadId")]
    public required string WorkloadId { get; init; }

    /// <summary>The workload-instance GUID (identity of THIS materialization). <c>inst8</c> derives from it.</summary>
    [JsonPropertyName("instanceId")]
    public required Guid InstanceId { get; init; }

    /// <summary>First 8 hex chars of <see cref="InstanceId"/> — the naming discriminator.</summary>
    [JsonPropertyName("inst8")]
    public required string Inst8 { get; init; }

    /// <summary>The active provider this spec was computed for (affects some materialization, e.g. docker hostPort).</summary>
    [JsonPropertyName("provider")]
    public required WorkloadProviderId Provider { get; init; }

    /// <summary>Provider-appropriate primary object/container name.</summary>
    [JsonPropertyName("resourceName")]
    public required string ResourceName { get; init; }

    /// <summary>Provider-appropriate Secret name that carries per-instance secret env: the workload's credentials and secret material.</summary>
    [JsonPropertyName("secretName")]
    public required string SecretName { get; init; }

    /// <summary>The full label set applied to the materialized object (instance identity + installation + spec-hash + criticality).</summary>
    [JsonPropertyName("labels")]
    public required IReadOnlyDictionary<string, string> Labels { get; init; }

    /// <summary>The FINAL image reference AFTER mirror rewrite — digest preserved (<c>ref@digest</c>).</summary>
    [JsonPropertyName("image")]
    public required string Image { get; init; }

    /// <summary>Declared platforms (informational to the renderer's node-affinity), order-insensitive → hashed sorted.</summary>
    [JsonPropertyName("platforms")]
    public IReadOnlyList<string> Platforms { get; init; } = [];

    [JsonPropertyName("kind")]
    public required WorkloadKind Kind { get; init; }

    /// <summary>Resolved node-selector labels — the label→value map the workload must schedule onto. Empty ⇒ no
    /// selector. INCLUDED in <see cref="SpecHash"/> (sorted keys) so a node-selector edit forces a rollout.</summary>
    [JsonPropertyName("nodeSelector")]
    public IReadOnlyDictionary<string, string> NodeSelector { get; init; } = new Dictionary<string, string>();

    /// <summary>Resolved resource requests/limits (author defaults + operator <c>resources.*</c> overrides). Null when none.</summary>
    [JsonPropertyName("resources")]
    public ResolvedResources? Resources { get; init; }

    [JsonPropertyName("network")]
    public ResolvedNetwork? Network { get; init; }

    [JsonPropertyName("privileges")]
    public ResolvedPrivileges? Privileges { get; init; }

    /// <summary>The effective user-namespace value. Hashed only when true.</summary>
    [JsonPropertyName("userNamespace")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool UserNamespace { get; init; }

    /// <summary>A report that a requested user namespace was skipped for host networking. Excluded from the hash.</summary>
    [JsonPropertyName("userNamespaceSkippedForHostNetwork")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool UserNamespaceSkippedForHostNetwork { get; init; }

    /// <summary>A report that a requested user namespace was skipped because the workload binds a device on a provider
    /// that opens it only from a privileged container, and a privileged container in a user namespace holds no host
    /// privilege. Excluded from the hash.</summary>
    [JsonPropertyName("userNamespaceSkippedForDevicePassthrough")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool UserNamespaceSkippedForDevicePassthrough { get; init; }

    [JsonPropertyName("volumes")]
    public IReadOnlyList<ResolvedVolume> Volumes { get; init; } = [];

    /// <summary>Curated read-only ConfigMap mounts, each with its resolved ConfigMap name. A mount whose parameter the
    /// operator left blank is absent.</summary>
    [JsonPropertyName("configMapMounts")]
    public IReadOnlyList<ResolvedConfigMapMount> ConfigMapMounts { get; init; } = [];

    [JsonPropertyName("health")]
    public required WorkloadHealth Health { get; init; }
    [JsonPropertyName("observability")]
    public WorkloadObservability? Observability { get; init; }

    /// <summary>Each entry is a literal value, a secret reference, or the synthesized <c>REEVE_SPEC_HASH</c> — whose
    /// value is <see cref="SpecHash"/>, populated after the hash is computed.</summary>
    [JsonPropertyName("env")]
    public IReadOnlyList<ResolvedEnvVar> Env { get; init; } = [];

    /// <summary>Declarative host prerequisites, carried informationally: not machine-verified, OUT of the hash.</summary>
    [JsonPropertyName("hostRequirements")]
    public IReadOnlyList<HostRequirement> HostRequirements { get; init; } = [];

    /// <summary>The RFC 6902 <c>unsupportedPodPatch</c> the provider applies to the rendered pod object, or null.
    /// Hashed ONLY when present, so a patch-less workload hashes as before; the provider re-validates after applying.</summary>
    [JsonPropertyName("unsupportedPodPatch")]
    public UnsupportedPodPatchSpec? UnsupportedPodPatch { get; init; }

    /// <summary>The operator-RESTART nonce. EXCLUDED from <see cref="SpecHash"/>, so it never poisons drift; the k8s
    /// renderer stamps it as a second pod-template annotation, which is what makes a Restart actually roll.</summary>
    [JsonPropertyName("restartNonce")]
    public long RestartNonce { get; init; }

    /// <summary>Extra per-instance SECRET values the provider writes into the instance's Secret at Apply, keyed by
    /// the ENV VAR name referencing them. EXCLUDED from the hash and from every artifact.</summary>
    /// <remarks>[JsonIgnore] is what makes "never at rest" true rather than merely intended: this spec is
    /// serialized whole into the update intent's JSON target/previous columns, which persist indefinitely. The
    /// reconcile engine takes live material back from a fresh render before it applies one.</remarks>
    [JsonIgnore]
    public IReadOnlyDictionary<string, string> SecretMaterial { get; init; } =
        new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>Credentials the provider writes at apply whether or not an env declares them, each under its own
    /// name. EXCLUDED from <see cref="SpecHash"/>: they live outside <see cref="Env"/>.</summary>
    /// <remarks>Left out of this record's JSON shape, which the intent spec-map codec writes: an intent persists
    /// through the document mapper instead.</remarks>
    [JsonIgnore]
    public IReadOnlyList<ProvisionedCredential> ProvisionedCredentials { get; init; } = [];

    /// <summary>The pending scan-run id, or null when no run is pending — a SEPARATE gating axis from hash drift:
    /// Jobs are created IFF this is non-null. EXCLUDED from <see cref="SpecHash"/>. Null for non-scan workloads.</summary>
    [JsonPropertyName("pendingScanRunId")]
    public string? PendingScanRunId { get; init; }

    /// <summary>The Ready target-node set the scan fan-out materializes one Job per, supplied at scan-trigger time.
    /// Null or empty ⇒ zero Jobs. EXCLUDED from the hash: a live node set is a runtime input, not a config change.</summary>
    [JsonPropertyName("scanReadyNodeNames")]
    public IReadOnlyList<string>? ScanReadyNodeNames { get; init; }

    /// <summary>The <i>skipped, ineligible</i> companion: READY node names the selector EXCLUDED. A REPORT only — the
    /// fan-out renders exactly <see cref="ScanReadyNodeNames"/>, and a not-Ready node is in NEITHER list.</summary>
    [JsonPropertyName("scanIneligibleNodeNames")]
    public IReadOnlyList<string>? ScanIneligibleNodeNames { get; init; }

    /// <summary>The canonical SpecHash (lowercase hex, no <c>sha256:</c> prefix). Also injected as REEVE_SPEC_HASH.</summary>
    [JsonPropertyName("specHash")]
    public required string SpecHash { get; init; }
}

/// <summary>The RFC 6902 <c>unsupportedPodPatch</c> carried on the desired spec. <see cref="PatchJson"/> is the raw
/// array JSON preserved VERBATIM from the manifest body, which the provider deserializes and applies.</summary>
public sealed record UnsupportedPodPatchSpec
{
    /// <summary>The raw RFC 6902 JSON Patch array (verbatim manifest body text).</summary>
    [JsonPropertyName("patchJson")]
    public required string PatchJson { get; init; }
}

/// <summary>Resolved resource requests/limits, after any operator override.</summary>
public sealed record ResolvedResources
{
    [JsonPropertyName("requestsCpu")]
    public string? RequestsCpu { get; init; }
    [JsonPropertyName("requestsMemory")]
    public string? RequestsMemory { get; init; }
    [JsonPropertyName("limitsCpu")]
    public string? LimitsCpu { get; init; }
    [JsonPropertyName("limitsMemory")]
    public string? LimitsMemory { get; init; }
}

/// <summary>Resolved network exposure. Ports carry the resolved docker hostPort when applicable.</summary>
public sealed record ResolvedNetwork
{
    /// <summary>True IFF <see cref="Mode"/> is <see cref="WorkloadNetworkMode.HostNetwork"/>. The boolean stays the
    /// SINGLE source of truth for every consumer that reads only the boolean; the factory is what keeps the two coherent.</summary>
    [JsonPropertyName("hostNetwork")]
    public bool HostNetwork { get; init; }

    /// <summary>The operator's resolved network posture, read from the reserved <c>network.mode</c> parameter.
    /// Defaults to Cluster which, with empty <see cref="Attachments"/>, is byte-identical to the boolean-only shape.</summary>
    [JsonPropertyName("mode")]
    public WorkloadNetworkMode Mode { get; init; } = WorkloadNetworkMode.Cluster;

    /// <summary>The chosen attachment names, reserved-slot-first then author slots in DECLARED order, de-duplicated.
    /// ORDER IS SIGNIFICANT and never sorted. EMPTY whenever <see cref="Mode"/> is not OperatorNetwork.</summary>
    [JsonPropertyName("attachments")]
    public IReadOnlyList<string> Attachments { get; init; } = [];

    [JsonPropertyName("mdns")]
    public bool Mdns { get; init; }
    [JsonPropertyName("ipv6Required")]
    public bool Ipv6Required { get; init; }
    [JsonPropertyName("ports")]
    public IReadOnlyList<ResolvedPort> Ports { get; init; } = [];
}

/// <summary>A resolved service port. <see cref="HostPort"/> is set (docker) from the reserved <c>port.{name}.hostPort</c> param.</summary>
public sealed record ResolvedPort
{
    [JsonPropertyName("name")]
    public required string Name { get; init; }
    [JsonPropertyName("port")]
    public required int Port { get; init; }
    [JsonPropertyName("protocol")]
    public required string Protocol { get; init; }

    /// <summary>Docker host-published port from reserved <c>port.{name}.hostPort</c>; null when not published / k8s.</summary>
    [JsonPropertyName("hostPort")]
    public int? HostPort { get; init; }
}

/// <summary>Resolved privilege surface — paths RESOLVED from their parameters.</summary>
public sealed record ResolvedPrivileges
{
    /// <summary>Declared capabilities as wire strings (e.g. <c>NET_ADMIN</c>). Order-insensitive → hashed sorted.</summary>
    [JsonPropertyName("capabilities")]
    public IReadOnlyList<string> Capabilities { get; init; } = [];

    /// <summary>Whether the manifest declares privileged mode. Every provider renders it.</summary>
    [JsonPropertyName("privileged")]
    public bool Privileged { get; init; }

    /// <summary>True when the manifest does not declare privileged mode but the provider must run the workload privileged
    /// to open its bound devices (<see cref="Reeve.Workloads.Abstractions.Providers.WorkloadProviderDescriptor.DevicePassthroughRequiresPrivileged"/>).
    /// A Kubernetes or Helm rendering honours it; a Compose rendering does not, because its <c>devices:</c> entry already
    /// opens the device. Hashed only when set.</summary>
    [JsonPropertyName("devicePassthroughPrivileged")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool DevicePassthroughPrivileged { get; init; }

    /// <summary>Whether a pod rendering runs the container privileged: declared, or needed for device passthrough.</summary>
    [JsonIgnore]
    public bool EffectivePrivileged => Privileged || DevicePassthroughPrivileged;

    [JsonPropertyName("runAsRoot")]
    public bool RunAsRoot { get; init; }

    /// <summary>The group that owns every declared volume and that the workload joins (<c>privileges.fsGroup</c>),
    /// or null when none is declared. A rendering input, hashed only when set so an existing spec's hash holds.</summary>
    [JsonPropertyName("fsGroup")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? FsGroup { get; init; }

    [JsonPropertyName("tunDevice")]
    public bool TunDevice { get; init; }

    /// <summary>Opt-in backend-REST privilege. Security-bearing but NOT a rendering input, so it is deliberately
    /// EXCLUDED from <see cref="WorkloadDesiredSpec.SpecHash"/> and read only as a privilege requirement.</summary>
    [JsonPropertyName("callsBackendRest")]
    public bool CallsBackendRest { get; init; }

    /// <summary>Resolved D-Bus socket-dir mount path, or null when <c>hostDbus</c> is off.</summary>
    [JsonPropertyName("dbusHostPath")]
    public string? DbusHostPath { get; init; }

    /// <summary>Resolved host device paths (from <c>devices[].parameter</c> ⇒ /dev/**). Order-insensitive → hashed sorted.</summary>
    [JsonPropertyName("devicePaths")]
    public IReadOnlyList<string> DevicePaths { get; init; } = [];

    /// <summary>Owning group ids granted by <c>group: auto</c> slots, de-duplicated. Order-insensitive → hashed sorted.</summary>
    [JsonPropertyName("deviceGroupIds")]
    public IReadOnlyList<int> DeviceGroupIds { get; init; } = [];

    /// <summary>True when a <c>group: auto</c> slot bound a device — the security-bearing FACT, independent of
    /// whether a GID was found. Deliberately NOT hashed: the GIDs are the rendering input, this is not.</summary>
    [JsonPropertyName("joinsDeviceGroups")]
    public bool JoinsDeviceGroups { get; init; }

    /// <summary>Resolved READ-ONLY host directory mounts: each pairs a resolved host path with the container
    /// <c>mountPath</c>. Always rendered read-only. Order-insensitive, so hashed sorted.</summary>
    [JsonPropertyName("hostPaths")]
    public IReadOnlyList<ResolvedHostPath> HostPaths { get; init; } = [];

    /// <summary>Resolved node-local hostPath dir <c>{nodeStateRoot}/{pluginTypeId}/{workloadId}[/subdir]</c>, or null when none.</summary>
    [JsonPropertyName("nodeLocalState")]
    public ResolvedNodeLocalState? NodeLocalState { get; init; }
}

/// <summary>A resolved read-only host-inspect mount: <see cref="HostPath"/> is the resolved host directory, mounted
/// read-only at the container <see cref="MountPath"/>. Read-only is implicit.</summary>
public sealed record ResolvedHostPath
{
    /// <summary>The resolved absolute host directory (constrained to /sys|/proc|/run|/var/run/**).</summary>
    [JsonPropertyName("hostPath")]
    public required string HostPath { get; init; }

    /// <summary>The absolute container path the host directory is mounted at, read-only.</summary>
    [JsonPropertyName("mountPath")]
    public required string MountPath { get; init; }
}

/// <summary>Resolved node-local hostPath persistence.</summary>
public sealed record ResolvedNodeLocalState
{
    /// <summary>The resolved absolute host path <c>{nodeStateRoot}/{pluginTypeId}/{workloadId}[/subdir]</c>.</summary>
    [JsonPropertyName("hostPath")]
    public required string HostPath { get; init; }
    [JsonPropertyName("criticality")]
    public required VolumeCriticality Criticality { get; init; }
}

/// <summary>A resolved volume. <see cref="StorageClassName"/> is carried for CREATE but is EXCLUDED from
/// <see cref="WorkloadDesiredSpec.SpecHash"/>, so a default-class change never rolls the pod.</summary>
public sealed record ResolvedVolume
{
    [JsonPropertyName("id")]
    public required string Id { get; init; }
    [JsonPropertyName("mountPath")]
    public required string MountPath { get; init; }
    [JsonPropertyName("size")]
    public required string Size { get; init; }
    [JsonPropertyName("criticality")]
    public required VolumeCriticality Criticality { get; init; }
    [JsonPropertyName("accessMode")]
    public string AccessMode { get; init; } = "rwo";

    /// <summary>The PVC / named-volume name the pod mounts and the provider materializes. On a restore this is the
    /// RETAINED physical name. EXCLUDED from the hash — a rebind of the name must NOT roll the pod.</summary>
    [JsonPropertyName("resourceName")]
    public required string ResourceName { get; init; }

    /// <summary>True when this volume ADOPTS an existing retained claim rather than creating a fresh one;
    /// <see cref="ResourceName"/> is then the retained name. INFORMATIONAL, and excluded from the hash.</summary>
    [JsonPropertyName("existingClaim")]
    public bool ExistingClaim { get; init; }

    /// <summary>StorageClass to create the PVC with; null ⇒ provider default. EXCLUDED from the SpecHash.</summary>
    [JsonPropertyName("storageClassName")]
    public string? StorageClassName { get; init; }
}

/// <summary>A resolved ConfigMap mount — the operator-created ConfigMap name is resolved from its parameter.</summary>
public sealed record ResolvedConfigMapMount
{
    [JsonPropertyName("mountPath")]
    public required string MountPath { get; init; }

    /// <summary>The resolved operator-created ConfigMap name (from the referenced text parameter).</summary>
    [JsonPropertyName("configMapName")]
    public required string ConfigMapName { get; init; }
}

/// <summary>A resolved environment variable. Exactly one of <see cref="Value"/> / <see cref="SecretRef"/> is
/// meaningful, discriminated by <see cref="Kind"/>.</summary>
public sealed record ResolvedEnvVar
{
    [JsonPropertyName("name")]
    public required string Name { get; init; }

    [JsonPropertyName("kind")]
    public required ResolvedEnvKind Kind { get; init; }

    /// <summary>The concrete value for <see cref="ResolvedEnvKind.Literal"/> / <see cref="ResolvedEnvKind.SpecHash"/>.</summary>
    [JsonPropertyName("value")]
    public string? Value { get; init; }

    /// <summary>The stable secret reference NAME for a SecretRef env — never the secret value.</summary>
    [JsonPropertyName("secretRef")]
    public string? SecretRef { get; init; }

    /// <summary>The host's id for the credential backing a SecretRef env, or null for any other env. EXCLUDED from
    /// the SpecHash: the ref name is hashed, and a renamed id is not a configuration change.</summary>
    [JsonPropertyName("credentialId")]
    public string? CredentialId { get; init; }

    /// <summary>True for an env the SpecHash does not see at all, name and value. The value still reaches the
    /// workload on every apply, so changing it alone is not drift; only a flagged literal is expected.</summary>
    [JsonPropertyName("excludedFromSpecHash")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool ExcludedFromSpecHash { get; init; }
}

/// <summary>How a resolved env var carries its value. Serialized as its STRING name, not the ordinal: without that,
/// a future member reorder would reinterpret every persisted in-flight intent's env kinds.</summary>
[System.Text.Json.Serialization.JsonConverter(typeof(System.Text.Json.Serialization.JsonStringEnumConverter<ResolvedEnvKind>))]
public enum ResolvedEnvKind
{
    /// <summary>A concrete non-secret value (parameter/literal/non-secret bootstrap). Hashed by value.</summary>
    Literal = 0,

    /// <summary>A reference into the per-instance Secret (a credential or secret material). Hashed by ref NAME only.</summary>
    SecretRef = 1,

    /// <summary>The self-referential REEVE_SPEC_HASH. Value = the computed hash; EXCLUDED from the hash itself.</summary>
    SpecHash = 2,
}
