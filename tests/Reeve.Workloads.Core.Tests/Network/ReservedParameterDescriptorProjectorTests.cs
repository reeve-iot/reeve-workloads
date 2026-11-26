using FluentAssertions;
using Reeve.Workloads.Abstractions.Contracts.Deployment;
using Reeve.Workloads.Abstractions.Providers;
using Reeve.Workloads.Core.Network;
using Reeve.Workloads.DeploymentFixtures;
using Reeve.Workloads.Domain.DesiredSpec;
using Reeve.Workloads.Providers.Docker;
using Reeve.Workloads.Providers.Kubernetes;
using Reeve.Workloads.Providers.Manual;

namespace Reeve.Workloads.Core.Tests.Network;

public sealed class ReservedParameterDescriptorProjectorTests
{
    private static readonly WorkloadManifest EveryReservedName = DeploymentManifestBuilder.Workload(
        network: new WorkloadNetwork
        {
            HostNetwork = true,
            Ports = [new WorkloadPort { Name = "api", Port = 8080, Protocol = "TCP" }],
        },
        privileges: new WorkloadPrivileges { HostDbus = true },
        volumes: [new WorkloadVolume
        {
            Id = "data", MountPath = "/data", Size = "1Gi", Criticality = VolumeCriticality.Recoverable,
        }]);

    public static TheoryData<string> Providers => ["kubernetes", "docker", "manual"];

    [Theory]
    [MemberData(nameof(Providers))]
    public void A_provider_is_offered_exactly_the_names_its_resolver_synthesizes(string providerName)
    {
        var provider = Describe(providerName);

        var applicable = ReservedParameterDescriptorProjector.Project(EveryReservedName, provider)
            .Where(descriptor => descriptor.ProviderApplicable)
            .Select(descriptor => descriptor.Name);

        applicable.Should().BeEquivalentTo(
            WorkloadReservedParameters.For(EveryReservedName, provider).Select(pair => pair.Key),
            "the wizard offers what the resolver synthesizes, from the same producer");
    }

    [Fact]
    public void Without_a_provider_every_unconditional_name_of_the_producer_is_projected_with_its_type()
    {
        var projected = ReservedParameterDescriptorProjector.Project(EveryReservedName, provider: null)
            .Select(descriptor => (descriptor.Name, descriptor.Type));

        projected.Should().BeEquivalentTo(
            WorkloadReservedParameters.For(EveryReservedName, provider: null)
                .Where(pair => !ReservedParameterDescriptorProjector.IsProviderConditional(pair.Key))
                .Select(pair => (pair.Key, pair.Value)));
    }

    private static WorkloadProviderDescriptor Describe(string name) => name switch
    {
        "kubernetes" => KubernetesProvider.Descriptor,
        "docker" => DockerProvider.Descriptor,
        _ => ManualProvider.Descriptor,
    };
}
