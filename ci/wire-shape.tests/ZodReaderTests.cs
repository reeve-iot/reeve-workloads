using FluentAssertions;
using Reeve.Workloads.WireShape;

namespace Reeve.Workloads.WireShape.Tests;

public sealed class ZodReaderTests
{
    private static ZodSchema One(string source, string name)
        => ZodReader.Read(source, "twin.ts").Single(x => x.Name == name).Schema;

    [Fact]
    public void A_declaration_wrapped_across_lines_is_the_same_declaration()
    {
        var schema = One("""
            export const setWorkloadResourcesRequestSchema = z
              .object({
                requestsCpu: z.string().nullable(),
              })
              .passthrough()
            """, "setWorkloadResourcesRequestSchema");

        schema.Kind.Should().Be(ZodKind.Object);
        schema.Members.Should().Equal("requestsCpu");
        schema.Passthrough.Should().BeTrue();
    }

    [Fact]
    public void A_nested_objects_nullable_belongs_to_the_nested_key()
    {
        var schema = One("""
            export const outerSchema = z.object({
              inner: z.object({ deep: z.string().nullable() }),
              flat: z.string(),
            })
            """, "outerSchema");

        schema.Members.Should().Equal("inner", "flat");
        schema.Nullable.Should().BeEmpty();
    }

    [Fact]
    public void A_keys_own_nullable_is_recorded()
        => One("export const sSchema = z.object({ driver: z.string().nullable().optional() })", "sSchema")
            .Nullable.Should().Equal("driver");

    [Fact]
    public void Nullish_accepts_null_too()
        => One("export const sSchema = z.object({ driver: z.string().nullish() })", "sSchema")
            .Nullable.Should().Equal("driver");

    [Fact]
    public void An_enum_reduces_to_its_members()
        => One("export const modeSchema = z.enum(['cluster', 'hostNetwork', 'operatorNetwork'])", "modeSchema")
            .Members.Should().Equal("cluster", "hostNetwork", "operatorNetwork");

    [Fact]
    public void A_declaration_that_is_neither_is_Other_rather_than_an_empty_object()
    {
        One("export const sSchema = z.union([a, b])", "sSchema").Kind.Should().Be(ZodKind.Other);
    }

    [Fact]
    public void A_regex_literal_containing_a_slash_pair_does_not_swallow_the_file()
    {
        var schemas = ZodReader.Read("""
            const guidSchema = z.string().regex(/^[0-9a-f]{8}\/\/[0-9a-f]{4}$/i)
            export const sSchema = z.object({ id: guidSchema })
            """, "twin.ts").ToList();

        schemas.Should().ContainSingle();
        schemas[0].Schema.Members.Should().Equal("id");
    }

    [Fact]
    public void A_doc_comment_between_declarations_is_not_a_key()
        => One("""
            /** The 200 body. `items` may be empty. */
            export const listSchema = z.object({
              // items is the only key
              items: z.array(z.string()),
            })
            """, "listSchema").Members.Should().Equal("items");

    [Fact]
    public void A_declaration_that_is_not_a_schema_is_ignored()
        => ZodReader.Read("export const guidPattern = /^[0-9]+$/", "twin.ts").Should().BeEmpty();
}
