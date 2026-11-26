using Microsoft.Extensions.Logging;
using Reeve.Pipeline;
using Reeve.Workloads.Abstractions;
using Reeve.Workloads.Abstractions.Contracts;
using Reeve.Workloads.Abstractions.Domain;
using Reeve.Workloads.Abstractions.Domain.DesiredSpec;
using Reeve.Workloads.Abstractions.Domain.Entities;
using Reeve.Workloads.Abstractions.Domain.Entities.Documents;

using Reeve.Workloads.Abstractions.Update;
using Reeve.Workloads.Core;
using Reeve.Workloads.Domain;

namespace Reeve.Workloads.Core.Update;

internal sealed class WorkloadUpdateReadService : IWorkloadUpdateReadService
{
    public const int MaxRecentIntents = 50;

    private readonly IWorkloadUpdatePlanner _planner;
    private readonly IWorkloadUpdateIntentRepository _intentRepository;
    private readonly Pipeline<WorkloadUpdateStageContext> _pipeline;
    private readonly ILogger<WorkloadUpdateReadService> _logger;

    public WorkloadUpdateReadService(
        IWorkloadUpdatePlanner planner,
        IWorkloadUpdateIntentRepository intentRepository,
        Pipeline<WorkloadUpdateStageContext> pipeline,
        ILogger<WorkloadUpdateReadService> logger)
    {
        _planner = planner ?? throw new ArgumentNullException(nameof(planner));
        _intentRepository = intentRepository ?? throw new ArgumentNullException(nameof(intentRepository));
        _pipeline = pipeline ?? throw new ArgumentNullException(nameof(pipeline));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<WorkloadUpdateDiffResponse> GetDiffPreviewAsync(
        Guid pluginInstanceId,
        string candidateVersion,
        CancellationToken cancellationToken = default)
    {
        if (pluginInstanceId == Guid.Empty)
        {
            return WorkloadUpdateDiffResponse.Blocked("A plugin instance id is required.", pluginInstanceId, candidateVersion);
        }

        if (string.IsNullOrWhiteSpace(candidateVersion))
        {
            return WorkloadUpdateDiffResponse.Blocked("A candidate version is required.", pluginInstanceId, candidateVersion);
        }

        WorkloadUpdatePlan plan;
        WorkloadDeploymentDiff diff;
        try
        {
            plan = await _planner
                .BuildPlanAsync(pluginInstanceId, candidateVersion, intentId: null, operatorParameters: null,
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            diff = WorkloadDeploymentDiff.Compute(
                plan.PreviousManifest,
                plan.PreviousRequirements,
                plan.PreviousResolvedParameters,
                plan.CandidateManifest,
                plan.CandidateRequirements,
                plan.PreviousUnreadable);
        }
        catch (WorkloadHostRefusedException refused)
        {
            _logger.LogInformation(
                "Update-diff preview for instance {InstanceId} → {Version} is blocked: the host refused it — {Refusal}",
                pluginInstanceId, candidateVersion, refused.Reason);
            return WorkloadUpdateDiffResponse.Blocked(
                $"The host refused the update: {refused.Reason}", pluginInstanceId, candidateVersion);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex,
                "Update-diff preview for instance {InstanceId} → {Version} could not be computed; returning a blocked result (fail-closed).",
                pluginInstanceId, candidateVersion);
            return WorkloadUpdateDiffResponse.Blocked(
                "The update diff could not be computed. The candidate release could not be resolved — the update is blocked until it can be previewed.",
                pluginInstanceId, candidateVersion);
        }

        var workloads = diff.Workloads.Select(ToWorkloadDiffDto).ToList();

        return new WorkloadUpdateDiffResponse
        {
            Ok = true,
            GroupInstanceId = pluginInstanceId,
            FromVersion = plan.FromVersion,
            ToVersion = plan.ToVersion,
            Workloads = workloads,
            AddedWorkloadIds = diff.AddedWorkloadIds,
            RemovedWorkloadIds = diff.RemovedWorkloadIds,
            AnySecurityEscalation = diff.AnySecurityEscalation,
            AnyNewRequiredParameters = diff.AnyNewRequiredParameters,
            PointOfNoReturn = WorkloadUpdatePointsOfNoReturn.Of(_pipeline) is { } point
                ? new WorkloadUpdatePointOfNoReturnDto { StageId = point.StageId, Reason = point.Reason }
                : null,
        };
    }

    private static WorkloadUpdateWorkloadDiff ToWorkloadDiffDto(WorkloadDiff w) => new()
    {
        WorkloadId = w.WorkloadId,
        IsAdded = w.IsAdded,
        FromDigest = w.FromDigest,
        ToDigest = w.ToDigest,
        DigestChanged = w.DigestChanged,
        SecurityChange = w.RequirementsDiff.Security.ToString(),
        Escalations = w.RequirementsDiff.EscalatedFields,
        Narrowings = w.RequirementsDiff.NarrowedFields,
        InformationalChanges = w.RequirementsDiff.VolumeNotes,
        FromExtensions = w.FromExtensions,
        ToExtensions = w.ToExtensions,
        PreviousUnreadable = w.PreviousUnreadable,
        NewRequiredParameters = w.NewRequiredParameters,
        FromResources = ToResourceValues(w.FromResources),
        ToResources = ToResourceValues(w.ToResources),
        ResourcesChanged = w.ResourcesChanged,
        OperatorResourceOverrides = ToResourceValues(w.OperatorResourceOverrides),
    };

    private static WorkloadUpdateResourceValues? ToResourceValues(ResolvedResources? r)
        => r is null
            ? null
            : new WorkloadUpdateResourceValues
            {
                RequestsCpu = r.RequestsCpu,
                RequestsMemory = r.RequestsMemory,
                LimitsCpu = r.LimitsCpu,
                LimitsMemory = r.LimitsMemory,
            };

    public async Task<WorkloadUpdateStatusResponse> GetStatusAsync(
        Guid pluginInstanceId,
        int recentLimit = 10,
        CancellationToken cancellationToken = default)
    {
        var limit = Math.Clamp(recentLimit <= 0 ? 10 : recentLimit, 1, MaxRecentIntents);

        var intents = await _intentRepository
            .GetRecentForInstanceAsync(pluginInstanceId, limit, cancellationToken)
            .ConfigureAwait(false);

        var ordered = intents
            .OrderBy(i => i.IsTerminal)
            .ThenByDescending(i => i.CreatedAtUtc)
            .Select(ToIntentSummary)
            .ToList();

        var activeIntentId = intents.FirstOrDefault(i => i.IsActive)?.Id;

        return new WorkloadUpdateStatusResponse
        {
            GroupInstanceId = pluginInstanceId,
            ActiveIntentId = activeIntentId,
            Intents = ordered,
        };
    }

    private WorkloadUpdateIntentSummary ToIntentSummary(WorkloadUpdateIntent i) => new()
    {
        Id = i.Id,
        FromVersion = i.FromVersion,
        ToVersion = i.ToVersion,
        Phase = i.Phase.ToString(),
        CurrentStage = CurrentStageOf(i),
        Direction = i.Direction.ToString(),
        Outcome = i.Outcome.ToString(),
        IsTerminal = i.IsTerminal,
        Error = i.Error,
        WalkFault = i.WalkFault,
        WalkFaultSinceUtc = i.WalkFaultSinceUtc,
        CreatedAtUtc = i.CreatedAtUtc,
        PhaseEnteredAtUtc = i.PhaseEnteredAtUtc,
        CompletedAtUtc = i.CompletedAtUtc,
        StepCount = i.Steps.Count,
        CompletedStepCount = i.Steps.Count(s => s.Progress is WorkloadUpdateStepProgress.Done
            or WorkloadUpdateStepProgress.Undone
            or WorkloadUpdateStepProgress.Failed),
    };

    public async Task<WorkloadUpdateIntentResponse?> GetIntentAsync(
        Guid intentId,
        CancellationToken cancellationToken = default)
    {
        var intent = await _intentRepository.GetByIdAsync(intentId, cancellationToken).ConfigureAwait(false);
        if (intent is null)
        {
            return null;
        }

        return new WorkloadUpdateIntentResponse
        {
            Id = intent.Id,
            GroupInstanceId = intent.GroupInstanceId,
            FromVersion = intent.FromVersion,
            ToVersion = intent.ToVersion,
            Phase = intent.Phase.ToString(),
            CurrentStage = CurrentStageOf(intent),
            Direction = intent.Direction.ToString(),
            Outcome = intent.Outcome.ToString(),
            IsTerminal = intent.IsTerminal,
            Error = intent.Error,
            WalkFault = intent.WalkFault,
            WalkFaultSinceUtc = intent.WalkFaultSinceUtc,
            CreatedAtUtc = intent.CreatedAtUtc,
            PhaseEnteredAtUtc = intent.PhaseEnteredAtUtc,
            CompletedAtUtc = intent.CompletedAtUtc,
            FromDigests = intent.ReadFromDigests(),
            ToDigests = intent.ReadToDigests(),
            Steps = intent.Steps
                .OrderBy(s => s.WorkloadId, StringComparer.Ordinal)
                .Select(ToStepDto)
                .ToList(),
            Evidence = ParseEvidence(intent),
            PointOfNoReturn = PointOfNoReturnOf(intent),
        };
    }

    private string? CurrentStageOf(WorkloadUpdateIntent intent)
        => WorkloadUpdateRowReading.Of(intent, _pipeline).Journal is { Entries: [.., var last] }
            ? last.StageId
            : null;

    private WorkloadUpdatePointOfNoReturnDto? PointOfNoReturnOf(WorkloadUpdateIntent intent)
    {
        if (WorkloadUpdateRowReading.Of(intent, _pipeline).Journal is { } journal
            && WorkloadUpdatePointsOfNoReturn.PassedIn(journal, _pipeline) is { } passed)
        {
            return new WorkloadUpdatePointOfNoReturnDto
            {
                StageId = passed.Point.StageId,
                Reason = passed.Point.Reason,
                PassedAtUtc = passed.EnteredAtUtc,
            };
        }

        return WorkloadUpdatePointsOfNoReturn.Of(_pipeline) is { } ahead
            ? new WorkloadUpdatePointOfNoReturnDto { StageId = ahead.StageId, Reason = ahead.Reason }
            : null;
    }

    private static WorkloadUpdateStepDto ToStepDto(WorkloadUpdateIntentStep s) => new()
    {
        WorkloadId = s.WorkloadId,
        Step = s.Step.ToString(),
        State = s.State.ToString(),
        Progress = s.Progress.ToString(),
        Message = s.Message,
        AppliedSpecHash = s.AppliedSpecHash,
        UpdatedAtUtc = s.UpdatedAtUtc,
    };

    private WorkloadUpdateEvidenceResponse? ParseEvidence(WorkloadUpdateIntent intent)
    {
        if (intent.Outcome is not (WorkloadUpdateOutcome.RolledBack or WorkloadUpdateOutcome.RollbackFailed))
        {
            return null;
        }

        WorkloadUpdateEvidence? evidence;
        try
        {
            evidence = intent.ReadEvidence();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Update intent {IntentId} has unparseable evidence JSON; surfacing no evidence.", intent.Id);
            return null;
        }

        if (evidence is null)
        {
            return null;
        }

        return new WorkloadUpdateEvidenceResponse
        {
            ToVersion = evidence.ToVersion,
            CapturedAtUtc = evidence.CapturedAtUtc,
            Workloads = evidence.Workloads.Select(w => new WorkloadUpdateEvidenceWorkloadDto
            {
                WorkloadId = w.WorkloadId,
                PreviousContainerLogTail = w.PreviousContainerLogTail,
                RecentEvents = w.RecentEvents,
                Note = w.Note,
            }).ToList(),
        };
    }
}
