using System;
using System.Linq;
using FluentAssertions;
using Reeve.Workloads.Abstractions;
using Reeve.Workloads.Providers.Kubernetes;
using Xunit;

namespace Reeve.Workloads.Providers.Kubernetes.Tests;

public sealed class AdoptableResourceKindParityTests
{
    [Fact]
    public void Both_kind_enums_declare_the_same_names_with_the_same_ordinals()
    {
        var neutral = Enum.GetValues<AdoptableResourceKind>()
            .Select(k => (Name: k.ToString(), Ordinal: (int)k)).OrderBy(p => p.Ordinal).ToList();
        var provider = Enum.GetValues<KubernetesObjectKind>()
            .Select(k => (Name: k.ToString(), Ordinal: (int)k)).OrderBy(p => p.Ordinal).ToList();

        neutral.Should().Equal(provider);
    }

    [Fact]
    public void Every_provider_kind_round_trips_through_the_neutral_kind()
    {
        foreach (var kind in Enum.GetValues<KubernetesObjectKind>())
        {
            kind.ToAdoptableKind().ToKubernetesKind().Should().Be(kind);
        }
    }

    [Fact]
    public void Every_neutral_kind_round_trips_through_the_provider_kind()
    {
        foreach (var kind in Enum.GetValues<AdoptableResourceKind>())
        {
            kind.ToKubernetesKind().ToAdoptableKind().Should().Be(kind);
        }
    }

    [Fact]
    public void An_unmapped_kind_throws_rather_than_inventing_a_member()
    {
        var invented = (AdoptableResourceKind)9999;

        Action act = () => invented.ToKubernetesKind();

        act.Should().Throw<ArgumentOutOfRangeException>();
    }
}
