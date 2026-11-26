namespace Reeve.Workloads.Abstractions.Providers;

/// <summary>Outcome of <see cref="IWorkloadProvider.ApplyAsync"/>. Success carries <see cref="AppliedSpecHash"/>;
/// failure carries a machine-readable <see cref="Reason"/>. A k8s SSA 409 additionally names <see cref="ConflictingFieldManager"/>.</summary>
public sealed record WorkloadApplyResult
{
    public required bool Succeeded { get; init; }

    /// <summary>Machine-readable outcome reason (drives Repairs/diagnostics text).</summary>
    public required WorkloadApplyReason Reason { get; init; }

    /// <summary>Human-readable free-text companion to <see cref="Reason"/>.</summary>
    public string? Message { get; init; }

    /// <summary>The spec hash that was applied (echoes <c>spec.SpecHash</c> on success), or null on failure.</summary>
    public string? AppliedSpecHash { get; init; }

    /// <summary>For a k8s SSA 409 (<see cref="WorkloadApplyReason.FieldManagerConflict"/>): the conflicting manager name.</summary>
    public string? ConflictingFieldManager { get; init; }

    /// <summary>A successful apply that recorded the supplied spec hash.</summary>
    public static WorkloadApplyResult Applied(string appliedSpecHash, string? message = null) => new()
    {
        Succeeded = true,
        Reason = WorkloadApplyReason.Applied,
        AppliedSpecHash = appliedSpecHash,
        Message = message,
    };

    /// <summary>A generic failed apply with a machine-readable reason.</summary>
    public static WorkloadApplyResult Failed(WorkloadApplyReason reason, string? message = null) => new()
    {
        Succeeded = false,
        Reason = reason,
        Message = message,
    };

    /// <summary>The k8s SSA 409 failure naming the conflicting field manager.</summary>
    public static WorkloadApplyResult Conflict(string conflictingFieldManager, string? message = null) => new()
    {
        Succeeded = false,
        Reason = WorkloadApplyReason.FieldManagerConflict,
        ConflictingFieldManager = conflictingFieldManager,
        Message = message,
    };
}

/// <summary>A throttled snapshot of provider-side apply progress, reported via an optional <see cref="IProgress{T}"/>
/// for providers with a long materialization step. Cosmetic and best-effort — a null or throwing reporter never changes the apply outcome.</summary>
public sealed record WorkloadApplyProgress
{
    /// <summary>Overall apply/pull completion 0..100, or null when indeterminate (no byte total known yet).</summary>
    public int? Percent { get; init; }

    /// <summary>A human one-line status for the operator, e.g. <c>"Pulling image… 46% (12/26 MB)"</c>. Never a secret.</summary>
    public required string Message { get; init; }
}

/// <summary>Machine-readable <see cref="WorkloadApplyResult"/> reason.</summary>
public enum WorkloadApplyReason
{
    /// <summary>The desired spec was created/updated on the provider.</summary>
    Applied = 0,

    /// <summary>k8s SSA 409 — another field manager owns a field (<see cref="WorkloadApplyResult.ConflictingFieldManager"/>).</summary>
    FieldManagerConflict = 1,

    /// <summary>The provider was unreachable (docker socket lost, API server down), or a runtime object the apply needs
    /// is not ready yet (e.g. a PVC still Terminating) — retry later.</summary>
    ProviderUnreachable = 2,

    /// <summary>The provider rejected the spec (invalid/immutable field, quota, admission).</summary>
    Rejected = 3,

    /// <summary>Any other provider-side error.</summary>
    Error = 4,
}

/// <summary>Provider-observed status of a materialized workload — provider-level readiness only. A provider
/// never claims a workload is Healthy; under a host health mode that signal comes from the host's health contributor.</summary>
public sealed record WorkloadStatus
{
    /// <summary>The provider phase of the runtime object (pod/container).</summary>
    public required WorkloadProviderPhase Phase { get; init; }

    /// <summary>True when the provider reports the runtime object ready (pod Ready / container healthy) — the
    /// reconciler ANDs this with the host's health contribution to reach <c>Healthy</c> under a host mode.</summary>
    public required bool ProviderReady { get; init; }

    /// <summary>Provider-reported restart count — drives crash-loop detection / Repairs.</summary>
    public int RestartCount { get; init; }

