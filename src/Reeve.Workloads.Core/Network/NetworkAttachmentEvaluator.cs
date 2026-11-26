using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Reeve.Workloads.Abstractions.Contracts;
using Reeve.Workloads.Abstractions.Contracts.Deployment;
using Reeve.Workloads.Abstractions.Network;
using Reeve.Workloads.Abstractions.Providers;

namespace Reeve.Workloads.Core.Network;

/// <summary>The single home of every <c>network-attachment-*</c> check id; it LAUNCHES nothing. It judges an
/// operator network by its name and whether it exists, and reads nothing the network object contains.</summary>
public sealed class NetworkAttachmentEvaluator : INetworkAttachmentEvaluator
{
    private const string NoneInNamespaceReason = "none-in-namespace";

    private readonly IWorkloadProviderRegistry _providerRegistry;
    private readonly IReadOnlyDictionary<WorkloadProviderId, INetworkAttachmentSource> _sources;

    public NetworkAttachmentEvaluator(
        IWorkloadProviderRegistry providerRegistry,
        IEnumerable<INetworkAttachmentSource> sources)
    {
        _providerRegistry = providerRegistry ?? throw new ArgumentNullException(nameof(providerRegistry));
        ArgumentNullException.ThrowIfNull(sources);

        var byProvider = new Dictionary<WorkloadProviderId, INetworkAttachmentSource>();
        foreach (var source in sources)
        {
            byProvider[source.Provider] = source;
        }

        _sources = byProvider;
    }

    /// <inheritdoc />
    public Task<NetworkAttachmentEvaluation> EvaluateAsync(
        NetworkAttachmentEvaluationRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (request.Mode != WorkloadNetworkMode.OperatorNetwork)
            return Task.FromResult(NetworkAttachmentEvaluation.NotEvaluated);

        return Task.FromResult(Evaluate(request));
    }

    private NetworkAttachmentEvaluation Evaluate(NetworkAttachmentEvaluationRequest request)
    {
        var checks = new List<PreflightCheck>();

        if (NodeEligibilityCheck(request) is { } eligibility)
            checks.Add(eligibility);

        var source = _sources.GetValueOrDefault(request.Provider);
        if (source is null)
        {
            checks.Add(PreflightCheck.Pass(
                NetworkAttachmentCheckIds.Exists,
                $"This workload runs on the {Traits(request).Label} provider, which reeve cannot interrogate for "
                    + "networks, so it cannot verify that the named one exists — the network is configured out of "
                    + "band.",
                PreflightSeverity.Info));

            return new NetworkAttachmentEvaluation { Checks = checks };
        }

        if (IsEnumerationUnavailable(request.EnumerationUnavailableReason))
        {
            checks.Add(PreflightCheck.Fail(
                NetworkAttachmentCheckIds.Exists,
                PreflightSeverity.Error,
                $"Reeve could not enumerate the {Traits(request).Location} networks this workload attaches "
                    + $"to (reason: {request.EnumerationUnavailableReason}), so it cannot confirm that "
                    + $"{FormatNames(request.Attachments)} exist. The install is refused rather than proceeding "
                    + "onto an unverified network."));

            return new NetworkAttachmentEvaluation { Checks = checks };
        }

        if (request.Attachments.Count == 0)
        {
            checks.Add(PreflightCheck.Fail(
                NetworkAttachmentCheckIds.Exists,
                PreflightSeverity.Error,
                "This workload requests the operatorNetwork mode but names no attachment, so there is nothing to "
                    + "verify and nothing to attach to."));

            return new NetworkAttachmentEvaluation { Checks = checks };
        }

        var missing = request.Attachments.Where(a => !a.Exists).ToList();
        if (missing.Count > 0)
        {
            checks.Add(PreflightCheck.Fail(
                NetworkAttachmentCheckIds.Exists,
                PreflightSeverity.Error,
                string.Join(" ", missing.Select(a => source.DescribeMissing(a.Name)))));

            return new NetworkAttachmentEvaluation { Checks = checks };
        }

        checks.Add(PreflightCheck.Pass(
            NetworkAttachmentCheckIds.Exists,
            $"{FormatNames(request.Attachments)} "
                + (request.Attachments.Count == 1 ? "is" : "are")
                + $" present on the {Traits(request).Location} provider."));

        return new NetworkAttachmentEvaluation { Checks = checks };
    }

    private PreflightCheck? NodeEligibilityCheck(NetworkAttachmentEvaluationRequest request)
    {
        if (!Traits(request).SchedulesAcrossNodes)
            return null;

        var hasSelector = request.NodeEligibilitySelector is { Count: > 0 };
        var fanOut = request.Kind is WorkloadKind.PerNode or WorkloadKind.ScanPerNode;

        if (hasSelector)
        {
            return PreflightCheck.Pass(
                NetworkAttachmentCheckIds.NodeEligibility,
                "A node-eligibility selector scopes this workload to the nodes that carry the attachment's parent "
                    + "interface.");
        }

        if (fanOut)
        {
            return PreflightCheck.Fail(
                NetworkAttachmentCheckIds.NodeEligibility,
                PreflightSeverity.Error,
                "This workload fans out across every matching node, but no node-eligibility selector scopes it to "
                    + "the nodes that carry the attachment's parent interface. On a node without that interface "
                    + "the CNI attach fails and the pod stays in ContainerCreating forever, with no completion and "
                    + "no cleanup. Add a node-eligibility selector and retry.",
                "Label the nodes that carry the attachment's parent interface and set the workload's "
                    + "node-eligibility selector to match them.");
        }

        return PreflightCheck.Fail(
            NetworkAttachmentCheckIds.NodeEligibility,
            PreflightSeverity.Warning,
            "No node-eligibility selector is set, so the scheduler may place this workload on a node that does not "
                + "carry the attachment's parent interface. That failure is a single visible pod rather than a "
                + "silent fan-out, so it is not blocked here — but a selector avoids it.",
            "Label the nodes that carry the attachment's parent interface and set the workload's node-eligibility "
                + "selector to match them.");
    }

    private static bool IsEnumerationUnavailable(string? reason)
        => !string.IsNullOrWhiteSpace(reason)
            && !string.Equals(reason.Trim(), NoneInNamespaceReason, StringComparison.OrdinalIgnoreCase);

    private WorkloadProviderDescriptor Traits(NetworkAttachmentEvaluationRequest request)
        => _providerRegistry.Describe(request.Provider);

    private static string FormatNames(IReadOnlyList<NetworkAttachmentObservation> attachments)
        => attachments.Count == 0
            ? "no attachment"
            : string.Join(", ", attachments.Select(a => $"'{a.Name}'"));
}
