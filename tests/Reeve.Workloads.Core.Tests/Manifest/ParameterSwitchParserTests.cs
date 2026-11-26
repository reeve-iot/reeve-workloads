using System.Text.Json;
using System.Text.Json.Serialization;
using FluentAssertions;
using Reeve.Workloads.Abstractions.Contracts.Deployment;
using Reeve.Workloads.Abstractions.Domain;
using Reeve.Workloads.Manifest;

namespace Reeve.Workloads.Core.Tests.Manifest;

public sealed class ParameterSwitchParserTests
{
    private static readonly IReadOnlyList<IWorkloadExtensionValidator> NoValidators = [];

    private const string Prefix = "deployment.workloads[0].parameters";

    private const string Switch = """
        { "name": "source", "type": "enum", "section": "Bluetooth", "enumValues": ["none", "proxy", "esp"],
          "widget": "radio", "enumLabels": { "none": "None" }, "reveals": { "proxy": ["Proxy"], "esp": ["ESP"] } }
        """;

    private const string ProxyUrl = """{ "name": "url", "type": "text", "section": "Proxy" }""";

    private const string EspHost = """{ "name": "host", "type": "text", "section": "ESP" }""";

    private static (DeploymentManifest? Manifest, IReadOnlyList<DeploymentValidationError> Errors) Parse(params string[] parameters)
        => ParseWithNetwork("null", parameters);

    private static (DeploymentManifest? Manifest, IReadOnlyList<DeploymentValidationError> Errors) ParseWithNetwork(
        string networkJson, params string[] parameters)
    {
        var json = $$$$"""
            {
              "schemaVersion": 1,
              "workloads": [{
                "id": "w1",
                "displayName": "W1",
                "image": { "ref": "example.com/repo/img", "digest": "sha256:0000000000000000000000000000000000000000000000000000000000000000" },
                "kind": "service",
                "health": { "mode": "container" },
                "extensions": { "reeve": { "protocol": { "version": "1", "minSupported": "1" } } },
                "network": {{{{networkJson}}}},
                "parameters": [ {{{{string.Join(",", parameters)}}}} ]
              }]
            }
            """;
        using var doc = JsonDocument.Parse(json);
        var manifest = DeploymentManifestParser.Parse(doc.RootElement.Clone(), NoValidators, out var errors);
        return (manifest, errors);
    }

    [Fact]
    public void Valid_switch_fields_survive_parser_and_wire_round_trips()
    {
        var (manifest, errors) = Parse(Switch, ProxyUrl, EspHost);

        errors.Should().BeEmpty();
        var parameters = manifest!.Workloads[0].Parameters;
        parameters[0].Widget.Should().Be(WorkloadParameterWidget.Radio);
        parameters[0].EnumLabels.Should().BeEquivalentTo(new Dictionary<string, string> { ["none"] = "None" });
        parameters[0].Reveals.Should().BeEquivalentTo(new Dictionary<string, string[]> { ["proxy"] = ["Proxy"], ["esp"] = ["ESP"] });

        var json = JsonSerializer.Serialize(manifest, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        });
        using var doc = JsonDocument.Parse(json);
        var reparsed = DeploymentManifestParser.Parse(doc.RootElement.Clone(), NoValidators, out var roundTripErrors);
        roundTripErrors.Should().BeEmpty();
        reparsed!.Workloads[0].Parameters.Should().BeEquivalentTo(parameters);