    /// <summary>ScanPerNode only: true when every observed Job has finished (Succeeded/Failed, none active).
    /// <see cref="Phase"/> alone can't tell "freshly created" from "all complete" — both read <see cref="WorkloadProviderPhase.Ready"/>; gate marker-clearing on this, never on bare Ready.</summary>
    public bool ScanRunTerminal { get; init; }

    /// <summary>ScanPerNode only: the nodes whose Job in the terminal run ended Failed, so the run scanned them
    /// and got nothing. Empty for a clean run — <see cref="ScanRunTerminal"/> alone cannot tell the two apart.</summary>
    public IReadOnlyList<string> ScanFailedNodeNames { get; init; } = [];

    /// <summary>Human-readable provider phase/message (e.g. "CrashLoopBackOff", "0/1 ready").</summary>
    public string? Message { get; init; }

    /// <summary>The image the provider observed running (post-mirror ref), when known.</summary>
    public string? ObservedImage { get; init; }

    /// <summary>Recent provider events (last N), newest-last.</summary>
    public IReadOnlyList<WorkloadProviderEvent> RecentEvents { get; init; } = [];

    /// <summary>Resource names of volumes the live runtime mounts that do not exist. The reconciler re-applies to
    /// recreate them. Empty when the provider could not tell, so an unread list never reads as a deleted volume.</summary>
    public IReadOnlyList<string> MissingVolumes { get; init; } = [];

    /// <summary>The sentinel status for a ref the provider can no longer find (post-teardown / never-applied).</summary>
    public static WorkloadStatus NotFound(string? message = null) => new()
    {
        Phase = WorkloadProviderPhase.NotFound,
        ProviderReady = false,
        Message = message,
    };
}

/// <summary>A provider-level phase for a materialized workload's runtime object.</summary>
public enum WorkloadProviderPhase
{
    /// <summary>Accepted by the provider but not yet scheduled/started.</summary>
    Pending = 0,

    /// <summary>The runtime object is running but not yet reported ready.</summary>
    Running = 1,

    /// <summary>The runtime object is running AND provider-ready (pod Ready / container healthy).</summary>
    Ready = 2,

    /// <summary>The runtime object failed (crash loop, exec-format, admission reject at runtime).</summary>
    Failed = 3,

    /// <summary>No runtime object matches the ref's label selector (torn down or never applied).</summary>
    NotFound = 4,

    /// <summary>The provider itself was unreachable — last-known status is unavailable.</summary>
    Unknown = 5,
}

/// <summary>A single provider event line. Closed record — no provider event object leaks out.</summary>
public sealed record WorkloadProviderEvent
{
    public required DateTimeOffset AtUtc { get; init; }

    /// <summary>Event type/reason as the provider reports it (k8s Event.reason, docker event action).</summary>
    public required string Reason { get; init; }

    public required string Message { get; init; }

    /// <summary>The event severity as the provider classifies it (k8s Normal/Warning).</summary>
    public WorkloadEventSeverity Severity { get; init; } = WorkloadEventSeverity.Normal;
}

/// <summary>Severity of a <see cref="WorkloadProviderEvent"/> (mirrors k8s Event type).</summary>
public enum WorkloadEventSeverity
{
    Normal = 0,
    Warning = 1,
}

/// <summary>Result of <see cref="IWorkloadProvider.PreflightAsync"/>: named checks, each with a severity and an
/// optional copy-paste fix snippet.</summary>
public sealed record WorkloadPreflightResult
{
    public required IReadOnlyList<PreflightCheck> Checks { get; init; }

    /// <summary>True when every check passed.</summary>
    public bool AllPassed => Checks.All(c => c.Passed);

    /// <summary>True when at least one failing check is an <see cref="PreflightSeverity.Error"/> — deploy is blocked.</summary>
    public bool HasBlockingError => Checks.Any(c => !c.Passed && c.Severity == PreflightSeverity.Error);

    /// <summary>Scan-per-node Kubernetes preflight only: the eligible Ready node names to fan a Job out over.
    /// Null means the node list was unreadable (<see cref="HasBlockingError"/> is true) — distinct from an empty list, which means a genuinely zero-Ready cluster.</summary>
    public IReadOnlyList<string>? ReadyNodeNames { get; init; }

