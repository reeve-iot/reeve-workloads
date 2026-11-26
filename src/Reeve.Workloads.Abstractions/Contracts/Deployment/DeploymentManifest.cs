using System.Text.Json;
using System.Text.Json.Serialization;

namespace Reeve.Workloads.Abstractions.Contracts.Deployment;

/// <summary>The parsed <c>deployment</c> object. The schema version is a hard gate: version 1 is the only one
/// this build understands.</summary>
public sealed record DeploymentManifest
{
    [JsonPropertyName("schemaVersion")]
    public required int SchemaVersion { get; init; }

    [JsonPropertyName("workloads")]
    public required IReadOnlyList<WorkloadManifest> Workloads { get; init; }
}

/// <summary>One managed workload — a container the plugin declares.</summary>
public sealed record WorkloadManifest
{
    /// <summary>Stable slug within the plugin (<c>^[a-z0-9-]{1,40}$</c>).</summary>
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    /// <summary>UI label — author text, rendered quarantined.</summary>
    [JsonPropertyName("displayName")]
    public required string DisplayName { get; init; }

    /// <summary>Author prose explaining what this workload is and why it is installed, surfaced to the operator as
    /// attributed text. Optional, and absent from every manifest predating it.</summary>
    [JsonPropertyName("description")]
    public string? Description { get; init; }

    [JsonPropertyName("image")]
    public required WorkloadImage Image { get; init; }

    [JsonPropertyName("kind")]
    public required WorkloadKind Kind { get; init; }

    /// <summary>Author-declared resource requests/limits. Optional.</summary>
    [JsonPropertyName("resources")]
    public WorkloadResources? Resources { get; init; }

    [JsonPropertyName("network")]
    public WorkloadNetwork? Network { get; init; }

    [JsonPropertyName("privileges")]
    public WorkloadPrivileges? Privileges { get; init; }

    /// <summary>Persistent storage backed by a claim. Forbidden on the per-node workload kinds.</summary>
    [JsonPropertyName("volumes")]
    public IReadOnlyList<WorkloadVolume> Volumes { get; init; } = [];

    /// <summary>Curated, kubernetes-only, read-only ConfigMap mounts, each naming an operator-created ConfigMap
    /// via a declared parameter. Read-only is forced by contract — there is no flag. Optional.</summary>
    [JsonPropertyName("configMapMount")]
    public IReadOnlyList<ConfigMapMount> ConfigMapMounts { get; init; } = [];

    [JsonPropertyName("health")]
    public required WorkloadHealth Health { get; init; }

    /// <summary>Host-owned manifest data, carried verbatim under <c>extensions.&lt;namespace&gt;</c>. The library
    /// reads none of it; a host's registered extension validator gives it meaning.</summary>
    [JsonPropertyName("extensions")]
    public WorkloadExtensions Extensions { get; init; } = WorkloadExtensions.None;

    /// <summary>Declarative host prerequisites (not machine-verified in Phase 1).</summary>
    [JsonPropertyName("hostRequirements")]
    public IReadOnlyList<HostRequirement> HostRequirements { get; init; } = [];

    [JsonPropertyName("observability")]
    public WorkloadObservability? Observability { get; init; }

    /// <summary>Environment variables sourced from <c>parameter:</c>/<c>bootstrap:</c>/<c>literal:</c>.</summary>
    [JsonPropertyName("env")]
    public IReadOnlyList<WorkloadEnv> Env { get; init; } = [];

    /// <summary>Install-time parameters.</summary>
    [JsonPropertyName("parameters")]
    public IReadOnlyList<WorkloadParameter> Parameters { get; init; } = [];

    /// <summary>The patch escape hatch, preserved verbatim: a later step hashes it and the host renders it
    /// as written. Null when the workload declares none.</summary>
    [JsonPropertyName("unsupportedPodPatch")]
    public UnsupportedPodPatch? UnsupportedPodPatch { get; init; }
}

/// <summary>Workload scheduling kind. Serialized as its spec string name.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<WorkloadKind>))]
public enum WorkloadKind
{
    /// <summary><c>service</c>: normal Deployment (replicas, ClusterIP Service).</summary>
    [JsonStringEnumMemberName("service")]
    Service = 0,

    /// <summary>Pinned to exactly one node via a selector and device path; persistent volumes are allowed.</summary>
    [JsonStringEnumMemberName("single-node")]
    SingleNode = 1,

