using System.Text.Json;
using FluentAssertions;
using Reeve.Workloads.Abstractions.Contracts;

namespace Reeve.Workloads.Abstractions.Tests;

public sealed class WorkloadOrchestratorSettingsProvidersMapTests
{
    [Fact]
    public void An_unknown_providers_section_survives_a_round_trip_under_its_own_id()
    {
        var settings = JsonSerializer.Deserialize<WorkloadOrchestratorSettings>(
            """{"provider":"nomad","providers":{"nomad":{"address":"https://nomad.internal:4646","region":"eu"}}}""")!;

        settings.Provider.Should().Be(WorkloadProviderId.Parse("nomad"));
        var entry = settings.Providers[WorkloadProviderId.Parse("nomad")];
        entry.GetProperty("address").GetString().Should().Be("https://nomad.internal:4646");

        JsonSerializer.Serialize(settings).Should().Contain("\"nomad\":{",
            "a section the library cannot read must still be written back, or the first save from the "
            + "settings UI would silently drop the provider's whole configuration");
    }

    [Fact]
    public void A_key_is_canonicalised_the_way_every_other_provider_id_is()
    {
        var settings = JsonSerializer.Deserialize<WorkloadOrchestratorSettings>(
            """{"providers":{"Nomad":{"region":"eu"}}}""")!;

        settings.Providers.Should().ContainKey(WorkloadProviderId.Parse("nomad"));
    }

    [Fact]
    public void A_document_without_the_map_reads_as_empty_rather_than_null()
    {
        var settings = JsonSerializer.Deserialize<WorkloadOrchestratorSettings>(
            """{"provider":"kubernetes","kubernetes":{"namespace":"reeve-workloads"}}""")!;

        settings.Providers.Should().BeEmpty(
            "the legacy top-level section is not a `providers` entry — folding one into the other is the settings "
            + "service\'s job, done against the registered codecs, not the contract\'s");
    }

    [Fact]
    public void An_entry_that_was_never_configured_reads_as_undefined_rather_than_throwing()
    {
        var settings = new WorkloadOrchestratorSettings();

        settings.Entry(WorkloadProviderId.Parse("nomad")).ValueKind.Should().Be(JsonValueKind.Undefined);
    }

    [Fact]
    public void An_entry_written_as_null_reads_as_undefined_too()
    {
        var settings = JsonSerializer.Deserialize<WorkloadOrchestratorSettings>(
            """{"providers":{"nomad":null}}""")!;

        settings.Entry(WorkloadProviderId.Parse("nomad")).ValueKind.Should().Be(JsonValueKind.Undefined);
    }

    [Fact]
    public void WithProvider_replaces_one_entry_and_keeps_the_others()
    {
        var settings = new WorkloadOrchestratorSettings()
            .WithProvider(WorkloadProviderId.Parse("nomad"), new { address = "https://a:4646", region = "eu" })
            .WithProvider(WorkloadProviderId.Parse("other"), new { keep = true })
            .WithProvider(WorkloadProviderId.Parse("nomad"), new { address = "https://b:4646" });

        var nomad = settings.Entry(WorkloadProviderId.Parse("nomad"));
        nomad.GetProperty("address").GetString().Should().Be("https://b:4646");
        nomad.TryGetProperty("region", out _).Should().BeFalse("a replace is a replace, not a merge");
        settings.Entry(WorkloadProviderId.Parse("other")).GetProperty("keep").GetBoolean().Should().BeTrue();
    }
}
