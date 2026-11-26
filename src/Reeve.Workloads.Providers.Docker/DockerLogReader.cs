using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Docker.DotNet;
using Docker.DotNet.Models;
using Reeve.Workloads.Abstractions.Providers;

namespace Reeve.Workloads.Providers.Docker;

internal sealed class DockerLogReader : IDockerLogReader
{
    public const string RestartBoundaryMarkerPrefix = "--- previous container instance";

    private const int ReadBufferSize = 16 * 1024;

    public async Task<WorkloadLogs> ReadAsync(
        IDockerClient client,
        WorkloadRef reference,
        int tailLines,
        bool previousContainer,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(client);

        try
        {
            var listed = await client.Containers
                .ListContainersAsync(
                    new ContainersListParameters
                    {
                        All = true,
                        Filters = InstanceLabelFilter(reference.InstanceId),
                    },
                    cancellationToken)
                .ConfigureAwait(false);

            var mine = (listed ?? new List<ContainerListResponse>())
                .Where(c => HasInstanceLabel(c, reference.InstanceId))
                .OrderByDescending(c => c.Created)
                .ToList();

            if (mine.Count == 0)
            {
                return WorkloadLogs.Empty();
            }

            var current = mine.FirstOrDefault(IsRunning) ?? mine[0];
            var currentHash = SpecHashOf(current);

            ContainerListResponse? previous = null;
            if (previousContainer)
            {
                previous = mine.FirstOrDefault(c =>
                    !string.Equals(c.ID, current.ID, StringComparison.Ordinal)
                    && !string.Equals(SpecHashOf(c), currentHash, StringComparison.Ordinal));
            }

            var includePrevious = previous is not null;

            var currentLog = await ReadContainerTailAsync(client, current.ID, tailLines, cancellationToken)
                .ConfigureAwait(false);

            string? previousLog = null;
            if (includePrevious)
            {
                previousLog = await ReadContainerTailAsync(client, previous!.ID, tailLines, cancellationToken)
                    .ConfigureAwait(false);
            }

            return Assemble(currentLog, previousLog, SpecHashOf(previous), includePrevious, tailLines);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return WorkloadLogs.Empty();
        }
    }

    internal static WorkloadLogs Assemble(
        string? currentLog,
        string? previousLog,
        string? previousSpecHash,
        bool includePrevious,
        int tailLines)
    {
        var assembled = new List<WorkloadLogLine>();

        if (includePrevious)
        {
            foreach (var text in SplitLines(previousLog))
            {
                assembled.Add(WorkloadLogTail.ParseTimestampedLine(text));
            }

            assembled.Add(WorkloadLogLine.RestartMarker(BuildRestartMarker(previousSpecHash)));
        }

        foreach (var text in SplitLines(currentLog))
        {
            assembled.Add(WorkloadLogTail.ParseTimestampedLine(text));
        }

        if (tailLines > 0 && assembled.Count > tailLines)
        {
            var kept = assembled.Skip(assembled.Count - tailLines).ToList();
            return new WorkloadLogs { Lines = kept, Truncated = true };
        }

        return new WorkloadLogs { Lines = assembled };
    }

    private static async Task<string> ReadContainerTailAsync(
        IDockerClient client,
        string containerId,
        int tailLines,
        CancellationToken cancellationToken)
    {
        var parameters = new ContainerLogsParameters
        {
            ShowStdout = true,
            ShowStderr = true,
            Follow = false,
            Timestamps = true,
            Tail = tailLines > 0 ? tailLines.ToString(CultureInfo.InvariantCulture) : "all",
        };

        try
        {
            using var stream = await client.Containers
                .GetContainerLogsAsync(containerId, parameters, cancellationToken)
                .ConfigureAwait(false);

            return await DemuxToTextAsync(stream, cancellationToken).ConfigureAwait(false);
        }
        catch (DockerApiException)
        {
            return string.Empty;
        }
    }

    internal static async Task<string> DemuxToTextAsync(
        MultiplexedStream stream,
        CancellationToken cancellationToken,
        long? maxBytes = null)
    {
        using var merged = new MemoryStream();
        var buffer = new byte[ReadBufferSize];
        var capped = maxBytes is > 0;

        while (true)
        {
            var read = await stream.ReadOutputAsync(buffer, 0, buffer.Length, cancellationToken).ConfigureAwait(false);
            if (read.EOF)
            {
                break;
            }

            if (read.Count > 0 && merged.Length < (capped ? maxBytes!.Value : long.MaxValue))
            {
                var take = capped
                    ? (int)Math.Min(read.Count, maxBytes!.Value - merged.Length)
                    : read.Count;
                merged.Write(buffer, 0, take);
            }
        }

        return Encoding.UTF8.GetString(merged.ToArray());
    }

    private static string BuildRestartMarker(string? previousSpecHash)
    {
        var shortHash = string.IsNullOrEmpty(previousSpecHash)
            ? "unknown"
            : previousSpecHash.Length <= 12 ? previousSpecHash : previousSpecHash[..12];

        return string.Format(CultureInfo.InvariantCulture, "{0} (spec-hash: {1}) ---", RestartBoundaryMarkerPrefix, shortHash);
    }

    private static IDictionary<string, IDictionary<string, bool>> InstanceLabelFilter(Guid instanceId) =>
        new Dictionary<string, IDictionary<string, bool>>(StringComparer.Ordinal)
        {
            ["label"] = new Dictionary<string, bool>(StringComparer.Ordinal)
            {
                [$"{DockerProviderConstants.InstanceLabelKey}={instanceId:D}"] = true,
            },
        };

    private static bool HasInstanceLabel(ContainerListResponse container, Guid instanceId) =>
        container.Labels is not null
        && container.Labels.TryGetValue(DockerProviderConstants.InstanceLabelKey, out var value)
        && string.Equals(value, instanceId.ToString("D"), StringComparison.Ordinal);

    private static bool IsRunning(ContainerListResponse container) =>
        string.Equals(container.State, "running", StringComparison.OrdinalIgnoreCase);

    private static string? SpecHashOf(ContainerListResponse? container) =>
        container?.Labels is not null
        && container.Labels.TryGetValue(DockerProviderConstants.SpecHashLabelKey, out var hash)
            ? hash
            : null;

    private static IReadOnlyList<string> SplitLines(string? raw)
    {
        if (string.IsNullOrEmpty(raw))
        {
            return [];
        }

        var normalized = raw.Replace("\r\n", "\n", StringComparison.Ordinal);

        if (normalized.EndsWith('\n'))
        {
            normalized = normalized[..^1];
        }

        return normalized.Split('\n');
    }
}
