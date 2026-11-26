using System.Text.Json;
using System.Text.Json.Serialization;
using FluentAssertions;
using Reeve.Workloads.Abstractions.Providers;
using Reeve.Workloads.Providers.Docker;
using Reeve.Workloads.Providers.Kubernetes;
using Reeve.Workloads.Providers.Manual;
using Xunit;

namespace Reeve.Workloads.Core.Tests;

public sealed class ProviderSettingsPassThroughTests
{
    private static readonly IReadOnlySet<string> SampleKeys = ProviderSettingsPassThrough.OwnedKeys<Sample>();

    [Fact]
    public void OwnedKeys_takes_a_properties_JsonPropertyName_over_its_clr_name()
    {
        SampleKeys.Should().Contain("legacy_name");
        SampleKeys.Should().NotContain(["LegacyName", "legacyName"]);
    }

    [Fact]
    public void OwnedKeys_takes_a_camel_case_name_for_a_property_with_no_JsonPropertyName()
    {
        SampleKeys.Should().Contain("plainValue");
        SampleKeys.Should().NotContain("PlainValue");
    }

    [Fact]
    public void OwnedKeys_is_exactly_the_records_properties()
        => SampleKeys.Should().BeEquivalentTo(["legacy_name", "plainValue"]);

    [Fact]
    public void No_shipped_record_owns_a_gateway_url_any_more()
    {
        ProviderSettingsPassThrough.OwnedKeys<DockerProviderSettings>().Should().NotContain("gatewayUrl")
            .And.Contain(["socketUri", "networkName", "registryCredentials"])
            .And.NotContain("volumeNamePrefix");
        ProviderSettingsPassThrough.OwnedKeys<KubernetesProviderSettings>().Should().NotContain("gatewayUrl")
            .And.Contain(["namespace", "connectionMode", "kubeconfig"]);
        ProviderSettingsPassThrough.OwnedKeys<ManualProviderSettings>().Should().BeEmpty();
    }

    [Fact]
    public void Carry_adds_every_unowned_property_of_the_source_including_a_null_one()
    {
        var carried = ProviderSettingsPassThrough.Carry(
            Json("""{"legacy_name":"typed"}"""),
            Json("""{"legacy_name":"stored","gatewayUrl":"ws://gw:8080","other":null}"""),
            SampleKeys);

        carried.GetProperty("gatewayUrl").GetString().Should().Be("ws://gw:8080");
        carried.GetProperty("other").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Fact]
    public void Carry_never_lets_the_source_overwrite_or_restore_an_owned_property()
    {
        var carried = ProviderSettingsPassThrough.Carry(
            Json("""{"legacy_name":"typed"}"""),
            Json("""{"legacy_name":"stored","plainValue":"stored"}"""),
            SampleKeys);

        Names(carried).Should().BeEquivalentTo(["legacy_name"]);
        carried.GetProperty("legacy_name").GetString().Should().Be("typed");
    }

    [Fact]
    public void Carry_never_writes_a_key_the_owned_side_already_has_a_second_time()
    {
        var carried = ProviderSettingsPassThrough.Carry(
            Json("""{"legacy_name":"typed","extra":"typed"}"""),
            Json("""{"extra":"stored"}"""),
            SampleKeys);

        Names(carried).Should().Equal("legacy_name", "extra");
        carried.GetProperty("extra").GetString().Should().Be("typed");
    }

    [Theory]
    [InlineData("null")]
    [InlineData("\"text\"")]
    [InlineData("[1]")]
    public void Carry_from_a_source_that_is_not_an_object_adds_nothing(string source)
        => Names(ProviderSettingsPassThrough.Carry(Json("""{"legacy_name":"typed"}"""), Json(source), SampleKeys))
            .Should().BeEquivalentTo(["legacy_name"]);

    private static readonly JsonElement Stored = Json("""{"legacy_name":"old","gatewayUrl":"ws://stored:1"}""");

    [Fact]
    public void Merge_takes_a_present_string_from_the_request()
        => Merge("""{"gatewayUrl":"ws://request:2"}""", Stored)
            .GetProperty("gatewayUrl").GetString().Should().Be("ws://request:2");

    [Fact]
    public void Merge_takes_a_present_null_from_the_request_which_clears_the_stored_value()
        => Merge("""{"gatewayUrl":null}""", Stored)
            .GetProperty("gatewayUrl").ValueKind.Should().Be(JsonValueKind.Null);

    [Fact]
    public void Merge_keeps_the_stored_value_when_the_request_omits_the_key()
        => Merge("""{"legacy_name":"new"}""", Stored)
            .GetProperty("gatewayUrl").GetString().Should().Be("ws://stored:1");

    [Fact]
    public void Merge_leaves_a_key_absent_from_both_absent()
    {
        Merge("""{"legacy_name":"new"}""", Json("""{"legacy_name":"old"}""")).TryGetProperty("gatewayUrl", out _)
            .Should().BeFalse();
        Merge("""{"legacy_name":"new"}""", stored: null).TryGetProperty("gatewayUrl", out _)
            .Should().BeFalse();
    }

    [Fact]
    public void Merge_carries_an_unowned_key_only_the_request_has()
        => Merge("""{"fresh":"x"}""", Stored).GetProperty("fresh").GetString().Should().Be("x");

