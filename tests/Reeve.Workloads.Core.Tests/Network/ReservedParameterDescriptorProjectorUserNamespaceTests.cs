using FluentAssertions;
using Reeve.Workloads.Abstractions.Contracts.Deployment;
using Reeve.Workloads.Core.Network;
using Reeve.Workloads.DeploymentFixtures;
using Reeve.Workloads.Providers.Docker;
using Reeve.Workloads.Providers.Kubernetes;
using Reeve.Workloads.Providers.Manual;

namespace Reeve.Workloads.Core.Tests.Network;

public sealed class ReservedParameterDescriptorProjectorUserNamespaceTests
{
    [Fact]
    public void Kubernetes_projects_a_renderable_boolean_defaulting_to_false()
    {
        var descriptor = ReservedParameterDescriptorProjector.Project(
                DeploymentManifestBuilder.Workload(), KubernetesProvider.Descriptor)
            .Should().ContainSingle(x => x.Name == "security.userNamespace").Subject;
        descriptor.Default.Should().Be("false");
        descriptor.Type.Should().Be(WorkloadParameterType.Boolean);
        descriptor.Hint.Should().NotBeNullOrWhiteSpace();
        descriptor.ProviderApplicable.Should().BeTrue();
        descriptor.Renderable.Should().BeTrue();
    }

    [Fact]
    public void Docker_and_manual_project_it_neither_applicable_nor_renderable()
    {
        foreach (var provider in new[] { DockerProvider.Descriptor, ManualProvider.Descriptor })
        {
            var descriptor = ReservedParameterDescriptorProjector.Project(DeploymentManifestBuilder.Workload(), provider)
                .Should().ContainSingle(x => x.Name == "security.userNamespace").Subject;
            descriptor.ProviderApplicable.Should().BeFalse();
            descriptor.Renderable.Should().BeFalse();
        }
    }

    [Fact]
    public void No_provider_omits_it_and_the_name_is_provider_conditional()
    {
        ReservedParameterDescriptorProjector.Project(DeploymentManifestBuilder.Workload(), provider: null)
            .Should().NotContain(x => x.Name == "security.userNamespace");
        ReservedParameterDescriptorProjector.IsProviderConditional("security.userNamespace").Should().BeTrue();
    }

    [Fact]
    public void A_host_networked_workload_is_still_offered_the_switch()
        => ReservedParameterDescriptorProjector.Project(
                DeploymentManifestBuilder.Workload(network: new WorkloadNetwork { HostNetwork = true }),
                KubernetesProvider.Descriptor)
            .Should().Contain(x => x.Name == "security.userNamespace" && x.Renderable);
}
