using Reeve.Workloads.Abstractions.Contracts.Deployment;
using Reeve.Workloads.Abstractions.Domain;
using Reeve.Workloads.Abstractions.Domain.DesiredSpec;
using Reeve.Workloads.Abstractions.Providers;

namespace Reeve.Workloads.Abstractions.Update;

/// <summary>Everything an update needs, so the orchestrator touches no EF / HTTP / k8s. Declaration order
/// in <see cref="Workloads"/> is load-bearing: apply walks forwards, rollback in reverse, never re-sorted.</summary>
public sealed record WorkloadUpdatePlan
{
    /// <summary>The plugin instance whose atomic upgrade this plan describes (the active-intent uniqueness key).</summary>
    public required Guid GroupInstanceId { get; init; }

    /// <summary>The plugin type id (what a host stage addresses its reload by, and workload refs).</summary>
    public required string GroupId { get; init; }

    /// <summary>The host's key for where the group is loaded from, as the group's row records it; a host stage is
    /// handed it back on the stage context.</summary>
    public required string SourceKey { get; init; }

    /// <summary>The version being upgraded FROM, or null (e.g. away from an adopted pre-Workloads image).</summary>
    public string? FromVersion { get; init; }

    /// <summary>The candidate version being upgraded TO (required).</summary>
    public required string ToVersion { get; init; }

    /// <summary>The installed (version N) deployment manifest — the previous side of the diff. When that manifest is
    /// absent or unreadable, the candidate stands in for it.</summary>
    public required DeploymentManifest PreviousManifest { get; init; }

    /// <summary>True when the installed release's manifest exists but could not be read, so
    /// <see cref="PreviousManifest"/> is a stand-in with its extensions emptied. False when it was read, and when
    /// there is none to read.</summary>
    public bool PreviousUnreadable { get; init; }

    /// <summary>The candidate (version N+1) deployment manifest — the candidate side of the diff.</summary>
    public required DeploymentManifest CandidateManifest { get; init; }

    /// <summary>The requirements of each previous spec, by workload id; a workload the candidate adds has no entry.</summary>
    public required IReadOnlyDictionary<string, WorkloadPrivilegeRequirements> PreviousRequirements { get; init; }

    /// <summary>The requirements of each target spec, by workload id.</summary>
    public required IReadOnlyDictionary<string, WorkloadPrivilegeRequirements> CandidateRequirements { get; init; }

    /// <summary>The fingerprint of <see cref="CandidateRequirements"/>, which the initiation's precondition must name.
    /// It is the one <see cref="IWorkloadRequirementsService.ForUpdateAsync"/> reports for the same plan.</summary>
    public required string RequirementsFingerprint { get; init; }

    /// <summary>The previously-resolved parameter values per workload id (name→value), including synthesized
    /// reserved parameters. Uses plaintext values for password parameters so it can be compared with the candidate.</summary>
    public required IReadOnlyDictionary<string, IReadOnlyDictionary<string, string?>> PreviousResolvedParameters { get; init; }

    /// <summary>The raw at-rest parameter values per workload id. Used only for the rollback snapshot, preserving
    /// protected password and reference bytes exactly.</summary>
    public required IReadOnlyDictionary<string, IReadOnlyDictionary<string, string?>> PreviousStoredParameters { get; init; }

