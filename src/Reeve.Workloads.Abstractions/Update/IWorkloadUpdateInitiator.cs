namespace Reeve.Workloads.Abstractions.Update;

/// <summary>Builds the plan, checks the host's precondition against it, and stages the durable update intent —
/// it does NOT walk the update; the update walk in Core does that, stage by stage.</summary>
public interface IWorkloadUpdateInitiator
{
    /// <summary>Submitted parameters ride on the intent and commit only at completion. Nothing is written unless the
    /// plan's requirements fingerprint is the one the host judged.</summary>
    Task<WorkloadUpdateInitiationResult> InitiateAsync(
        WorkloadUpdateInitiationRequest request,
        CancellationToken cancellationToken = default);
}

/// <summary>Everything one update initiation needs, as values.</summary>
public sealed record WorkloadUpdateInitiationRequest
{
    /// <summary>The module instance to update.</summary>
    public required Guid PluginInstanceId { get; init; }

    /// <summary>The version to update to.</summary>
    public required string CandidateVersion { get; init; }

    /// <summary>What the host judged, from <see cref="IWorkloadRequirementsService.ForUpdateAsync"/> over the same
    /// instance, version, parameters and adoptions.</summary>
    public required WorkloadWritePrecondition Precondition { get; init; }

    /// <summary>The operator who asked, when a human did.</summary>
    public Guid? ActorUserId { get; init; }

    /// <summary>Parameter values the operator submitted with the update.</summary>
    public IReadOnlyDictionary<string, string?>? OperatorParameters { get; init; }

    /// <summary>Declared resource workloads the operator agreed to adopt.</summary>
    public IReadOnlyCollection<string>? AdoptDeclaredResourceWorkloadIds { get; init; }
}

/// <summary>The closed outcome of <see cref="IWorkloadUpdateInitiator.InitiateAsync"/>. Value-equal.</summary>
public sealed record WorkloadUpdateInitiationResult
{
    /// <summary>Which kind of outcome this is.</summary>
    public required WorkloadUpdateInitiationOutcome Outcome { get; init; }

    /// <summary>The staged intent id when <see cref="Outcome"/> is <see cref="WorkloadUpdateInitiationOutcome.Started"/>, or the
    /// in-flight intent id when <see cref="WorkloadUpdateInitiationOutcome.AlreadyInProgress"/>; otherwise null.</summary>
    public Guid? IntentId { get; init; }

    /// <summary>Why nothing was staged when <see cref="Outcome"/> is Refused or PreconditionFailed; null otherwise.</summary>
    public string? RefusalMessage { get; init; }

    /// <summary>Set only on <see cref="WorkloadUpdateInitiationOutcome.AlreadyInProgress"/>: the caller submitted
    /// parameters the in-flight intent does not carry, so echoing success would drop them silently.</summary>
    public bool SubmittedParametersDiffer { get; init; }

    /// <summary>Set only on <see cref="WorkloadUpdateInitiationOutcome.Started"/>: the first stage of this update
    /// past which it can no longer be rolled back, and the reason that stage gives. <c>null</c> when every stage the
    /// composition registers is reversible. An operator is told this when the update starts, before anything past
    /// it has run.</summary>
    public WorkloadUpdatePointOfNoReturn? PointOfNoReturn { get; init; }

    /// <summary>A new intent was staged; the leader's update walk takes it from here.</summary>
    public static WorkloadUpdateInitiationResult Started(Guid intentId, WorkloadUpdatePointOfNoReturn? pointOfNoReturn = null) => new()
    {
        Outcome = WorkloadUpdateInitiationOutcome.Started,
        IntentId = intentId,
        PointOfNoReturn = pointOfNoReturn,
    };

    /// <summary>An active intent already exists — the day-2 409 case (no new intent staged).</summary>
    public static WorkloadUpdateInitiationResult AlreadyInProgress(
        Guid activeIntentId, bool submittedParametersDiffer = false) => new()
    {
        Outcome = WorkloadUpdateInitiationOutcome.AlreadyInProgress,
        IntentId = activeIntentId,
        SubmittedParametersDiffer = submittedParametersDiffer,
    };

    /// <summary>No plan can be built — no module row, the manifest is absent or unreadable, a value cannot be
    /// resolved, or the host refused a side's bootstrap. No intent staged; <paramref name="message"/> says why, in
    /// the host's words for a refusal.</summary>
    public static WorkloadUpdateInitiationResult Refused(string message) => new()
    {
        Outcome = WorkloadUpdateInitiationOutcome.Refused,
        RefusalMessage = message,
    };

    /// <summary>The plan's requirements are not the ones the host judged, or a pin names a workload with no row.
    /// Nothing was written.</summary>
    public static WorkloadUpdateInitiationResult PreconditionFailed(string message) => new()
    {
        Outcome = WorkloadUpdateInitiationOutcome.PreconditionFailed,
        RefusalMessage = message,
    };
}

/// <summary>The initiation outcomes.</summary>
public enum WorkloadUpdateInitiationOutcome
{
    /// <summary>A fresh intent was staged and the reconciler nudged.</summary>
    Started = 0,

    /// <summary>An active intent already exists for the instance (409).</summary>
    AlreadyInProgress = 1,

    /// <summary>No plan can be built; no intent created.</summary>
    Refused = 3,

    /// <summary>The requirements changed since the host judged them; nothing written, and the host decides again.</summary>
    PreconditionFailed = 4,
}

/// <summary>The stage past which an update can no longer be rolled back, and why — the sentence the stage itself
/// declares with <c>StageReversibility.IrreversibleOnceEntered</c>, or the library's own for a stage this build does
/// not register.</summary>
/// <param name="StageId">The id of the stage: before an update starts, the first irreversible stage in walk order.</param>
/// <param name="Reason">What an operator should know about it.</param>
public sealed record WorkloadUpdatePointOfNoReturn(string StageId, string Reason);
