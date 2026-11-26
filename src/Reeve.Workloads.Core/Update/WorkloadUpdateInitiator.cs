using Microsoft.Extensions.Logging;
using Reeve.Pipeline;
using Reeve.Workloads.Abstractions;
using Reeve.Workloads.Abstractions.Domain.Entities;
using Reeve.Workloads.Abstractions.Domain.Network;
using Reeve.Workloads.Abstractions.Update;
using Reeve.Workloads.Domain.DesiredSpec;

namespace Reeve.Workloads.Core.Update;

internal sealed class WorkloadUpdateInitiator : IWorkloadUpdateInitiator, IWorkloadUpdateCohortInitiator
{
    private readonly IWorkloadUpdateIntentRepository _intentRepository;
    private readonly IWorkloadUpdatePlanner _planner;
    private readonly IWorkloadInstanceRepository _instanceRepository;
    private readonly IWorkloadUpdateReconcileNudge _nudge;
    private readonly Pipeline<WorkloadUpdateStageContext> _pipeline;
    private readonly IWorkloadClock _clock;
    private readonly ILogger<WorkloadUpdateInitiator> _logger;
    private readonly IWorkloadGroupRepository? _groupRepository;

    public WorkloadUpdateInitiator(
        IWorkloadUpdateIntentRepository intentRepository,
        IWorkloadUpdatePlanner planner,
        IWorkloadInstanceRepository instanceRepository,
        IWorkloadUpdateReconcileNudge nudge,
        Pipeline<WorkloadUpdateStageContext> pipeline,
        IWorkloadClock clock,
        ILogger<WorkloadUpdateInitiator> logger)
        : this(intentRepository, planner, instanceRepository, nudge, pipeline, clock, logger, null)
    {
    }

    public WorkloadUpdateInitiator(
        IWorkloadUpdateIntentRepository intentRepository,
        IWorkloadUpdatePlanner planner,
        IWorkloadInstanceRepository instanceRepository,
        IWorkloadUpdateReconcileNudge nudge,
        Pipeline<WorkloadUpdateStageContext> pipeline,
        IWorkloadClock clock,
        ILogger<WorkloadUpdateInitiator> logger,
        IWorkloadGroupRepository? groupRepository)
    {
        _intentRepository = intentRepository ?? throw new ArgumentNullException(nameof(intentRepository));
        _planner = planner ?? throw new ArgumentNullException(nameof(planner));
        _instanceRepository = instanceRepository ?? throw new ArgumentNullException(nameof(instanceRepository));
        _nudge = nudge ?? throw new ArgumentNullException(nameof(nudge));
        _pipeline = pipeline ?? throw new ArgumentNullException(nameof(pipeline));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _groupRepository = groupRepository;
    }

    private static string? HeldMemberRefusal(IReadOnlyList<WorkloadInstance> rows)
    {
        var held = rows.Where(row => row.IsHeldByHost || row.IsHostHoldPending).Select(row => row.WorkloadId).ToList();
        return held.Count == 0
            ? null
            : $"Workload(s) {string.Join(", ", held)} of instance {rows[0].GroupInstanceId} are held suspended by the host "
                + "(the plugin instance is disabled or its consent withdrawn); enable it or restore the consent, then update.";
    }

