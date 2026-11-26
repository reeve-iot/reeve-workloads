using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Reeve.Workloads.Abstractions.Contracts;
using Reeve.Workloads.Abstractions.Contracts.Deployment;
using Reeve.Workloads.Abstractions.Providers;

namespace Reeve.Workloads.Abstractions.Network;

/// <summary>The single home of the <c>network-attachment-*</c> check family — deliberately not the provider's
/// preflight, which installs never traverse. An operator network is judged by its name and whether it exists;
/// nothing is read from what the object contains. Launches nothing.</summary>
public interface INetworkAttachmentEvaluator
{
    /// <summary>Evaluates the <c>network-attachment-*</c> checks for one workload. A non-<c>operatorNetwork</c>
    /// request evaluates nothing — an unattached install must not acquire a network evaluation it never had.</summary>
    Task<NetworkAttachmentEvaluation> EvaluateAsync(
        NetworkAttachmentEvaluationRequest request,
        CancellationToken cancellationToken = default);
}

/// <summary>The <c>network-attachment-*</c> check family: exactly two ids, both owned by
/// <see cref="INetworkAttachmentEvaluator"/>, exposed as <see cref="All"/> so a completeness check can enumerate them instead of hardcoding a list that can drift.</summary>
public static class NetworkAttachmentCheckIds
{
    /// <summary>Kubernetes: a NAD of that name is listed. Docker: a network of that name is listed. When enumeration
    /// is unavailable, the blocking error carries the reason, not just "not found".</summary>
    public const string Exists = "network-attachment-exists";

    /// <summary>Blocking Error on <c>PerNode</c>/<c>ScanPerNode</c> without a node-eligibility selector; the
    /// same id is a Warning on <c>Service</c>/<c>SingleNode</c> — one home per id.</summary>
    public const string NodeEligibility = "network-attachment-node-eligibility";

    /// <summary>Both ids. Nothing outside this evaluator may author any of them.</summary>
    public static IReadOnlyList<string> All { get; } =
    [
        Exists,
        NodeEligibility,
    ];
}

/// <summary>Everything the evaluator needs, and nothing it could use to launch something — every provider
/// observation is supplied by a caller that already read it, keeping this seam free of provider clients.</summary>
public sealed record NetworkAttachmentEvaluationRequest
{
    /// <summary>The provider the workload is bound to.</summary>
    public required WorkloadProviderId Provider { get; init; }

    /// <summary>The RESOLVED network mode. Anything but <c>operatorNetwork</c> evaluates nothing at all.</summary>
    public required WorkloadNetworkMode Mode { get; init; }

    /// <summary>The manifest's workload kind — the blocking arm is scoped to the fan-out kinds.</summary>
    public required WorkloadKind Kind { get; init; }

    /// <summary>The referenced attachments in declared order — never sorted.</summary>
    public IReadOnlyList<NetworkAttachmentObservation> Attachments { get; init; } = [];

    /// <summary>Why enumeration could not be performed at all (<c>multus-not-installed</c>, <c>forbidden</c>, …);
    /// null on success. <c>none-in-namespace</c> is deliberately excluded — that's "not found", not "unreadable".</summary>
    public string? EnumerationUnavailableReason { get; init; }

    /// <summary>The resolved node-eligibility selector (operator label=value pairs); empty or null is refused on a fan-out kind.</summary>
    public IReadOnlyDictionary<string, string>? NodeEligibilitySelector { get; init; }
}

/// <summary>One attachment as the caller observed it: its name and whether the provider lists it.</summary>
public sealed record NetworkAttachmentObservation
{
    /// <summary>The attachment name exactly as the manifest/parameters declared it.</summary>
    public required string Name { get; init; }

    /// <summary>Whether the named object was found at all (a listed NAD / a listed docker network).</summary>
    public bool Exists { get; init; }
}

/// <summary>The evaluator's whole output: the checks, and whether any of them blocks.</summary>
public sealed record NetworkAttachmentEvaluation
{
    /// <summary>An evaluation that ran nothing (non-<c>operatorNetwork</c>).</summary>
    public static NetworkAttachmentEvaluation NotEvaluated { get; } = new();

    /// <summary>The emitted checks. Every id is one of <see cref="NetworkAttachmentCheckIds.All"/>.</summary>
    public IReadOnlyList<PreflightCheck> Checks { get; init; } = [];

    /// <summary>True when any emitted check is a failing <see cref="PreflightSeverity.Error"/>.</summary>
    public bool HasBlockingError => Checks.Any(c => !c.Passed && c.Severity == PreflightSeverity.Error);
}
