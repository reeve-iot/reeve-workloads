using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Reeve.Workloads.Providers.Kubernetes;
using SettingsMode = Reeve.Workloads.Providers.Kubernetes.KubernetesConnectionMode;
using Xunit;

namespace Reeve.Workloads.Providers.Kubernetes.Tests;

public sealed class KubernetesConnectionResolverTests
{
    private static IConfiguration Config(params (string Key, string? Value)[] pairs)
    {
        var dict = new Dictionary<string, string?>();
        foreach (var (key, value) in pairs)
        {
            dict[key] = value;
        }

        return new ConfigurationBuilder().AddInMemoryCollection(dict).Build();
    }

    private static string Kubeconfig(string server) =>
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

    [Fact]
    public void Resolve_returns_InCluster_when_no_settings_mode_and_no_env_seam()
    {
        var source = KubernetesConnectionResolver.Resolve(Config(), new KubernetesProviderSettings());

        source.Mode.Should().Be(KubernetesConnectionKind.InCluster);
        source.KubeconfigPath.Should().BeNull();
        source.KubeconfigContent.Should().BeNull();
    }

    [Fact]
    public void Resolve_returns_InCluster_when_AllowKubeconfig_is_false_even_if_a_path_is_set()
    {
        var source = KubernetesConnectionResolver.Resolve(
            Config(
                ("Workloads:Kubernetes:AllowKubeconfig", "false"),
                ("Workloads:Kubernetes:KubeconfigPath", "/some/kubeconfig.yaml")),
            new KubernetesProviderSettings());

        source.Mode.Should().Be(KubernetesConnectionKind.InCluster);
    }

    [Fact]
    public void Resolve_returns_InCluster_when_AllowKubeconfig_is_not_a_boolean()
    {
        var source = KubernetesConnectionResolver.Resolve(
            Config(
                ("Workloads:Kubernetes:AllowKubeconfig", "yes"),
                ("Workloads:Kubernetes:KubeconfigPath", "/some/kubeconfig.yaml")),
            new KubernetesProviderSettings { ConnectionMode = SettingsMode.InCluster });

        source.Mode.Should().Be(KubernetesConnectionKind.InCluster);
    }

    [Fact]
    public void Resolve_returns_dev_seam_only_when_allowed_and_path_non_empty_and_settings_inCluster()
    {
        var source = KubernetesConnectionResolver.Resolve(
            Config(
                ("Workloads:Kubernetes:AllowKubeconfig", "true"),
                ("Workloads:Kubernetes:KubeconfigPath", "/home/dev/.kube/k3d.yaml")),
            new KubernetesProviderSettings { ConnectionMode = SettingsMode.InCluster });

        source.Mode.Should().Be(KubernetesConnectionKind.Kubeconfig);
        source.KubeconfigPath.Should().Be("/home/dev/.kube/k3d.yaml");
    }

    [Fact]
    public void Resolve_returns_Remote_when_settings_mode_is_remote_with_content()
    {
        var yaml = Kubeconfig("https://remote.example:6443");
        var source = KubernetesConnectionResolver.Resolve(
            Config(),
            new KubernetesProviderSettings
            {
                ConnectionMode = SettingsMode.RemoteKubeconfig,
                Kubeconfig = yaml,
                KubeconfigContext = "prod",
            });

        source.Mode.Should().Be(KubernetesConnectionKind.RemoteKubeconfig);
        source.KubeconfigContent.Should().Be(yaml);
        source.Context.Should().Be("prod");
    }

    [Fact]
    public void Resolve_Remote_wins_over_the_dev_seam()
    {
        var yaml = Kubeconfig("https://remote.example:6443");
        var source = KubernetesConnectionResolver.Resolve(
            Config(
                ("Workloads:Kubernetes:AllowKubeconfig", "true"),
                ("Workloads:Kubernetes:KubeconfigPath", "/home/dev/.kube/k3d.yaml")),
            new KubernetesProviderSettings
            {
                ConnectionMode = SettingsMode.RemoteKubeconfig,
                Kubeconfig = yaml,
            });

        source.Mode.Should().Be(KubernetesConnectionKind.RemoteKubeconfig);
        source.KubeconfigContent.Should().Be(yaml);
    }

