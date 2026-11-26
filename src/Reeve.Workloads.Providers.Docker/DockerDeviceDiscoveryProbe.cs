using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Docker.DotNet;
using Docker.DotNet.Models;

namespace Reeve.Workloads.Providers.Docker;

internal sealed class DockerDeviceDiscoveryProbe : IDockerDeviceDiscoveryProbe
{
    public const string DefaultDevicePath = "/dev/serial/by-id";

    public const string DefaultHelperImage = "busybox:latest";

    public const string HelperMarkerLabelKey = "dev.reeve-iot.device-discovery";

    public const string HelperMarkerLabelValue = "host-serial";

    public const string UnprivilegedUser = "65534:65534";

    private static readonly TimeSpan DiscoveryRunTimeout = TimeSpan.FromSeconds(30);

    private const long StdoutByteCap = 1L * 1024 * 1024;

    public async Task<DockerDeviceDiscoveryResult> DiscoverAsync(
        IDockerClient client,
        DockerDeviceDiscoveryRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(request);

        if (string.IsNullOrWhiteSpace(request.HelperImage))
        {
            return DockerDeviceDiscoveryResult.Unavailable("no device-discovery helper image was configured");
        }

        var devicePath = string.IsNullOrWhiteSpace(request.DevicePath) ? DefaultDevicePath : request.DevicePath.Trim();

        if (!IsAllowedDevicePath(devicePath))
        {
            return DockerDeviceDiscoveryResult.Unavailable(
                $"the device-discovery path '{devicePath}' is not permitted — only paths under '/dev/' may be enumerated");
        }

        if (!await TryEnsureHelperImageAsync(client, request.HelperImage, cancellationToken).ConfigureAwait(false))
        {
            return DockerDeviceDiscoveryResult.Unavailable(
                $"the device-discovery helper image '{request.HelperImage}' is not available (it could not be pulled or found)");
        }

        string? containerId = null;
        try
        {
            var parameters = BuildHelperContainer(request.HelperImage, devicePath);

            var created = await client.Containers.CreateContainerAsync(parameters, cancellationToken).ConfigureAwait(false);
            containerId = created?.ID;
            if (string.IsNullOrEmpty(containerId))
            {
                return DockerDeviceDiscoveryResult.Unavailable("the ephemeral device-discovery helper container could not be created");
            }

            await client.Containers
                .StartContainerAsync(containerId, new ContainerStartParameters(), cancellationToken)
                .ConfigureAwait(false);

            using var waitCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            waitCts.CancelAfter(DiscoveryRunTimeout);
            await client.Containers.WaitContainerAsync(containerId, waitCts.Token).ConfigureAwait(false);

            var stdout = await ReadStdoutAsync(client, containerId, cancellationToken).ConfigureAwait(false);
            var devices = ParseInventory(stdout, devicePath);
            return DockerDeviceDiscoveryResult.Enumerated(devices);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return DockerDeviceDiscoveryResult.Unavailable(
                $"the device-discovery helper did not finish within {DiscoveryRunTimeout.TotalSeconds:0}s");
        }
        catch (Exception ex)
        {
            return DockerDeviceDiscoveryResult.Unavailable($"the ephemeral device-discovery helper could not run: {Summarize(ex.Message)}");
        }
        finally
        {
            if (!string.IsNullOrEmpty(containerId))
            {
                await SafeRemoveAsync(client, containerId!).ConfigureAwait(false);
            }
        }
    }

    internal static IReadOnlyList<string> ParseInventory(string? stdout, string devicePath)
    {
        if (string.IsNullOrWhiteSpace(stdout))
        {
            return [];
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<string>();
        foreach (var raw in stdout.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || !line.StartsWith("/dev/", StringComparison.Ordinal))
            {
                continue;
            }

            if (seen.Add(line))
            {
                result.Add(line);
            }
        }

        return result;
    }