    /// <summary>Password parameter names in each workload's previous manifest, used to protect rollback snapshots.</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<string>> PreviousPasswordParameterNames { get; init; }
        = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);

    /// <summary>The values the candidate deploys with and commits at Completed: the stored ones, plus whatever the
    /// operator submitted with the update. Equal to <see cref="PreviousResolvedParameters"/> when nothing was submitted.</summary>
    public required IReadOnlyDictionary<string, IReadOnlyDictionary<string, string?>> CandidateResolvedParameters { get; init; }

    /// <summary>The values commit-params writes: only what the operator supplied (the stored values plus the ones
    /// submitted with the update), never an author default the resolver filled in, in their operator-stored form.
    /// Password references remain their opaque <c>ref:</c> strings here while
    /// <see cref="CandidateResolvedParameters"/> carries resolved values for rendering.</summary>
    public required IReadOnlyDictionary<string, IReadOnlyDictionary<string, string?>> CandidateStoredParameters { get; init; }

    /// <summary>Unpinned password reference values per workload, identified by the resolver for apply safety.</summary>
    public IReadOnlyDictionary<string, IReadOnlyDictionary<string, string?>> CandidateUnpinnedReferences { get; init; }
        = new Dictionary<string, IReadOnlyDictionary<string, string?>>(StringComparer.Ordinal);

    /// <summary>Unpinned password reference values per workload from the previous version, for rollback apply safety.</summary>
    public IReadOnlyDictionary<string, IReadOnlyDictionary<string, string?>> PreviousUnpinnedReferences { get; init; }
        = new Dictionary<string, IReadOnlyDictionary<string, string?>>(StringComparer.Ordinal);

    /// <summary>The per-workload plan slices, in manifest DECLARATION ORDER (apply forwards, rollback reverse).</summary>
    public required IReadOnlyList<WorkloadUpdatePlanItem> Workloads { get; init; }

    /// <summary>The rendered TARGET (N+1) desired spec of every workload, by id — staged verbatim onto the intent.</summary>
    public required IReadOnlyDictionary<string, WorkloadDesiredSpec> TargetSpecs { get; init; }

    /// <summary>The PREVIOUS (N) applied desired specs, by id — the rollback re-apply source. A workload the
    /// candidate ADDS has no entry.</summary>
    public required IReadOnlyDictionary<string, WorkloadDesiredSpec> PreviousSpecs { get; init; }

    /// <summary>Per-workload FROM (N) image digests — the rollback pull targets.</summary>
    public required IReadOnlyDictionary<string, string?> FromDigests { get; init; }

    /// <summary>Per-workload TO (N+1) image digests.</summary>
    public required IReadOnlyDictionary<string, string?> ToDigests { get; init; }
}

/// <summary>One workload's slice of a <see cref="WorkloadUpdatePlan"/>: addressing, rendered specs, and the
/// gate inputs the orchestrator projects onto the HealthGate seam.</summary>
public sealed record WorkloadUpdatePlanItem
{
    /// <summary>The manifest workload id (the intent step key). Unique within the plan.</summary>
    public required string WorkloadId { get; init; }

    /// <summary>The provider-agnostic ref addressing this workload's runtime resources (apply/status/logs/teardown).</summary>
    public required WorkloadRef Reference { get; init; }

    /// <summary>The rendered TARGET (N+1) desired spec — applied in <see cref="Reeve.Workloads.Abstractions.Domain.Entities.WorkloadUpdatePhase.ApplyingSidecar"/>.</summary>
    public required WorkloadDesiredSpec TargetSpec { get; init; }

    /// <summary>The last-known-good applied spec re-applied on rollback, NOT a re-derivation. Null for a newly
    /// ADDED workload, which rollback tears down rather than restores.</summary>
    public WorkloadDesiredSpec? PreviousSpec { get; init; }

    /// <summary>Defaults to the target's hash, but the orchestrator overrides it with the provider's ACTUAL
    /// applied hash once ApplyingSidecar records one: the gate certifies what was applied.</summary>
    public string ExpectedSpecHash => TargetSpec.SpecHash;

    /// <summary>The extensions of the side that runs now — the previous workload's, or the candidate's when the
    /// previous side is absent or unreadable. Never null.</summary>
    public required WorkloadExtensions CurrentExtensions { get; init; }

    /// <summary>The candidate workload's extensions.</summary>
    public required WorkloadExtensions CandidateExtensions { get; init; }

    /// <summary>What the candidate side can be certified by. Required: no default may select host evidence.</summary>
    public required WorkloadHealthGateCertification Certification { get; init; }

    /// <summary>What a ROLLBACK re-certifies against. A release may change its health mode or kind, so rollback
    /// must gate on what the OLD manifest declared, not the candidate's.</summary>
    public required WorkloadHealthGateCertification PreviousCertification { get; init; }
}
