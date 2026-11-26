using Reeve.Workloads.Abstractions.Domain.Entities;

namespace Reeve.Workloads.Abstractions;

/// <summary>Orchestration-plane lifecycle commands (restart/redeploy/pause/resume) for workload instances —
/// distinct from any command a host sends to a running workload. Each performs a state transition and nudges the leader's reconciler, which does the real provider work; serialized behind an update-intent conflict gate.</summary>
public interface IWorkloadOperationsService
{
    /// <summary>Restarts the workload: clears the applied-spec hash, moves toward
    /// <see cref="WorkloadInstanceState.Deploying"/> so the reconciler re-applies, then nudges it.</summary>
    Task<WorkloadOperationResult> RestartAsync(Guid workloadInstanceId, CancellationToken cancellationToken = default);

    /// <summary>Forces a fresh apply — as <see cref="RestartAsync"/>, but re-applying even without spec drift.</summary>
    Task<WorkloadOperationResult> RedeployAsync(Guid workloadInstanceId, CancellationToken cancellationToken = default);

    /// <summary>Pauses the workload: transitions to <see cref="WorkloadInstanceState.Suspended"/> and nudges.</summary>
    Task<WorkloadOperationResult> PauseAsync(Guid workloadInstanceId, CancellationToken cancellationToken = default);

    /// <summary>Resumes a suspended workload toward <see cref="WorkloadInstanceState.Deploying"/> and nudges the
    /// reconciler.</summary>
    Task<WorkloadOperationResult> ResumeAsync(Guid workloadInstanceId, CancellationToken cancellationToken = default);

    /// <summary>Sets or clears the per-instance observe-only flag. A FLAG flip, not a state transition, so it skips
    /// the <c>CanMutate</c> gate, starts no intent, and echoes the instance's UNCHANGED current state.</summary>
    Task<WorkloadOperationResult> SetObserveOnlyAsync(Guid workloadInstanceId, bool observeOnly, CancellationToken cancellationToken = default);

    /// <summary>Triggers an on-demand scan run — the SOLE setter of the pending scan-run marker. One atomic
    /// decision: refuse if in-flight or pending, else enumerate Ready nodes and mint a run-id, or fail typed. A
    /// SUSPENDED instance is refused as <see cref="WorkloadOperationOutcome.NotMutable"/> rather than queued: the
    /// reconciler will not fan a run out for a held instance, so a Success here would promise a run that cannot
    /// start until the instance is resumed.</summary>
    Task<WorkloadOperationResult> ScanAsync(Guid workloadInstanceId, CancellationToken cancellationToken = default);
}

/// <summary>The outcome kind of a workload lifecycle command.</summary>
public enum WorkloadOperationOutcome
{
    /// <summary>The transition was applied and a reconcile nudge published.</summary>
    Success = 0,

    /// <summary>No instance exists for the supplied id.</summary>
    NotFound = 1,

    /// <summary>An update intent is active on the instance, so the command is serialized behind it.</summary>
    Conflict = 2,

    /// <summary>The instance is in a non-mutable state (<see cref="WorkloadInstanceState.Unknown"/> /
    /// <see cref="WorkloadInstanceState.Terminating"/>), so the command is rejected.</summary>
    NotMutable = 3,

    /// <summary>A scan trigger was refused because a run is already in progress: no second run-id is minted and the
    /// existing marker is left untouched, so a re-trigger is idempotent.</summary>
    ScanAlreadyInProgress = 4,

    /// <summary>A scan trigger found ZERO eligible Ready nodes, so the run resolves SYNCHRONOUSLY without setting
    /// the marker or nudging the reconciler. Distinct from a success and from an already-in-progress refusal.</summary>
    NoTargets = 5,

    /// <summary>The provider preflight could not enumerate nodes — almost always a missing read-only nodes
    /// ClusterRole. Operator-actionable, so it is a TYPED 422 outcome rather than an untyped 500.</summary>
    NodesUnreadable = 6,

    /// <summary>A blocking preflight error that is NOT the nodes 403 — unreachable API, Pod Security Admission, a
    /// ResourceQuota, arch affinity. Typed separately so these are not misreported as an RBAC-nodes gap.</summary>
    PreflightFailed = 7,

    /// <summary>The host refused the workload's bootstrap, so there is no spec to scan with. Nothing was changed;
    /// <see cref="WorkloadOperationResult.Error"/> carries the host's reason.</summary>
    HostRefused = 8,

    /// <summary>A scan trigger was refused because the provider's declared traits say it materialises nothing at
    /// runtime, so it can create no per-node Job and has no Ready-node set to enumerate. A refusal NAMED apart from
    /// <see cref="NoTargets"/> on purpose: an empty target set reads as a cluster with no eligible nodes and hides a
    /// provider on which no run can ever start. <see cref="WorkloadOperationResult.Error"/> names the provider.
    /// Operator-actionable, so it is a TYPED 422 outcome.</summary>
    ProviderCannotScan = 9,

    /// <summary>A restart, redeploy or pause was refused because the provider's declared traits say it
    /// materialises nothing: the operator runs the workload, so Reeve can neither restart nor stop it and the
    /// command would change nothing but the state shown. <see cref="WorkloadOperationResult.Error"/> names the
    /// provider and the command. Operator-actionable, so it is a TYPED 422 outcome.</summary>
    ProviderCannotControlRuntime = 10,
}

/// <summary>The typed result of a workload lifecycle command. A closed record — no <c>object</c> or exception
/// escape hatch — so the controller maps outcomes to HTTP deterministically.</summary>
public sealed record WorkloadOperationResult
{
    public required WorkloadOperationOutcome Outcome { get; init; }

