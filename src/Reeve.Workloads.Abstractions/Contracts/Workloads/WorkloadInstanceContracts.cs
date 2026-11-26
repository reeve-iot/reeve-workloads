using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;
using Reeve.Workloads.Abstractions.Contracts.Deployment;

namespace Reeve.Workloads.Abstractions.Contracts;

/// <summary>One workload instance projected for the operator surface. A provider is null until the instance
/// first deploys.</summary>
public sealed record WorkloadInstanceSummary
{
    [JsonPropertyName("id")]
    public required Guid Id { get; init; }

    [JsonPropertyName("pluginInstanceId")]
    public required Guid GroupInstanceId { get; init; }

    [JsonPropertyName("pluginTypeId")]
    public required string GroupId { get; init; }

    [JsonPropertyName("workloadId")]
    public required string WorkloadId { get; init; }

    /// <summary>The workload's scheduling kind from its manifest, so a client offers only the actions that kind has
    /// (a scan only for <see cref="WorkloadKind.ScanPerNode"/>). Null when the host cannot tell.</summary>
    [JsonPropertyName("kind")]
    public WorkloadKind? Kind { get; init; }

    /// <summary>The runtime provider this instance is (or will be) materialized on; null until first deploy.</summary>
    [JsonPropertyName("provider")]
    public WorkloadProviderId? Provider { get; init; }

    /// <summary>The lifecycle state as its domain-enum string name.</summary>
    [JsonPropertyName("state")]
    public required string State { get; init; }

    /// <summary>The machine-readable state reason as its domain-enum string name.</summary>
    [JsonPropertyName("stateReason")]
    public required string StateReason { get; init; }

    /// <summary>Human-readable free-text companion to <see cref="StateReason"/>, when present.</summary>
    [JsonPropertyName("stateMessage")]
    public string? StateMessage { get; init; }

    /// <summary>Transient image-pull progress 0..100, null when not provisioning or indeterminate. Cosmetic, and
    /// cleared the moment the deploy settles.</summary>
    [JsonPropertyName("provisioningPercent")]
    public int? ProvisioningPercent { get; init; }

    /// <summary>SHA-256 of the desired (rendered) spec, or null before first render.</summary>
    [JsonPropertyName("desiredSpecHash")]
    public string? DesiredSpecHash { get; init; }

    /// <summary>SHA-256 of the spec last successfully applied to the provider, or null.</summary>
    [JsonPropertyName("appliedSpecHash")]
    public string? AppliedSpecHash { get; init; }

    /// <summary>Per-workload observe-only flag: the reconciler skips drift correction for it.</summary>
    [JsonPropertyName("observeOnly")]
    public required bool ObserveOnly { get; init; }

    /// <summary>Provider-reported restart count, the crash-loop detection input.</summary>
    [JsonPropertyName("restartCount")]
    public required int RestartCount { get; init; }

    /// <summary>When the instance last moved into Healthy (or refreshed its Healthy reason or message), or null.</summary>
    [JsonPropertyName("lastHealthyAtUtc")]
    public DateTimeOffset? LastHealthyAtUtc { get; init; }

    /// <summary>The CURRENT resolved install-time parameters, seeding the day-2 edit form. A declared password
    /// rides MASKED as a sentinel the form re-posts to retain the stored value; an undeclared param rides verbatim.</summary>
    [JsonPropertyName("resolvedParameters")]
    public IReadOnlyDictionary<string, string?> ResolvedParameters { get; init; } = new Dictionary<string, string?>();
}

/// <summary>Per-instance summaries plus the per-plugin aggregate-health chip: the worst NON-SUSPENDED state,
/// with suspension reported separately so it can never mask a Failed critical workload.</summary>
public sealed record WorkloadInstanceListResponse
{
    /// <summary>The projected workload instances (filtered by <c>pluginInstanceId</c> when the query supplied one).</summary>
    [JsonPropertyName("instances")]
    public required IReadOnlyList<WorkloadInstanceSummary> Instances { get; init; }

    /// <summary>The per-plugin aggregate-health rollups (one per distinct plugin instance in <see cref="Instances"/>).</summary>
    [JsonPropertyName("pluginHealth")]
    public required IReadOnlyList<WorkloadPluginHealth> PluginHealth { get; init; }
}

/// <summary>The aggregate-health chip for one plugin instance: the worst non-suspended state, or null when the
/// plugin has no instances or only suspended ones.</summary>
public sealed record WorkloadPluginHealth
{
    [JsonPropertyName("pluginInstanceId")]
    public required Guid GroupInstanceId { get; init; }

    /// <summary>Worst non-suspended state as its domain-enum string name, or null (empty / all-suspended).</summary>
    [JsonPropertyName("aggregateState")]
    public string? AggregateState { get; init; }

