using Microsoft.EntityFrameworkCore;
using Reeve.Workloads.Abstractions;
using Reeve.Workloads.Abstractions.Domain.Entities;
using Reeve.Workloads.EntityFrameworkCore.Persistence;

namespace Reeve.Workloads.EntityFrameworkCore;

/// <summary>NEVER deletes a record — purge/reattach flip lifecycle timestamps via <see cref="UpdateAsync"/>,
/// leaving a permanent audit row.</summary>
public sealed class RetainedVolumeRepository(WorkloadsDbContext dbContext) : IRetainedVolumeRepository
{
    private readonly WorkloadsDbContext dbContext = dbContext;

    public Task<RetainedVolume?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
        => dbContext.RetainedVolumes.FirstOrDefaultAsync(r => r.Id == id, cancellationToken);

    public Task<RetainedVolume?> FindBySourceAsync(
        Guid sourceWorkloadInstanceId,
        string volumeId,
        CancellationToken cancellationToken = default)
    {
        var normalized = (volumeId ?? string.Empty).Trim();
        return dbContext.RetainedVolumes.FirstOrDefaultAsync(
            r => r.SourceWorkloadInstanceId == sourceWorkloadInstanceId
                 && r.VolumeId == normalized
                 && r.ReattachedAtUtc == null
                 && r.PurgedAtUtc == null,
            cancellationToken);
    }

    public async Task<IReadOnlyList<RetainedVolume>> ListActiveBySourceInstanceAsync(
        Guid sourceWorkloadInstanceId,
        CancellationToken cancellationToken = default)
        => await dbContext.RetainedVolumes
            .Where(r => r.SourceWorkloadInstanceId == sourceWorkloadInstanceId
                        && r.ReattachedAtUtc == null
                        && r.PurgedAtUtc == null)
            .OrderBy(r => r.RetainedAtUtc)
            .ToListAsync(cancellationToken).ConfigureAwait(false);

    public Task<RetainedVolume?> FindActiveByResourceNameAsync(
        string resourceName,
        CancellationToken cancellationToken = default)
    {
        var normalized = (resourceName ?? string.Empty).Trim();
        if (normalized.Length == 0)
        {
            return Task.FromResult<RetainedVolume?>(null);
        }

        return dbContext.RetainedVolumes.FirstOrDefaultAsync(
            r => r.ResourceName == normalized
                 && r.ReattachedAtUtc == null
                 && r.PurgedAtUtc == null,
            cancellationToken);
    }

    public async Task<IReadOnlyList<RetainedVolume>> ListAllAsync(CancellationToken cancellationToken = default)
        => await dbContext.RetainedVolumes
            .OrderBy(r => r.RetainedAtUtc)
            .ToListAsync(cancellationToken).ConfigureAwait(false);

    public async Task<IReadOnlyList<RetainedVolume>> ListActiveAsync(CancellationToken cancellationToken = default)
        => await dbContext.RetainedVolumes
            .Where(r => r.ReattachedAtUtc == null && r.PurgedAtUtc == null)
            .OrderBy(r => r.RetainedAtUtc)
            .ToListAsync(cancellationToken).ConfigureAwait(false);

