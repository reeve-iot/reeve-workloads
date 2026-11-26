using System.Threading;
using System.Threading.Tasks;
using k8s;

namespace Reeve.Workloads.Providers.Kubernetes;

internal interface IKubernetesClientFactory
{
    Task<IKubernetes> CreateClientAsync(CancellationToken cancellationToken = default);
}
