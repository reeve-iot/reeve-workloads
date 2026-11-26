using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Docker.DotNet;
using Docker.DotNet.Models;
using Reeve.Workloads.Abstractions.Domain.DesiredSpec;

namespace Reeve.Workloads.Providers.Docker;

internal interface IDockerVolumeGroupOwnership
{
    Task<DockerOperationResult> EnsureAsync(IDockerClient client, WorkloadDesiredSpec spec, CancellationToken cancellationToken);
}

internal sealed class DockerVolumeGroupOwnership : IDockerVolumeGroupOwnership
{
    public const string HelperMarkerLabelKey = "dev.reeve-iot.volume-ownership";

    public const string HelperMarkerLabelValue = "fs-group";

    private static readonly TimeSpan DefaultRunTimeout = TimeSpan.FromSeconds(200);

    private const long WalkFailedExitCode = 1;

    private const long HelperMemoryBytes = 128L * 1024 * 1024;
    private const long HelperPidsLimit = 64;
    private const long HelperNanoCpus = 500_000_000;

    public const string HelperInstanceLabelKey = "dev.reeve-iot.volume-ownership.instance";

    private readonly TimeSpan _runTimeout;

    public DockerVolumeGroupOwnership()
        : this(DefaultRunTimeout)
    {
    }

    internal DockerVolumeGroupOwnership(TimeSpan runTimeout) => _runTimeout = runTimeout;

    private const long LogByteCap = 16 * 1024;

    internal const string Script = """
        gid="$1"; shift
        for t in find chgrp chmod; do command -v "$t" >/dev/null 2>&1 || exit 127; done
        rc=0
        for d in "$@"; do
          if [ -z "$(find "$d" -maxdepth 0 -group "$gid" -perm -2070 2>/dev/null)" ]; then
            ok=1
            find "$d" -mindepth 1 -xdev ! -group "$gid" -exec chgrp -h "$gid" {} + || ok=0
            find "$d" -mindepth 1 -xdev -type d ! -perm -2070 -exec chmod g+rwxs {} + || ok=0
            find "$d" -mindepth 1 -xdev ! -type d ! -type l ! -perm -060 -exec chmod g+rw {} + || ok=0
            if [ "$ok" = 1 ]; then
              chgrp "$gid" "$d" && chmod g+rwxs "$d" || ok=0
            fi
            [ "$ok" = 1 ] || rc=1
          fi
        done
        exit $rc
        """;

