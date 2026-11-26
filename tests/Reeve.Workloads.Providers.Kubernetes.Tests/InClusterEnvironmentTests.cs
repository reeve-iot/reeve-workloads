using FluentAssertions;
using Reeve.Workloads.Providers.Kubernetes;
using Xunit;

namespace Reeve.Workloads.Providers.Kubernetes.Tests;

public sealed class InClusterEnvironmentTests
{
    [Fact]
    public void InClusterEnvironment_is_true_when_service_account_token_present()
    {
        var env = new InClusterEnvironment(
            fileExists: path => path == InClusterEnvironment.ServiceAccountTokenPath,
            tokenPath: InClusterEnvironment.ServiceAccountTokenPath);

        env.IsInCluster.Should().BeTrue();
    }

    [Fact]
    public void InClusterEnvironment_is_false_when_service_account_token_absent()
    {
        var env = new InClusterEnvironment(
            fileExists: _ => false,
            tokenPath: InClusterEnvironment.ServiceAccountTokenPath);

        env.IsInCluster.Should().BeFalse();
    }
}
