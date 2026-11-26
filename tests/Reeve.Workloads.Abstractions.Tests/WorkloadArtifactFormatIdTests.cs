using System.Text.Json;
using FluentAssertions;
using Reeve.Workloads.Abstractions.Rendering;
using Xunit;

namespace Reeve.Workloads.Abstractions.Tests;

public sealed class WorkloadArtifactFormatIdTests
{
    [Theory]
    [InlineData("compose")]
    [InlineData("kubernetes")]
    [InlineData("helm-values")]
    [InlineData("env")]
    public void The_wire_tokens_hosts_already_use_are_valid_ids(string token)
        => WorkloadArtifactFormatId.Parse(token).Value.Should().Be(token);

    [Theory]
    [InlineData("Compose", "compose")]
    [InlineData("HELM-VALUES", "helm-values")]
    public void Parsing_is_case_insensitive_and_canonical(string raw, string canonical)
        => WorkloadArtifactFormatId.Parse(raw).Should().Be(WorkloadArtifactFormatId.Parse(canonical));

    [Theory]
    [InlineData("compose", "Compose")]
    [InlineData("kubernetes", "Kubernetes")]
    [InlineData("helm-values", "HelmValues")]
    [InlineData("env", "Env")]
    public void The_four_formats_6x_knew_are_recorded_under_the_member_names_6x_wrote(string id, string before700)
        => WorkloadArtifactFormatId.Parse(id).PersistedValue.Should().Be(before700);

    [Fact]
    public void A_format_new_in_7_0_0_is_recorded_as_its_canonical_id()
        => WorkloadArtifactFormatId.Parse("Acme.Nomad-Job").PersistedValue.Should().Be("acme.nomad-job");

    [Fact]
    public void The_empty_id_is_recorded_as_empty_rather_than_throwing()
        => default(WorkloadArtifactFormatId).PersistedValue.Should().BeEmpty();

    [Theory]
    [InlineData("")]
    [InlineData("helm values")]
    [InlineData("-leading-hyphen")]
    [InlineData("a-format-id-that-is-longer-than-32")]
    public void What_the_grammar_rejects_does_not_parse(string raw)
    {
        WorkloadArtifactFormatId.TryParse(raw, out _).Should().BeFalse();
        FluentActions.Invoking(() => WorkloadArtifactFormatId.Parse(raw)).Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Thirty_two_characters_still_parses()
        => WorkloadArtifactFormatId.TryParse(new string('a', WorkloadArtifactFormatId.MaxLength), out _).Should().BeTrue();

    [Fact]
    public void Json_carries_the_canonical_id_both_ways()
    {
        JsonSerializer.Serialize(WorkloadArtifactFormatId.Parse("helm-values")).Should().Be("\"helm-values\"");
        JsonSerializer.Deserialize<WorkloadArtifactFormatId>("\"Helm-Values\"").Should().Be(WorkloadArtifactFormatId.Parse("helm-values"));
        FluentActions.Invoking(() => JsonSerializer.Deserialize<WorkloadArtifactFormatId>("3")).Should().Throw<JsonException>();
    }
}