    /// <summary><c>per-node</c>: DaemonSet across matching nodes; forbids <c>volumes[]</c> — as does
    /// <see cref="ScanPerNode"/> — (uses nodeLocalState).</summary>
    [JsonStringEnumMemberName("per-node")]
    PerNode = 2,

    /// <summary><c>scan-per-node</c>: run-once-per-node scan Job across Ready nodes; forbids
    /// <c>volumes[]</c> like <c>per-node</c> (a scan Job cannot share one RWO PVC across nodes).</summary>
    [JsonStringEnumMemberName("scan-per-node")]
    ScanPerNode = 3,
}

/// <summary>Image reference — pinned by index digest, or a render-time product-image reference.</summary>
public sealed record WorkloadImage
{
    /// <summary>Registry and repository, no tag; the registry host is shown to the operator. Empty for a
    /// product-image reference, and mutually exclusive with it. External plugins MUST pin a digest.</summary>
    [JsonPropertyName("ref")]
    public string Ref { get; init; } = string.Empty;

    /// <summary><c>sha256:</c> digest of the multi-arch index (or single manifest). CI-stamped. Empty for a
    /// <see cref="Component"/> reference.</summary>
    [JsonPropertyName("digest")]
    public string Digest { get; init; } = string.Empty;

    /// <summary>Informational tag.</summary>
    [JsonPropertyName("tag")]
    public string? Tag { get; init; }

    /// <summary>Product-image reference for a bundled workload: a component slug the backend resolves at render
    /// time from product-image configuration. MUTUALLY EXCLUSIVE with a direct reference; resolution fail-closes.</summary>
    [JsonPropertyName("component")]
    public string? Component { get; init; }

    /// <summary>Supported platforms, which drive node architecture affinity.</summary>
    [JsonPropertyName("platforms")]
    public IReadOnlyList<string> Platforms { get; init; } = [];
}

/// <summary>Network exposure.</summary>
public sealed record WorkloadNetwork
{
    [JsonPropertyName("hostNetwork")]
    public bool HostNetwork { get; init; }

    [JsonPropertyName("mdns")]
    public bool Mdns { get; init; }

    [JsonPropertyName("ipv6Required")]
    public bool Ipv6Required { get; init; }

    [JsonPropertyName("ports")]
    public IReadOnlyList<WorkloadPort> Ports { get; init; } = [];

    /// <summary>Network attachment slots, each binding a declared attachment parameter. Optional and additive: a
    /// workload declaring none parses to an EMPTY, non-null list. Never nullable, so no consumer needs a check.</summary>
    [JsonPropertyName("attachments")]
    public IReadOnlyList<WorkloadNetworkAttachmentSlot> Attachments { get; init; } = [];
}

/// <summary>A network attachment request bound to an attachment parameter. The binding is what makes the slot
/// meaningful, so the parser requires it to resolve to a declared parameter of the attachment type.</summary>
public sealed record WorkloadNetworkAttachmentSlot
{
    /// <summary>Name of a declared <c>networkAttachment</c>-typed parameter carrying the operator's attachment choice.</summary>
    [JsonPropertyName("parameter")]
    public required string Parameter { get; init; }

    /// <summary>Author prose, rendered quarantined, explaining why the attachment is needed.</summary>
    [JsonPropertyName("description")]
    public string? Description { get; init; }
}

/// <summary>A named service port.</summary>
public sealed record WorkloadPort
{
    [JsonPropertyName("name")]
    public required string Name { get; init; }

    [JsonPropertyName("port")]
    public required int Port { get; init; }

    [JsonPropertyName("protocol")]
    public required string Protocol { get; init; }
}

/// <summary>Privilege escalation surface — each field is a privilege requirement. <see cref="FsGroup"/> is the one
/// whose requirement is the group id itself rather than a flag.</summary>
public sealed record WorkloadPrivileges
{
    /// <summary>Closed capability enum; <c>drop: ALL</c> + these adds.</summary>
    [JsonPropertyName("capabilities")]
    public IReadOnlyList<WorkloadCapability> Capabilities { get; init; } = [];

    [JsonPropertyName("privileged")]
    public bool Privileged { get; init; }

    [JsonPropertyName("runAsRoot")]
    public bool RunAsRoot { get; init; }

