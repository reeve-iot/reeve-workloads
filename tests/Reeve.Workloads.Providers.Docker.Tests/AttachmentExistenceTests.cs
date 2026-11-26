using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Docker.DotNet;
using Docker.DotNet.Models;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Reeve.Workloads.Abstractions;
using Reeve.Workloads.Abstractions.Contracts;
using Reeve.Workloads.Abstractions.Providers;
using Reeve.Workloads.Providers.Docker;
using Xunit;

namespace Reeve.Workloads.Providers.Docker.Tests;

public sealed class AttachmentExistenceTests
{
    [Fact]
    public async Task A_name_the_daemon_lists_exists_and_an_id_prefix_does_not()
    {
        var networks = Substitute.For<INetworkOperations>();
        networks.ListNetworksAsync(Arg.Any<NetworksListParameters>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IList<NetworkResponse>>(
            [
                new NetworkResponse { ID = "abcdef0123456789", Name = "present", Driver = "macvlan" },
                new NetworkResponse { ID = "0123456789abcdef", Name = "present-but-longer", Driver = "bridge" },
            ]));
        var client = Substitute.For<IDockerClient>();
        client.Networks.Returns(networks);
        var provider = Build(client);

        var existence = await provider.GetAttachmentExistenceAsync(["present", "gone", "abcdef01", "present-but"]);

        existence.Status.Should().Be(WorkloadAttachmentExistenceStatus.Read);
        existence.MissingAttachments.Should().Equal("gone", "abcdef01", "present-but");

        var allThere = await provider.GetAttachmentExistenceAsync(["present"]);
        allThere.MissingAttachments.Should().BeEmpty();
        await networks.Received(2).ListNetworksAsync(Arg.Any<NetworksListParameters>(), Arg.Any<CancellationToken>());
        await networks.DidNotReceiveWithAnyArgs().InspectNetworkAsync(default!, default);
    }

    [Fact]
    public async Task A_daemon_fault_is_unreadable()
    {
        var networks = Substitute.For<INetworkOperations>();
        networks.ListNetworksAsync(Arg.Any<NetworksListParameters>(), Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromException<IList<NetworkResponse>>(
                new DockerApiException(System.Net.HttpStatusCode.Forbidden, "denied")));
        var client = Substitute.For<IDockerClient>();
        client.Networks.Returns(networks);

        var existence = await Build(client).GetAttachmentExistenceAsync(["present"]);

        existence.Status.Should().Be(WorkloadAttachmentExistenceStatus.Unreadable);
        existence.MissingAttachments.Should().BeEmpty();
    }

    private static DockerWorkloadProvider Build(IDockerClient client)
    {
        var settingsReader = Substitute.For<IWorkloadOrchestratorSecretReader>();
        settingsReader.GetDecryptedSettingsAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new WorkloadOrchestratorSettings()));
        var serviceProvider = Substitute.For<IServiceProvider>();
        serviceProvider.GetService(typeof(IWorkloadOrchestratorSecretReader)).Returns(settingsReader);
        var scope = Substitute.For<IServiceScope>();
        scope.ServiceProvider.Returns(serviceProvider);
        var scopeFactory = Substitute.For<IServiceScopeFactory>();
        scopeFactory.CreateScope().Returns(scope);
        var clientFactory = Substitute.For<IDockerClientFactory>();
        clientFactory.CreateClient(Arg.Any<string>()).Returns(client);

        return new DockerWorkloadProvider(
            clientFactory,
            Substitute.For<IDockerImagePuller>(),
            Substitute.For<IDockerNetworkManager>(),
            Substitute.For<IDockerVolumeManager>(),
            Substitute.For<IDockerContainerManager>(),
            Substitute.For<IDockerPrivilegeRenderer>(),
            Substitute.For<IDockerResourceRenderer>(),
            Substitute.For<IDockerDeploymentValidator>(),
            Substitute.For<IDockerDaemonGate>(),
            Substitute.For<IDockerJournaledRecreate>(),
            Substitute.For<IDockerLogReader>(),
            Substitute.For<IDockerRollbackCoordinator>(),
            Substitute.For<IDockerStatusMapper>(),
            scopeFactory,
            NullLogger<DockerWorkloadProvider>.Instance);
    }
}
