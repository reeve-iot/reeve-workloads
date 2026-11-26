using System.Text.Json.Serialization;
using Reeve.Workloads.Abstractions.Contracts.Deployment;

namespace Reeve.Workloads.Abstractions.Contracts;

/// <summary>The requirements projection for upgrading an installed instance to a candidate version. A diff that
/// could not be built reports NOT ok: a blocked diff must never masquerade as an empty all-clear.</summary>
public sealed record WorkloadUpdateDiffResponse
{
    [JsonPropertyName("ok")]
    public required bool Ok { get; init; }

    /// <summary>Human-readable failure reason when <see cref="Ok"/> is false; null on success.</summary>
    [JsonPropertyName("error")]
    public string? Error { get; init; }

    /// <summary>The plugin instance the diff was computed for (echoed for the UI).</summary>
    [JsonPropertyName("pluginInstanceId")]
    public Guid GroupInstanceId { get; init; }

    /// <summary>The version being upgraded FROM (the installed release), when resolvable.</summary>
    [JsonPropertyName("fromVersion")]
    public string? FromVersion { get; init; }

    /// <summary>The candidate version being previewed (echoed for the UI).</summary>
    [JsonPropertyName("toVersion")]
    public string? ToVersion { get; init; }

    /// <summary>One diff per candidate workload (present-on-both AND added), ordered by workload id.</summary>
    [JsonPropertyName("workloads")]
    public IReadOnlyList<WorkloadUpdateWorkloadDiff> Workloads { get; init; } = [];

    /// <summary>Ids of workloads the candidate adds (absent from the installed manifest), sorted.</summary>
    [JsonPropertyName("addedWorkloadIds")]
    public IReadOnlyList<string> AddedWorkloadIds { get; init; } = [];

    /// <summary>Ids of workloads the candidate removes (present installed, absent in candidate), sorted.</summary>
    [JsonPropertyName("removedWorkloadIds")]
    public IReadOnlyList<string> RemovedWorkloadIds { get; init; } = [];

    /// <summary>True when ANY workload's security-axis diff is an escalation (drives the red escalation banner).</summary>
    [JsonPropertyName("anySecurityEscalation")]
    public bool AnySecurityEscalation { get; init; }

    /// <summary>True when ANY workload introduces a new required, unsatisfiable parameter.</summary>
    [JsonPropertyName("anyNewRequiredParameters")]
    public bool AnyNewRequiredParameters { get; init; }

    /// <summary>The first stage of this update past which it can no longer be rolled back, and why; null when every
    /// stage is reversible. Shown BEFORE the operator agrees, which is the point: <see cref="WorkloadUpdatePointOfNoReturnDto.PassedAtUtc"/>
    /// is always null here.</summary>
    [JsonPropertyName("pointOfNoReturn")]
    public WorkloadUpdatePointOfNoReturnDto? PointOfNoReturn { get; init; }

    /// <summary>A blocked diff carrying a reason and no workloads.</summary>
    public static WorkloadUpdateDiffResponse Blocked(string error, Guid pluginInstanceId = default, string? toVersion = null) => new()
    {
        Ok = false,
        Error = error,
        GroupInstanceId = pluginInstanceId,
        ToVersion = toVersion,
    };
}

/// <summary>The per-workload slice of an update diff. For an ADDED workload the from-fields are null and the
/// security lists are empty, so the addition is reasoned about through its own two flags.</summary>
public sealed record WorkloadUpdateWorkloadDiff
{
    [JsonPropertyName("workloadId")]
    public required string WorkloadId { get; init; }

    /// <summary>True when this workload is present in the candidate but absent from the installed manifest.</summary>
    [JsonPropertyName("isAdded")]
    public required bool IsAdded { get; init; }

    /// <summary>The installed image digest, or null for an added workload.</summary>
    [JsonPropertyName("fromDigest")]
    public string? FromDigest { get; init; }

    /// <summary>The candidate image digest.</summary>
    [JsonPropertyName("toDigest")]
    public required string ToDigest { get; init; }

    /// <summary>True when the image digest changed (never for an added workload — the addition is IsAdded).</summary>
    [JsonPropertyName("digestChanged")]
    public bool DigestChanged { get; init; }

