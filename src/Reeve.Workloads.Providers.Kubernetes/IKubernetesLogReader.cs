using System.Threading;
using System.Threading.Tasks;
using k8s;
using Reeve.Workloads.Abstractions.Providers;

namespace Reeve.Workloads.Providers.Kubernetes;

internal interface IKubernetesLogReader
{
    Task<WorkloadLogs> ReadAsync(
        IKubernetes client,
        string @namespace,
        WorkloadRef reference,
        int tailLines,
        bool previousContainer,
        CancellationToken ct);
}
