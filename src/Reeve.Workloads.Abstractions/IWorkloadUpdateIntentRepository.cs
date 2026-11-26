using Reeve.Workloads.Abstractions.Domain.Entities;

namespace Reeve.Workloads.Abstractions;

/// <summary>Persistence port for the durable update-intent store. A filtered unique index in the DB, not app
/// logic, allows at most one active intent per instance, so racing updates queue behind it.</summary>
public interface IWorkloadUpdateIntentRepository
{
    /// <summary>Whether the cohort repository operations below are implemented.</summary>
    bool SupportsCohorts => false;

    /// <summary>Every member of a cohort, terminal or not, with Steps, untracked and ordered by ordinal.</summary>
    Task<IReadOnlyList<WorkloadUpdateIntent>> GetCohortAsync(Guid cohortId, CancellationToken ct = default)
        => throw new NotSupportedException("This intent store does not support update cohorts.");

    /// <summary>Adds every intent in one unit of work: all or none.</summary>
    Task AddCohortAsync(IReadOnlyList<WorkloadUpdateIntent> intents, CancellationToken ct = default)
        => throw new NotSupportedException("This intent store does not support update cohorts.");

    /// <summary>Saves a cohort member and its group row in one fenced unit of work. A lost race throws
    /// <c>DbUpdateConcurrencyException</c>, as <see cref="UpdateAsync"/> does, naming the row that lost; on Npgsql EF 11
    /// so does a serialization failure or deadlock, as <see cref="UpdateAsync"/> says.</summary>
    Task SaveCohortDecisionAsync(WorkloadUpdateIntent intent, WorkloadGroup group, CancellationToken ct = default)
        => throw new NotSupportedException("This intent store does not support update cohorts.");

    /// <summary>The single active intent, with its Steps. Returns a TRACKED entity so the caller can mutate
    /// and <see cref="UpdateAsync"/> in the same unit of work.</summary>
    Task<WorkloadUpdateIntent?> GetActiveForInstanceAsync(
        Guid pluginInstanceId,
        CancellationToken cancellationToken = default);

    /// <summary>Id-only lookup of the active intent, for the day-2 mutation guard's 409. No entity
    /// materialization and no Steps load.</summary>
    Task<Guid?> GetActiveIntentIdAsync(
        Guid pluginInstanceId,
        CancellationToken cancellationToken = default);

    /// <summary>Every non-terminal intent, with Steps and TRACKED: the leader resumes these BEFORE its first
    /// drift pass.</summary>
    Task<IReadOnlyList<WorkloadUpdateIntent>> GetOpenIntentsAsync(CancellationToken cancellationToken = default);

    /// <summary>Loads an intent by id, including its Steps.</summary>
    Task<WorkloadUpdateIntent?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>The most recent intent, active OR TERMINAL, for the Repairs scanner: a rollback settles the
    /// intent before the scanner reads it, and latest-wins is what clears a superseded parked issue.</summary>
    Task<WorkloadUpdateIntent?> GetLatestForInstanceAsync(
        Guid pluginInstanceId,
        CancellationToken cancellationToken = default);

    /// <summary>The most recent intents, newest first, with Steps and untracked. A non-positive
    /// <paramref name="limit"/> yields an empty list.</summary>
    Task<IReadOnlyList<WorkloadUpdateIntent>> GetRecentForInstanceAsync(
        Guid pluginInstanceId,
        int limit,
        CancellationToken cancellationToken = default);

    /// <summary>Adds a new intent with its staged Steps. A second ACTIVE intent for the same instance violates
    /// the filtered unique index and THROWS — the intended concurrency behaviour, mapped to a 409.</summary>
    Task AddAsync(WorkloadUpdateIntent intent, CancellationToken cancellationToken = default);

    /// <summary>Persists in-place mutations of a tracked intent. The Version concurrency token guards two
    /// leaders racing the same row; the losing write throws <c>DbUpdateConcurrencyException</c>. Npgsql EF 11 throws
    /// that type for a serialization failure or deadlock too (SQLSTATE 40001 or 40P01 in its inner exception),
    /// which is not a lost race.</summary>
    Task UpdateAsync(WorkloadUpdateIntent intent, CancellationToken cancellationToken = default);
}
