using Reeve.Workloads.Abstractions.Contracts;
using Reeve.Workloads.Abstractions.Contracts.Deployment;
using Reeve.Workloads.Abstractions.Domain.Entities;

namespace Reeve.Workloads.Abstractions;

/// <summary>Durable retained-volume store: on a <see cref="WorkloadRetention.RetainAll"/> teardown, one row is
/// written per volume before the instance row is deleted. Never auto-deleted — only manual purge or restore-on-reinstall removes it from the active view.</summary>
public interface IRetainedVolumeService
{
    /// <summary>Records the volumes retained by a <see cref="WorkloadRetention.RetainAll"/> teardown, one row
    /// per <paramref name="volumes"/>, idempotent per (source, volumeId). Returns the count of new rows.</summary>
    Task<int> RecordRetainedAsync(
        WorkloadInstance instance,
        IReadOnlyList<RetainedVolumeInput> volumes,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken = default);

    /// <summary>Every retained-volume record (active + reattached + purged)  — the full history view.</summary>
    Task<IReadOnlyList<RetainedVolume>> ListAsync(CancellationToken cancellationToken = default);

    /// <summary>Only ACTIVE records (neither reattached nor purged) — the "still retained" list + reinstall-restore source.</summary>
    Task<IReadOnlyList<RetainedVolume>> ListActiveAsync(CancellationToken cancellationToken = default);

    /// <summary>Active retained-volume records left by a previous instance of <paramref name="pluginTypeId"/> —
    /// the install wizard's "restore previous data" step. Reattached/purged rows are excluded.</summary>
    Task<IReadOnlyList<RetainedVolume>> ListActiveByPluginTypeAsync(
        string pluginTypeId,
        CancellationToken cancellationToken = default);

    /// <summary>The install wizard's offer query: active, unclaimed records of <paramref name="pluginTypeId"/>.
    /// Excludes claimed rows so the wizard never re-offers one an in-flight restore already bound.</summary>
    Task<IReadOnlyList<RetainedVolume>> ListOfferableByPluginTypeAsync(
        string pluginTypeId,
        CancellationToken cancellationToken = default);

    /// <summary>Restore-validation query for one install: active records of <paramref name="pluginTypeId"/> that
    /// are unclaimed or already claimed by <paramref name="forWorkloadInstanceId"/>. Another instance's claim is excluded.</summary>
    Task<IReadOnlyList<RetainedVolume>> ListActiveByPluginTypeForInstanceAsync(
        string pluginTypeId,
        Guid forWorkloadInstanceId,
        CancellationToken cancellationToken = default);

    /// <summary>Atomically claims an active, unclaimed row for <paramref name="claimingWorkloadInstanceId"/> —
    /// TOCTOU-safe; only one concurrent claimant wins. Re-claiming your own row is an idempotent win.</summary>
    Task<bool> TryClaimForRestoreAsync(
        Guid retainedVolumeId,
        Guid claimingWorkloadInstanceId,
        CancellationToken cancellationToken = default);

    /// <summary>Releases every still-active claim held by <paramref name="claimingWorkloadInstanceId"/> — the
    /// abandon/terminal path — so those rows become offerable again. Idempotent no-op when none are held.</summary>
    Task<int> ReleaseClaimsForInstanceAsync(
        Guid claimingWorkloadInstanceId,
        CancellationToken cancellationToken = default);

    /// <summary>True when some active row for physical <paramref name="resourceName"/> is claimed by another
    /// instance — used to refuse a destructive purge of a volume mid-restore. Guarded by name, not row id.</summary>
    Task<bool> AnyActiveClaimForResourceNameAsync(
        string resourceName,
        Guid? excludingClaimingInstanceId,
        CancellationToken cancellationToken = default);

    /// <summary>True when <paramref name="claimingWorkloadInstanceId"/> holds a claim on some active row. The
    /// reconciler's self-heal backstop when a restore-intent flag write was lost.</summary>
    Task<bool> AnyActiveClaimByInstanceAsync(
        Guid claimingWorkloadInstanceId,
        CancellationToken cancellationToken = default);

    /// <summary>Active rows recorded by <paramref name="sourceWorkloadInstanceId"/> itself — survives a PurgeAll
    /// teardown, so the purge-vs-claim guard can still resolve real physical names once the per-instance snapshot is nulled.</summary>
    Task<IReadOnlyList<RetainedVolume>> ListActiveBySourceInstanceAsync(
        Guid sourceWorkloadInstanceId,
        CancellationToken cancellationToken = default);

    /// <summary>Active records retained more than <paramref name="age"/> ago — the 90-day informational scan
    /// input. Reattached/purged records are excluded.</summary>
    Task<IReadOnlyList<RetainedVolume>> ListOlderThanAsync(
        TimeSpan age,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken = default);

    /// <summary>Marks a record reattached by a reinstall. Idempotent (null/unknown id is a no-op); never
    /// deletes the record.</summary>
    Task MarkReattachedAsync(Guid id, DateTimeOffset nowUtc, CancellationToken cancellationToken = default);

