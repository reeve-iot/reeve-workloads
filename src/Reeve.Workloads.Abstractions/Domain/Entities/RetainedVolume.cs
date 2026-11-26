using Reeve.Workloads.Abstractions.Contracts;
using Reeve.Workloads.Abstractions.Contracts.Deployment;

namespace Reeve.Workloads.Abstractions.Domain.Entities;

/// <summary>One volume RETAINED at teardown. Deliberately self-contained, because retention outlives the plugin
/// instance and there is nothing left to navigate to; a retained volume is kept indefinitely, never auto-deleted.</summary>
public sealed class RetainedVolume
{
    private RetainedVolume()
    {
    }

    private RetainedVolume(
        Guid id,
        string pluginTypeId,
        string workloadId,
        string volumeId,
        string resourceName,
        string size,
        VolumeCriticality criticality,
        string? description,
        WorkloadProviderId provider,
        RetainedVolumeSchemeId volumeNamingScheme,
        Guid sourceGroupInstanceId,
        Guid sourceWorkloadInstanceId,
        DateTimeOffset retainedAtUtc)
    {
        Id = id == Guid.Empty ? throw new ArgumentException("Id is required.", nameof(id)) : id;
        GroupId = NormalizeRequired(pluginTypeId, nameof(pluginTypeId));
        WorkloadId = NormalizeRequired(workloadId, nameof(workloadId));
        VolumeId = NormalizeRequired(volumeId, nameof(volumeId));
        ResourceName = NormalizeRequired(resourceName, nameof(resourceName));
        Size = NormalizeRequired(size, nameof(size));
        Criticality = criticality;
        Description = string.IsNullOrWhiteSpace(description) ? null : description.Trim();
        Provider = provider;
        VolumeNamingScheme = volumeNamingScheme;
        SourceGroupInstanceId = sourceGroupInstanceId == Guid.Empty
            ? throw new ArgumentException("Source plugin instance id is required.", nameof(sourceGroupInstanceId))
            : sourceGroupInstanceId;
        SourceWorkloadInstanceId = sourceWorkloadInstanceId == Guid.Empty
            ? throw new ArgumentException("Source workload instance id is required.", nameof(sourceWorkloadInstanceId))
            : sourceWorkloadInstanceId;
        RetainedAtUtc = retainedAtUtc;
    }

    /// <summary>All identity and metadata is copied in at retention time, because the source plugin and
    /// workload instance are uninstalled and gone.</summary>
    public static RetainedVolume Record(
        Guid id,
        string pluginTypeId,
        string workloadId,
        string volumeId,
        string resourceName,
        string size,
        VolumeCriticality criticality,
        string? description,
        WorkloadProviderId provider,
        RetainedVolumeSchemeId volumeNamingScheme,
        Guid sourceGroupInstanceId,
        Guid sourceWorkloadInstanceId,
        DateTimeOffset retainedAtUtc)
        => new(id, pluginTypeId, workloadId, volumeId, resourceName, size, criticality, description, provider,
            volumeNamingScheme, sourceGroupInstanceId, sourceWorkloadInstanceId, retainedAtUtc);

    /// <summary>The record identity (the "Retained data" UI keys purge/restore on this).</summary>
    public Guid Id { get; private set; }

    /// <summary>The plugin type the retained volume belonged to (survives the plugin's uninstall).</summary>
    public string GroupId { get; private set; } = null!;

    /// <summary>The manifest workload id the volume belonged to.</summary>
    public string WorkloadId { get; private set; } = null!;

    /// <summary>The manifest volume id (e.g. <c>state</c>).</summary>
    public string VolumeId { get; private set; } = null!;

    /// <summary>The provider PVC / named-volume name the data physically lives under (survives on the cluster).</summary>
    public string ResourceName { get; private set; } = null!;

    /// <summary>Requested/allocated size, e.g. <c>"512Mi"</c>, surfaced verbatim in the informational item.</summary>
    public string Size { get; private set; } = null!;

