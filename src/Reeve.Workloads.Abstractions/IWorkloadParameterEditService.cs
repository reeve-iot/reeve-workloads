namespace Reeve.Workloads.Abstractions;

/// <summary>Applies an operator's edit to a live instance's install-time parameters and nudges the reconciler
/// to roll it out. Whether the edited requirements are acceptable is the host's decision, made before it calls.</summary>
public interface IWorkloadParameterEditService
{
    /// <summary>Rebinds a stored password literal to an opaque reference after the host has resolved that reference
    /// and proved its value equals <paramref name="expectedStoredLiteral"/>. The old literal is a compare-and-set
    /// precondition. The write preserves the applied spec hash and does not nudge reconciliation. The host supplies
    /// whether this exact reference is version-pinned. Audit is emitted after persistence.</summary>
    Task<WorkloadParameterEditResult> RebindPasswordReferenceAsync(
        Guid workloadInstanceId,
        string parameterName,
        string expectedStoredLiteral,
        string reference,
        Guid? actorUserId,
        bool isVersionPinned,
        CancellationToken cancellationToken = default);

    /// <summary>Applies <paramref name="newValues"/> to the instance's resolved install-time parameters, once the
    /// requirements they resolve to are the ones <paramref name="precondition"/> names.</summary>
    /// <param name="workloadInstanceId">The workload instance.</param>
    /// <param name="newValues">The submitted values, merged over the stored ones.</param>
    /// <param name="actorUserId">The operator who asked, when a human did.</param>
    /// <param name="precondition">The fingerprint from <see cref="IWorkloadRequirementsService.ForParameterEditAsync"/>
    /// over the same values, and at most a pin for this workload, recorded in the same write.</param>
    /// <param name="cancellationToken">Cancels the reads, the write and the nudge.</param>
    Task<WorkloadParameterEditResult> UpdateParametersAsync(
        Guid workloadInstanceId,
        IReadOnlyDictionary<string, string?> newValues,
        Guid? actorUserId,
        WorkloadWritePrecondition precondition,
        CancellationToken cancellationToken = default);
}

/// <summary>The outcome kind of a runtime parameter edit.</summary>
public enum WorkloadParameterEditOutcome
{
    /// <summary>The edit was validated, persisted, and a reconcile nudge published.</summary>
    Success = 0,

    /// <summary>No instance exists for the supplied id (or its workload is no longer declared).</summary>
    NotFound = 1,

    /// <summary>An update intent is active on the instance — the edit is serialized behind it.</summary>
    Conflict = 2,

    /// <summary>Phase-1 schema validation failed (a resolved path-typed value violates its prefix policy).</summary>
    ValidationFailed = 3,

    /// <summary>The requirements the edit resolves to are not the ones the host judged, a pin names another workload,
    /// the stored literal changed, or a concurrent write won. Nothing was committed.</summary>
    PreconditionFailed = 5,

    /// <summary>The host refused the workload's bootstrap, so the edit could not be rendered to judge. Nothing was
    /// written; <see cref="WorkloadParameterEditResult.Error"/> carries the host's reason.</summary>
    HostRefused = 6,
}

/// <summary>Machine-readable reason a reference rebind precondition was refused.</summary>
public enum WorkloadParameterEditFailureCode
{
    StoredValueMismatch = 1,
    AlreadyReference = 2,
    ConcurrentChange = 3,
}

/// <summary>The typed result of a runtime parameter edit. A closed record, so the controller maps outcomes to
/// HTTP deterministically: Success 200, NotFound 404, Conflict 409, ValidationFailed 400, PreconditionFailed 412.</summary>
public sealed record WorkloadParameterEditResult
{
    public required WorkloadParameterEditOutcome Outcome { get; init; }

    /// <summary>The active update-intent id, set only when <see cref="Outcome"/> is
    /// <see cref="WorkloadParameterEditOutcome.Conflict"/>.</summary>
    public Guid? ActiveIntentId { get; init; }

    /// <summary>The machine-readable rebind refusal reason, when the compare-and-set did not succeed.</summary>
    public WorkloadParameterEditFailureCode? FailureCode { get; init; }

    /// <summary>The human-readable reason, set when <see cref="Outcome"/> is
    /// <see cref="WorkloadParameterEditOutcome.ValidationFailed"/>, <see cref="WorkloadParameterEditOutcome.PreconditionFailed"/>
    /// or <see cref="WorkloadParameterEditOutcome.HostRefused"/>.</summary>
    public string? Error { get; init; }

    /// <summary>An accepted edit.</summary>
    public static WorkloadParameterEditResult Success() => new()
    {
        Outcome = WorkloadParameterEditOutcome.Success,
    };

    /// <summary>The unknown-instance / no-longer-declared outcome.</summary>
    public static WorkloadParameterEditResult NotFound() => new()
    {
        Outcome = WorkloadParameterEditOutcome.NotFound,
    };

    /// <summary>The intent-serialization conflict, naming the active intent.</summary>
    public static WorkloadParameterEditResult Conflict(Guid activeIntentId) => new()
    {
        Outcome = WorkloadParameterEditOutcome.Conflict,
        ActiveIntentId = activeIntentId,
    };

    /// <summary>The phase-1 schema-validation failure (⇒ 400), carrying the reason.</summary>
    public static WorkloadParameterEditResult ValidationFailed(string error) => new()
    {
        Outcome = WorkloadParameterEditOutcome.ValidationFailed,
        Error = error,
    };

    /// <summary>A write precondition failed (⇒ 412), carrying the reason.</summary>
    public static WorkloadParameterEditResult PreconditionFailed(string error) => PreconditionFailed(error, null);

    public static WorkloadParameterEditResult PreconditionFailed(string error, WorkloadParameterEditFailureCode? failureCode) => new()
    {
        Outcome = WorkloadParameterEditOutcome.PreconditionFailed,
        Error = error,
        FailureCode = failureCode,
    };

    /// <summary>The host refused the workload's bootstrap, carrying its reason. Nothing was written.</summary>
    public static WorkloadParameterEditResult HostRefused(string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        return new() { Outcome = WorkloadParameterEditOutcome.HostRefused, Error = reason };
    }
}
