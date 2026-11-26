using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Docker.DotNet;
using Docker.DotNet.Models;
using FluentAssertions;
using Reeve.Workloads.Providers.Docker;

namespace Reeve.Workloads.Providers.Docker.IntegrationTests;

[Collection(DockerIntegrationCollection.Name)]
public sealed class DockerDeviceDiscoverySymlinkFarmTests
{
    private const string Image = "busybox:latest";
    private const string ImageRepo = "busybox";
    private const string ImageTag = "latest";

    private static readonly TimeSpan WaitBudget = TimeSpan.FromSeconds(20);

    private readonly DockerDaemonFixture _docker;

    public DockerDeviceDiscoverySymlinkFarmTests(DockerDaemonFixture docker) => _docker = docker;

    [SkippableFact]
    public async Task EnumerationCommand_lists_symlinks_whose_targets_are_not_mounted()
    {
        _docker.EnsureAvailable();
        Skip.IfNot(!RuntimeInformation.IsOSPlatform(OSPlatform.Windows), "Symlink-farm bind mount requires a POSIX host daemon.");
        var client = _docker.Client!;
        Skip.IfNot(await EnsureImageAsync(client, CancellationToken.None), $"'{Image}' unavailable and not pullable on this runner.");
        Skip.IfNot(await DaemonSharesTestFilesystemAsync(client), SharedFsSkipReason);

        var farm = Path.Combine(Path.GetTempPath(), "reeve-devdisco-" + Guid.NewGuid().ToString("N")[..12]);
        Directory.CreateDirectory(farm);

        const string ContainerMount = "/dev/serial/by-id";
        var createdIds = new List<string>();
        try
        {
            CreateSymlink(Path.Combine(farm, "usb-FTDI_FT232R_A50285BI-if00-port0"), "/dev/ttyUSB0");
            CreateSymlink(Path.Combine(farm, "usb-Silicon_Labs_CP2102-if00-port0"), "/dev/ttyUSB1");
            CreateSymlink(Path.Combine(farm, "usb-Broken_Link-if00"), "/dev/does-not-exist-anywhere");

            var stdout = await RunEnumerationAsync(client, farm, ContainerMount, createdIds);

            var lines = stdout
                .Replace("\r\n", "\n", StringComparison.Ordinal)
                .Split('\n')
                .Select(l => l.Trim())
                .Where(l => l.Length > 0)
                .ToList();

            lines.Should().Contain(ContainerMount + "/usb-FTDI_FT232R_A50285BI-if00-port0");
            lines.Should().Contain(ContainerMount + "/usb-Silicon_Labs_CP2102-if00-port0");
            lines.Should().Contain(ContainerMount + "/usb-Broken_Link-if00");

            var inventory = DockerDeviceDiscoveryProbe.ParseInventory(stdout, ContainerMount);
            inventory.Should().Contain(ContainerMount + "/usb-FTDI_FT232R_A50285BI-if00-port0");
        }
        finally
        {
            await RemoveAllAsync(client, createdIds);
            TryDeleteDir(farm);
        }
    }

    [SkippableFact]
    public async Task EnumerationCommand_on_an_empty_directory_prints_nothing()
    {
        _docker.EnsureAvailable();
        Skip.IfNot(!RuntimeInformation.IsOSPlatform(OSPlatform.Windows), "Bind mount requires a POSIX host daemon.");
        var client = _docker.Client!;
        Skip.IfNot(await EnsureImageAsync(client, CancellationToken.None), $"'{Image}' unavailable and not pullable on this runner.");
        Skip.IfNot(await DaemonSharesTestFilesystemAsync(client), SharedFsSkipReason);

        var farm = Path.Combine(Path.GetTempPath(), "reeve-devdisco-empty-" + Guid.NewGuid().ToString("N")[..12]);
        Directory.CreateDirectory(farm);
        var createdIds = new List<string>();
        try
        {
            var stdout = await RunEnumerationAsync(client, farm, "/dev/serial/by-id", createdIds);
            DockerDeviceDiscoveryProbe.ParseInventory(stdout, "/dev/serial/by-id").Should().BeEmpty();
        }
        finally
        {
            await RemoveAllAsync(client, createdIds);
            TryDeleteDir(farm);
        }
    }

    private async Task<bool> DaemonSharesTestFilesystemAsync(IDockerClient client)
    {
        const string Sentinel = "reeve-fs-visibility-probe";
        var probeDir = Path.Combine(Path.GetTempPath(), "reeve-fsprobe-" + Guid.NewGuid().ToString("N")[..12]);
        Directory.CreateDirectory(probeDir);
        var createdIds = new List<string>();
        try
        {
            await File.WriteAllTextAsync(Path.Combine(probeDir, Sentinel), "probe").ConfigureAwait(false);
            var stdout = await RunEnumerationAsync(client, probeDir, "/dev/serial/by-id", createdIds).ConfigureAwait(false);
            return stdout.Contains(Sentinel, StringComparison.Ordinal);
        }
        catch (DockerApiException)
        {
            return false;
        }
        finally
        {
            await RemoveAllAsync(client, createdIds).ConfigureAwait(false);
            TryDeleteDir(probeDir);
        }
    }

    private const string SharedFsSkipReason =
        "dockerd does not resolve bind sources on this test process's filesystem (test runs in a container, daemon is "
        + "on the host), so the bind-mounted symlink farm is invisible to it. Run this suite on a host that owns the "
        + "daemon to exercise it.";

    private async Task<string> RunEnumerationAsync(
        IDockerClient client, string hostFarm, string containerMount, List<string> createdIds)
    {
        var labels = new Dictionary<string, string>(_docker.BuildLabels(), StringComparer.Ordinal)
        {
            [DockerDeviceDiscoveryProbe.HelperMarkerLabelKey] = DockerDeviceDiscoveryProbe.HelperMarkerLabelValue,
        };

        var created = await client.Containers.CreateContainerAsync(
            new CreateContainerParameters
            {
                Image = Image,
                Cmd = DockerDeviceDiscoveryProbe.BuildEnumerationCommand(containerMount),
                Labels = labels,
                Tty = false,
                Name = $"reeve-it-devdisco-{_docker.RunId}-{Guid.NewGuid():N}",
                HostConfig = new HostConfig
                {
                    Binds = new[] { $"{hostFarm}:{containerMount}:ro" },
                    NetworkMode = "none",
                },
            },
            CancellationToken.None).ConfigureAwait(false);
        createdIds.Add(created.ID);

        (await client.Containers.StartContainerAsync(created.ID, new ContainerStartParameters(), CancellationToken.None)
            .ConfigureAwait(false)).Should().BeTrue("the enumeration container should start");

        using var waitCts = new CancellationTokenSource(WaitBudget);
        await client.Containers.WaitContainerAsync(created.ID, waitCts.Token).ConfigureAwait(false);

        using var stream = await client.Containers.GetContainerLogsAsync(
            created.ID,
            new ContainerLogsParameters { ShowStdout = true, ShowStderr = false, Follow = false, Tail = "all" },
            CancellationToken.None).ConfigureAwait(false);

        return await DockerLogReader.DemuxToTextAsync(stream, CancellationToken.None).ConfigureAwait(false);
    }

    private static void CreateSymlink(string linkPath, string target)
    {
        File.CreateSymbolicLink(linkPath, target);
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
                    id, new ContainerRemoveParameters { Force = true, RemoveVolumes = true }, CancellationToken.None)
                    .ConfigureAwait(false);
            }
            catch (DockerApiException)
            {
            }
        }
    }

    private static void TryDeleteDir(string dir)
    {
        try
        {
            Directory.Delete(dir, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
