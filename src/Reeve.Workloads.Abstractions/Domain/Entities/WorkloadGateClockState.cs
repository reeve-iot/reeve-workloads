namespace Reeve.Workloads.Abstractions.Domain.Entities;

/// <summary>The health gate's clock as the intent persists it, so the gate survives a leader failover mid-gate.
/// The gate's own engine keeps the matching state; this is the shape that crosses the persistence boundary.</summary>
/// <remarks>Effective running-elapsed is (now − <see cref="ClockStartedAtUtc"/>) − <see cref="AccumulatedPause"/>
/// − any pause in progress.</remarks>
public sealed record WorkloadGateClockState
{
    /// <summary>When the timeout clock started, or null while still pulling or paused before the start.</summary>
    public DateTimeOffset? ClockStartedAtUtc { get; init; }

    /// <summary>When the current pause began, or null when not paused.</summary>
    public DateTimeOffset? PausedSinceUtc { get; init; }

    /// <summary>Paused duration already folded in from completed pauses.</summary>
    public TimeSpan AccumulatedPause { get; init; }

    /// <summary>Until when still-absent host evidence is tolerated after a pause ends.</summary>
    public DateTimeOffset? ReconnectGraceUntilUtc { get; init; }

    /// <summary>Whether that one reconnect grace has already been spent.</summary>
    public bool GraceConsumed { get; init; }

}
