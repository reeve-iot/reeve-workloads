using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Docker.DotNet;
using Docker.DotNet.Models;
using FluentAssertions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Reeve.Workloads.Providers.Docker;

namespace Reeve.Workloads.Providers.Docker.IntegrationTests;

public sealed class DockerDeviceDiscoveryProbeTests
{
    private const string HelperImage = "reeve-device-discovery-helper:latest";
    private const string HelperId = "ephemeral-devdisco-id";

    private readonly DockerDeviceDiscoveryProbe _sut = new();

    [Fact]
    public async Task DiscoverAsync_runs_the_helper_reads_stdout_inventory_and_removes_the_container()
    {
        var client = Substitute.For<IDockerClient>();
        client.Images.InspectImageAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new ImageInspectResponse());

        CreateContainerParameters? captured = null;
        client.Containers.CreateContainerAsync(Arg.Any<CreateContainerParameters>(), Arg.Any<CancellationToken>())
            .Returns(ci => { captured = ci.Arg<CreateContainerParameters>(); return new CreateContainerResponse { ID = HelperId }; });
        client.Containers.WaitContainerAsync(HelperId, Arg.Any<CancellationToken>())
            .Returns(new ContainerWaitResponse { StatusCode = 0 });
        StubStdout(client,
            "/dev/serial/by-id/usb-FTDI_FT232R_A50285BI-if00-port0\n" +
            "/dev/serial/by-id/usb-Silicon_Labs_CP2102-if00-port0\n");

        var result = await _sut.DiscoverAsync(
            client, new DockerDeviceDiscoveryRequest { HelperImage = HelperImage }, CancellationToken.None);

        result.Ran.Should().BeTrue("the helper enumerated to completion");
        result.DevicePaths.Should().Equal(
            "/dev/serial/by-id/usb-FTDI_FT232R_A50285BI-if00-port0",
            "/dev/serial/by-id/usb-Silicon_Labs_CP2102-if00-port0");

        captured.Should().NotBeNull();
        captured!.HostConfig.Should().NotBeNull("the probe renders a HostConfig — the /dev bind has nowhere else to ride");
        captured.HostConfig!.Binds.Should().ContainSingle()
            .Which.Should().Be("/dev/serial/by-id:/dev/serial/by-id:ro");
        captured.Labels.Should().ContainKey(DockerDeviceDiscoveryProbe.HelperMarkerLabelKey)
            .WhoseValue.Should().Be(DockerDeviceDiscoveryProbe.HelperMarkerLabelValue);