    /// <summary>The sibling of <see cref="ReadyNodeNames"/>: Ready nodes the eligibility selector excluded,
    /// reported as skipped rather than left silently pending forever. Same null-vs-empty semantics as <see cref="ReadyNodeNames"/>.</summary>
    public IReadOnlyList<string>? IneligibleNodeNames { get; init; }

    /// <summary>An all-clear result (no checks failed). Used by fakes and by providers with nothing to flag.</summary>
    public static WorkloadPreflightResult Ok(IReadOnlyList<PreflightCheck>? checks = null) => new()
    {
        Checks = checks ?? [],
    };
}

/// <summary>One named preflight check. A failing <see cref="PreflightSeverity.Error"/> blocks deploy.</summary>
public sealed record PreflightCheck
{
    /// <summary>Stable machine id (e.g. <c>psa-baseline</c>, <c>arch-affinity</c>, <c>single-node-selector</c>, <c>api-reachable</c>).</summary>
    public required string Id { get; init; }

    public required PreflightSeverity Severity { get; init; }

    public required bool Passed { get; init; }

    public required string Message { get; init; }

    /// <summary>Copy-paste remediation (e.g. a <c>kubectl label node …</c> line), when the check offers one.</summary>
    public string? FixSnippet { get; init; }

    /// <summary>A passing check.</summary>
    public static PreflightCheck Pass(string id, string message, PreflightSeverity severity = PreflightSeverity.Info) => new()
    {
        Id = id,
        Severity = severity,
        Passed = true,
        Message = message,
    };

    /// <summary>A failing check with an optional fix snippet.</summary>
    public static PreflightCheck Fail(string id, PreflightSeverity severity, string message, string? fixSnippet = null) => new()
    {
        Id = id,
        Severity = severity,
        Passed = false,
        Message = message,
        FixSnippet = fixSnippet,
    };
}

/// <summary>Severity of a <see cref="PreflightCheck"/>. Only <see cref="Error"/> blocks deploy.</summary>
public enum PreflightSeverity
{
    Info = 0,
    Warning = 1,
    Error = 2,
}

/// <summary>Recent logs for a materialized workload. When the caller asked for the previous container,
/// restart-boundary markers (<see cref="WorkloadLogLine.IsRestartMarker"/>) are interleaved.</summary>
public sealed record WorkloadLogs
{
    public required IReadOnlyList<WorkloadLogLine> Lines { get; init; }

    /// <summary>True when older lines were dropped to satisfy the requested tail (spec: tail-limited fetch).</summary>
    public bool Truncated { get; init; }

    /// <summary>An empty log set (a fake's default, or a workload with no output yet).</summary>
    public static WorkloadLogs Empty() => new() { Lines = [] };
}

/// <summary>A single log line, or a synthesized restart-boundary or gap marker.</summary>
public sealed record WorkloadLogLine
{
    /// <summary>The log line text (or the marker text when <see cref="IsRestartMarker"/> or <see cref="IsGapMarker"/>).</summary>
    public required string Text { get; init; }

    /// <summary>The time the runtime recorded the line, in UTC, truncated to 100 ns. Docker and Kubernetes fill it;
    /// null for a marker, for Manual mode and for a line whose runtime timestamp could not be parsed.</summary>
    public DateTimeOffset? AtUtc { get; init; }

    /// <summary>True when this line is a synthesized "--- container restarted ---" boundary, not real output.</summary>
    public bool IsRestartMarker { get; init; }

    /// <summary>True when this line is a synthesized marker saying log lines may be missing at this point, not real
    /// output. Only <see cref="WorkloadLogTail.LinesAfter"/> emits it.</summary>
    public bool IsGapMarker { get; init; }

    public static WorkloadLogLine Line(string text, DateTimeOffset? atUtc = null) => new() { Text = text, AtUtc = atUtc };

    public static WorkloadLogLine RestartMarker(string text, DateTimeOffset? atUtc = null) => new()
    {
        Text = text,
        AtUtc = atUtc,
        IsRestartMarker = true,
    };

    public static WorkloadLogLine GapMarker(string text) => new() { Text = text, IsGapMarker = true };
}
