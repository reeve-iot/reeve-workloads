using FluentAssertions;
using Reeve.Workloads.Abstractions;
using Reeve.Workloads.Abstractions.Contracts;
using Reeve.Workloads.Abstractions.Contracts.Deployment;
using Reeve.Workloads.Core.Groups;
using Xunit;

namespace Reeve.Workloads.Core.Tests.Groups;


public sealed class GroupManifestDeploymentContextSourceTests
{
    private const string GroupId = "acme.zwave";

    private readonly InMemoryWorkloadGroupRepository modules = new();

    [Fact]
    public async Task The_declaration_at_the_recorded_version_is_the_one_served()
    {
        var manifests = new FakeManifests().Install(modules, GroupId, new InstalledGroupManifestRead
        {
            InstalledVersion = "1.2.0",
            AtInstalledVersion = Manifest("at-installed"),
            HighestAvailable = Manifest("highest"),
        });

        var context = await Source(manifests).GetForGroupAsync(GroupId);

        context!.GroupId.Should().Be(GroupId);
        context.Deployment.Workloads[0].Id.Should().Be("at-installed");
    }

    [Fact]
    public async Task A_recorded_version_with_no_declaration_degrades_and_never_falls_back()
    {
        var manifests = new FakeManifests().Install(modules, GroupId, new InstalledGroupManifestRead
        {
            InstalledVersion = "1.2.0",
            AtInstalledVersion = null,
            HighestAvailable = Manifest("highest"),
            Bundled = Manifest("bundled"),
        });

        (await Source(manifests).GetForGroupAsync(GroupId)).Should().BeNull();
    }

    [Fact]
    public async Task With_no_recorded_version_the_highest_declaration_is_served()
    {
        var manifests = new FakeManifests().Install(modules, GroupId, new InstalledGroupManifestRead { HighestAvailable = Manifest("highest") });

        var context = await Source(manifests).GetForGroupAsync(GroupId);

        context!.Deployment.Workloads[0].Id.Should().Be("highest");
    }

    [Fact]
    public async Task With_nothing_declared_the_bundled_one_is_served()
    {
        var manifests = new FakeManifests().Install(modules, GroupId, new InstalledGroupManifestRead { Bundled = Manifest("bundled") });

        var context = await Source(manifests).GetForGroupAsync(GroupId);

        context!.GroupId.Should().Be(GroupId);
        context.Deployment.Workloads[0].Id.Should().Be("bundled");
    }

    [Fact]
    public async Task A_refused_declaration_holds_rather_than_falling_through_to_the_bundled_one()
    {
        var manifests = new FakeManifests().Install(modules, GroupId, new InstalledGroupManifestRead { Refused = true, Bundled = Manifest("bundled") });

        (await Source(manifests).GetForGroupAsync(GroupId)).Should().BeNull();
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    public async Task A_blank_module_id_reads_no_manifest_at_all(string groupId)
    {
        var manifests = new FakeManifests();

        var context = await Source(manifests).GetForGroupAsync(groupId);

        context.Should().BeNull();
        manifests.Calls.Should().BeEmpty();
    }

    private GroupManifestDeploymentContextSource Source(FakeManifests manifests) => new(manifests, modules);

    private static DeploymentManifest Manifest(string workloadId) => new()
    {
        SchemaVersion = 1,
        Workloads =
        [
            new WorkloadManifest
            {
                Id = workloadId,
                DisplayName = workloadId,
                Image = new WorkloadImage { Component = "controller" },
                Kind = WorkloadKind.Service,
                Health = new WorkloadHealth { Mode = WorkloadHealthMode.Container },
            },
        ],
    };
}
