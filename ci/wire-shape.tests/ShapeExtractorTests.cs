using FluentAssertions;
using Reeve.Workloads.WireShape;

namespace Reeve.Workloads.WireShape.Tests;

public sealed class ShapeExtractorTests : IDisposable
{
    private readonly List<string> _files = [];

    private List<WireType> Extract(string source)
    {
        var path = Path.Combine(Path.GetTempPath(), $"wireshape-{Guid.NewGuid():N}.cs");
        File.WriteAllText(path, source);
        _files.Add(path);
        return ShapeExtractor.ForFile(path, "src/Test/Contract.cs");
    }

    public void Dispose()
    {
        foreach (var f in _files) File.Delete(f);
    }

    [Fact]
    public void The_wire_key_is_the_attribute_not_the_CLR_name()
    {
        var types = Extract("""
            using System.Text.Json.Serialization;
            namespace T;
            public sealed record Response
            {
                [JsonPropertyName("moduleId")]
                public required string PluginTypeId { get; init; }
            }
            """);

        types.Should().ContainSingle();
        types[0].Properties!.Single().Wire.Should().Be("moduleId");
        types[0].Properties!.Single().Clr.Should().Be("PluginTypeId");
    }

    [Fact]
    public void A_property_with_no_declared_key_is_reported_rather_than_guessed()
    {
        var act = () => Extract("""
            namespace T;
            public sealed record Response
            {
                public string? ObservedConfigDigest { get; init; }
            }
            """);

        act.Should().Throw<ShapeExtractor.UnreadableShapeException>()
            .Which.Problems.Should().ContainSingle()
            .Which.Should().Contain("ObservedConfigDigest");
    }

    [Fact]
    public void Every_unreadable_member_is_reported_in_one_pass()
    {
        var act = () => Extract("""
            namespace T;
            public sealed record Response
            {
                public string? A { get; init; }
                public string? B { get; init; }
                public string? C { get; init; }
            }
            """);

        act.Should().Throw<ShapeExtractor.UnreadableShapeException>()
            .Which.Problems.Should().HaveCount(3);
    }

    [Fact]
    public void A_type_level_converter_publishes_no_property_list()
    {
        var types = Extract("""
            using System.Text.Json.Serialization;
            namespace T;
            [JsonConverter(typeof(ExtensionsConverter))]
            public sealed record Extensions
            {
                public IReadOnlyDictionary<string, string> ByNamespace { get; init; } = null!;
            }
            """);

        types.Should().ContainSingle();
        types[0].Kind.Should().Be("custom");
        types[0].Converter.Should().Be("ExtensionsConverter");
        types[0].Properties.Should().BeNull("a converter's key set is not derivable from its properties");
    }

    [Fact]
    public void An_undeclared_property_under_a_converter_is_NOT_an_error()
    {
        var types = Extract("""
            using System.Text.Json.Serialization;
            namespace T;
            [JsonConverter(typeof(C))]
            public sealed record Extensions { public string Raw { get; init; } = ""; }
            """);

        types.Should().ContainSingle().Which.Kind.Should().Be("custom");
    }

    [Fact]
    public void Enum_members_ride_as_their_declared_spelling_else_their_name()
    {
        var types = Extract("""
            using System.Text.Json.Serialization;
            namespace T;
            public enum Mode
            {
                [JsonStringEnumMemberName("hostNetwork")]
                HostNetwork,
                Cluster,
            }
            """);

        types.Should().ContainSingle();
        types[0].Kind.Should().Be("enum");
        types[0].Values.Should().Equal("hostNetwork", "Cluster");
    }

    [Fact]
    public void A_static_class_of_const_strings_is_a_closed_vocabulary()
    {
        var types = Extract("""
            namespace T;
            public static class Reasons
            {
                public const string Forbidden = "forbidden";
                public const string NotSupported = "not-supported";
                public static IReadOnlyList<string> All { get; } = [Forbidden, NotSupported];
            }
            """);

        types.Should().ContainSingle();
        types[0].Kind.Should().Be("vocabulary");
        types[0].Values.Should().Equal("forbidden", "not-supported");
    }

    [Fact]
    public void An_ignored_property_is_not_on_the_wire()
    {
        var types = Extract("""
            using System.Text.Json.Serialization;
            namespace T;
            public sealed record Response
            {
                [JsonPropertyName("id")] public required string Id { get; init; }
                [JsonIgnore] public string Secret { get; init; } = "";
            }
            """);

        types.Should().ContainSingle();
        types[0].Properties!.Select(p => p.Wire).Should().Equal("id");
    }

    [Fact]
    public void A_computed_property_carries_nothing_and_needs_no_key()
    {
        var types = Extract("""
            using System.Text.Json.Serialization;
            namespace T;
            public sealed record Response
            {
                [JsonPropertyName("id")] public required string Id { get; init; }
                public bool IsEmpty => Id.Length == 0;
            }
            """);

        types.Should().ContainSingle().Which.Properties!.Should().ContainSingle();
    }

    [Theory]
    [InlineData("string", "string")]
    [InlineData("Guid", "string")]
    [InlineData("int", "number")]
    [InlineData("bool", "boolean")]
    [InlineData("IReadOnlyList<WorkloadDto>", "array")]
    [InlineData("WorkloadDto[]", "array")]
    [InlineData("IReadOnlyDictionary<string, string>", "record")]
    [InlineData("JsonElement", "unknown")]
    [InlineData("SomeContract", "ref")]
    public void The_wire_kind_is_coarse_but_never_silently_any(string clr, string expected)
        => ShapeExtractor.WireKind(clr).Should().Be(expected);

    [Fact]
    public void Nullability_survives_into_the_shape_because_a_null_is_what_breaks_a_twin()
        => Extract("""
            using System.Text.Json.Serialization;
            namespace T;
            public sealed record Response
            {
                [JsonPropertyName("driver")] public string? Driver { get; init; }
            }
            """)[0].Properties!.Single().Nullable.Should().BeTrue();
}
