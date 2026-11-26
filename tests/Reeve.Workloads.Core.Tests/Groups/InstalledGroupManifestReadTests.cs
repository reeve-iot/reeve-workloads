using FluentAssertions;
using NSubstitute;
using Reeve.Workloads.Abstractions;
using Reeve.Workloads.Abstractions.Contracts.Deployment;
using Reeve.Workloads.Core.Groups;
using Xunit;

namespace Reeve.Workloads.Core.Tests.Groups;

public sealed class InstalledGroupManifestReadTests
{
    private const string GroupId = "zigbee2mqtt";

    private readonly IWorkloadGroupManifestSource manifests = Substitute.For<IWorkloadGroupManifestSource>();
    private readonly InMemoryWorkloadGroupRepository modules = new();
    private readonly DeploymentManifest highest = Manifest("highest");
    private readonly DeploymentManifest bundled = Manifest("bundled");
    private readonly DeploymentManifest atVersion = Manifest("at-version");

    public InstalledGroupManifestReadTests()
    {
        manifests.GetAvailableAsync(GroupId, Arg.Any<CancellationToken>())
            .Returns(new AvailableGroupManifestRead { HighestAvailable = highest, Bundled = bundled, Refused = true });
    }

    [Fact]
    public async Task A_module_with_no_row_records_no_version_and_asks_for_none()
    {
        var read = await InstalledGroupManifestRead.ReadAsync(manifests, modules, GroupId, CancellationToken.None);

        read.InstalledVersion.Should().BeNull();
        read.AtInstalledVersion.Should().BeNull();
        read.HighestAvailable.Should().BeSameAs(highest);
        read.Bundled.Should().BeSameAs(bundled);
        read.Refused.Should().BeTrue();
        await manifests.DidNotReceiveWithAnyArgs().GetAtVersionAsync(default!, default!, default!, default);
    }

    [Fact]
    public async Task A_row_without_a_version_asks_for_none()
    {
        modules.Seed(GroupId, "repo-1", installedVersion: null);

        var read = await InstalledGroupManifestRead.ReadAsync(manifests, modules, GroupId, CancellationToken.None);

        read.InstalledVersion.Should().BeNull();
        read.AtInstalledVersion.Should().BeNull();
        await manifests.DidNotReceiveWithAnyArgs().GetAtVersionAsync(default!, default!, default!, default);
    }

    [Fact]
    public async Task A_row_with_a_version_reads_that_version_from_the_source_it_names()
    {
        modules.Seed("ZigBee2MQTT", "repo-1", "1.2.0");
        manifests.GetAtVersionAsync("repo-1", GroupId, "1.2.0", Arg.Any<CancellationToken>())
            .Returns(new GroupManifestRead(atVersion, Refused: false));

        var read = await InstalledGroupManifestRead.ReadAsync(manifests, modules, GroupId, CancellationToken.None);

        read.InstalledVersion.Should().Be("1.2.0");
        read.AtInstalledVersion.Should().BeSameAs(atVersion);
        read.HighestAvailable.Should().BeSameAs(highest);
    }

    [Fact]
    public async Task A_version_the_source_refuses_or_lacks_reads_as_the_mid_materialize_gap()
    {
        modules.Seed(GroupId, "repo-1", "1.2.0");
        manifests.GetAtVersionAsync("repo-1", GroupId, "1.2.0", Arg.Any<CancellationToken>())
            .Returns(GroupManifestRead.Unreadable);

        var read = await InstalledGroupManifestRead.ReadAsync(manifests, modules, GroupId, CancellationToken.None);

        read.InstalledVersion.Should().Be("1.2.0",
            "a recorded version with no manifest must hold the apply path, never fall back to the highest");
        read.AtInstalledVersion.Should().BeNull();
    }

    private static DeploymentManifest Manifest(string name) => new() { SchemaVersion = 1, Workloads = [] };
}
