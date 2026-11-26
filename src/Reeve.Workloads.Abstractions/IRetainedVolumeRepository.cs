using Reeve.Workloads.Abstractions.Domain.Entities;

namespace Reeve.Workloads.Abstractions;

/// <summary>Durable persistence seam for <see cref="RetainedVolume"/> records. Rows are never deleted — purge
/// and reattach flip lifecycle timestamps via <see cref="UpdateAsync"/>.</summary>
public interface IRetainedVolumeRepository
{
    /// <summary>The record with <paramref name="id"/>, or null when unknown.</summary>
    Task<RetainedVolume?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>The single active record for <c>(sourceWorkloadInstanceId, volumeId)</c>, or null. Reattached
    /// or purged rows are excluded — they must never block a genuine re-record of the same source.</summary>
    Task<RetainedVolume?> FindBySourceAsync(
        Guid sourceWorkloadInstanceId,
        string volumeId,
        CancellationToken cancellationToken = default);

    /// <summary>Every active record for <paramref name="sourceWorkloadInstanceId"/> — the rows the host's
    /// redeploy must mark reattached. Reattached/purged rows are excluded.</summary>
    Task<IReadOnlyList<RetainedVolume>> ListActiveBySourceInstanceAsync(
        Guid sourceWorkloadInstanceId,
        CancellationToken cancellationToken = default);

    /// <summary>The single active record binding physical <paramref name="resourceName"/> (at most one, by a
    /// unique index). Used to absorb a would-be duplicate active row before its reattach-mark lands.</summary>
    Task<RetainedVolume?> FindActiveByResourceNameAsync(
        string resourceName,
        CancellationToken cancellationToken = default);

    /// <summary>Every record (active + reattached + purged), ordered by <see cref="RetainedVolume.RetainedAtUtc"/>.</summary>
    Task<IReadOnlyList<RetainedVolume>> ListAllAsync(CancellationToken cancellationToken = default);

    /// <summary>Only active records (never reattached or purged), ordered by <see cref="RetainedVolume.RetainedAtUtc"/>.
    /// The predicate is pushed into the DB query since audit rows accumulate unboundedly.</summary>
    Task<IReadOnlyList<RetainedVolume>> ListActiveAsync(CancellationToken cancellationToken = default);

    /// <summary>Active records matching <paramref name="pluginTypeId"/> — the install wizard's "restore previous
    /// data" match query. Both predicates are pushed into the DB query so the covering index is used.</summary>
    Task<IReadOnlyList<RetainedVolume>> ListActiveByPluginTypeAsync(
        string pluginTypeId,
        CancellationToken cancellationToken = default);

    /// <summary>Active, unclaimed rows older than <paramref name="retainedOnOrBeforeUtc"/> whose source instance
    /// is gone — the 90-day purge-scan input. A source whose row still exists counts as alive in any state —
    /// suspended, held by the host or mid-teardown — so its volume is never surfaced.</summary>
    Task<IReadOnlyList<RetainedVolume>> ListActiveWithoutLiveSourceOlderThanAsync(
        DateTimeOffset retainedOnOrBeforeUtc,
        CancellationToken cancellationToken = default);

    /// <summary>Atomically claims an active, unclaimed row for <paramref name="claimingWorkloadInstanceId"/> via
    /// a rowcount-guarded UPDATE — TOCTOU-safe. Re-claiming your own row is a win, not a conflict.</summary>
    Task<bool> TryClaimForRestoreAsync(
        Guid retainedVolumeId,
        Guid claimingWorkloadInstanceId,
        CancellationToken cancellationToken = default);

    /// <summary>Releases every still-active claim held by <paramref name="claimingWorkloadInstanceId"/> (the
    /// abandon/terminal path), so those rows become offerable again. Reattached/purged rows are untouched.</summary>
    Task<int> ReleaseClaimsForInstanceAsync(
        Guid claimingWorkloadInstanceId,
        CancellationToken cancellationToken = default);

    /// <summary>Releases the claim on one row only when still held by <paramref name="claimant"/> — never a
    /// different concurrent claimant's reservation of the same row. Idempotent.</summary>
    Task<bool> ReleaseClaimAsync(
        Guid rowId,
        Guid claimant,
        CancellationToken cancellationToken = default);

    /// <summary>True when the source <see cref="WorkloadInstance"/> row still exists, in any state — such a
    /// row's retained volume must be neither offered nor purged.</summary>
    Task<bool> SourceInstanceExistsAsync(
        Guid sourceWorkloadInstanceId,
        CancellationToken cancellationToken = default);

    /// <summary>Active records whose source instance no longer exists — the "retained data" panel list. A
    /// source whose row still exists is excluded in any state — suspended, held by the host or mid-teardown —
    /// because it can still come back, and its volume must not be purged.</summary>
    Task<IReadOnlyList<RetainedVolume>> ListActiveWithoutLiveSourceAsync(CancellationToken cancellationToken = default);

    /// <summary>Active, unclaimed records of <paramref name="pluginTypeId"/> whose source instance no longer
    /// exists — offerable to the install wizard. Claimed rows are excluded to prevent double-adoption.</summary>
    Task<IReadOnlyList<RetainedVolume>> ListOfferableByPluginTypeAsync(
        string pluginTypeId,
        CancellationToken cancellationToken = default);

    /// <summary>Active records of <paramref name="pluginTypeId"/> that <paramref name="forWorkloadInstanceId"/>
    /// may bind: unclaimed, or already claimed by it. Rows claimed by another instance are excluded.</summary>
    Task<IReadOnlyList<RetainedVolume>> ListActiveByPluginTypeForInstanceAsync(
        string pluginTypeId,
        Guid forWorkloadInstanceId,
        CancellationToken cancellationToken = default);

    /// <summary>True when some active row for physical <paramref name="resourceName"/> is claimed by another
    /// instance — refuses a destructive purge of a volume mid-restore. Guarded by name, not row id.</summary>
    Task<bool> AnyActiveClaimForResourceNameAsync(
        string resourceName,
        Guid? excludingClaimingInstanceId,
        CancellationToken cancellationToken = default);

    /// <summary>True when <paramref name="claimingWorkloadInstanceId"/> holds a claim on some active row — the
    /// reconciler's self-heal backstop when a restore-intent flag write was lost.</summary>
    Task<bool> AnyActiveClaimByInstanceAsync(
        Guid claimingWorkloadInstanceId,
        CancellationToken cancellationToken = default);

    /// <summary>Persists a new record.</summary>
    Task AddAsync(RetainedVolume retainedVolume, CancellationToken cancellationToken = default);

    /// <summary>Persists a lifecycle change (reattach/purge). Never deletes.</summary>
    Task UpdateAsync(RetainedVolume retainedVolume, CancellationToken cancellationToken = default);
}
