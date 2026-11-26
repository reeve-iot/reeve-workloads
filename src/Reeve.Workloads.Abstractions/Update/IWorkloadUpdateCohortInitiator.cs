namespace Reeve.Workloads.Abstractions.Update;

/// <summary>Stages one update intent per member, in one write, sharing one From and one To.</summary>
public interface IWorkloadUpdateCohortInitiator
{
    Task<WorkloadUpdateCohortInitiationResult> InitiateCohortAsync(
        WorkloadUpdateCohortInitiationRequest request,
        CancellationToken cancellationToken = default);
}

/// <summary>One ordered, group-wide update request.</summary>
public sealed record WorkloadUpdateCohortInitiationRequest
{
    public required string CandidateVersion { get; init; }
    public required IReadOnlyList<WorkloadUpdateCohortMember> Members { get; init; }
    public Guid? ActorUserId { get; init; }
}

/// <summary>The requirements and optional submitted values for one member.</summary>
public sealed record WorkloadUpdateCohortMember
{
    public required Guid GroupInstanceId { get; init; }
    public required WorkloadWritePrecondition Precondition { get; init; }
    public IReadOnlyDictionary<string, string?>? OperatorParameters { get; init; }
    public IReadOnlyCollection<string>? AdoptDeclaredResourceWorkloadIds { get; init; }
}

/// <summary>The result for an atomic cohort initiation attempt.</summary>
public sealed record WorkloadUpdateCohortInitiationResult
{
    public required WorkloadUpdateInitiationOutcome Outcome { get; init; }
    public Guid? CohortId { get; init; }
    public IReadOnlyList<WorkloadUpdateCohortMemberIntent> Members { get; init; } = [];
    public Guid? RefusedGroupInstanceId { get; init; }
    public string? RefusalMessage { get; init; }
    public bool SubmittedParametersDiffer { get; init; }
    public WorkloadUpdatePointOfNoReturn? PointOfNoReturn { get; init; }
}

/// <summary>An initiated member's identity and place in gating order.</summary>
public sealed record WorkloadUpdateCohortMemberIntent(Guid GroupInstanceId, Guid IntentId, int Ordinal);
