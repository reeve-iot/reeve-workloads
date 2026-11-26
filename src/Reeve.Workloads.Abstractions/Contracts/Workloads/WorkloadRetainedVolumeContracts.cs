using System.Text.Json.Serialization;

namespace Reeve.Workloads.Abstractions.Contracts;

/// <summary>One active retained volume offered on the install-wizard restore step. The wizard keys the
/// restore on <see cref="VolumeId"/> (binding <c>existingClaim.{volumeId}</c>).</summary>
public sealed record RetainedVolumeSummary
{
    /// <summary>The retained-volume record identity.</summary>
    [JsonPropertyName("id")]
    public required Guid Id { get; init; }

    /// <summary>The manifest volume id (e.g. <c>state</c>) — the wizard binds <c>existingClaim.{volumeId}</c> to it.</summary>
    [JsonPropertyName("volumeId")]
    public required string VolumeId { get; init; }

    /// <summary>The plugin type the retained volume belonged to (the match key).</summary>
    [JsonPropertyName("pluginTypeId")]
    public required string GroupId { get; init; }

    /// <summary>The manifest workload id the volume belonged to.</summary>
    [JsonPropertyName("workloadId")]
    public required string WorkloadId { get; init; }

    /// <summary>The provider PVC / named-volume name the data physically lives under (survives on the cluster).</summary>
    [JsonPropertyName("resourceName")]
    public required string ResourceName { get; init; }

    /// <summary>The volume's criticality, as its enum string name (e.g. <c>Critical</c>).</summary>
    [JsonPropertyName("criticality")]
    public required string Criticality { get; init; }

    /// <summary>The author's free-text volume description quoted in the restore choice, or null when none.</summary>
    [JsonPropertyName("criticalityDescription")]
    public string? CriticalityDescription { get; init; }

    /// <summary>Requested/allocated size, e.g. <c>"512Mi"</c> (surfaced verbatim), or null when unknown.</summary>
    [JsonPropertyName("size")]
    public string? Size { get; init; }

    /// <summary>The runtime provider the volume lives on, as its enum string name (e.g. <c>Kubernetes</c>).</summary>
    [JsonPropertyName("provider")]
    public required string Provider { get; init; }

    /// <summary>The now-uninstalled workload instance the volume came from — surfaced so the wizard can
    /// warn the data may be from a different prior instance/hardware.</summary>
    [JsonPropertyName("sourceWorkloadInstanceId")]
    public required Guid SourceWorkloadInstanceId { get; init; }

    /// <summary>When the volume was retained (teardown time, UTC).</summary>
    [JsonPropertyName("retainedAtUtc")]
    public required DateTimeOffset RetainedAtUtc { get; init; }
}

/// <summary>The 200 body for the retained-volumes list: filtered to one plugin type for the install wizard,
/// or all active volumes (any type) for the "Retained data" panel, when <c>pluginTypeId</c> is omitted.</summary>
public sealed record RetainedVolumeListResponse
{
    /// <summary>The matching active retained volumes, ordered by retention time (oldest first).</summary>
    [JsonPropertyName("items")]
    public required IReadOnlyList<RetainedVolumeSummary> Items { get; init; }
}

/// <summary>The 200 body for the retained-volume purge action, which physically frees the volume's storage
/// and marks the durable row purged; this echoes the id and purge timestamp so the FE can update the row.</summary>
public sealed record RetainedVolumePurgeResponse
{
    /// <summary>The retained-volume record that was purged.</summary>
    [JsonPropertyName("id")]
    public required Guid Id { get; init; }

    /// <summary>When the volume was purged (its storage freed + the row stamped), UTC.</summary>
    [JsonPropertyName("purgedAtUtc")]
    public required DateTimeOffset PurgedAtUtc { get; init; }
}