    /// <summary>The volume's criticality; <c>Critical</c> volumes are kept indefinitely.</summary>
    public VolumeCriticality Criticality { get; private set; }

    /// <summary>The author's free-text volume description, or null.</summary>
    public string? Description { get; private set; }

    /// <summary>The runtime provider the volume lives on (kubernetes/docker/manual).</summary>
    public WorkloadProviderId Provider { get; private set; }

    /// <summary>Distinct from <see cref="Provider"/>: a MANUAL instance records the scheme of the artifact it
    /// actually renders, so the recorded name is the REAL one a later restore can adopt.</summary>
    public RetainedVolumeSchemeId VolumeNamingScheme { get; private set; }

    /// <summary>The now-uninstalled plugin instance the volume came from (audit/trace only — no live nav).</summary>
    public Guid SourceGroupInstanceId { get; private set; }

    /// <summary>The now-deleted workload instance the volume came from — the idempotency key part with <see cref="VolumeId"/>.</summary>
    public Guid SourceWorkloadInstanceId { get; private set; }

    /// <summary>When the volume was retained (teardown time). The ageing scan reads this.</summary>
    public DateTimeOffset RetainedAtUtc { get; private set; }

    /// <summary>When a reinstall RE-ATTACHED this retained volume (restore-on-reinstall), or null while still retained.</summary>
    public DateTimeOffset? ReattachedAtUtc { get; private set; }

    /// <summary>When the operator manually PURGED (destructively deleted) this retained volume, or null.</summary>
    public DateTimeOffset? PurgedAtUtc { get; private set; }

    /// <summary>The instance that has CLAIMED this row for a restore-adoption. The claim is atomic so at most ONE
    /// instance adopts it, and is released on any abandon path so an abandoned restore cannot strand the row.</summary>
    public Guid? ClaimedByWorkloadInstanceId { get; private set; }

    /// <summary>Mirrors the repository's atomic conditional UPDATE so the tracked entity stays coherent after a
    /// winning DB claim. Idempotent for the same claimant, refused for a different one.</summary>
    public bool TryClaimForRestore(Guid workloadInstanceId)
    {
        if (workloadInstanceId == Guid.Empty || !IsActive)
        {
            return false;
        }

        if (ClaimedByWorkloadInstanceId is { } holder)
        {
            return holder == workloadInstanceId;
        }

        ClaimedByWorkloadInstanceId = workloadInstanceId;
        return true;
    }

    /// <summary>The abandon path. Only clears a claim held by <paramref name="workloadInstanceId"/>, so one
    /// instance's cleanup can never free another instance's live claim.</summary>
    public bool ReleaseClaimFor(Guid workloadInstanceId)
    {
        if (ClaimedByWorkloadInstanceId == workloadInstanceId && workloadInstanceId != Guid.Empty)
        {
            ClaimedByWorkloadInstanceId = null;
            return true;
        }

        return false;
    }

    /// <summary>True while the volume is still awaiting a decision. Only active records are listed to the operator
    /// or counted by the ageing scan.</summary>
    public bool IsActive => ReattachedAtUtc is null && PurgedAtUtc is null;

    /// <summary>Idempotent: re-marking keeps the first timestamp. NEVER deletes the record — the row is the audit
    /// trail and merely leaves the active view.</summary>
    public void MarkReattached(DateTimeOffset nowUtc)
    {
        ReattachedAtUtc ??= nowUtc;
    }

    /// <summary>An explicit operator action is the ONLY way a retained volume is ever deleted. Idempotent, and the
    /// record row itself stays as the audit trail.</summary>
    public void MarkPurged(DateTimeOffset nowUtc)
    {
        PurgedAtUtc ??= nowUtc;
    }

    private static string NormalizeRequired(string value, string paramName)
        => string.IsNullOrWhiteSpace(value)
            ? throw new ArgumentException("Value is required.", paramName)
            : value.Trim();
}