    /// <summary>The security-axis verdict wire token: <c>Unchanged</c>|<c>Narrowing</c>|<c>Escalation</c>.</summary>
    [JsonPropertyName("securityChange")]
    public required string SecurityChange { get; init; }

    /// <summary>Human-readable descriptions of security-axis fields that INCREASED (rendered red).</summary>
    [JsonPropertyName("escalations")]
    public IReadOnlyList<string> Escalations { get; init; } = [];

    /// <summary>Human-readable descriptions of security-axis fields that DECREASED (narrowings — informational).</summary>
    [JsonPropertyName("narrowings")]
    public IReadOnlyList<string> Narrowings { get; init; } = [];

    /// <summary>Informational, never-escalation change notes: volume growth, additions and removals.</summary>
    [JsonPropertyName("informationalChanges")]
    public IReadOnlyList<string> InformationalChanges { get; init; } = [];

    /// <summary>The installed release's host-extension data, verbatim, in the manifest's wire shape. Null for an
    /// added workload, which has no previous release; empty when <see cref="PreviousUnreadable"/>.</summary>
    [JsonPropertyName("fromExtensions")]
    public WorkloadExtensions? FromExtensions { get; init; }

    /// <summary>The candidate release's host-extension data, verbatim, in the manifest's wire shape.</summary>
    [JsonPropertyName("toExtensions")]
    public required WorkloadExtensions ToExtensions { get; init; }

    /// <summary>True when the installed release's manifest could not be read, so its extensions are unknown rather
    /// than empty. A host judging an axis from them fails closed on it.</summary>
    [JsonPropertyName("previousUnreadable")]
    public required bool PreviousUnreadable { get; init; }

    /// <summary>Names of candidate parameters that are required, undefaulted and unsatisfiable. Sorted.</summary>
    [JsonPropertyName("newRequiredParameters")]
    public IReadOnlyList<string> NewRequiredParameters { get; init; } = [];

    /// <summary>The four axes the INSTALLED manifest declared; null for an added workload.</summary>
    [JsonPropertyName("fromResources")]
    public WorkloadUpdateResourceValues? FromResources { get; init; }

    /// <summary>The four axes the CANDIDATE manifest declares.</summary>
    [JsonPropertyName("toResources")]
    public WorkloadUpdateResourceValues? ToResources { get; init; }

    /// <summary>True when a DECLARATION moved between versions, compared numerically — never when the environment
    /// merely differs from the manifest.</summary>
    [JsonPropertyName("resourcesChanged")]
    public bool ResourcesChanged { get; init; }

    /// <summary>The operator's stored overrides; null when they hold none, in which case nothing is adoptable.</summary>
    [JsonPropertyName("operatorResourceOverrides")]
    public WorkloadUpdateResourceValues? OperatorResourceOverrides { get; init; }
}

/// <summary>One CPU/memory posture on the update wire: the four axes, each null when unset.</summary>
public sealed record WorkloadUpdateResourceValues
{
    [JsonPropertyName("requestsCpu")]
    public string? RequestsCpu { get; init; }

    [JsonPropertyName("requestsMemory")]
    public string? RequestsMemory { get; init; }

    [JsonPropertyName("limitsCpu")]
    public string? LimitsCpu { get; init; }

    [JsonPropertyName("limitsMemory")]
    public string? LimitsMemory { get; init; }
}

/// <summary>The ACTIVE intent, if any, plus the most-recent terminal ones, so the operator can watch phase
/// progress and review the last outcome.</summary>
public sealed record WorkloadUpdateStatusResponse
{
    /// <summary>The plugin instance these intents belong to (echoed for the UI).</summary>
    [JsonPropertyName("pluginInstanceId")]
    public required Guid GroupInstanceId { get; init; }

    /// <summary>The id of the currently-active (non-terminal) intent, or null when nothing is in flight.</summary>
    [JsonPropertyName("activeIntentId")]
    public Guid? ActiveIntentId { get; init; }

    /// <summary>Active + recent intents (active first, then most-recent terminal), newest-first within terminals.</summary>
    [JsonPropertyName("intents")]
    public IReadOnlyList<WorkloadUpdateIntentSummary> Intents { get; init; } = [];
}

/// <summary>One intent summary row: no steps and no evidence, which belong to the detail response.</summary>
public sealed record WorkloadUpdateIntentSummary
{
    [JsonPropertyName("id")]
    public required Guid Id { get; init; }