    /// <summary>Group id that owns every declared <c>volumes[]</c> entry and that the workload joins as a supplementary
    /// group, for an image whose <c>USER</c> is not root. Null (the default) renders nothing, so a volume is as
    /// writable as it was before this field existed. Range 1–65534, and only valid with at least one volume.
    /// <para>Kubernetes renders pod <c>securityContext.fsGroup</c> with <c>fsGroupChangePolicy: OnRootMismatch</c>.
    /// Docker adds the group to the container and, before every create, hands each named volume to it (setgid,
    /// group read/write) when the volume root does not already belong to it — a new volume and one that predates the
    /// field alike. The image's own uid/gid is untouched, so the process must run with this gid as its primary or
    /// supplementary group, which the render itself guarantees.</para>
    /// <para>Docker requirements on the image: the hand-over runs the workload's own image once as root (with only
    /// CHOWN, FOWNER and DAC_OVERRIDE, no network, a read-only root filesystem and only this workload's volumes), so
    /// the image must provide <c>sh</c>, <c>find</c>, <c>chgrp</c> and <c>chmod</c>. An image without them (distroless,
    /// scratch) fails the install rather than starting a workload that cannot write. Files a still-running previous
    /// version writes as root during an update are not covered until the volume next mismatches.</para>
    /// <para>A privilege requirement: joining a group can reach group-owned objects outside the volumes (a socket in
    /// a consented host path), so the number is carried into the consent profile and an update that adds or changes it
    /// is an escalation.</para></summary>
    [JsonPropertyName("fsGroup")]
    public int? FsGroup { get; init; }

    /// <summary>Mounts the D-Bus socket directory read-only, at the path a parameter supplies.</summary>
    [JsonPropertyName("hostDbus")]
    public bool HostDbus { get; init; }

    /// <summary><c>/dev/net/tun</c>.</summary>
    [JsonPropertyName("tunDevice")]
    public bool TunDevice { get; init; }

    /// <summary>Opt-in: the workload asks to call its host's own API with a credential the host issued.
    /// Security-bearing, so it is a privilege requirement; the library enforces nothing on it. Default false.
    /// The key keeps its name for manifest compatibility.</summary>
    [JsonPropertyName("callsBackendRest")]
    public bool CallsBackendRest { get; init; }

    /// <summary>Host character devices from device-path parameters, whose values are constrained to <c>/dev</c>.</summary>
    [JsonPropertyName("devices")]
    public IReadOnlyList<WorkloadDevice> Devices { get; init; } = [];

    /// <summary>Read-only host directory mounts for host-inspecting system services. Read-only is ALWAYS true by
    /// contract, and each host path comes from a parameter constrained to the safe root allowlist.</summary>
    [JsonPropertyName("hostPaths")]
    public IReadOnlyList<WorkloadHostPath> HostPaths { get; init; } = [];

    /// <summary>Curated node-local host-path persistence — the per-node persistence answer.</summary>
    [JsonPropertyName("nodeLocalState")]
    public NodeLocalState? NodeLocalState { get; init; }
}

/// <summary>Closed capability set. Serialized as the raw Linux capability name.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<WorkloadCapability>))]
public enum WorkloadCapability
{
    [JsonStringEnumMemberName("NET_ADMIN")]
    NetAdmin = 0,

    [JsonStringEnumMemberName("NET_RAW")]
    NetRaw = 1,

    [JsonStringEnumMemberName("SYS_ADMIN")]
    SysAdmin = 2,

    /// <summary>Bind a privileged (&lt; 1024 / low-PSM) socket — e.g. a bundled bluetoothd binding the L2CAP
    /// ATT/SDP PSM for its GATT database. Low-risk; does not grant host or device access.</summary>
    [JsonStringEnumMemberName("NET_BIND_SERVICE")]
    NetBindService = 3,
}

/// <summary>A host character device request bound to a device-path parameter. One slot may bind several
/// devices (<see cref="MaxCount"/>), in which case the parameter resolves to indexed keys.</summary>
public sealed record WorkloadDevice
{
    [JsonPropertyName("parameter")]
    public required string Parameter { get; init; }

    /// <summary>How many devices the operator may bind in this slot (1–8). 1 keeps the un-indexed key.</summary>
    [JsonPropertyName("maxCount")]
    public int MaxCount { get; init; } = 1;

    /// <summary>Class filter for the install-wizard picker. Null means "no filter".</summary>
    [JsonPropertyName("deviceKind")]
    public WorkloadDeviceKind? DeviceKind { get; init; }

    /// <summary>Whether the container joins the device's owning group.</summary>
    [JsonPropertyName("group")]
    public WorkloadDeviceGroup Group { get; init; } = WorkloadDeviceGroup.None;
}

