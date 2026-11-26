using System.Threading;
using System.Threading.Tasks;
using Reeve.Workloads.Abstractions.Contracts;

namespace Reeve.Workloads.Abstractions.Network;

/// <summary>ONE provider's answer to "what can a workload attach to?". A host compiling a provider in must register
/// its source: an absent one degrades to <c>notSupported</c>, the manual provider's honest answer and nobody else's.</summary>
public interface INetworkAttachmentSource
{
    /// <summary>The provider this source speaks for. Exactly one source per provider.</summary>
    WorkloadProviderId Provider { get; }

    /// <summary>Enumerates this provider's attachment targets by name from already-resolved settings. Degrades to an
    /// empty list plus a closed-vocabulary reason; only caller cancellation may propagate.</summary>
    Task<WorkloadNetworkAttachmentListResponse> ListAsync(
        WorkloadOrchestratorSettings settings,
        CancellationToken cancellationToken);

    /// <summary>The operator-facing sentence for an attachment that is not listed, in this provider's own
    /// vocabulary ("no NetworkAttachmentDefinition", "not present on the docker daemon").</summary>
    string DescribeMissing(string name);
}