    [JsonPropertyName("fromVersion")]
    public string? FromVersion { get; init; }

    [JsonPropertyName("toVersion")]
    public required string ToVersion { get; init; }

    /// <summary>The intent-level phase wire token (<see cref="Domain"/> enum member name, e.g. <c>HealthGate</c>).</summary>
    [JsonPropertyName("phase")]
    public required string Phase { get; init; }

    /// <summary>The id of the last stage in the walk's journal, as the walk reads the row: the stage it last entered, a
    /// library stage or a host's. A stage is entered on the row before it is first called. Null before the walk entered any stage, and when the row does not account for its walk — for
    /// example, its journal cannot be read or is ahead of its phase.</summary>
    [JsonPropertyName("currentStage")]
    public string? CurrentStage { get; init; }

    /// <summary>Which way the walk is going, or went: <c>Forward</c> or <c>RollingBack</c>.</summary>
    [JsonPropertyName("direction")]
    public required string Direction { get; init; }

    /// <summary>How the update ended: <c>InProgress</c>, <c>Completed</c>, <c>RolledBack</c> or
    /// <c>RollbackFailed</c>. With <see cref="Direction"/> and <see cref="CurrentStage"/> it says what
    /// <c>phase</c> says, in the pipeline's terms.</summary>
    [JsonPropertyName("outcome")]
    public required string Outcome { get; init; }

    /// <summary>True once the intent reached a terminal phase (Completed/RolledBack/RollbackFailed).</summary>
    [JsonPropertyName("isTerminal")]
    public required bool IsTerminal { get; init; }

    /// <summary>An error message on a failed/rolled-back/rollback-failed intent, else null.</summary>
    [JsonPropertyName("error")]
    public string? Error { get; init; }

    /// <summary>Why the walk's latest attempt on this open update got no answer from a stage — its plan could not be
    /// built, or the attempt threw — or null while the walk is moving and once the update has settled. Past the point
    /// of no return, an abandon is taken for an update whose walk has been failing for a minute, and the walk then ends
    /// it where it stands, undoing nothing; one that is still moving is refused.</summary>
    [JsonPropertyName("walkFault")]
    public string? WalkFault { get; init; }

    /// <summary>When the walk's current run of failed attempts began; null when <see cref="WalkFault"/> is.</summary>
    [JsonPropertyName("walkFaultSinceUtc")]
    public DateTimeOffset? WalkFaultSinceUtc { get; init; }

    [JsonPropertyName("createdAtUtc")]
    public required DateTimeOffset CreatedAtUtc { get; init; }

    /// <summary>When the current phase was entered (the progress heartbeat / park-age anchor).</summary>
    [JsonPropertyName("phaseEnteredAtUtc")]
    public required DateTimeOffset PhaseEnteredAtUtc { get; init; }

    /// <summary>Set on the terminal transitions; null while active.</summary>
    [JsonPropertyName("completedAtUtc")]
    public DateTimeOffset? CompletedAtUtc { get; init; }

    /// <summary>Total per-workload step rows.</summary>
    [JsonPropertyName("stepCount")]
    public required int StepCount { get; init; }

    /// <summary>Step rows that have reached a terminal per-workload state (Done/RolledBack/Failed).</summary>
    [JsonPropertyName("completedStepCount")]
    public required int CompletedStepCount { get; init; }
}

/// <summary>The detail for one update intent: phase, per-workload steps, digests, error, and — for a terminal
/// rolled-back intent — the PARSED rollback evidence, so the UI can show why it rolled back.</summary>
public sealed record WorkloadUpdateIntentResponse
{
    [JsonPropertyName("id")]
    public required Guid Id { get; init; }

    [JsonPropertyName("pluginInstanceId")]
    public required Guid GroupInstanceId { get; init; }

    [JsonPropertyName("fromVersion")]
    public string? FromVersion { get; init; }

    [JsonPropertyName("toVersion")]
    public required string ToVersion { get; init; }

    [JsonPropertyName("phase")]
    public required string Phase { get; init; }

    /// <summary>The id of the last stage in the walk's journal, as the walk reads the row: the stage it last entered, a
    /// library stage or a host's. A stage is entered on the row before it is first called. Null before the walk entered any stage, and when the row does not account for its walk — for
    /// example, its journal cannot be read or is ahead of its phase.</summary>
    [JsonPropertyName("currentStage")]
    public string? CurrentStage { get; init; }