/// <summary>Closed device-class vocabulary, shared with the node hardware inventory's deviceKind.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<WorkloadDeviceKind>))]
public enum WorkloadDeviceKind
{
    [JsonStringEnumMemberName("usb-serial")]
    UsbSerial = 0,

    [JsonStringEnumMemberName("gpu-render")]
    GpuRender = 1,

    [JsonStringEnumMemberName("accelerator")]
    Accelerator = 2,
}

/// <summary>Whether a device slot grants the device's owning group to the container.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<WorkloadDeviceGroup>))]
public enum WorkloadDeviceGroup
{
    [JsonStringEnumMemberName("none")]
    None = 0,

    [JsonStringEnumMemberName("auto")]
    Auto = 1,
}

/// <summary>A READ-ONLY host directory mount, taken from a declared host-path parameter and mounted at an
/// absolute path. There is no flag: read-only is implied and FORCED, since every provider renders it so.</summary>
public sealed record WorkloadHostPath
{
    /// <summary>Name of a declared <c>hostPath</c>-typed parameter carrying the host directory to mount RO.</summary>
    [JsonPropertyName("parameter")]
    public required string Parameter { get; init; }

    /// <summary>Absolute path inside the container the host directory is mounted at, e.g. <c>/host/sys/bus/usb</c>.</summary>
    [JsonPropertyName("mountPath")]
    public required string MountPath { get; init; }

    /// <summary>Author prose, rendered quarantined, explaining why the host read is needed.</summary>
    [JsonPropertyName("description")]
    public string? Description { get; init; }
}

/// <summary>Curated node-local host-path directory. Node-local, non-migratable and security-bearing, since
/// it is a host filesystem write. This is how bluetooth keeps per-node bindkeys.</summary>
public sealed record NodeLocalState
{
    [JsonPropertyName("subdir")]
    public required string Subdir { get; init; }

    [JsonPropertyName("criticality")]
    public required VolumeCriticality Criticality { get; init; }

    [JsonPropertyName("description")]
    public string? Description { get; init; }
}

/// <summary>Persistent storage backed by a claim.</summary>
public sealed record WorkloadVolume
{
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    [JsonPropertyName("mountPath")]
    public required string MountPath { get; init; }

    /// <summary>Requested size, e.g. <c>"64Mi"</c>. Create-only except for an upward resize.</summary>
    [JsonPropertyName("size")]
    public required string Size { get; init; }

    [JsonPropertyName("criticality")]
    public required VolumeCriticality Criticality { get; init; }

    [JsonPropertyName("description")]
    public string? Description { get; init; }

    /// <summary>Access mode — always <c>"rwo"</c> in schemaVersion 1.</summary>
    [JsonPropertyName("accessMode")]
    public string AccessMode { get; init; } = "rwo";
}

/// <summary>A curated, kubernetes-only, read-only ConfigMap mount, whose name a declared parameter supplies.
/// There is no flag: read-only is implied and forced, since the orchestrator renders the mount read-only.</summary>
public sealed record ConfigMapMount
{
    /// <summary>Absolute path the ConfigMap is mounted at, e.g. <c>/etc/matter/paa</c>.</summary>
    [JsonPropertyName("mountPath")]
    public required string MountPath { get; init; }

    /// <summary>Name of a declared install-time parameter carrying the operator-created ConfigMap's name. Validated
    /// against the same undeclared-parameter rule as <c>devices[].parameter</c>/<c>env</c>.</summary>
    [JsonPropertyName("parameter")]
    public required string Parameter { get; init; }
}

/// <summary>Volume criticality. Drives the backup selector and the retention experience.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<VolumeCriticality>))]
public enum VolumeCriticality
{
    /// <summary>Reconstructable on loss; never worth retaining.</summary>
    [JsonStringEnumMemberName("cache")]
    Cache = 0,

    /// <summary>Losing it costs work but not identity; offered on reinstall.</summary>
    [JsonStringEnumMemberName("recoverable")]
    Recoverable = 1,

    /// <summary>Losing it orphans bonded devices or networks, so it is retained indefinitely.</summary>
    [JsonStringEnumMemberName("critical")]
    Critical = 2,
}

/// <summary>Author-declared resource requests and limits. Optional, but preflight-checked under a quota.</summary>
public sealed record WorkloadResources
{
    [JsonPropertyName("requests")]
    public ResourceQuantities? Requests { get; init; }

    [JsonPropertyName("limits")]
    public ResourceQuantities? Limits { get; init; }
}

