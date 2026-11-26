using Reeve.Workloads.Abstractions;
using Reeve.Workloads.Abstractions.Domain.Entities;

namespace Reeve.Workloads.Core;

/// <summary>Records retained volumes at teardown, idempotently per <c>(sourceWorkloadInstanceId, volumeId)</c>,
/// and flips the reattach/purge timestamps. NEVER auto-deletes a record: purge is an explicit operator action.</summary>
public sealed class RetainedVolumeService : IRetainedVolumeService
{
    private readonly IRetainedVolumeRepository repository;
    private readonly IRetainedVolumeDeleter physicalDeleter;

    /// <param name="repository">The durable retained-volume store.</param>
    /// <param name="physicalDeleter">Frees the storage on purge; a row is marked purged only when it answers <c>true</c>.</param>
    public RetainedVolumeService(IRetainedVolumeRepository repository, IRetainedVolumeDeleter physicalDeleter)
    {
        this.repository = repository;
        this.physicalDeleter = physicalDeleter;
    }

    public async Task<int> RecordRetainedAsync(
        WorkloadInstance instance,
        IReadOnlyList<RetainedVolumeInput> volumes,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(instance);
        ArgumentNullException.ThrowIfNull(volumes);

        if (volumes.Count > 0 && instance.Provider is null)
        {
            throw new InvalidOperationException(
                $"Workload instance {instance.Id} has volumes to retain but was never bound to a provider, so no "
                + "retained-volume row can say what created them.");
        }

        var written = 0;
        foreach (var volume in volumes)
        {
            var existing = await repository
                .FindBySourceAsync(instance.Id, volume.VolumeId, cancellationToken)
                .ConfigureAwait(false);
            if (existing is not null)
            {
                continue;
            }

            var sameName = await repository
                .FindActiveByResourceNameAsync(volume.ResourceName, cancellationToken)
                .ConfigureAwait(false);
            if (sameName is not null)
            {
                continue;
            }

            var record = RetainedVolume.Record(
                Guid.NewGuid(),
                instance.GroupId,
                instance.WorkloadId,
                volume.VolumeId,
                volume.ResourceName,
                volume.Size,
                volume.Criticality,
                volume.Description,
                instance.Provider!.Value,
                volume.VolumeNamingScheme,
                instance.GroupInstanceId,
                instance.Id,
                nowUtc);

            await repository.AddAsync(record, cancellationToken).ConfigureAwait(false);
            written++;
        }

        return written;
    }

    public Task<IReadOnlyList<RetainedVolume>> ListAsync(CancellationToken cancellationToken = default)
        => repository.ListAllAsync(cancellationToken);

    public Task<IReadOnlyList<RetainedVolume>> ListActiveAsync(CancellationToken cancellationToken = default)
        => repository.ListActiveWithoutLiveSourceAsync(cancellationToken);

    public Task<IReadOnlyList<RetainedVolume>> ListActiveByPluginTypeAsync(
        string pluginTypeId,
        CancellationToken cancellationToken = default)
    {
        var normalized = (pluginTypeId ?? string.Empty).Trim();
        if (normalized.Length == 0)
        {
            return Task.FromResult<IReadOnlyList<RetainedVolume>>(Array.Empty<RetainedVolume>());
        }

        return repository.ListActiveByPluginTypeAsync(normalized, cancellationToken);
    }

    public Task<IReadOnlyList<RetainedVolume>> ListOfferableByPluginTypeAsync(
        string pluginTypeId,
        CancellationToken cancellationToken = default)
    {
        var normalized = (pluginTypeId ?? string.Empty).Trim();
        if (normalized.Length == 0)
        {
            return Task.FromResult<IReadOnlyList<RetainedVolume>>(Array.Empty<RetainedVolume>());
        }

        return repository.ListOfferableByPluginTypeAsync(normalized, cancellationToken);
    }

