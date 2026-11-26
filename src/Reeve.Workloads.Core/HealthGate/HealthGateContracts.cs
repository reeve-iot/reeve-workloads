using Reeve.Workloads.Abstractions.Contracts.Deployment;
using Reeve.Workloads.Abstractions.Update;

namespace Reeve.Workloads.Core.HealthGate;

/// <summary>The per-workload gate configuration for one update. Every field comes from the durably recorded
/// intent and the release manifests, so the evaluator never reads the DB or the provider.</summary>
public sealed record HealthGateConfig
{
    /// <summary>The applied N+1 spec hash. The certifier's evidence must come from a workload running THIS — the
    /// NEW rollout.</summary>
    public required string ExpectedSpecHash { get; init; }

    /// <summary>The spec hash of the workload being replaced, or null when there is none to tell apart (an unwind,
    /// an added workload). Handed to the certifier so it can recognise the outgoing instance by what it runs.</summary>
    public string? OutgoingSpecHash { get; init; }

    /// <summary>When the new spec was applied. A candidate whose <c>ObservedSinceUtc</c> is not strictly after this
    /// is the OUTGOING instance and never satisfies the gate.</summary>
    public required DateTimeOffset ApplyTimestampUtc { get; init; }

    /// <summary>The candidate release version the workload must report; null ⇒ not checked.</summary>
    public string? ToVersion { get; init; }

    /// <summary>The extensions of the side that runs now, for the certifier. Never null: with no readable previous
    /// side, the candidate's stand in.</summary>
    public required WorkloadExtensions CurrentExtensions { get; init; }

    /// <summary>The extensions of the side being certified: the candidate's forwards, the current side's on rollback.</summary>
    public required WorkloadExtensions CandidateExtensions { get; init; }

    /// <summary>The gate timeout. Effective running-elapsed, with paused intervals excluded, is measured against this.</summary>
    public required TimeSpan Timeout { get; init; }

    /// <summary>How long a still-absent candidate is tolerated after a pause ends — one reconnect cycle.
    /// Granted at most once per gate, so a flapping host cannot suspend the timeout forever.</summary>
    public TimeSpan ReconnectGrace { get; init; } = TimeSpan.FromSeconds(90);

    /// <summary>What evidence this workload can be certified by. Required: no default may select host evidence.</summary>
    public required HealthGateCertification Certification { get; init; }
}

/// <summary>The evidence a gate can certify a rollout WITH. A workload whose mode requires no host evidence may
/// never produce any, and a scan-per-node one has no runtime between runs, so demanding it wedges them forever.</summary>
public enum HealthGateCertification
{
    /// <summary>A health mode the registered certifier requires — the full conjunction: provider-ready, a
    /// post-apply candidate, its identity, and its freshness.</summary>
    HostEvidence = 0,

    /// <summary>A mode no certifier requires — provider-ready IS the certification. This drops the evidence
    /// REQUIREMENT, never the evidence: a candidate that answers with the wrong identity still fails.</summary>
    ProviderReady = 1,

    /// <summary><c>kind: scan-per-node</c> — zero pods between runs, so a successful apply IS the
    /// certification. Still fails closed on a fatal crash and still pauses while the provider is unreachable.</summary>
    AppliedSpec = 2,
}

/// <summary>One tick's abstracted observation of the world, gathered so the pure evaluator never touches the
/// certifier or the provider.</summary>
public readonly record struct HealthGateObservation(
    RolloutEvidence Evidence,
    WorkloadProviderReadiness Readiness,
    int RestartCount = 0);