/// <summary>A cpu/memory quantity pair (Kubernetes quantity strings, kept verbatim).</summary>
public sealed record ResourceQuantities
{
    [JsonPropertyName("cpu")]
    public string? Cpu { get; init; }

    [JsonPropertyName("memory")]
    public string? Memory { get; init; }
}

/// <summary>Polymorphic health contract, one record discriminated on the mode: a host mode may carry
/// <see cref="HeartbeatSeconds"/> and an HTTP probe, <c>http</c> uses the probe descriptor, and <c>container</c>
/// uses neither.</summary>
public sealed record WorkloadHealth
{
    [JsonPropertyName("mode")]
    public required WorkloadHealthMode Mode { get; init; }

    /// <summary>The manifest's <c>heartbeatSeconds</c> for a host mode (default 30), 0 under <c>http</c> and
    /// <c>container</c>. The parser validates it, the spec hash covers it and the stored spec carries it; the
    /// library decides nothing on it.</summary>
    [JsonPropertyName("heartbeatSeconds")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int HeartbeatSeconds { get; init; }

    /// <summary>HTTP probe (required under <c>http</c>, optional under a host mode).</summary>
    [JsonPropertyName("http")]
    public WorkloadHttpProbe? Http { get; init; }
}

/// <summary>HTTP liveness and readiness probe descriptor.</summary>
public sealed record WorkloadHttpProbe
{
    [JsonPropertyName("port")]
    public required int Port { get; init; }

    [JsonPropertyName("livenessPath")]
    public string? LivenessPath { get; init; }

    [JsonPropertyName("readinessPath")]
    public string? ReadinessPath { get; init; }
}

/// <summary>Declarative host prerequisite. Rendered verbatim; not machine-verified.</summary>
public sealed record HostRequirement
{
    [JsonPropertyName("kind")]
    public required HostRequirementKind Kind { get; init; }

    /// <summary>e.g. <c>bluetoothd</c> (dbus-service) or <c>tun</c> (kernel-module).</summary>
    [JsonPropertyName("name")]
    public string? Name { get; init; }

    [JsonPropertyName("description")]
    public string? Description { get; init; }
}

/// <summary>Kind of host prerequisite. Serialized as its spec string name.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<HostRequirementKind>))]
public enum HostRequirementKind
{
    [JsonStringEnumMemberName("dbus-service")]
    DbusService = 0,

    [JsonStringEnumMemberName("kernel-module")]
    KernelModule = 1,

    [JsonStringEnumMemberName("udev-serial")]
    UdevSerial = 2,

    [JsonStringEnumMemberName("multicast")]
    Multicast = 3,
}

/// <summary>Observability surface.</summary>
public sealed record WorkloadObservability
{
    [JsonPropertyName("metrics")]
    public WorkloadMetrics? Metrics { get; init; }
}

/// <summary>Prometheus scrape target.</summary>
public sealed record WorkloadMetrics
{
    [JsonPropertyName("port")]
    public required int Port { get; init; }

    [JsonPropertyName("path")]
    public required string Path { get; init; }
}

/// <summary>One environment variable. The source is a prefixed string kept VERBATIM — the parser does not
/// resolve it, since resolution happens at render time.</summary>
public sealed record WorkloadEnv
{
    [JsonPropertyName("name")]
    public required string Name { get; init; }

    [JsonPropertyName("from")]
    public required string From { get; init; }
}

/// <summary>An install-time parameter, mirroring the per-plugin parameter model. The parser models the declared
/// list exactly; it does NOT synthesize the reserved parameters, which is a render-time concern.</summary>
public sealed record WorkloadParameter
{
    [JsonPropertyName("name")]
    public required string Name { get; init; }

    [JsonPropertyName("type")]
    public required WorkloadParameterType Type { get; init; }

    [JsonPropertyName("displayName")]
    public string? DisplayName { get; init; }

    [JsonPropertyName("description")]
    public string? Description { get; init; }

    [JsonPropertyName("required")]
    public bool Required { get; init; }

    /// <summary>Default value as a string (uniform representation; typed coercion is a render concern).</summary>
    [JsonPropertyName("default")]
    public string? Default { get; init; }

    /// <summary>Allowed values for <see cref="WorkloadParameterType.Enum"/>. Null for other types.</summary>
    [JsonPropertyName("enumValues")]
    public IReadOnlyList<string>? EnumValues { get; init; }

    [JsonPropertyName("hint")]
    public string? Hint { get; init; }

