using Reeve.Workloads.Abstractions.Contracts.Deployment;

namespace Reeve.Workloads.Abstractions.Domain;

/// <summary>One requested volume: <c>{ Id, Size, Criticality }</c>. Not part of the security gate and
/// never counts as escalation — growth/additions flow through auto-update as informational notes.</summary>
public sealed record VolumeRequirement
{
    public required string Id { get; init; }

    /// <summary>Requested size, e.g. <c>"64Mi"</c> — verbatim Kubernetes quantity string.</summary>
    public required string Size { get; init; }

    public required VolumeCriticality Criticality { get; init; }
}

/// <summary>A requirement for the curated node-local hostPath persistence (<c>nodeLocalState</c>). Presence is
/// a security-axis element (absent &lt; present); <see cref="Criticality"/> is ordered cache &lt; recoverable &lt; critical for escalation checks.</summary>
public sealed record NodeLocalStateRequirement
{
    public required VolumeCriticality Criticality { get; init; }
}

/// <summary>Classification of a security-axis change between two sets of requirements.</summary>
public enum RequirementChangeKind
{
    /// <summary>No security-axis field changed.</summary>
    Unchanged = 0,

    /// <summary>All security-axis fields ≤ under the partial order, at least one strictly &lt;.</summary>
    Narrowing = 1,

    /// <summary>At least one security-axis field increases under the partial order.</summary>
    Escalation = 2,
}

/// <summary>Structured escalation/narrowing diff between two sets of requirements. The security axis is a
/// single <see cref="RequirementChangeKind"/> — any increase escalates, even alongside concurrent narrowings. Volume changes are a separate informational axis that never affects <see cref="Security"/>.</summary>
public sealed record WorkloadPrivilegeRequirementsDiff
{
    /// <summary>Security-axis classification of the change.</summary>
    public required RequirementChangeKind Security { get; init; }

    /// <summary>Human-readable descriptions of security-axis fields that increased.</summary>
    public required IReadOnlyList<string> EscalatedFields { get; init; }

    /// <summary>Human-readable descriptions of security-axis fields that decreased.</summary>
    public required IReadOnlyList<string> NarrowedFields { get; init; }

    /// <summary>True when any volume was added, removed, grown or shrunk (informational only, never escalation).</summary>
    public required bool InformationalVolumeChange { get; init; }

    /// <summary>Human-readable notes about the informational volume changes .</summary>
    public required IReadOnlyList<string> VolumeNotes { get; init; }
}
