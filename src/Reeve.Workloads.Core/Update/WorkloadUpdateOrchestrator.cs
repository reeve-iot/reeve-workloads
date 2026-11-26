using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Reeve.Pipeline;
using Reeve.Workloads.Abstractions;
using Reeve.Workloads.Abstractions.Domain.Entities;
using Reeve.Workloads.Abstractions.Domain.Entities.Documents;
using Reeve.Workloads.Abstractions.Update;
using Reeve.Workloads.Core;
using Reeve.Workloads.Core.Update.Stages;

namespace Reeve.Workloads.Core.Update;

internal sealed class WorkloadUpdateOrchestrator
{
    internal static readonly DateTimeOffset RollbackHealthNoApplyTimeGate = DateTimeOffset.MinValue;

    private readonly IWorkloadUpdateIntentRepository _intentRepository;
    private readonly IWorkloadGroupRepository? _groupRepository;
    private readonly IWorkloadInstanceRepository? _instanceRepository;
    private readonly IWorkloadUpdatePlanner _planner;
    private readonly Pipeline<WorkloadUpdateStageContext> _pipeline;
    private readonly PipelineRunner<WorkloadUpdateStageContext> _runner;
    private readonly IWorkloadClock _clock;
    private readonly ILogger<WorkloadUpdateOrchestrator> _logger;
    private readonly IHostApplicationLifetime _lifetime;

    public WorkloadUpdateOrchestrator(
        IWorkloadUpdateIntentRepository intentRepository,
        IWorkloadUpdatePlanner planner,
        Pipeline<WorkloadUpdateStageContext> pipeline,
        IWorkloadClock clock,
        ILogger<WorkloadUpdateOrchestrator> logger,
        IHostApplicationLifetime lifetime)
        : this(intentRepository, planner, pipeline, clock, logger, lifetime, null, null)
    {
    }

    public WorkloadUpdateOrchestrator(
        IWorkloadUpdateIntentRepository intentRepository,
        IWorkloadUpdatePlanner planner,
        Pipeline<WorkloadUpdateStageContext> pipeline,
        IWorkloadClock clock,
        ILogger<WorkloadUpdateOrchestrator> logger,
        IHostApplicationLifetime lifetime,
        IWorkloadGroupRepository? groupRepository)
        : this(intentRepository, planner, pipeline, clock, logger, lifetime, groupRepository, null)
    {
    }

    public WorkloadUpdateOrchestrator(
        IWorkloadUpdateIntentRepository intentRepository,
        IWorkloadUpdatePlanner planner,
        Pipeline<WorkloadUpdateStageContext> pipeline,
        IWorkloadClock clock,
        ILogger<WorkloadUpdateOrchestrator> logger,
        IHostApplicationLifetime lifetime,
        IWorkloadGroupRepository? groupRepository,
        IWorkloadInstanceRepository? instanceRepository)
    {
        _lifetime = lifetime ?? throw new ArgumentNullException(nameof(lifetime));
        _intentRepository = intentRepository ?? throw new ArgumentNullException(nameof(intentRepository));
        _groupRepository = groupRepository;
        _instanceRepository = instanceRepository;
        _planner = planner ?? throw new ArgumentNullException(nameof(planner));
        _pipeline = pipeline ?? throw new ArgumentNullException(nameof(pipeline));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _runner = new PipelineRunner<WorkloadUpdateStageContext>(_pipeline, new WorkloadClockTimeProvider(_clock));
    }