    /// <summary>OPTIONAL author-supplied grouping label. Parameters sharing a section render under one heading in the
    /// install wizard and the day-2 edit form; parameters with no section render first, unlabelled.</summary>
    [JsonPropertyName("section")]
    public string? Section { get; init; }

    /// <summary>How an enum value is picked. Null renders as <see cref="WorkloadParameterWidget.Select"/>; null and not
    /// a default, so a non-enum parameter serialises no widget for the parser to reject.</summary>
    [JsonPropertyName("widget")]
    public WorkloadParameterWidget? Widget { get; init; }

    /// <summary>The label shown for an enum value. A value with no entry shows the value itself.</summary>
    [JsonPropertyName("enumLabels")]
    public IReadOnlyDictionary<string, string>? EnumLabels { get; init; }

    /// <summary>The sections shown while this enum parameter holds a value, names trimmed. Presentation only: a
    /// hidden section's values are still stored, resolved and delivered.</summary>
    [JsonPropertyName("reveals")]
    public IReadOnlyDictionary<string, IReadOnlyList<string>>? Reveals { get; init; }
}

/// <summary>Parameter value type — a closed set mirroring the per-plugin config types plus the workload-specific
/// ones. Serialized as its spec camelCase string name.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<WorkloadParameterType>))]
public enum WorkloadParameterType
{
    [JsonStringEnumMemberName("text")]
    Text = 0,

    [JsonStringEnumMemberName("integer")]
    Integer = 1,

    [JsonStringEnumMemberName("boolean")]
    Boolean = 2,

    [JsonStringEnumMemberName("password")]
    Password = 3,

    [JsonStringEnumMemberName("enum")]
    Enum = 4,

    [JsonStringEnumMemberName("devicePath")]
    DevicePath = 5,

    [JsonStringEnumMemberName("nodeSelector")]
    NodeSelector = 6,

    [JsonStringEnumMemberName("networkInterface")]
    NetworkInterface = 7,

    [JsonStringEnumMemberName("storageSize")]
    StorageSize = 8,

    [JsonStringEnumMemberName("storageClass")]
    StorageClass = 9,

    /// <summary>A read-only host directory to mount. Its value is constrained to the safe root allowlist, so a
    /// parameter edit cannot widen the mount. Append-only ordinal.</summary>
    [JsonStringEnumMemberName("hostPath")]
    HostPath = 10,

    /// <summary>How the workload's pod attaches to the network. That mode vocabulary is NOT a manifest vocabulary
    /// and reaches the operator as the reserved descriptor's enum values. Append-only ordinal.</summary>
    [JsonStringEnumMemberName("networkAttachment")]
    NetworkAttachment = 11,
}

/// <summary>How an enum parameter's value is picked in the install wizard and the day-2 edit form. Serialized as its
/// spec string name.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<WorkloadParameterWidget>))]
public enum WorkloadParameterWidget
{
    [JsonStringEnumMemberName("select")]
    Select = 0,

    [JsonStringEnumMemberName("radio")]
    Radio = 1,
}

/// <summary>The patch escape hatch, preserved VERBATIM: the raw text keeps whitespace and the parsed body allows
/// structural inspection. Its mere presence on a workload is a security-axis requirement.</summary>
[JsonConverter(typeof(UnsupportedPodPatchConverter))]
public sealed record UnsupportedPodPatch
{
    /// <summary>Exact JSON source text of the patch array, preserved character-for-character.</summary>
    [JsonIgnore]
    public string RawJson { get; init; } = string.Empty;

    /// <summary>The patch as a <see cref="JsonElement"/> (a clone that outlives the source document).</summary>
    [JsonIgnore]
    public required JsonElement Body { get; init; }
}

/// <summary>Serializes <see cref="UnsupportedPodPatch"/> AS its bare RFC 6902 array (the manifest wire shape for the
/// <c>unsupportedPodPatch</c> key), and deserializes from that array — preserving the body verbatim.</summary>
public sealed class UnsupportedPodPatchConverter : JsonConverter<UnsupportedPodPatch>
{
    public override UnsupportedPodPatch Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var doc = JsonDocument.ParseValue(ref reader);
        var body = doc.RootElement.Clone();
        return new UnsupportedPodPatch { RawJson = body.GetRawText(), Body = body };
    }

    public override void Write(Utf8JsonWriter writer, UnsupportedPodPatch value, JsonSerializerOptions options)
        => value.Body.WriteTo(writer);
}