    /// <summary>True when at least one instance of this plugin is operator-suspended (drives the paused chip).</summary>
    [JsonPropertyName("anySuspended")]
    public required bool AnySuspended { get; init; }
}

/// <summary>The per-workload reconciler kill-switch: drift correction stops while the desired spec is still
/// recorded and status still read. It reveals no secret, so it sits under the day-2 permission.</summary>
public sealed record SetWorkloadObserveOnlyRequest
{
    /// <summary>True to freeze reconciliation for this instance (observe-only); false to resume it.</summary>
    [JsonPropertyName("observeOnly")]
    public required bool ObserveOnly { get; init; }
}

/// <summary>The instance summary plus a provider status snapshot when a provider is resolvable; otherwise the
/// status is null and the caller falls back to the last-known DB state on the instance.</summary>
public sealed record WorkloadInstanceDetailResponse
{
    [JsonPropertyName("instance")]
    public required WorkloadInstanceSummary Instance { get; init; }

    /// <summary>Live provider status when a provider is resolvable; null when none is registered.</summary>
    [JsonPropertyName("status")]
    public WorkloadStatusSnapshot? Status { get; init; }

    /// <summary>The provider's recent events, newest-LAST and capped at 20. Empty when no provider is
    /// resolvable or none were reported.</summary>
    [JsonPropertyName("recentEvents")]
    public IReadOnlyList<WorkloadEventDto> RecentEvents { get; init; } = [];

    /// <summary>Reserved parameters are BACKEND-PROJECTED and never appear in the deployment manifest, so an edit
    /// form built from declared parameters alone freezes them. Projected against the instance's REAL provider.</summary>
    [JsonPropertyName("reservedParameters")]
    public IReadOnlyList<WorkloadReservedParameterDescriptor> ReservedParameters { get; init; } = [];
}

/// <summary>One provider event line for the Deployment tab, mirroring the provider SPI's event shape: time,
/// severity, provider reason and human-readable detail.</summary>
public sealed record WorkloadEventDto
{
    [JsonPropertyName("timestampUtc")]
    public DateTimeOffset? TimestampUtc { get; init; }

    /// <summary>The event severity/type as its string name (e.g. <c>"Normal"</c>, <c>"Warning"</c>).</summary>
    [JsonPropertyName("type")]
    public required string Type { get; init; }

    /// <summary>The provider reason (k8s Event.reason / docker action — e.g. <c>"FailedScheduling"</c>).</summary>
    [JsonPropertyName("reason")]
    public required string Reason { get; init; }

    /// <summary>The human-readable event message.</summary>
    [JsonPropertyName("message")]
    public required string Message { get; init; }
}

/// <summary>A provider-observed status snapshot: provider-level readiness ONLY. Under a host health mode the
/// Healthy signal comes from the host's health contributor, not from here.</summary>
public sealed record WorkloadStatusSnapshot
{
    /// <summary>The provider phase of the runtime object as its enum string name (e.g. <c>"Ready"</c>, <c>"Failed"</c>).</summary>
    [JsonPropertyName("phase")]
    public required string Phase { get; init; }

    /// <summary>True when the provider reports the runtime object ready (pod Ready / container healthy).</summary>
    [JsonPropertyName("providerReady")]
    public required bool ProviderReady { get; init; }

    /// <summary>Provider-reported restart count.</summary>
    [JsonPropertyName("restartCount")]
    public required int RestartCount { get; init; }

    /// <summary>Human-readable provider phase/message (e.g. "CrashLoopBackOff", "0/1 ready"), when known.</summary>
    [JsonPropertyName("message")]
    public string? Message { get; init; }

    /// <summary>The image the provider observed running (post-mirror ref), when known.</summary>
    [JsonPropertyName("observedImage")]
    public string? ObservedImage { get; init; }
}

/// <summary>The derived live-status of a volume resize, serialized as its STRING name so a reorder cannot
/// reinterpret a cached value. Unknown is the honest degrade and MUST NOT be reported as Stable.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<WorkloadVolumeResizeState>))]
public enum WorkloadVolumeResizeState
{
    /// <summary>The live PVC status could not be read/trusted (403 / API fault / no client). NEVER a false "stable".</summary>
    [JsonStringEnumMemberName("unknown")]
    Unknown = 0,

    /// <summary>No resize in progress — the actual capacity has settled at the requested size.</summary>
    [JsonStringEnumMemberName("stable")]
    Stable = 1,

    /// <summary>The controller is expanding the underlying volume (<c>status.conditions[Resizing]=True</c>).</summary>
    [JsonStringEnumMemberName("resizing")]
    Resizing = 2,

