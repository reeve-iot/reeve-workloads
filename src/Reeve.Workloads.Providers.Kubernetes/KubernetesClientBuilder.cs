using System.IO;
using System.Text;
using k8s;

namespace Reeve.Workloads.Providers.Kubernetes;

internal static class KubernetesClientBuilder
{
    public static IKubernetes Build(KubernetesConnectionSource source)
        => Build(source, httpTimeout: null);

    public static IKubernetes Build(KubernetesConnectionSource source, TimeSpan? httpTimeout)
    {
        switch (source.Mode)
        {
            case KubernetesConnectionKind.RemoteKubeconfig:
                if (string.IsNullOrWhiteSpace(source.KubeconfigContent))
                {
                    throw new InvalidOperationException(
                        "remoteKubeconfig is configured but the stored kubeconfig is empty/undecryptable.");
                }

                using (var stream = new MemoryStream(Encoding.UTF8.GetBytes(source.KubeconfigContent)))
                {
                    var k8sConfig = KubernetesClientConfiguration.LoadKubeConfig(stream);
                    var remoteConfig = KubernetesClientConfiguration.BuildConfigFromConfigObject(
                        k8sConfig, currentContext: source.Context);

                    return new k8s.Kubernetes(WithTimeout(remoteConfig, httpTimeout));
                }

            case KubernetesConnectionKind.Kubeconfig:
                var fileConfig = KubernetesClientConfiguration.BuildConfigFromConfigFile(source.KubeconfigPath);
                return new k8s.Kubernetes(WithTimeout(fileConfig, httpTimeout));

            case KubernetesConnectionKind.InCluster:
            default:
                var inClusterConfig = KubernetesClientConfiguration.InClusterConfig();
                return new k8s.Kubernetes(WithTimeout(inClusterConfig, httpTimeout));
        }
    }

    private static KubernetesClientConfiguration WithTimeout(KubernetesClientConfiguration config, TimeSpan? httpTimeout)
    {
        if (httpTimeout is { } timeout && timeout > TimeSpan.Zero)
        {
            config.HttpClientTimeout = timeout;
        }

        return config;
    }
}
