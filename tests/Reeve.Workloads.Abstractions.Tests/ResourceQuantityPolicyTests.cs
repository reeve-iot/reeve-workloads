using Reeve.Workloads.Abstractions.Domain.DesiredSpec;
using Xunit;
using Reeve.Workloads.Domain.DesiredSpec;

namespace Reeve.Workloads.Abstractions.Tests;

public class ResourceQuantityPolicyTests
{
    [Theory]
    [InlineData("100m")]
    [InlineData("250m")]
    [InlineData("1")]
    [InlineData("2")]
    [InlineData("1.5")]
    [InlineData("64")]
    public void Strict_cpu_accepts_whole_millicore_values(string value)
    {
        Assert.Null(ResourceQuantityPolicy.Validate(ResourceQuantityKind.Cpu, value));
    }

    [Theory]
    [InlineData("0.0005")]
    [InlineData("100u")]
    [InlineData("1.5m")]
    [InlineData("1e3")]
    [InlineData(".5")]
    [InlineData("128Mi")]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("abc")]
    [InlineData("64 MB")]
    [InlineData("1.2.3")]
    [InlineData("65")]
    public void Strict_cpu_rejects(string value)
    {
        Assert.NotNull(ResourceQuantityPolicy.Validate(ResourceQuantityKind.Cpu, value));
    }

    [Theory]
    [InlineData("64Mi")]
    [InlineData("256Mi")]
    [InlineData("1.5Gi")]
    [InlineData("256M")]
    [InlineData("6Mi")]
    public void Strict_memory_accepts(string value)
    {
        Assert.Null(ResourceQuantityPolicy.Validate(ResourceQuantityKind.Memory, value));
    }

    [Theory]
    [InlineData("512m")]
    [InlineData("128Ki")]
    [InlineData("2Ti")]
    [InlineData("1e3")]
    [InlineData(".5")]
    [InlineData("abc")]
    [InlineData("64 MB")]
    [InlineData("0")]
    [InlineData("-1")]
    public void Strict_memory_rejects(string value)
    {
        Assert.NotNull(ResourceQuantityPolicy.Validate(ResourceQuantityKind.Memory, value));
    }

    [Fact]
    public void A_blank_value_is_not_an_error_it_is_the_absence_of_an_override()
    {
        Assert.Null(ResourceQuantityPolicy.Validate(ResourceQuantityKind.Cpu, null));
        Assert.Null(ResourceQuantityPolicy.Validate(ResourceQuantityKind.Memory, "   "));
    }

    [Fact]
    public void Cpu_parses_to_millicores_so_cross_unit_values_compare()
    {
        Assert.True(ResourceQuantityPolicy.TryParseCpuMillicores("1", out var cores));
        Assert.True(ResourceQuantityPolicy.TryParseCpuMillicores("1000m", out var millis));
        Assert.Equal(cores, millis);
    }

    [Fact]
    public void Memory_parses_to_bytes_so_cross_unit_values_compare()
    {
        Assert.True(ResourceQuantityPolicy.TryParseMemoryBytes("1Gi", out var gi));
        Assert.True(ResourceQuantityPolicy.TryParseMemoryBytes("1024Mi", out var mi));
        Assert.Equal(gi, mi);
    }

    [Fact]
    public void A_cpu_limit_below_its_request_is_rejected_because_kubernetes_rejects_the_pod()
    {
        var effective = new ResolvedResources { RequestsCpu = "500m", LimitsCpu = "250m" };
        var error = ResourceQuantityPolicy.ValidatePair(effective);
        Assert.NotNull(error);
        Assert.Contains("250m", error);
    }

    [Fact]
    public void A_memory_limit_below_its_request_is_rejected_across_units()
    {
        var effective = new ResolvedResources { RequestsMemory = "1Gi", LimitsMemory = "512Mi" };
        Assert.NotNull(ResourceQuantityPolicy.ValidatePair(effective));
    }

    [Fact]
    public void Equal_request_and_limit_spelled_differently_is_accepted()
    {
        var effective = new ResolvedResources { RequestsMemory = "1Gi", LimitsMemory = "1024Mi" };
        Assert.Null(ResourceQuantityPolicy.ValidatePair(effective));
    }

    [Fact]
    public void The_pair_check_reads_a_declared_value_the_STRICT_gate_would_refuse()
    {
        var effective = new ResolvedResources { RequestsMemory = "4Mi", LimitsMemory = "512Mi" };

        Assert.Null(ResourceQuantityPolicy.ValidatePair(effective));
    }

    [Theory]
    [InlineData("1e3")]
    [InlineData(".5")]
    [InlineData("4Mi")]
    public void The_render_gate_accepts_what_the_manifest_parser_accepts(string value)
    {
        Assert.True(ResourceQuantityPolicy.TryParseForRender(ResourceQuantityKind.Memory, value, out var units));
        Assert.True(units > 0);
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("64 MB")]
    [InlineData("")]
    [InlineData("0")]
    public void The_render_gate_refuses_what_would_silently_become_unlimited(string value)
    {
        Assert.False(ResourceQuantityPolicy.TryParseForRender(ResourceQuantityKind.Memory, value, out var units));
        Assert.Equal(0, units);
    }

    [Theory]
    [InlineData(ResourceQuantityKind.Memory, "99999999999Ei")]
    [InlineData(ResourceQuantityKind.Cpu, "99999999999999999999999999")]
    public void The_strict_gate_refuses_an_unrepresentable_magnitude_instead_of_throwing(
        ResourceQuantityKind kind, string value)
    {
        Assert.NotNull(ResourceQuantityPolicy.Validate(kind, value));
    }

    [Theory]
    [InlineData("1e29")]
    [InlineData("1e-2147483648")]
    [InlineData("99999999999Ei")]
    public void The_render_gate_refuses_an_unrepresentable_magnitude_instead_of_throwing(string value)
    {
        Assert.False(ResourceQuantityPolicy.TryParseForRender(ResourceQuantityKind.Memory, value, out var units));
        Assert.Equal(0, units);
    }

    [Theory]
    [InlineData("512m", 1L)]
    [InlineData("1k", 1_000L)]
    [InlineData("2M", 2_000_000L)]
    [InlineData("3Ki", 3L * 1024)]
    public void The_render_gate_reads_every_memory_suffix_the_manifest_grammar_allows(string value, long expected)
    {
        Assert.True(ResourceQuantityPolicy.TryParseForRender(ResourceQuantityKind.Memory, value, out var units));
        Assert.Equal(expected, units);
    }

    [Theory]
    [InlineData("250m", 250L)]
    [InlineData("2", 2_000L)]
    [InlineData("1k", 1_000_000L)]
    [InlineData("1Ki", 1024L * 1000)]
    public void The_render_gate_reads_every_cpu_suffix_the_manifest_grammar_allows(string value, long expected)
    {
        Assert.True(ResourceQuantityPolicy.TryParseForRender(ResourceQuantityKind.Cpu, value, out var units));
        Assert.Equal(expected, units);
    }
}
