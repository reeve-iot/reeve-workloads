namespace Reeve.Workloads.Abstractions.Domain.Entities.Documents;

/// <summary>An update intent's per-workload parameter snapshot: one map of resolved name/value pairs per
/// workload, both levels sorted by ordinal key. The column is a map of maps, not a flat one.</summary>
public sealed class WorkloadParameterMapDocument : IWorkloadJsonDocument
{
    public int SchemaVersion { get; init; } = 1;

    public string? SourceFingerprint { get; init; }

    public bool Unreadable { get; init; }

    public List<WorkloadParametersDocument> Workloads { get; init; } = [];
}

/// <summary>One workload's resolved parameters.</summary>
public sealed class WorkloadParametersDocument
{
    /// <summary>The workload id the map was stored under.</summary>
    public string WorkloadId { get; init; } = string.Empty;

    public List<WorkloadStringPairDocument> Entries { get; init; } = [];
}

/// <summary>An update intent's rendered desired specs: one entry per workload, sorted by ordinal map key.</summary>
/// <remarks>The spec's secret material is never here, as it was never in the column: it is grafted back at read.</remarks>
public sealed class WorkloadSpecMapDocument : IWorkloadJsonDocument
{
    public int SchemaVersion { get; init; } = 1;

    public string? SourceFingerprint { get; init; }

    public bool Unreadable { get; init; }

    public List<WorkloadSpecDocument> Workloads { get; init; } = [];
}

/// <summary>One workload's desired spec. Lists keep their order, which is significant for some (attachments);
/// maps are key-sorted pairs.</summary>
public sealed class WorkloadSpecDocument
{
    /// <summary>The key the spec was stored under in the map.</summary>
    public string Key { get; init; } = string.Empty;

    /// <summary>The group instance. Still named for the module: a document's property names are the JSON keys of
    /// stored rows, so the rename from module to group stops at the mapper.</summary>
    public Guid ModuleInstanceId { get; init; }

    /// <summary>The group, under the name it is stored with; see <see cref="ModuleInstanceId"/>.</summary>
    public string ModuleId { get; init; } = string.Empty;

    public string WorkloadId { get; init; } = string.Empty;

    public Guid InstanceId { get; init; }

    public string Inst8 { get; init; } = string.Empty;

    /// <summary>The canonical provider id.</summary>
    public string Provider { get; init; } = string.Empty;

    public string ResourceName { get; init; } = string.Empty;

    public string SecretName { get; init; } = string.Empty;

    public List<WorkloadStringPairDocument> Labels { get; init; } = [];

    public string Image { get; init; } = string.Empty;

    public List<string> Platforms { get; init; } = [];

    /// <summary>A <c>WorkloadKind</c> member name.</summary>
    public string Kind { get; init; } = string.Empty;

    public List<WorkloadStringPairDocument> NodeSelector { get; init; } = [];

    public SpecResourcesDocument? Resources { get; init; }

    public SpecNetworkDocument? Network { get; init; }

    public SpecPrivilegesDocument? Privileges { get; init; }

    /// <summary>Nullable so older rows omit it; written as true or null, never false.</summary>
    public bool? UserNamespace { get; init; }

    /// <summary>Skip report, written as true or null, never false.</summary>
    public bool? UserNamespaceSkippedForHostNetwork { get; init; }

    /// <summary>Skip report, written as true or null, never false.</summary>
    public bool? UserNamespaceSkippedForDevicePassthrough { get; init; }

    public List<SpecVolumeDocument> Volumes { get; init; } = [];

    public List<SpecConfigMapMountDocument> ConfigMapMounts { get; init; } = [];

    public SpecHealthDocument Health { get; init; } = new();

    public SpecObservabilityDocument? Observability { get; init; }

    public List<SpecEnvVarDocument> Env { get; init; } = [];

    /// <summary>Null when the row has no such key; the mapper reads that as none.</summary>
    public List<SpecProvisionedCredentialDocument>? ProvisionedCredentials { get; init; }

    public List<SpecHostRequirementDocument> HostRequirements { get; init; } = [];

