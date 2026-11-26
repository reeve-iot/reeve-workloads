using Reeve.Workloads.Abstractions.Domain.Entities;

namespace Reeve.Workloads.Abstractions.Update;

internal sealed record WorkloadUpdateAdvanceResult
{
    public required Guid IntentId { get; init; }

    public required WorkloadUpdatePhase Phase { get; init; }

    public required bool Progressed { get; init; }

    public bool IsTerminal => Phase is WorkloadUpdatePhase.Completed
        or WorkloadUpdatePhase.RolledBack
        or WorkloadUpdatePhase.RollbackFailed;

    public string? Detail { get; init; }

    public string? WalkFault { get; init; }

    public static WorkloadUpdateAdvanceResult Advanced(Guid intentId, WorkloadUpdatePhase phase, string? detail = null) => new()
    {
        IntentId = intentId,
        Phase = phase,
        Progressed = true,
        Detail = detail,
    };

    public static WorkloadUpdateAdvanceResult Stayed(Guid intentId, WorkloadUpdatePhase phase, string? detail = null) => new()
    {
        IntentId = intentId,
        Phase = phase,
        Progressed = false,
        Detail = detail,
    };

    public static WorkloadUpdateAdvanceResult Faulted(Guid intentId, WorkloadUpdatePhase phase, string detail, string walkFault) => new()
    {
        IntentId = intentId,
        Phase = phase,
        Progressed = false,
        Detail = detail,
        WalkFault = walkFault,
    };
}
