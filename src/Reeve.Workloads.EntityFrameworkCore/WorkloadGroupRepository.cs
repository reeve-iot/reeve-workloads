using Microsoft.EntityFrameworkCore;
using Reeve.Workloads.Abstractions;
using Reeve.Workloads.Abstractions.Domain.Entities;
using Reeve.Workloads.EntityFrameworkCore.Persistence;

namespace Reeve.Workloads.EntityFrameworkCore;

/// <summary>EF Core <see cref="IWorkloadGroupRepository"/> over <see cref="WorkloadsDbContext"/>.</summary>
public sealed class WorkloadGroupRepository(WorkloadsDbContext dbContext) : IWorkloadGroupRepository
{
    private readonly WorkloadsDbContext dbContext = dbContext;

    public async Task<WorkloadGroup?> GetAsync(string groupId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(groupId))
        {
            return null;
        }

        var lowered = groupId.Trim().ToLowerInvariant();
        return await dbContext.WorkloadGroups
            .FirstOrDefaultAsync(group => group.GroupId.ToLower() == lowered, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<bool> TryAddAsync(WorkloadGroup group, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(group);
        if (await GetAsync(group.GroupId, cancellationToken).ConfigureAwait(false) is not null)
        {
            return false;
        }

        var entry = await dbContext.WorkloadGroups.AddAsync(group, cancellationToken).ConfigureAwait(false);
        try
        {
            await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (DbUpdateException) when (entry.State == EntityState.Added)
        {
            entry.State = EntityState.Detached;
            if (await GetAsync(group.GroupId, cancellationToken).ConfigureAwait(false) is not null)
            {
                return false;
            }

            throw;
        }
    }

    public async Task UpdateAsync(WorkloadGroup group, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(group);
        dbContext.WorkloadGroups.Update(group);

        try
        {
            await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateConcurrencyException ex) when (DatabaseContention.IsLostOptimisticRace(ex))
        {
            var lost = ex.Entries.Select(entry => entry.Entity).OfType<WorkloadGroup>().FirstOrDefault() ?? group;
            throw new WorkloadConcurrencyConflictException(
                $"Module row {lost.Id} ('{lost.GroupId}') was written by someone else; this update was refused.", ex)
            {
                EntityId = lost.Id,
            };
        }
    }
}
