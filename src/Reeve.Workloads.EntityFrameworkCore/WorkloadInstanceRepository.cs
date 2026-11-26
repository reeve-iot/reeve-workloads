using Microsoft.EntityFrameworkCore;
using Reeve.Workloads.Abstractions;
using Reeve.Workloads.Abstractions.Domain.Entities;
using Reeve.Workloads.EntityFrameworkCore.Persistence;

namespace Reeve.Workloads.EntityFrameworkCore;

/// <summary>EF Core implementation over <see cref="WorkloadsDbContext"/> and the single persistence choke point
/// every state transition flows through, so the realtime status notifier fires here on a REAL state change.</summary>
public sealed class WorkloadInstanceRepository(
    WorkloadsDbContext dbContext,
    IWorkloadStatusRealtimeNotifier statusNotifier) : IWorkloadInstanceRepository
{
    private readonly WorkloadsDbContext dbContext = dbContext;
    private readonly IWorkloadStatusRealtimeNotifier statusNotifier = statusNotifier;

    public Task<WorkloadInstance?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
        => dbContext.WorkloadInstances.FirstOrDefaultAsync(instance => instance.Id == id, cancellationToken);

    public Task<WorkloadInstance?> GetByWorkloadAsync(
        Guid pluginInstanceId,
        string workloadId,
        CancellationToken cancellationToken = default)
    {
        var normalized = (workloadId ?? string.Empty).Trim();
        return dbContext.WorkloadInstances.FirstOrDefaultAsync(
            instance => instance.GroupInstanceId == pluginInstanceId && instance.WorkloadId == normalized,
            cancellationToken);
    }

    public async Task<IReadOnlyList<WorkloadInstance>> ListByPluginInstanceAsync(
        Guid pluginInstanceId,
        CancellationToken cancellationToken = default)
        => await dbContext.WorkloadInstances
            .Where(instance => instance.GroupInstanceId == pluginInstanceId)
            .OrderBy(instance => instance.WorkloadId)
            .ToListAsync(cancellationToken).ConfigureAwait(false);

    public async Task<IReadOnlyList<WorkloadInstance>> ListByStateAsync(
        WorkloadInstanceState state,
        CancellationToken cancellationToken = default)
        => await dbContext.WorkloadInstances
            .Where(instance => instance.State == state)
            .ToListAsync(cancellationToken).ConfigureAwait(false);

    public async Task<IReadOnlyList<WorkloadInstance>> ListAllAsync(CancellationToken cancellationToken = default)
        => await dbContext.WorkloadInstances.ToListAsync(cancellationToken).ConfigureAwait(false);

    /// <summary>The fleet pass's list, pushed into the SELECT: five columns, no JSON documents and nothing
    /// in the change tracker. The interface's default would list whole entities and throw the rest away.</summary>
    /// <remarks><c>AsNoTracking</c> is stated rather than relied on. A projection to a non-entity type is
    /// already untracked, but the reason nothing here may be tracked is that NOTHING mutates a ref — the pass
    /// carries ids out of the scope and each worker re-reads its own row, tracked, in a scope of its own.</remarks>
    public async Task<IReadOnlyList<WorkloadInstanceRef>> ListRefsAsync(CancellationToken cancellationToken = default)
        => await dbContext.WorkloadInstances
            .AsNoTracking()
            .Select(instance => new WorkloadInstanceRef(
                instance.Id,
                instance.GroupInstanceId,
                instance.GroupId,
                instance.WorkloadId,
                instance.Provider))
            .ToListAsync(cancellationToken).ConfigureAwait(false);

    /// <summary>One column for the health barrier's poll. <c>null</c> is the row being GONE — never a state
    /// — because the barrier releases a deleted instance's slot instead of waiting out its timeout.</summary>
    /// <remarks>A scalar projection, so there is no entity to track; <c>AsNoTracking</c> says so, and says that
    /// the barrier is a pure read. It must never become a read someone saves: the tracked, mutable copy of this
    /// row is the one the reconcile path loads through <see cref="GetByIdAsync"/>.</remarks>
    public Task<WorkloadInstanceState?> TryGetStateAsync(Guid id, CancellationToken cancellationToken = default)
        => dbContext.WorkloadInstances
            .AsNoTracking()
            .Where(instance => instance.Id == id)
            .Select(instance => (WorkloadInstanceState?)instance.State)
            .FirstOrDefaultAsync(cancellationToken);

    public async Task<IReadOnlyDictionary<WorkloadInstanceState, int>> CountByStateAsync(
        CancellationToken cancellationToken = default)
    {
        var grouped = await dbContext.WorkloadInstances
            .GroupBy(instance => instance.State)
            .Select(group => new { State = group.Key, Count = group.Count() })
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        return grouped.ToDictionary(entry => entry.State, entry => entry.Count);
    }

    public async Task AddAsync(WorkloadInstance instance, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(instance);
        await dbContext.WorkloadInstances.AddAsync(instance, cancellationToken).ConfigureAwait(false);
        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task UpdateAsync(WorkloadInstance instance, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(instance);

        var entry = dbContext.Entry(instance);
        if (entry.State == EntityState.Detached)
        {
            entry = dbContext.WorkloadInstances.Update(instance);
        }
        else
        {
            entry.DetectChanges();
        }

        var stateChanged = !Equals(
            entry.Property(e => e.State).OriginalValue,
            entry.Property(e => e.State).CurrentValue);
        var messageChanged = !string.Equals(
            entry.Property(e => e.StateMessage).OriginalValue,
            entry.Property(e => e.StateMessage).CurrentValue,
            StringComparison.Ordinal);
        var reasonChanged = !Equals(
            entry.Property(e => e.StateReason).OriginalValue,
            entry.Property(e => e.StateReason).CurrentValue);

        await SaveRefusingLostRaceAsync(instance, "update", cancellationToken).ConfigureAwait(false);

        if (stateChanged || messageChanged || reasonChanged)
        {
            await statusNotifier.NotifyStatusChangedAsync(instance, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task SaveRefusingLostRaceAsync(WorkloadInstance instance, string write, CancellationToken cancellationToken)
    {
        try
        {
            await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateConcurrencyException ex) when (DatabaseContention.IsLostOptimisticRace(ex))
        {
            var lost = ex.Entries.Select(entry => entry.Entity).OfType<WorkloadInstance>().FirstOrDefault() ?? instance;
            throw new WorkloadConcurrencyConflictException(
                $"Workload instance {lost.Id} was written by someone else; this {write} was refused.", ex)
            {
                EntityId = lost.Id,
            };
        }
    }

    public async Task<WorkloadInstance?> ReloadAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var tracked = dbContext.ChangeTracker.Entries<WorkloadInstance>()
            .Select(entry => entry.Entity)
            .FirstOrDefault(instance => instance.Id == id);
        if (tracked is null)
        {
            return await GetByIdAsync(id, cancellationToken).ConfigureAwait(false);
        }

        dbContext.Entry(tracked).State = EntityState.Detached;
        return await GetByIdAsync(id, cancellationToken).ConfigureAwait(false);
    }

    public async Task<bool> TrySetRestoreBindingUnresolvedAsync(
        Guid workloadInstanceId,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken = default)
    {
        if (workloadInstanceId == Guid.Empty)
        {
            return false;
        }

        var affected = await dbContext.WorkloadInstances
            .Where(instance => instance.Id == workloadInstanceId)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(instance => instance.RestoreBindingUnresolved, true)
                    .SetProperty(instance => instance.UpdatedAtUtc, nowUtc)
                    .SetProperty(instance => instance.Version, instance => instance.Version + 1),
                cancellationToken)
            .ConfigureAwait(false);

        return affected > 0;
    }

    public async Task DeleteAsync(WorkloadInstance instance, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(instance);
        dbContext.WorkloadInstances.Remove(instance);
        await SaveRefusingLostRaceAsync(instance, "delete", cancellationToken).ConfigureAwait(false);

        await statusNotifier.NotifyStatusChangedAsync(instance, cancellationToken).ConfigureAwait(false);
    }
}
