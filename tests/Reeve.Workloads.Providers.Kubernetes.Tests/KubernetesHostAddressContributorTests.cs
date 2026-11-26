using FluentAssertions;
using Reeve.Workloads.Abstractions;
using Reeve.Workloads.Providers.Kubernetes;
using Xunit;

namespace Reeve.Workloads.Providers.Kubernetes.Tests;

public sealed class KubernetesHostAddressContributorTests
{
    private static KubernetesHostAddressContributor Contributor(string? namespaceFile)
        => new(new InClusterEnvironment(_ => true, InClusterEnvironment.ServiceAccountTokenPath, () => namespaceFile));

    [Fact]
    public void It_answers_for_the_kubernetes_provider()
        => Contributor("reeve").Provider.Should().Be(KubernetesProvider.Id);

    [Fact]
    public void An_explicit_namespace_is_used_and_the_pods_is_not()
        => Contributor("reeve").AddressOf(new HostEndpoint("reeve-backend", 3000, "home"))
            .Should().Be("reeve-backend.home.svc.cluster.local:3000");

    [Fact]
    public void A_padded_explicit_namespace_is_trimmed()
        => Contributor("reeve").AddressOf(new HostEndpoint("reeve-backend", 3000, " home\n"))
            .Should().Be("reeve-backend.home.svc.cluster.local:3000");

    [Fact]
    public void The_port_is_always_written_even_when_it_is_80()
        => Contributor("reeve").AddressOf(new HostEndpoint("reeve-backend", 80, "reeve"))
            .Should().Be("reeve-backend.reeve.svc.cluster.local:80");

    [Theory]
    [InlineData("reeve")]
    [InlineData("  reeve\n")]
    public void A_null_namespace_answers_with_the_pods_trimmed(string namespaceFile)
        => Contributor(namespaceFile).AddressOf(new HostEndpoint("reeve-backend", 80))
            .Should().Be("reeve-backend.reeve.svc.cluster.local:80");

    [Fact]
    public void A_null_namespace_with_no_namespace_file_answers_null()
        => Contributor(namespaceFile: null).AddressOf(new HostEndpoint("reeve-backend", 80))
            .Should().BeNull();

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\n")]
    public void A_null_namespace_with_a_blank_namespace_file_answers_null(string namespaceFile)
        => Contributor(namespaceFile).AddressOf(new HostEndpoint("reeve-backend", 80))
            .Should().BeNull("a blank file is an unknown namespace, never a DNS name with an empty label");

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    public void A_blank_explicit_namespace_answers_null_and_does_not_fall_back_to_the_pods(string blank)
        => Contributor("reeve").AddressOf(new HostEndpoint("reeve-backend", 80, blank))
            .Should().BeNull("only null means the pod's namespace; a blank one names no namespace at all");

    [Fact]
    public void The_pods_namespace_is_read_once_at_construction()
    {
        var reads = 0;
        var environment = new InClusterEnvironment(
            _ => true, InClusterEnvironment.ServiceAccountTokenPath, () => { reads++; return "reeve"; });
        var contributor = new KubernetesHostAddressContributor(environment);

        contributor.AddressOf(new HostEndpoint("a", 1));
        contributor.AddressOf(new HostEndpoint("b", 2));

        reads.Should().Be(1);
    }

    [Fact]
    public void The_environment_reports_a_blank_namespace_file_as_unknown()
        => new InClusterEnvironment(_ => true, InClusterEnvironment.ServiceAccountTokenPath, () => " \t\n")
            .Namespace.Should().BeNull();

    [Fact]
    public void The_namespace_is_read_from_where_kubernetes_projects_it()
        => InClusterEnvironment.NamespacePath.Should().Be("/var/run/secrets/kubernetes.io/serviceaccount/namespace");

    [Theory]
    [InlineData("reeve", "reeve")]
    [InlineData("reeve\n", "reeve")]
    [InlineData("", null)]
    [InlineData(" \n", null)]
    public void The_environment_reads_the_namespace_file_on_disk(string content, string? expected)
    {
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, content);

            new InClusterEnvironment(_ => true, InClusterEnvironment.ServiceAccountTokenPath, namespacePath: path)
                .Namespace.Should().Be(expected);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void A_missing_namespace_file_on_disk_is_unknown()
        => new InClusterEnvironment(
                _ => true,
                InClusterEnvironment.ServiceAccountTokenPath,
                namespacePath: Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "namespace"))
            .Namespace.Should().BeNull();

    [Fact]
    public void An_unreadable_namespace_file_is_unknown_rather_than_a_crash()
    {
        if (OperatingSystem.IsWindows() || Environment.IsPrivilegedProcess)
        {
            return;
        }

        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, "reeve");
            File.SetUnixFileMode(path, UnixFileMode.None);

            new InClusterEnvironment(_ => true, InClusterEnvironment.ServiceAccountTokenPath, namespacePath: path)
                .Namespace.Should().BeNull();
        }
        finally
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            File.Delete(path);
        }
    }

    [Fact]
    public void A_namespace_file_whose_read_faults_is_unknown_rather_than_a_crash()
    {
        const string path = "/proc/self/mem";
        if (!OperatingSystem.IsLinux() || !File.Exists(path))
        {
            return;
        }

        new InClusterEnvironment(_ => true, InClusterEnvironment.ServiceAccountTokenPath, namespacePath: path)
            .Namespace.Should().BeNull();
    }
}
