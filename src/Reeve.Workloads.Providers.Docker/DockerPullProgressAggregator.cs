using System;
using System.Collections.Generic;
using Docker.DotNet.Models;
using Reeve.Workloads.Abstractions;

namespace Reeve.Workloads.Providers.Docker;

internal sealed class DockerPullProgressAggregator
{
    private const long MiB = 1024L * 1024L;

    [SingleNodeState("Per-pull, per-process fold of ONE docker pull's per-layer byte stream on the LOCAL host's docker daemon — a docker pull is inherently single-host and this map is ephemeral progress-bar state, not cluster-shared coordination; each replica reconciling its own docker workloads keeps its own copy and there is nothing to propagate.")]
    private readonly Dictionary<string, LayerBytes> _layers = new(StringComparer.Ordinal);

    private sealed class LayerBytes
    {
        public long Current;
        public long Total;
        public bool Complete;
    }

    public void Observe(JSONMessage message)
    {
        var id = message?.ID;
        if (string.IsNullOrEmpty(id))
        {
            return;
        }

        if (!_layers.TryGetValue(id, out var layer))
        {
            layer = new LayerBytes();
            _layers[id] = layer;
        }

        var status = message!.Status ?? string.Empty;
        if (IsComplete(status))
        {
            layer.Complete = true;
            if (layer.Total > 0)
            {
                layer.Current = layer.Total;
            }

            return;
        }

        var progress = message.Progress;
        var total = progress?.Total ?? 0;
        if (total > 0)
        {
            var current = progress!.Current ?? 0;
            layer.Total = total;
            layer.Current = layer.Complete
                ? layer.Total
                : Math.Max(layer.Current, Math.Min(current, layer.Total));
        }
    }

    public DockerPullProgress Snapshot()
    {
        long sumCurrent = 0;
        long sumTotal = 0;
        foreach (var layer in _layers.Values)
        {
            if (layer.Total > 0)
            {
                sumTotal += layer.Total;
                sumCurrent += Math.Min(layer.Current, layer.Total);
            }
        }

        if (sumTotal <= 0)
        {
            return new DockerPullProgress { Percent = null, Message = "Pulling image…" };
        }

        var percent = (int)Math.Round(100.0 * sumCurrent / sumTotal, MidpointRounding.AwayFromZero);
        percent = Math.Clamp(percent, 0, 100);

        return new DockerPullProgress
        {
            Percent = percent,
            Message = $"Pulling image… {percent}% ({sumCurrent / MiB}/{sumTotal / MiB} MB)",
        };
    }

    private static bool IsComplete(string status) =>
        status.Equals("Download complete", StringComparison.Ordinal)
        || status.Equals("Pull complete", StringComparison.Ordinal)
        || status.Equals("Already exists", StringComparison.Ordinal);
}
