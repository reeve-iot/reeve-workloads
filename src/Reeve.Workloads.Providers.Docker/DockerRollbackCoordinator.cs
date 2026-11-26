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
using Reeve.Workloads.Abstractions.Domain.DesiredSpec;
using Reeve.Workloads.Abstractions.Providers;

namespace Reeve.Workloads.Providers.Docker;

internal sealed class DockerRollbackCoordinator : IDockerRollbackCoordinator
{
    private const int EvidenceTailLines = 200;

    private const int ReadBufferSize = 16 * 1024;

    private const int LastOutputMaxChars = 200;

    public async Task<DockerRollbackResult> RollbackAsync(
        IDockerClient client,
        WorkloadDesiredSpec spec,
        DockerContainerRef failedCandidate,
        DockerContainerRef? previousContainer,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(spec);
        ArgumentNullException.ThrowIfNull(failedCandidate);

        var (evidence, exitSummary) = await CaptureCandidateEvidenceAsync(client, failedCandidate, cancellationToken)
            .ConfigureAwait(false);

        await RemoveCandidateAsync(client, failedCandidate.Id, cancellationToken).ConfigureAwait(false);

        var previousRestored = false;
        if (previousContainer is not null)
        {
            previousRestored = await RestorePreviousAsync(client, previousContainer.Id, cancellationToken).ConfigureAwait(false);
        }

        return new DockerRollbackResult
        {
            PreviousRestored = previousRestored,
            CapturedEvidence = evidence,
            Message = BuildMessage(exitSummary, LastOutput(evidence), previousContainer is not null, previousRestored),
        };
    }

    private static async Task<(WorkloadLogs? Evidence, string? ExitSummary)> CaptureCandidateEvidenceAsync(
        IDockerClient client,
        DockerContainerRef candidate,
        CancellationToken cancellationToken)
    {
        var exitSummary = await SafeReadExitSummaryAsync(client, candidate.Id, cancellationToken).ConfigureAwait(false);
        var evidence = await SafeReadRecentLogsAsync(client, candidate.Id, cancellationToken).ConfigureAwait(false);
        return (evidence, exitSummary);
    }

    private static async Task<WorkloadLogs?> SafeReadRecentLogsAsync(
        IDockerClient client,
        string containerId,
        CancellationToken cancellationToken)
    {
        var parameters = new ContainerLogsParameters
        {
            ShowStdout = true,
            ShowStderr = true,
            Follow = false,
            Timestamps = false,
            Tail = EvidenceTailLines.ToString(CultureInfo.InvariantCulture),
        };

        try
        {
            using var stream = await client.Containers
                .GetContainerLogsAsync(containerId, parameters, cancellationToken)
                .ConfigureAwait(false);

            var text = await DemuxToTextAsync(stream, cancellationToken).ConfigureAwait(false);
            var lines = SplitLines(text).Select(l => WorkloadLogLine.Line(l)).ToList();
            return new WorkloadLogs { Lines = lines };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return null;
        }
    }

    private static async Task<string?> SafeReadExitSummaryAsync(
        IDockerClient client,
        string containerId,
        CancellationToken cancellationToken)
    {
        try
        {
            var inspected = await client.Containers.InspectContainerAsync(containerId, cancellationToken).ConfigureAwait(false);
            var state = inspected.State;
            if (state is null)
            {
                return null;
            }

            var summary = $"exit code {state.ExitCode.ToString(CultureInfo.InvariantCulture)}";
            if (state.OOMKilled)
            {
                summary += ", OOMKilled";
            }

            if (!string.IsNullOrWhiteSpace(state.Error))
            {
                summary += $", error: {state.Error}";
            }

            return summary;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return null;
        }
    }

    private static async Task RemoveCandidateAsync(IDockerClient client, string containerId, CancellationToken cancellationToken)
    {
        try
        {
            await client.Containers.RemoveContainerAsync(
                containerId,
                new ContainerRemoveParameters { Force = true, RemoveVolumes = true },
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
        }
    }

    private static async Task<bool> RestorePreviousAsync(IDockerClient client, string containerId, CancellationToken cancellationToken)
    {
        try
        {
            await client.Containers
                .StartContainerAsync(containerId, new ContainerStartParameters(), cancellationToken)
                .ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return false;
        }
    }

    private static string? LastOutput(WorkloadLogs? evidence)
    {
        var last = evidence?.Lines
            .Select(l => l.Text.Trim())
            .LastOrDefault(t => !string.IsNullOrEmpty(t));
        if (last is null)
        {
            return null;
        }

        return last.Length <= LastOutputMaxChars ? last : last[..LastOutputMaxChars] + "...";
    }

    private static string BuildMessage(string? exitSummary, string? lastOutput, bool hadPrevious, bool previousRestored)
    {
        var details = new[] { exitSummary, lastOutput is null ? null : $"last output: {lastOutput}" }
            .Where(d => !string.IsNullOrEmpty(d))
            .ToList();
        var candidatePart = details.Count == 0
            ? "rolled back the failed candidate"
            : $"rolled back the failed candidate ({string.Join(", ", details)})";

        if (!hadPrevious)
        {
            return candidatePart + "; no previous container to restore.";
        }

        return previousRestored
            ? candidatePart + "; restored the previous container to running."
            : candidatePart + "; the previous container could not be restored.";
    }

    private static async Task<string> DemuxToTextAsync(MultiplexedStream stream, CancellationToken cancellationToken)
    {
        using var merged = new MemoryStream();
        var buffer = new byte[ReadBufferSize];

        while (true)
        {
            var read = await stream.ReadOutputAsync(buffer, 0, buffer.Length, cancellationToken).ConfigureAwait(false);
            if (read.EOF)
            {
                break;
            }

            if (read.Count > 0)
            {
                merged.Write(buffer, 0, read.Count);
            }
        }

        return Encoding.UTF8.GetString(merged.ToArray());
    }

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