    public async Task<DockerOperationResult> EnsureAsync(
        IDockerClient client, WorkloadDesiredSpec spec, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(spec);

        if (spec.Privileges?.FsGroup is not { } fsGroup)
        {
            return DockerOperationResult.Ok();
        }

        var writable = spec.Volumes;
        if (writable.Count == 0)
        {
            return DockerOperationResult.Ok();
        }

        string? containerId = null;
        try
        {
            await RemoveLeftoverHelpersAsync(client, spec.InstanceId, cancellationToken).ConfigureAwait(false);

            var created = await client.Containers
                .CreateContainerAsync(BuildHelper(spec.Image, spec.InstanceId, fsGroup, writable), cancellationToken)
                .ConfigureAwait(false);
            containerId = created?.ID;
            if (string.IsNullOrEmpty(containerId))
            {
                return DockerOperationResult.Fail(DockerFailureKind.Error, "the volume-ownership helper container could not be created.");
            }

            await client.Containers
                .StartContainerAsync(containerId, new ContainerStartParameters(), cancellationToken)
                .ConfigureAwait(false);

            using var waitCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            waitCts.CancelAfter(_runTimeout);
            ContainerWaitResponse exit;
            try
            {
                exit = await client.Containers.WaitContainerAsync(containerId, waitCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return DockerOperationResult.Fail(
                    DockerFailureKind.Error,
                    $"giving volumes to group {fsGroup.ToString(CultureInfo.InvariantCulture)} did not finish within "
                    + $"{_runTimeout.TotalSeconds:0} seconds; the next apply carries on from where it stopped.");
            }

            if (exit.StatusCode == 0 && string.IsNullOrEmpty(exit.Error?.Message))
            {
                return DockerOperationResult.Ok();
            }

            var detail = await ReadLogAsync(client, containerId, cancellationToken).ConfigureAwait(false);
            if (exit.StatusCode == WalkFailedExitCode && string.IsNullOrEmpty(exit.Error?.Message))
            {
                return DockerOperationResult.Fail(
                    DockerFailureKind.Error,
                    $"giving the volumes of workload '{spec.WorkloadId}' to group {fsGroup.ToString(CultureInfo.InvariantCulture)} did not complete "
                    + "(a file may have changed during the walk); the volume root stays unmarked, so the next apply redoes it."
                    + (string.IsNullOrWhiteSpace(detail) ? string.Empty : " " + detail));
            }

            return DockerOperationResult.Fail(
                DockerFailureKind.Rejected,
                $"giving the volumes of workload '{spec.WorkloadId}' to group {fsGroup.ToString(CultureInfo.InvariantCulture)} failed "
                + $"(exit {exit.StatusCode.ToString(CultureInfo.InvariantCulture)}{(string.IsNullOrEmpty(exit.Error?.Message) ? string.Empty : ", " + Summarize(exit.Error.Message))}). "
                + "The image must provide sh, find, chgrp and chmod."
                + (string.IsNullOrWhiteSpace(detail) ? string.Empty : " " + detail));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (DockerApiException ex)
        {
            return DockerOperationResult.Fail(
                ex.StatusCode >= HttpStatusCode.InternalServerError ? DockerFailureKind.Error : DockerFailureKind.Rejected,
                $"the volume-ownership helper could not run: {Summarize(ex.Message)}");
        }
        catch (Exception ex)
        {
            return DockerOperationResult.Fail(DockerFailureKind.Unreachable, $"the volume-ownership helper could not run: {Summarize(ex.Message)}");
        }
        finally
        {
            if (!string.IsNullOrEmpty(containerId))
            {
                await SafeRemoveAsync(client, containerId!).ConfigureAwait(false);
            }
        }
    }

    internal static CreateContainerParameters BuildHelper(string image, Guid instanceId, int fsGroup, IReadOnlyList<ResolvedVolume> volumes)
    {
        var entrypoint = new List<string> { "/bin/sh", "-c", Script, "reeve-volume-ownership", fsGroup.ToString(CultureInfo.InvariantCulture) };
        entrypoint.AddRange(volumes.Select(v => v.MountPath));

        return new CreateContainerParameters
        {
            Image = image,
            Name = $"reeve-volown-{Guid.NewGuid():N}",
            Labels = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [HelperMarkerLabelKey] = HelperMarkerLabelValue,
                [HelperInstanceLabelKey] = instanceId.ToString("D"),
                [DockerProviderConstants.ManagedByLabelKey] = DockerProviderConstants.ManagedByLabelValue,
            },
            Entrypoint = entrypoint,
            Cmd = new List<string>(),
            User = "0",
            WorkingDir = "/",
            HostConfig = new HostConfig
            {
                Mounts = volumes
                    .Select(v => new Mount { Type = "volume", Source = v.ResourceName, Target = v.MountPath })
                    .ToList(),
                NetworkMode = "none",
                ReadonlyRootfs = true,
                CapDrop = new List<string> { "ALL" },
                CapAdd = new List<string> { "CHOWN", "FOWNER", "DAC_OVERRIDE" },
                SecurityOpt = new List<string> { "no-new-privileges" },
                Memory = HelperMemoryBytes,
                MemorySwap = HelperMemoryBytes,
                PidsLimit = HelperPidsLimit,
                NanoCPUs = HelperNanoCpus,
                AutoRemove = false,
            },
        };
    }

    private static async Task RemoveLeftoverHelpersAsync(IDockerClient client, Guid instanceId, CancellationToken cancellationToken)
    {
        var leftovers = await client.Containers.ListContainersAsync(
            new ContainersListParameters
            {
                All = true,
                Filters = new Dictionary<string, IDictionary<string, bool>>(StringComparer.Ordinal)
                {
                    ["label"] = new Dictionary<string, bool>(StringComparer.Ordinal)
                    {
                        [$"{HelperInstanceLabelKey}={instanceId:D}"] = true,
                    },
                },
            },
            cancellationToken).ConfigureAwait(false);

        foreach (var leftover in leftovers ?? [])
        {
            await SafeRemoveAsync(client, leftover.ID).ConfigureAwait(false);
        }
    }

    private static async Task<string> ReadLogAsync(IDockerClient client, string containerId, CancellationToken cancellationToken)
    {
        try
        {
            using var stream = await client.Containers
                .GetContainerLogsAsync(
                    containerId,
                    new ContainerLogsParameters { ShowStdout = true, ShowStderr = true, Follow = false, Tail = "20" },
                    cancellationToken)
                .ConfigureAwait(false);
            return Summarize(await DockerLogReader.DemuxToTextAsync(stream, cancellationToken, LogByteCap).ConfigureAwait(false));
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
                .RemoveContainerAsync(containerId, new ContainerRemoveParameters { Force = true, RemoveVolumes = false }, CancellationToken.None)
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