        await client.Containers.Received(1).RemoveContainerAsync(
            HelperId, Arg.Any<ContainerRemoveParameters>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DiscoverAsync_removes_the_helper_container_when_the_run_fails()
    {
        var client = Substitute.For<IDockerClient>();
        client.Images.InspectImageAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new ImageInspectResponse());
        client.Containers.CreateContainerAsync(Arg.Any<CreateContainerParameters>(), Arg.Any<CancellationToken>())
            .Returns(new CreateContainerResponse { ID = HelperId });
        client.Containers.StartContainerAsync(Arg.Any<string>(), Arg.Any<ContainerStartParameters>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("start boom"));

        var result = await _sut.DiscoverAsync(
            client, new DockerDeviceDiscoveryRequest { HelperImage = HelperImage }, CancellationToken.None);

        result.Ran.Should().BeFalse("a helper that could not run yields no inventory");
        result.DevicePaths.Should().BeEmpty();
        result.Message.Should().NotBeNullOrWhiteSpace();

        await client.Containers.Received(1).RemoveContainerAsync(
            HelperId, Arg.Any<ContainerRemoveParameters>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DiscoverAsync_runs_the_helper_as_an_unprivileged_user()
    {
        var client = Substitute.For<IDockerClient>();
        client.Images.InspectImageAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new ImageInspectResponse());
        CreateContainerParameters? created = null;
        client.Containers.CreateContainerAsync(Arg.Do<CreateContainerParameters>(p => created = p), Arg.Any<CancellationToken>())
            .Returns(new CreateContainerResponse { ID = HelperId });
        client.Containers.WaitContainerAsync(HelperId, Arg.Any<CancellationToken>())
            .Returns(new ContainerWaitResponse { StatusCode = 0 });
        StubStdout(client, "/dev/serial/by-id/usb-X\n");

        await _sut.DiscoverAsync(
            client, new DockerDeviceDiscoveryRequest { HelperImage = HelperImage }, CancellationToken.None);

        created.Should().NotBeNull();
        created!.User.Should().Be(DockerDeviceDiscoveryProbe.UnprivilegedUser);
    }

    [Fact]
    public async Task DiscoverAsync_empty_or_garbage_stdout_yields_an_empty_inventory_without_throwing()
    {
        var client = Substitute.For<IDockerClient>();
        client.Images.InspectImageAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new ImageInspectResponse());
        client.Containers.CreateContainerAsync(Arg.Any<CreateContainerParameters>(), Arg.Any<CancellationToken>())
            .Returns(new CreateContainerResponse { ID = HelperId });
        client.Containers.WaitContainerAsync(HelperId, Arg.Any<CancellationToken>())
            .Returns(new ContainerWaitResponse { StatusCode = 0 });
        StubStdout(client, "ls: /dev/serial/by-id: No such file or directory\n\n   \n");

        var result = await _sut.DiscoverAsync(
            client, new DockerDeviceDiscoveryRequest { HelperImage = HelperImage }, CancellationToken.None);

        result.Ran.Should().BeTrue("the helper completed — the empty inventory is authoritative");
        result.DevicePaths.Should().BeEmpty("garbage / non-/dev lines are not devices");

        await client.Containers.Received(1).RemoveContainerAsync(
            HelperId, Arg.Any<ContainerRemoveParameters>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DiscoverAsync_when_the_helper_image_is_unavailable_does_not_create_a_container()
    {
        var client = Substitute.For<IDockerClient>();
        client.Images.InspectImageAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("no such image"));
        client.Images.CreateImageAsync(
                Arg.Any<ImagesCreateParameters>(), Arg.Any<AuthConfig>(), Arg.Any<IProgress<JSONMessage>>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("pull failed"));

        var result = await _sut.DiscoverAsync(
            client, new DockerDeviceDiscoveryRequest { HelperImage = HelperImage }, CancellationToken.None);

        result.Ran.Should().BeFalse();
        result.Message.Should().Contain("not available");

        await client.Containers.DidNotReceive().CreateContainerAsync(
            Arg.Any<CreateContainerParameters>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public void ParseInventory_keeps_only_absolute_dev_paths_deduped_and_tolerates_crlf()
    {
        const string stdout =
            "/dev/serial/by-id/usb-A\r\n" +
            "  /dev/serial/by-id/usb-B  \r\n" +
            "/dev/serial/by-id/usb-A\r\n" +
            "not-a-device\r\n" +
            "\r\n";

        var parsed = DockerDeviceDiscoveryProbe.ParseInventory(stdout, DockerDeviceDiscoveryProbe.DefaultDevicePath);

        parsed.Should().Equal("/dev/serial/by-id/usb-A", "/dev/serial/by-id/usb-B");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   \n  \n")]
    [InlineData("ls: cannot access\nsome error text\n")]
    public void ParseInventory_empty_or_garbage_yields_no_devices(string? stdout)
    {
        DockerDeviceDiscoveryProbe.ParseInventory(stdout, DockerDeviceDiscoveryProbe.DefaultDevicePath)
            .Should().BeEmpty();
    }

    [Fact]
    public void BuildEnumerationCommand_enumerates_symlinks_and_does_not_rely_on_e_alone()
    {
        var cmd = DockerDeviceDiscoveryProbe.BuildEnumerationCommand(DockerDeviceDiscoveryProbe.DefaultDevicePath);

        cmd.Should().HaveCount(3);
        cmd[0].Should().Be("sh");
        cmd[1].Should().Be("-c");

        var script = cmd[2];
        script.Should().Contain("-L", "the by-id entries are symlinks; a bare `-e` would dereference to the unmounted target and print nothing");
        script.Should().Contain(DockerDeviceDiscoveryProbe.DefaultDevicePath + "/*");
        script.Should().Contain("echo");
    }

    [Theory]
    [InlineData("/etc")]
    [InlineData("/etc/shadow")]
    [InlineData("/")]
    [InlineData("/var/run/docker.sock")]
    [InlineData("/dev/../etc")]
    public async Task DiscoverAsync_rejects_a_device_path_outside_dev_without_creating_a_container(string badPath)
    {
        var client = Substitute.For<IDockerClient>();
        client.Images.InspectImageAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new ImageInspectResponse());

        var result = await _sut.DiscoverAsync(
            client, new DockerDeviceDiscoveryRequest { HelperImage = HelperImage, DevicePath = badPath }, CancellationToken.None);

        result.Ran.Should().BeFalse("a path outside /dev must not be enumerated");
        result.Message.Should().Contain("not permitted");
        result.DevicePaths.Should().BeEmpty();

        await client.Containers.DidNotReceive().CreateContainerAsync(
            Arg.Any<CreateContainerParameters>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("/dev/serial/by-id", true)]
    [InlineData("/dev/bus/usb", true)]
    [InlineData("/dev", false)]
    [InlineData("/dev/", false)]
    [InlineData("/dev//", false)]
    [InlineData("/etc", false)]
    [InlineData("/dev/../etc", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsAllowedDevicePath_admits_only_paths_under_dev(string? path, bool expected)
    {
        DockerDeviceDiscoveryProbe.IsAllowedDevicePath(path).Should().Be(expected);
    }

    private static void StubStdout(IDockerClient client, string stdout)
    {
        client.Containers.GetContainerLogsAsync(HelperId, Arg.Any<ContainerLogsParameters>(), Arg.Any<CancellationToken>())
            .Returns(_ => new MultiplexedStream(new MemoryStream(Encoding.UTF8.GetBytes(stdout)), multiplexed: false));
    }
}