    private static bool SameParameters(
        IReadOnlyDictionary<string, string?>? left, IReadOnlyDictionary<string, string?>? right)
    {
        if (left is null or { Count: 0 })
        {
            return right is null or { Count: 0 };
        }

        if (right is null || right.Count != left.Count)
        {
            return false;
        }

        foreach (var (name, value) in left)
        {
            if (!right.TryGetValue(name, out var other) || !string.Equals(value, other, StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    public async Task<WorkloadUpdateInitiationResult> InitiateAsync(
        WorkloadUpdateInitiationRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.CandidateVersion);
        ArgumentNullException.ThrowIfNull(request.Precondition);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Precondition.RequirementsFingerprint, nameof(request));

        var pluginInstanceId = request.PluginInstanceId;
        var candidateVersion = request.CandidateVersion;

        var instanceRows = await _instanceRepository.ListByPluginInstanceAsync(pluginInstanceId, cancellationToken)
            .ConfigureAwait(false);
        var groupId = instanceRows.FirstOrDefault()?.GroupId;
        if (groupId is not null)
        {
            var groupInstanceIds = (await _instanceRepository.ListAllAsync(cancellationToken).ConfigureAwait(false))
                .Where(row => string.Equals(row.GroupId, groupId, StringComparison.OrdinalIgnoreCase))
                .Select(row => row.GroupInstanceId)
                .ToHashSet();
            var cohortIntent = (await _intentRepository.GetOpenIntentsAsync(cancellationToken).ConfigureAwait(false))
                .FirstOrDefault(open => open.CohortId is not null && groupInstanceIds.Contains(open.GroupInstanceId));
            if (cohortIntent is not null)
            {
                return WorkloadUpdateInitiationResult.Refused(
                    $"Group '{groupId}' has update cohort {cohortIntent.CohortId} in progress; a single-instance update cannot join it.");
            }
        }

        var active = await _intentRepository.GetActiveForInstanceAsync(pluginInstanceId, cancellationToken).ConfigureAwait(false);
        if (active is not null)
        {
            var differ = !SameParameters(request.OperatorParameters, active.ReadOperatorParameters());
            _logger.LogInformation(
                "Update of instance {InstanceId} to {Version} rejected — intent {ActiveIntentId} already in progress (stage {Stage}, {Direction}, submitted parameters differ: {Differ}).",
                pluginInstanceId, candidateVersion, active.Id,
                WorkloadUpdateRowReading.Of(active, _pipeline).Journal is { Entries: [.., var last] } ? last.StageId : "(none)",
                active.Direction, differ);
            return WorkloadUpdateInitiationResult.AlreadyInProgress(active.Id, differ);
        }

        if (HeldMemberRefusal(instanceRows) is { } held)
        {
            return WorkloadUpdateInitiationResult.Refused(held);
        }

        var intentId = Guid.NewGuid();
        var preparation = await PrepareMemberAsync(
            pluginInstanceId, candidateVersion, intentId, request.OperatorParameters,
            request.AdoptDeclaredResourceWorkloadIds, request.Precondition, null, null, cancellationToken)
            .ConfigureAwait(false);
        if (preparation.Message is not null)
        {
            return preparation.Outcome == WorkloadUpdateInitiationOutcome.Refused
                ? WorkloadUpdateInitiationResult.Refused(preparation.Message)
                : WorkloadUpdateInitiationResult.PreconditionFailed(preparation.Message);
        }

        var plan = preparation.Plan!;
        var rows = preparation.Rows!.ToDictionary(row => row.WorkloadId, StringComparer.Ordinal);

        var now = _clock.UtcNow;

        var pinned = request.Precondition.AttachmentPins
            .Select(entry =>
            {
                var row = rows[entry.Key];
                row.PinAttachment(entry.Value, now);
                return (Row: row, Pin: entry.Value);
            })
            .ToList();
        for (var index = 0; index < pinned.Count; index++)
        {
            await PinAttachmentAsync(pinned, index, now, cancellationToken).ConfigureAwait(false);
        }

        var intent = WorkloadUpdateIntent.Create(intentId, pluginInstanceId, plan.FromVersion, plan.ToVersion, now);
        intent.StageSnapshots(
            new WorkloadUpdateSnapshots
            {
                TargetSpecs = plan.TargetSpecs,
                PreviousSpecs = plan.PreviousSpecs,
                PreviousParameters = plan.PreviousStoredParameters,
                PreviousPasswordParameterNames = plan.PreviousPasswordParameterNames,
                CandidatePasswordParameterNames = plan.CandidateManifest.Workloads.ToDictionary(
                    workload => workload.Id,
                    workload => (IReadOnlyList<string>)WorkloadPasswordNames.For(workload).ToArray(),
                    StringComparer.Ordinal),
                FromDigests = plan.FromDigests,
                ToDigests = plan.ToDigests,
                OperatorParameters = request.OperatorParameters,
                AdoptedResourceWorkloadIds = request.AdoptDeclaredResourceWorkloadIds?.ToList(),
            },
            now);

        intent.RecordStageJournal(Reeve.Pipeline.PipelineJournal.New());

        foreach (var item in plan.Workloads)
        {
            intent.AddStep(WorkloadUpdateIntentStep.Create(Guid.NewGuid(), intent.Id, item.WorkloadId, now));
        }

        await _intentRepository.AddAsync(intent, cancellationToken).ConfigureAwait(false);
        await _nudge.NudgeAsync(pluginInstanceId, WorkloadUpdateNudgeReasons.Initiated, cancellationToken).ConfigureAwait(false);

        _logger.LogInformation(
            "Staged workload update intent {IntentId} for instance {InstanceId}: {From} → {To} ({WorkloadCount} workload(s), {PinCount} pin(s), actor={Actor}).",
            intent.Id, pluginInstanceId, plan.FromVersion ?? "(adopted)", plan.ToVersion, plan.Workloads.Count,
            request.Precondition.AttachmentPins.Count, request.ActorUserId);

        return WorkloadUpdateInitiationResult.Started(intent.Id, WorkloadUpdatePointsOfNoReturn.Of(_pipeline));
    }

    private async Task<MemberPreparation> PrepareMemberAsync(
        Guid groupInstanceId,
        string candidateVersion,
        Guid intentId,
        IReadOnlyDictionary<string, string?>? operatorParameters,
        IReadOnlyCollection<string>? adoptedResourceWorkloadIds,
        WorkloadWritePrecondition precondition,
        IReadOnlyList<WorkloadInstance>? knownRows,
        WorkloadUpdatePlanOrigin? origin,
        CancellationToken cancellationToken)
    {
        WorkloadUpdatePlan plan;
        try
        {
            plan = await _planner.BuildPlanAsync(groupInstanceId, candidateVersion, intentId,
                operatorParameters, adoptedResourceWorkloadIds, origin, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is InvalidOperationException or WorkloadParameterResolutionException)
        {
            return new(null, null, WorkloadUpdateInitiationOutcome.Refused, ex.Message);
        }

        if (!string.Equals(plan.RequirementsFingerprint, precondition.RequirementsFingerprint, StringComparison.Ordinal))
        {
            return new(null, null, WorkloadUpdateInitiationOutcome.PreconditionFailed,
                $"The requirements of updating '{plan.GroupId}' to {candidateVersion} changed since they were judged; nothing was written.");
        }

        var rows = knownRows ?? await _instanceRepository.ListByPluginInstanceAsync(groupInstanceId, cancellationToken)
            .ConfigureAwait(false);
        var rowIds = rows.Select(row => row.WorkloadId).ToHashSet(StringComparer.Ordinal);
        var unpinnable = precondition.AttachmentPins.Keys.Where(id => !rowIds.Contains(id)).ToList();
        if (unpinnable.Count > 0)
        {
            return new(null, null, WorkloadUpdateInitiationOutcome.PreconditionFailed,
                $"A pin names workload(s) '{string.Join("', '", unpinnable)}' with no row on this instance; nothing was written.");
        }

        return new(plan, rows, WorkloadUpdateInitiationOutcome.Started, null);
    }

    private sealed record MemberPreparation(
        WorkloadUpdatePlan? Plan,
        IReadOnlyList<WorkloadInstance>? Rows,
        WorkloadUpdateInitiationOutcome Outcome,
        string? Message);

    public async Task<WorkloadUpdateCohortInitiationResult> InitiateCohortAsync(
        WorkloadUpdateCohortInitiationRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!_intentRepository.SupportsCohorts)
        {
            return CohortResult(WorkloadUpdateInitiationOutcome.Refused,
                "The configured intent store does not support update cohorts.");
        }

        if (string.IsNullOrWhiteSpace(request.CandidateVersion))
        {
            return CohortResult(WorkloadUpdateInitiationOutcome.Refused, "A candidate version is required.");
        }

        if (request.Members is null || request.Members.Count == 0)
        {
            return CohortResult(WorkloadUpdateInitiationOutcome.Refused, "An update cohort must have at least one member.");
        }

        if (request.Members.Any(member => member is null || member.GroupInstanceId == Guid.Empty
            || member.Precondition is null || string.IsNullOrWhiteSpace(member.Precondition.RequirementsFingerprint)))
        {
            return CohortResult(WorkloadUpdateInitiationOutcome.Refused,
                "Every cohort member needs an instance id and a non-empty requirements fingerprint.");
        }

        if (request.Members.Select(member => member.GroupInstanceId).Distinct().Count() != request.Members.Count)
        {
            return CohortResult(WorkloadUpdateInitiationOutcome.Refused,
                "An update cohort cannot contain the same group instance more than once.");
        }

        var rowsByMember = new Dictionary<Guid, IReadOnlyList<WorkloadInstance>>();
        string? groupId = null;
        foreach (var member in request.Members)
        {
            var rows = await _instanceRepository.ListByPluginInstanceAsync(member.GroupInstanceId, cancellationToken)
                .ConfigureAwait(false);
            if (rows.Count == 0)
            {
                return CohortResult(WorkloadUpdateInitiationOutcome.Refused,
                    $"Instance {member.GroupInstanceId} has no workload rows.", member.GroupInstanceId);
            }

            var memberGroupIds = rows.Select(row => row.GroupId).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (memberGroupIds.Count != 1 || (groupId is not null
                && !string.Equals(groupId, memberGroupIds[0], StringComparison.OrdinalIgnoreCase)))
            {
                return CohortResult(WorkloadUpdateInitiationOutcome.Refused,
                    "Every cohort member must belong to the same group.", member.GroupInstanceId);
            }

            if (HeldMemberRefusal(rows) is { } held)
            {
                return CohortResult(WorkloadUpdateInitiationOutcome.Refused, held, member.GroupInstanceId);
            }

            groupId ??= memberGroupIds[0];
            rowsByMember.Add(member.GroupInstanceId, rows);
        }

        var group = _groupRepository is null
            ? null
            : await _groupRepository.GetAsync(groupId!, cancellationToken).ConfigureAwait(false);
        if (group is null)
        {
            return CohortResult(WorkloadUpdateInitiationOutcome.Refused,
                $"Group '{groupId}' was not found.");
        }

        var groupInstanceIds = (await _instanceRepository.ListAllAsync(cancellationToken).ConfigureAwait(false))
            .Where(row => string.Equals(row.GroupId, groupId, StringComparison.OrdinalIgnoreCase))
            .Select(row => row.GroupInstanceId)
            .ToHashSet();
        var requestedIds = request.Members.Select(member => member.GroupInstanceId).ToHashSet();
        if (!groupInstanceIds.SetEquals(requestedIds))
        {
            var missing = groupInstanceIds.Except(requestedIds).Select(id => id.ToString()).Order(StringComparer.Ordinal);
            var extra = requestedIds.Except(groupInstanceIds).Select(id => id.ToString()).Order(StringComparer.Ordinal);
            return CohortResult(WorkloadUpdateInitiationOutcome.Refused,
                $"Cohort coverage for group '{groupId}' is incomplete; missing [{string.Join(", ", missing)}], extra [{string.Join(", ", extra)}].");
        }

        var open = (await _intentRepository.GetOpenIntentsAsync(cancellationToken).ConfigureAwait(false))
            .Where(intent => groupInstanceIds.Contains(intent.GroupInstanceId))
            .ToList();
        if (open.Count > 0)
        {
            var intents = open.OrderBy(intent => intent.CohortOrdinal ?? int.MaxValue)
                .ThenBy(intent => intent.GroupInstanceId).ToList();
            var sameCohort = intents.All(intent => intent.CohortId is not null)
                && intents.Select(intent => intent.CohortId).Distinct().Count() == 1
                && intents.Select(intent => intent.ToVersion).Distinct(StringComparer.Ordinal).Count() == 1
                && string.Equals(intents[0].ToVersion, request.CandidateVersion, StringComparison.Ordinal)
                && intents.Count == request.Members.Count
                && intents.Select(intent => intent.GroupInstanceId).SequenceEqual(request.Members.Select(member => member.GroupInstanceId));
            var differ = request.Members.Any(member =>
            {
                var intent = intents.FirstOrDefault(item => item.GroupInstanceId == member.GroupInstanceId);
                return intent is not null && !SameParameters(member.OperatorParameters, intent.ReadOperatorParameters());
            });
            return new WorkloadUpdateCohortInitiationResult
            {
                Outcome = WorkloadUpdateInitiationOutcome.AlreadyInProgress,
                CohortId = sameCohort ? intents[0].CohortId : null,
                Members = [.. intents.Select((intent, index) => new WorkloadUpdateCohortMemberIntent(
                    intent.GroupInstanceId, intent.Id, intent.CohortOrdinal ?? index))],
                SubmittedParametersDiffer = differ,
            };
        }

        var from = group.InstalledVersion;
        if (from is null)
        {
            return CohortResult(WorkloadUpdateInitiationOutcome.Refused,
                $"Group '{groupId}' has no installed version; its cohort cannot be safely rolled back.");
        }

        if (string.Equals(from, request.CandidateVersion, StringComparison.Ordinal))
        {
            return CohortResult(WorkloadUpdateInitiationOutcome.Refused,
                $"Group '{groupId}' is already at version {request.CandidateVersion}.");
        }

        var prepared = new List<(WorkloadUpdateCohortMember Member, Guid IntentId, WorkloadUpdatePlan Plan,
            IReadOnlyList<WorkloadInstance> Rows)>();
        foreach (var member in request.Members)
        {
            var intentId = Guid.NewGuid();
            var preparation = await PrepareMemberAsync(
                member.GroupInstanceId, request.CandidateVersion, intentId, member.OperatorParameters,
                member.AdoptDeclaredResourceWorkloadIds, member.Precondition, rowsByMember[member.GroupInstanceId],
                new WorkloadUpdatePlanOrigin(from), cancellationToken).ConfigureAwait(false);
            if (preparation.Message is not null)
            {
                return CohortResult(preparation.Outcome, preparation.Message, member.GroupInstanceId);
            }

            prepared.Add((member, intentId, preparation.Plan!, preparation.Rows!));
        }

        var now = _clock.UtcNow;
        var allPins = new List<(WorkloadInstance Row, WorkloadAttachmentPin Pin)>();
        foreach (var item in prepared)
        {
            var rowByWorkload = item.Rows.ToDictionary(row => row.WorkloadId, StringComparer.Ordinal);
            foreach (var (workloadId, pin) in item.Member.Precondition.AttachmentPins)
            {
                var row = rowByWorkload[workloadId];
                row.PinAttachment(pin, now);
                allPins.Add((row, pin));
            }
        }

        for (var index = 0; index < allPins.Count; index++)
        {
            await PinAttachmentAsync(allPins, index, now, cancellationToken).ConfigureAwait(false);
        }

        var cohortId = Guid.NewGuid();
        var intentsToAdd = new List<WorkloadUpdateIntent>();
        for (var ordinal = 0; ordinal < prepared.Count; ordinal++)
        {
            var item = prepared[ordinal];
            var intent = WorkloadUpdateIntent.Create(item.IntentId, item.Member.GroupInstanceId,
                from, item.Plan.ToVersion, now, new WorkloadUpdateCohortPlace(cohortId, ordinal, prepared.Count));
            intent.StageSnapshots(new WorkloadUpdateSnapshots
            {
                PreviousSpecs = item.Plan.PreviousSpecs,
                PreviousParameters = item.Plan.PreviousStoredParameters,
                PreviousPasswordParameterNames = item.Plan.PreviousPasswordParameterNames,
                CandidatePasswordParameterNames = item.Plan.CandidateManifest.Workloads.ToDictionary(
                    workload => workload.Id,
                    workload => (IReadOnlyList<string>)WorkloadPasswordNames.For(workload).ToArray(),
                    StringComparer.Ordinal),
                FromDigests = item.Plan.FromDigests,
                ToDigests = item.Plan.ToDigests,
                OperatorParameters = item.Member.OperatorParameters,
                AdoptedResourceWorkloadIds = item.Member.AdoptDeclaredResourceWorkloadIds?.ToList(),
            }, now);
            intent.RecordStageJournal(PipelineJournal.New());
            foreach (var workload in item.Plan.Workloads)
            {
                intent.AddStep(WorkloadUpdateIntentStep.Create(Guid.NewGuid(), intent.Id, workload.WorkloadId, now));
            }

            intentsToAdd.Add(intent);
        }

        try
        {
            await _intentRepository.AddCohortAsync(intentsToAdd, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            var raced = (await _intentRepository.GetOpenIntentsAsync(cancellationToken).ConfigureAwait(false))
                .Where(intent => groupInstanceIds.Contains(intent.GroupInstanceId)).ToList();
            if (raced.Count == 0)
            {
                throw;
            }

            return new WorkloadUpdateCohortInitiationResult
            {
                Outcome = WorkloadUpdateInitiationOutcome.AlreadyInProgress,
                Members = [.. raced.OrderBy(intent => intent.CohortOrdinal ?? int.MaxValue)
                    .Select((intent, index) => new WorkloadUpdateCohortMemberIntent(
                        intent.GroupInstanceId, intent.Id, intent.CohortOrdinal ?? index))],
                CohortId = raced.Select(intent => intent.CohortId).Distinct().Count() == 1 ? raced[0].CohortId : null,
            };
        }

        foreach (var intent in intentsToAdd)
        {
            await _nudge.NudgeAsync(intent.GroupInstanceId, WorkloadUpdateNudgeReasons.Initiated, cancellationToken)
                .ConfigureAwait(false);
        }

        _logger.LogInformation(
            "Staged workload update cohort {CohortId} for group {GroupId}: {From} → {To} ({MemberCount} member(s), actor={Actor}).",
            cohortId, groupId, from, request.CandidateVersion, intentsToAdd.Count, request.ActorUserId);
        return new WorkloadUpdateCohortInitiationResult
        {
            Outcome = WorkloadUpdateInitiationOutcome.Started,
            CohortId = cohortId,
            Members = [.. intentsToAdd.Select(intent => new WorkloadUpdateCohortMemberIntent(
                intent.GroupInstanceId, intent.Id, intent.CohortOrdinal!.Value))],
            PointOfNoReturn = new WorkloadUpdatePointOfNoReturn(WorkloadUpdateStages.CommitVersion,
                "the cohort's version commit already landed; one instance cannot be moved back alone"),
        };
    }

    private static WorkloadUpdateCohortInitiationResult CohortResult(
        WorkloadUpdateInitiationOutcome outcome, string message, Guid? groupInstanceId = null)
        => new()
        {
            Outcome = outcome,
            RefusalMessage = message,
            RefusedGroupInstanceId = groupInstanceId,
        };

    private const int PinWriteAttempts = 3;

    private async Task PinAttachmentAsync(
        List<(WorkloadInstance Row, WorkloadAttachmentPin Pin)> pinned,
        int index,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await _instanceRepository.UpdateAsync(pinned[index].Row, cancellationToken).ConfigureAwait(false);
                return;
            }
            catch (WorkloadConcurrencyConflictException conflict)
                when (attempt < PinWriteAttempts && pinned.Exists(entry => entry.Row.Id == conflict.EntityId))
            {
                for (var pending = index; pending < pinned.Count; pending++)
                {
                    var (row, pin) = pinned[pending];
                    var fresh = await _instanceRepository.ReloadAsync(row.Id, cancellationToken).ConfigureAwait(false);
                    if (fresh is null)
                    {
                        throw;
                    }

                    fresh.PinAttachment(pin, nowUtc);
                    pinned[pending] = (fresh, pin);
                }

                _logger.LogDebug(
                    "Pinning attachments on instance {InstanceId} lost a write race on {LostId}; re-read and retrying ({Attempt}/{Attempts}).",
                    pinned[index].Row.Id, conflict.EntityId, attempt, PinWriteAttempts);
            }
        }
    }
}