    [Fact]
    public void Merge_never_writes_a_key_the_owned_side_already_has_a_second_time()
    {
        var merged = ProviderSettingsPassThrough.Merge(
            Json("""{"legacy_name":"typed","extra":"typed"}"""),
            Json("""{"extra":"request"}"""),
            Json("""{"extra":"stored"}"""),
            SampleKeys);

        Names(merged).Should().Equal("legacy_name", "extra");
        merged.GetProperty("extra").GetString().Should().Be("typed");
    }

    [Fact]
    public void Merge_never_takes_an_owned_property_from_the_request_or_the_store()
    {
        var merged = ProviderSettingsPassThrough.Merge(
            Json("""{"plainValue":"typed"}"""),
            Json("""{"plainValue":"request","legacy_name":"request"}"""),
            Json("""{"legacy_name":"stored"}"""),
            SampleKeys);

        Names(merged).Should().BeEquivalentTo(["plainValue"]);
        merged.GetProperty("plainValue").GetString().Should().Be("typed");
    }

    [Fact]
    public void Carry_drops_a_property_that_differs_from_an_owned_key_only_in_case()
        => Names(ProviderSettingsPassThrough.Carry(
                Json("""{"legacy_name":"typed"}"""),
                Json("""{"Legacy_Name":"stored","PlainValue":"stored","gatewayUrl":"ws://gw"}"""),
                SampleKeys))
            .Should().Equal("legacy_name", "gatewayUrl");

    [Fact]
    public void Merge_drops_a_property_that_differs_from_an_owned_key_only_in_case()
        => Names(ProviderSettingsPassThrough.Merge(
                Json("""{"legacy_name":"typed"}"""),
                Json("""{"LEGACY_NAME":"request","gatewayUrl":"ws://gw"}"""),
                Json("""{"plainvalue":"stored"}"""),
                SampleKeys))
            .Should().Equal("legacy_name", "gatewayUrl");

    private static JsonElement Merge(string incoming, JsonElement? stored)
        => ProviderSettingsPassThrough.Merge(Json("""{"legacy_name":"typed"}"""), Json(incoming), stored, SampleKeys);

    public static TheoryData<string, string> Codecs() => new()
    {
        { "docker",       "gatewayUrl" },
        { "kubernetes",   "gatewayUrl" },
        { "manual",       "externalGatewayUrl" },
    };

    [Theory]
    [MemberData(nameof(Codecs))]
    public void Mask_keeps_the_url(string provider, string key)
        => Codec(provider).Mask(Json($$"""{"{{key}}":"wss://home.example.com"}"""))
            .GetProperty(key).GetString().Should().Be("wss://home.example.com");

    [Theory]
    [MemberData(nameof(Codecs))]
    public void ResolveMasked_takes_the_submitted_url_when_present(string provider, string key)
        => Codec(provider).ResolveMasked(
                Json($$"""{"{{key}}":"wss://new.example.com"}"""),
                Json($$"""{"{{key}}":"wss://old.example.com"}"""))
            .GetProperty(key).GetString().Should().Be("wss://new.example.com");

    [Theory]
    [MemberData(nameof(Codecs))]
    public void ResolveMasked_clears_the_url_on_a_present_null(string provider, string key)
        => Codec(provider).ResolveMasked(
                Json($$"""{"{{key}}":null}"""),
                Json($$"""{"{{key}}":"wss://old.example.com"}"""))
            .GetProperty(key).ValueKind.Should().Be(JsonValueKind.Null);

    [Theory]
    [MemberData(nameof(Codecs))]
    public void ResolveMasked_keeps_the_stored_url_when_the_submission_omits_it(string provider, string key)
        => Codec(provider).ResolveMasked(Json("{}"), Json($$"""{"{{key}}":"wss://old.example.com"}"""))
            .GetProperty(key).GetString().Should().Be("wss://old.example.com");

    [Theory]
    [MemberData(nameof(Codecs))]
    public void ResolveMasked_with_nothing_stored_and_no_url_writes_no_url(string provider, string key)
        => Codec(provider).ResolveMasked(Json("{}"), stored: null)
            .TryGetProperty(key, out _).Should().BeFalse();

    public static TheoryData<string, string> WrongCaseSecrets() => new()
    {
        { "docker",       """{"RegistryCredentials":[{"hostPattern":"r.example.com","username":"u","token":"plain"}]}""" },
        { "kubernetes",   """{"Kubeconfig":"apiVersion: v1 plain"}""" },
    };

    [Theory]
    [MemberData(nameof(WrongCaseSecrets))]
    public void A_mis_cased_secret_key_is_not_returned_by_Mask(string provider, string entry)
        => Codec(provider).Mask(Json(entry)).GetRawText()
            .Should().NotContain("plain", "Mask would otherwise hand a wrong-case secret back in plaintext");

    private static IWorkloadProviderSettingsCodec Codec(string provider) => provider switch
    {
        "docker" => new DockerProviderSettingsCodec(),
        "kubernetes" => new KubernetesProviderSettingsCodec(),
        "manual" => new ManualProviderSettingsCodec(),
        _ => throw new ArgumentOutOfRangeException(nameof(provider), provider, null),
    };

    private static JsonElement Json(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private static IEnumerable<string> Names(JsonElement element) => element.EnumerateObject().Select(p => p.Name);

    private sealed record Sample
    {
        [JsonPropertyName("legacy_name")]
        public string? LegacyName { get; init; }

        public string? PlainValue { get; init; }
    }
}
