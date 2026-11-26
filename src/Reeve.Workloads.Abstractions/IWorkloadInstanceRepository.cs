using Reeve.Workloads.Abstractions.Contracts;
using Reeve.Workloads.Abstractions.Domain.Entities;

namespace Reeve.Workloads.Abstractions;

/// <summary>Persistence port for the workload-instance orchestration plane, kept deliberately thin: lookups,
/// the reconcile/hydrate lists and CRUD. The state machine itself is pure and lives on the entity.</summary>
public interface IWorkloadInstanceRepository
{
    /// <summary>Loads the instance by its canonical id (the <c>instanceId</c> the host's seams are keyed on), or <c>null</c> if absent.</summary>
    Task<WorkloadInstance?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>Loads the instance by its business key, or <c>null</c> if absent.</summary>
    Task<WorkloadInstance?> GetByWorkloadAsync(
        Guid pluginInstanceId,
        string workloadId,
        CancellationToken cancellationToken = default);

    /// <summary>All workload instances for one plugin instance (the plugin's Deployment tab / aggregate health).</summary>
    Task<IReadOnlyList<WorkloadInstance>> ListByPluginInstanceAsync(
        Guid pluginInstanceId,
        CancellationToken cancellationToken = default);

    /// <summary>All instances currently in <paramref name="state"/> (the Repairs sweep / boot hydrate by state).</summary>
    Task<IReadOnlyList<WorkloadInstance>> ListByStateAsync(
        WorkloadInstanceState state,
        CancellationToken cancellationToken = default);

    /// <summary>Every instance — the reconciler's full-scan input on the leader.</summary>
    Task<IReadOnlyList<WorkloadInstance>> ListAllAsync(CancellationToken cancellationToken = default);

    /// <summary>Every instance as the identity fields alone — what the reconciler's fleet pass actually uses.
    /// Same rows and the same set as <see cref="ListAllAsync"/>, in no guaranteed order; the pass imposes its own.</summary>
    /// <remarks>
    /// The fleet pass needs identity fields only. Loading whole entities would deserialize each row's parameters document and retained-volumes snapshot, and change-track it, for a list of ids and a fleet status read.
    /// <para>A default implementation, not a plain member, for the reason
    /// <see cref="Providers.IWorkloadProvider.GetStatusesAsync"/> gives: this port is public and a store outside
    /// this repository may already implement it, so a member they must write would break their build on a MINOR
    /// release. The default lists and projects — correct for everyone, cheaper for nobody — and a store that can
    /// push the projection into its query overrides it. The projection is a plain record, never a query object:
    /// a store that is not a relational ORM has to be able to answer this.</para>
    /// </remarks>
    async Task<IReadOnlyList<WorkloadInstanceRef>> ListRefsAsync(CancellationToken cancellationToken = default)
    {
        var instances = await ListAllAsync(cancellationToken).ConfigureAwait(false);
        return [.. instances.Select(WorkloadInstanceRef.FromInstance)];
    }