    /// <summary>The volume grew; the node must expand the filesystem (<c>status.conditions[FileSystemResizePending]=True</c>).</summary>
    [JsonStringEnumMemberName("fileSystemResizePending")]
    FileSystemResizePending = 3,

    /// <summary>A LARGER size the live PVC spec has not accepted — pending admission, or refused outright by a
    /// non-expandable StorageClass. MUST NOT read as Stable, or the refusal is swallowed into a backend log.</summary>
    [JsonStringEnumMemberName("resizeRequested")]
    ResizeRequested = 4,

    /// <summary>The volume's claim does not exist (the API server answered 404). Distinct from <see cref="Unknown"/>,
    /// which is a read that could not be made or trusted.</summary>
    [JsonStringEnumMemberName("missing")]
    Missing = 5,
}

/// <summary>The live status of ONE declared volume against its PVC binding. Nullable size fields are null when
/// the live read could not supply them — never a fabricated capacity.</summary>
public sealed record WorkloadVolumeStatus
{
    /// <summary>The manifest volume id this status is for.</summary>
    [JsonPropertyName("volumeId")]
    public required string VolumeId { get; init; }

    /// <summary>The container mount path the volume is mounted at.</summary>
    [JsonPropertyName("mountPath")]
    public required string MountPath { get; init; }

    /// <summary>The live PVC's requested storage when readable, else the declared manifest size. This is the size
    /// an upward resize targets.</summary>
    [JsonPropertyName("requestedSize")]
    public string? RequestedSize { get; init; }

    /// <summary>The ACTUAL provisioned capacity, which can lag the requested size while a resize is in flight.
    /// Null when the PVC was not readable or not yet bound — never fabricated.</summary>
    [JsonPropertyName("actualCapacity")]
    public string? ActualCapacity { get; init; }

    /// <summary>The effective StorageClass (live PVC's <c>spec.storageClassName</c> when readable, else the declared one), or null for the cluster default.</summary>
    [JsonPropertyName("storageClassName")]
    public string? StorageClassName { get; init; }

    /// <summary>The derived resize state (<see cref="WorkloadVolumeResizeState.Unknown"/> when the read could not be trusted — never a false "stable").</summary>
    [JsonPropertyName("resizeState")]
    public required WorkloadVolumeResizeState ResizeState { get; init; }

    /// <summary>The resize condition's human-readable message (<c>status.conditions[].message</c>), when a resize condition is present.</summary>
    [JsonPropertyName("resizeMessage")]
    public string? ResizeMessage { get; init; }
}

/// <summary>Always a well-formed body: for a non-Kubernetes instance the provider reads unavailable, the volume
/// list is empty and the message explains why — HTTP 200, never 500.</summary>
public sealed record WorkloadVolumeStatusResponse
{
    /// <summary>False when volume live-status is not applicable (non-Kubernetes provider / workload no longer declared).</summary>
    [JsonPropertyName("providerAvailable")]
    public required bool ProviderAvailable { get; init; }

    /// <summary>The per-declared-volume live status (empty when not applicable or the workload declares no volumes).</summary>
    [JsonPropertyName("volumes")]
    public required IReadOnlyList<WorkloadVolumeStatus> Volumes { get; init; }

    /// <summary>Human-readable status line (e.g. the non-applicable explanation), when present.</summary>
    [JsonPropertyName("message")]
    public string? Message { get; init; }
}

/// <summary>An operator UPWARD volume-resize, sized as a Kubernetes storage quantity. The gate is upward-only and
/// numeric-bounded: a shrink or over-cap value is a 400, and an equal size is an idempotent no-op.</summary>
public sealed record SetWorkloadVolumeSizeRequest
{
    /// <summary>The new requested size as a Kubernetes storage quantity (e.g. <c>"10Gi"</c>, <c>"512Mi"</c>).</summary>
    [JsonPropertyName("size")]
    [StringLength(64)]
    public required string Size { get; init; }
}

/// <summary>The 200 body for an accepted volume-resize, echoing the effective requested size. LIVE resize
/// progress is observed through the volumes read, not from here.</summary>
public sealed record WorkloadVolumeResizeResponse
{
    /// <summary>The declared volume id the resize targeted.</summary>
    [JsonPropertyName("volumeId")]
    public required string VolumeId { get; init; }

    /// <summary>The effective requested size after the request (the accepted new size, or the unchanged current size).</summary>
    [JsonPropertyName("requestedSize")]
    public required string RequestedSize { get; init; }

    /// <summary>True when the request PERSISTED a new (larger) size; false for the equal-size idempotent no-op.</summary>
    [JsonPropertyName("applied")]
    public required bool Applied { get; init; }
}

