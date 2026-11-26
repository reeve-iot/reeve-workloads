using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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
using Reeve.Workloads.Abstractions.Contracts.Deployment;
using Reeve.Workloads.Abstractions.Domain.DesiredSpec;
using Reeve.Workloads.Abstractions.Network;
using Reeve.Workloads.Abstractions.Providers;
using Reeve.Workloads.Providers.Docker;
using Xunit;

namespace Reeve.Workloads.Providers.Docker.Tests;

public sealed class NetworkAttachmentPreflightAbsenceTests
{
    [Fact(DisplayName = "none of the network-attachment ids appears in provider.PreflightAsync's result")]
    public async Task None_of_the_network_attachment_ids_appears_in_provider_PreflightAsync_result()
    {
        var provider = BuildDockerProvider(out _, out _);

        var result = await provider.PreflightAsync(SpecFixture(), CancellationToken.None);

        var ids = result.Checks.Select(c => c.Id).ToList();


        foreach (var networkId in NetworkAttachmentCheckIds.All)
        {
            ids.Should().NotContain(
                networkId,
                $"'{networkId}' belongs to the dedicated network-attachment evaluator and to "
                    + "nothing else: authored here it would fire from the reconcile loop and gate no install");
        }
    }

    [Fact]
    public async Task A_hostPort_workload_on_a_macvlan_attachment_on_a_non_linux_daemon_preflights_with_no_attachment_check()
    {
        var provider = BuildDockerProvider(out var daemonClient, out var networkManager, nonLinuxDaemon: true);

        var result = await provider.PreflightAsync(SpecFixture(), CancellationToken.None);

        var ids = result.Checks.Select(c => c.Id).ToList();
        await daemonClient!.System.Received().GetSystemInfoAsync(Arg.Any<CancellationToken>());
        await daemonClient.Networks.DidNotReceive().ListNetworksAsync(
            Arg.Any<NetworksListParameters>(), Arg.Any<CancellationToken>());
        await networkManager.DidNotReceive().ListAttachableNetworksAsync(
            Arg.Any<IDockerClient>(), Arg.Any<DockerProviderSettings>(), Arg.Any<CancellationToken>());
        ids.Should().NotContain(
            ["docker-hostport-no-bridge-attachment", "docker-attachment-driver-non-linux", "docker-attachment-driver-os"]);
    }

    [Fact(DisplayName = "the network-attachment evaluator is the registered home of the ids")]
    public void The_network_attachment_evaluator_is_the_registered_home_of_the_ids()
    {
        var descriptors = new ServiceCollection();
        descriptors.AddScoped<INetworkAttachmentEvaluator, Reeve.Workloads.Core.Network.NetworkAttachmentEvaluator>();

        descriptors.Should().ContainSingle(d => d.ServiceType == typeof(INetworkAttachmentEvaluator))
            .Which.ImplementationType.Should()
            .Be(typeof(Reeve.Workloads.Core.Network.NetworkAttachmentEvaluator));

        typeof(INetworkAttachmentEvaluator).Assembly.Should().BeSameAs(typeof(PreflightCheck).Assembly,
            "the seam lives beside the PreflightCheck type its three callers already consume — both moved to Reeve.Workloads.Abstractions together, and this asserts they did not come apart");
    }

    private const string MacvlanAttachmentName = "iot-macvlan";

    private static DockerWorkloadProvider BuildDockerProvider(out IDockerClient? daemonClient, out IDockerNetworkManager networkManager, bool nonLinuxDaemon = false)
    {
        var settings = new WorkloadOrchestratorSettings()
            .WithProvider(DockerProvider.Id, new DockerProviderSettings());

        var settingsService = Substitute.For<IWorkloadOrchestratorSecretReader>();
        settingsService.GetDecryptedSettingsAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(settings));

        var serviceProvider = Substitute.For<IServiceProvider>();
        serviceProvider.GetService(typeof(IWorkloadOrchestratorSecretReader)).Returns(settingsService);
        var scope = Substitute.For<IServiceScope>();
        scope.ServiceProvider.Returns(serviceProvider);
        var scopeFactory = Substitute.For<IServiceScopeFactory>();
        scopeFactory.CreateScope().Returns(scope);

        var clientFactory = Substitute.For<IDockerClientFactory>();
        clientFactory.CreateClient(Arg.Any<string>()).Returns(Substitute.For<IDockerClient>());

        var validator = Substitute.For<IDockerDeploymentValidator>();
        validator.Validate(Arg.Any<WorkloadDesiredSpec>()).Returns(Array.Empty<PreflightCheck>());

        daemonClient = null;
        IDockerDaemonGate daemonGate;
        if (nonLinuxDaemon)
        {
            var system = Substitute.For<ISystemOperations>();
            system.GetVersionAsync(Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(new VersionResponse { Os = "windows", Arch = "amd64" }));
            system.GetSystemInfoAsync(Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(new SystemInfoResponse { OSType = "windows", Architecture = "x86_64" }));
            var client = Substitute.For<IDockerClient>();
            client.System.Returns(system);
            var networks = Substitute.For<INetworkOperations>();
            networks.ListNetworksAsync(Arg.Any<NetworksListParameters>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult<IList<NetworkResponse>>(
                    [new NetworkResponse { Name = MacvlanAttachmentName, Driver = "macvlan", Scope = "local" }]));
            client.Networks.Returns(networks);
            clientFactory.CreateClient(Arg.Any<string>()).Returns(client);
            daemonClient = client;
            daemonGate = new DockerDaemonGate();
        }
        else
        {
            daemonGate = Substitute.For<IDockerDaemonGate>();
            daemonGate.EvaluateAsync(Arg.Any<IDockerClient>(), Arg.Any<WorkloadDesiredSpec>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult<IReadOnlyList<PreflightCheck>>(Array.Empty<PreflightCheck>()));
        }

        networkManager = Substitute.For<IDockerNetworkManager>();

        return new DockerWorkloadProvider(
            clientFactory,
            Substitute.For<IDockerImagePuller>(),
            networkManager,
            Substitute.For<IDockerVolumeManager>(),
            Substitute.For<IDockerContainerManager>(),
            Substitute.For<IDockerPrivilegeRenderer>(),
            Substitute.For<IDockerResourceRenderer>(),
            validator,
            daemonGate,
            Substitute.For<IDockerJournaledRecreate>(),
            Substitute.For<IDockerLogReader>(),
            Substitute.For<IDockerRollbackCoordinator>(),
            Substitute.For<IDockerStatusMapper>(),
            scopeFactory,
            NullLogger<DockerWorkloadProvider>.Instance);
    }

    private static WorkloadDesiredSpec SpecFixture() => new()
    {
        GroupInstanceId = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee"),
        GroupId = "com.reeve.iot",
        WorkloadId = "bridge",
        InstanceId = Guid.Parse("11111111-2222-3333-4444-555555555555"),
        Inst8 = "11111111",
        Provider = DockerProvider.Id,
        ResourceName = "reeve-iot-11111111",
        SecretName = "reeve-iot-11111111-secret",
        Labels = new Dictionary<string, string>(),
        Image = "ghcr.io/reeve/iot@sha256:" + new string('0', 64),
        Kind = WorkloadKind.Service,
        Health = new WorkloadHealth { Mode = WorkloadHealthMode.Container },
        Network = new ResolvedNetwork
        {
            Mode = WorkloadNetworkMode.OperatorNetwork,
            Attachments = [MacvlanAttachmentName],
            Ports = [new ResolvedPort { Name = "api", Port = 8123, Protocol = "tcp", HostPort = 8123 }],
        },
        SpecHash = "deadbeef",
    };
}
