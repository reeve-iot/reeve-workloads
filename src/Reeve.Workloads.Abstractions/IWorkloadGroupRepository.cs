using Reeve.Workloads.Abstractions.Domain.Entities;

namespace Reeve.Workloads.Abstractions;

/// <summary>Persistence port for the module rows: which version of each module is installed, and from where.</summary>
public interface IWorkloadGroupRepository
{
    /// <summary>The row for a module, matched trimmed and without case, TRACKED so the caller can move it and
    /// <see cref="UpdateAsync"/>. Null when the module has no row: nothing records its version.</summary>
    Task<WorkloadGroup?> GetAsync(string groupId, CancellationToken cancellationToken = default);

    /// <summary>Adds and saves a new row. False, and nothing added, when the module already has one: every replica
    /// adopts at boot, so a second insert is the ordinary loser of that race rather than a fault.</summary>
    Task<bool> TryAddAsync(WorkloadGroup group, CancellationToken cancellationToken = default);

    /// <summary>Saves a moved row. A writer holding a row someone else saved first loses with
    /// <see cref="WorkloadConcurrencyConflictException"/>.</summary>
    Task UpdateAsync(WorkloadGroup group, CancellationToken cancellationToken = default);
}
