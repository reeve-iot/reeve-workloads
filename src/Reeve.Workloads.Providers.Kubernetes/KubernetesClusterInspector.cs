using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Security.Authentication;
using System.Threading;
using System.Threading.Tasks;
using k8s;
using k8s.Autorest;
using k8s.Models;

namespace Reeve.Workloads.Providers.Kubernetes;

internal class KubernetesClusterInspector : IKubernetesClusterInspector
{
    private const string ArchLabel = "kubernetes.io/arch";
    private const string PsaLabelPrefix = "pod-security.kubernetes.io/";

    public async Task<KubernetesClusterInspection> InspectAsync(IKubernetes client, string @namespace, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentException.ThrowIfNullOrWhiteSpace(@namespace);

        var nodes = await ReadAsync(
            async () => MapNodes(await ReadNodesRawAsync(client, ct).ConfigureAwait(false)),
            ct).ConfigureAwait(false);
        if (Terminal(nodes) is { } t1)
        {
            return t1;
        }

        var psa = await ReadAsync(
            async () => MapPsaLabels(await ReadNamespaceRawAsync(client, @namespace, ct).ConfigureAwait(false)),
            ct).ConfigureAwait(false);
        if (Terminal(psa) is { } t2)
        {
            return t2;
        }

        var storageClasses = await ReadAsync(
            async () => MapStorageClasses(await ReadStorageClassesRawAsync(client, ct).ConfigureAwait(false)),
            ct).ConfigureAwait(false);
        if (Terminal(storageClasses) is { } t3)
        {
            return t3;
        }

        var quota = await ReadAsync(
            async () => (bool?)AnyQuotaRequiresRequests(await ReadResourceQuotasRawAsync(client, @namespace, ct).ConfigureAwait(false)),
            ct).ConfigureAwait(false);
        if (Terminal(quota) is { } t4)
        {
            return t4;
        }

        var version = await ReadAsync(
            () => ReadServerVersionAsync(client, ct),
            ct).ConfigureAwait(false);
        if (Terminal(version) is { } t5)
        {
            return t5;
        }

        return new KubernetesClusterInspection
        {
            ApiReachable = true,
            Nodes = nodes.Value ?? [],
            NodesAvailable = nodes.Available,
            NamespacePsaLabels = psa.Value ?? new Dictionary<string, string>(),
            NamespacePsaAvailable = psa.Available,
            StorageClasses = storageClasses.Value ?? [],
            StorageClassesAvailable = storageClasses.Available,
            NamespaceHasResourceQuotaRequiringRequests = quota.Available && (quota.Value ?? false),
            ResourceQuotaAvailable = quota.Available,
            ServerVersion = version.Available ? version.Value : null,
        };
    }

    private static KubernetesClusterInspection? Terminal<T>(in ReadOutcome<T> outcome) => outcome.Failure switch
    {
        KubernetesApiFailure.AuthRejected => KubernetesClusterInspection.AuthRejected(outcome.Error!),
        KubernetesApiFailure.Unreachable or KubernetesApiFailure.TlsCaFailure =>
            KubernetesClusterInspection.Unreachable(outcome.Error!, outcome.Failure),
        _ => null,
    };

