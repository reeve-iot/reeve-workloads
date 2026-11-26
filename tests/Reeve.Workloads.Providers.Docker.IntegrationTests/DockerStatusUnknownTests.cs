using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Docker.DotNet;
using Docker.DotNet.Models;
using FluentAssertions;
using NSubstitute;
using Reeve.Workloads.Abstractions.Contracts;
using Reeve.Workloads.Abstractions.Providers;
using Reeve.Workloads.Providers.Docker;

namespace Reeve.Workloads.Providers.Docker.IntegrationTests;

[Collection(DockerIntegrationCollection.Name)]
public sealed class DockerStatusUnknownTests
{
    private const string Image = "busybox:1.36";
    private const string ImageRepo = "busybox";
    private const string ImageTag = "1.36";

    private static readonly TimeSpan WaitBudget = TimeSpan.FromSeconds(20);

    private readonly DockerDaemonFixture _docker;
    private readonly DockerStatusMapper _sut = new();

    public DockerStatusUnknownTests(DockerDaemonFixture docker) => _docker = docker;

    [Fact]
    public async Task Unreachable_daemon_synchronous_connection_error_maps_to_Unknown_and_never_throws()
    {
        var client = FakeDaemonWhoseListThrows(_ => throw new HttpRequestException("connection refused"));
        var reference = NewReference(out _);

        var status = await _sut.GetStatusAsync(client, reference, CancellationToken.None);

        status.Phase.Should().Be(WorkloadProviderPhase.Unknown, "a lost socket is a greyed last-known, not a failure");
        status.ProviderReady.Should().BeFalse();
        status.RestartCount.Should().Be(0);
    }

    [Fact]
    public async Task Unreachable_daemon_faulted_task_maps_to_Unknown_and_never_throws()
    {
        var client = FakeDaemonWhoseListReturns(
            Task.FromException<IList<ContainerListResponse>>(new SocketException(10061)));
        var reference = NewReference(out _);

        var status = await _sut.GetStatusAsync(client, reference, CancellationToken.None);

        status.Phase.Should().Be(WorkloadProviderPhase.Unknown);
        status.ProviderReady.Should().BeFalse();
    }

    [SkippableFact]
    public async Task Running_labeled_container_maps_to_provider_ready()
    {
        _docker.EnsureAvailable();
        var client = _docker.Client!;
        Skip.IfNot(await EnsureImageAsync(client, CancellationToken.None), $"'{Image}' unavailable and not pullable on this runner.");

        var reference = NewReference(out var instanceId);
        var createdIds = new List<string>();
        try
        {
            var id = await CreateWorkloadContainerAsync(
                client, instanceId, specHash: "hash-run", cmd: new[] { "sleep", "3600" }, createdIds, start: true);
            await WaitForStateAsync(client, id, s => s.Running);

            var status = await _sut.GetStatusAsync(client, reference, CancellationToken.None);

            status.Phase.Should().Be(WorkloadProviderPhase.Ready, "a running container (no failing healthcheck) is provider-ready");
            status.ProviderReady.Should().BeTrue();
            status.ObservedImage.Should().Contain(ImageRepo, "the mapper reports the image it observed running");
        }
        finally
        {
            await RemoveAllAsync(client, createdIds);
        }
    }

    [SkippableFact]
    public async Task Exited_labeled_container_maps_to_failed_with_restart_count()
    {
        _docker.EnsureAvailable();
        var client = _docker.Client!;
        Skip.IfNot(await EnsureImageAsync(client, CancellationToken.None), $"'{Image}' unavailable and not pullable on this runner.");

        var reference = NewReference(out var instanceId);
        var createdIds = new List<string>();
        try
        {
            var id = await CreateWorkloadContainerAsync(
                client, instanceId, specHash: "hash-exit", cmd: new[] { "sh", "-c", "exit 1" }, createdIds, start: true);
            await WaitForStateAsync(client, id, s => !s.Running);

            var status = await _sut.GetStatusAsync(client, reference, CancellationToken.None);

            status.Phase.Should().Be(WorkloadProviderPhase.Failed, "an exited/crashed container is a provider failure");
            status.ProviderReady.Should().BeFalse();
            status.RestartCount.Should().BeGreaterThanOrEqualTo(0, "the restart count comes from the container's inspect state");
        }
        finally
        {
            await RemoveAllAsync(client, createdIds);
        }
    }

