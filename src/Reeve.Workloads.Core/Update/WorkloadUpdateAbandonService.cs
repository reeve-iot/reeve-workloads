using Microsoft.Extensions.Logging;
using Reeve.Pipeline;
using Reeve.Workloads.Abstractions;
using Reeve.Workloads.Abstractions.Domain.Entities;
using Reeve.Workloads.Abstractions.Domain.Entities.Documents;

using Reeve.Workloads.Abstractions.Update;

namespace Reeve.Workloads.Core.Update;

/// <summary>Operator escape hatch for an update intent. Whether the walk may still be unwound, and whether this build
/// carries it on, are read off the row's reading against the pipeline ; what an abandon does to the row is
/// <see cref="WorkloadUpdateIntent.Abandon"/>'s.
/// This asks the one, calls the other, and does the durable bookkeeping.</summary>
public sealed class WorkloadUpdateAbandonService : IWorkloadUpdateAbandonService
{
    /// <summary>The audit action written for every successful abandon.</summary>
    public const string AuditAction = "workloads.update.abandoned";

    internal static readonly TimeSpan WalkFaultEndsAfter = TimeSpan.FromMinutes(1);

    private readonly IWorkloadUpdateIntentRepository _intentRepository;
    private readonly IWorkloadUpdateReconcileNudge _nudge;
    private readonly IWorkloadAuditSink _auditSink;
    private readonly IWorkloadClock _clock;
    private readonly Pipeline<WorkloadUpdateStageContext> _pipeline;
    private readonly ILogger<WorkloadUpdateAbandonService> _logger;
    private readonly IWorkloadGroupRepository _groupRepository;
    private readonly IWorkloadInstanceRepository _instanceRepository;

    /// <summary>Creates the abandon service. The instance repository resolves the member's group id, which
    /// the intent row intentionally does not duplicate.</summary>
    public WorkloadUpdateAbandonService(
        IWorkloadUpdateIntentRepository intentRepository,
        IWorkloadUpdateReconcileNudge nudge,
        IWorkloadAuditSink auditSink,
        IWorkloadClock clock,
        Pipeline<WorkloadUpdateStageContext> pipeline,
        ILogger<WorkloadUpdateAbandonService> logger,
        IWorkloadGroupRepository groupRepository,
        IWorkloadInstanceRepository instanceRepository)
    {
        _intentRepository = intentRepository ?? throw new ArgumentNullException(nameof(intentRepository));
        _nudge = nudge ?? throw new ArgumentNullException(nameof(nudge));
        _auditSink = auditSink ?? throw new ArgumentNullException(nameof(auditSink));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _pipeline = pipeline ?? throw new ArgumentNullException(nameof(pipeline));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _groupRepository = groupRepository ?? throw new ArgumentNullException(nameof(groupRepository));
        _instanceRepository = instanceRepository ?? throw new ArgumentNullException(nameof(instanceRepository));
    }

