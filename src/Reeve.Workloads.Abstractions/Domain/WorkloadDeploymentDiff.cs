using Reeve.Workloads.Abstractions.Contracts;
using Reeve.Workloads.Abstractions.Contracts.Deployment;
using Reeve.Workloads.Abstractions.Domain.DesiredSpec;
using Reeve.Workloads.Abstractions.Domain.Network;

namespace Reeve.Workloads.Abstractions.Domain;

/// <summary>The structured per-plugin deployment diff. The escalation reading is DELEGATED to
/// <c>WorkloadPrivilegeRequirements.Compare</c>; this file never re-implements subset/escalation logic.</summary>
public sealed record WorkloadDeploymentDiff
{
    /// <summary>Per-workload diffs, ordered by <see cref="WorkloadDiff.WorkloadId"/> for determinism. A removed
    /// workload has no candidate side, so it appears only in <see cref="RemovedWorkloadIds"/>.</summary>
    public required IReadOnlyList<WorkloadDiff> Workloads { get; init; }

    /// <summary>Ids of workloads present in the candidate but absent from the previous manifest (sorted).</summary>
    public required IReadOnlyList<string> AddedWorkloadIds { get; init; }

    /// <summary>Ids of workloads present in the previous manifest but absent from the candidate (sorted).</summary>
    public required IReadOnlyList<string> RemovedWorkloadIds { get; init; }

    /// <summary>True when ANY workload's security-axis profile diff is an escalation.</summary>
    public bool AnySecurityEscalation => Workloads.Any(w => w.RequirementsDiff.Security == RequirementChangeKind.Escalation);


    /// <summary>True when ANY workload introduces a new required, unsatisfiable parameter.</summary>
    public bool AnyNewRequiredParameters => Workloads.Any(w => w.NewRequiredParameters.Count > 0);

    /// <summary>Every workload's informational volume notes, sorted and de-duplicated. Drives the
    /// "informational" flow — never an escalation.</summary>
    public IReadOnlyList<string> VolumeNotes
    {
        get
        {
            var notes = new SortedSet<string>(StringComparer.Ordinal);
            foreach (var w in Workloads)
            {
                foreach (var note in w.RequirementsDiff.VolumeNotes)
                    notes.Add($"[{w.WorkloadId}] {note}");
            }
            return notes.ToList();
        }
    }
}

/// <summary>The per-workload slice of a <see cref="WorkloadDeploymentDiff"/>. For an ADDED workload the "from"
/// fields are null and <see cref="RequirementsDiff"/> is vacuous.</summary>
public sealed record WorkloadDiff
{
    public required string WorkloadId { get; init; }

    /// <summary>True when this workload is present in the candidate but was absent from the previous manifest.</summary>
    public required bool IsAdded { get; init; }

    /// <summary>The installed image digest, or null for an added workload.</summary>
    public required string? FromDigest { get; init; }

    /// <summary>The candidate image digest.</summary>
    public required string ToDigest { get; init; }

    /// <summary>True when the image digest changed (always true for an added workload's introduction is reported via IsAdded).</summary>
    public bool DigestChanged => !IsAdded && !string.Equals(FromDigest, ToDigest, StringComparison.Ordinal);

    /// <summary>The security escalation/narrowing and informational-volume verdict from
    /// <c>WorkloadPrivilegeRequirements.Compare</c>. Vacuous for an added workload.</summary>
    public required WorkloadPrivilegeRequirementsDiff RequirementsDiff { get; init; }

    /// <summary>The installed release's host-extension data, verbatim; null for an added workload, which has no
    /// previous release to have moved away from, and <see cref="WorkloadExtensions.None"/> when
    /// <see cref="PreviousUnreadable"/>. The library carries it and judges none of it.</summary>
    public required WorkloadExtensions? FromExtensions { get; init; }

    /// <summary>The candidate release's host-extension data, verbatim, for the host to judge.</summary>
    public required WorkloadExtensions ToExtensions { get; init; }

    /// <summary>True when the installed release's manifest could not be read: its extensions are unknown, not
    /// empty, so a host judging an axis from them fails closed.</summary>
    public required bool PreviousUnreadable { get; init; }

    /// <summary>Names of candidate parameters that are required, undefaulted and unsatisfiable, sorted. For an
    /// ADDED workload there are no prior values, so this is EVERY required, undefaulted param it declares.</summary>
    public required IReadOnlyList<string> NewRequiredParameters { get; init; }

    /// <summary>The four axes the INSTALLED manifest declared; null for an added workload.</summary>
    public ResolvedResources? FromResources { get; init; }

    /// <summary>The four axes the CANDIDATE manifest declares.</summary>
    public ResolvedResources? ToResources { get; init; }

    /// <summary>True when any axis moved between the two DECLARATIONS, compared numerically so the same value in
    /// another unit is not a change. Always false for an added workload.</summary>
    public bool ResourcesChanged { get; init; }

    /// <summary>The operator's stored overrides; null when they have none. Without one there is nothing to adopt.</summary>
    public ResolvedResources? OperatorResourceOverrides { get; init; }
}
