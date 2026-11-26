using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using k8s;
using k8s.Models;
using Microsoft.Extensions.Logging;

namespace Reeve.Workloads.Providers.Kubernetes;

internal sealed class KubernetesPodLogSource : IKubernetesPodLogSource
{
    internal const string NetworkStatusAnnotationKey = "k8s.v1.cni.cncf.io/network-status";

    private readonly ILogger<KubernetesPodLogSource> _logger;

    public KubernetesPodLogSource(ILogger<KubernetesPodLogSource> logger)
    {
        ArgumentNullException.ThrowIfNull(logger);
        _logger = logger;
    }

    public async Task<IReadOnlyList<PodLogTarget>> ListPodsAsync(
        IKubernetes client,
        string @namespace,
        IReadOnlyDictionary<string, string> selectorLabels,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(selectorLabels);

        var labelSelector = BuildLabelSelector(selectorLabels);

        try
        {
            var list = await client.CoreV1
                .ListNamespacedPodAsync(@namespace, labelSelector: labelSelector, cancellationToken: ct)
                .ConfigureAwait(false);

            var items = list?.Items;
            if (items is null || items.Count == 0)
            {
                return [];
            }

            return items
                .OrderByDescending(p => p.Metadata?.CreationTimestamp ?? DateTime.MinValue)
                .Select(ToTarget)
                .ToList();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(
                ex,
                "Listing pods for log read failed (namespace {Namespace}, selector {Selector}); returning no pods.",
                @namespace,
                labelSelector);
            return [];
        }
    }

    public async Task<string?> ReadPodLogAsync(
        IKubernetes client,
        string @namespace,
        string podName,
        int tailLines,
        bool previous,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(client);

        try
        {
            int? tail = tailLines > 0 ? tailLines : null;

            using var stream = await client.CoreV1
                .ReadNamespacedPodLogAsync(
                    name: podName,
                    namespaceParameter: @namespace,
                    previous: previous,
                    tailLines: tail,
                    timestamps: true,
                    cancellationToken: ct)
                .ConfigureAwait(false);

            if (stream is null)
            {
                return string.Empty;
            }

            using var reader = new StreamReader(stream);
            return await reader.ReadToEndAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(
                ex,
                "Reading pod log failed (pod {Pod}, namespace {Namespace}, previous {Previous}); returning no text.",
                podName,
                @namespace,
                previous);
            return null;
        }
    }

    public async Task<IReadOnlyList<(DateTimeOffset AtUtc, string Reason, string Message, bool Warning)>> ListPodEventsAsync(
        IKubernetes client,
        string @namespace,
        string podName,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(client);

        try
        {
            var list = await client.CoreV1
                .ListNamespacedEventAsync(@namespace, fieldSelector: $"involvedObject.name={podName}", cancellationToken: ct)
                .ConfigureAwait(false);

            return (list?.Items ?? [])
                .Select(e =>
                {
                    var at = e.LastTimestamp ?? e.EventTime ?? e.FirstTimestamp ?? e.Metadata?.CreationTimestamp;
                    return (
                        at.HasValue ? new DateTimeOffset(DateTime.SpecifyKind(at.Value, DateTimeKind.Utc)) : DateTimeOffset.MinValue,
                        e.Reason ?? string.Empty,
                        e.Message ?? string.Empty,
                        string.Equals(e.Type, "Warning", StringComparison.Ordinal));
                })
                .ToList();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Reading events for pod {Pod} in {Namespace} failed; returning none.", podName, @namespace);
            return [];
        }
    }

    private static string BuildLabelSelector(IReadOnlyDictionary<string, string> selectorLabels)
        => string.Join(
            ',',
            selectorLabels
                .OrderBy(kv => kv.Key, StringComparer.Ordinal)
                .Select(kv => $"{kv.Key}={kv.Value}"));

    private static PodLogTarget ToTarget(V1Pod pod)
    {
        var statuses = pod.Status?.ContainerStatuses;
        var restarts = statuses is { Count: > 0 }
            ? statuses.Max(s => s.RestartCount)
            : 0;

        return new PodLogTarget
        {
            PodName = pod.Metadata?.Name ?? string.Empty,
            RestartCount = restarts,
            Phase = pod.Status?.Phase,
            NetworkStatusJson = pod.Metadata?.Annotations is { } annotations
                && annotations.TryGetValue(NetworkStatusAnnotationKey, out var status) ? status : null,
        };
    }
}