    public async Task<IReadOnlyList<RetainedVolume>> ListActiveByPluginTypeAsync(
        string pluginTypeId,
        CancellationToken cancellationToken = default)
    {
        var normalized = (pluginTypeId ?? string.Empty).Trim();
        return await dbContext.RetainedVolumes
            .Where(r => r.GroupId == normalized
                        && r.ReattachedAtUtc == null
                        && r.PurgedAtUtc == null)
            .OrderBy(r => r.RetainedAtUtc)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<RetainedVolume>> ListActiveWithoutLiveSourceOlderThanAsync(
        DateTimeOffset retainedOnOrBeforeUtc,
        CancellationToken cancellationToken = default)
        => await dbContext.RetainedVolumes
            .Where(r => r.ReattachedAtUtc == null
                        && r.PurgedAtUtc == null
                        && r.ClaimedByWorkloadInstanceId == null
                        && r.RetainedAtUtc <= retainedOnOrBeforeUtc
                        && !dbContext.WorkloadInstances.Any(i => i.Id == r.SourceWorkloadInstanceId))
            .OrderBy(r => r.RetainedAtUtc)
            .ToListAsync(cancellationToken).ConfigureAwait(false);

    public async Task<bool> TryClaimForRestoreAsync(
        Guid retainedVolumeId,
        Guid claimingWorkloadInstanceId,
        CancellationToken cancellationToken = default)
    {
        if (retainedVolumeId == Guid.Empty || claimingWorkloadInstanceId == Guid.Empty)
        {
            return false;
        }

        var alreadyMine = await dbContext.RetainedVolumes
            .AsNoTracking()
            .AnyAsync(
                r => r.Id == retainedVolumeId
                     && r.ClaimedByWorkloadInstanceId == claimingWorkloadInstanceId
                     && r.ReattachedAtUtc == null
                     && r.PurgedAtUtc == null,
                cancellationToken)
            .ConfigureAwait(false);
        if (alreadyMine)
        {
            return true;
        }

        var affected = await dbContext.RetainedVolumes
            .Where(r => r.Id == retainedVolumeId
                        && r.ClaimedByWorkloadInstanceId == null
                        && r.ReattachedAtUtc == null
                        && r.PurgedAtUtc == null)
            .ExecuteUpdateAsync(
                setters => setters.SetProperty(r => r.ClaimedByWorkloadInstanceId, claimingWorkloadInstanceId),
                cancellationToken)
            .ConfigureAwait(false);

        return affected == 1;
    }

    public async Task<int> ReleaseClaimsForInstanceAsync(
        Guid claimingWorkloadInstanceId,
        CancellationToken cancellationToken = default)
    {
        if (claimingWorkloadInstanceId == Guid.Empty)
        {
            return 0;
        }

        return await dbContext.RetainedVolumes
            .Where(r => r.ClaimedByWorkloadInstanceId == claimingWorkloadInstanceId
                        && r.ReattachedAtUtc == null
                        && r.PurgedAtUtc == null)
            .ExecuteUpdateAsync(
                setters => setters.SetProperty(r => r.ClaimedByWorkloadInstanceId, (Guid?)null),
                cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<bool> ReleaseClaimAsync(
        Guid rowId,
        Guid claimant,
        CancellationToken cancellationToken = default)
    {
        if (rowId == Guid.Empty || claimant == Guid.Empty)
        {
            return false;
        }

        var affected = await dbContext.RetainedVolumes
            .Where(r => r.Id == rowId
                        && r.ClaimedByWorkloadInstanceId == claimant
                        && r.ReattachedAtUtc == null
                        && r.PurgedAtUtc == null)
            .ExecuteUpdateAsync(
                setters => setters.SetProperty(r => r.ClaimedByWorkloadInstanceId, (Guid?)null),
                cancellationToken)
            .ConfigureAwait(false);

        return affected == 1;
    }

    public Task<bool> SourceInstanceExistsAsync(
        Guid sourceWorkloadInstanceId,
        CancellationToken cancellationToken = default)
        => dbContext.WorkloadInstances.AnyAsync(i => i.Id == sourceWorkloadInstanceId, cancellationToken);

    public async Task<IReadOnlyList<RetainedVolume>> ListActiveWithoutLiveSourceAsync(CancellationToken cancellationToken = default)
        => await dbContext.RetainedVolumes
            .Where(r => r.ReattachedAtUtc == null
                        && r.PurgedAtUtc == null
                        && !dbContext.WorkloadInstances.Any(i => i.Id == r.SourceWorkloadInstanceId))
            .OrderBy(r => r.RetainedAtUtc)
            .ToListAsync(cancellationToken).ConfigureAwait(false);

    public async Task<IReadOnlyList<RetainedVolume>> ListOfferableByPluginTypeAsync(
        string pluginTypeId,
        CancellationToken cancellationToken = default)
    {
        var normalized = (pluginTypeId ?? string.Empty).Trim();
        return await dbContext.RetainedVolumes
            .Where(r => r.GroupId == normalized
                        && r.ReattachedAtUtc == null
                        && r.PurgedAtUtc == null
                        && r.ClaimedByWorkloadInstanceId == null
                        && !dbContext.WorkloadInstances.Any(i => i.Id == r.SourceWorkloadInstanceId))
            .OrderBy(r => r.RetainedAtUtc)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<RetainedVolume>> ListActiveByPluginTypeForInstanceAsync(
        string pluginTypeId,
        Guid forWorkloadInstanceId,
        CancellationToken cancellationToken = default)
    {
        var normalized = (pluginTypeId ?? string.Empty).Trim();
        return await dbContext.RetainedVolumes
            .Where(r => r.GroupId == normalized
                        && r.ReattachedAtUtc == null
                        && r.PurgedAtUtc == null
                        && (r.ClaimedByWorkloadInstanceId == null
                            || r.ClaimedByWorkloadInstanceId == forWorkloadInstanceId)
                        && (r.ClaimedByWorkloadInstanceId == forWorkloadInstanceId
                            || !dbContext.WorkloadInstances.Any(i => i.Id == r.SourceWorkloadInstanceId)))
            .OrderBy(r => r.RetainedAtUtc)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<bool> AnyActiveClaimForResourceNameAsync(
        string resourceName,
        Guid? excludingClaimingInstanceId,
        CancellationToken cancellationToken = default)
    {
        var normalized = (resourceName ?? string.Empty).Trim();
        if (normalized.Length == 0)
        {
            return false;
        }

        return await dbContext.RetainedVolumes
            .AnyAsync(
                r => r.ResourceName == normalized
                     && r.ReattachedAtUtc == null
                     && r.PurgedAtUtc == null
                     && r.ClaimedByWorkloadInstanceId != null
                     && r.ClaimedByWorkloadInstanceId != excludingClaimingInstanceId,
                cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<bool> AnyActiveClaimByInstanceAsync(
        Guid claimingWorkloadInstanceId,
        CancellationToken cancellationToken = default)
    {
        if (claimingWorkloadInstanceId == Guid.Empty)
        {
            return false;
        }

        return await dbContext.RetainedVolumes
            .AnyAsync(
                r => r.ClaimedByWorkloadInstanceId == claimingWorkloadInstanceId
                     && r.ReattachedAtUtc == null
                     && r.PurgedAtUtc == null,
                cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task AddAsync(RetainedVolume retainedVolume, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(retainedVolume);
        var entry = await dbContext.RetainedVolumes.AddAsync(retainedVolume, cancellationToken).ConfigureAwait(false);
        try
        {
            await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            entry.State = EntityState.Detached;
            throw;
        }
    }

    public async Task UpdateAsync(RetainedVolume retainedVolume, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(retainedVolume);
        dbContext.RetainedVolumes.Update(retainedVolume);
        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }
}