    private static async Task<bool> TryEnsureHelperImageAsync(IDockerClient client, string image, CancellationToken cancellationToken)
    {
        if (await ImagePresentAsync(client, image, cancellationToken).ConfigureAwait(false))
        {
            return true;
        }

        try
        {
            var parsed = DockerRegistryAuth.ParseReference(image);
            await client.Images.CreateImageAsync(
                new ImagesCreateParameters { FromImage = parsed.FromImage, Tag = parsed.Tag },
                authConfig: null,
                new Progress<JSONMessage>(),
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
        }

        return await ImagePresentAsync(client, image, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<bool> ImagePresentAsync(IDockerClient client, string image, CancellationToken cancellationToken)
    {
        try
        {
            await client.Images.InspectImageAsync(image, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static CreateContainerParameters BuildHelperContainer(string image, string devicePath)
        => new()
        {
            Image = image,
            Name = $"reeve-devdisco-{Guid.NewGuid():N}",
            Labels = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [HelperMarkerLabelKey] = HelperMarkerLabelValue,
                [DockerProviderConstants.ManagedByLabelKey] = DockerProviderConstants.ManagedByLabelValue,
            },
            Cmd = BuildEnumerationCommand(devicePath),
            User = UnprivilegedUser,
            HostConfig = new HostConfig
            {
                Binds = new[] { $"{devicePath}:{devicePath}:ro" },
                NetworkMode = "none",
                AutoRemove = false,
            },
        };

    internal static string[] BuildEnumerationCommand(string devicePath) => new[]
    {
        "sh", "-c",
        $"for f in {devicePath}/*; do [ -L \"$f\" ] || [ -e \"$f\" ] && echo \"$f\"; done",
    };

    internal static bool IsAllowedDevicePath(string? devicePath)
    {
        if (string.IsNullOrWhiteSpace(devicePath) || !devicePath.StartsWith("/dev/", StringComparison.Ordinal))
        {
            return false;
        }

        var hasNamedSegment = devicePath["/dev/".Length..]
            .Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Length > 0;
        if (!hasNamedSegment)
        {
            return false;
        }

        foreach (var segment in devicePath.Split('/'))
        {
            if (segment == "..")
            {
                return false;
            }
        }

        return true;
    }

    private static async Task<string> ReadStdoutAsync(IDockerClient client, string containerId, CancellationToken cancellationToken)
    {
        var parameters = new ContainerLogsParameters
        {
            ShowStdout = true,
            ShowStderr = false,
            Follow = false,
            Timestamps = false,
            Tail = "all",
        };

        try
        {
            using var stream = await client.Containers
                .GetContainerLogsAsync(containerId, parameters, cancellationToken)
                .ConfigureAwait(false);

            return await DockerLogReader.DemuxToTextAsync(stream, cancellationToken, StdoutByteCap).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return string.Empty;
        }
    }

    private static async Task SafeRemoveAsync(IDockerClient client, string containerId)
    {
        try
        {
            await client.Containers
                .RemoveContainerAsync(containerId, new ContainerRemoveParameters { Force = true }, CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (Exception)
        {
        }
    }

    private static string Summarize(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return "(no detail)";
        }

        var single = text.Trim().ReplaceLineEndings(" ");
        const int cap = 300;
        return single.Length <= cap ? single : single[..cap] + "…";
    }
}

internal interface IDockerDeviceDiscoveryProbe
{
    Task<DockerDeviceDiscoveryResult> DiscoverAsync(
        IDockerClient client,
        DockerDeviceDiscoveryRequest request,
        CancellationToken cancellationToken);
}

internal sealed record DockerDeviceDiscoveryRequest
{
    public required string HelperImage { get; init; }

    public string DevicePath { get; init; } = DockerDeviceDiscoveryProbe.DefaultDevicePath;
}

internal sealed record DockerDeviceDiscoveryResult
{
    public required bool Ran { get; init; }

    public IReadOnlyList<string> DevicePaths { get; init; } = [];

    public string? Message { get; init; }

    public static DockerDeviceDiscoveryResult Enumerated(IReadOnlyList<string> devicePaths) =>
        new() { Ran = true, DevicePaths = devicePaths ?? [] };

    public static DockerDeviceDiscoveryResult Unavailable(string message) =>
        new() { Ran = false, Message = message };
}