    /// <summary>The lifecycle state of one instance and nothing else, or <c>null</c> when there is no such row.</summary>
    /// <remarks>
    /// The health barrier's poll reads exactly one field; loading the entity for it would repeat the typed-document deserialization once per poll interval for as long as an instance settles. <c>null</c> means the ROW is gone, which the barrier answers differently from any state: an
    /// instance that was deleted mid-settle releases its slot at once instead of holding the fleet behind it.
    /// A default implementation for the same reason as <see cref="ListRefsAsync"/>.
    /// </remarks>
    async Task<WorkloadInstanceState?> TryGetStateAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var instance = await GetByIdAsync(id, cancellationToken).ConfigureAwait(false);
        return instance?.State;
    }

    /// <summary>Re-reads one instance and answers the fresh entity, discarding whatever an earlier read in the
    /// same unit of work cached — the field values AND the concurrency token. <c>null</c> when the row is gone.</summary>
    /// <remarks>
    /// <para>The answer MAY be a different object from the instance read earlier, which a store is free to stop
    /// tracking: a caller continues with the answer and drops the old reference.</para>
    /// <para>The counterpart to <see cref="UpdateAsync"/>'s refusal, which re-throws rather than retrying
    /// because the CALLER owns the re-read. Until this existed a caller could not own it: <see
    /// cref="GetByIdAsync"/> and the list methods answer from whatever the unit of work already tracks, so a
    /// re-read after a lost race handed back the same stale token and the retry lost again the same way.</para>
    /// <para>Default-implemented for the reason <see cref="ListRefsAsync"/> gives — a store outside this
    /// repository must keep compiling. The default delegates to <see cref="GetByIdAsync"/>: exactly right for a
    /// store that caches nothing, and no worse than the behaviour that preceded it for one that does.</para>
    /// </remarks>
    Task<WorkloadInstance?> ReloadAsync(Guid id, CancellationToken cancellationToken = default)
        => GetByIdAsync(id, cancellationToken);

    /// <summary>Live per-state counts for the gauge scrape: RAW totals, with Suspended under its own state and
    /// never the masking aggregate. Zero-count states may be omitted.</summary>
    Task<IReadOnlyDictionary<WorkloadInstanceState, int>> CountByStateAsync(CancellationToken cancellationToken = default);

    /// <summary>Adds a new instance and persists it.</summary>
    Task AddAsync(WorkloadInstance instance, CancellationToken cancellationToken = default);

    /// <summary>Persists in-place mutations of a tracked instance (state transitions, spec-hash/provider updates).
    /// A row written by someone else since it was read is refused with <see cref="WorkloadConcurrencyConflictException"/>.</summary>
    Task UpdateAsync(WorkloadInstance instance, CancellationToken cancellationToken = default);

    /// <summary>Sets the restore-binding hold in ONE untracked UPDATE, so the flag that stops a rejected restore
    /// deploying a fresh empty volume persists — and retries — independently of the cosmetic write.</summary>
    Task<bool> TrySetRestoreBindingUnresolvedAsync(
        Guid workloadInstanceId,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken = default);

    /// <summary>Deletes the instance (the terminal step after <see cref="WorkloadInstanceState.Terminating"/>). A row
    /// written by someone else since it was read is refused with <see cref="WorkloadConcurrencyConflictException"/>.</summary>
    Task DeleteAsync(WorkloadInstance instance, CancellationToken cancellationToken = default);
}

/// <summary>One workload instance reduced to the fields that identify and address it — no state, no hashes and
/// none of the JSON documents. What <see cref="IWorkloadInstanceRepository.ListRefsAsync"/> answers with.</summary>
/// <remarks>
/// Positional, and every member a value the store reads straight off a column, because this is a PROJECTION: a
/// relational store is expected to translate the construction into its SELECT list, and a member that needed
/// anything but a column would stop it. <see cref="Inst8"/> is derived from <see cref="Id"/> here, exactly as it
/// is on the entity, rather than being carried.
/// </remarks>
/// <param name="Id">The canonical workload-instance id — the queue's key and the id the host's seams are keyed on.</param>
/// <param name="GroupInstanceId">The owning plugin instance. Part of the pass's stable order.</param>
/// <param name="GroupId">The owning plugin TYPE id — a resource-name segment
/// <see cref="Providers.WorkloadRef"/> requires, so the fleet status read cannot be built without it.</param>
/// <param name="WorkloadId">The manifest workload id — a resource-name segment, and part of the stable order.</param>
/// <param name="Provider">The provider this instance is materialized on, or <c>null</c> when it never deployed
/// and so has no runtime object any provider could be asked about.</param>
public readonly record struct WorkloadInstanceRef(
    Guid Id,
    Guid GroupInstanceId,
    string GroupId,
    string WorkloadId,
    WorkloadProviderId? Provider)
{
    /// <summary>First 8 lowercase-hex chars of <see cref="Id"/> — the resource-name discriminator.</summary>
    public string Inst8 => WorkloadInstance.ComputeInst8(Id);

    /// <summary>The same reduction taken from a loaded entity. What the default
    /// <see cref="IWorkloadInstanceRepository.ListRefsAsync"/> projects with, so the store-side projection and the
    /// fallback cannot disagree about which fields a ref carries.</summary>
    public static WorkloadInstanceRef FromInstance(WorkloadInstance instance)
    {
        ArgumentNullException.ThrowIfNull(instance);
        return new WorkloadInstanceRef(
            instance.Id,
            instance.GroupInstanceId,
            instance.GroupId,
            instance.WorkloadId,
            instance.Provider);
    }
}
