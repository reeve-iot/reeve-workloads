using System.Diagnostics.CodeAnalysis;
using System.Linq;
using Microsoft.Extensions.Logging;
using Reeve.Workloads.Abstractions;
using Reeve.Workloads.Abstractions.Domain.DesiredSpec;
using Reeve.Workloads.Abstractions.Domain.Entities;
using Reeve.Workloads.Abstractions.Domain.Entities.Documents;
using Reeve.Workloads.Abstractions.Providers;
using Reeve.Workloads.Abstractions.Update;

namespace Reeve.Workloads.Core;

/// <summary>Runs on the leader before its boot reconcile pass: it never re-applies a spec, it only terminalizes an
/// intent that cannot be resumed, and unwinds an instance stranded mid-switch (in <c>Switching</c> for longer than
/// a switch request takes), so neither wedges the reconciler.</summary>
public sealed class WorkloadIntentResumer : IWorkloadIntentResumer
{
    private readonly IWorkloadUpdateIntentRepository _intentRepository;

    private readonly IWorkloadClock _clock;

    private readonly ILogger<WorkloadIntentResumer> _logger;

    private readonly IWorkloadInstanceRepository _instanceRepository;
    private readonly IWorkloadGroupRepository _groupRepository;
    private readonly IWorkloadProviderRegistry _providerRegistry;

    /// <summary>Creates the resumer, able to terminalize cohort members under the group-row token.</summary>
    public WorkloadIntentResumer(
        IWorkloadUpdateIntentRepository intentRepository,
        IWorkloadClock clock,
        ILogger<WorkloadIntentResumer> logger,
        IWorkloadInstanceRepository instanceRepository,
        IWorkloadGroupRepository groupRepository,
        IWorkloadProviderRegistry providerRegistry)
    {
        _providerRegistry = providerRegistry ?? throw new ArgumentNullException(nameof(providerRegistry));
        _intentRepository = intentRepository ?? throw new ArgumentNullException(nameof(intentRepository));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _instanceRepository = instanceRepository ?? throw new ArgumentNullException(nameof(instanceRepository));
        _groupRepository = groupRepository ?? throw new ArgumentNullException(nameof(groupRepository));
    }

