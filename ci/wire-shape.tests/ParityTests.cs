using FluentAssertions;
using Reeve.Workloads.WireShape;

namespace Reeve.Workloads.WireShape.Tests;

public sealed class ParityTests
{
    private static WireType Object(string name, params (string Wire, bool Nullable)[] props) => new()
    {
        Name = name, Kind = "object", File = "src/C.cs",
        Properties = props.Select(p => new WireProperty
        {
            Wire = p.Wire, Clr = p.Wire, Kind = "string", Required = true, Nullable = p.Nullable,
        }).ToList(),
    };

    private static ZodSchema Schema(string name, params string[] keys) => new()
    {
        Name = name, File = "twin.ts", Kind = ZodKind.Object, Members = keys,
        Nullable = new HashSet<string>(StringComparer.Ordinal),
    };

    private static ShapeDocument Shape(params WireType[] types)
        => new() { Version = 1, Source = "src", Types = types.ToList() };

    private static PairingDocument Pairing(Dictionary<string, string> pairs) => new()
    {
        Version = 1, Twins = ["twin.ts"], Pairs = pairs,
    };

    [Fact]
    public void A_matching_pair_is_silent()
    {
        var problems = Consumer.Compare(
            Shape(Object("Response", ("id", false))),
            Pairing(new() { ["Response"] = "responseSchema" }),
            new Dictionary<string, ZodSchema> { ["responseSchema"] = Schema("responseSchema", "id") });

        problems.Should().BeEmpty();
    }

    [Fact]
    public void A_wire_key_renamed_on_the_backend_alone_is_caught()
    {
        var problems = Consumer.Compare(
            Shape(Object("Response", ("moduleId", false))),
            Pairing(new() { ["Response"] = "responseSchema" }),
            new Dictionary<string, ZodSchema> { ["responseSchema"] = Schema("responseSchema", "pluginTypeId") });

        problems.Should().HaveCount(2);
        problems.Should().Contain(p => p.Contains("moduleId") && p.Contains("does not declare"));
        problems.Should().Contain(p => p.Contains("pluginTypeId") && p.Contains("never sends"));
    }

    [Fact]
    public void A_type_renamed_on_the_backend_alone_is_caught()
    {
        var problems = Consumer.Compare(
            Shape(Object("ResponseV2", ("id", false))),
            Pairing(new() { ["Response"] = "responseSchema" }),
            new Dictionary<string, ZodSchema> { ["responseSchema"] = Schema("responseSchema", "id") });

        problems.Should().Contain(p => p.Contains("ResponseV2") && p.Contains("says nothing about it"));
        problems.Should().Contain(p => p.Contains("Response") && p.Contains("does not contain"));
    }

    [Fact]
    public void A_new_contract_type_fails_until_someone_decides_what_parses_it()
    {
        var problems = Consumer.Compare(
            Shape(Object("Existing", ("id", false)), Object("BrandNew", ("id", false))),
            Pairing(new() { ["Existing"] = "existingSchema" }),
            new Dictionary<string, ZodSchema> { ["existingSchema"] = Schema("existingSchema", "id") });

        problems.Should().ContainSingle().Which.Should().Contain("BrandNew");
    }

    [Fact]
    public void A_value_added_to_a_closed_vocabulary_is_caught()
    {
        var enumType = new WireType
        {
            Name = "Mode", Kind = "enum", File = "src/C.cs",
            Values = ["cluster", "hostNetwork", "smuggled"],
        };
        var twin = new ZodSchema
        {
            Name = "modeSchema", File = "twin.ts", Kind = ZodKind.Enum,
            Members = ["cluster", "hostNetwork"], Nullable = new HashSet<string>(StringComparer.Ordinal),
        };

        var problems = Consumer.Compare(Shape(enumType), Pairing(new() { ["Mode"] = "modeSchema" }),
            new Dictionary<string, ZodSchema> { ["modeSchema"] = twin });

        problems.Should().ContainSingle().Which.Should().Contain("smuggled");
    }

    [Fact]
    public void A_nullable_field_whose_twin_rejects_null_is_caught()
    {
        var problems = Consumer.Compare(
            Shape(Object("Response", ("driver", true))),
            Pairing(new() { ["Response"] = "responseSchema" }),
            new Dictionary<string, ZodSchema> { ["responseSchema"] = Schema("responseSchema", "driver") });

        problems.Should().ContainSingle().Which.Should().Contain("does not accept null");
    }

