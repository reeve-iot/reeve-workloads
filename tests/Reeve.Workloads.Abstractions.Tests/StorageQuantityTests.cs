using FluentAssertions;
using Reeve.Workloads.Abstractions.Domain.DesiredSpec;
using Xunit;
using Reeve.Workloads.Domain.DesiredSpec;

namespace Reeve.Workloads.Abstractions.Tests;

public sealed class StorageQuantityTests
{
    [Theory]
    [InlineData("512Mi", 512L * 1024 * 1024)]
    [InlineData("1Gi", 1024L * 1024 * 1024)]
    [InlineData("20Gi", 20L * 1024 * 1024 * 1024)]
    [InlineData("50Gi", 50L * 1024 * 1024 * 1024)]
    [InlineData("1Ti", 1024L * 1024 * 1024 * 1024)]
    [InlineData("64Mi", 64L * 1024 * 1024)]
    [InlineData("1Ki", 1024L)]
    [InlineData("1Pi", 1024L * 1024 * 1024 * 1024 * 1024)]
    [InlineData("1k", 1000L)]
    [InlineData("1M", 1000L * 1000)]
    [InlineData("1G", 1000L * 1000 * 1000)]
    [InlineData("1T", 1000L * 1000 * 1000 * 1000)]
    [InlineData("2G", 2_000_000_000L)]
    [InlineData("512", 512L)]
    [InlineData("1073741824", 1073741824L)]
    [InlineData("1e3", 1000L)]
    [InlineData("1.5e6", 1_500_000L)]
    [InlineData("+20Gi", 20L * 1024 * 1024 * 1024)]
    public void TryParseBytes_parses_known_quantities(string input, long expected)
    {
        StorageQuantity.TryParseBytes(input, out var bytes).Should().BeTrue($"'{input}' is a valid quantity");
        bytes.Should().Be(expected);
    }

    [Fact]
    public void Cross_unit_comparison_is_byte_exact()
    {
        StorageQuantity.TryParseBytes("1Gi", out var gi).Should().BeTrue();
        StorageQuantity.TryParseBytes("1000Mi", out var mi).Should().BeTrue();

        gi.Should().BeGreaterThan(mi);
        gi.Should().Be(1024L * 1024 * 1024);
        mi.Should().Be(1000L * 1024 * 1024);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("abc")]
    [InlineData("64 MB")]
    [InlineData("1.2.3")]
    [InlineData("20GB")]
    [InlineData("Gi")]
    [InlineData("-5Gi")]
    [InlineData("0")]
    [InlineData("0Gi")]
    public void TryParseBytes_rejects_garbage_and_non_positive(string? input)
    {
        StorageQuantity.TryParseBytes(input, out var bytes).Should().BeFalse($"'{input}' is not a usable positive quantity");
        bytes.Should().Be(0);
    }
}
