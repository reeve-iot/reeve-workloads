using System;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using k8s;
using k8s.Autorest;
using k8s.Models;
using Microsoft.Extensions.Logging;
using Reeve.Workloads.Abstractions.Contracts;

namespace Reeve.Workloads.Providers.Kubernetes.Relocate;

internal class KubernetesPvcInspector : IKubernetesPvcInspector
{
    public const string HostnameLabel = "kubernetes.io/hostname";

    private const string BoundPhase = "Bound";

    private readonly ILogger<KubernetesPvcInspector> logger;

    public KubernetesPvcInspector(ILogger<KubernetesPvcInspector> logger)
    {
        this.logger = logger;
    }

    public async Task<PvcBinding?> ReadPvcBindingAsync(IKubernetes client, string ns, string pvcName, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentException.ThrowIfNullOrWhiteSpace(ns);
        ArgumentException.ThrowIfNullOrWhiteSpace(pvcName);

        V1PersistentVolumeClaim pvc;
        try
        {
            pvc = await ReadPvcRawAsync(client, ns, pvcName, ct).ConfigureAwait(false);
        }
        catch (HttpOperationException ex) when (ex.Response?.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Reading PVC {Pvc} in {Namespace} failed; treating binding as unknown.", pvcName, ns);
            return new PvcBinding { Name = pvcName, Bound = false, Phase = string.Empty, BindingUnreadable = true };
        }

        var phase = pvc.Status?.Phase ?? string.Empty;
        var bound = string.Equals(phase, BoundPhase, StringComparison.Ordinal);

        var (boundNode, pvReadable) = bound && !string.IsNullOrEmpty(pvc.Spec?.VolumeName)
            ? await ReadPvNodeAffinityAsync(client, pvc.Spec!.VolumeName, ns, pvcName, ct).ConfigureAwait(false)
            : (null, true);

        return new PvcBinding
        {
            Name = pvcName,
            Bound = bound,
            BoundNode = string.IsNullOrEmpty(boundNode) ? null : boundNode,
            Phase = phase,
            BindingUnreadable = !pvReadable,
        };
    }

    public async Task<PvcLiveStatus?> ReadPvcLiveStatusAsync(IKubernetes client, string ns, string pvcName, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentException.ThrowIfNullOrWhiteSpace(ns);
        ArgumentException.ThrowIfNullOrWhiteSpace(pvcName);

        V1PersistentVolumeClaim pvc;
        try
        {
            pvc = await ReadPvcRawAsync(client, ns, pvcName, ct).ConfigureAwait(false);
        }
        catch (HttpOperationException ex) when (ex.Response?.StatusCode == HttpStatusCode.NotFound)
        {
            return new PvcLiveStatus
            {
                Name = pvcName,
                ResizeState = WorkloadVolumeResizeState.Missing,
                ResizeMessage = "The volume's persistent volume claim does not exist.",
            };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Reading live PVC status for {Pvc} in {Namespace} failed; reporting resize state Unknown.", pvcName, ns);
            return new PvcLiveStatus
            {
                Name = pvcName,
                ResizeState = WorkloadVolumeResizeState.Unknown,
            };
        }

        var (state, message) = DeriveResizeState(pvc);
        return new PvcLiveStatus
        {
            Name = pvcName,
            ResizeState = state,
            ResizeMessage = message,
            Phase = pvc.Status?.Phase,
            RequestedSize = ReadStorage(pvc.Spec?.Resources?.Requests),
            ActualCapacity = ReadStorage(pvc.Status?.Capacity),
            StorageClassName = pvc.Spec?.StorageClassName,
        };
    }

    public async Task<bool> DeletePvcAsync(IKubernetes client, string ns, string pvcName, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentException.ThrowIfNullOrWhiteSpace(ns);
        ArgumentException.ThrowIfNullOrWhiteSpace(pvcName);

        try
        {
            await client.CoreV1.DeleteNamespacedPersistentVolumeClaimAsync(pvcName, ns, cancellationToken: ct)
                .ConfigureAwait(false);
            return true;
        }
        catch (HttpOperationException ex) when (ex.Response?.StatusCode == HttpStatusCode.NotFound)
        {
            return true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex,
                "Deleting PVC {Pvc} in {Namespace} failed.",
                pvcName, ns);
            return false;
        }
    }

    internal virtual Task<V1PersistentVolumeClaim> ReadPvcRawAsync(IKubernetes client, string ns, string pvcName, CancellationToken ct)
        => client.CoreV1.ReadNamespacedPersistentVolumeClaimAsync(pvcName, ns, cancellationToken: ct);

    internal virtual Task<V1PersistentVolume> ReadPvRawAsync(IKubernetes client, string pvName, CancellationToken ct)
        => client.CoreV1.ReadPersistentVolumeAsync(pvName, cancellationToken: ct);

    private const string ResizingConditionType = "Resizing";

    private const string FileSystemResizePendingConditionType = "FileSystemResizePending";

    private const string ConditionStatusTrue = "True";
    private const string StorageResourceKey = "storage";

    private static (WorkloadVolumeResizeState State, string? Message) DeriveResizeState(V1PersistentVolumeClaim pvc)
    {
        var conditions = pvc.Status?.Conditions;
        if (conditions is not null)
        {
            var fsPending = conditions.FirstOrDefault(c =>
                string.Equals(c.Type, FileSystemResizePendingConditionType, StringComparison.Ordinal)
                && string.Equals(c.Status, ConditionStatusTrue, StringComparison.OrdinalIgnoreCase));
            if (fsPending is not null)
            {
                return (WorkloadVolumeResizeState.FileSystemResizePending, fsPending.Message);
            }

            var resizing = conditions.FirstOrDefault(c =>
                string.Equals(c.Type, ResizingConditionType, StringComparison.Ordinal)
                && string.Equals(c.Status, ConditionStatusTrue, StringComparison.OrdinalIgnoreCase));
            if (resizing is not null)
            {
                return (WorkloadVolumeResizeState.Resizing, resizing.Message);
            }
        }

        return (WorkloadVolumeResizeState.Stable, null);
    }

    private static string? ReadStorage(System.Collections.Generic.IDictionary<string, ResourceQuantity>? quantities)
        => quantities is not null && quantities.TryGetValue(StorageResourceKey, out var q) && q is not null
            ? q.ToString()
            : null;

    private async Task<(string? Node, bool Readable)> ReadPvNodeAffinityAsync(
        IKubernetes client, string pvName, string ns, string pvcName, CancellationToken ct)
    {
        try
        {
            var pv = await ReadPvRawAsync(client, pvName, ct).ConfigureAwait(false);
            var terms = pv.Spec?.NodeAffinity?.Required?.NodeSelectorTerms;
            if (terms is null)
            {
                return (null, true);
            }

            var hostnames = terms
                .SelectMany(t => t.MatchExpressions ?? Enumerable.Empty<V1NodeSelectorRequirement>())
                .Where(e => string.Equals(e.Key, HostnameLabel, StringComparison.Ordinal))
                .ToList();
            var pinned = terms.Count == 1
                   && hostnames is [{ Values: [var node] } only]
                   && string.Equals(only.OperatorProperty, "In", StringComparison.Ordinal)
                   && !string.IsNullOrWhiteSpace(node)
                ? node
                : null;
            return (pinned, true);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Reading PV {Pv} node-affinity for PVC {Pvc} in {Namespace} failed; bound node unknown.", pvName, pvcName, ns);
            return (null, false);
        }
    }
}
