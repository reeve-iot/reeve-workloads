using FluentAssertions;
using Reeve.Workloads.Abstractions.Contracts;
using Reeve.Workloads.Domain;
using Xunit;
using Reeve.Workloads.Providers.Kubernetes;

namespace Reeve.Workloads.Core.Tests;

public sealed class WorkloadResourceNamerRegistryTests
{
    private static readonly WorkloadProviderId Nomad = WorkloadProviderId.Parse("acme.nomad");

    [Fact]
    public void A_registered_namer_answers_for_its_provider_and_no_other()
    {
        var nomad = new TaggedResourceNamer(Nomad, "nomad");
        var registry = new WorkloadResourceNamerRegistry([nomad]);

        registry.For(Nomad).Should().BeSameAs(nomad);
        registry.For(KubernetesProvider.Id).Should().BeSameAs(DefaultWorkloadResourceNamer.Instance);
    }

    [Fact]
    public void A_provider_with_no_namer_gets_the_default_rather_than_nothing()
        => new WorkloadResourceNamerRegistry([]).For(KubernetesProvider.Id)
            .Should().BeSameAs(DefaultWorkloadResourceNamer.Instance);

    [Fact]
    public void The_last_registration_for_a_provider_wins()
    {
        var shipped = new TaggedResourceNamer(Nomad, "shipped");
        var host = new TaggedResourceNamer(Nomad, "host");

        new WorkloadResourceNamerRegistry([shipped, host]).For(Nomad).Should().BeSameAs(host);
    }

    [Fact]
    public void A_namer_that_named_no_provider_cannot_take_over_the_unbound_instance()
    {
        var unset = new TaggedResourceNamer(default, "unset");

        new WorkloadResourceNamerRegistry([unset]).For(default)
            .Should().BeSameAs(DefaultWorkloadResourceNamer.Instance);
    }
}