        var wire = JsonSerializer.Serialize(parameters, WorkloadJsonDefaults.CanonicalWire);
        JsonSerializer.Deserialize<List<WorkloadParameter>>(wire, WorkloadJsonDefaults.CanonicalWire)
            .Should().BeEquivalentTo(parameters);
        parameters.Skip(1).Should().OnlyContain(p => p.Widget == null && p.EnumLabels == null && p.Reveals == null);
    }

    [Theory]
    [InlineData("\"widget\": \"radio\"", "widget")]
    [InlineData("\"enumLabels\": { \"a\": \"A\" }", "enumLabels")]
    [InlineData("\"reveals\": { \"a\": [\"Other\"] }", "reveals")]
    public void Rule1_switch_key_on_non_enum_is_rejected(string keyJson, string key)
    {
        var (_, errors) = Parse(
            $$"""{ "name": "host", "type": "text", {{keyJson}} }""",
            """{ "name": "other", "type": "text", "section": "Other" }""");

        errors.Should().ContainSingle(e => e.Code == DeploymentValidationCodes.SwitchKeyOnNonEnumParameter)
            .Which.Path.Should().Be($"{Prefix}[0].{key}");
    }

    [Theory]
    [InlineData("enumLabels", "\"enumLabels\": { \"wifi\": \"Wi-Fi\" }")]
    [InlineData("reveals", "\"reveals\": { \"wifi\": [\"ESP\"] }")]
    public void Rule2_map_key_must_be_enum_value(string field, string keyJson)
    {
        var (_, errors) = Parse(
            $$"""{ "name": "src", "type": "enum", "enumValues": ["none", "esp"], {{keyJson}} }""",
            EspHost);

        errors.Should().ContainSingle(e => e.Code == DeploymentValidationCodes.SwitchKeyNotAnEnumValue)
            .Which.Path.Should().Be($"{Prefix}[0].{field}.wifi");
    }

    [Fact]
    public void Null_switch_keys_on_a_non_enum_parameter_mean_absent()
    {
        var (manifest, errors) = Parse("""{ "name": "host", "type": "text", "widget": null, "enumLabels": null, "reveals": null }""");

        errors.Should().BeEmpty();
        var parameter = manifest!.Workloads[0].Parameters[0];
        parameter.Widget.Should().BeNull();
        parameter.EnumLabels.Should().BeNull();
        parameter.Reveals.Should().BeNull();
    }

    [Fact]
    public void An_unknown_widget_is_an_unknown_enum_value()
    {
        var (_, errors) = Parse("""{ "name": "src", "type": "enum", "enumValues": ["a", "b"], "widget": "toggle" }""");

        errors.Should().ContainSingle()
            .Which.Should().Match<DeploymentValidationError>(e =>
                e.Code == DeploymentValidationCodes.UnknownEnumValue && e.Path == $"{Prefix}[0].widget");
    }

    [Fact]
    public void A_numeric_widget_is_wrong_type()
    {
        var (_, errors) = Parse("""{ "name": "src", "type": "enum", "enumValues": ["a"], "widget": 1 }""");

        errors.Should().ContainSingle()
            .Which.Should().Match<DeploymentValidationError>(e =>
                e.Code == DeploymentValidationCodes.WrongType && e.Path == $"{Prefix}[0].widget");
    }

    [Fact]
    public void A_wrongly_shaped_switch_key_is_wrong_type()
    {
        var (_, errors) = Parse("""{ "name": "src", "type": "enum", "enumValues": ["a"], "enumLabels": ["A"] }""");

        errors.Should().ContainSingle()
            .Which.Should().Match<DeploymentValidationError>(e =>
                e.Code == DeploymentValidationCodes.WrongType && e.Path == $"{Prefix}[0].enumLabels");
    }

    [Fact]
    public void A_reveals_array_is_wrong_type()
    {
        var (_, errors) = Parse("""{ "name": "src", "type": "enum", "enumValues": ["a"], "reveals": ["S"] }""");

        errors.Should().ContainSingle()
            .Which.Should().Match<DeploymentValidationError>(e =>
                e.Code == DeploymentValidationCodes.WrongType && e.Path == $"{Prefix}[0].reveals");
    }

    [Fact]
    public void A_non_array_reveals_entry_is_wrong_type()
    {
        var (_, errors) = Parse("""{ "name": "src", "type": "enum", "enumValues": ["a"], "reveals": { "a": "S" } }""");

        errors.Should().ContainSingle()
            .Which.Should().Match<DeploymentValidationError>(e =>
                e.Code == DeploymentValidationCodes.WrongType && e.Path == $"{Prefix}[0].reveals.a");
    }

    [Fact]
    public void A_bad_reveals_entry_is_wrong_type()
    {
        var (_, errors) = Parse("""{ "name": "src", "type": "enum", "enumValues": ["a"], "reveals": { "a": [1] } }""");

        errors.Should().ContainSingle()
            .Which.Should().Match<DeploymentValidationError>(e =>
                e.Code == DeploymentValidationCodes.WrongType && e.Path == $"{Prefix}[0].reveals.a[0]");
    }

    [Fact]
    public void A_blank_enum_label_is_wrong_type()
    {
        var (_, errors) = Parse("""{ "name": "src", "type": "enum", "enumValues": ["a"], "enumLabels": { "a": "  " } }""");

        errors.Should().ContainSingle()
            .Which.Should().Match<DeploymentValidationError>(e =>
                e.Code == DeploymentValidationCodes.WrongType && e.Path == $"{Prefix}[0].enumLabels.a");
    }

    [Fact]
    public void A_control_character_in_an_enum_label_is_wrong_type()
    {
        var (_, errors) = Parse("""{ "name": "src", "type": "enum", "enumValues": ["a"], "enumLabels": { "a": "Bad\u0001Label" } }""");

        errors.Should().ContainSingle()
            .Which.Should().Match<DeploymentValidationError>(e =>
                e.Code == DeploymentValidationCodes.ControlCharacterInString && e.Path == $"{Prefix}[0].enumLabels.a");
    }

    [Fact]
    public void Rule2_is_quiet_when_enum_values_is_missing()
    {
        var (_, errors) = Parse("""{ "name": "src", "type": "enum", "enumLabels": { "a": "A" } }""");

        errors.Should().ContainSingle().Which.Code.Should().Be(DeploymentValidationCodes.MissingEnumValues);
    }

    [Fact]
    public void Rule3_a_revealed_section_no_parameter_declares_is_rejected()
    {
        var typo = Switch.Replace("\"ESP\"]", "\"ES P\"]", StringComparison.Ordinal);

        var (_, errors) = Parse(typo, ProxyUrl, EspHost);

        errors.Should().ContainSingle()
            .Which.Should().Match<DeploymentValidationError>(e =>
                e.Code == DeploymentValidationCodes.RevealedSectionNotDeclared && e.Path == $"{Prefix}[0].reveals");
    }

    [Fact]
    public void Rule4_a_switch_revealing_its_own_section_is_rejected()
    {
        var (_, errors) = Parse(
            """{ "name": "src", "type": "enum", "section": "Bluetooth", "enumValues": ["none", "on"], "reveals": { "on": ["Bluetooth"] } }""");

        errors.Should().ContainSingle()
            .Which.Should().Match<DeploymentValidationError>(e =>
                e.Code == DeploymentValidationCodes.SwitchRevealsOwnSection && e.Path == $"{Prefix}[0].reveals");
    }

    [Fact]
    public void Rule5_a_section_revealed_by_two_switches_is_rejected()
    {
        var second = """{ "name": "radioSource", "type": "enum", "enumValues": ["none", "esp"], "reveals": { "esp": ["ESP"] } }""";

        var (_, errors) = Parse(Switch, second, ProxyUrl, EspHost);

        errors.Should().ContainSingle()
            .Which.Should().Match<DeploymentValidationError>(e =>
                e.Code == DeploymentValidationCodes.SectionRevealedByTwoSwitches && e.Path == $"{Prefix}[1].reveals");
    }

    [Fact]
    public void Rule6_a_required_parameter_in_a_gated_section_is_rejected()
    {
        var requiredHost = """{ "name": "host", "type": "text", "section": "ESP", "required": true }""";

        var (_, errors) = Parse(Switch, ProxyUrl, requiredHost);

        errors.Should().ContainSingle()
            .Which.Should().Match<DeploymentValidationError>(e =>
                e.Code == DeploymentValidationCodes.RequiredParameterInGatedSection && e.Path == $"{Prefix}[2].required");
    }

    [Fact]
    public void Rule7_a_switch_in_a_gated_section_is_rejected_once_at_its_section_path()
    {
        var outer = Switch;
        var nested = """{ "name": "nested", "type": "enum", "enumValues": ["a", "b"], "section": "ESP", "reveals": { "b": ["Nested options"] } }""";
        var child = """{ "name": "child", "type": "text", "section": "Nested options" }""";

        var (_, errors) = Parse(outer, nested, child, ProxyUrl);

        errors.Should().ContainSingle()
            .Which.Should().Match<DeploymentValidationError>(e =>
                e.Code == DeploymentValidationCodes.SwitchInGatedSection && e.Path == $"{Prefix}[1].section");
    }

    [Fact]
    public void Rule7_both_switches_in_a_mutual_hide_pair_are_reported()
    {
        var first = """{ "name": "first", "type": "enum", "enumValues": ["a", "b"], "section": "A", "reveals": { "a": ["B"] } }""";
        var second = """{ "name": "second", "type": "enum", "enumValues": ["a", "b"], "section": "B", "reveals": { "a": ["A"] } }""";

        var (_, errors) = Parse(first, second);

        errors.Should().HaveCount(2);
        errors.Should().OnlyContain(e => e.Code == DeploymentValidationCodes.SwitchInGatedSection);
        errors.Select(e => e.Path).Should().BeEquivalentTo($"{Prefix}[0].section", $"{Prefix}[1].section");
    }

    [Fact]
    public void Rule7_accepts_a_non_switch_enum_in_a_gated_section()
    {
        var plainEnum = """{ "name": "plain", "type": "enum", "enumValues": ["a", "b"], "section": "ESP" }""";

        var (_, errors) = Parse(Switch, plainEnum, ProxyUrl);

        errors.Should().BeEmpty();
    }

    [Fact]
    public void Rule7_accepts_a_switch_in_an_ungated_section()
    {
        var outer = """{ "name": "outer", "type": "enum", "enumValues": ["a", "b"], "reveals": { "a": ["ESP"] } }""";
        var nested = """{ "name": "nested", "type": "enum", "enumValues": ["a", "b"], "section": "Independent", "reveals": { "a": ["More"] } }""";

        var (_, errors) = Parse(outer, nested, EspHost,
            """{ "name": "more", "type": "text", "section": "More" }""");

        errors.Should().BeEmpty();
    }

    [Fact]
    public void Rule7_does_not_treat_a_switch_with_only_empty_reveals_as_a_switch()
    {
        var nested = """{ "name": "nested", "type": "enum", "enumValues": ["a", "b"], "section": "ESP", "reveals": { "a": [] } }""";

        var (_, errors) = Parse(Switch, nested, ProxyUrl);

        errors.Should().BeEmpty();
    }

    [Fact]
    public void Rule7_reports_a_switch_only_once_when_two_other_switches_gate_its_section()
    {
        var first = """{ "name": "first", "type": "enum", "enumValues": ["a", "b"], "reveals": { "a": ["ESP"] } }""";
        var second = """{ "name": "second", "type": "enum", "enumValues": ["a", "b"], "reveals": { "a": ["ESP"] } }""";
        var nested = """{ "name": "nested", "type": "enum", "enumValues": ["a", "b"], "section": "ESP", "reveals": { "a": ["Child"] } }""";
        var child = """{ "name": "child", "type": "text", "section": "Child" }""";

        var (_, errors) = Parse(first, second, nested, child);

        errors.Should().ContainSingle(e => e.Code == DeploymentValidationCodes.SwitchInGatedSection)
            .Which.Path.Should().Be($"{Prefix}[2].section");
        errors.Should().ContainSingle(e => e.Code == DeploymentValidationCodes.SectionRevealedByTwoSwitches);
    }

    [Fact]
    public void Revealed_section_names_are_trimmed_like_section()
    {
        var padded = Switch.Replace("\"ESP\"]", "\"  ESP \"]", StringComparison.Ordinal);

        var (manifest, errors) = Parse(padded, ProxyUrl, EspHost);

        errors.Should().BeEmpty();
        manifest!.Workloads[0].Parameters[0].Reveals!["esp"].Should().Equal("ESP");
    }

    [Fact]
    public void One_switch_may_reveal_a_section_under_two_values()
    {
        var (_, errors) = Parse(
            """{ "name": "src", "type": "enum", "enumValues": ["a", "b"], "reveals": { "a": ["Shared"], "b": ["Shared"] } }""",
            """{ "name": "shared", "type": "text", "section": "Shared" }""");

        errors.Should().BeEmpty();
    }

    [Theory]
    [InlineData("\"widget\": \"radio\"")]
    [InlineData("\"enumLabels\": { \"hostNetwork\": \"Host network\" }")]
    [InlineData("\"reveals\": { \"hostNetwork\": [\"Shared\"] }")]
    public void A_reserved_parameter_may_not_carry_a_switch_key(string switchKeyJson)
    {
        var (_, errors) = ParseWithNetwork(
            """{ "hostNetwork": true }""",
            $$"""{ "name": "network.mode", "type": "enum", "enumValues": ["cluster", "hostNetwork"], {{switchKeyJson}} }""",
            """{ "name": "shared", "type": "text", "section": "Shared" }""");

        errors.Should().ContainSingle()
            .Which.Should().Match<DeploymentValidationError>(e =>
                e.Code == DeploymentValidationCodes.ReservedParameterOverride && e.Path == $"{Prefix}[0]");
    }
}
