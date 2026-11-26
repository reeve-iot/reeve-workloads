using System.Text.Json;
using FluentAssertions;
using Reeve.Workloads.WireShape;

namespace Reeve.Workloads.WireShape.Tests;

public sealed class ShapeDiffTests
{
    private static string Doc(params WireType[] types)
        => JsonSerializer.Serialize(new ShapeDocument { Version = 1, Source = "src", Types = types.ToList() },
                                    ShapeJson.Options);

    private static WireType Object(string name, params string[] keys) => new()
    {
        Name = name, Kind = "object", File = "src/C.cs",
        Properties = keys.Select(k => new WireProperty
        {
            Wire = k, Clr = k, Kind = "string", Required = true, Nullable = false,
        }).ToList(),
    };

    [Fact]
    public void An_unchanged_shape_produces_no_lines()
        => ShapeDiff.Describe(Doc(Object("A", "id")), Doc(Object("A", "id"))).Should().BeEmpty();

    [Fact]
    public void Only_the_type_that_moved_is_named()
    {
        var lines = ShapeDiff.Describe(
            Doc(Object("A", "id"), Object("B", "name")),
            Doc(Object("A", "zzid"), Object("B", "name"))).ToList();

        lines.Should().HaveCount(3);
        lines[0].Should().Be("CHANGED type A");
        lines.Should().Contain(l => l.Contains("key REMOVED id"));
        lines.Should().Contain(l => l.Contains("key ADDED   zzid"));
    }

    [Fact]
    public void An_added_and_a_removed_type_are_both_named()
    {
        var lines = ShapeDiff.Describe(Doc(Object("Gone", "id")), Doc(Object("New", "id"))).ToList();

        lines.Should().Contain("REMOVED type Gone");
        lines.Should().Contain("ADDED   type New");
    }
}
