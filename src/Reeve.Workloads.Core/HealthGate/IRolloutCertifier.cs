using Reeve.Workloads.Abstractions.Contracts.Deployment;

namespace Reeve.Workloads.Core.HealthGate;

/// <summary>The host's evidence that what came up after an apply is what was applied. At most one is registered:
/// the host fails to start with two. With none, nothing answers and no mode requires evidence.</summary>
public interface IRolloutCertifier
{
    /// <summary>The health modes whose gate REQUIRES this certifier's evidence. Any other mode certifies on
    /// provider-ready, and <c>scan-per-node</c> on the applied spec, whatever this set holds.</summary>
    IReadOnlySet<WorkloadHealthMode> RequiredModes { get; }

    /// <summary>Gathers evidence for one tick. Asked on every tick and for every mode, required or not, so a
    /// candidate that answers with the wrong identity fails the gate either way. Must not throw for a
    /// merely-unreachable source — that is <see cref="RolloutEvidence.Unavailable"/>, which pauses the clock
    /// rather than counting against it.</summary>
    Task<RolloutEvidence> CertifyAsync(
        RolloutCertificationRequest request,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken = default);
}

/// <summary>What the certifier is asked about. Neither the health mode nor the direction of the walk is here: an
/// answer that could branch on them could stop checking identity under a mode that does not require evidence.</summary>
public sealed record RolloutCertificationRequest
{
    /// <summary>The instance whose rollout is being certified.</summary>
    public required Guid InstanceId { get; init; }

    /// <summary>The spec hash that was applied. Evidence has to come from a workload running THIS spec.</summary>
    public required string ExpectedSpecHash { get; init; }

    /// <summary>The spec hash of the workload being replaced, or null when the walk has none to tell apart (an
    /// unwind, an added workload). A candidate running exactly this spec is the outgoing instance, and is
    /// reported as such with <see cref="RolloutCandidate.IsOutgoing"/> rather than as an identity failure.</summary>
    public string? OutgoingSpecHash { get; init; }

    /// <summary>The version the workload must report, or null when none is checked.</summary>
    public string? ToVersion { get; init; }

    /// <summary>The extensions of the side that runs now. Never null: with no readable previous side, the
    /// candidate's stand in.</summary>
    public required WorkloadExtensions CurrentExtensions { get; init; }

    /// <summary>The extensions of the side being certified. On a rollback these are the current side's, since
    /// a rollback certifies against the old pair on both sides.</summary>
    public required WorkloadExtensions CandidateExtensions { get; init; }
}
