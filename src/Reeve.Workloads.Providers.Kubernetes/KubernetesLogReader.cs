using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using k8s;
using Reeve.Workloads.Abstractions.Providers;

namespace Reeve.Workloads.Providers.Kubernetes;

internal sealed class KubernetesLogReader : IKubernetesLogReader
{
    private readonly IKubernetesPodLogSource _source;

    public KubernetesLogReader(IKubernetesPodLogSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        _source = source;
    }

    public async Task<WorkloadLogs> ReadAsync(
        IKubernetes client,
        string @namespace,
        WorkloadRef reference,
        int tailLines,
        bool previousContainer,
        CancellationToken ct)
    {
        try
        {
            var pods = await _source
                .ListPodsAsync(client, @namespace, reference.InstanceSelectorLabels, ct)
                .ConfigureAwait(false);

            if (pods is null || pods.Count == 0)
            {
                return WorkloadLogs.Empty();
            }

            var target = pods[0];

            var includePrevious = previousContainer || target.RestartCount > 0;

            var currentLog = await _source
                .ReadPodLogAsync(client, @namespace, target.PodName, tailLines, previous: false, ct)
                .ConfigureAwait(false);

            if (currentLog is null)
            {
                return WorkloadLogs.Empty();
            }

            string? previousLog = null;
            if (includePrevious)
            {
                previousLog = await _source
                    .ReadPodLogAsync(client, @namespace, target.PodName, tailLines, previous: true, ct)
                    .ConfigureAwait(false);
            }

            return Assemble(currentLog, previousLog, target.RestartCount, includePrevious, tailLines);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
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
        int restartCount,
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

            assembled.Add(WorkloadLogLine.RestartMarker(
                string.Format(
                    CultureInfo.InvariantCulture,
                    "--- previous container instance (restarts: {0}) ---",
                    restartCount)));
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
