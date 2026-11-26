using FluentAssertions;
using Reeve.Workloads.Core;
using Reeve.Workloads.Providers.Kubernetes;
using Xunit;

namespace Reeve.Workloads.Core.Tests;

public sealed class RetainedScanSizeFormattingTests
{
    [Theory]
    [InlineData("512Mi", 512L * 1024 * 1024)]
    [InlineData("1Gi", 1024L * 1024 * 1024)]
    [InlineData("64M", 64_000_000L)]
    [InlineData("", 0L)]
    [InlineData("garbage", 0L)]
    public void ParseSizeToBytes_parses_k8s_quantities(string size, long expected)
        => WorkloadRetainedScanUnit.ParseSizeToBytes(size).Should().Be(expected);

    [Theory]
    [InlineData(0L, "unknown size")]
    [InlineData(1024L * 1024 * 1024, "1 GiB")]
    [InlineData(1536L * 1024 * 1024, "1.5 GiB")]
    public void FormatBytes_formats_binary_sizes(long bytes, string expected)
        => WorkloadRetainedScanUnit.FormatBytes(bytes).Should().Be(expected);
}