    public async Task ResumeOpenIntentsAsync(CancellationToken cancellationToken = default)
    {
        await new WorkloadStrandedSwitchUnwind(_instanceRepository, _clock, _logger)
            .UnwindAsync(cancellationToken).ConfigureAwait(false);

        IReadOnlyList<WorkloadUpdateIntent> open;
        try
        {
            open = await _intentRepository.GetOpenIntentsAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Failed to load open workload update intents for boot-resume; skipping resume pass.");
            return;
        }

        if (open.Count == 0)
        {
            _logger.LogDebug("No open workload update intents to resume.");
            return;
        }

        foreach (var intent in open)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await ProcessOneAsync(intent, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task ProcessOneAsync(WorkloadUpdateIntent intent, CancellationToken cancellationToken)
    {
        try
        {
            if (await IsInstanceDisownedAsync(intent, cancellationToken).ConfigureAwait(false))
            {
                TerminalizeToRollbackFailed(intent,
                    "the target workload instance was switched to the manual provider (ExternallyManaged) — the managed update is moot");
                await SaveDecisionOrIntentAsync(intent, cancellationToken).ConfigureAwait(false);

                _logger.LogWarning(
                    "Terminalized workload update intent {IntentId} for plugin instance {GroupInstanceId} on boot-resume: " +
                    "its workload instance(s) were switched to the manual provider (ExternallyManaged) — skipping resume so " +
                    "the one-active-intent filtered unique index cannot wedge.",
                    intent.Id, intent.GroupInstanceId);
                return;
            }

            var noTurnTargetNeeded = intent.CohortId is not null && !HasCompletedForwardParams(intent);
            var nonResumableReason = ClassifyNonResumable(intent, noTurnTargetNeeded);
            if (nonResumableReason is null)
            {
                _logger.LogInformation(
                    "Resuming open workload update intent {IntentId} for instance {InstanceId} at phase {Phase} " +
                    "(target/previous specs intact).",
                    intent.Id, intent.GroupInstanceId, intent.Phase);
                return;
            }

            TerminalizeToRollbackFailed(intent, nonResumableReason);
            await SaveDecisionOrIntentAsync(intent, cancellationToken).ConfigureAwait(false);

            _logger.LogWarning(
                "Marked workload update intent {IntentId} for instance {InstanceId} as RollbackFailed on boot-resume: {Reason}",
                intent.Id, intent.GroupInstanceId, nonResumableReason);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex,
                "Failed to resume/terminalize workload update intent {IntentId} for instance {InstanceId}; leaving it as-is.",
                intent.Id, intent.GroupInstanceId);
        }
    }

    private async Task<bool> IsInstanceDisownedAsync(WorkloadUpdateIntent intent, CancellationToken cancellationToken)
    {
        var instances = await _instanceRepository
            .ListByPluginInstanceAsync(intent.GroupInstanceId, cancellationToken)
            .ConfigureAwait(false);

        return instances.Count > 0
            && instances.All(i => i.State == WorkloadInstanceState.ExternallyManaged
                && i.IsOwnershipReleased(_providerRegistry));
    }

    private static string? ClassifyNonResumable(WorkloadUpdateIntent intent, bool noTurnTargetNeeded = false)
    {
        var target = intent.ReadTargetSpecs();
        var previous = intent.ReadPreviousSpecs();

        if (target.Verdict == WorkloadDocumentVerdict.Absent && !noTurnTargetNeeded)
        {
            return "no target specs recorded (nothing to converge on)";
        }

        if (previous.Verdict == WorkloadDocumentVerdict.Absent)
        {
            return "no previous specs recorded (rollback would be impossible)";
        }

        if (target.Verdict != WorkloadDocumentVerdict.Absent)
        {
            if (target.Value is not { } targetSpecs)
            {
                return "target specs are corrupt/undeserializable";
            }

            if (targetSpecs.Count == 0)
            {
                return "target spec map is empty (nothing to converge on)";
            }
        }

        if (previous.Value is null)
        {
            return "previous specs are corrupt/undeserializable (rollback would be impossible)";
        }

        return null;
    }

    private static bool HasCompletedForwardParams(WorkloadUpdateIntent intent)
        => intent.ReadStageJournal() is
            { Verdict: WorkloadDocumentVerdict.Value, Value.Entries: var entries }
            && entries.Any(entry => entry.StageId == WorkloadUpdateStages.Params
                && entry.Direction == Reeve.Pipeline.PipelineDirection.Forward
                && entry.State == Reeve.Pipeline.StageEntryState.Done);

    private async Task SaveDecisionOrIntentAsync(WorkloadUpdateIntent intent, CancellationToken cancellationToken)
    {
        if (intent.CohortId is null)
        {
            await _intentRepository.UpdateAsync(intent, cancellationToken).ConfigureAwait(false);
            return;
        }

        var instances = await _instanceRepository.ListByPluginInstanceAsync(intent.GroupInstanceId, cancellationToken)
            .ConfigureAwait(false);
        var groupId = instances.FirstOrDefault()?.GroupId
            ?? throw new InvalidOperationException($"Cannot find group id for cohort member {intent.GroupInstanceId}.");
        var group = await _groupRepository.GetAsync(groupId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Cannot find group '{groupId}' for cohort member {intent.GroupInstanceId}.");
        await _intentRepository.SaveCohortDecisionAsync(intent, group, cancellationToken).ConfigureAwait(false);
    }

    private void TerminalizeToRollbackFailed(WorkloadUpdateIntent intent, string reason)
    {
        var now = _clock.UtcNow;
        var error = $"Boot-resume: intent not resumable — {reason}.";

        switch (intent.Phase)
        {
            case WorkloadUpdatePhase.RollingBack:
                intent.MarkRollbackFailed(error, now);
                return;

            case WorkloadUpdatePhase.SwappingModule:
                intent.MarkSwappingModuleUnresumable(error, now);
                return;

            default:
                intent.Fail(error, now);
                intent.MarkRollbackFailed(error, now);
                return;
        }
    }
}
