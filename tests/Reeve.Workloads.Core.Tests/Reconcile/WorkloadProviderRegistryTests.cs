using FluentAssertions;
using NSubstitute;
using Reeve.Workloads.Abstractions.Contracts;
using Reeve.Workloads.Abstractions.Providers;
using Reeve.Workloads.Core.Providers;
using Reeve.Workloads.Core.Tests;
using Reeve.Workloads.Providers.Docker;
using Reeve.Workloads.Providers.Kubernetes;

namespace Reeve.Workloads.Core.Tests.Reconcile;

public sealed class WorkloadProviderRegistryTests
{
    [Fact]
    public void An_empty_registry_answers_null_rather_than_throwing()
    {
        var registry = new WorkloadProviderRegistry([]);

        registry.TryResolve(DockerProvider.Id).Should().BeNull(
            "a host with no provider registered is an expected state, not a fault");
        registry.RegisteredProviders.Should().BeEmpty();
    }

    [Fact]
    public void A_provider_with_no_package_in_this_build_is_described_by_the_defaults()
    {
        var registry = new WorkloadProviderRegistry([Provider(KubernetesProvider.Id)]);
        var docker = WorkloadProviderId.Parse("docker");

        registry.Describe(docker).Should().Be(new WorkloadProviderDescriptor { Id = docker });
    }

    [Fact]
    public void A_missing_kind_throws_only_on_the_demanding_call()
    {
        var registry = new WorkloadProviderRegistry([Provider(DockerProvider.Id)]);

        registry.TryResolve(KubernetesProvider.Id).Should().BeNull();
        registry.Invoking(r => r.Resolve(KubernetesProvider.Id))
            .Should().Throw<WorkloadProviderNotRegisteredException>(
                "the two reads differ precisely in whether an absent provider is an error");
    }

    [Fact]
    public void A_duplicate_kind_resolves_to_the_last_registered_and_does_not_crash()
    {
        var first = Provider(DockerProvider.Id);
        var last = Provider(DockerProvider.Id);

        var registry = new WorkloadProviderRegistry([first, last]);

        registry.TryResolve(DockerProvider.Id).Should().BeSameAs(last,
            "a duplicate kind is a wiring smell the registry resolves deterministically rather than throwing on");
        registry.RegisteredProviders.Should().HaveCount(1);
    }

    [Fact]
    public void Each_registered_kind_resolves_to_its_own_provider()
    {
        var docker = Provider(DockerProvider.Id);
        var kubernetes = Provider(KubernetesProvider.Id);

        var registry = new WorkloadProviderRegistry([docker, kubernetes]);

        registry.Resolve(DockerProvider.Id).Should().BeSameAs(docker);
        registry.Resolve(KubernetesProvider.Id).Should().BeSameAs(kubernetes,
            "indexing by kind is the whole job; handing back the wrong one would deploy onto the wrong plane");
        registry.RegisteredProviders.Should().BeEquivalentTo(
            [DockerProvider.Id, KubernetesProvider.Id]);
    }

    [Fact]
    public void A_null_provider_set_is_refused_at_construction()
        => FluentActions.Invoking(() => new WorkloadProviderRegistry(null!))
            .Should().Throw<ArgumentNullException>();

    private static IWorkloadProvider Provider(WorkloadProviderId kind)
    {
        var provider = Substitute.For<IWorkloadProvider>();
        provider.Descriptor.Returns(ShippedProviders.For(kind));
        return provider;
    }
}