    public Task<IReadOnlyList<RetainedVolume>> ListActiveByPluginTypeForInstanceAsync(
        string pluginTypeId,
        Guid forWorkloadInstanceId,
        CancellationToken cancellationToken = default)
    {
        var normalized = (pluginTypeId ?? string.Empty).Trim();
        if (normalized.Length == 0)
        {
            return Task.FromResult<IReadOnlyList<RetainedVolume>>(Array.Empty<RetainedVolume>());
        }

        return repository.ListActiveByPluginTypeForInstanceAsync(normalized, forWorkloadInstanceId, cancellationToken);
    }

    public Task<bool> TryClaimForRestoreAsync(
        Guid retainedVolumeId,
        Guid claimingWorkloadInstanceId,
        CancellationToken cancellationToken = default)
        => repository.TryClaimForRestoreAsync(retainedVolumeId, claimingWorkloadInstanceId, cancellationToken);

    public Task<int> ReleaseClaimsForInstanceAsync(
        Guid claimingWorkloadInstanceId,
        CancellationToken cancellationToken = default)
        => repository.ReleaseClaimsForInstanceAsync(claimingWorkloadInstanceId, cancellationToken);

    public Task<bool> AnyActiveClaimForResourceNameAsync(
        string resourceName,
        Guid? excludingClaimingInstanceId,
        CancellationToken cancellationToken = default)
    {
        var normalized = (resourceName ?? string.Empty).Trim();
        if (normalized.Length == 0)
        {
            return Task.FromResult(false);
        }

        return repository.AnyActiveClaimForResourceNameAsync(normalized, excludingClaimingInstanceId, cancellationToken);
    }

    public Task<bool> AnyActiveClaimByInstanceAsync(
        Guid claimingWorkloadInstanceId,
        CancellationToken cancellationToken = default)
        => repository.AnyActiveClaimByInstanceAsync(claimingWorkloadInstanceId, cancellationToken);

    public Task<IReadOnlyList<RetainedVolume>> ListActiveBySourceInstanceAsync(
        Guid sourceWorkloadInstanceId,
        CancellationToken cancellationToken = default)
        => repository.ListActiveBySourceInstanceAsync(sourceWorkloadInstanceId, cancellationToken);

    public Task<IReadOnlyList<RetainedVolume>> ListOlderThanAsync(
        TimeSpan age,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken = default)
        => repository.ListActiveWithoutLiveSourceOlderThanAsync(nowUtc - age, cancellationToken);

    public async Task MarkReattachedAsync(Guid id, DateTimeOffset nowUtc, CancellationToken cancellationToken = default)
    {
        var record = await repository.GetByIdAsync(id, cancellationToken).ConfigureAwait(false);
        if (record is null || !record.IsActive)
        {
            return;
        }

        record.MarkReattached(nowUtc);
        await repository.UpdateAsync(record, cancellationToken).ConfigureAwait(false);
    }

    public async Task<bool> TryMarkReattachedAsync(Guid id, DateTimeOffset nowUtc, CancellationToken cancellationToken = default)
    {
        var record = await repository.GetByIdAsync(id, cancellationToken).ConfigureAwait(false);
        if (record is null || !record.IsActive)
        {
            return false;
        }

        record.MarkReattached(nowUtc);
        await repository.UpdateAsync(record, cancellationToken).ConfigureAwait(false);
        return true;
    }

    public async Task<int> MarkReattachedForInstanceAsync(
        Guid sourceWorkloadInstanceId,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken = default)
    {
        var active = await repository
            .ListActiveBySourceInstanceAsync(sourceWorkloadInstanceId, cancellationToken)
            .ConfigureAwait(false);

        var reattached = 0;
        foreach (var record in active)
        {
            if (!record.IsActive)
            {
                continue;
            }

            record.MarkReattached(nowUtc);
            await repository.UpdateAsync(record, cancellationToken).ConfigureAwait(false);
            reattached++;
        }

        return reattached;
    }

