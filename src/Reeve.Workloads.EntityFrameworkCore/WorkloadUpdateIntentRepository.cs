using Microsoft.EntityFrameworkCore;
using Reeve.Workloads.Abstractions;
using Reeve.Workloads.Abstractions.Domain.Entities;
using Reeve.Workloads.EntityFrameworkCore.Persistence;

namespace Reeve.Workloads.EntityFrameworkCore;

/// <summary>EF Core <see cref="IWorkloadUpdateIntentRepository"/>: reads that mutate are tracked with Steps
/// eager-loaded, reads that don't use AsNoTracking. "Active" matches the DB's filtered unique index, so at most one active row per instance ever exists.</summary>
public sealed class WorkloadUpdateIntentRepository(WorkloadsDbContext dbContext) : IWorkloadUpdateIntentRepository
{
    private readonly WorkloadsDbContext dbContext = dbContext;

    public bool SupportsCohorts => true;

    public async Task<IReadOnlyList<WorkloadUpdateIntent>> GetCohortAsync(
        Guid cohortId,
        CancellationToken cancellationToken = default)
        => await dbContext.WorkloadUpdateIntents
            .AsNoTracking()
            .Include(intent => intent.Steps)
            .Where(intent => intent.CohortId == cohortId)
            .OrderBy(intent => intent.CohortOrdinal)
            .ToListAsync(cancellationToken).ConfigureAwait(false);

    public async Task AddCohortAsync(
        IReadOnlyList<WorkloadUpdateIntent> intents,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(intents);
        var alreadyAdded = dbContext.ChangeTracker.Entries()
            .Where(entry => entry.State == EntityState.Added)
            .Select(entry => entry.Entity)
            .ToHashSet(ReferenceEqualityComparer.Instance);
        await dbContext.WorkloadUpdateIntents.AddRangeAsync(intents, cancellationToken).ConfigureAwait(false);
        try
        {
            await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            foreach (var entry in dbContext.ChangeTracker.Entries()
                         .Where(entry => entry.State == EntityState.Added && !alreadyAdded.Contains(entry.Entity)).ToList())
            {
                entry.State = EntityState.Detached;
            }

            throw;
        }
    }

    public async Task SaveCohortDecisionAsync(
        WorkloadUpdateIntent intent,
        WorkloadGroup group,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(intent);
        ArgumentNullException.ThrowIfNull(group);

        var groupEntry = dbContext.Entry(group);
        if (groupEntry.State == EntityState.Detached)
        {
            dbContext.WorkloadGroups.Attach(group);
        }

        dbContext.Entry(group).State = EntityState.Modified;
        if (dbContext.Entry(intent).State == EntityState.Detached)
        {
            dbContext.WorkloadUpdateIntents.Update(intent);
        }

        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    private static readonly WorkloadUpdatePhase[] TerminalPhases =
    [
        WorkloadUpdatePhase.Completed,
        WorkloadUpdatePhase.RolledBack,
        WorkloadUpdatePhase.RollbackFailed
    ];

    public Task<WorkloadUpdateIntent?> GetActiveForInstanceAsync(
        Guid pluginInstanceId,
        CancellationToken cancellationToken = default)
        => dbContext.WorkloadUpdateIntents
            .Include(intent => intent.Steps)
            .FirstOrDefaultAsync(
                intent => intent.GroupInstanceId == pluginInstanceId
                          && !TerminalPhases.Contains(intent.Phase),
                cancellationToken);

    public async Task<Guid?> GetActiveIntentIdAsync(
        Guid pluginInstanceId,
        CancellationToken cancellationToken = default)
    {
        var ids = await dbContext.WorkloadUpdateIntents
            .AsNoTracking()
            .Where(intent => intent.GroupInstanceId == pluginInstanceId
                             && !TerminalPhases.Contains(intent.Phase))
            .Select(intent => (Guid?)intent.Id)
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        return ids.Count == 0 ? null : ids[0];
    }

    public async Task<IReadOnlyList<WorkloadUpdateIntent>> GetOpenIntentsAsync(
        CancellationToken cancellationToken = default)
        => await dbContext.WorkloadUpdateIntents
            .Include(intent => intent.Steps)
            .Where(intent => !TerminalPhases.Contains(intent.Phase))
            .OrderBy(intent => intent.CreatedAtUtc)
            .ThenBy(intent => intent.CohortOrdinal)
            .ThenBy(intent => intent.Id)
            .ToListAsync(cancellationToken).ConfigureAwait(false);

    public Task<WorkloadUpdateIntent?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
        => dbContext.WorkloadUpdateIntents
            .Include(intent => intent.Steps)
            .FirstOrDefaultAsync(intent => intent.Id == id, cancellationToken);

    public Task<WorkloadUpdateIntent?> GetLatestForInstanceAsync(
        Guid pluginInstanceId,
        CancellationToken cancellationToken = default)
        => dbContext.WorkloadUpdateIntents
            .AsNoTracking()
            .Where(intent => intent.GroupInstanceId == pluginInstanceId)
            .OrderByDescending(intent => intent.CreatedAtUtc)
            .FirstOrDefaultAsync(cancellationToken);

    public async Task<IReadOnlyList<WorkloadUpdateIntent>> GetRecentForInstanceAsync(
        Guid pluginInstanceId,
        int limit,
        CancellationToken cancellationToken = default)
    {
        if (limit <= 0)
        {
            return Array.Empty<WorkloadUpdateIntent>();
        }

        return await dbContext.WorkloadUpdateIntents
            .AsNoTracking()
            .Include(intent => intent.Steps)
            .Where(intent => intent.GroupInstanceId == pluginInstanceId)
            .OrderByDescending(intent => intent.CreatedAtUtc)
            .Take(limit)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task AddAsync(WorkloadUpdateIntent intent, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(intent);
        await dbContext.WorkloadUpdateIntents.AddAsync(intent, cancellationToken).ConfigureAwait(false);
        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task UpdateAsync(WorkloadUpdateIntent intent, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(intent);

        if (dbContext.Entry(intent).State == EntityState.Detached)
        {
            dbContext.WorkloadUpdateIntents.Update(intent);
        }

        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }
}
