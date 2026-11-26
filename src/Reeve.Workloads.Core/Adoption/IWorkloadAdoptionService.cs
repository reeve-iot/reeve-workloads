using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Reeve.Workloads.Abstractions;

namespace Reeve.Workloads.Core.Adoption;

/// <summary>Orchestrates workload adoption. This is the ONLY place a <c>force:true</c> SSA is issued, and only
/// after ownership evidence is verified: a name match is never evidence.</summary>
public interface IWorkloadAdoptionService
{
    /// <summary>Detects adoptable resources, grouped by instance label. Reeve's own resources are not candidates,
    /// and an unreachable provider returns empty because adoption is never offered blind.</summary>
    Task<IReadOnlyList<AdoptionCandidate>> DetectAsync(CancellationToken cancellationToken = default);

    /// <summary>Verifies the candidate's actual labels, image and PVC binding against the operator-supplied
    /// evidence. This is the gate that must pass before <see cref="AdoptAsync"/> will force-adopt.</summary>
    Task<AdoptionEvidenceResult> VerifyEvidenceAsync(
        AdoptionCandidate candidate,
        AdoptionEvidence expected,
        CancellationToken cancellationToken = default);

    /// <summary>Adopts the candidate, only after the same evidence verifies: PVC metadata surgery then one
    /// <c>force:true</c> SSA. Idempotent, and fails closed with no SSA and no audit when evidence does not verify.</summary>
    Task<AdoptionResult> AdoptAsync(
        AdoptionCandidate candidate,
        AdoptionEvidence expected,
        CancellationToken cancellationToken = default);
}

/// <summary>Every labelled object sharing one <c>reeve.io/workload-instance</c> value, plus its classification.
/// Detection is by label only: <see cref="InstanceLabel"/> is authority and names are convenience.</summary>
public sealed record AdoptionCandidate
{
    /// <summary>The shared <c>reeve.io/workload-instance</c> label value (the grouping + adoption key).</summary>
    public required string InstanceLabel { get; init; }

    /// <summary>Whether this candidate is a DR/foreign-installation (b) or manual→managed (c) adoption.</summary>
    public required AdoptionClass Class { get; init; }

    /// <summary>The <c>reeve.io/installation</c> label value carried by the resources; null when absent.</summary>
    public string? InstallationLabel { get; init; }

    /// <summary>Every live object (Deployment/DaemonSet/Service/PVC) in the group.</summary>
    public required IReadOnlyList<AdoptableResource> Resources { get; init; }
}

/// <summary>The ownership evidence required before a <c>force:true</c> SSA. A candidate matching a name pattern
/// but not this evidence is a decoy and is never force-adopted.</summary>
public sealed record AdoptionEvidence
{
    /// <summary>Labels every resource must carry. Empty means verification rests on image and PVC evidence alone,
    /// which is weak.</summary>
    public IReadOnlyDictionary<string, string> ExpectedLabels { get; init; } =
        new Dictionary<string, string>(System.StringComparer.Ordinal);

    /// <summary>The image ref the workload object must run, when the caller can assert it. A mismatch is a hard
    /// verification failure; null means no image evidence.</summary>
    public string? ExpectedImage { get; init; }

    /// <summary>The PVC names the candidate must include, proving it owns the retained volume being re-homed
    /// rather than merely sharing a name.</summary>
    public IReadOnlyList<string> ExpectedPvcNames { get; init; } = [];
}

/// <summary>The typed result of <see cref="IWorkloadAdoptionService.VerifyEvidenceAsync"/>.</summary>
public sealed record AdoptionEvidenceResult
{
    /// <summary>True only when EVERY piece of supplied evidence matched the candidate's actual state.</summary>
    public required bool Verified { get; init; }

    /// <summary>The specific evidence mismatches (empty when <see cref="Verified"/>).</summary>
    public IReadOnlyList<string> Failures { get; init; } = [];

    /// <summary>A verified result with no failures.</summary>
    public static AdoptionEvidenceResult Ok() => new() { Verified = true, Failures = [] };

    /// <summary>An unverified result carrying the failures.</summary>
    public static AdoptionEvidenceResult Fail(IReadOnlyList<string> failures) =>
        new() { Verified = false, Failures = failures };
}

/// <summary>The outcome of an adoption attempt. Serialises as its string name on the wire (operator-facing
/// adoption result the FE branches on — a stable string is drift-proof where a numeric ordinal is not).</summary>
[System.Text.Json.Serialization.JsonConverter(typeof(System.Text.Json.Serialization.JsonStringEnumConverter))]
public enum AdoptionOutcome
{
    /// <summary>The candidate was adopted (metadata surgery + force SSA succeeded).</summary>
    Adopted = 0,

    /// <summary>Refused — the supplied evidence did not verify. NO SSA was issued.</summary>
    EvidenceNotVerified = 1,

    /// <summary>The API server was unreachable during adoption — nothing was mutated; retry later.</summary>
    ProviderUnreachable = 2,

    /// <summary>A force SSA hit an unexpected error taking ownership of an object.</summary>
    Error = 3,
}

/// <summary>The outcome of <see cref="IWorkloadAdoptionService.AdoptAsync"/> plus what was adopted.</summary>
public sealed record AdoptionResult
{
    /// <summary>The adoption outcome.</summary>
    public required AdoptionOutcome Outcome { get; init; }

    /// <summary>The names of the workload objects force-adopted (Deployment/DaemonSet/Service).</summary>
    public IReadOnlyList<string> AdoptedObjectNames { get; init; } = [];

    /// <summary>The names of the PVCs whose ownership metadata was surgeried + relabelled.</summary>
    public IReadOnlyList<string> AdoptedPvcNames { get; init; } = [];

    /// <summary>The evidence failures when <see cref="Outcome"/> is <see cref="AdoptionOutcome.EvidenceNotVerified"/>.</summary>
    public IReadOnlyList<string> Failures { get; init; } = [];

    /// <summary>An operator-facing message on an error/unreachable outcome.</summary>
    public string? Message { get; init; }
}
