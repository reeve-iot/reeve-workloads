using System;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Docker.DotNet;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Reeve.Workloads.Abstractions.Contracts.Deployment;
using Reeve.Workloads.Abstractions.Domain.Entities;
using Reeve.Workloads.Providers.Docker;
using Reeve.Workloads.Providers.Docker.Orphans;
using Reeve.Workloads.Tests.Shared;
using Xunit;

namespace Reeve.Workloads.Providers.Docker.Tests;

public sealed class DockerRetainedVolumeDeleterTests
{
    private const string VolumeName = "reeve-zwave-state-abc12345";
    private const string Socket = "unix:///var/run/docker.sock";

    private readonly IDockerClient client = Substitute.For<IDockerClient>();
    private readonly IVolumeOperations volumes = Substitute.For<IVolumeOperations>();
    private readonly IDockerClientFactory factory = Substitute.For<IDockerClientFactory>();

    public DockerRetainedVolumeDeleterTests()
    {
        client.Volumes.Returns(volumes);
        factory.CreateClient(Arg.Any<string?>()).Returns(client);
    }

    private DockerRetainedVolumeDeleter Deleter()
    {
        var socket = Substitute.For<IDockerSocketUriReader>();
        socket.GetDockerSocketUriAsync(Arg.Any<CancellationToken>()).Returns(Socket);
        return new DockerRetainedVolumeDeleter(factory, socket, NullLogger<DockerRetainedVolumeDeleter>.Instance);
    }

    private static RetainedVolume Row(RetainedVolumeSchemeId scheme, string name = VolumeName) => RetainedVolume.Record(
        Guid.NewGuid(), "zwave", "zwave-js", "state", name, "1Gi", VolumeCriticality.Critical, null,
        StandInProviders.DockerId, scheme, Guid.NewGuid(), Guid.NewGuid(), DateTimeOffset.UnixEpoch);

    [Fact]
    public async Task Removes_the_named_volume_without_force_on_the_configured_socket()
    {
        Assert.True(await Deleter().TryDeletePhysicalVolumeAsync(Row(RetainedVolumeSchemeId.Docker)));

        await volumes.Received(1).RemoveAsync(VolumeName, false, Arg.Any<CancellationToken>());
        factory.Received(1).CreateClient(Socket);
    }

    [Fact]
    public async Task A_volume_already_gone_counts_as_freed()
    {
        volumes.RemoveAsync(VolumeName, false, Arg.Any<CancellationToken>())
            .Returns<Task>(_ => throw new DockerApiException(HttpStatusCode.NotFound, "no such volume"));

        Assert.True(await Deleter().TryDeletePhysicalVolumeAsync(Row(RetainedVolumeSchemeId.Docker)));
    }

    [Fact]
    public async Task A_volume_still_used_by_a_container_is_refused()
    {
        volumes.RemoveAsync(VolumeName, false, Arg.Any<CancellationToken>())
            .Returns<Task>(_ => throw new DockerApiException(HttpStatusCode.Conflict, "volume is in use"));

        Assert.False(await Deleter().TryDeletePhysicalVolumeAsync(Row(RetainedVolumeSchemeId.Docker)));
    }

    [Fact]
    public async Task An_unreachable_daemon_keeps_the_volume_for_a_retry()
    {
        volumes.RemoveAsync(VolumeName, false, Arg.Any<CancellationToken>())
            .Returns<Task>(_ => throw new System.Net.Http.HttpRequestException("daemon down"));

        Assert.False(await Deleter().TryDeletePhysicalVolumeAsync(Row(RetainedVolumeSchemeId.Docker)));
    }

    [Fact]
    public async Task A_row_of_another_scheme_is_refused_without_touching_the_daemon()
    {
        Assert.False(await Deleter().TryDeletePhysicalVolumeAsync(Row(RetainedVolumeSchemeId.Kubernetes, "reeve-wl-zwave-state-abc")));

        await volumes.DidNotReceiveWithAnyArgs().RemoveAsync(default!, default, default);
    }

    [Fact]
    public async Task A_docker_scheme_row_retained_by_another_provider_is_refused_without_touching_the_daemon()
    {
        var row = RetainedVolume.Record(
            Guid.NewGuid(), "zwave", "zwave-js", "state", VolumeName, "1Gi", VolumeCriticality.Critical, null,
            StandInProviders.KubernetesId, RetainedVolumeSchemeId.Docker, Guid.NewGuid(), Guid.NewGuid(), DateTimeOffset.UnixEpoch);

        Assert.False(await Deleter().TryDeletePhysicalVolumeAsync(row));

        await volumes.DidNotReceiveWithAnyArgs().RemoveAsync(default!, default, default);
    }

    [Fact]
    public async Task Cancellation_propagates()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        volumes.RemoveAsync(VolumeName, false, Arg.Any<CancellationToken>())
            .Returns<Task>(_ => throw new OperationCanceledException(cts.Token));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => Deleter().TryDeletePhysicalVolumeAsync(Row(RetainedVolumeSchemeId.Docker), cts.Token));
    }
}
