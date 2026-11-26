using Reeve.Workloads.Domain;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Docker.DotNet;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Reeve.Workloads.Abstractions;
using Reeve.Workloads.Abstractions.Contracts;
using Reeve.Workloads.Providers.Docker;
using Reeve.Workloads.Providers.Kubernetes;

namespace Reeve.Workloads.Providers.Docker.IntegrationTests;

public sealed class WorkloadDeviceDiscoveryServiceTests
{
    private readonly IWorkloadOrchestratorSettingsService _settings = Substitute.For<IWorkloadOrchestratorSettingsService>();
    private readonly IDockerClientFactory _clientFactory = Substitute.For<IDockerClientFactory>();
    private readonly IDockerDeviceDiscoveryProbe _probe = Substitute.For<IDockerDeviceDiscoveryProbe>();

    [Fact]
    public async Task DiscoverDevicesAsync_unacknowledged_runs_nothing_and_returns_the_danger_disclosure()
    {
        var sut = BuildService();

        var response = await sut.DiscoverDevicesAsync(acknowledged: false, CancellationToken.None);

        response.Ok.Should().BeTrue();
        response.Acknowledged.Should().BeFalse();
        response.Ran.Should().BeFalse("the helper must NOT run until the operator acknowledges (fail-closed)");
        response.Devices.Should().BeEmpty();

        response.Disclosure.Severity.Should().Be(WorkloadDisclosureSeverity.Danger);
        response.Disclosure.Code.Should().Be(WorkloadDeviceDiscoveryService.DisclosureCode);
        response.Disclosure.Label.Should().Contain(DockerDeviceDiscoveryProbe.DefaultDevicePath);

        await _settings.DidNotReceiveWithAnyArgs().GetAsync(default);
        _clientFactory.DidNotReceiveWithAnyArgs().CreateClient(default);
        await _probe.DidNotReceiveWithAnyArgs().DiscoverAsync(default!, default!, default);
    }

    [Fact]
    public async Task DiscoverDevicesAsync_acknowledged_runs_the_helper_and_maps_the_inventory()
    {
        _settings.GetAsync(Arg.Any<CancellationToken>()).Returns(SettingsFor(DockerProvider.Id));

        _clientFactory.CreateClient(Arg.Any<string?>()).Returns(Substitute.For<IDockerClient>());
        _probe.DiscoverAsync(Arg.Any<IDockerClient>(), Arg.Any<DockerDeviceDiscoveryRequest>(), Arg.Any<CancellationToken>())
            .Returns(DockerDeviceDiscoveryResult.Enumerated(new[] { "/dev/serial/by-id/usb-X" }));

        var sut = BuildService();

        var response = await sut.DiscoverDevicesAsync(acknowledged: true, CancellationToken.None);

        response.Ok.Should().BeTrue();
        response.Acknowledged.Should().BeTrue();
        response.Ran.Should().BeTrue();
        response.Devices.Should().ContainSingle().Which.Path.Should().Be("/dev/serial/by-id/usb-X");

        await _probe.Received(1).DiscoverAsync(
            Arg.Any<IDockerClient>(), Arg.Any<DockerDeviceDiscoveryRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DiscoverDevicesAsync_acknowledged_on_a_non_docker_provider_is_blocked_without_running_the_helper()
    {
        _settings.GetAsync(Arg.Any<CancellationToken>()).Returns(SettingsFor(KubernetesProvider.Id));

        var sut = BuildService();

        var response = await sut.DiscoverDevicesAsync(acknowledged: true, CancellationToken.None);

        response.Ran.Should().BeFalse();
        response.Message.Should().Contain("docker provider");

        _clientFactory.DidNotReceiveWithAnyArgs().CreateClient(default);
        await _probe.DidNotReceiveWithAnyArgs().DiscoverAsync(default!, default!, default);
    }

    [Fact]
    public async Task DiscoverDevicesAsync_asks_the_probe_for_upstream_busybox_by_tag()
    {
        _settings.GetAsync(Arg.Any<CancellationToken>()).Returns(SettingsFor(DockerProvider.Id));
        _clientFactory.CreateClient(Arg.Any<string?>()).Returns(Substitute.For<IDockerClient>());

        DockerDeviceDiscoveryRequest? seen = null;
        _probe.DiscoverAsync(Arg.Any<IDockerClient>(), Arg.Do<DockerDeviceDiscoveryRequest>(r => seen = r), Arg.Any<CancellationToken>())
            .Returns(DockerDeviceDiscoveryResult.Enumerated(new[] { "/dev/serial/by-id/usb-X" }));

        await BuildService().DiscoverDevicesAsync(acknowledged: true, CancellationToken.None);

        seen.Should().NotBeNull();
        seen!.HelperImage.Should().Be("busybox:latest");
        seen.HelperImage.Should().NotContain("reeve", "nothing of ours is in this image any more");
    }

    private static WorkloadOrchestratorSettingsResponse SettingsFor(WorkloadProviderId provider) => new()
    {
        Settings = new WorkloadOrchestratorSettings(),
        EffectiveProvider = provider,
        AutoDetected = false,
        Providers = new List<WorkloadProviderInfo>(),
    };

    private WorkloadDeviceDiscoveryService BuildService() => new(
        _settings,
        _clientFactory,
        _probe,
        NullLogger<WorkloadDeviceDiscoveryService>.Instance);
}
