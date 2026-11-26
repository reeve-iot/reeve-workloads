using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Docker.DotNet;
using Docker.DotNet.Models;
using FluentAssertions;
using NSubstitute;
using Reeve.Workloads.Abstractions.Contracts;
using Reeve.Workloads.Abstractions.Providers;
using Reeve.Workloads.Providers.Docker;
using Xunit;

namespace Reeve.Workloads.Providers.Docker.Tests;

public sealed class DockerStatusMapperFleetTests
{
    private static readonly Guid Running = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid Exited = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid Gone = Guid.Parse("33333333-3333-3333-3333-333333333333");
    private static readonly Guid Stranger = Guid.Parse("44444444-4444-4444-4444-444444444444");

    [Fact]
    public async Task The_fleet_shares_one_container_list()
    {
        var (client, calls) = Daemon();

        await new DockerStatusMapper().GetStatusesAsync(
            client, [Reference(Running), Reference(Exited), Reference(Gone)], CancellationToken.None);

        calls.Lists.Should().Be(1, "one list for the fleet is the whole point");
        calls.Inspects.Should().Be(2, "only the two instances with a container need the state a listing omits");
    }

    [Fact]
    public async Task Each_instance_gets_its_own_container()
    {
        var (client, _) = Daemon();

        var statuses = await new DockerStatusMapper().GetStatusesAsync(
            client, [Reference(Running), Reference(Exited), Reference(Gone)], CancellationToken.None);

        statuses[Reference(Running)].Phase.Should().Be(WorkloadProviderPhase.Ready);
        statuses[Reference(Exited)].Phase.Should().Be(WorkloadProviderPhase.Failed);
        statuses[Reference(Gone)].Phase.Should().Be(WorkloadProviderPhase.NotFound);
    }

    [Fact]
    public async Task A_container_labelled_for_an_instance_nobody_asked_about_is_ignored()
    {
        var (client, _) = Daemon();

        var statuses = await new DockerStatusMapper().GetStatusesAsync(client, [Reference(Running)], CancellationToken.None);

        statuses.Should().HaveCount(1);
        statuses.Should().NotContainKey(Reference(Stranger));
    }

    [Fact]
    public async Task An_unreachable_daemon_is_Unknown_for_every_instance()
    {
        var client = Substitute.For<IDockerClient>();
        var containers = Substitute.For<IContainerOperations>();
        containers
            .ListContainersAsync(Arg.Any<ContainersListParameters>(), Arg.Any<CancellationToken>())
            .Returns<Task<IList<ContainerListResponse>>>(_ => throw new TimeoutException("socket gone"));
        client.Containers.Returns(containers);

        var statuses = await new DockerStatusMapper().GetStatusesAsync(
            client, [Reference(Running), Reference(Exited)], CancellationToken.None);

        statuses.Should().HaveCount(2);
        statuses.Values.Should().OnlyContain(s => s.Phase == WorkloadProviderPhase.Unknown);
    }

    [Fact]
    public async Task An_unreachable_daemon_is_Unknown_for_a_single_read_never_NotFound()
    {
        var client = Substitute.For<IDockerClient>();
        var containers = Substitute.For<IContainerOperations>();
        containers
            .ListContainersAsync(Arg.Any<ContainersListParameters>(), Arg.Any<CancellationToken>())
            .Returns<Task<IList<ContainerListResponse>>>(_ => throw new TimeoutException("socket gone"));
        client.Containers.Returns(containers);

        var status = await new DockerStatusMapper().GetStatusAsync(client, Reference(Gone), CancellationToken.None);

        status.Phase.Should().Be(WorkloadProviderPhase.Unknown, "the reconciler recreates a NotFound workload");
    }

    [Fact]
    public async Task A_failed_inspect_of_a_listed_container_is_Unknown_never_NotFound()
    {
        var (client, _) = Daemon();
        client.Containers
            .InspectContainerAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns<Task<ContainerInspectResponse>>(_ => throw new DockerApiException(
                System.Net.HttpStatusCode.InternalServerError, "daemon error"));

        var status = await new DockerStatusMapper().GetStatusAsync(client, Reference(Running), CancellationToken.None);

        status.Phase.Should().Be(WorkloadProviderPhase.Unknown, "only a confirmed absence may read as NotFound");
    }