    /// <summary>The panel's reattach/dismiss action: marks one active row reattached and reports whether it was
    /// found active, so the endpoint can answer 404 for an unknown or already-handled id.</summary>
    Task<bool> TryMarkReattachedAsync(Guid id, DateTimeOffset nowUtc, CancellationToken cancellationToken = default);

    /// <summary>Physically frees the volume's storage and marks the row purged — the only way a retained volume
    /// leaves retention. Fail-closed: a claimed row is refused, and a failed delete leaves the row active for a retry, never marked purged over live data.</summary>
    Task<RetainedVolumePurgeResult> TryPurgeAsync(Guid id, DateTimeOffset nowUtc, CancellationToken cancellationToken = default);

    /// <summary>Marks every still-active row of <paramref name="sourceWorkloadInstanceId"/> reattached — the
    /// host's redeploy path. Otherwise the 90-day scan would alert on, and a purge could target, a live volume.</summary>
    Task<int> MarkReattachedForInstanceAsync(
        Guid sourceWorkloadInstanceId,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken = default);

    /// <summary>Marks a record purged by an explicit operator action — the only way a retained volume ever
    /// leaves retention. Idempotent (null/unknown id is a no-op); never auto-deletes.</summary>
    Task PurgeAsync(Guid id, DateTimeOffset nowUtc, CancellationToken cancellationToken = default);

    /// <summary>Marks every still-active row of <paramref name="sourceWorkloadInstanceId"/> purged — the
    /// PurgeAll-uninstall bookkeeping path, so rows don't linger as phantom entries for already-deleted volumes.</summary>
    Task<int> MarkPurgedForInstanceAsync(
        Guid sourceWorkloadInstanceId,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken = default);
}

/// <summary>The outcome kind of a panel purge attempt: <see cref="Purged"/> is 200, <see cref="NotFound"/> is
/// 404, <see cref="Claimed"/> and <see cref="DeleteFailed"/> are 409.</summary>
public enum RetainedVolumePurgeOutcome
{
    /// <summary>The physical storage was freed and the durable row marked purged (left the active view).</summary>
    Purged = 0,

    /// <summary>No active row exists for the id (unknown, or already purged/reattached) — nothing was deleted.</summary>
    NotFound = 1,

    /// <summary>The row's physical volume is claimed by another instance's in-flight restore — refused; nothing deleted.</summary>
    Claimed = 2,

    /// <summary>The physical volume could not be freed (provider unreachable) — the row is LEFT ACTIVE for a retry.</summary>
    DeleteFailed = 3,
}

/// <summary>Typed result of <see cref="IRetainedVolumeService.TryPurgeAsync"/>: carries <see cref="Kind"/> and,
/// on a successful purge, the <see cref="PurgedAtUtc"/> stamp.</summary>
public sealed record RetainedVolumePurgeResult
{
    public required RetainedVolumePurgeOutcome Kind { get; init; }

    /// <summary>The purge timestamp — set only on <see cref="RetainedVolumePurgeOutcome.Purged"/>.</summary>
    public DateTimeOffset? PurgedAtUtc { get; init; }

    public static RetainedVolumePurgeResult Purged(DateTimeOffset purgedAtUtc)
        => new() { Kind = RetainedVolumePurgeOutcome.Purged, PurgedAtUtc = purgedAtUtc };

    public static RetainedVolumePurgeResult NotFound() => new() { Kind = RetainedVolumePurgeOutcome.NotFound };

    public static RetainedVolumePurgeResult Claimed() => new() { Kind = RetainedVolumePurgeOutcome.Claimed };

    public static RetainedVolumePurgeResult DeleteFailed() => new() { Kind = RetainedVolumePurgeOutcome.DeleteFailed };
}

/// <summary>Volume metadata the teardown hook hands to <see cref="IRetainedVolumeService.RecordRetainedAsync"/>,
/// one per retained volume — provider-agnostic so the service doesn't depend on the manifest vs desired-spec shape.</summary>
public sealed record RetainedVolumeInput
{
    /// <summary>The manifest volume id (e.g. <c>state</c>).</summary>
    public required string VolumeId { get; init; }

    /// <summary>The provider PVC / named-volume name the data physically lives under.</summary>
    public required string ResourceName { get; init; }

    /// <summary>The naming scheme <see cref="ResourceName"/> follows, recorded so a later restore resolves the
    /// real physical name. Defaults to <see cref="RetainedVolumeSchemeId.Kubernetes"/>.</summary>
    public RetainedVolumeSchemeId VolumeNamingScheme { get; init; } = RetainedVolumeSchemeId.Kubernetes;

    /// <summary>Requested/allocated size, e.g. <c>"512Mi"</c>.</summary>
    public required string Size { get; init; }

    /// <summary>The volume's criticality.</summary>
    public required VolumeCriticality Criticality { get; init; }

    /// <summary>The author's free-text volume description, or null.</summary>
    public string? Description { get; init; }
}