    public async Task PurgeAsync(Guid id, DateTimeOffset nowUtc, CancellationToken cancellationToken = default)
    {
        var record = await repository.GetByIdAsync(id, cancellationToken).ConfigureAwait(false);
        if (record is null || !record.IsActive)
        {
            return;
        }

        if (await repository.AnyActiveClaimForResourceNameAsync(record.ResourceName, excludingClaimingInstanceId: null, cancellationToken).ConfigureAwait(false))
        {
            throw new RetainedVolumeClaimedException(
                $"Retained volume '{record.ResourceName}' cannot be purged: it is currently claimed by an in-flight " +
                "restore of another instance. Complete or abandon that restore first.");
        }

        record.MarkPurged(nowUtc);
        await repository.UpdateAsync(record, cancellationToken).ConfigureAwait(false);
    }

    private static readonly Guid PurgeReservationSentinel = new("ffffffff-ffff-ffff-ffff-ffffffffffff");

    public async Task<RetainedVolumePurgeResult> TryPurgeAsync(Guid id, DateTimeOffset nowUtc, CancellationToken cancellationToken = default)
    {
        var record = await repository.GetByIdAsync(id, cancellationToken).ConfigureAwait(false);
        if (record is null || !record.IsActive)
        {
            return RetainedVolumePurgeResult.NotFound();
        }

        if (await repository.SourceInstanceExistsAsync(record.SourceWorkloadInstanceId, cancellationToken).ConfigureAwait(false))
        {
            return RetainedVolumePurgeResult.Claimed();
        }

        var reserved = await repository
            .TryClaimForRestoreAsync(record.Id, PurgeReservationSentinel, cancellationToken)
            .ConfigureAwait(false);
        if (!reserved)
        {
            return RetainedVolumePurgeResult.Claimed();
        }

        if (await repository
                .AnyActiveClaimForResourceNameAsync(record.ResourceName, excludingClaimingInstanceId: PurgeReservationSentinel, cancellationToken)
                .ConfigureAwait(false))
        {
            await repository.ReleaseClaimAsync(record.Id, PurgeReservationSentinel, cancellationToken).ConfigureAwait(false);
            return RetainedVolumePurgeResult.Claimed();
        }

        var freed = await physicalDeleter.TryDeletePhysicalVolumeAsync(record, cancellationToken).ConfigureAwait(false);
        if (!freed)
        {
            await repository.ReleaseClaimAsync(record.Id, PurgeReservationSentinel, cancellationToken).ConfigureAwait(false);
            return RetainedVolumePurgeResult.DeleteFailed();
        }

        var reservedRecord = await repository.GetByIdAsync(id, cancellationToken).ConfigureAwait(false) ?? record;
        reservedRecord.MarkPurged(nowUtc);
        await repository.UpdateAsync(reservedRecord, cancellationToken).ConfigureAwait(false);
        return RetainedVolumePurgeResult.Purged(nowUtc);
    }

    public async Task<int> MarkPurgedForInstanceAsync(
        Guid sourceWorkloadInstanceId,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken = default)
    {
        var active = await repository
            .ListActiveBySourceInstanceAsync(sourceWorkloadInstanceId, cancellationToken)
            .ConfigureAwait(false);

        var purged = 0;
        foreach (var record in active)
        {
            if (!record.IsActive)
            {
                continue;
            }

            if (await repository
                    .AnyActiveClaimForResourceNameAsync(record.ResourceName, excludingClaimingInstanceId: sourceWorkloadInstanceId, cancellationToken)
                    .ConfigureAwait(false))
            {
                continue;
            }

            record.MarkPurged(nowUtc);
            await repository.UpdateAsync(record, cancellationToken).ConfigureAwait(false);
            purged++;
        }

        return purged;
    }
}

/// <summary>Thrown when a destructive purge is refused because the physical volume is CLAIMED by another
/// instance's in-flight restore-adoption. The caller maps this to a 409 Conflict.</summary>
public sealed class RetainedVolumeClaimedException(string message) : InvalidOperationException(message);
