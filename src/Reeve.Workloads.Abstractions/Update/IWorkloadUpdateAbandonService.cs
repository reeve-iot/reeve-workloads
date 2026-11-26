using Reeve.Workloads.Abstractions.Domain.Entities;

namespace Reeve.Workloads.Abstractions.Update;

/// <summary>The OPERATOR ESCAPE from a wedged update intent: one that can't advance holds the active-intent unique
/// index open, freezing every mutation on the instance. Thin — drives <see cref="WorkloadUpdateIntent.Abandon"/> then hands rollback work to the orchestrator.</summary>
public interface IWorkloadUpdateAbandonService
{
    /// <summary>Abandons <paramref name="intentId"/> for <paramref name="actorUserId"/> (the host must already have
    /// authorized the caller — not enforced here). A concurrent leader advance throws <c>DbUpdateConcurrencyException</c>, mapped to 409 globally. On Npgsql EF 11
    /// a serialization failure or deadlock throws that type too (SQLSTATE 40001 or 40P01 inside), and is not a lost race.</summary>
    Task<WorkloadUpdateAbandonResult> AbandonAsync(
        Guid intentId,
        Guid? actorUserId,
        CancellationToken cancellationToken = default);
}

/// <summary>The closed outcome of <see cref="IWorkloadUpdateAbandonService.AbandonAsync"/>. Value-equal.</summary>
public sealed record WorkloadUpdateAbandonResult
{
    /// <summary>Which kind of outcome this is.</summary>
    public required WorkloadUpdateAbandonStatus Status { get; init; }

    /// <summary>The intent id, or null when it was not found.</summary>
    public Guid? IntentId { get; init; }

    /// <summary>How the update stands AFTER the call — still in progress when the abandon only started a rollback.
    /// Null when the intent was not found.</summary>
    public WorkloadUpdateOutcome? Outcome { get; init; }

    /// <summary>Which way the walk was asked to go AFTER the call (<see cref="WorkloadUpdateIntent.Direction"/>):
    /// rolling back once an abandon asks for the turn, unchanged on a refusal. An abandon that ENDED the update reads
    /// forward whenever the walk had not yet turned — an update ended without rollback, or a rollback given up before
    /// the walk took the turn — because no rollback ran; a row with no readable journal reads rolling back even then,
    /// having nothing to say it did not turn. Null when the intent was not found.</summary>
    public WorkloadUpdateDirection? Direction { get; init; }

    /// <summary>The last stage in the walk's journal, when it has one — on a refusal, the journal as the walk and the
    /// status read the row.</summary>
    public string? CurrentStage { get; init; }

    /// <summary>The <c>phase</c> wire token the abandon response carries: the name of the intent's
    /// phase projection AFTER the call. Null when the intent was not found. Removed with the key, in the release that
    /// drops the phase.</summary>
    public string? Phase { get; init; }

    /// <summary>Whether the intent has settled. False when the abandon only STARTED a rollback (the intent is still
    /// active until the walk settles it) — the UI must not claim the update is over.</summary>
    public bool IsTerminal { get; init; }

    /// <summary>An operator-facing explanation of what happened (or why nothing did). Never null.</summary>
    public required string Message { get; init; }

    public static WorkloadUpdateAbandonResult Abandoned(WorkloadUpdateIntent intent, string message)
        => Of(intent, WorkloadUpdateAbandonStatus.Abandoned, message);

    public static WorkloadUpdateAbandonResult NotFound(Guid intentId) => new()
    {
        Status = WorkloadUpdateAbandonStatus.NotFound,
        IntentId = null,
        Message = $"Update intent {intentId} was not found.",
    };

    public static WorkloadUpdateAbandonResult AlreadyTerminal(WorkloadUpdateIntent intent)
        => Of(intent, WorkloadUpdateAbandonStatus.AlreadyTerminal,
            $"This update has already settled as {intent.Outcome} — there is nothing to abandon.");

    public static WorkloadUpdateAbandonResult PastThePointOfNoReturn(WorkloadUpdateIntent intent, string message)
        => Of(intent, WorkloadUpdateAbandonStatus.PastThePointOfNoReturn, message);

    private static WorkloadUpdateAbandonResult Of(WorkloadUpdateIntent intent, WorkloadUpdateAbandonStatus status, string message)
    {
        ArgumentNullException.ThrowIfNull(intent);
        return new()
        {
            Status = status,
            IntentId = intent.Id,
            Outcome = intent.Outcome,
            Direction = intent.Direction,
            CurrentStage = intent.CurrentStage,
            Phase = intent.Phase.ToString(),
            IsTerminal = intent.IsTerminal,
            Message = message,
        };
    }
}

/// <summary>The outcomes of an operator abandon.</summary>
public enum WorkloadUpdateAbandonStatus
{
    /// <summary>The intent was driven onto the rollback branch, or ended — a rollback given up on, or a walk this build
    /// could neither undo nor carry on, ended where it stood. See <see cref="WorkloadUpdateAbandonResult.IsTerminal"/>
    /// for which.</summary>
    Abandoned = 0,

    /// <summary>No intent with that id exists (404).</summary>
    NotFound = 1,

    /// <summary>The intent had already settled (409) — Completed / RolledBack / RollbackFailed.</summary>
    AlreadyTerminal = 2,

    /// <summary>The walk may not be unwound and this build carries it on, so the abandon is refused (409) and the
    /// update keeps going on its own. It may not be unwound once it entered a stage that cannot be undone, or a stage
    /// this build does not register, which may be one. <see cref="WorkloadUpdateAbandonResult.Message"/> gives the reason and,
    /// when the row says, the stage its walk stands at; for a walk that is failing, from when an abandon is taken.
    /// Where this build cannot carry the walk on either, the abandon ends it instead:
    /// <see cref="WorkloadUpdateAbandonOutcome.EndedWithoutRollback"/>. Where its walk has been failing for a minute
    /// (<see cref="WorkloadUpdateIntent.WalkFaultSinceUtc"/>), the abandon is taken and the walk ends the update where
    /// it stands, undoing nothing; the result is not terminal until it has.</summary>
    PastThePointOfNoReturn = 3,
}
