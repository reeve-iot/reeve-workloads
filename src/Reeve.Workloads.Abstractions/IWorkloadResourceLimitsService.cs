using Reeve.Workloads.Abstractions.Contracts;

namespace Reeve.Workloads.Abstractions;

/// <summary>Day-2 read/write of a workload instance's resource overrides. The write records the reserved
/// resources.* parameters and lets the reconciler re-apply; it never touches a live container.</summary>
public interface IWorkloadResourceLimitsService
{
    /// <summary>The declared, overridden and effective resources; null when the instance is unknown, its workload is
    /// no longer declared, or the host refused its bootstrap (logged with the reason — <see cref="SetAsync"/> refuses
    /// by name).</summary>
    Task<WorkloadResourcesResponse?> GetAsync(
        Guid workloadInstanceId,
        CancellationToken cancellationToken = default);

    Task<WorkloadResourceLimitsResult> SetAsync(
        Guid workloadInstanceId,
        SetWorkloadResourcesRequest request,
        Guid? actorUserId,
        CancellationToken cancellationToken = default);
}

public enum WorkloadResourceLimitsOutcome
{
    Success = 0,
    NotFound = 1,
    Conflict = 2,
    Rejected = 3,
    NoChange = 4,

    /// <summary>The host refused the workload's bootstrap, so the limits could not be rendered. Nothing was
    /// written; <see cref="WorkloadResourceLimitsResult.Error"/> carries the host's reason.</summary>
    HostRefused = 5,
}

public sealed record WorkloadResourceLimitsResult
{
    public required WorkloadResourceLimitsOutcome Outcome { get; init; }

    /// <summary>The effective posture after the request; null for the failure outcomes.</summary>
    public WorkloadResourceValues? Effective { get; init; }

    /// <summary>Set only for <see cref="WorkloadResourceLimitsOutcome.Conflict"/>.</summary>
    public Guid? ActiveIntentId { get; init; }

    /// <summary>Set for <see cref="WorkloadResourceLimitsOutcome.Rejected"/> and <see cref="WorkloadResourceLimitsOutcome.HostRefused"/>.</summary>
    public string? Error { get; init; }

    public static WorkloadResourceLimitsResult Success(WorkloadResourceValues effective) => new()
    {
        Outcome = WorkloadResourceLimitsOutcome.Success,
        Effective = effective,
    };

    public static WorkloadResourceLimitsResult NoChange(WorkloadResourceValues effective) => new()
    {
        Outcome = WorkloadResourceLimitsOutcome.NoChange,
        Effective = effective,
    };

    public static WorkloadResourceLimitsResult NotFound() => new()
    {
        Outcome = WorkloadResourceLimitsOutcome.NotFound,
    };

    public static WorkloadResourceLimitsResult Conflict(Guid activeIntentId) => new()
    {
        Outcome = WorkloadResourceLimitsOutcome.Conflict,
        ActiveIntentId = activeIntentId,
    };

    public static WorkloadResourceLimitsResult Rejected(string error) => new()
    {
        Outcome = WorkloadResourceLimitsOutcome.Rejected,
        Error = error,
    };

    public static WorkloadResourceLimitsResult HostRefused(string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        return new() { Outcome = WorkloadResourceLimitsOutcome.HostRefused, Error = reason };
    }
}