    /// <summary>The raw RFC 6902 patch array, verbatim, or null when the workload has none.</summary>
    public string? UnsupportedPodPatchJson { get; init; }

    public long RestartNonce { get; init; }

    public string? PendingScanRunId { get; init; }

    public List<string>? ScanReadyNodeNames { get; init; }

    public List<string>? ScanIneligibleNodeNames { get; init; }

    public string SpecHash { get; init; } = string.Empty;
}

public sealed class SpecResourcesDocument
{
    public string? RequestsCpu { get; init; }

    public string? RequestsMemory { get; init; }

    public string? LimitsCpu { get; init; }

    public string? LimitsMemory { get; init; }
}

public sealed class SpecNetworkDocument
{
    public bool HostNetwork { get; init; }

    /// <summary>A <c>WorkloadNetworkMode</c> member name.</summary>
    public string Mode { get; init; } = string.Empty;

    /// <summary>In declared order, which is significant.</summary>
    public List<string> Attachments { get; init; } = [];

    public bool Mdns { get; init; }

    public bool Ipv6Required { get; init; }

    public List<SpecPortDocument> Ports { get; init; } = [];
}

public sealed class SpecPortDocument
{
    public string Name { get; init; } = string.Empty;

    public int Port { get; init; }

    public string Protocol { get; init; } = string.Empty;

    public int? HostPort { get; init; }
}

public sealed class SpecPrivilegesDocument
{
    public List<string> Capabilities { get; init; } = [];

    public bool Privileged { get; init; }

    /// <summary>Nullable so older rows omit it; written as true or null, never false.</summary>
    public bool? DevicePassthroughPrivileged { get; init; }

    public bool RunAsRoot { get; init; }

    /// <summary>Nullable so older rows omit it; null when no volume group is declared.</summary>
    public int? FsGroup { get; init; }

    public bool TunDevice { get; init; }

    public bool CallsBackendRest { get; init; }

    public string? DbusHostPath { get; init; }

    public List<string> DevicePaths { get; init; } = [];

    public List<int> DeviceGroupIds { get; init; } = [];

    public bool JoinsDeviceGroups { get; init; }

    public List<SpecHostPathDocument> HostPaths { get; init; } = [];

    public SpecNodeLocalStateDocument? NodeLocalState { get; init; }
}

public sealed class SpecHostPathDocument
{
    public string HostPath { get; init; } = string.Empty;

    public string MountPath { get; init; } = string.Empty;
}

public sealed class SpecNodeLocalStateDocument
{
    public string HostPath { get; init; } = string.Empty;

    /// <summary>A <c>VolumeCriticality</c> member name.</summary>
    public string Criticality { get; init; } = string.Empty;
}

public sealed class SpecVolumeDocument
{
    public string Id { get; init; } = string.Empty;

    public string MountPath { get; init; } = string.Empty;

    public string Size { get; init; } = string.Empty;

    /// <summary>A <c>VolumeCriticality</c> member name.</summary>
    public string Criticality { get; init; } = string.Empty;

    public string AccessMode { get; init; } = string.Empty;

    public string ResourceName { get; init; } = string.Empty;

    public bool ExistingClaim { get; init; }

    public string? StorageClassName { get; init; }
}

public sealed class SpecConfigMapMountDocument
{
    public string MountPath { get; init; } = string.Empty;

    public string ConfigMapName { get; init; } = string.Empty;
}

public sealed class SpecHealthDocument
{
    /// <summary>The health mode's wire value, verbatim: an open vocabulary, so a host's own mode is kept as is.</summary>
    public string Mode { get; init; } = string.Empty;

    public int HeartbeatSeconds { get; init; }

    public SpecHttpProbeDocument? Http { get; init; }
}

public sealed class SpecHttpProbeDocument
{
    public int Port { get; init; }

    public string? LivenessPath { get; init; }

    public string? ReadinessPath { get; init; }
}

public sealed class SpecObservabilityDocument
{
    public SpecMetricsDocument? Metrics { get; init; }
}

public sealed class SpecMetricsDocument
{
    public int Port { get; init; }

    public string Path { get; init; } = string.Empty;
}

