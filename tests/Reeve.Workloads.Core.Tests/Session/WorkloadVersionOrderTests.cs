using FluentAssertions;
using Reeve.Workloads.Core.Session;
using Xunit;

namespace Reeve.Workloads.Core.Tests.Session;

public sealed class WorkloadVersionOrderTests
{
    [Theory]
    [InlineData("1.2.3", "1.2.4", true)]
    [InlineData("1.2.4", "1.2.3", false)]
    [InlineData("1.2.3", "1.2.3", false)]
    [InlineData("v1.2.3", "1.2.4", true)]
    [InlineData("1.2.3-rc1", "1.2.3", true)]
    [InlineData("1.2.3", "1.2.3-rc1", false)]
    [InlineData("1.2.3+build9", "1.2.4", true)]
    [InlineData("abc", "1.2.3", true)]
    [InlineData("1.2.3", "abc", false)]
    [InlineData("abc", "abd", true)]
    [InlineData(null, "1.2.3", false)]
    [InlineData("1.2.3", null, false)]
    [InlineData("  ", "1.2.3", false)]
    public void Older_means_what_the_hosts_loader_meant_by_it(
        string? running, string? installed, bool expectedOlder)
        => WorkloadVersionOrder.IsOlder(running, installed).Should().Be(expectedOlder);
}