    private static async Task<ReadOutcome<T>> ReadAsync<T>(Func<Task<T>> read, CancellationToken ct)
    {
        try
        {
            var value = await read().ConfigureAwait(false);
            return ReadOutcome<T>.Ok(value);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (HttpOperationException ex) when (ex.Response is not null)
        {
            return ex.Response.StatusCode == HttpStatusCode.Unauthorized
                ? ReadOutcome<T>.AuthRejected(ex.Message)
                : ReadOutcome<T>.Forbidden();
        }
        catch (Exception ex)
        {
            return ReadOutcome<T>.Transport(ex.Message, ClassifyTransportFailure(ex));
        }
    }

    private static KubernetesApiFailure ClassifyTransportFailure(Exception ex)
    {
        for (var e = ex; e is not null; e = e.InnerException)
        {
            if (e is AuthenticationException)
            {
                return KubernetesApiFailure.TlsCaFailure;
            }
        }

        return KubernetesApiFailure.Unreachable;
    }

    private readonly struct ReadOutcome<T>
    {
        public bool Available { get; private init; }
        public T? Value { get; private init; }

        public string? Error { get; private init; }

        public KubernetesApiFailure Failure { get; private init; }

        public static ReadOutcome<T> Ok(T value) => new() { Available = true, Value = value };

        public static ReadOutcome<T> Forbidden() => new() { Available = false };

        public static ReadOutcome<T> AuthRejected(string error) =>
            new() { Available = false, Error = error, Failure = KubernetesApiFailure.AuthRejected };

        public static ReadOutcome<T> Transport(string error, KubernetesApiFailure failure) =>
            new() { Available = false, Error = error, Failure = failure };
    }

    internal virtual Task<V1NodeList> ReadNodesRawAsync(IKubernetes client, CancellationToken ct)
        => client.CoreV1.ListNodeAsync(cancellationToken: ct);

    internal virtual Task<V1Namespace> ReadNamespaceRawAsync(IKubernetes client, string @namespace, CancellationToken ct)
        => client.CoreV1.ReadNamespaceAsync(@namespace, cancellationToken: ct);

    internal virtual Task<V1StorageClassList> ReadStorageClassesRawAsync(IKubernetes client, CancellationToken ct)
        => client.StorageV1.ListStorageClassAsync(cancellationToken: ct);

    internal virtual Task<V1ResourceQuotaList> ReadResourceQuotasRawAsync(IKubernetes client, string @namespace, CancellationToken ct)
        => client.CoreV1.ListNamespacedResourceQuotaAsync(@namespace, cancellationToken: ct);

    internal virtual Task<VersionInfo> ReadServerVersionRawAsync(IKubernetes client, CancellationToken ct)
        => client.Version.GetCodeAsync(ct);

    private async Task<Version?> ReadServerVersionAsync(IKubernetes client, CancellationToken ct)
    {
        try
        {
            return ParseServerVersion(await ReadServerVersionRawAsync(client, ct).ConfigureAwait(false));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpOperationException or HttpRequestException or IOException
                                   or TimeoutException or AuthenticationException or System.Net.Sockets.SocketException)
        {
            throw;
        }
        catch
        {
            return null;
        }
    }

    internal static Version? ParseServerVersion(VersionInfo? info)
    {
        if (info is null) return null;
        if (LeadingNumber(info.Major) is { } major && LeadingNumber(info.Minor) is { } minor)
        {
            return new Version(major, minor);
        }

        var parts = (info.GitVersion ?? string.Empty).TrimStart('v').Split('.');
        return parts.Length >= 2 && LeadingNumber(parts[0]) is { } gitMajor && LeadingNumber(parts[1]) is { } gitMinor
            ? new Version(gitMajor, gitMinor)
            : null;
    }

    private static int? LeadingNumber(string? value)
    {
        if (string.IsNullOrEmpty(value)) return null;
        var end = 0;
        while (end < value.Length && char.IsAsciiDigit(value[end])) end++;
        return end > 0 && int.TryParse(value.AsSpan(0, end), NumberStyles.None, CultureInfo.InvariantCulture, out var number)
            ? number : null;
    }

    internal static bool? DefaultHandlerUserNamespaces(V1Node node)
    {
        foreach (var handler in node.Status?.RuntimeHandlers ?? [])
        {
            if (string.IsNullOrEmpty(handler.Name)) return handler.Features?.UserNamespaces;
        }
        return null;
    }

    private static IReadOnlyList<ClusterNodeInfo> MapNodes(V1NodeList nodeList)
    {
        var items = nodeList?.Items;
        if (items is null || items.Count == 0)
        {
            return [];
        }

        var result = new List<ClusterNodeInfo>(items.Count);
        foreach (var node in items)
        {
            var labels = node.Metadata?.Labels is { } l
                ? new Dictionary<string, string>(l)
                : new Dictionary<string, string>();

            var arch = labels.TryGetValue(ArchLabel, out var archLabel) && !string.IsNullOrEmpty(archLabel)
                ? archLabel
                : node.Status?.NodeInfo?.Architecture;

            result.Add(new ClusterNodeInfo
            {
                Name = node.Metadata?.Name ?? string.Empty,
                Labels = labels,
                Arch = string.IsNullOrEmpty(arch) ? null : arch,
                Schedulable = IsNodeSchedulable(node),
                UserNamespacesSupported = DefaultHandlerUserNamespaces(node),
            });
        }

        return result;
    }

    private static bool IsNodeSchedulable(V1Node node)
    {
        if (node.Spec?.Unschedulable == true)
        {
            return false;
        }

        var taints = node.Spec?.Taints;
        if (taints is { Count: > 0 })
        {
            foreach (var taint in taints)
            {
                if (string.Equals(taint.Effect, "NoSchedule", StringComparison.Ordinal) ||
                    string.Equals(taint.Effect, "NoExecute", StringComparison.Ordinal))
                {
                    return false;
                }
            }
        }

        return true;
    }

    private static IReadOnlyDictionary<string, string> MapPsaLabels(V1Namespace ns)
    {
        var labels = ns?.Metadata?.Labels;
        if (labels is null || labels.Count == 0)
        {
            return new Dictionary<string, string>();
        }

        var psa = new Dictionary<string, string>();
        foreach (var (key, value) in labels)
        {
            if (key.StartsWith(PsaLabelPrefix, StringComparison.Ordinal))
            {
                psa[key] = value;
            }
        }

        return psa;
    }

    private static IReadOnlyList<ClusterStorageClassInfo> MapStorageClasses(V1StorageClassList storageClassList)
    {
        var items = storageClassList?.Items;
        if (items is null || items.Count == 0)
        {
            return [];
        }

        return items
            .Select(sc => new ClusterStorageClassInfo
            {
                Name = sc.Metadata?.Name ?? string.Empty,
                Provisioner = sc.Provisioner,
                VolumeBindingMode = sc.VolumeBindingMode,
                AllowVolumeExpansion = sc.AllowVolumeExpansion ?? false,
                IsDefault = sc.Metadata?.Annotations is { } ann
                    && ann.TryGetValue("storageclass.kubernetes.io/is-default-class", out var def)
                    && string.Equals(def, "true", StringComparison.OrdinalIgnoreCase),
            })
            .ToList();
    }

    private static bool AnyQuotaRequiresRequests(V1ResourceQuotaList quotaList)
    {
        var items = quotaList?.Items;
        if (items is null || items.Count == 0)
        {
            return false;
        }

        foreach (var quota in items)
        {
            var hard = quota.Spec?.Hard;
            if (hard is null)
            {
                continue;
            }

            if (hard.ContainsKey("requests.cpu") || hard.ContainsKey("requests.memory") ||
                hard.ContainsKey("limits.cpu") || hard.ContainsKey("limits.memory") ||
                hard.ContainsKey("cpu") || hard.ContainsKey("memory"))
            {
                return true;
            }
        }

        return false;
    }
}