public sealed class SpecEnvVarDocument
{
    public string Name { get; init; } = string.Empty;

    /// <summary>A <c>ResolvedEnvKind</c> member name.</summary>
    public string Kind { get; init; } = string.Empty;

    public string? Value { get; init; }

    public string? SecretRef { get; init; }

    public string? CredentialId { get; init; }

    /// <summary>Set only for an env outside the SpecHash; older rows omit it and read back as false.</summary>
    public bool? ExcludedFromSpecHash { get; init; }
}

public sealed class SpecProvisionedCredentialDocument
{
    public string CredentialId { get; init; } = string.Empty;

    public string Name { get; init; } = string.Empty;
}

public sealed class SpecHostRequirementDocument
{
    /// <summary>A <c>HostRequirementKind</c> member name.</summary>
    public string Kind { get; init; } = string.Empty;

    public string? Name { get; init; }

    public string? Description { get; init; }
}

/// <summary>Rollback diagnostics captured before teardown, workloads in the order they were captured.</summary>
public sealed class WorkloadUpdateEvidenceDocument : IWorkloadJsonDocument
{
    public int SchemaVersion { get; init; } = 1;

    public string? SourceFingerprint { get; init; }

    public bool Unreadable { get; init; }

    public string ToVersion { get; init; } = string.Empty;

    public DateTimeOffset CapturedAtUtc { get; init; }

    public List<EvidenceWorkloadDocument> Workloads { get; init; } = [];
}

public sealed class EvidenceWorkloadDocument
{
    public string WorkloadId { get; init; } = string.Empty;

    public List<string> PreviousContainerLogTail { get; init; } = [];

    public List<string> RecentEvents { get; init; } = [];

    public string? Note { get; init; }
}

/// <summary>A step's health-gate clock, field for field the state the gate threads across ticks.</summary>
public sealed class GateClockStateDocument : IWorkloadJsonDocument
{
    public int SchemaVersion { get; init; } = 1;

    public string? SourceFingerprint { get; init; }

    public bool Unreadable { get; init; }

    public DateTimeOffset? ClockStartedAtUtc { get; init; }

    public DateTimeOffset? PausedSinceUtc { get; init; }

    public TimeSpan AccumulatedPause { get; init; }

    public DateTimeOffset? ReconnectGraceUntilUtc { get; init; }

    public bool GraceConsumed { get; init; }
}

/// <summary>One entry of the pipeline journal: a stage the walk entered, when, in which direction, and how it
/// ended. <see cref="StageId"/> is a free string on purpose — a host registers its own stages, so an id this
/// build does not know is an ordinary thing to read back, not a corruption.</summary>
public sealed class PipelineStageEntryDocument
{
    public string StageId { get; init; } = string.Empty;

    public DateTimeOffset EnteredAtUtc { get; init; }

    /// <summary>Enum MEMBER NAME, strictly, like every other enum this mapper writes.</summary>
    public string Direction { get; init; } = string.Empty;

    public string State { get; init; } = string.Empty;

    public string? Detail { get; init; }
}

/// <summary>The walk's durable position: which stages were entered, in order, and where the intent stands.
/// The pipeline itself is not persisted (a stage the build no longer registers is an id nothing can advance,
/// not a plan with a hole), so this document is the whole of what survives a leader failover.</summary>
/// <remarks>Read it with <see cref="WorkloadDocumentRead.Decide"/> and never with
/// <see cref="WorkloadDocumentRead.Read"/>. Degrading an unreadable journal to an empty one would report a walk
/// that is part-way through compensation as one that never started, and the engine would then replay forwards
/// over work it had already undone. "Cannot read it" is a decision here, not a default.</remarks>
public sealed class PipelineJournalDocument : IWorkloadJsonDocument
{
    public int SchemaVersion { get; init; } = 1;

    public string? SourceFingerprint { get; init; }

    public bool Unreadable { get; init; }

    public string Direction { get; init; } = string.Empty;

    public string State { get; init; } = string.Empty;

    public List<PipelineStageEntryDocument> Entries { get; init; } = [];
}