/// <summary>What the host's certifier found. Distinguishes a source that FAULTED (pause the clock) from a
/// genuine ABSENCE (counts toward the gate) — conflating the two is the fault≠absence bug.</summary>
public readonly record struct RolloutEvidence
{
    /// <summary>The three evidence kinds: a candidate answered, nothing answered, or the source was unreadable.</summary>
    public enum Kind
    {
        /// <summary>The certifier's source threw — NOT "absent". Pauses the gate clock .</summary>
        Unavailable = 0,

        /// <summary>Nothing has answered yet. Counts toward the gate/timeout.</summary>
        NoCandidate = 1,

        /// <summary>Something answered; <see cref="Candidate"/> carries the certifier's verdict on it.</summary>
        Candidate = 2,
    }

    private RolloutEvidence(Kind evidence, RolloutCandidate? candidate)
    {
        Evidence = evidence;
        Candidate = candidate;
    }

    /// <summary>Which evidence kind this is.</summary>
    public Kind Evidence { get; }

    /// <summary>The candidate, when <see cref="Evidence"/> is <see cref="Kind.Candidate"/>; otherwise null.</summary>
    public RolloutCandidate? Candidate { get; }

    /// <summary>The certifier's source faulted — pause the gate clock, never treat as absent.</summary>
    public static readonly RolloutEvidence Unavailable = new(Kind.Unavailable, null);

    /// <summary>Genuine absence — counts toward the gate subject to the timeout.</summary>
    public static readonly RolloutEvidence NoCandidate = new(Kind.NoCandidate, null);

    /// <summary>Evidence that something answered, carrying the certifier's verdict on its identity.</summary>
    public static RolloutEvidence From(RolloutCandidate candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        return new RolloutEvidence(Kind.Candidate, candidate);
    }
}

/// <summary>The certifier's verdict on one candidate. The gate owns whether it is the OUTGOING instance and what
/// a stale one costs; the certifier owns what "the right thing answered" means, which is host vocabulary.</summary>
public sealed record RolloutCandidate
{
    /// <summary>Since when the host has observed this candidate. Only one strictly after the apply is the new rollout.</summary>
    public required DateTimeOffset ObservedSinceUtc { get; init; }

    /// <summary>Whether the candidate runs the outgoing spec named by the request, so it is the workload being
    /// replaced still answering, whenever it connected. Never fatal and never a pass: the gate keeps waiting for the
    /// new one, subject to its timeout.</summary>
    public bool IsOutgoing { get; init; }

    /// <summary>Whether the candidate has gone quiet by the host's own liveness rule. Not fatal — it is
    /// "no qualifying candidate yet".</summary>
    public bool Stale { get; init; }

    /// <summary>The host's own code for a FATAL identity mismatch, or null when the candidate is the right one.
    /// A wrong thing having answered is decisive evidence the rollout is bad. The gate fails with
    /// <see cref="HealthGateFailureReason.HostEvidence"/> and reports this code verbatim.
    /// <para>Only null means "the right one": an empty or whitespace code still fails the gate, and the failure then
    /// reads <c>HostEvidence</c>, since there is no code to report.</para></summary>
    public string? IdentityFailure { get; init; }

    /// <summary>Human-readable detail for the failure, carried into diagnostics.</summary>
    public string? Detail { get; init; }
}

/// <summary>Provider-observed readiness for the gate. Kept separate from <c>WorkloadProviderPhase</c> so the
/// clock rules (start / pause / fatal) read declaratively.</summary>
public enum WorkloadProviderReadiness
{
    /// <summary>The provider itself is unreachable (<c>WorkloadProviderPhase.Unknown</c>) — pause the clock (fail-closed, never pass/timeout while unknown).</summary>
    Unknown = 0,

    /// <summary>The image is still pulling (Pending phase with a pull event, no crash) — the clock is NOT started; the phase stays in progress.</summary>
    Pulling = 1,

    /// <summary>The runtime object is running but not yet provider-ready (<c>Running</c> phase) — clock started, awaiting Ready and any required evidence.</summary>
    Running = 2,

    /// <summary>The runtime object is provider-ready (<c>Ready</c> phase / pod Ready). Starts the clock if not already started.</summary>
    Ready = 3,

    /// <summary>A fatal crash (CrashLoopBackOff / exec-format / admission reject) — fail the gate IMMEDIATELY.</summary>
    FatalCrash = 4,

    /// <summary>A retryable crash / backoff — the clock starts (first crash observed) but the gate stays pending until the timeout.</summary>
    RetryableCrash = 5,
}

/// <summary>The gate's deterministic clock state, threaded across ticks and persisted on the intent. Effective
/// running-elapsed is (now − ClockStartedAtUtc) − AccumulatedPause − any pause in progress.</summary>
public sealed record HealthGateClockState
{
    /// <summary>When the timeout clock started (pull complete/Ready or first crash observed), or null while still pulling / paused-before-start.</summary>
    public DateTimeOffset? ClockStartedAtUtc { get; init; }

    /// <summary>When the current pause began (evidence unavailable or provider unreachable), or null when not paused. A pause before the clock starts is still tracked so a later start does not retroactively count it.</summary>
    public DateTimeOffset? PausedSinceUtc { get; init; }

