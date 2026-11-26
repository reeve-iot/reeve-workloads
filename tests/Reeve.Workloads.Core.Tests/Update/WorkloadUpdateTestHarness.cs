using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging.Abstractions;
using Reeve.Workloads.Abstractions;
using Reeve.Workloads.Abstractions.Contracts;
using Reeve.Workloads.Abstractions.Contracts.Deployment;
using Reeve.Workloads.Abstractions.Domain;
using Reeve.Workloads.Abstractions.Domain.DesiredSpec;
using Reeve.Workloads.Abstractions.Domain.Entities;
using Reeve.Workloads.Abstractions.Domain.Network;
using Reeve.Workloads.Abstractions.Providers;
using Reeve.Workloads.Abstractions.Update;
using Reeve.Workloads.Core;
using Reeve.Workloads.Core.Tests.Groups;
using Reeve.Workloads.Core.Update;
using Reeve.Workloads.Core.Update.Stages;
using Reeve.Workloads.DeploymentFixtures;
using Reeve.Workloads.Core.Tests;
using Reeve.Workloads.Providers.Kubernetes;

namespace Reeve.Workloads.Core.Tests.Update;

public sealed class MutableClock(DateTimeOffset now) : IWorkloadClock
{
    public DateTimeOffset UtcNow { get; set; } = now;

    public void Advance(TimeSpan by) => UtcNow += by;
}

public sealed class InMemoryIntentRepository : IWorkloadUpdateIntentRepository
{
    private readonly List<WorkloadUpdateIntent> _intents = [];

    public int UpdateCallCount { get; private set; }
    public bool SupportsCohorts { get; set; }
    public int CohortDecisionSaveCount { get; private set; }
    public Action<IReadOnlyList<WorkloadUpdateIntent>>? BeforeAddCohort { get; set; }
    public Action<Guid>? BeforeGetCohort { get; set; }
    public Action<WorkloadUpdateIntent, WorkloadGroup>? BeforeSaveCohortDecision { get; set; }

    public Task<IReadOnlyList<WorkloadUpdateIntent>> GetCohortAsync(Guid cohortId, CancellationToken ct = default)
    {
        BeforeGetCohort?.Invoke(cohortId);
        return Task.FromResult<IReadOnlyList<WorkloadUpdateIntent>>(
            _intents.Where(intent => intent.CohortId == cohortId).OrderBy(intent => intent.CohortOrdinal).ToList());
    }

    public Task AddCohortAsync(IReadOnlyList<WorkloadUpdateIntent> intents, CancellationToken ct = default)
    {
        BeforeAddCohort?.Invoke(intents);
        if (intents.Any(candidate => _intents.Any(existing => existing.GroupInstanceId == candidate.GroupInstanceId && existing.IsActive)))
        {
            throw new InvalidOperationException("An active intent already exists for this instance (unique-index violation).");
        }
        _intents.AddRange(intents);
        return Task.CompletedTask;
    }

    public Task SaveCohortDecisionAsync(WorkloadUpdateIntent intent, WorkloadGroup group, CancellationToken ct = default)
    {
        BeforeSaveCohortDecision?.Invoke(intent, group);
        CohortDecisionSaveCount++;
        UpdateCallCount++;
        SavedJournals.Add(intent.ReadStageJournal().Value);
        if (!_intents.Contains(intent))
        {
            _intents.Add(intent);
        }
        return Task.CompletedTask;
    }

    public IReadOnlyList<WorkloadUpdateIntent> All => _intents;

    public Task<WorkloadUpdateIntent?> GetActiveForInstanceAsync(Guid pluginInstanceId, CancellationToken cancellationToken = default)
        => Task.FromResult(_intents.FirstOrDefault(i => i.GroupInstanceId == pluginInstanceId && i.IsActive));

    public Task<Guid?> GetActiveIntentIdAsync(Guid pluginInstanceId, CancellationToken cancellationToken = default)
        => Task.FromResult(_intents.FirstOrDefault(i => i.GroupInstanceId == pluginInstanceId && i.IsActive)?.Id);

