using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Docker.DotNet;
using Docker.DotNet.Models;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Reeve.Workloads.Abstractions;
using Reeve.Workloads.Providers.Docker;
using Reeve.Workloads.Providers.Docker.Orphans;
using Xunit;

namespace Reeve.Workloads.Providers.Docker.Tests;

public sealed class DockerWorkloadOrphanServiceTests
{
    private static readonly Guid ThisInstallation = Guid.Parse("11111111-1111-1111-1111-111111111111");

    [Fact]
    public async Task DetectAsync_reports_a_managed_container_whose_workload_row_is_gone()
    {
        var orphanId = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
        var service = BuildService(
            containers: [Container("reeve-wl-virtual-devices-simulator-aaaa", orphanId, ThisInstallation)],
            knownInstanceIds: []);

        var orphans = await service.DetectAsync(CancellationToken.None);

        var orphan = Assert.Single(orphans);
        Assert.Equal(orphanId, orphan.InstanceId);
        Assert.Equal("reeve-wl-virtual-devices-simulator-aaaa", orphan.ContainerNames.Single());
        Assert.Equal("virtual-devices", orphan.GroupId);
        Assert.Equal("simulator", orphan.WorkloadId);
    }

    [Fact]
    public async Task DetectAsync_ignores_a_container_that_still_has_its_row()
    {
        var liveId = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000002");
        var service = BuildService(
            containers: [Container("reeve-wl-live", liveId, ThisInstallation)],
            knownInstanceIds: [liveId]);

        Assert.Empty(await service.DetectAsync(CancellationToken.None));
    }

    [Fact]
    public async Task DetectAsync_reports_another_installations_container_as_foreign_rather_than_hiding_it()
    {
        var foreignInstallation = Guid.Parse("22222222-2222-2222-2222-222222222222");
        var foreignId = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000003");
        var service = BuildService(
            containers: [Container("reeve-wl-foreign", foreignId, foreignInstallation)],
            knownInstanceIds: []);

        var orphan = Assert.Single(await service.DetectAsync(CancellationToken.None));
        Assert.True(orphan.IsForeign);
        Assert.Equal(foreignInstallation.ToString(), orphan.ForeignInstallationId);
    }

    [Fact]
    public async Task DetectAsync_reports_a_foreign_container_even_when_an_unrelated_row_shares_its_id()
    {
        var sharedId = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000005");
        var service = BuildService(
            containers: [Container("reeve-wl-foreign-collide", sharedId, Guid.Parse("22222222-2222-2222-2222-222222222222"))],
            knownInstanceIds: [sharedId]);

        Assert.True(Assert.Single(await service.DetectAsync(CancellationToken.None)).IsForeign);
    }

    [Fact]
    public async Task PurgeAsync_refuses_a_foreign_container_group_without_an_explicit_acknowledgement()
    {
        var foreignId = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000006");
        var service = BuildService(
            containers: [Container("reeve-wl-foreign-purge", foreignId, Guid.Parse("22222222-2222-2222-2222-222222222222"))],
            knownInstanceIds: []);

        await Assert.ThrowsAsync<WorkloadOrphanForeignInstallationException>(
            () => service.PurgeAsync(foreignId, cancellationToken: CancellationToken.None));
    }

    [Fact]
    public async Task PurgeAsync_removes_a_foreign_container_group_once_acknowledged()
    {
        var foreignId = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000007");
        var service = BuildService(
            containers: [Container("reeve-wl-foreign-ack", foreignId, Guid.Parse("22222222-2222-2222-2222-222222222222"))],
            knownInstanceIds: []);

        Assert.Equal(1, await service.PurgeAsync(foreignId, acknowledgeForeignInstallation: true, CancellationToken.None));
    }

    [Fact]
    public async Task DetectAsync_groups_every_container_sharing_one_instance_label()
    {
        var instanceId = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000004");
        var service = BuildService(
            containers:
            [
                Container("reeve-wl-a", instanceId, ThisInstallation),
                Container("reeve-wl-b", instanceId, ThisInstallation),
            ],
            knownInstanceIds: []);

        var orphan = Assert.Single(await service.DetectAsync(CancellationToken.None));
        Assert.Equal(2, orphan.ContainerNames.Count);
    }

    [Fact]
    public async Task DetectAsync_returns_nothing_when_the_daemon_cannot_be_reached()
    {
        var client = Substitute.For<IDockerClient>();
        var containers = Substitute.For<IContainerOperations>();
        containers
            .ListContainersAsync(Arg.Any<ContainersListParameters>(), Arg.Any<CancellationToken>())
            .Returns<Task<IList<ContainerListResponse>>>(_ => throw new DockerApiException(
                System.Net.HttpStatusCode.ServiceUnavailable, "daemon down"));
        client.Containers.Returns(containers);

        var service = BuildService(client, knownInstanceIds: []);

        await Assert.ThrowsAsync<WorkloadOrphanProviderUnreachableException>(
            () => service.DetectAsync(CancellationToken.None));
    }

    private static ContainerListResponse Container(string name, Guid instanceId, Guid installationId) => new()
    {
        ID = Guid.NewGuid().ToString("N"),
        Names = [$"/{name}"],
        Labels = new Dictionary<string, string>
        {
            [DockerProviderConstants.ManagedByLabelKey] = DockerProviderConstants.ManagedByLabelValue,
            [DockerProviderConstants.InstanceLabelKey] = instanceId.ToString("D"),
            [DockerProviderConstants.InstallationLabelKey] = installationId.ToString("D"),
            [DockerProviderConstants.GroupIdLabelKey] = "virtual-devices",
            [DockerProviderConstants.WorkloadIdLabelKey] = "simulator",
        },
    };

    private static DockerWorkloadOrphanService BuildService(
        IReadOnlyList<ContainerListResponse> containers,
        IReadOnlyCollection<Guid> knownInstanceIds)
    {
        var client = Substitute.For<IDockerClient>();
        var operations = Substitute.For<IContainerOperations>();
        operations
            .ListContainersAsync(Arg.Any<ContainersListParameters>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IList<ContainerListResponse>>([.. containers]));
        client.Containers.Returns(operations);
        return BuildService(client, knownInstanceIds);
    }

    private static DockerWorkloadOrphanService BuildService(
        IDockerClient client,
        IReadOnlyCollection<Guid> knownInstanceIds)
    {
        var factory = Substitute.For<IDockerClientFactory>();
        factory.CreateClient(Arg.Any<string?>()).Returns(client);

        var identity = Substitute.For<IWorkloadInstallationIdentity>();
        identity.GetInstallationIdAsync(Arg.Any<CancellationToken>()).Returns(ThisInstallation.ToString("D"));

        var rows = Substitute.For<IWorkloadInstanceIdReader>();
        rows.GetKnownInstanceIdsAsync(Arg.Any<CancellationToken>())
            .Returns<IReadOnlySet<Guid>>(_ => new HashSet<Guid>(knownInstanceIds));

        var socket = Substitute.For<IDockerSocketUriReader>();
        socket.GetDockerSocketUriAsync(Arg.Any<CancellationToken>()).Returns((string?)null);

        return new DockerWorkloadOrphanService(
            factory,
            identity,
            rows,
            socket,
            NullLogger<DockerWorkloadOrphanService>.Instance);
    }
}