    [Fact]
    public async Task The_single_read_and_the_fleet_read_agree()
    {
        var (fleetClient, _) = Daemon();
        var (singleClient, _) = Daemon();
        var mapper = new DockerStatusMapper();

        var fleet = await mapper.GetStatusesAsync(fleetClient, [Reference(Running), Reference(Exited)], CancellationToken.None);
        var running = await mapper.GetStatusAsync(singleClient, Reference(Running), CancellationToken.None);
        var exited = await mapper.GetStatusAsync(singleClient, Reference(Exited), CancellationToken.None);

        fleet[Reference(Running)].Phase.Should().Be(running.Phase);
        fleet[Reference(Running)].RestartCount.Should().Be(running.RestartCount);
        fleet[Reference(Exited)].Phase.Should().Be(exited.Phase);
        fleet[Reference(Exited)].RestartCount.Should().Be(exited.RestartCount);
    }

    [Fact]
    public void A_container_in_restart_backoff_is_Failed_although_docker_still_reports_it_running()
    {
        var status = DockerStatusMapper.Map(
            new ContainerInspectResponse
            {
                RestartCount = 4,
                State = new State { Running = true, Restarting = true, Status = "restarting", ExitCode = 1 },
            },
            observedImage: null);

        status.Phase.Should().Be(WorkloadProviderPhase.Failed);
        status.ProviderReady.Should().BeFalse();
        status.RestartCount.Should().Be(4);
        status.Message.Should().Contain("restarting");
    }

    [Fact]
    public void A_running_container_that_is_not_restarting_is_Ready()
    {
        DockerStatusMapper.Map(
                new ContainerInspectResponse { State = new State { Running = true, Status = "running" } },
                observedImage: null)
            .Phase.Should().Be(WorkloadProviderPhase.Ready);
    }

    private sealed class CallCounts
    {
        public int Lists { get; set; }
        public int Inspects { get; set; }
    }

    private static (IDockerClient Client, CallCounts Calls) Daemon()
    {
        var calls = new CallCounts();
        var containers = Substitute.For<IContainerOperations>();

        containers
            .ListContainersAsync(Arg.Any<ContainersListParameters>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                calls.Lists++;
                return Task.FromResult<IList<ContainerListResponse>>(
                [
                    Listed("c-running", Running, "running"),
                    Listed("c-exited", Exited, "exited"),
                    Listed("c-stranger", Stranger, "running"),
                ]);
            });

        containers
            .InspectContainerAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                calls.Inspects++;
                var id = ci.ArgAt<string>(0);
                return Task.FromResult(id == "c-running" ? Inspected(running: true) : Inspected(running: false));
            });

        var client = Substitute.For<IDockerClient>();
        client.Containers.Returns(containers);
        return (client, calls);
    }

    private static ContainerListResponse Listed(string id, Guid instanceId, string state) => new()
    {
        ID = id,
        State = state,
        Image = "registry.example.com/acme/thread@sha256:deadbeef",
        Created = DateTime.UtcNow,
        Labels = new Dictionary<string, string>
        {
            ["dev.reeve-iot.workload-instance"] = instanceId.ToString("D"),
        },
    };

    private static ContainerInspectResponse Inspected(bool running) => new()
    {
        ID = running ? "c-running" : "c-exited",
        Image = "sha256:deadbeef",
        RestartCount = running ? 0 : 3,
        State = new State
        {
            Running = running,
            Status = running ? "running" : "exited",
            ExitCode = running ? 0 : 1,
        },
    };

    private static WorkloadRef Reference(Guid instanceId) => new()
    {
        InstanceId = instanceId,
        GroupId = "acme.thread",
        WorkloadId = "border-router",
        Inst8 = instanceId.ToString("N")[..8],
        Provider = DockerProvider.Id,
    };
}
