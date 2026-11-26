using System.Threading;
using System.Threading.Tasks;
using k8s;

namespace Reeve.Workloads.Providers.Kubernetes;

internal interface IKubernetesClusterInspector
{
    Task<KubernetesClusterInspection> InspectAsync(IKubernetes client, string @namespace, CancellationToken ct);
}