    public Task<IReadOnlyList<WorkloadUpdateIntent>> GetOpenIntentsAsync(CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<WorkloadUpdateIntent>>(_intents.Where(i => i.IsActive).ToList());

    public Task<WorkloadUpdateIntent?> GetLatestForInstanceAsync(Guid pluginInstanceId, CancellationToken cancellationToken = default)
        => Task.FromResult(_intents.Where(i => i.GroupInstanceId == pluginInstanceId).LastOrDefault());

    public Task<WorkloadUpdateIntent?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
        => Task.FromResult(_intents.FirstOrDefault(i => i.Id == id));

    public Task<IReadOnlyList<WorkloadUpdateIntent>> GetRecentForInstanceAsync(Guid pluginInstanceId, int limit, CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<WorkloadUpdateIntent>>(limit <= 0
            ? []
            : _intents
                .Where(i => i.GroupInstanceId == pluginInstanceId)
                .OrderByDescending(i => i.CreatedAtUtc)
                .Take(limit)
                .ToList());

    public Task AddAsync(WorkloadUpdateIntent intent, CancellationToken cancellationToken = default)
    {
        if (_intents.Any(i => i.GroupInstanceId == intent.GroupInstanceId && i.IsActive))
        {
            throw new InvalidOperationException("An active intent already exists for this instance (unique-index violation).");
        }
        _intents.Add(intent);
        return Task.CompletedTask;
    }

    public List<Reeve.Pipeline.PipelineJournal?> SavedJournals { get; } = [];

    public Func<WorkloadUpdateIntent, Exception?>? FailUpdate { get; set; }

    public Task UpdateAsync(WorkloadUpdateIntent intent, CancellationToken cancellationToken = default)
    {
        if (FailUpdate?.Invoke(intent) is { } lost)
        {
            throw lost;
        }

        UpdateCallCount++;
        SavedJournals.Add(intent.ReadStageJournal().Value);
        if (!_intents.Contains(intent))
        {
            _intents.Add(intent);
        }
        return Task.CompletedTask;
    }
}

internal sealed class ScriptedPlanner(WorkloadUpdatePlan plan) : IWorkloadUpdatePlanner
{
    public int BuildCount { get; private set; }
    public Func<WorkloadUpdatePlan>? Override { get; set; }
    public Dictionary<Guid, WorkloadUpdatePlan> PlansByInstance { get; } = [];

    public IWorkloadUpdatePlanner? Real { get; set; }

    public IReadOnlyDictionary<string, string?>? LastSubmittedParameters { get; private set; }

    public IReadOnlyCollection<string>? LastAdoptedResourceWorkloadIds { get; private set; }

    public WorkloadUpdatePlanOrigin? LastOrigin { get; private set; }
    public List<WorkloadUpdatePlanOrigin?> Origins { get; } = [];

    public void Forget()
    {
        LastSubmittedParameters = null;
        LastAdoptedResourceWorkloadIds = null;
    }

    public Task<WorkloadUpdatePlan> BuildPlanAsync(
        Guid pluginInstanceId,
        string candidateVersion,
        Guid? intentId,
        IReadOnlyDictionary<string, string?>? operatorParameters = null,
        IReadOnlyCollection<string>? adoptDeclaredResourceWorkloadIds = null,
        WorkloadUpdatePlanOrigin? origin = null,
        CancellationToken cancellationToken = default)
    {
        BuildCount++;
        LastOrigin = origin;
        Origins.Add(origin);
        LastSubmittedParameters = operatorParameters;
        LastAdoptedResourceWorkloadIds = adoptDeclaredResourceWorkloadIds;
        if (Real is not null)
        {
            return Real.BuildPlanAsync(
                pluginInstanceId, candidateVersion, intentId, operatorParameters, adoptDeclaredResourceWorkloadIds,
                origin, cancellationToken);
        }

        return Task.FromResult(PlansByInstance.TryGetValue(pluginInstanceId, out var byInstance)
            ? byInstance
            : Override?.Invoke() ?? plan);
    }
}

internal sealed class IntentAwarePlanner(Func<Guid, WorkloadUpdatePlan> planFor) : IWorkloadUpdatePlanner
{
    public Task<WorkloadUpdatePlan> BuildPlanAsync(
        Guid pluginInstanceId,
        string candidateVersion,
        Guid? intentId,
        IReadOnlyDictionary<string, string?>? operatorParameters = null,
        IReadOnlyCollection<string>? adoptDeclaredResourceWorkloadIds = null,
        WorkloadUpdatePlanOrigin? origin = null,
        CancellationToken cancellationToken = default)
        => Task.FromResult(planFor(intentId ?? Guid.Empty));
}

public sealed class RecordingEffects(CallLog log) : IWorkloadUpdateEffects
{
    public int CommitInstalledVersionCount { get; private set; }
    public string? LastCommittedGroupId { get; private set; }
    public int CommitParamsCount { get; private set; }
    public int RestoreCount { get; private set; }
    public Action<WorkloadUpdatePlan>? OnCommitParams { get; set; }
    public Action<string, string>? OnCommitInstalledVersion { get; set; }

    public string? InstalledVersion { get; set; } = "2.5.0";

    public Task CommitInstalledVersionAsync(string groupId, string targetVersion, CancellationToken cancellationToken = default)
    {
        CommitInstalledVersionCount++;
        InstalledVersion = targetVersion;
        LastCommittedGroupId = groupId;
        log.Add($"commit-installed-version:{targetVersion}");
        OnCommitInstalledVersion?.Invoke(groupId, targetVersion);
        return Task.CompletedTask;
    }

    public Task CommitParamsAsync(WorkloadUpdatePlan plan, CancellationToken cancellationToken = default)
    {
        CommitParamsCount++;
        log.Add("commit-params");
        OnCommitParams?.Invoke(plan);
        return Task.CompletedTask;
    }

    public Task RestorePreviousParamsAsync(WorkloadUpdateIntent intent, CancellationToken cancellationToken = default)
    {
        RestoreCount++;
        log.Add("restore-params");
        return Task.CompletedTask;
    }
}

internal sealed class RecordingRowEffects(CallLog log) : IWorkloadUpdateRowEffects
{
    public int EnsureCount { get; private set; }

    public Task EnsureRowsAsync(WorkloadUpdateIntent intent, WorkloadUpdatePlan plan, CancellationToken cancellationToken = default)
    {
        EnsureCount++;
        log.Add("ensure-rows");
        return Task.CompletedTask;
    }

    public int RemoveCount { get; private set; }

    public Task RemoveAddedRowsAsync(WorkloadUpdateIntent intent, WorkloadUpdatePlan plan, CancellationToken cancellationToken = default)
    {
        RemoveCount++;
        log.Add("remove-rows");
        return Task.CompletedTask;
    }

    public Task TearDownDroppedRowsAsync(WorkloadUpdatePlan plan, CancellationToken cancellationToken = default)
    {
        log.Add("teardown-dropped");
        return Task.CompletedTask;
    }
}

internal sealed class RecordingDroppedTeardown : IWorkloadDroppedWorkloadTeardown
{
    public List<(WorkloadInstance Row, Reeve.Workloads.Abstractions.Contracts.Deployment.WorkloadManifest? Declaration, string WhyUndeclared, string Version)> Calls { get; } = [];

    public Task BeginTeardownAsync(
        WorkloadInstance instance,
        Reeve.Workloads.Abstractions.Contracts.Deployment.WorkloadManifest? lastDeclaration,
        string whyUndeclared,
        string droppingVersion,
        CancellationToken cancellationToken = default)
    {
        Calls.Add((instance, lastDeclaration, whyUndeclared, droppingVersion));
        if (WorkloadInstanceStateMachine.IsValidTransition(instance.State, WorkloadInstanceState.Terminating))
        {
            instance.TransitionTo(WorkloadInstanceState.Terminating, WorkloadStateReason.Uninstalling, null, instance.UpdatedAtUtc);
        }

        return Task.CompletedTask;
    }
}

public sealed class CallLog
{
    private readonly List<string> _entries = [];
    public IReadOnlyList<string> Entries => _entries;
    public void Add(string entry) => _entries.Add(entry);
    public int IndexOf(string entry) => _entries.IndexOf(entry);
    public int IndexOfFirst(Func<string, bool> pred) => _entries.FindIndex(e => pred(e));
    public override string ToString() => string.Join(" | ", _entries);
}

public sealed class RecordingProvider(WorkloadProviderId provider, CallLog log) : IWorkloadProvider
{
    public WorkloadProviderDescriptor Descriptor { get; } = ShippedProviders.For(provider);

    public Dictionary<string, Func<WorkloadDesiredSpec, WorkloadApplyResult>> ApplyBehaviour { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, WorkloadStatus> StatusByWorkload { get; } = new(StringComparer.Ordinal);
    public Func<WorkloadRef, WorkloadLogs>? LogsBehaviour { get; set; }
    public List<string> AppliedInOrder { get; } = [];
    public List<string> TornDownInOrder { get; } = [];
    public List<WorkloadDesiredSpec> AppliedSpecs { get; } = [];
    public List<WorkloadRef> TornDownReferences { get; } = [];
    public List<(string WorkloadId, bool Force)> ApplyForce { get; } = [];

    public Task<WorkloadApplyResult> ApplyAsync(WorkloadDesiredSpec spec, IProgress<WorkloadApplyProgress>? progress, CancellationToken cancellationToken = default, bool forceConflicts = false)
    {
        AppliedInOrder.Add(spec.WorkloadId);
        AppliedSpecs.Add(spec);
        ApplyForce.Add((spec.WorkloadId, forceConflicts));
        log.Add($"apply:{spec.WorkloadId}:{spec.SpecHash}:force={forceConflicts}");
        var result = ApplyBehaviour.TryGetValue(spec.WorkloadId, out var beh)
            ? beh(spec)
            : WorkloadApplyResult.Applied(spec.SpecHash);
        return Task.FromResult(result);
    }

    public Task TeardownAsync(WorkloadRef reference, bool retainVolumes, CancellationToken cancellationToken = default)
    {
        TornDownInOrder.Add(reference.WorkloadId);
        TornDownReferences.Add(reference);
        log.Add($"teardown:{reference.WorkloadId}:retain={retainVolumes}");
        return Task.CompletedTask;
    }

    public Task SuspendAsync(WorkloadRef reference, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task ResumeAsync(WorkloadRef reference, CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task<WorkloadStatus> GetStatusAsync(WorkloadRef reference, CancellationToken cancellationToken = default)
    {
        log.Add($"status:{reference.WorkloadId}");
        return Task.FromResult(StatusByWorkload.TryGetValue(reference.WorkloadId, out var s)
            ? s
            : new WorkloadStatus { Phase = WorkloadProviderPhase.Ready, ProviderReady = true });
    }

    public Task<WorkloadPreflightResult> PreflightAsync(WorkloadDesiredSpec spec, CancellationToken cancellationToken = default)
        => Task.FromResult(WorkloadPreflightResult.Ok());

    public Task<WorkloadLogs> GetRecentLogsAsync(WorkloadRef reference, int tailLines, bool previousContainer, CancellationToken cancellationToken = default)
    {
        log.Add($"logs:{reference.WorkloadId}:tail={tailLines}:prev={previousContainer}");
        return Task.FromResult(LogsBehaviour?.Invoke(reference) ?? new WorkloadLogs
        {
            Lines = [WorkloadLogLine.Line("panic: sidecar boot failed")],
        });
    }

    public Task<WorkloadAttachmentExistence> GetAttachmentExistenceAsync(
        IReadOnlyList<string> attachments,
        CancellationToken cancellationToken = default)
    {
        log.Add($"attachment-existence:{string.Join("+", attachments)}");
        return Task.FromResult(WorkloadAttachmentExistence.Read([]));
    }
}

public sealed class FakeProviderRegistry(IWorkloadProvider? provider) : IWorkloadProviderRegistry
{
    public IWorkloadProvider? Provider { get; set; } = provider;

    public IWorkloadProvider? TryResolve(WorkloadProviderId provider) => Provider;

    public IWorkloadProvider Resolve(WorkloadProviderId provider)
        => Provider ?? throw new WorkloadProviderNotRegisteredException(provider);

    public IReadOnlyCollection<WorkloadProviderId> RegisteredProviders
        => Provider is null ? [] : [Provider.Provider];

    public IReadOnlyCollection<WorkloadProviderDescriptor> Descriptors
        => [.. RegisteredProviders.Select(Describe)];

    public WorkloadProviderDescriptor Describe(WorkloadProviderId kind)
        => Provider is { } registered && registered.Provider == kind ? registered.Descriptor : ShippedProviders.For(kind);
}

public sealed class ScriptedHostStage(CallLog log) : Reeve.Pipeline.IStage<WorkloadUpdateStageContext>
{
    public const string StageId = "stage.host.plugin-roll";
    public const string IrreversibleReason = "a certified N+1 may have forward-migrated data";

    private readonly Queue<Reeve.Pipeline.StageOutcome> _outcomes = new();

    public string Id => StageId;

    public Reeve.Pipeline.StagePlacement Placement => Reeve.Pipeline.StagePlacement.After(WorkloadUpdateStages.CommitVersion);

    public Reeve.Pipeline.StageReversibility Reversibility
        => Reeve.Pipeline.StageReversibility.IrreversibleOnceEntered(IrreversibleReason);

    public Reeve.Pipeline.StageBudget Budget => Reeve.Pipeline.StageBudget.None;

    public List<WorkloadUpdateStageContext> Calls { get; } = [];

    public Reeve.Pipeline.StageOutcome DefaultOutcome { get; set; } = Reeve.Pipeline.StageOutcome.Done;

    public void Enqueue(Reeve.Pipeline.StageOutcome outcome) => _outcomes.Enqueue(outcome);

    public void KeepWorking() => DefaultOutcome = Reeve.Pipeline.StageOutcome.Working("waiting on replica-b");

    public Action? OnCall { get; set; }

    public Func<Task>? AsyncOnCall { get; set; }

    public async ValueTask<Reeve.Pipeline.StageOutcome> UpAsync(WorkloadUpdateStageContext context, CancellationToken cancellationToken)
    {
        OnCall?.Invoke();
        if (AsyncOnCall is { } asyncOnCall)
        {
            await asyncOnCall().ConfigureAwait(false);
        }
        Calls.Add(context);
        log.Add($"host-stage:{context.ToVersion}");
        return _outcomes.Count > 0 ? _outcomes.Dequeue() : DefaultOutcome;
    }

    public int Downs { get; private set; }

    public ValueTask<Reeve.Pipeline.StageOutcome> DownAsync(WorkloadUpdateStageContext context, CancellationToken cancellationToken)
    {
        Downs++;
        return ValueTask.FromResult(Reeve.Pipeline.StageOutcome.Failed("an irreversible stage is never unwound"));
    }
}

public sealed class ScriptedHealthGate(CallLog log) : IWorkloadUpdateHealthGate
{
    public Dictionary<string, Queue<WorkloadHealthGateVerdict>> VerdictsByWorkload { get; } = new(StringComparer.Ordinal);
    public WorkloadHealthGateVerdict DefaultVerdict { get; set; } = WorkloadHealthGateVerdict.Passed;
    public List<WorkloadHealthGateTickRequest> Requests { get; } = [];

    public Task<WorkloadHealthGateTickResult> EvaluateTickAsync(WorkloadHealthGateTickRequest request, CancellationToken cancellationToken = default)
    {
        Requests.Add(request);
        log.Add($"gate:{request.Reference.WorkloadId}:expected={request.ExpectedSpecHash}");

        var verdict = DefaultVerdict;
        if (VerdictsByWorkload.TryGetValue(request.Reference.WorkloadId, out var q) && q.Count > 0)
        {
            verdict = q.Dequeue();
        }

        var priorTicks = (int)request.PriorClockState.AccumulatedPause.TotalSeconds;

        return Task.FromResult(new WorkloadHealthGateTickResult
        {
            Verdict = verdict,
            FailureReason = verdict == WorkloadHealthGateVerdict.Failed ? "SpecHashMismatch" : null,
            Detail = verdict == WorkloadHealthGateVerdict.Failed ? "wrong image connected" : null,
            NextClockState = new WorkloadGateClockState { AccumulatedPause = TimeSpan.FromSeconds(priorTicks + 1) },
        });
    }

    public static int ParseTicks(WorkloadUpdateIntentStep step)
        => (int)step.ReadPriorClockState().AccumulatedPause.TotalSeconds;
}

public sealed class SpyIssueRegistry : IWorkloadDiagnosticsSink
{
    public List<(string Code, WorkloadDiagnosticSeverity Severity, string Message)> Raised { get; } = [];
    public List<string> Cleared { get; } = [];

    public Task RaiseAsync(WorkloadDiagnostic diagnostic, CancellationToken cancellationToken = default)
    {
        Raised.Add((diagnostic.Code, diagnostic.Severity, diagnostic.Message));
        return Task.CompletedTask;
    }

    public Task ClearAsync(string code, CancellationToken cancellationToken = default)
    {
        Cleared.Add(code);
        return Task.CompletedTask;
    }
}

public sealed class SpyRepairsFactory(SpyIssueRegistry registry) : IWorkloadDiagnosticsSinkFactory
{
    public SpyIssueRegistry Registry { get; } = registry;
    public List<Guid> CreatedFor { get; } = [];
    public IWorkloadDiagnosticsSink Create(Guid instanceId)
    {
        CreatedFor.Add(instanceId);
        return Registry;
    }

    public void Prune(IEnumerable<Guid> liveInstanceIds)
    {
    }
}

public sealed class RecordingNudge : IWorkloadUpdateReconcileNudge
{
    public List<(Guid Instance, string Reason)> Nudges { get; } = [];

    public Exception? Throw { get; set; }

    public Task NudgeAsync(Guid pluginInstanceId, string reason, CancellationToken cancellationToken = default)
    {
        if (Throw is not null)
        {
            throw Throw;
        }
        Nudges.Add((pluginInstanceId, reason));
        return Task.CompletedTask;
    }
}

public sealed class RecordingAuditWriter : Reeve.Workloads.Abstractions.IWorkloadAuditSink
{
    public List<Reeve.Workloads.Abstractions.WorkloadAuditEntry> Entries { get; } = [];

    public Exception? Throw { get; set; }

    public Task WriteAsync(Reeve.Workloads.Abstractions.WorkloadAuditEntry entry, CancellationToken cancellationToken = default)
    {
        if (Throw is not null)
        {
            throw Throw;
        }
        Entries.Add(entry);
        return Task.CompletedTask;
    }
}

internal sealed class FakeObservedWorkloadVersionReader : IObservedWorkloadVersionReader
{
    public Dictionary<Guid, string?> VersionsByInstance { get; } = new();

    public Exception? Throw { get; set; }

    public int Reads { get; private set; }

    public Task<string?> GetObservedAppVersionAsync(Guid instanceId, CancellationToken cancellationToken = default)
    {
        Reads++;
        return Throw is { } fault
            ? Task.FromException<string?>(fault)
            : Task.FromResult(VersionsByInstance.TryGetValue(instanceId, out var v) ? v : null);
    }
}

internal sealed class FakePresenceContributor : IWorkloadHealthContributor
{
    public Dictionary<Guid, string?> SpecHashByInstance { get; } = new();

    public IReadOnlySet<WorkloadHealthMode> ObservedModes { get; } =
        new HashSet<WorkloadHealthMode> { new("adapter-session") };

    public Task<WorkloadHealthContribution> ObserveAsync(
        WorkloadHealthObservation observation, CancellationToken cancellationToken = default)
        => Task.FromResult(new WorkloadHealthContribution
        {
            Presence = SpecHashByInstance.TryGetValue(observation.InstanceId, out var reported)
                ? Reeve.Workloads.Domain.WorkloadHealthEvaluator.FromDefiniteRead(reported, observation.DesiredSpecHash)
                : Reeve.Workloads.Domain.WorkloadPresenceSignal.PresentMatching,
        });

    public Reeve.Workloads.Core.Health.WorkloadHealthContributorDispatch Dispatch() => new([this]);
}

public static class WorkloadUpdatePlanBuilder
{
    public static readonly Guid InstanceGuid = Guid.Parse("11111111-1111-1111-1111-111111111111");

    public static WorkloadDesiredSpec Spec(string workloadId, string specHash, WorkloadProviderId? provider = null)
        => new()
        {
            GroupInstanceId = InstanceGuid,
            GroupId = "zwave",
            WorkloadId = workloadId,
            InstanceId = InstanceGuid,
            Inst8 = "11111111",
            Provider = provider ?? KubernetesProvider.Id,
            ResourceName = $"zwave-{workloadId}-11111111",
            SecretName = $"zwave-{workloadId}-11111111-secret",
            Labels = new Dictionary<string, string>(),
            Image = "reeve/zwave@sha256:deadbeef",
            Kind = WorkloadKind.Service,
            Health = new WorkloadHealth { Mode = new WorkloadHealthMode("adapter-session"), HeartbeatSeconds = 30 },
            SpecHash = specHash,
        };

    public static WorkloadRef RefFor(string workloadId, Guid instanceId, WorkloadProviderId? provider = null)
        => new()
        {
            InstanceId = instanceId,
            GroupId = "zwave",
            WorkloadId = workloadId,
            Inst8 = instanceId.ToString("N")[..8],
            Provider = provider ?? KubernetesProvider.Id,
        };

    public static WorkloadUpdatePlanItem Item(string workloadId, Guid instanceId, string targetHash, string? previousHash, WorkloadProviderId? provider = null)
        => new()
        {
            WorkloadId = workloadId,
            Reference = RefFor(workloadId, instanceId, provider),
            TargetSpec = Spec(workloadId, targetHash, provider),
            PreviousSpec = previousHash is null ? null : Spec(workloadId, previousHash, provider),
            CurrentExtensions = ReeveTestExtensions.Pair(),
            CandidateExtensions = ReeveTestExtensions.Pair(),
            Certification = WorkloadHealthGateCertification.HostEvidence,
            PreviousCertification = WorkloadHealthGateCertification.HostEvidence,
        };

    public static WorkloadUpdatePlan Plan(
        Guid instanceId,
        IReadOnlyList<WorkloadUpdatePlanItem> items,
        DeploymentManifest? previous = null,
        DeploymentManifest? candidate = null,
        IReadOnlyDictionary<string, WorkloadPrivilegeRequirements>? prevRequirements = null,
        IReadOnlyDictionary<string, WorkloadPrivilegeRequirements>? candRequirements = null)
    {
        var emptyManifest = new DeploymentManifest { SchemaVersion = 1, Workloads = [] };
        var targetSpecs = items.ToDictionary(item => item.WorkloadId, item => item.TargetSpec, StringComparer.Ordinal);
        var previousSpecs = items
            .Where(item => item.PreviousSpec is not null)
            .ToDictionary(item => item.WorkloadId, item => item.PreviousSpec!, StringComparer.Ordinal);
        var candidateRequirements = candRequirements ?? WorkloadRequirementsFacts.FromSpecs(targetSpecs);
        return new WorkloadUpdatePlan
        {
            GroupInstanceId = instanceId,
            GroupId = "zwave",
            SourceKey = "22222222-2222-2222-2222-222222222222",
            FromVersion = "2.5.0",
            ToVersion = "2.6.0",
            PreviousManifest = previous ?? emptyManifest,
            CandidateManifest = candidate ?? emptyManifest,
            PreviousRequirements = prevRequirements ?? WorkloadRequirementsFacts.FromSpecs(previousSpecs),
            CandidateRequirements = candidateRequirements,
            RequirementsFingerprint = WorkloadRequirementsFacts.Fingerprint(candidateRequirements),
            PreviousResolvedParameters = new Dictionary<string, IReadOnlyDictionary<string, string?>>(),
            PreviousStoredParameters = new Dictionary<string, IReadOnlyDictionary<string, string?>>(),
            CandidateResolvedParameters = new Dictionary<string, IReadOnlyDictionary<string, string?>>(),
            CandidateStoredParameters = new Dictionary<string, IReadOnlyDictionary<string, string?>>(),
            Workloads = items,
            TargetSpecs = targetSpecs,
            PreviousSpecs = previousSpecs,
            FromDigests = new Dictionary<string, string?>(StringComparer.Ordinal),
            ToDigests = new Dictionary<string, string?>(StringComparer.Ordinal),
        };
    }
}

internal sealed class OrchestratorHarness
{
    public CallLog Log { get; } = new();
    public MutableClock Clock { get; }
    public InMemoryIntentRepository Repo { get; } = new();
    public ScriptedPlanner Planner { get; }
    public RecordingProvider Provider { get; }
    public FakeProviderRegistry Registry { get; }
    public ScriptedHealthGate Gate { get; }
    public ScriptedHostStage HostStage { get; }
    public RecordingEffects Effects { get; }
    public RecordingRowEffects Rows { get; }
    public SpyIssueRegistry Issues { get; } = new();
    public FakeObservedWorkloadVersionReader ObservedVersions { get; } = new();

    public FakePresenceContributor Presence { get; } = new();

    public Reeve.Workloads.Core.Tests.Hosting.FakeHostApplicationLifetime Lifetime { get; } = new();
    public WorkloadUpdateOrchestrator Orchestrator { get; }
    public WorkloadUpdateOrchestratorOptions Options { get; }

    public Reeve.Pipeline.Pipeline<WorkloadUpdateStageContext> Pipeline { get; }

    public Reeve.Workloads.Core.Tests.Hosting.RecordingLogger<WorkloadUpdateOrchestrator> OrchestratorLog { get; } = new();

    public OrchestratorHarness(WorkloadUpdatePlan plan, DateTimeOffset? now = null, WorkloadUpdateOrchestratorOptions? options = null, WorkloadProviderId? providerKind = null)
    {
        Clock = new MutableClock(now ?? new DateTimeOffset(2026, 7, 6, 12, 0, 0, TimeSpan.Zero));
        Planner = new ScriptedPlanner(plan);
        Provider = new RecordingProvider(providerKind ?? KubernetesProvider.Id, Log);
        Registry = new FakeProviderRegistry(Provider);
        Gate = new ScriptedHealthGate(Log);
        HostStage = new ScriptedHostStage(Log);
        Effects = new RecordingEffects(Log);
        Rows = new RecordingRowEffects(Log);
        Options = options ?? new WorkloadUpdateOrchestratorOptions();
        Pipeline = PipelineWith(Gate, Clock, Options);
        Orchestrator = OrchestratorWith(Gate, Clock, Options);
    }

    public WorkloadUpdateOrchestrator OrchestratorWith(
        IWorkloadUpdateHealthGate gate,
        MutableClock clock,
        WorkloadUpdateOrchestratorOptions options,
        params Reeve.Pipeline.IStage<WorkloadUpdateStageContext>[] hostStages)
        => new(Repo, Planner, PipelineWith(gate, clock, options, hostStages), clock, OrchestratorLog, Lifetime);

    public WorkloadUpdateOrchestrator CohortOrchestratorWith(
        IWorkloadUpdateHealthGate gate,
        MutableClock clock,
        WorkloadUpdateOrchestratorOptions options,
        IWorkloadGroupRepository groups,
        IWorkloadInstanceRepository instances,
        params Reeve.Pipeline.IStage<WorkloadUpdateStageContext>[] hostStages)
        => new(Repo, Planner, PipelineWith(gate, clock, options, hostStages), clock, OrchestratorLog, Lifetime,
            groups, instances);

    public Reeve.Pipeline.Pipeline<WorkloadUpdateStageContext> PipelineWith(
        IWorkloadUpdateHealthGate gate,
        MutableClock clock,
        WorkloadUpdateOrchestratorOptions options,
        params Reeve.Pipeline.IStage<WorkloadUpdateStageContext>[] hostStages)
    {
        var repairs = new SpyRepairsFactory(Issues);
        return WorkloadUpdatePipeline.Resolve(
        [
            .. hostStages,
            new ParamsStage(Effects),
            new SidecarStage(Registry, gate, clock, options,
                NSubstitute.Substitute.For<IWorkloadInstanceRepository>(),
                new UnpinnedReferenceRolloutGuard(new Microsoft.AspNetCore.DataProtection.EphemeralDataProtectionProvider()), new Reeve.Workloads.Abstractions.NoCredentialPort()),
            new HealthGateStage(gate, clock, options),
            new OperatorRedeployStage(Registry, ObservedVersions, Presence.Dispatch(), repairs, clock,
                NullLogger<OperatorRedeployStage>.Instance),
            new CommitVersionStage(Effects, Rows, Repo),
            HostStage,
            new CommitParamsStage(Effects, Rows),
        ]);
    }

    public async Task<WorkloadUpdateIntent> StageManualAsync(
        WorkloadUpdatePlan plan,
        IReadOnlyDictionary<string, string?>? operatorParameters = null,
        IReadOnlyCollection<string>? adoptDeclaredResourceWorkloadIds = null)
    {
        var initiator = new WorkloadUpdateInitiator(
            Repo, Planner, new FakeInstances(), new RecordingNudge(), Pipeline, Clock,
            NullLogger<WorkloadUpdateInitiator>.Instance);
        var result = await initiator.InitiateAsync(new WorkloadUpdateInitiationRequest
        {
            PluginInstanceId = plan.GroupInstanceId,
            CandidateVersion = plan.ToVersion,
            Precondition = new WorkloadWritePrecondition { RequirementsFingerprint = plan.RequirementsFingerprint },
            OperatorParameters = operatorParameters,
            AdoptDeclaredResourceWorkloadIds = adoptDeclaredResourceWorkloadIds,
        });
        LastInitiation = result;
        return (await Repo.GetByIdAsync(result.IntentId!.Value))!;
    }

    public WorkloadUpdateInitiationResult? LastInitiation { get; private set; }
}
