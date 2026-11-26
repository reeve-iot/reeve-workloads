namespace Reeve.Workloads.Abstractions;

/// <summary>Operator-requested UPWARD volume resize. Never mutates a live PVC: it writes a desired override so
/// the reconciler re-applies. The save gate is upward-only plus numeric bounds; the cluster checks the rest.</summary>
public interface IWorkloadVolumeResizeService
{
    /// <summary>Requests an upward resize to <paramref name="newSize"/>, a Kubernetes storage quantity.
    /// <paramref name="actorUserId"/> is <c>null</c> for a non-user machine caller.</summary>
    Task<WorkloadVolumeResizeResult> RequestResizeAsync(
        Guid workloadInstanceId,
        string volumeId,
        string newSize,
        Guid? actorUserId,
        CancellationToken cancellationToken = default);
}

/// <summary>The outcome kind of an operator volume-resize request.</summary>
public enum WorkloadVolumeResizeOutcome
{
    /// <summary>The upward resize was validated, persisted (as the desired override), and a reconcile nudge published.</summary>
    Success = 0,

    /// <summary>No instance exists for the supplied id (or its workload is no longer declared).</summary>
    NotFound = 1,

    /// <summary>The instance is declared, but it has no volume with the supplied <c>volumeId</c>.</summary>
    VolumeNotFound = 2,

    /// <summary>The instance is not a Kubernetes-provider workload — PVC resize is a Kubernetes-only concept.</summary>
    NotApplicable = 3,

    /// <summary>An update intent is active on the instance, so the resize is serialized behind it.</summary>
    Conflict = 4,

    /// <summary>The requested size is invalid: un-parseable, over the sanity cap, or a downward shrink.</summary>
    Rejected = 5,

    /// <summary>The requested size EQUALS the current requested size — an idempotent no-op (nothing persisted/nudged).</summary>
    NoChange = 6,

    /// <summary>The host refused the workload's bootstrap, so the resize could not be rendered. Nothing was
    /// written; <see cref="WorkloadVolumeResizeResult.Error"/> carries the host's reason.</summary>
    HostRefused = 7,
}

/// <summary>The typed result of a volume-resize request. A closed record so the controller maps every outcome
/// to HTTP deterministically.</summary>
public sealed record WorkloadVolumeResizeResult
{
    public required WorkloadVolumeResizeOutcome Outcome { get; init; }

    /// <summary>The effective requested size AFTER the request — the accepted new size (Success) or the unchanged
    /// current size (NoChange). Null for the failure outcomes. Echoed by the controller so the UI can confirm.</summary>
    public string? RequestedSize { get; init; }

    /// <summary>The active update-intent id, set only when <see cref="Outcome"/> is <see cref="WorkloadVolumeResizeOutcome.Conflict"/>.</summary>
    public Guid? ActiveIntentId { get; init; }

    /// <summary>The human-readable message, set for <see cref="WorkloadVolumeResizeOutcome.Rejected"/> / <see cref="WorkloadVolumeResizeOutcome.NotApplicable"/>
    /// / <see cref="WorkloadVolumeResizeOutcome.VolumeNotFound"/> / <see cref="WorkloadVolumeResizeOutcome.HostRefused"/>.</summary>
    public string? Error { get; init; }

    public static WorkloadVolumeResizeResult Success(string requestedSize) => new()
    {
        Outcome = WorkloadVolumeResizeOutcome.Success,
        RequestedSize = requestedSize,
    };

    public static WorkloadVolumeResizeResult NoChange(string requestedSize) => new()
    {
        Outcome = WorkloadVolumeResizeOutcome.NoChange,
        RequestedSize = requestedSize,
    };

    public static WorkloadVolumeResizeResult NotFound() => new()
    {
        Outcome = WorkloadVolumeResizeOutcome.NotFound,
    };

    public static WorkloadVolumeResizeResult VolumeNotFound(string error) => new()
    {
        Outcome = WorkloadVolumeResizeOutcome.VolumeNotFound,
        Error = error,
    };

    public static WorkloadVolumeResizeResult NotApplicable(string error) => new()
    {
        Outcome = WorkloadVolumeResizeOutcome.NotApplicable,
        Error = error,
    };

    public static WorkloadVolumeResizeResult Conflict(Guid activeIntentId) => new()
    {
        Outcome = WorkloadVolumeResizeOutcome.Conflict,
        ActiveIntentId = activeIntentId,
    };

    public static WorkloadVolumeResizeResult Rejected(string error) => new()
    {
        Outcome = WorkloadVolumeResizeOutcome.Rejected,
        Error = error,
    };

    public static WorkloadVolumeResizeResult HostRefused(string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        return new() { Outcome = WorkloadVolumeResizeOutcome.HostRefused, Error = reason };
    }
}
