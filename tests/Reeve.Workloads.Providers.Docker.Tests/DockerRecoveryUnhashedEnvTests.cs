using System.Collections.Generic;
using FluentAssertions;
using Reeve.Workloads.Providers.Docker;
using Xunit;

namespace Reeve.Workloads.Providers.Docker.Tests;

public sealed class DockerRecoveryUnhashedEnvTests
{
    private const string Canonical = "reeve-wl-acme-zwave-controller-abcdef12";
    private const string SpecHash = "hhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhh";

    private static DockerContainerRef Container(string? digest, bool running = true)
        => new() { Id = Canonical, Name = Canonical, SpecHash = SpecHash, UnhashedEnvDigest = digest, IsRunning = running };

    [Fact]
    public void A_container_built_with_another_digest_is_swapped_out_not_converged()
    {
        var live = Container("aaaa");

        var decision = DockerRecoveryDecisionTable.Decide([live], SpecHash, "0", "bbbb");

        decision.Action.Should().Be(DockerRecoveryAction.RunFullSwap);
        decision.PreviousContainer.Should().Be(live);
    }

    [Fact]
    public void A_container_built_with_the_same_digest_is_converged()
        => DockerRecoveryDecisionTable.Decide([Container("aaaa")], SpecHash, "0", "aaaa")
            .Action.Should().Be(DockerRecoveryAction.Converged);

    [Fact]
    public void A_container_with_no_digest_is_swapped_out_once_a_spec_flags_an_env()
        => DockerRecoveryDecisionTable.Decide([Container(null)], SpecHash, "0", "aaaa")
            .Action.Should().Be(DockerRecoveryAction.RunFullSwap);

    [Fact]
    public void A_container_with_a_digest_is_swapped_out_when_the_spec_no_longer_flags_an_env()
        => DockerRecoveryDecisionTable.Decide([Container("aaaa")], SpecHash, "0", null)
            .Action.Should().Be(DockerRecoveryAction.RunFullSwap);

    [Fact]
    public void An_absent_and_a_blank_digest_are_the_same_none()
        => DockerRecoveryDecisionTable.Decide([Container("  ")], SpecHash, "0", null)
            .Action.Should().Be(DockerRecoveryAction.Converged);

    [Fact]
    public void The_digest_ignores_the_order_of_the_list_and_moves_with_a_name_or_a_value()
    {
        var a = Flagged("A", "1");
        var b = Flagged("B", "2");

        DockerProviderConstants.UnhashedEnvDigest([a, b]).Should().Be(DockerProviderConstants.UnhashedEnvDigest([b, a]));
        DockerProviderConstants.UnhashedEnvDigest([a, b]).Should().NotBe(DockerProviderConstants.UnhashedEnvDigest([a, Flagged("B", "3")]));
        DockerProviderConstants.UnhashedEnvDigest([a, b]).Should().NotBe(DockerProviderConstants.UnhashedEnvDigest([a, Flagged("C", "2")]));
        DockerProviderConstants.UnhashedEnvDigest([Flagged("AB", "")]).Should().NotBe(DockerProviderConstants.UnhashedEnvDigest([Flagged("A", "B")]));
    }

    [Fact]
    public void An_unflagged_env_contributes_nothing()
    {
        var plain = new Reeve.Workloads.Abstractions.Domain.DesiredSpec.ResolvedEnvVar
        {
            Name = "P", Kind = Reeve.Workloads.Abstractions.Domain.DesiredSpec.ResolvedEnvKind.Literal, Value = "x",
        };

        DockerProviderConstants.UnhashedEnvDigest([plain]).Should().BeNull();
        DockerProviderConstants.UnhashedEnvDigest([plain, Flagged("A", "1")]).Should().Be(DockerProviderConstants.UnhashedEnvDigest([Flagged("A", "1")]));
    }

    private static Reeve.Workloads.Abstractions.Domain.DesiredSpec.ResolvedEnvVar Flagged(string name, string value) => new()
    {
        Name = name,
        Kind = Reeve.Workloads.Abstractions.Domain.DesiredSpec.ResolvedEnvKind.Literal,
        Value = value,
        ExcludedFromSpecHash = true,
    };
}