    /// <summary>Which way the walk is going, or went: <c>Forward</c> or <c>RollingBack</c>.</summary>
    [JsonPropertyName("direction")]
    public required string Direction { get; init; }

    /// <summary>How the update ended: <c>InProgress</c>, <c>Completed</c>, <c>RolledBack</c> or
    /// <c>RollbackFailed</c>. With <see cref="Direction"/> and <see cref="CurrentStage"/> it says what
    /// <c>phase</c> says, in the pipeline's terms.</summary>
    [JsonPropertyName("outcome")]
    public required string Outcome { get; init; }

    [JsonPropertyName("isTerminal")]
    public required bool IsTerminal { get; init; }

    [JsonPropertyName("error")]
    public string? Error { get; init; }

    /// <summary>Why the walk's latest attempt on this open update got no answer from a stage — its plan could not be
    /// built, or the attempt threw — or null while the walk is moving and once the update has settled. Past the point
    /// of no return, an abandon is taken for an update whose walk has been failing for a minute, and the walk then ends
    /// it where it stands, undoing nothing; one that is still moving is refused.</summary>
    [JsonPropertyName("walkFault")]
    public string? WalkFault { get; init; }

    /// <summary>When the walk's current run of failed attempts began; null when <see cref="WalkFault"/> is.</summary>
    [JsonPropertyName("walkFaultSinceUtc")]
    public DateTimeOffset? WalkFaultSinceUtc { get; init; }

    [JsonPropertyName("createdAtUtc")]
    public required DateTimeOffset CreatedAtUtc { get; init; }

    [JsonPropertyName("phaseEnteredAtUtc")]
    public required DateTimeOffset PhaseEnteredAtUtc { get; init; }

    [JsonPropertyName("completedAtUtc")]
    public DateTimeOffset? CompletedAtUtc { get; init; }

    /// <summary>Per-workload FROM image digests, keyed by workload id (the rollback pull targets), or empty.</summary>
    [JsonPropertyName("fromDigests")]
    public IReadOnlyDictionary<string, string?> FromDigests { get; init; } = new Dictionary<string, string?>();

    /// <summary>Per-workload TO image digests, keyed by workload id, or empty.</summary>
    [JsonPropertyName("toDigests")]
    public IReadOnlyDictionary<string, string?> ToDigests { get; init; } = new Dictionary<string, string?>();

    /// <summary>The per-workload progress step rows, ordered by workload id.</summary>
    [JsonPropertyName("steps")]
    public IReadOnlyList<WorkloadUpdateStepDto> Steps { get; init; } = [];

    /// <summary>The parsed rollback evidence, present only on a terminal rolled-back intent that captured it
    /// before teardown. Null on an active intent, or when none was captured.</summary>
    [JsonPropertyName("evidence")]
    public WorkloadUpdateEvidenceResponse? Evidence { get; init; }

    /// <summary>Where this update stops being reversible, read from the journal stored on the row, if that can be
    /// read. When that journal is going forwards and bars an unwind, this is the barrier, with when it
    /// was passed: the first stage the journal entered that is irreversible or that this build
    /// does not register, whose reversibility this build cannot tell. Otherwise it is the pipeline's first
    /// irreversible stage, with no <see cref="WorkloadUpdatePointOfNoReturnDto.PassedAtUtc"/>, whatever the update
    /// has done, and null when the pipeline has none. The journal can record less than the update did.</summary>
    [JsonPropertyName("pointOfNoReturn")]
    public WorkloadUpdatePointOfNoReturnDto? PointOfNoReturn { get; init; }
}

/// <summary>The stage past which an update can no longer be rolled back, the reason that stage declares, and when
/// the update entered it.</summary>
public sealed record WorkloadUpdatePointOfNoReturnDto
{
    [JsonPropertyName("stageId")]
    public required string StageId { get; init; }

    /// <summary>Why: an irreversible stage's own sentence, e.g. that a new version may have migrated data forward; for
    /// a stage this build does not register, or a journal read off a row in its module swap, the library's.</summary>
    [JsonPropertyName("reason")]
    public required string Reason { get; init; }