/// <summary>Always a well-formed body: with no resolvable provider the availability flag is false, the lines are
/// empty and the message explains why — HTTP 200, never 500.</summary>
public sealed record WorkloadLogsResponse
{
    /// <summary>False when no runtime provider is registered for the instance's provider (logs cannot be served).</summary>
    [JsonPropertyName("providerAvailable")]
    public required bool ProviderAvailable { get; init; }

    /// <summary>The recent log lines (empty when unavailable / no output yet).</summary>
    [JsonPropertyName("lines")]
    public required IReadOnlyList<WorkloadLogLineDto> Lines { get; init; }

    /// <summary>True when older lines were dropped to satisfy the requested tail.</summary>
    [JsonPropertyName("truncated")]
    public required bool Truncated { get; init; }

    /// <summary>Human-readable status line (e.g. the no-provider explanation), when present.</summary>
    [JsonPropertyName("message")]
    public string? Message { get; init; }
}

/// <summary>A single log line, or a synthesized restart-boundary or gap marker.</summary>
public sealed record WorkloadLogLineDto
{
    [JsonPropertyName("text")]
    public required string Text { get; init; }

    [JsonPropertyName("atUtc")]
    public DateTimeOffset? AtUtc { get; init; }

    /// <summary>True when this line is a synthesized "--- container restarted ---" boundary, not real output.</summary>
    [JsonPropertyName("isRestartMarker")]
    public required bool IsRestartMarker { get; init; }

    /// <summary>True when this line is a synthesized "log lines may be missing here" marker of a live tail, not real output.</summary>
    [JsonPropertyName("isGapMarker")]
    public bool IsGapMarker { get; init; }
}

/// <summary>The 409 body when a lifecycle command is rejected because an update intent is active. Carries the
/// intent id so the UI can point the operator at the in-flight update.</summary>
public sealed record WorkloadCommandConflictResponse
{
    [JsonPropertyName("workloadInstanceId")]
    public required Guid WorkloadInstanceId { get; init; }

    /// <summary>The id of the active update intent that is serializing mutations on this instance.</summary>
    [JsonPropertyName("activeIntentId")]
    public required Guid ActiveIntentId { get; init; }

    /// <summary>Human-readable explanation for the operator/UI.</summary>
    [JsonPropertyName("message")]
    public required string Message { get; init; }
}

/// <summary>The 409 body when a provider switch is blocked by live instances on the old provider. The switch
/// succeeds only once none remain.</summary>
public sealed record WorkloadProviderSwitchBlockedResponse
{
    /// <summary>The currently-effective provider the instances are on (the switch is blocked away from this).</summary>
    [JsonPropertyName("currentProvider")]
    public required WorkloadProviderId CurrentProvider { get; init; }

    /// <summary>The provider the operator attempted to switch to.</summary>
    [JsonPropertyName("requestedProvider")]
    public required WorkloadProviderId RequestedProvider { get; init; }

    /// <summary>The live instances still on <see cref="CurrentProvider"/> that block the switch (need migration first).</summary>
    [JsonPropertyName("blockingInstances")]
    public required IReadOnlyList<WorkloadProviderSwitchBlocker> BlockingInstances { get; init; }

    /// <summary>Human-readable explanation for the operator/UI.</summary>
    [JsonPropertyName("message")]
    public required string Message { get; init; }
}

/// <summary>One live instance blocking a provider switch, with enough identity for a migration wizard to key on.</summary>
public sealed record WorkloadProviderSwitchBlocker
{
    [JsonPropertyName("id")]
    public required Guid Id { get; init; }

    [JsonPropertyName("pluginTypeId")]
    public required string GroupId { get; init; }

    [JsonPropertyName("workloadId")]
    public required string WorkloadId { get; init; }
}

/// <summary>The push payload broadcast to the admin group whenever an instance's persisted lifecycle state or
/// message changes, so the operator surface refreshes without polling.</summary>
public sealed record WorkloadStatusChangedEvent
{
    /// <summary>The instance identity — the id the host's seams are keyed on and the query row key.</summary>
    [JsonPropertyName("id")]
    public required Guid Id { get; init; }

    [JsonPropertyName("pluginInstanceId")]
    public required Guid GroupInstanceId { get; init; }

    [JsonPropertyName("pluginTypeId")]
    public required string GroupId { get; init; }

    [JsonPropertyName("workloadId")]
    public required string WorkloadId { get; init; }

    /// <summary>The new lifecycle state as its domain-enum string name.</summary>
    [JsonPropertyName("state")]
    public required string State { get; init; }

    /// <summary>The machine-readable state reason as its domain-enum string name.</summary>
    [JsonPropertyName("stateReason")]
    public required string StateReason { get; init; }

    /// <summary>Human-readable free-text companion to <see cref="StateReason"/>, when present.</summary>
    [JsonPropertyName("stateMessage")]
    public string? StateMessage { get; init; }
}
