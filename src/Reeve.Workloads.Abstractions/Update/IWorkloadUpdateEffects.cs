using Reeve.Workloads.Abstractions.Domain.Entities;

namespace Reeve.Workloads.Abstractions.Update;

/// <summary>The durable side effects the library's update stages delegate so they stay free of EF details. Each
/// effect must be safe to re-invoke (a no-op on re-commit) so a leader failover mid-stage never corrupts state.</summary>
public interface IWorkloadUpdateEffects
{
    /// <summary>Moves the group's row to <paramref name="targetVersion"/>. It is <c>stage.commit-version</c>'s work,
    /// and every host stage runs after it — resolution refuses one that does not — so a host reloading its plugins
    /// reloads to a version the library already reads as installed. A row already at the target is left alone.
    ///
    /// <para>In an update cohort the commit is a fenced decision: the orchestrator has read the group row, tracked,
    /// before it read the members, and the library's implementation writes that same tracked row through
    /// <c>IWorkloadGroupRepository</c>, so the save carries the concurrency token the decision was taken over and a
    /// lost race throws <see cref="WorkloadConcurrencyConflictException"/>. An implementation that writes the group
    /// row another way is still called once per cohort member that reaches the barrier, but the fence does not hold
    /// for it: two members, or a member and a veto, can then disagree about whether the version moved.</para></summary>
    Task CommitInstalledVersionAsync(string groupId, string targetVersion, CancellationToken cancellationToken = default);

    /// <summary>Commits the candidate's resolved parameters as the new last-known-good. It is
    /// <c>stage.commit-params</c>'s work, the walk's last stage.</summary>
    Task CommitParamsAsync(WorkloadUpdatePlan plan, CancellationToken cancellationToken = default);

    /// <summary>Restores the parameter snapshot recorded on <paramref name="intent"/> as the live last-known-good —
    /// <c>stage.params</c>' and <c>stage.commit-params</c>' way back.</summary>
    Task RestorePreviousParamsAsync(WorkloadUpdateIntent intent, CancellationToken cancellationToken = default);
}