    /// <summary>When the update passed this point — when it entered the stage, or, for a journal read off a row in its
    /// module swap, when the row entered its swap phase; null when it is not reported past it.</summary>
    [JsonPropertyName("passedAtUtc")]
    public DateTimeOffset? PassedAtUtc { get; init; }
}

/// <summary>One per-workload progress step row. Enums are wire string member names.</summary>
public sealed record WorkloadUpdateStepDto
{
    [JsonPropertyName("workloadId")]
    public required string WorkloadId { get; init; }

    /// <summary>WHICH intent phase this workload is at (WorkloadUpdatePhase member name).</summary>
    [JsonPropertyName("step")]
    public required string Step { get; init; }

    /// <summary>HOW that phase is going for this workload (WorkloadUpdateStepState member name).</summary>
    [JsonPropertyName("state")]
    public required string State { get; init; }

    /// <summary>How this workload is getting on: <c>Pending</c>, <c>Working</c>, <c>Done</c>, <c>Undone</c> or
    /// <c>Failed</c>. With the intent's <c>currentStage</c> it says what <c>step</c> and <c>state</c> say.</summary>
    [JsonPropertyName("progress")]
    public required string Progress { get; init; }

    [JsonPropertyName("message")]
    public string? Message { get; init; }

    /// <summary>The spec hash the provider actually applied for this workload, or null.</summary>
    [JsonPropertyName("appliedSpecHash")]
    public string? AppliedSpecHash { get; init; }

    [JsonPropertyName("updatedAtUtc")]
    public required DateTimeOffset UpdatedAtUtc { get; init; }
}

/// <summary>The version the intent tried to reach, when the evidence was captured, and per-workload log tails
/// and events. Parsed by the BACKEND, so the UI never touches the raw codec.</summary>
public sealed record WorkloadUpdateEvidenceResponse
{
    [JsonPropertyName("toVersion")]
    public required string ToVersion { get; init; }

    [JsonPropertyName("capturedAtUtc")]
    public required DateTimeOffset CapturedAtUtc { get; init; }

    [JsonPropertyName("workloads")]
    public IReadOnlyList<WorkloadUpdateEvidenceWorkloadDto> Workloads { get; init; } = [];
}

/// <summary>One workload's captured evidence, or a note when capture was not possible.</summary>
public sealed record WorkloadUpdateEvidenceWorkloadDto
{
    [JsonPropertyName("workloadId")]
    public required string WorkloadId { get; init; }

    [JsonPropertyName("previousContainerLogTail")]
    public IReadOnlyList<string> PreviousContainerLogTail { get; init; } = [];

    [JsonPropertyName("recentEvents")]
    public IReadOnlyList<string> RecentEvents { get; init; } = [];

    [JsonPropertyName("note")]
    public string? Note { get; init; }
}

/// <summary>The operator ESCAPE from a wedged update, which otherwise freezes every mutation on the instance.
/// Abandoning an update that can still be unwound only turns it round — the update is not over until it reports an
/// outcome.</summary>
public sealed record WorkloadUpdateAbandonResponse
{
    [JsonPropertyName("intentId")]
    public required Guid IntentId { get; init; }

    /// <summary>The intent's phase AFTER the call — <c>RollingBack</c> when a rollback was started, or
    /// <c>RollbackFailed</c> when the rollback itself was abandoned. Serialized as the phase NAME.</summary>
    [JsonPropertyName("phase")]
    public required string Phase { get; init; }

    /// <summary>The last stage in the walk's journal, when it has one.</summary>
    [JsonPropertyName("currentStage")]
    public string? CurrentStage { get; init; }

    /// <summary>Which way the walk is going after the call: <c>RollingBack</c> once a rollback was started.</summary>
    [JsonPropertyName("direction")]
    public required string Direction { get; init; }

    /// <summary>How the update stands after the call: <c>InProgress</c> while a started rollback runs,
    /// <c>RollbackFailed</c> when the rollback itself was abandoned.</summary>
    [JsonPropertyName("outcome")]
    public required string Outcome { get; init; }

    /// <summary>Whether the intent has settled. False while a rollback the operator just started is still running.</summary>
    [JsonPropertyName("isTerminal")]
    public required bool IsTerminal { get; init; }

    /// <summary>An operator-facing explanation of what was actually done — including, on the terminal path, that the
    /// deployment was NOT restored.</summary>
    [JsonPropertyName("message")]
    public required string Message { get; init; }
}
