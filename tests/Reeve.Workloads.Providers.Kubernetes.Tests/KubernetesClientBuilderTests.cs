using System;
using System.IO;
using FluentAssertions;
using k8s;
using Reeve.Workloads.Providers.Kubernetes;
using Xunit;

namespace Reeve.Workloads.Providers.Kubernetes.Tests;

public sealed class KubernetesClientBuilderTests
{
    private static string SingleContextKubeconfig(string server) =>
        $"""
        apiVersion: v1
        kind: Config
        clusters:
        - name: reeve
          cluster:
            server: {server}
            insecure-skip-tls-verify: true
        contexts:
        - name: reeve
          context:
            cluster: reeve
            user: reeve
        current-context: reeve
        users:
        - name: reeve
          user:
            token: dummy-token-not-a-real-credential
        """;

    private static string TwoContextKubeconfig(string serverA, string serverB) =>
        $"""
        apiVersion: v1
        kind: Config
        clusters:
        - name: cluster-a
          cluster:
            server: {serverA}
            insecure-skip-tls-verify: true
        - name: cluster-b
          cluster:
            server: {serverB}
            insecure-skip-tls-verify: true
        contexts:
        - name: ctx-a
          context:
            cluster: cluster-a
            user: reeve
        - name: ctx-b
          context:
            cluster: cluster-b
            user: reeve
        current-context: ctx-a
        users:
        - name: reeve
          user:
            token: dummy-token-not-a-real-credential
        """;

    [Fact]
    public void Build_remote_from_in_memory_content_produces_a_client_with_the_expected_host()
    {
        var source = KubernetesConnectionSource.Remote(SingleContextKubeconfig("https://remote.example:6443"), context: null);

        using var client = KubernetesClientBuilder.Build(source);

        client.Should().NotBeNull();
        client.BaseUri.Should().Be(new Uri("https://remote.example:6443/"));
    }

    [Fact]
    public void Build_remote_honours_an_explicit_non_current_context()
    {
        var yaml = TwoContextKubeconfig("https://cluster-a.example:6443", "https://cluster-b.example:6443");
        var source = KubernetesConnectionSource.Remote(yaml, context: "ctx-b");

        using var client = KubernetesClientBuilder.Build(source);

        client.BaseUri.Should().Be(new Uri("https://cluster-b.example:6443/"));
    }

    [Fact]
    public void Build_remote_with_null_context_uses_the_kubeconfig_current_context()
    {
        var yaml = TwoContextKubeconfig("https://cluster-a.example:6443", "https://cluster-b.example:6443");
        var source = KubernetesConnectionSource.Remote(yaml, context: null);

        using var client = KubernetesClientBuilder.Build(source);

        client.BaseUri.Should().Be(new Uri("https://cluster-a.example:6443/"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Build_remote_with_empty_content_throws_fail_closed(string content)
    {
        var source = KubernetesConnectionSource.Remote(content, context: null);

        var act = () => KubernetesClientBuilder.Build(source);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*empty/undecryptable*");
    }

    [Fact]
    public void Build_kubeconfig_file_seam_produces_a_client_with_the_expected_host()
    {
        var tempPath = Path.Combine(Path.GetTempPath(), $"reeve-workloads-kubeconfig-{Guid.NewGuid():N}.yaml");
        File.WriteAllText(tempPath, SingleContextKubeconfig("https://127.0.0.1:6443"));

        try
        {
            using var client = KubernetesClientBuilder.Build(KubernetesConnectionSource.KubeconfigFile(tempPath));

            client.Should().NotBeNull();
            client.BaseUri.Should().Be(new Uri("https://127.0.0.1:6443/"));
        }
        finally
        {
            File.Delete(tempPath);
        }
    }
}