    public async Task<WorkloadUpdateAbandonResult> AbandonAsync(
        Guid intentId,
        Guid? actorUserId,
        CancellationToken cancellationToken = default)
    {
        var intent = await _intentRepository.GetByIdAsync(intentId, cancellationToken).ConfigureAwait(false);
        if (intent is null)
        {
            return WorkloadUpdateAbandonResult.NotFound(intentId);
        }

        if (intent.IsTerminal)
        {
            return WorkloadUpdateAbandonResult.AlreadyTerminal(intent);
        }

        var cohort = intent.CohortId is null
            ? null
            : await ReadCohortAsync(intent, cancellationToken).ConfigureAwait(false);
        var (judgement, why, journal) = intent.Phase == WorkloadUpdatePhase.RollingBack
            ? (Judgement.GiveUp, null, null)
            : Judge(intent, cohort);
        if (judgement == Judgement.Refuse)
        {
            var stage = journal is { Entries: [.., var last] } ? last.StageId : null;
            var carriesOn = intent.WalkFaultSinceUtc is { } since
                ? $"Its walk has been failing since {since:u} ({intent.WalkFault}); if it still is at "
                    + $"{since + WalkFaultEndsAfter:u}, an abandon is taken and the walk ends it where it stands, undoing nothing"
                : "It keeps going on its own";
            return WorkloadUpdateAbandonResult.PastThePointOfNoReturn(intent,
                $"This update is past the point where it can be rolled back: {why}. {carriesOn}"
                + (stage is null ? "." : $" — its walk stands at stage '{stage}'.")) with { CurrentStage = stage };
        }

        var fromPhase = intent.Phase;
        var now = _clock.UtcNow;
        var reason = BuildReason(actorUserId, fromPhase);

        var outcome = intent.Abandon(reason, now);
        if (judgement == Judgement.End)
        {
            intent.MarkRollbackFailed(
                $"{reason} It could not be rolled back here — {why} — so it was ended where it stood and nothing was undone.",
                now);
            outcome = WorkloadUpdateAbandonOutcome.EndedWithoutRollback;
        }

        if (outcome != WorkloadUpdateAbandonOutcome.RollbackRequested)
        {
            foreach (var step in intent.Steps)
            {
                step.Advance(WorkloadUpdatePhase.RollbackFailed, WorkloadUpdateStepState.Failed, intent.Error ?? reason, now);
            }
        }

        if (cohort is null)
        {
            await _intentRepository.UpdateAsync(intent, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await _intentRepository.SaveCohortDecisionAsync(intent, cohort.Group, cancellationToken).ConfigureAwait(false);
        }

        _logger.LogWarning(
            "Update intent {IntentId} (instance {InstanceId}) abandoned by operator {ActorUserId}: {FromPhase} → {ToPhase}.",
            intent.Id, intent.GroupInstanceId, actorUserId, fromPhase, intent.Phase);

        await WriteAuditAsync(intent, actorUserId, fromPhase, cancellationToken).ConfigureAwait(false);

        try
        {
            await _nudge.NudgeAsync(intent.GroupInstanceId, WorkloadUpdateNudgeReasons.Abandoned, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Reconcile nudge after abandoning intent {IntentId} failed; the walk will pick it up on its idle fallback.", intent.Id);
        }

        if (judgement == Judgement.LeaveToTheWalk)
        {
            return WorkloadUpdateAbandonResult.Abandoned(intent,
                $"The update is past the point where it can be rolled back: {why}. The abandon is recorded, and the "
                + "walk ends the update where it stands without undoing anything — this is not finished yet; watch "
                + "the update until it reports a final outcome, then reconcile the deployment manually and retry the "
                + "update.");
        }

        return WorkloadUpdateAbandonResult.Abandoned(intent, outcome switch
        {
            WorkloadUpdateAbandonOutcome.RollbackRequested =>
                (cohort is null ? "The update is being rolled back to the previous version." : "The update cohort is being rolled back to the previous version.")
                + " This is not finished yet — watch the "
                + "update until it reports a final outcome.",
            WorkloadUpdateAbandonOutcome.EndedWithoutRollback =>
                $"The update could not be rolled back here: {why}. It was ended where it stood and nothing was undone — "
                + "reconcile the deployment manually, then retry the update.",
            _ => "The rollback was abandoned and the update is marked failed. The deployment was NOT restored — "
                + "reconcile it manually, then retry the update.",
        });
    }

    private enum Judgement
    {
        Turn,
        LeaveToTheWalk,
        Refuse,
        End,
        GiveUp,
    }

    private async Task<WorkloadUpdateCohortReading> ReadCohortAsync(
        WorkloadUpdateIntent intent, CancellationToken cancellationToken)
    {
        var instances = await _instanceRepository.ListByPluginInstanceAsync(intent.GroupInstanceId, cancellationToken)
            .ConfigureAwait(false);
        var groupId = instances.FirstOrDefault()?.GroupId
            ?? throw new InvalidOperationException($"Cannot find group id for cohort member {intent.GroupInstanceId}.");
        var group = await _groupRepository.GetAsync(groupId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Cannot find group '{groupId}' for cohort member {intent.GroupInstanceId}.");
        var members = await _intentRepository.GetCohortAsync(intent.CohortId!.Value, cancellationToken).ConfigureAwait(false);
        var unaccounted = members
            .Where(member => WorkloadUpdateRowReading.Of(member, _pipeline).Standing == WorkloadUpdateRowStanding.Unaccounted)
            .Select(member => member.Id)
            .ToHashSet();
        return WorkloadUpdateCohortReading.Create(group, members, unaccounted);
    }

    private (Judgement Judgement, string? Why, PipelineJournal? Journal) Judge(
        WorkloadUpdateIntent intent, WorkloadUpdateCohortReading? cohort)
    {
        var row = WorkloadUpdateRowReading.Of(intent, _pipeline);
        if (row.Journal is not { } journal)
        {
            return (Judgement.End, row.Why, null);
        }

        if (WorkloadUpdatePointsOfNoReturn.RefusalToUnwind(journal, _pipeline, cohort) is not { } refusal)
        {
            return (Judgement.Turn, null, null);
        }

        if (WorkloadUpdatePointsOfNoReturn.RefusalToCarryOn(journal, _pipeline) is { } stuck)
        {
            return (Judgement.End, $"{stuck}, and {refusal}", null);
        }

        if (intent.WalkFaultSinceUtc is { } since && _clock.UtcNow - since >= WalkFaultEndsAfter)
        {
            return (Judgement.LeaveToTheWalk, $"{refusal}, and its walk has been failing since {since:u}: {intent.WalkFault}", null);
        }

        return (Judgement.Refuse, refusal, journal);
    }

    private static string BuildReason(Guid? actorUserId, WorkloadUpdatePhase fromPhase)
        => $"Update abandoned by operator (user {(actorUserId is { } id ? id.ToString() : "unknown")}) from phase {fromPhase}.";

    private async Task WriteAuditAsync(
        WorkloadUpdateIntent intent,
        Guid? actorUserId,
        WorkloadUpdatePhase fromPhase,
        CancellationToken cancellationToken)
    {
        try
        {
            await _auditSink.WriteAsync(
                new WorkloadAuditEntry(
                    actorUserId,
                    AuditAction,
                    nameof(WorkloadUpdateIntent),
                    intent.Id,
                    new
                    {
                        pluginInstanceId = intent.GroupInstanceId,
                        fromPhase = fromPhase.ToString(),
                        toPhase = intent.Phase.ToString(),
                        toVersion = intent.ToVersion,
                        fromVersion = intent.FromVersion,
                    }),
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Failed to write the {Action} audit entry for intent {IntentId}; the abandon itself is committed.",
                AuditAction, intent.Id);
        }
    }
}
