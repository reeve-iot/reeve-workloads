using System.Text.Json;
using FluentAssertions;
using Xunit;

namespace Reeve.Workloads.Providers.Kubernetes.Tests;

public sealed class KubernetesProviderSettingsCodecTests
{
    private static string Kubeconfig(string server) => $"""
        apiVersion: v1
        kind: Config
        current-context: remote
        clusters:
        - name: remote-cluster
          cluster:
            server: {server}
            certificate-authority-data: ZmFrZS1jYS1kYXRh
        contexts:
        - name: remote
          context:
            cluster: remote-cluster
            user: remote-user
        users:
        - name: remote-user
          user:
            token: t
        """;

    private static JsonElement Entry(KubernetesProviderSettings settings) => JsonSerializer.SerializeToElement(settings);

    [Fact]
    public void Placement_of_an_in_cluster_entry_is_the_namespace_and_the_mode()
    {
        var placement = new KubernetesProviderSettingsCodec().Placement(Entry(new KubernetesProviderSettings { Namespace = "reeve" }));

        placement.Should().BeEquivalentTo(new Dictionary<string, string> { ["namespace"] = "reeve", ["connectionMode"] = "InCluster" });
    }

    [Fact]
    public void Placement_of_a_remote_entry_adds_the_resolved_server()
    {
        var placement = new KubernetesProviderSettingsCodec().Placement(Entry(new KubernetesProviderSettings
        {
            ConnectionMode = KubernetesConnectionMode.RemoteKubeconfig,
            Kubeconfig = Kubeconfig("https://10.0.0.1:6443"),
        }));

        placement["namespace"].Should().BeEmpty();
        placement["server"].Should().Be("https://10.0.0.1:6443");
    }

    [Theory]
    [InlineData("https://API.example:6443/", "https://api.example:6443")]
    [InlineData("HTTPS://user:pass@api.example:6443", "https://api.example:6443")]
    [InlineData("https://api.example:6443/k8s/clusters/c-1/", "https://api.example:6443/k8s/clusters/c-1")]
    public void The_server_is_normalised(string written, string expected)
        => KubeconfigValidator.ResolveServer(Kubeconfig(written)).Should().Be(expected);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not a kubeconfig [")]
    public void An_unresolvable_kubeconfig_has_no_server(string? kubeconfig)
        => KubeconfigValidator.ResolveServer(kubeconfig).Should().BeNull();
}
