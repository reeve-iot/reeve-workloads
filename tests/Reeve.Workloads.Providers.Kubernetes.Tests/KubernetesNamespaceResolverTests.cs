using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Reeve.Workloads.Providers.Kubernetes.Tests;

public sealed class KubernetesNamespaceResolverTests
{
    private static KubernetesNamespaceResolver Resolver(string? podNamespace, bool devKubeconfigSeam = false)
        => new(
            new InClusterEnvironment(_ => true, InClusterEnvironment.ServiceAccountTokenPath, () => podNamespace),
            new ConfigurationBuilder()
                .AddInMemoryCollection(devKubeconfigSeam
                    ? new Dictionary<string, string?>
                    {
                        [KubernetesConnectionResolver.AllowKubeconfigKey] = "true",
                        [KubernetesConnectionResolver.KubeconfigPathKey] = "/dev/kubeconfig",
                    }
                    : new Dictionary<string, string?>())
                .Build());

    [Fact]
    public void A_configured_namespace_wins_over_the_pods_and_is_trimmed()
        => Resolver("pod-ns").Resolve(new KubernetesProviderSettings { Namespace = " configured\n" })
            .Should().Be("configured");

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void A_blank_namespace_in_cluster_resolves_to_the_pods_own(string? configured)
        => Resolver("pod-ns").Resolve(new KubernetesProviderSettings { Namespace = configured })
            .Should().Be("pod-ns");

    [Fact]
    public void A_blank_namespace_in_cluster_with_no_pod_namespace_resolves_to_none()
        => Resolver(null).Resolve(new KubernetesProviderSettings()).Should().BeNull();

    [Fact]
    public void A_blank_namespace_on_a_remote_kubeconfig_resolves_to_none_even_inside_a_pod()
        => Resolver("pod-ns").Resolve(new KubernetesProviderSettings
        {
            ConnectionMode = KubernetesConnectionMode.RemoteKubeconfig,
        }).Should().BeNull();

    [Fact]
    public void A_configured_namespace_on_a_remote_kubeconfig_is_used()
        => Resolver(null).Resolve(new KubernetesProviderSettings
        {
            Namespace = "remote-ns",
            ConnectionMode = KubernetesConnectionMode.RemoteKubeconfig,
        }).Should().Be("remote-ns");

    [Fact]
    public void A_blank_namespace_on_the_dev_kubeconfig_seam_resolves_to_none_even_inside_a_pod()
        => Resolver("pod-ns", devKubeconfigSeam: true).Resolve(new KubernetesProviderSettings()).Should().BeNull();
}