    /// <summary>The active update-intent id, set only when <see cref="Outcome"/> is
    /// <see cref="WorkloadOperationOutcome.Conflict"/>.</summary>
    public Guid? ActiveIntentId { get; init; }

    /// <summary>The non-mutable state, set only when <see cref="Outcome"/> is
    /// <see cref="WorkloadOperationOutcome.NotMutable"/>.</summary>
    public WorkloadInstanceState? BlockingState { get; init; }

    /// <summary>The instance's state AFTER a successful transition (for the caller's response/echo).</summary>
    public WorkloadInstanceState? ResultingState { get; init; }

    /// <summary>The failing preflight check's message, set only for a preflight failure — the controller surfaces it
    /// so the operator sees the ACTUAL blocker, not the nodes-ClusterRole boilerplate.</summary>
    public string? PreflightFailureMessage { get; init; }

    /// <summary>The failing preflight check's optional copy-paste remediation snippet.</summary>
    public string? PreflightFixSnippet { get; init; }

    /// <summary>The refusal's own reason: the host's, set when <see cref="Outcome"/> is
    /// <see cref="WorkloadOperationOutcome.HostRefused"/>, or the message naming the provider when it is
    /// <see cref="WorkloadOperationOutcome.ProviderCannotScan"/> or
    /// <see cref="WorkloadOperationOutcome.ProviderCannotControlRuntime"/>. Kept apart from
    /// <see cref="PreflightFailureMessage"/> so a caller never reads a refusal as a cluster blocker.</summary>
    public string? Error { get; init; }

    /// <summary>A success carrying the resulting state.</summary>
    public static WorkloadOperationResult Success(WorkloadInstanceState resultingState) => new()
    {
        Outcome = WorkloadOperationOutcome.Success,
        ResultingState = resultingState,
    };

    /// <summary>The unknown-instance outcome.</summary>
    public static WorkloadOperationResult NotFound() => new()
    {
        Outcome = WorkloadOperationOutcome.NotFound,
    };

    /// <summary>The intent-serialization conflict, naming the active intent.</summary>
    public static WorkloadOperationResult Conflict(Guid activeIntentId) => new()
    {
        Outcome = WorkloadOperationOutcome.Conflict,
        ActiveIntentId = activeIntentId,
    };

    /// <summary>The non-mutable-state rejection, naming the blocking state.</summary>
    public static WorkloadOperationResult NotMutable(WorkloadInstanceState blockingState) => new()
    {
        Outcome = WorkloadOperationOutcome.NotMutable,
        BlockingState = blockingState,
    };

    /// <summary>The idempotent "scan already in progress" refusal (in-flight OR marker already pending).</summary>
    public static WorkloadOperationResult ScanAlreadyInProgress() => new()
    {
        Outcome = WorkloadOperationOutcome.ScanAlreadyInProgress,
    };

    /// <summary>The synchronous zero-eligible-nodes outcome, with the marker left UNSET.</summary>
    public static WorkloadOperationResult NoTargets() => new()
    {
        Outcome = WorkloadOperationOutcome.NoTargets,
    };

    /// <summary>The loud, operator-actionable "nodes unreadable" refusal: marker left UNSET, reconciler NOT
    /// nudged.</summary>
    public static WorkloadOperationResult NodesUnreadable() => new()
    {
        Outcome = WorkloadOperationOutcome.NodesUnreadable,
    };

    /// <summary>The named "this provider cannot scan" refusal, <paramref name="providerLabel"/> being the
    /// provider's operator-facing label. No run-id minted, marker left UNSET, reconciler NOT nudged, and no provider
    /// I/O — a provider that materialises nothing has no target set to ask for.</summary>
    public static WorkloadOperationResult ProviderCannotScan(string providerLabel)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerLabel);
        return new()
        {
            Outcome = WorkloadOperationOutcome.ProviderCannotScan,
            Error = $"The {providerLabel} provider cannot run a per-node scan.",
        };
    }

    /// <summary>The named "this provider does not run the workload" refusal of <paramref name="command"/>
    /// (restart, redeploy or pause), <paramref name="providerLabel"/> being the provider's operator-facing label.
    /// Nothing written, reconciler NOT nudged.</summary>
    public static WorkloadOperationResult ProviderCannotControlRuntime(string providerLabel, string command)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerLabel);
        ArgumentException.ThrowIfNullOrWhiteSpace(command);
        return new()
        {
            Outcome = WorkloadOperationOutcome.ProviderCannotControlRuntime,
            Error = $"The {providerLabel} provider does not run this workload, so Reeve cannot {command} it. "
                + $"{char.ToUpperInvariant(command[0])}{command[1..]} it where it is deployed.",
        };
    }

    /// <summary>A loud refusal for a blocking error OTHER than the nodes 403, carrying the failing check's own
    /// message and fix snippet so the 422 states the REAL blocker. Marker UNSET, reconciler NOT nudged.</summary>
    public static WorkloadOperationResult PreflightFailed(string message, string? fixSnippet = null) => new()
    {
        Outcome = WorkloadOperationOutcome.PreflightFailed,
        PreflightFailureMessage = message,
        PreflightFixSnippet = fixSnippet,
    };

    /// <summary>The host refused the workload's bootstrap, carrying its reason. Nothing was changed.</summary>
    public static WorkloadOperationResult HostRefused(string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        return new() { Outcome = WorkloadOperationOutcome.HostRefused, Error = reason };
    }
}