    /// <summary>Total paused duration already folded in from completed pauses (excluded from effective running-elapsed).</summary>
    public TimeSpan AccumulatedPause { get; init; }

    /// <summary>The instant until which a still-absent candidate is tolerated after a pause ends (one
    /// reconnect cycle). Cleared once a qualifying signal is seen or the grace lapses.</summary>
    public DateTimeOffset? ReconnectGraceUntilUtc { get; init; }

    /// <summary>Once armed on a pause→recover, a later flapping outage must NOT re-arm a fresh grace: a
    /// source flapping faster than the liveness window would suspend the timeout indefinitely.</summary>
    public bool GraceConsumed { get; init; }

    /// <summary>The pristine starting state for a fresh gate (nothing started, not paused).</summary>
    public static readonly HealthGateClockState Initial = new();
}

/// <summary>The outcome of one gate tick: a verdict, an optional machine-readable failure reason + detail, and the next clock state.</summary>
public sealed record HealthGateTickResult
{
    /// <summary>The gate verdict this tick.</summary>
    public required HealthGateVerdict Verdict { get; init; }

    /// <summary>The machine-readable failure reason when <see cref="Verdict"/> is <see cref="HealthGateVerdict.Failed"/>; otherwise null.</summary>
    public HealthGateFailureReason? Reason { get; init; }

    /// <summary>The certifier's own code when <see cref="Reason"/> is <see cref="HealthGateFailureReason.HostEvidence"/>; otherwise null.</summary>
    public string? HostFailureCode { get; init; }

    /// <summary>The failure as one token: the host's code for <see cref="HealthGateFailureReason.HostEvidence"/>,
    /// the enum name otherwise, null while not failed. Carried as a string that nothing parses.</summary>
    public string? FailureReason => Reason is HealthGateFailureReason.HostEvidence && !string.IsNullOrWhiteSpace(HostFailureCode)
        ? HostFailureCode
        : Reason?.ToString();

    /// <summary>Human-readable detail for diagnostics / Repairs text (never the sole signal).</summary>
    public string? Detail { get; init; }

    /// <summary>The clock state to persist and feed into the next tick.</summary>
    public required HealthGateClockState Clock { get; init; }

    internal static HealthGateTickResult Pending(HealthGateClockState clock, string? detail = null) => new()
    {
        Verdict = HealthGateVerdict.Pending,
        Clock = clock,
        Detail = detail,
    };

    internal static HealthGateTickResult Passed(HealthGateClockState clock) => new()
    {
        Verdict = HealthGateVerdict.Passed,
        Clock = clock,
    };

    internal static HealthGateTickResult Failed(HealthGateFailureReason reason, HealthGateClockState clock, string? detail = null) => new()
    {
        Verdict = HealthGateVerdict.Failed,
        Reason = reason,
        Clock = clock,
        Detail = detail,
    };

    internal static HealthGateTickResult FailedOnHostEvidence(string hostFailureCode, HealthGateClockState clock, string? detail = null) => new()
    {
        Verdict = HealthGateVerdict.Failed,
        Reason = HealthGateFailureReason.HostEvidence,
        HostFailureCode = hostFailureCode,
        Clock = clock,
        Detail = detail,
    };
}

/// <summary>The three terminal-or-in-progress gate verdicts.</summary>
public enum HealthGateVerdict
{
    /// <summary>The gate has neither passed nor failed yet — keep ticking (phase stays HealthGate).</summary>
    Pending = 0,

    /// <summary>The full conjunction is satisfied against the NEW rollout — the gate's stage is done.</summary>
    Passed = 1,

    /// <summary>The gate failed — the orchestrator transitions to RollingBack (fatal reasons immediately, else on timeout).</summary>
    Failed = 2,
}

/// <summary>Machine-readable gate failure reason. Never stored as a number: the tick result carries it as a string.</summary>
public enum HealthGateFailureReason
{
    /// <summary>The candidate did not become healthy within the (pause-excluded) timeout budget.</summary>
    Timeout = 0,

    /// <summary>The provider reported a fatal crash loop (CrashLoopBackOff) — fail immediately.</summary>
    CrashLoop = 4,

    /// <summary>A post-apply candidate failed the host's identity check (fatal). The host's own code is
    /// <see cref="HealthGateTickResult.HostFailureCode"/>.</summary>
    HostEvidence = 6,
}