    [Fact]
    public void Resolve_explicit_Remote_with_empty_kubeconfig_stays_Remote_and_does_not_fall_through_to_the_dev_seam()
    {
        var source = KubernetesConnectionResolver.Resolve(
            Config(
                ("Workloads:Kubernetes:AllowKubeconfig", "true"),
                ("Workloads:Kubernetes:KubeconfigPath", "/home/dev/.kube/k3d.yaml")),
            new KubernetesProviderSettings
            {
                ConnectionMode = SettingsMode.RemoteKubeconfig,
                Kubeconfig = "   ",
            });

        source.Mode.Should().Be(KubernetesConnectionKind.RemoteKubeconfig);
    }

    [Fact]
    public void Resolve_explicit_Remote_with_empty_kubeconfig_and_no_seam_stays_Remote_not_InCluster()
    {
        var source = KubernetesConnectionResolver.Resolve(
            Config(),
            new KubernetesProviderSettings
            {
                ConnectionMode = SettingsMode.RemoteKubeconfig,
                Kubeconfig = null,
            });

        source.Mode.Should().Be(KubernetesConnectionKind.RemoteKubeconfig);
    }

    [Fact]
    public void Fingerprint_incluster_is_the_stable_literal()
    {
        KubernetesConnectionSource.InCluster().Fingerprint.Should().Be("incluster");
    }

    [Fact]
    public void Fingerprint_kubeconfig_file_embeds_the_path()
    {
        KubernetesConnectionSource.KubeconfigFile("/a/b.yaml").Fingerprint.Should().Be("kubeconfigfile:/a/b.yaml");
    }

    [Fact]
    public void Fingerprint_remote_is_stable_for_the_same_content_and_context()
    {
        var yaml = Kubeconfig("https://remote.example:6443");
        var a = KubernetesConnectionSource.Remote(yaml, "ctx").Fingerprint;
        var b = KubernetesConnectionSource.Remote(yaml, "ctx").Fingerprint;

        b.Should().Be(a);
    }

    [Fact]
    public void Fingerprint_remote_changes_when_the_kubeconfig_content_changes()
    {
        var a = KubernetesConnectionSource.Remote(Kubeconfig("https://one.example:6443"), "ctx").Fingerprint;
        var b = KubernetesConnectionSource.Remote(Kubeconfig("https://two.example:6443"), "ctx").Fingerprint;

        b.Should().NotBe(a);
    }

    [Fact]
    public void Fingerprint_remote_changes_when_the_context_changes()
    {
        var yaml = Kubeconfig("https://remote.example:6443");
        var a = KubernetesConnectionSource.Remote(yaml, "ctx-a").Fingerprint;
        var b = KubernetesConnectionSource.Remote(yaml, "ctx-b").Fingerprint;

        b.Should().NotBe(a);
    }

    [Fact]
    public void Fingerprint_remote_never_contains_the_plaintext_kubeconfig()
    {
        var yaml = Kubeconfig("https://remote.example:6443");
        var fingerprint = KubernetesConnectionSource.Remote(yaml, "ctx").Fingerprint;

        fingerprint.Should().NotContain("dummy-token-not-a-real-credential");
        fingerprint.Should().NotContain("BEGIN");
        fingerprint.Should().NotContain(yaml);
        fingerprint.Should().StartWith("remote:");
    }

    [Fact]
    public void Fingerprint_remote_uses_the_sha256_hex_of_the_content()
    {
        var yaml = Kubeconfig("https://remote.example:6443");
        var expectedHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(yaml))).ToLowerInvariant();

        KubernetesConnectionSource.Remote(yaml, "ctx").Fingerprint.Should().Be($"remote:{expectedHash}:ctx");
    }
}
