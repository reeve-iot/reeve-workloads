using System.Threading;
using System.Threading.Tasks;
using Reeve.Workloads.Abstractions.Contracts;

namespace Reeve.Workloads.Abstractions.Network;

/// <summary>The ONE place that resolves the effective workload provider and dispatches attachment enumeration to
/// exactly one source (Kubernetes/Docker/Manual). Degrades to an empty list + reason, never throws except on caller cancellation.</summary>
public interface INetworkAttachmentEnumerationService
{
    /// <summary>Enumerates attachment targets for the effective provider. Always well-formed (200 material): a
    /// populated list, or an empty list plus a closed-vocabulary reason. Never throws for a provider fault.</summary>
    Task<WorkloadNetworkAttachmentListResponse> ListAsync(CancellationToken cancellationToken);
}