    [Fact]
    public void A_declared_unmirrored_key_is_allowed_but_the_NEXT_one_still_fails()
    {
        var shape = Shape(Object("Settings", ("provider", false), ("docker", false), ("kubernetes", false)));
        var pairing = Pairing(new() { ["Settings"] = "settingsSchema" });
        pairing.UnmirroredKeys["Settings"] = ["docker"];

        var problems = Consumer.Compare(shape, pairing,
            new Dictionary<string, ZodSchema> { ["settingsSchema"] = Schema("settingsSchema", "provider") });

        problems.Should().ContainSingle().Which.Should().Contain("kubernetes");
    }

    [Fact]
    public void An_unmirrored_key_the_backend_stopped_sending_fails_rather_than_standing_for_nothing()
    {
        var pairing = Pairing(new() { ["Response"] = "responseSchema" });
        pairing.UnmirroredKeys["Response"] = ["gone"];

        var problems = Consumer.Compare(
            Shape(Object("Response", ("id", false))), pairing,
            new Dictionary<string, ZodSchema> { ["responseSchema"] = Schema("responseSchema", "id") });

        problems.Should().ContainSingle().Which.Should().Contain("does not send any more");
    }

    [Fact]
    public void A_schema_with_no_backend_half_has_to_say_what_it_mirrors()
    {
        var problems = Consumer.Compare(
            Shape(Object("Response", ("id", false))),
            Pairing(new() { ["Response"] = "responseSchema" }),
            new Dictionary<string, ZodSchema>
            {
                ["responseSchema"] = Schema("responseSchema", "id"),
                ["orphanSchema"] = Schema("orphanSchema", "id"),
            });

        problems.Should().ContainSingle().Which.Should().Contain("orphanSchema");
    }

    [Fact]
    public void A_frontendOnly_entry_that_names_a_deleted_schema_fails()
    {
        var pairing = Pairing(new() { ["Response"] = "responseSchema" });
        pairing.FrontendOnly["goneSchema"] = "mirrors a host type";

        var problems = Consumer.Compare(
            Shape(Object("Response", ("id", false))), pairing,
            new Dictionary<string, ZodSchema> { ["responseSchema"] = Schema("responseSchema", "id") });

        problems.Should().ContainSingle().Which.Should().Contain("goneSchema");
    }

    [Fact]
    public void An_exemption_with_an_empty_reason_is_not_an_exemption()
    {
        var pairing = Pairing([]);
        pairing.BackendOnly["Response"] = "   ";

        var problems = Consumer.Compare(Shape(Object("Response", ("id", false))), pairing, new Dictionary<string, ZodSchema>());

        problems.Should().ContainSingle().Which.Should().Contain("empty reason");
    }

    [Fact]
    public void A_file_exemption_that_covers_no_published_file_fails()
    {
        var pairing = Pairing(new() { ["Response"] = "responseSchema" });
        pairing.BackendOnlyFiles["src/Moved.cs"] = "moved away";

        var problems = Consumer.Compare(
            Shape(Object("Response", ("id", false))), pairing,
            new Dictionary<string, ZodSchema> { ["responseSchema"] = Schema("responseSchema", "id") });

        problems.Should().ContainSingle().Which.Should().Contain("publishes no type");
    }

    [Fact]
    public void A_type_classified_twice_fails_rather_than_letting_one_classification_win()
    {
        var pairing = Pairing(new() { ["Response"] = "responseSchema" });
        pairing.BackendOnly["Response"] = "also excused";

        var problems = Consumer.Compare(
            Shape(Object("Response", ("id", false))), pairing,
            new Dictionary<string, ZodSchema> { ["responseSchema"] = Schema("responseSchema", "id") });

        problems.Should().Contain(p => p.Contains("classified 2 times"));
    }

    [Fact]
    public void Pairing_a_converter_written_type_is_refused_because_it_would_compare_nothing()
    {
        var custom = new WireType { Name = "Extensions", Kind = "custom", File = "src/C.cs", Converter = "C" };

        var problems = Consumer.Compare(Shape(custom), Pairing(new() { ["Extensions"] = "extensionsSchema" }),
            new Dictionary<string, ZodSchema> { ["extensionsSchema"] = Schema("extensionsSchema", "a") });

        problems.Should().Contain(p => p.Contains("not derivable"));
    }

    [Fact]
    public void An_object_paired_to_a_union_is_refused_rather_than_compared_against_an_empty_key_set()
    {
        var twin = new ZodSchema
        {
            Name = "responseSchema", File = "twin.ts", Kind = ZodKind.Other, Members = [],
            Nullable = new HashSet<string>(StringComparer.Ordinal),
        };

        var problems = Consumer.Compare(
            Shape(Object("Response", ("id", false))),
            Pairing(new() { ["Response"] = "responseSchema" }),
            new Dictionary<string, ZodSchema> { ["responseSchema"] = twin });

        problems.Should().ContainSingle().Which.Should().Contain("not a z.object");
    }
}
