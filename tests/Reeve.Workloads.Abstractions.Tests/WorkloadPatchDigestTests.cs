using System.Text.Json;
using FluentAssertions;
using Reeve.Workloads.Abstractions.Contracts.Deployment;
using Reeve.Workloads.Abstractions.Domain;
using Reeve.Workloads.Domain;

namespace Reeve.Workloads.Abstractions.Tests;

public sealed class WorkloadPatchDigestTests
{
    private static UnsupportedPodPatch Patch(string rawJson)
    {
        using var doc = JsonDocument.Parse(rawJson);
        var body = doc.RootElement.Clone();
        return new UnsupportedPodPatch { RawJson = body.GetRawText(), Body = body };
    }

    [Fact]
    public void Null_patch_yields_null_digest()
    {
        WorkloadPatchDigest.Normalize((UnsupportedPodPatch?)null).Should().BeNull();
    }

    [Fact]
    public void Reordered_object_keys_yield_the_same_digest()
    {
        var a = Patch("""[{"op":"add","path":"/spec/hostPID","value":true}]""");
        var b = Patch("""[{"value":true,"path":"/spec/hostPID","op":"add"}]""");

        a.RawJson.Should().NotBe(b.RawJson);
        WorkloadPatchDigest.Normalize(a).Should().Be(WorkloadPatchDigest.Normalize(b));
    }

    [Fact]
    public void Different_whitespace_yields_the_same_digest()
    {
        var a = Patch("""[{"op":"add","path":"/spec/hostPID","value":true}]""");
        var b = Patch("[\n  {\n    \"op\": \"add\",\n    \"path\": \"/spec/hostPID\",\n    \"value\": true\n  }\n]");

        a.RawJson.Should().NotBe(b.RawJson);
        WorkloadPatchDigest.Normalize(a).Should().Be(WorkloadPatchDigest.Normalize(b));
    }

    [Fact]
    public void Nested_object_keys_are_sorted_recursively()
    {
        var a = Patch("""[{"op":"add","path":"/x","value":{"b":1,"a":{"d":4,"c":3}}}]""");
        var b = Patch("""[{"op":"add","path":"/x","value":{"a":{"c":3,"d":4},"b":1}}]""");
        WorkloadPatchDigest.Normalize(a).Should().Be(WorkloadPatchDigest.Normalize(b));
    }

    [Fact]
    public void Array_element_ORDER_is_significant_reordering_changes_the_digest()
    {
        var a = Patch("""[{"op":"add","path":"/a","value":1},{"op":"add","path":"/b","value":2}]""");
        var b = Patch("""[{"op":"add","path":"/b","value":2},{"op":"add","path":"/a","value":1}]""");
        WorkloadPatchDigest.Normalize(a).Should().NotBe(WorkloadPatchDigest.Normalize(b));
    }

    [Fact]
    public void Different_values_yield_a_different_digest()
    {
        var a = Patch("""[{"op":"add","path":"/spec/hostPID","value":true}]""");
        var b = Patch("""[{"op":"add","path":"/spec/hostPID","value":false}]""");
        WorkloadPatchDigest.Normalize(a).Should().NotBe(WorkloadPatchDigest.Normalize(b));
    }

    [Fact]
    public void Digest_is_lowercase_hex_sha256_64_chars()
    {
        var a = Patch("""[{"op":"add","path":"/spec/hostPID","value":true}]""");
        var digest = WorkloadPatchDigest.Normalize(a);
        digest.Should().MatchRegex("^[0-9a-f]{64}$");
    }

    [Fact]
    public void Numeric_scalars_that_are_semantically_equal_but_formatted_differently_still_hash_equal()
    {
        var a = Patch("""[{"op":"add","path":"/x","value":1}]""");
        var b = Patch("""[{"op":"add","path":"/x","value":1.0}]""");
        WorkloadPatchDigest.Normalize(a).Should().Be(WorkloadPatchDigest.Normalize(b));
    }

    [Theory]
    [InlineData("1e400")]
    [InlineData("-1e400")]
    [InlineData("1e-400")]
    public void An_out_of_double_range_number_does_not_throw_and_yields_a_deterministic_digest(string number)
    {
        var patch = Patch($$"""[{"op":"add","path":"/x","value":{{number}}}]""");

        string? digest = null;
        var act = () => digest = WorkloadPatchDigest.Normalize(patch);

        act.Should().NotThrow("a valid-JSON number that overflows double must not crash the digest");
        digest.Should().MatchRegex("^[0-9a-f]{64}$");

        WorkloadPatchDigest.Normalize(Patch($$"""[{"op":"add","path":"/x","value":{{number}}}]""")).Should().Be(digest);
    }

    [Fact]
    public void A_400_digit_integer_beyond_decimal_range_does_not_throw_and_is_deterministic()
    {
        var bigInt = new string('9', 400);
        var patch = Patch($$"""[{"op":"add","path":"/x","value":{{bigInt}}}]""");

        string? digest = null;
        var act = () => digest = WorkloadPatchDigest.Normalize(patch);
        act.Should().NotThrow();
        digest.Should().MatchRegex("^[0-9a-f]{64}$");
        WorkloadPatchDigest.Normalize(Patch($$"""[{"op":"add","path":"/x","value":{{bigInt}}}]""")).Should().Be(digest);
    }

    [Fact]
    public void Two_overflow_numbers_with_different_magnitudes_hash_differently()
    {
        var a = Patch("""[{"op":"add","path":"/x","value":1e400}]""");
        var b = Patch("""[{"op":"add","path":"/x","value":2e400}]""");
        WorkloadPatchDigest.Normalize(a).Should().NotBe(WorkloadPatchDigest.Normalize(b));
    }
}
