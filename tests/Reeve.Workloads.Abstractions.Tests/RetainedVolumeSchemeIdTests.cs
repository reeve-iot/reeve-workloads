using System.Text.Json;
using FluentAssertions;
using Reeve.Workloads.Abstractions.Domain.Entities;
using Xunit;

namespace Reeve.Workloads.Abstractions.Tests;

public sealed class RetainedVolumeSchemeIdTests
{
    [Theory]
    [InlineData("Kubernetes")]
    [InlineData("kubernetes")]
    [InlineData("KUBERNETES")]
    public void Every_spelling_a_6_x_row_or_document_holds_is_the_kubernetes_scheme(string stored)
        => RetainedVolumeSchemeId.Parse(stored).Should().Be(RetainedVolumeSchemeId.Kubernetes);

    [Theory]
    [InlineData("Docker")]
    [InlineData("docker")]
    public void Every_spelling_a_6_x_row_or_document_holds_is_the_docker_scheme(string stored)
        => RetainedVolumeSchemeId.Parse(stored).Should().Be(RetainedVolumeSchemeId.Docker);

    [Fact]
    public void The_two_schemes_6_x_knew_are_written_as_the_member_names_6_x_parses()
    {
        string[] namesSixTwoParses = ["Kubernetes", "Docker"];

        RetainedVolumeSchemeId.Kubernetes.PersistedValue.Should().Be("Kubernetes").And.BeOneOf(namesSixTwoParses);
        RetainedVolumeSchemeId.Docker.PersistedValue.Should().Be("Docker").And.BeOneOf(namesSixTwoParses);
    }

    [Fact]
    public void A_scheme_new_in_7_0_0_is_written_as_its_canonical_id()
    {
        var nomad = RetainedVolumeSchemeId.Parse("Acme.Nomad");

        nomad.Value.Should().Be("acme.nomad");
        nomad.PersistedValue.Should().Be("acme.nomad");
        RetainedVolumeSchemeId.Parse(nomad.PersistedValue).Should().Be(nomad);
    }

    [Fact]
    public void The_empty_id_persists_as_empty_rather_than_throwing()
        => default(RetainedVolumeSchemeId).PersistedValue.Should().BeEmpty();

    [Theory]
    [InlineData("")]
    [InlineData("not a scheme")]
    [InlineData("-leading-hyphen")]
    [InlineData("a-scheme-id-that-is-longer-than-32")]
    public void What_the_grammar_rejects_does_not_parse(string raw)
    {
        RetainedVolumeSchemeId.TryParse(raw, out _).Should().BeFalse();
        FluentActions.Invoking(() => RetainedVolumeSchemeId.Parse(raw)).Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Thirty_two_characters_is_the_column_and_still_parses()
        => RetainedVolumeSchemeId.TryParse(new string('a', RetainedVolumeSchemeId.MaxLength), out _).Should().BeTrue();

    [Fact]
    public void Json_writes_the_persisted_spelling_and_reads_either()
    {
        JsonSerializer.Serialize(RetainedVolumeSchemeId.Docker).Should().Be("\"Docker\"");
        JsonSerializer.Deserialize<RetainedVolumeSchemeId>("\"docker\"").Should().Be(RetainedVolumeSchemeId.Docker);
        JsonSerializer.Deserialize<RetainedVolumeSchemeId>("\"Docker\"").Should().Be(RetainedVolumeSchemeId.Docker);
    }
}