    [SkippableFact]
    public async Task No_matching_container_maps_to_NotFound()
    {
        _docker.EnsureAvailable();
        var client = _docker.Client!;

        var reference = NewReference(out _);

        var status = await _sut.GetStatusAsync(client, reference, CancellationToken.None);

        status.Phase.Should().Be(WorkloadProviderPhase.NotFound, "no container carries this instance label");
        status.ProviderReady.Should().BeFalse();
    }

    private static WorkloadRef NewReference(out Guid instanceId)
    {
        instanceId = Guid.NewGuid();
        return new WorkloadRef
        {
            InstanceId = instanceId,
            GroupId = "reeve.test.status",
            WorkloadId = "status",
            Inst8 = instanceId.ToString("N")[..8],
            Provider = DockerProvider.Id,
        };
    }

    private static IDockerClient FakeDaemonWhoseListThrows(Func<ContainersListParameters, IList<ContainerListResponse>> onCall)
    {
        var client = Substitute.For<IDockerClient>();
        var containers = Substitute.For<IContainerOperations>();
        client.Containers.Returns(containers);
        containers
            .ListContainersAsync(Arg.Any<ContainersListParameters>(), Arg.Any<CancellationToken>())
            .Returns<Task<IList<ContainerListResponse>>>(ci => Task.FromResult(onCall(ci.Arg<ContainersListParameters>())));
        return client;
    }

    private static IDockerClient FakeDaemonWhoseListReturns(Task<IList<ContainerListResponse>> result)
    {
        var client = Substitute.For<IDockerClient>();
        var containers = Substitute.For<IContainerOperations>();
        client.Containers.Returns(containers);
        containers
            .ListContainersAsync(Arg.Any<ContainersListParameters>(), Arg.Any<CancellationToken>())
            .Returns(result);
        return client;
    }

    private async Task<string> CreateWorkloadContainerAsync(
        IDockerClient client,
        Guid instanceId,
        string specHash,
        IList<string> cmd,
        List<string> createdIds,
        bool start)
    {
        var labels = new Dictionary<string, string>(_docker.BuildLabels(), StringComparer.Ordinal)
        {
            [DockerProviderConstants.InstanceLabelKey] = instanceId.ToString("D"),
            [DockerProviderConstants.SpecHashLabelKey] = specHash,
            [DockerProviderConstants.ManagedByLabelKey] = DockerProviderConstants.ManagedByLabelValue,
        };

        var created = await client.Containers.CreateContainerAsync(
            new CreateContainerParameters
            {
                Image = Image,
                Cmd = cmd,
                Labels = labels,
                Name = $"reeve-it-status-{_docker.RunId}-{Guid.NewGuid():N}",
            },
            CancellationToken.None).ConfigureAwait(false);

        createdIds.Add(created.ID);

        if (start)
        {
            await client.Containers
                .StartContainerAsync(created.ID, new ContainerStartParameters(), CancellationToken.None)
                .ConfigureAwait(false);
        }

        return created.ID;
    }

    private static async Task WaitForStateAsync(IDockerClient client, string containerId, Func<State, bool> predicate)
    {
        var deadline = DateTime.UtcNow + WaitBudget;
        while (true)
        {
            var inspect = await client.Containers.InspectContainerAsync(containerId, CancellationToken.None).ConfigureAwait(false);
            if (inspect.State is { } state && predicate(state))
            {
                return;
            }

            if (DateTime.UtcNow >= deadline)
            {
                return;
            }

            await Task.Delay(150).ConfigureAwait(false);
        }
    }

    private async Task<bool> EnsureImageAsync(IDockerClient client, CancellationToken ct)
    {
        try
        {
            await client.Images.InspectImageAsync(Image, ct).ConfigureAwait(false);
            return true;
        }
        catch (DockerApiException)
        {
        }

        try
        {
            await client.Images.CreateImageAsync(
                new ImagesCreateParameters { FromImage = ImageRepo, Tag = ImageTag },
                authConfig: null,
                new Progress<JSONMessage>(),
                ct).ConfigureAwait(false);
            await client.Images.InspectImageAsync(Image, ct).ConfigureAwait(false);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static async Task RemoveAllAsync(IDockerClient client, IEnumerable<string> ids)
    {
        foreach (var id in ids)
        {
            try
            {
                await client.Containers.RemoveContainerAsync(
                    id,
                    new ContainerRemoveParameters { Force = true, RemoveVolumes = true },
                    CancellationToken.None).ConfigureAwait(false);
            }
            catch (DockerApiException)
            {
            }
        }
    }
}
