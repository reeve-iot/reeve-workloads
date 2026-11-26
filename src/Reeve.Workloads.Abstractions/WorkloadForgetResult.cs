using Reeve.Workloads.Abstractions.Contracts;
using Reeve.Workloads.Abstractions.Domain.Entities;

namespace Reeve.Workloads.Abstractions;

/// <summary>What <see cref="IWorkloadLifecycleService.ForgetAsync"/> did.</summary>
public enum WorkloadForgetOutcome
{
    /// <summary>The row is gone, and <see cref="WorkloadForgetResult.LeftBehind"/> says what may remain.</summary>
    Forgotten = 0,

    /// <summary>No row has this id: never one, already forgotten, or its teardown finished. Nothing was done.</summary>
    NotFound = 1,

    /// <summary>The row is not <see cref="WorkloadInstanceState.Terminating"/>, so no teardown is failing; uninstall it
    /// instead. <see cref="WorkloadForgetResult.BlockingState"/> names its state. Nothing was done.</summary>
    NotTearingDown = 2,

    /// <summary>The row is Terminating, but not yet for longer than <c>Workloads:Repairs:StuckWarningAfterSeconds</c>,
    /// so its teardown may still finish. It may be forgotten after <see cref="WorkloadForgetResult.EligibleAtUtc"/>.
    /// Nothing was done.</summary>
    TeardownNotStuck = 3,

    /// <summary>An update of the row's module is still in progress; it is ended first, by its own completion or by
    /// <see cref="Update.IWorkloadUpdateAbandonService"/>. <see cref="WorkloadForgetResult.ActiveIntentId"/> names it.
    /// Nothing was done.</summary>
    UpdateInProgress = 4,
}

/// <summary>The result of <see cref="IWorkloadLifecycleService.ForgetAsync"/>.</summary>
public sealed record WorkloadForgetResult
{
    public required WorkloadForgetOutcome Outcome { get; init; }

    /// <summary>The row's state for <see cref="WorkloadForgetOutcome.NotTearingDown"/>; null otherwise.</summary>
    public WorkloadInstanceState? BlockingState { get; init; }

    /// <summary>The moment after which the row may be forgotten, for <see cref="WorkloadForgetOutcome.TeardownNotStuck"/>;
    /// null otherwise.</summary>
    public DateTimeOffset? EligibleAtUtc { get; init; }

    /// <summary>The open update, for <see cref="WorkloadForgetOutcome.UpdateInProgress"/>; null otherwise.</summary>
    public Guid? ActiveIntentId { get; init; }

    /// <summary>What the platform did not remove, for <see cref="WorkloadForgetOutcome.Forgotten"/>; null otherwise.</summary>
    public WorkloadForgetLeftBehind? LeftBehind { get; init; }
}

/// <summary>What a forgotten workload may have left, for the host to show the operator and to audit. Whatever is left
/// on the environment side is the operator's to remove.</summary>
public sealed record WorkloadForgetLeftBehind
{
    /// <summary>The forgotten row's id; the runtime's objects carry it in their instance label.</summary>
    public required Guid InstanceId { get; init; }

    public required Guid GroupInstanceId { get; init; }

    public required string GroupId { get; init; }

    public required string WorkloadId { get; init; }

    /// <summary>The provider the runtime may still run on, or null when the row was never bound to one.</summary>
    public WorkloadProviderId? Provider { get; init; }

    /// <summary>The retention the uninstall chose, which the teardown did not carry out.</summary>
    public required WorkloadRetention Retention { get; init; }

    /// <summary>When the row became Terminating.</summary>
    public required DateTimeOffset TerminatingSinceUtc { get; init; }

    /// <summary>Why the last teardown attempt did not finish, as the row's state message held it.</summary>
    public string? LastTeardownMessage { get; init; }

    /// <summary>The volumes the workload declares, whatever its retention: forgetting deletes nothing on the
    /// environment side, so a purging uninstall's volumes are listed too. No retained-volume record is written for
    /// them, so neither a restore nor a purge offers them.</summary>
    public required IReadOnlyList<RetainedVolumeSnapshotItem> Volumes { get; init; }

    /// <summary>The physical names of the retained volumes the row had claimed for a restore. Forgetting keeps the
    /// claims, so those volumes are not offered to another restore or purged while a leftover runtime may mount them;
    /// the operator frees them after removing the runtime.</summary>
    public required IReadOnlyList<string> RestoreClaims { get; init; }

    /// <summary>The credential port's error when revoking the workload's credentials failed, or null when they were
    /// revoked. A failed revoke is not retried: the host revokes them for <see cref="InstanceId"/>.</summary>
    public string? CredentialRevokeError { get; init; }
}