    public async Task<WorkloadUpdateAdvanceResult> AdvanceAsync(WorkloadUpdateIntent intent, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(intent);

        if (intent.IsTerminal)
        {
            return WorkloadUpdateAdvanceResult.Stayed(intent.Id, intent.Phase, "intent is terminal");
        }

        var row = WorkloadUpdateRowReading.Of(intent, _pipeline);

        WorkloadGroup? trackedGroup = null;
        WorkloadUpdateCohortReading? cohortReading = null;
        if (intent.CohortId is { } cohortId)
        {
            if (_groupRepository is null || _instanceRepository is null)
            {
                return WorkloadUpdateAdvanceResult.Stayed(intent.Id, intent.Phase,
                    "cohort walk requires group and instance repositories");
            }

            var instanceRows = await _instanceRepository
                .ListByPluginInstanceAsync(intent.GroupInstanceId, cancellationToken).ConfigureAwait(false);
            var groupIds = instanceRows.Select(instance => instance.GroupId)
                .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (groupIds.Count != 1)
            {
                return WorkloadUpdateAdvanceResult.Stayed(intent.Id, intent.Phase,
                    $"cohort instance '{intent.GroupInstanceId}' has no single group row");
            }

            trackedGroup = await _groupRepository.GetAsync(groupIds[0], cancellationToken).ConfigureAwait(false);
            if (trackedGroup is null)
            {
                return WorkloadUpdateAdvanceResult.Stayed(intent.Id, intent.Phase,
                    $"cohort group '{groupIds[0]}' was not found");
            }

            var members = await _intentRepository.GetCohortAsync(cohortId, cancellationToken).ConfigureAwait(false);
            cohortReading = ReadCohort(trackedGroup, members);

            if (cohortReading.Failing
                && intent.Phase != WorkloadUpdatePhase.RollingBack
                && row.Journal is { Direction: PipelineDirection.Forward })
            {
                var failedMember = cohortReading.Members.FirstOrDefault(member =>
                    member.Id != intent.Id && (member.IsTerminal && member.Outcome != WorkloadUpdateOutcome.Completed
                        || member.ReadStageJournal() is { Verdict: WorkloadDocumentVerdict.Value, Value.Direction: PipelineDirection.Compensating }
                        || cohortReading.UnaccountedMemberIds.Contains(member.Id))) ?? intent;
                var reason = $"Update cohort is rolling back because member {failedMember.GroupInstanceId} failed or became unaccounted.";
                intent.Fail(reason, _clock.UtcNow);
                _logger.LogWarning("Update intent {IntentId} asked to turn by the cohort veto: {Reason}", intent.Id, reason);
                row = WorkloadUpdateRowReading.Of(intent, _pipeline);
            }
        }

        if (intent.Phase == WorkloadUpdatePhase.RollingBack)
        {
            if (row.Journal is not { } asked)
            {
                if (intent.ReadStageJournal() is { Verdict: WorkloadDocumentVerdict.Value, Value: { } stale })
                {
                    intent.RecordStageJournal(stale with { Direction = PipelineDirection.Compensating, State = PipelineState.Failed });
                }

                return await EndAsync(intent,
                    $"The rollback could not be walked: {row.Why}. The deployment was NOT restored — reconcile it "
                    + "manually, then retry the update.",
                    "a rollback nothing recorded ended",
                    cancellationToken, trackedGroup).ConfigureAwait(false);
            }

            if (asked.Direction == PipelineDirection.Forward)
            {
                if (asked.Entries.Count == 0)
                {
                    return await RollBackNothingAsync(intent, cancellationToken, trackedGroup).ConfigureAwait(false);
                }

                if (WorkloadUpdatePointsOfNoReturn.RefusalToUnwind(asked, _pipeline, cohortReading) is { } refusal)
                {
                    if (_lifetime.ApplicationStopping.IsCancellationRequested
                        && asked.Entries.Any(entry => entry.Direction == PipelineDirection.Forward && _pipeline.Find(entry.StageId) is null))
                    {
                        _logger.LogInformation(
                            "Update intent {IntentId}: the process is stopping, so its abandon across an unregistered stage is left for the next leader.",
                            intent.Id);
                        return WorkloadUpdateAdvanceResult.Stayed(intent.Id, intent.Phase,
                            "the process is stopping; the abandon crosses a stage this build does not register and is left for the next leader");
                    }

                    var failing = intent.WalkFaultSinceUtc is { } since
                        ? $" Its walk had been failing since {since:u}: {intent.WalkFault}."
                        : string.Empty;
                    var working = asked.Entries[^1] is { State: StageEntryState.Working } last
                        ? $" Stage '{last.StageId}' was still working and may have taken effect."
                        : string.Empty;
                    return await EndAsync(intent,
                        $"The update was abandoned, but {refusal}.{failing}{working} It was ended where it stood and "
                        + "nothing was undone — reconcile the deployment manually, then retry the update.",
                        "abandoned where it could not be unwound",
                        cancellationToken, trackedGroup).ConfigureAwait(false);
                }

                if (trackedGroup is not null)
                {
                    intent.RecordStageJournal(asked with { Direction = PipelineDirection.Compensating });
                    await _intentRepository.SaveCohortDecisionAsync(intent, trackedGroup, cancellationToken).ConfigureAwait(false);
                    row = WorkloadUpdateRowReading.Of(intent, _pipeline);
                }
            }
        }
        else if (row.Standing == WorkloadUpdateRowStanding.Unaccounted)
        {
            _logger.LogError("Update intent {IntentId} is in {Phase} and cannot be walked here: {Why}; leaving it untouched.",
                intent.Id, intent.Phase, row.Why);
            return WorkloadUpdateAdvanceResult.Stayed(intent.Id, intent.Phase, row.Why ?? "unaccounted");
        }

        WorkloadUpdatePlan plan;
        try
        {
            plan = await _planner
                .BuildPlanAsync(
                    intent.GroupInstanceId,
                    intent.ToVersion,
                    intent.Id,
                    intent.ReadOperatorParameters(),
                    intent.GetAdoptedResourceWorkloadIds(),
                    new WorkloadUpdatePlanOrigin(intent.FromVersion),
                    cancellationToken)
                .ConfigureAwait(false);
            plan = FromWhereItStarted(intent, plan);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (WorkloadHostRefusedException refused)
        {
            _logger.LogWarning(
                "The host refused the update plan for intent {IntentId} (instance {InstanceId}); staying in {Phase} — {Refusal}",
                intent.Id, intent.GroupInstanceId, intent.Phase, refused.Reason);
            return await HandlePlanFailureAsync(intent, row, trackedGroup, cohortReading,
                $"plan unavailable: {refused.Reason}", $"plan unavailable: {refused.Reason}", definitive: true,
                cancellationToken).ConfigureAwait(false);
        }
        catch (WorkloadParameterReferenceResolutionException ex)
        {
            _logger.LogWarning(ex,
                "Could not resolve parameter reference while building update plan for intent {IntentId} (instance {InstanceId}); staying in {Phase} to retry.",
                intent.Id, intent.GroupInstanceId, intent.Phase);
            return await HandlePlanFailureAsync(intent, row, trackedGroup, cohortReading,
                $"parameter reference unavailable: {ex.Message}", $"parameter reference unavailable: {ex.Message}",
                definitive: false, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Could not build the update plan for intent {IntentId} (instance {InstanceId}); staying in {Phase} to retry.",
                intent.Id, intent.GroupInstanceId, intent.Phase);
            return await HandlePlanFailureAsync(intent, row, trackedGroup, cohortReading,
                "plan unavailable", $"plan unavailable: {ex.Message}", definitive: false,
                cancellationToken).ConfigureAwait(false);
        }

        if (intent.CohortId is not null && trackedGroup is not null && !trackedGroup.IsGroup(plan.GroupId))
        {
            return WorkloadUpdateAdvanceResult.Stayed(intent.Id, intent.Phase,
                $"cohort plan group '{plan.GroupId}' does not match the instance group '{trackedGroup.GroupId}'");
        }

        var journal = row.Journal!;
        if (intent.Phase == WorkloadUpdatePhase.RollingBack && journal.Direction == PipelineDirection.Forward)
        {
            journal = journal with { Direction = PipelineDirection.Compensating };
            intent.RecordStageJournal(journal);
            await _intentRepository.UpdateAsync(intent, cancellationToken).ConfigureAwait(false);
        }

        if (journal is { Direction: PipelineDirection.Forward, State: PipelineState.Running }
            && (journal.Entries.Count == 0 || journal.Entries[^1].State != StageEntryState.Working)
            && WorkloadUpdateRowReading.NextForwardStage(journal, _pipeline) is { } next
            && _pipeline.Find(next) is { } nextStage)
        {
            if (!nextStage.Reversibility.IsReversible && _lifetime.ApplicationStopping.IsCancellationRequested)
            {
                _logger.LogInformation(
                    "Update intent {IntentId}: the process is stopping, so stage '{StageId}' is left for the next leader.",
                    intent.Id, next);
                return WorkloadUpdateAdvanceResult.Stayed(intent.Id, intent.Phase,
                    $"the process is stopping; stage '{next}' cannot be undone once entered and is left for the next leader");
            }

            var entered = new PipelineJournal(
                [.. journal.Entries, new StageEntry(next, _clock.UtcNow, PipelineDirection.Forward, StageEntryState.Working,
                    "entered; saved before its first call")],
                journal.Direction,
                journal.State);
            intent.RecordStageJournal(entered);
            await _intentRepository.UpdateAsync(intent, cancellationToken).ConfigureAwait(false);
            WarnOnPassingThePointOfNoReturn(intent, journal, entered);
            journal = entered;
        }

        var context = WorkloadUpdateStageContexts.For(intent, plan, cohortReading, trackedGroup, _clock.UtcNow, _pipeline);
        var directionBefore = journal.Direction;
        var tick = await _runner.AdvanceAsync(journal, context, cancellationToken).ConfigureAwait(false);
        intent.ClearWalkFault();

        if (tick.Result == PipelineTickResult.UnknownStage)
        {
            if (_lifetime.ApplicationStopping.IsCancellationRequested)
            {
                _logger.LogInformation(
                    "Update intent {IntentId}: the process is stopping, so its walk at unregistered stage '{StageId}' is left for the next leader.",
                    intent.Id, tick.StageId);
                return WorkloadUpdateAdvanceResult.Stayed(intent.Id, intent.Phase,
                    $"the process is stopping; the walk is at stage '{tick.StageId}', which this build does not register, and is left for the next leader");
            }

            var undone = tick.Journal.Direction == PipelineDirection.Compensating ? "nothing further" : "nothing";
            tick = tick with
            {
                Journal = tick.Journal with { State = PipelineState.Failed },
                Result = PipelineTickResult.Failed,
                Detail = $"The update's walk is at stage '{tick.StageId}', which this build does not register — a replica "
                    + $"on another build walked it there. It was ended where it stood and {undone} was undone; redeploy the "
                    + "plugin at the version you want, then retry the update if it is not already there.",
            };
        }

        if (directionBefore == PipelineDirection.Forward
            && tick.Journal.Direction == PipelineDirection.Compensating
            && WorkloadUpdatePointsOfNoReturn.RefusalToUnwind(tick.Journal with { Direction = PipelineDirection.Forward }, _pipeline, cohortReading)
                is { } vetoed)
        {
            tick = tick with
            {
                Journal = tick.Journal with { Direction = PipelineDirection.Forward, State = PipelineState.Failed },
                Result = PipelineTickResult.Failed,
                Detail = $"{tick.Detail} ({vetoed})",
            };
        }

        if (tick.Result == PipelineTickResult.WaitingOverBudget)
        {
            _logger.LogWarning(
                "Update intent {IntentId}: stage '{StageId}' is past its budget and still working: {Detail}",
                intent.Id, tick.StageId, tick.Detail);
        }

        Project(intent, tick);
        intent.RecordStageJournal(tick.Journal);
        if ((directionBefore == PipelineDirection.Forward
                && tick.Journal.Direction == PipelineDirection.Compensating
                || intent.IsTerminal)
            && intent.CohortId is not null
            && trackedGroup is not null)
        {
            await _intentRepository.SaveCohortDecisionAsync(intent, trackedGroup, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await _intentRepository.UpdateAsync(intent, cancellationToken).ConfigureAwait(false);
        }

        var flipped = tick.Journal.Direction != directionBefore;
        var moved = tick.Result is PipelineTickResult.Advanced
            or PipelineTickResult.Completed
            or PipelineTickResult.Compensated
            or PipelineTickResult.Failed;

        return moved || flipped
            ? WorkloadUpdateAdvanceResult.Advanced(intent.Id, intent.Phase, Describe(tick))
            : WorkloadUpdateAdvanceResult.Stayed(intent.Id, intent.Phase, Describe(tick));
    }

    private async Task<WorkloadUpdateAdvanceResult> HandlePlanFailureAsync(
        WorkloadUpdateIntent intent,
        WorkloadUpdateRowReading row,
        WorkloadGroup? trackedGroup,
        WorkloadUpdateCohortReading? cohortReading,
        string detail,
        string walkFault,
        bool definitive,
        CancellationToken cancellationToken)
    {
        if (!definitive
            || trackedGroup is null
            || cohortReading is null
            || intent.Phase == WorkloadUpdatePhase.RollingBack
            || row.Journal is not { Direction: PipelineDirection.Forward } forward
            || WorkloadUpdatePointsOfNoReturn.RefusalToUnwind(forward, _pipeline, cohortReading) is not null)
        {
            return WorkloadUpdateAdvanceResult.Faulted(intent.Id, intent.Phase, detail, walkFault);
        }

        var reason = $"Update cohort is rolling back because member {intent.GroupInstanceId} cannot be planned: {detail}.";
        intent.Fail(reason, _clock.UtcNow);
        _logger.LogWarning("Update intent {IntentId} asked to turn by the cohort after a definitive plan failure: {Detail}",
            intent.Id, detail);

        return await AdvanceAsync(intent, cancellationToken).ConfigureAwait(false);
    }

    public async Task<WorkloadUpdateAdvanceResult> WalkAsync(WorkloadUpdateIntent intent, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(intent);

        var bound = (2 * _pipeline.Order.Count) + 1;
        WorkloadUpdateAdvanceResult? first = null;
        WorkloadUpdateAdvanceResult result;
        var calls = 0;
        do
        {
            result = await AdvanceAsync(intent, cancellationToken).ConfigureAwait(false);
            first ??= result;
            calls++;
        }
        while (result.Progressed && !result.IsTerminal && calls < bound);

        return result with { Progressed = first.Progressed || result.Progressed };
    }

    private WorkloadUpdatePlan FromWhereItStarted(WorkloadUpdateIntent intent, WorkloadUpdatePlan plan)
    {
        var reading = intent.ReadPreviousSpecs();
        if (reading is not { Verdict: WorkloadDocumentVerdict.Value, Value: { } recorded })
        {
            if (reading.Verdict == WorkloadDocumentVerdict.Unreadable)
            {
                _logger.LogWarning(
                    "Update intent {IntentId} has an unreadable previous-spec snapshot; its previous specs are rendered "
                    + "again from version {FromVersion}.",
                    intent.Id, intent.FromVersion ?? "(none)");
            }

            return plan;
        }

        var restored = recorded.ToDictionary(
            entry => entry.Key,
            entry => plan.PreviousSpecs.TryGetValue(entry.Key, out var rendered)
                ? WorkloadStoredSpecs.WithHostValuesOf(entry.Value, rendered)
                : entry.Value,
            StringComparer.Ordinal);

        return plan with
        {
            FromVersion = intent.FromVersion,
            PreviousSpecs = restored,
            Workloads = [.. plan.Workloads.Select(item => item with
            {
                PreviousSpec = restored.GetValueOrDefault(item.WorkloadId),
            })],
        };
    }

    private WorkloadUpdateCohortReading ReadCohort(
        WorkloadGroup group,
        IReadOnlyList<WorkloadUpdateIntent> members)
    {
        var unaccounted = members
            .Where(member => WorkloadUpdateRowReading.Of(member, _pipeline).Standing
                == WorkloadUpdateRowStanding.Unaccounted)
            .Select(member => member.Id)
            .ToHashSet();
        return WorkloadUpdateCohortReading.Create(group, members, unaccounted);
    }

    private async Task<WorkloadUpdateAdvanceResult> EndAsync(
        WorkloadUpdateIntent intent, string error, string description, CancellationToken cancellationToken,
        WorkloadGroup? trackedGroup = null)
    {
        var now = _clock.UtcNow;
        foreach (var step in intent.Steps)
        {
            step.Advance(WorkloadUpdatePhase.RollbackFailed, WorkloadUpdateStepState.Failed, error, now);
        }

        intent.MarkRollbackFailed(error, now);
        if (intent.CohortId is not null && trackedGroup is not null)
        {
            await _intentRepository.SaveCohortDecisionAsync(intent, trackedGroup, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await _intentRepository.UpdateAsync(intent, cancellationToken).ConfigureAwait(false);
        }
        _logger.LogError("Update intent {IntentId} terminalized to RollbackFailed: {Error}", intent.Id, error);
        return WorkloadUpdateAdvanceResult.Advanced(intent.Id, intent.Phase, description);
    }

    private async Task<WorkloadUpdateAdvanceResult> RollBackNothingAsync(
        WorkloadUpdateIntent intent, CancellationToken cancellationToken, WorkloadGroup? trackedGroup = null)
    {
        var now = _clock.UtcNow;
        foreach (var step in intent.Steps)
        {
            step.Advance(WorkloadUpdatePhase.RolledBack, WorkloadUpdateStepState.RolledBack,
                "nothing had been applied", now);
        }

        intent.RecordStageJournal(new PipelineJournal([], PipelineDirection.Compensating, PipelineState.Compensated));
        intent.AdvanceTo(WorkloadUpdatePhase.RolledBack, now);
        if (intent.CohortId is not null && trackedGroup is not null)
        {
            await _intentRepository.SaveCohortDecisionAsync(intent, trackedGroup, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await _intentRepository.UpdateAsync(intent, cancellationToken).ConfigureAwait(false);
        }
        return WorkloadUpdateAdvanceResult.Advanced(intent.Id, intent.Phase, "abandoned before any stage ran");
    }

    private void Project(WorkloadUpdateIntent intent, PipelineTick tick)
    {
        var now = _clock.UtcNow;
        var journal = tick.Journal;

        switch (journal.State)
        {
            case PipelineState.Completed:
                foreach (var step in intent.Steps)
                {
                    step.Advance(WorkloadUpdatePhase.Completed, WorkloadUpdateStepState.Done, "update completed", now);
                }

                intent.Complete(now);
                return;

            case PipelineState.Compensated:
                foreach (var step in intent.Steps)
                {
                    step.Advance(WorkloadUpdatePhase.RolledBack, WorkloadUpdateStepState.RolledBack,
                        "restored to previous last-known-good", now);
                }

                intent.AdvanceTo(WorkloadUpdatePhase.RolledBack, now);
                return;

            case PipelineState.Failed:
                var error = tick.Detail ?? $"stage '{tick.StageId}' failed and the walk could not be unwound";
                foreach (var step in intent.Steps)
                {
                    step.Advance(WorkloadUpdatePhase.RollbackFailed, WorkloadUpdateStepState.Failed, error, now);
                }

                if (intent.Phase is not (WorkloadUpdatePhase.RollingBack or WorkloadUpdatePhase.SwappingModule))
                {
                    intent.Fail(error, now);
                }

                intent.MarkRollbackFailed(error, now);
                _logger.LogError("Update intent {IntentId} terminalized to RollbackFailed: {Error}", intent.Id, error);
                return;
        }

        if (journal.Direction == PipelineDirection.Compensating)
        {
            if (intent.Phase != WorkloadUpdatePhase.RollingBack)
            {
                _logger.LogWarning("Update intent {IntentId} is unwinding from stage '{StageId}': {Detail}",
                    intent.Id, tick.StageId, tick.Detail);
                intent.Fail(tick.Detail ?? $"stage '{tick.StageId}' failed", now);
            }

            return;
        }

        var projected = WorkloadUpdateRowReading.PhaseOf(WorkloadUpdateRowReading.NextForwardStage(journal, _pipeline));
        if (projected != intent.Phase)
        {
            intent.AdvanceTo(projected, now);
        }
    }

    private void WarnOnPassingThePointOfNoReturn(WorkloadUpdateIntent intent, PipelineJournal before, PipelineJournal after)
    {
        if (WorkloadUpdatePointsOfNoReturn.IrreversibleIn(before, _pipeline) is not null
            || WorkloadUpdatePointsOfNoReturn.IrreversibleIn(after, _pipeline) is not { } passed)
        {
            return;
        }

        _logger.LogWarning(
            "Update intent {IntentId} (instance {InstanceId}, {From} → {To}) entered stage '{StageId}' and can no longer be rolled back: {Reason}",
            intent.Id, intent.GroupInstanceId, intent.FromVersion ?? "(adopted)", intent.ToVersion,
            passed.Point.StageId, passed.Point.Reason);
    }

    private static string Describe(PipelineTick tick)
        => tick.Detail is { Length: > 0 } detail
            ? $"{tick.StageId}: {detail}"
            : $"{tick.StageId}: {tick.Result}";

    private sealed class WorkloadClockTimeProvider(IWorkloadClock clock) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => clock.UtcNow;
    }
}
