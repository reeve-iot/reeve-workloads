using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using k8s;
using k8s.Autorest;
using k8s.Models;
using Microsoft.Extensions.Logging;
using Reeve.Workloads.Abstractions;

namespace Reeve.Workloads.Providers.Kubernetes;

internal sealed class KubernetesObjectClient : IKubernetesObjectClient
{
    private static readonly Regex ConflictManagerPattern = new(
        "conflicts? with \"(?<mgr>[^\"]+)\"",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly ILogger<KubernetesObjectClient> _logger;

    public KubernetesObjectClient(ILogger<KubernetesObjectClient> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<KubernetesApplyOutcome> ServerSideApplyAsync(
        IKubernetes client,
        string @namespace,
        KubernetesObjectKind kind,
        string name,
        string manifestJson,
        string fieldManager,
        bool force,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(client);

        var patch = new V1Patch(manifestJson, V1Patch.PatchType.ApplyPatch);

        try
        {
            switch (kind)
            {
                case KubernetesObjectKind.Deployment:
                    await client.AppsV1.PatchNamespacedDeploymentAsync(
                        patch, name, @namespace, fieldManager: fieldManager, force: force, cancellationToken: ct)
                        .ConfigureAwait(false);
                    break;
                case KubernetesObjectKind.DaemonSet:
                    await client.AppsV1.PatchNamespacedDaemonSetAsync(
                        patch, name, @namespace, fieldManager: fieldManager, force: force, cancellationToken: ct)
                        .ConfigureAwait(false);
                    break;
                case KubernetesObjectKind.Service:
                    await client.CoreV1.PatchNamespacedServiceAsync(
                        patch, name, @namespace, fieldManager: fieldManager, force: force, cancellationToken: ct)
                        .ConfigureAwait(false);
                    break;
                case KubernetesObjectKind.Secret:
                    await client.CoreV1.PatchNamespacedSecretAsync(
                        patch, name, @namespace, fieldManager: fieldManager, force: force, cancellationToken: ct)
                        .ConfigureAwait(false);
                    break;
                case KubernetesObjectKind.Job:
                    await client.BatchV1.PatchNamespacedJobAsync(
                        patch, name, @namespace, fieldManager: fieldManager, force: force, cancellationToken: ct)
                        .ConfigureAwait(false);
                    break;
                default:
                    return KubernetesApplyOutcome.ErrorOutcome($"unknown object kind {kind}");
            }

            return KubernetesApplyOutcome.Ok();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (HttpOperationException ex) when (ex.Response?.StatusCode == HttpStatusCode.Conflict)
        {
            var manager = ParseConflictingFieldManager(ex.Response?.Content);
            _logger.LogWarning(
                "SSA 409 conflict applying {Kind} {Name} in {Namespace}: owned by field manager {Manager}.",
                kind, name, @namespace, manager ?? "unknown");
            return KubernetesApplyOutcome.Conflict(manager);
        }
        catch (HttpOperationException ex)
        {
            var message = ex.Response?.Content is { Length: > 0 } body ? Summarize(body) : ex.Message;
            var status = ex.Response?.StatusCode;

            if (IsPermanentRejection(status))
            {
                _logger.LogWarning(ex,
                    "SSA of {Kind} {Name} in {Namespace} was REJECTED ({Status}) — the spec cannot apply as written: {Message}",
                    kind, name, @namespace, (int)status!.Value, message);
                return KubernetesApplyOutcome.RejectedOutcome(message);
            }

            _logger.LogWarning(ex, "SSA of {Kind} {Name} in {Namespace} failed: {Message}", kind, name, @namespace, message);
            return KubernetesApplyOutcome.ErrorOutcome(message);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "SSA of {Kind} {Name} in {Namespace} could not reach the API server.", kind, name, @namespace);
            return KubernetesApplyOutcome.UnreachableOutcome(ex.Message);
        }
    }

    public async Task<IReadOnlyDictionary<string, string>> ReadSecretValuesAsync(
        IKubernetes client, string @namespace, string secretName, IReadOnlyCollection<string> keys, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(keys);

        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            var secret = await client.CoreV1.ReadNamespacedSecretAsync(secretName, @namespace, cancellationToken: ct)
                .ConfigureAwait(false);

            foreach (var key in keys)
            {
                if (secret.Data is { } data && data.TryGetValue(key, out var bytes) && bytes is not null)
                {
                    values[key] = System.Text.Encoding.UTF8.GetString(bytes);
                }
            }

            return values;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (HttpOperationException ex) when (ex.Response?.StatusCode == HttpStatusCode.NotFound)
        {
            return values;
        }
    }

    public async Task EnsurePvcAsync(IKubernetes client, string @namespace, V1PersistentVolumeClaim pvc, bool isAdoptedClaim, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(pvc);

        var name = pvc.Metadata?.Name
            ?? throw new ArgumentException("PVC must have a name.", nameof(pvc));

        V1PersistentVolumeClaim? existing = null;
        try
        {
            existing = await client.CoreV1.ReadNamespacedPersistentVolumeClaimAsync(name, @namespace, cancellationToken: ct)
                .ConfigureAwait(false);
        }
        catch (HttpOperationException ex) when (ex.Response?.StatusCode == HttpStatusCode.NotFound)
        {
            existing = null;
        }

        if (existing is null)
        {
            if (isAdoptedClaim)
            {
                _logger.LogWarning(
                    "Restore apply for adopted claim PVC {Pvc} in {Namespace} FAILED: the retained PVC does not exist (purged/never-existed) — refusing to create a FRESH EMPTY PVC while claiming a restore (fail-closed). The apply fails and the instance surfaces the reason.",
                    name, @namespace);
                throw new InvalidOperationException(
                    $"Restore binding requires the retained persistent volume claim '{name}' in '{@namespace}' to already exist, but it is absent " +
                    "(it may have been purged, or never existed). Refusing to create a fresh empty volume for a restore — uninstall and reinstall without restoring, or select a different retained volume.");
            }

            await client.CoreV1.CreateNamespacedPersistentVolumeClaimAsync(pvc, @namespace, cancellationToken: ct)
                .ConfigureAwait(false);
            return;
        }

        if (existing.Metadata?.DeletionTimestamp is not null)
        {
            throw new KubernetesPvcTerminatingException(name, @namespace);
        }

        if (!isAdoptedClaim && KubernetesPvcRecreatePolicy.Decide(pvc, existing) == KubernetesPvcAction.Recreate)
        {
            await DeleteUnboundPvcForRecreateAsync(client, @namespace, existing, pvc, ct).ConfigureAwait(false);
            throw new KubernetesPvcTerminatingException(name, @namespace);
        }

        var identityLabels = pvc.Metadata?.Labels;
        if (identityLabels is { Count: > 0 })
        {
            var currentLabels = existing.Metadata?.Labels;
            var needsRelabel = identityLabels.Any(kv =>
                currentLabels is null || !currentLabels.TryGetValue(kv.Key, out var cur) || !string.Equals(cur, kv.Value, StringComparison.Ordinal));
            if (needsRelabel)
            {
                var labelPatch = new V1Patch(
                    JsonSerializer.Serialize(new { metadata = new { labels = identityLabels } }),
                    V1Patch.PatchType.MergePatch);
                try
                {
                    await client.CoreV1.PatchNamespacedPersistentVolumeClaimAsync(labelPatch, name, @namespace, cancellationToken: ct)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex,
                        "Could not relabel adopted PVC {Pvc} in {Namespace} to the current instance — failing this apply so the reconciler RETRIES; the runtime stays down until the adopted PVC is relabelled (a label-based teardown must not miss it).",
                        name, @namespace);
                    throw new InvalidOperationException(
                        $"Failed to relabel adopted PVC '{name}' in '{@namespace}' to the current instance (retried next reconcile pass).",
                        ex);
                }
            }
        }

        var desired = RequestedStorageBytes(pvc);
        var current = RequestedStorageBytes(existing);
        if (desired is { } d && current is { } c && d > c)
        {
            var resizePatch = new V1Patch(
                JsonSerializer.Serialize(new
                {
                    spec = new
                    {
                        resources = new { requests = new { storage = DesiredStorageString(pvc) } },
                    },
                }),
                V1Patch.PatchType.MergePatch);

            try
            {
                await client.CoreV1.PatchNamespacedPersistentVolumeClaimAsync(resizePatch, name, @namespace, cancellationToken: ct)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (HttpOperationException ex) when (
                ex.Response?.StatusCode is HttpStatusCode.UnprocessableEntity
                    or HttpStatusCode.Forbidden
                    or HttpStatusCode.BadRequest)
            {
                _logger.LogInformation(
                    "PVC {Pvc} in {Namespace} needs a larger size but the API server rejected the resize ({Status}); the StorageClass likely does not allow expansion — leaving as-is.",
                    name, @namespace, ex.Response?.StatusCode);
            }
        }
    }

    private async Task DeleteUnboundPvcForRecreateAsync(
        IKubernetes client, string @namespace, V1PersistentVolumeClaim existing, V1PersistentVolumeClaim desired, CancellationToken ct)
    {
        var name = existing.Metadata!.Name;
        _logger.LogWarning(
            "PVC {Pvc} in {Namespace} is unbound with storage class {CurrentClass}, but the current class is {DesiredClass}. Kubernetes cannot change a claim's class, so the claim (which holds no data) is deleted and created again once it is gone.",
            name, @namespace, existing.Spec?.StorageClassName ?? "(none)", desired.Spec?.StorageClassName);

        var options = new V1DeleteOptions
        {
            Preconditions = new V1Preconditions
            {
                Uid = existing.Metadata.Uid,
                ResourceVersion = existing.Metadata.ResourceVersion,
            },
        };
        try
        {
            await client.CoreV1
                .DeleteNamespacedPersistentVolumeClaimAsync(name, @namespace, body: options, cancellationToken: ct)
                .ConfigureAwait(false);
        }
        catch (HttpOperationException ex) when (ex.Response?.StatusCode == HttpStatusCode.NotFound)
        {
        }
        catch (HttpOperationException ex) when (ex.Response?.StatusCode == HttpStatusCode.Conflict)
        {
            throw new InvalidOperationException(
                $"Persistent volume claim '{name}' in '{@namespace}' changed after it was read as unbound (it may have bound); it is kept and read again next pass.",
                ex);
        }
    }

    public async Task<KubernetesWorkloadRaw?> ReadWorkloadAsync(
        IKubernetes client, string @namespace, IReadOnlyDictionary<string, string> selectorLabels, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(selectorLabels);

        var labelSelector = BuildLabelSelector(selectorLabels);

        V1Deployment? deployment;
        V1DaemonSet? daemonSet;
        try
        {
            var deployments = await client.AppsV1.ListNamespacedDeploymentAsync(@namespace, labelSelector: labelSelector, cancellationToken: ct)
                .ConfigureAwait(false);
            var daemonSets = await client.AppsV1.ListNamespacedDaemonSetAsync(@namespace, labelSelector: labelSelector, cancellationToken: ct)
                .ConfigureAwait(false);

            deployment = deployments?.Items?.FirstOrDefault();
            daemonSet = daemonSets?.Items?.FirstOrDefault();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Reading workload object by label {Selector} in {Namespace} failed; reporting unreachable.",
                labelSelector, @namespace);
            return null;
        }

        IReadOnlyList<KubernetesJobRaw> jobs = Array.Empty<KubernetesJobRaw>();
        try
        {
            var jobList = await client.BatchV1.ListNamespacedJobAsync(@namespace, labelSelector: labelSelector, cancellationToken: ct)
                .ConfigureAwait(false);
            jobs = MapJobs(jobList);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Listing scan Jobs by label {Selector} in {Namespace} blipped; returning workload status with no Jobs.",
                labelSelector, @namespace);
        }

        if (deployment is null && daemonSet is null)
        {
            return jobs.Count == 0
                ? KubernetesWorkloadRaw.NotFound()
                : new KubernetesWorkloadRaw { Found = false, Jobs = jobs };
        }

        V1PodList? pods = null;
        try
        {
            pods = await client.CoreV1.ListNamespacedPodAsync(@namespace, labelSelector: labelSelector, cancellationToken: ct)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Reading pods for workload {Selector} in {Namespace} blipped; returning workload status best-effort.",
                labelSelector, @namespace);
        }

        var workloadName = deployment?.Metadata?.Name ?? daemonSet?.Metadata?.Name;

        var involvedNames = new List<string>();
        if (!string.IsNullOrEmpty(workloadName))
        {
            involvedNames.Add(workloadName);
        }

        foreach (var pod in pods?.Items ?? new List<V1Pod>())
        {
            if (pod.Metadata?.Name is { Length: > 0 } podName)
            {
                involvedNames.Add(podName);
            }
        }

        var eventItems = new List<Corev1Event>();
        foreach (var involvedName in involvedNames)
        {
            try
            {
                var slice = await client.CoreV1
                    .ListNamespacedEventAsync(@namespace, fieldSelector: $"involvedObject.name={involvedName}", cancellationToken: ct)
                    .ConfigureAwait(false);
                if (slice?.Items is { Count: > 0 } items)
                {
                    eventItems.AddRange(items);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Reading events for {Involved} in {Namespace} blipped; returning workload status without that slice.",
                    involvedName, @namespace);
            }
        }

        var events = eventItems.Count > 0 ? new Corev1EventList { Items = eventItems } : null;

        var mountedClaims = MountedClaimNames(deployment, daemonSet);
        var existingClaims = mountedClaims.Count == 0
            ? null
            : await ReadClaimNamesAsync(client, @namespace, ct).ConfigureAwait(false);

        return BuildRaw(deployment, daemonSet, pods, events, jobs, podsRead: pods is not null, existingClaims);
    }

    public async Task<IReadOnlyDictionary<Guid, KubernetesWorkloadRaw>> ReadWorkloadsAsync(
        IKubernetes client, string @namespace, IReadOnlyList<Guid> instanceIds, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(instanceIds);

        var wanted = new HashSet<Guid>(instanceIds);
        var raws = new Dictionary<Guid, KubernetesWorkloadRaw>(wanted.Count);
        if (wanted.Count == 0)
        {
            return raws;
        }

        var labelSelector = Reeve.Workloads.Abstractions.Domain.WorkloadResourceLabels.InstanceLabelKey;

        V1DeploymentList? deployments;
        V1DaemonSetList? daemonSets;
        try
        {
            deployments = await client.AppsV1.ListNamespacedDeploymentAsync(@namespace, labelSelector: labelSelector, cancellationToken: ct)
                .ConfigureAwait(false);
            daemonSets = await client.AppsV1.ListNamespacedDaemonSetAsync(@namespace, labelSelector: labelSelector, cancellationToken: ct)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Fleet read of workload objects in {Namespace} failed; reporting unreachable.", @namespace);
            return raws;
        }

        V1JobList? jobList = null;
        try
        {
            jobList = await client.BatchV1.ListNamespacedJobAsync(@namespace, labelSelector: labelSelector, cancellationToken: ct)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Fleet Job list in {Namespace} blipped; returning statuses with no Jobs.", @namespace);
        }

        V1PodList? podList = null;
        try
        {
            podList = await client.CoreV1.ListNamespacedPodAsync(@namespace, labelSelector: labelSelector, cancellationToken: ct)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Fleet pod list in {Namespace} blipped; returning statuses best-effort.", @namespace);
        }

        Corev1EventList? events = null;
        try
        {
            events = await client.CoreV1.ListNamespacedEventAsync(@namespace, cancellationToken: ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Fleet event list in {Namespace} blipped; returning statuses without events.", @namespace);
        }

        var deploymentsByInstance = IndexOne(deployments?.Items, d => d.Metadata?.Labels, wanted);
        var daemonSetsByInstance = IndexOne(daemonSets?.Items, d => d.Metadata?.Labels, wanted);

        var anyMountsClaim = deploymentsByInstance.Values.Any(d => MountedClaimNames(d, null).Count > 0)
            || daemonSetsByInstance.Values.Any(d => MountedClaimNames(null, d).Count > 0);
        var existingClaims = anyMountsClaim
            ? await ReadClaimNamesAsync(client, @namespace, ct).ConfigureAwait(false)
            : null;
        var jobsByInstance = IndexMany(jobList?.Items, j => j.Metadata?.Labels, wanted);
        var podsByInstance = IndexMany(podList?.Items, p => p.Metadata?.Labels, wanted);

        foreach (var instanceId in wanted)
        {
            deploymentsByInstance.TryGetValue(instanceId, out var deployment);
            daemonSetsByInstance.TryGetValue(instanceId, out var daemonSet);
            var jobs = jobsByInstance.TryGetValue(instanceId, out var jobItems)
                ? MapJobs(new V1JobList { Items = jobItems })
                : Array.Empty<KubernetesJobRaw>();

            if (deployment is null && daemonSet is null)
            {
                raws[instanceId] = jobs.Count == 0
                    ? KubernetesWorkloadRaw.NotFound()
                    : new KubernetesWorkloadRaw { Found = false, Jobs = jobs };
                continue;
            }

            var pods = podList is null
                ? null
                : new V1PodList { Items = podsByInstance.TryGetValue(instanceId, out var podItems) ? podItems : [] };

            raws[instanceId] = BuildRaw(deployment, daemonSet, pods, events, jobs, podsRead: podList is not null, existingClaims);
        }

        return raws;
    }

    private static Dictionary<Guid, T> IndexOne<T>(
        IList<T>? items, Func<T, IDictionary<string, string>?> labels, HashSet<Guid> wanted)
    {
        var indexed = new Dictionary<Guid, T>();
        foreach (var item in items ?? new List<T>())
        {
            if (ParseInstanceLabel(labels(item)) is { } id && wanted.Contains(id))
            {
                indexed.TryAdd(id, item);
            }
        }

        return indexed;
    }

    private static Dictionary<Guid, IList<T>> IndexMany<T>(
        IList<T>? items, Func<T, IDictionary<string, string>?> labels, HashSet<Guid> wanted)
    {
        var indexed = new Dictionary<Guid, IList<T>>();
        foreach (var item in items ?? new List<T>())
        {
            if (ParseInstanceLabel(labels(item)) is not { } id || !wanted.Contains(id))
            {
                continue;
            }

            if (!indexed.TryGetValue(id, out var group))
            {
                group = new List<T>();
                indexed[id] = group;
            }

            group.Add(item);
        }

        return indexed;
    }

    public async Task DeleteWorkloadByLabelAsync(
        IKubernetes client, string @namespace, IReadOnlyDictionary<string, string> selectorLabels, bool retainPvc, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(selectorLabels);

        var labelSelector = BuildLabelSelector(selectorLabels);

        var deployments = await client.AppsV1.ListNamespacedDeploymentAsync(@namespace, labelSelector: labelSelector, cancellationToken: ct).ConfigureAwait(false);
        foreach (var d in deployments?.Items ?? Enumerable.Empty<V1Deployment>())
        {
            await IgnoreNotFound(() => client.AppsV1.DeleteNamespacedDeploymentAsync(d.Metadata.Name, @namespace, cancellationToken: ct)).ConfigureAwait(false);
        }

        var daemonSets = await client.AppsV1.ListNamespacedDaemonSetAsync(@namespace, labelSelector: labelSelector, cancellationToken: ct).ConfigureAwait(false);
        foreach (var ds in daemonSets?.Items ?? Enumerable.Empty<V1DaemonSet>())
        {
            await IgnoreNotFound(() => client.AppsV1.DeleteNamespacedDaemonSetAsync(ds.Metadata.Name, @namespace, cancellationToken: ct)).ConfigureAwait(false);
        }

        var jobs = await client.BatchV1.ListNamespacedJobAsync(@namespace, labelSelector: labelSelector, cancellationToken: ct).ConfigureAwait(false);
        foreach (var job in jobs?.Items ?? Enumerable.Empty<V1Job>())
        {
            await IgnoreNotFound(() => client.BatchV1.DeleteNamespacedJobAsync(
                job.Metadata.Name, @namespace, propagationPolicy: "Background", cancellationToken: ct)).ConfigureAwait(false);
        }

        var services = await client.CoreV1.ListNamespacedServiceAsync(@namespace, labelSelector: labelSelector, cancellationToken: ct).ConfigureAwait(false);
        foreach (var s in services?.Items ?? Enumerable.Empty<V1Service>())
        {
            await IgnoreNotFound(() => client.CoreV1.DeleteNamespacedServiceAsync(s.Metadata.Name, @namespace, cancellationToken: ct)).ConfigureAwait(false);
        }

        var secrets = await client.CoreV1.ListNamespacedSecretAsync(@namespace, labelSelector: labelSelector, cancellationToken: ct).ConfigureAwait(false);
        foreach (var sec in secrets?.Items ?? Enumerable.Empty<V1Secret>())
        {
            await IgnoreNotFound(() => client.CoreV1.DeleteNamespacedSecretAsync(sec.Metadata.Name, @namespace, cancellationToken: ct)).ConfigureAwait(false);
        }

        if (!retainPvc)
        {
            var pvcs = await client.CoreV1.ListNamespacedPersistentVolumeClaimAsync(@namespace, labelSelector: labelSelector, cancellationToken: ct).ConfigureAwait(false);
            foreach (var pvc in pvcs?.Items ?? Enumerable.Empty<V1PersistentVolumeClaim>())
            {
                await IgnoreNotFound(() => client.CoreV1.DeleteNamespacedPersistentVolumeClaimAsync(pvc.Metadata.Name, @namespace, cancellationToken: ct)).ConfigureAwait(false);
            }
        }
    }

    public async Task DeleteOtherWorkloadKindsAsync(
        IKubernetes client, string @namespace, IReadOnlyDictionary<string, string> selectorLabels, KubernetesObjectKind keptKind, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(selectorLabels);

        var labelSelector = BuildLabelSelector(selectorLabels);

        if (keptKind != KubernetesObjectKind.Deployment)
        {
            var deployments = await client.AppsV1.ListNamespacedDeploymentAsync(@namespace, labelSelector: labelSelector, cancellationToken: ct).ConfigureAwait(false);
            foreach (var d in deployments?.Items ?? Enumerable.Empty<V1Deployment>())
            {
                await IgnoreNotFound(() => client.AppsV1.DeleteNamespacedDeploymentAsync(d.Metadata.Name, @namespace, cancellationToken: ct)).ConfigureAwait(false);
            }
        }

        if (keptKind != KubernetesObjectKind.DaemonSet)
        {
            var daemonSets = await client.AppsV1.ListNamespacedDaemonSetAsync(@namespace, labelSelector: labelSelector, cancellationToken: ct).ConfigureAwait(false);
            foreach (var ds in daemonSets?.Items ?? Enumerable.Empty<V1DaemonSet>())
            {
                await IgnoreNotFound(() => client.AppsV1.DeleteNamespacedDaemonSetAsync(ds.Metadata.Name, @namespace, cancellationToken: ct)).ConfigureAwait(false);
            }
        }

        if (keptKind != KubernetesObjectKind.Job)
        {
            var jobs = await client.BatchV1.ListNamespacedJobAsync(@namespace, labelSelector: labelSelector, cancellationToken: ct).ConfigureAwait(false);
            foreach (var job in jobs?.Items ?? Enumerable.Empty<V1Job>())
            {
                await IgnoreNotFound(() => client.BatchV1.DeleteNamespacedJobAsync(
                    job.Metadata.Name, @namespace, propagationPolicy: "Background", cancellationToken: ct)).ConfigureAwait(false);
            }
        }
    }

    public async Task DeleteScanJobsByLabelAsync(
        IKubernetes client, string @namespace, IReadOnlyDictionary<string, string> selectorLabels, string? exceptRunId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(selectorLabels);

        var labelSelector = BuildLabelSelector(selectorLabels);
        if (!string.IsNullOrEmpty(exceptRunId))
        {
            labelSelector = $"{labelSelector},{Reeve.Workloads.Abstractions.Domain.WorkloadResourceLabels.ScanRunLabelKey} notin ({exceptRunId})";
        }

        var jobs = await client.BatchV1.ListNamespacedJobAsync(@namespace, labelSelector: labelSelector, cancellationToken: ct).ConfigureAwait(false);
        foreach (var job in jobs?.Items ?? Enumerable.Empty<V1Job>())
        {
            await IgnoreNotFound(() => client.BatchV1.DeleteNamespacedJobAsync(
                job.Metadata.Name, @namespace, propagationPolicy: "Background", cancellationToken: ct)).ConfigureAwait(false);
        }
    }

    public async Task ScaleDeploymentAsync(
        IKubernetes client, string @namespace, IReadOnlyDictionary<string, string> selectorLabels, int replicas, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(client);

        var labelSelector = BuildLabelSelector(selectorLabels);
        var deployments = await client.AppsV1.ListNamespacedDeploymentAsync(@namespace, labelSelector: labelSelector, cancellationToken: ct).ConfigureAwait(false);
        var deployment = deployments?.Items?.FirstOrDefault();
        if (deployment is null)
        {
            return;
        }

        if (deployment.Spec?.Replicas == replicas)
        {
            return;
        }

        var scalePatch = new V1Patch(
            JsonSerializer.Serialize(new { spec = new { replicas } }),
            V1Patch.PatchType.MergePatch);

        await IgnoreNotFound(() => client.AppsV1.PatchNamespacedDeploymentAsync(
            scalePatch, deployment.Metadata.Name, @namespace, cancellationToken: ct)).ConfigureAwait(false);
    }

    public async Task SetDaemonSetSuspendedAsync(
        IKubernetes client, string @namespace, IReadOnlyDictionary<string, string> selectorLabels, bool suspended, string inst8, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(client);

        var labelSelector = BuildLabelSelector(selectorLabels);
        var daemonSets = await client.AppsV1.ListNamespacedDaemonSetAsync(@namespace, labelSelector: labelSelector, cancellationToken: ct).ConfigureAwait(false);
        var daemonSet = daemonSets?.Items?.FirstOrDefault();
        if (daemonSet is null)
        {
            return;
        }

        var key = Reeve.Workloads.Domain.WorkloadResourceNaming.SuspendSelectorKey(inst8);

        object patchBody = suspended
            ? new { spec = new { template = new { spec = new { nodeSelector = new Dictionary<string, string?> { [key] = "true" } } } } }
            : new { spec = new { template = new { spec = new { nodeSelector = new Dictionary<string, string?> { [key] = null } } } } };

        var patch = new V1Patch(JsonSerializer.Serialize(patchBody), V1Patch.PatchType.MergePatch);
        await IgnoreNotFound(() => client.AppsV1.PatchNamespacedDaemonSetAsync(
            patch, daemonSet.Metadata.Name, @namespace, cancellationToken: ct)).ConfigureAwait(false);
    }

    public async Task SetScanJobsSuspendedAsync(
        IKubernetes client, string @namespace, IReadOnlyDictionary<string, string> selectorLabels, bool suspended, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(selectorLabels);

        var labelSelector = BuildLabelSelector(selectorLabels);
        var jobs = await client.BatchV1.ListNamespacedJobAsync(@namespace, labelSelector: labelSelector, cancellationToken: ct).ConfigureAwait(false);

        var patch = new V1Patch(
            JsonSerializer.Serialize(new { spec = new { suspend = suspended } }),
            V1Patch.PatchType.MergePatch);

        foreach (var job in jobs?.Items ?? Enumerable.Empty<V1Job>())
        {
            if ((job.Status?.Succeeded ?? 0) >= 1 || (job.Status?.Failed ?? 0) >= 1)
            {
                continue;
            }

            if ((job.Spec?.Suspend ?? false) == suspended)
            {
                continue;
            }

            await IgnoreNotFound(() => client.BatchV1.PatchNamespacedJobAsync(
                patch, job.Metadata.Name, @namespace, cancellationToken: ct)).ConfigureAwait(false);
        }
    }

    public async Task<IReadOnlyList<AdoptableResource>> ListAdoptableAsync(
        IKubernetes client, string @namespace, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(client);

        var labelSelector = $"{Reeve.Workloads.Abstractions.Domain.WorkloadResourceLabels.InstanceLabelKey}";

        try
        {
            var deployments = await client.AppsV1.ListNamespacedDeploymentAsync(@namespace, labelSelector: labelSelector, cancellationToken: ct).ConfigureAwait(false);
            var daemonSets = await client.AppsV1.ListNamespacedDaemonSetAsync(@namespace, labelSelector: labelSelector, cancellationToken: ct).ConfigureAwait(false);
            var services = await client.CoreV1.ListNamespacedServiceAsync(@namespace, labelSelector: labelSelector, cancellationToken: ct).ConfigureAwait(false);
            var pvcs = await client.CoreV1.ListNamespacedPersistentVolumeClaimAsync(@namespace, labelSelector: labelSelector, cancellationToken: ct).ConfigureAwait(false);

            var results = new List<AdoptableResource>();

            foreach (var d in deployments?.Items ?? Enumerable.Empty<V1Deployment>())
            {
                results.Add(Project(
                    AdoptableResourceKind.Deployment, d.Metadata,
                    FirstContainerImage(d.Spec?.Template?.Spec?.Containers)));
            }

            foreach (var ds in daemonSets?.Items ?? Enumerable.Empty<V1DaemonSet>())
            {
                results.Add(Project(
                    AdoptableResourceKind.DaemonSet, ds.Metadata,
                    FirstContainerImage(ds.Spec?.Template?.Spec?.Containers)));
            }

            foreach (var s in services?.Items ?? Enumerable.Empty<V1Service>())
            {
                results.Add(Project(AdoptableResourceKind.Service, s.Metadata, image: null));
            }

            foreach (var pvc in pvcs?.Items ?? Enumerable.Empty<V1PersistentVolumeClaim>())
            {
                results.Add(Project(AdoptableResourceKind.PersistentVolumeClaim, pvc.Metadata, image: null));
            }

            return results;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Listing adoptable resources in {Namespace} failed; reporting none.", @namespace);
            return Array.Empty<AdoptableResource>();
        }
    }

    public async Task<KubernetesReadyNodePartition> ListReadyNodesAsync(
        IKubernetes client,
        IReadOnlyDictionary<string, string> nodeEligibilitySelector,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(client);

        var nodes = await client.CoreV1.ListNodeAsync(cancellationToken: ct).ConfigureAwait(false);

        var matching = new List<string>();
        var excluded = new List<string>();
        foreach (var node in nodes?.Items ?? Enumerable.Empty<V1Node>())
        {
            if (!IsReadyForPinnedScan(node))
            {
                continue;
            }

            var name = node.Metadata?.Name;
            if (string.IsNullOrEmpty(name))
            {
                continue;
            }

            (MatchesNodeEligibilitySelector(node, nodeEligibilitySelector) ? matching : excluded).Add(name!);
        }

        return new KubernetesReadyNodePartition { Matching = matching, Excluded = excluded };
    }

    private static bool MatchesNodeEligibilitySelector(V1Node node, IReadOnlyDictionary<string, string>? selector)
    {
        if (selector is null || selector.Count == 0)
        {
            return true;
        }

        var labels = node.Metadata?.Labels;
        if (labels is null)
        {
            return false;
        }

        foreach (var required in selector)
        {
            if (!labels.TryGetValue(required.Key, out var value) ||
                !string.Equals(value, required.Value, StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsReadyForPinnedScan(V1Node node)
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
                if (string.Equals(taint.Effect, "NoExecute", StringComparison.Ordinal))
                {
                    return false;
                }
            }
        }

        var conditions = node.Status?.Conditions;
        if (conditions is null)
        {
            return false;
        }

        foreach (var condition in conditions)
        {
            if (string.Equals(condition.Type, "Ready", StringComparison.Ordinal))
            {
                return string.Equals(condition.Status, "True", StringComparison.Ordinal);
            }
        }

        return false;
    }

    private const string NetworkAttachmentGroup = "k8s.cni.cncf.io";
    private const string NetworkAttachmentVersion = "v1";
    private const string NetworkAttachmentPlural = "network-attachment-definitions";

    public async Task<KubernetesNetworkAttachmentListResult> ListNetworkAttachmentDefinitionsAsync(
        IKubernetes client,
        string @namespace,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(client);

        object? raw;
        try
        {
            raw = await client.CustomObjects.ListNamespacedCustomObjectAsync(
                NetworkAttachmentGroup,
                NetworkAttachmentVersion,
                @namespace,
                NetworkAttachmentPlural,
                cancellationToken: ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (HttpOperationException ex) when (ex.Response?.StatusCode == HttpStatusCode.NotFound)
        {
            _logger.LogDebug(ex, "NetworkAttachmentDefinition CRD not found; reporting {Reason}.",
                KubernetesNetworkAttachmentUnavailableReasons.MultusNotInstalled);
            return KubernetesNetworkAttachmentListResult.Unavailable(
                KubernetesNetworkAttachmentUnavailableReasons.MultusNotInstalled);
        }
        catch (HttpOperationException ex) when (ex.Response?.StatusCode == HttpStatusCode.Forbidden)
        {
            _logger.LogDebug(ex, "NetworkAttachmentDefinition read in {Namespace} was rejected; reporting {Reason}.",
                @namespace, KubernetesNetworkAttachmentUnavailableReasons.Forbidden);
            return KubernetesNetworkAttachmentListResult.Unavailable(
                KubernetesNetworkAttachmentUnavailableReasons.Forbidden);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "NetworkAttachmentDefinition read in {Namespace} failed; reporting {Reason}.",
                @namespace, KubernetesNetworkAttachmentUnavailableReasons.ProviderUnavailable);
            return KubernetesNetworkAttachmentListResult.Unavailable(
                KubernetesNetworkAttachmentUnavailableReasons.ProviderUnavailable);
        }

        var items = ParseNetworkAttachments(raw);

        if (items is null)
        {
            _logger.LogDebug(
                "NetworkAttachmentDefinition listing in {Namespace} had no readable items; reporting {Reason}.",
                @namespace, KubernetesNetworkAttachmentUnavailableReasons.ProviderUnavailable);
            return KubernetesNetworkAttachmentListResult.Unavailable(
                KubernetesNetworkAttachmentUnavailableReasons.ProviderUnavailable);
        }

        return items.Count == 0
            ? KubernetesNetworkAttachmentListResult.Unavailable(
                KubernetesNetworkAttachmentUnavailableReasons.NoneInNamespace)
            : KubernetesNetworkAttachmentListResult.Populated(items);
    }

    private List<KubernetesNetworkAttachment>? ParseNetworkAttachments(object? raw)
    {
        if (!TryAsJsonElement(raw, out var root) ||
            root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("items", out var list) ||
            list.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var items = new List<KubernetesNetworkAttachment>();
        foreach (var item in list.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object ||
                !item.TryGetProperty("metadata", out var metadata) ||
                metadata.ValueKind != JsonValueKind.Object ||
                !metadata.TryGetProperty("name", out var nameElement) ||
                nameElement.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            var name = nameElement.GetString();
            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            items.Add(new KubernetesNetworkAttachment { Name = name });
        }

        return items;
    }

    private static bool TryAsJsonElement(object? raw, out JsonElement element)
    {
        element = default;
        switch (raw)
        {
            case null:
                return false;
            case JsonElement direct:
                element = direct;
                return true;
            case string json when !string.IsNullOrWhiteSpace(json):
                try
                {
                    using var document = JsonDocument.Parse(json);
                    element = document.RootElement.Clone();
                    return true;
                }
                catch (JsonException)
                {
                    return false;
                }

            default:
                try
                {
                    element = JsonSerializer.SerializeToElement(raw);
                    return true;
                }
                catch (NotSupportedException)
                {
                    return false;
                }
        }
    }

    public async Task PatchResourceMetadataAsync(
        IKubernetes client, string @namespace, KubernetesObjectKind kind, string name,
        IReadOnlyDictionary<string, string> labelsToSet, IReadOnlyList<string> labelsToRemove,
        IReadOnlyList<string> annotationsToRemove, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(labelsToSet);
        ArgumentNullException.ThrowIfNull(labelsToRemove);
        ArgumentNullException.ThrowIfNull(annotationsToRemove);

        var labels = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var kv in labelsToSet)
        {
            labels[kv.Key] = kv.Value;
        }

        foreach (var key in labelsToRemove)
        {
            labels[key] = null;
        }

        var annotations = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var key in annotationsToRemove)
        {
            annotations[key] = null;
        }

        object metadata = annotations.Count > 0
            ? new { labels, annotations }
            : new { labels };

        var patch = new V1Patch(JsonSerializer.Serialize(new { metadata }), V1Patch.PatchType.MergePatch);

        await IgnoreNotFound(() => PatchByKindAsync(client, @namespace, kind, name, patch, ct)).ConfigureAwait(false);
    }

    public async Task<string?> ReadObjectManifestAsync(
        IKubernetes client, string @namespace, KubernetesObjectKind kind, string name, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(client);

        try
        {
            object? live = kind switch
            {
                KubernetesObjectKind.Deployment =>
                    await client.AppsV1.ReadNamespacedDeploymentAsync(name, @namespace, cancellationToken: ct).ConfigureAwait(false),
                KubernetesObjectKind.DaemonSet =>
                    await client.AppsV1.ReadNamespacedDaemonSetAsync(name, @namespace, cancellationToken: ct).ConfigureAwait(false),
                KubernetesObjectKind.Service =>
                    await client.CoreV1.ReadNamespacedServiceAsync(name, @namespace, cancellationToken: ct).ConfigureAwait(false),
                KubernetesObjectKind.Secret =>
                    await client.CoreV1.ReadNamespacedSecretAsync(name, @namespace, cancellationToken: ct).ConfigureAwait(false),
                KubernetesObjectKind.PersistentVolumeClaim =>
                    await client.CoreV1.ReadNamespacedPersistentVolumeClaimAsync(name, @namespace, cancellationToken: ct).ConfigureAwait(false),
                _ => null,
            };

            return live is null ? null : SanitizeForApply(KubernetesJson.Serialize(live), kind);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (HttpOperationException ex) when (ex.Response?.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Reading {Kind} {Name} in {Namespace} for ownership handover failed; reporting no body.",
                kind, name, @namespace);
            return null;
        }
    }

    public async IAsyncEnumerable<WorkloadWatchEvent> WatchWorkloadsAsync(
        IKubernetes client,
        string @namespace,
        string labelSelector,
        [EnumeratorCancellation] CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(client);

        var deployments = await client.AppsV1
            .ListNamespacedDeploymentAsync(@namespace, labelSelector: labelSelector, cancellationToken: ct).ConfigureAwait(false);
        var daemonSets = await client.AppsV1
            .ListNamespacedDaemonSetAsync(@namespace, labelSelector: labelSelector, cancellationToken: ct).ConfigureAwait(false);
        var pods = await client.CoreV1
            .ListNamespacedPodAsync(@namespace, labelSelector: labelSelector, cancellationToken: ct).ConfigureAwait(false);

        yield return WorkloadWatchEvent.Synced();

        var channel = Channel.CreateUnbounded<WorkloadWatchEvent>(
            new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var token = linked.Token;

        void Fault(Exception ex)
        {
            var terminal = IsResourceVersionTooOld(ex)
                ? new WorkloadWatchResourceVersionTooOldException(
                    "Kubernetes watch resourceVersion expired (410 Gone) — a re-list is required.", ex)
                : ex;
            channel.Writer.TryComplete(terminal);
            linked.Cancel();
        }

        var deploymentWatch = client.AppsV1.WatchListNamespacedDeploymentAsync(
            @namespace, labelSelector: labelSelector, resourceVersion: deployments?.Metadata?.ResourceVersion,
            allowWatchBookmarks: true, onError: Fault, cancellationToken: token);
        var daemonSetWatch = client.AppsV1.WatchListNamespacedDaemonSetAsync(
            @namespace, labelSelector: labelSelector, resourceVersion: daemonSets?.Metadata?.ResourceVersion,
            allowWatchBookmarks: true, onError: Fault, cancellationToken: token);
        var podWatch = client.CoreV1.WatchListNamespacedPodAsync(
            @namespace, labelSelector: labelSelector, resourceVersion: pods?.Metadata?.ResourceVersion,
            allowWatchBookmarks: true, onError: Fault, cancellationToken: token);

        var pump = Task.Run(async () =>
        {
            try
            {
                await Task.WhenAll(
                    PumpAsync(deploymentWatch, channel, Fault, token),
                    PumpAsync(daemonSetWatch, channel, Fault, token),
                    PumpAsync(podWatch, channel, Fault, token)).ConfigureAwait(false);

                channel.Writer.TryComplete();
            }
            catch (Exception ex)
            {
                channel.Writer.TryComplete(ex);
            }
        }, CancellationToken.None);

        try
        {
            await foreach (var ev in channel.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                yield return ev;
            }
        }
        finally
        {
            linked.Cancel();
            try
            {
                await pump.ConfigureAwait(false);
            }
            catch
            {
            }
        }
    }

    private static async Task PumpAsync<T>(
        IAsyncEnumerable<(WatchEventType Type, T Item)> stream,
        Channel<WorkloadWatchEvent> channel,
        Action<Exception> fault,
        CancellationToken token)
        where T : class, IKubernetesObject<V1ObjectMeta>
    {
        try
        {
            await foreach (var (type, item) in stream.WithCancellation(token).ConfigureAwait(false))
            {
                if (type == WatchEventType.Bookmark)
                {
                    continue;
                }

                var instanceId = ParseInstanceLabel(item?.Metadata?.Labels);
                var resourceVersion = item?.Metadata?.ResourceVersion;
                await channel.Writer.WriteAsync(WorkloadWatchEvent.Changed(instanceId, resourceVersion), token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            fault(ex);
        }
    }

    private static Guid? ParseInstanceLabel(IDictionary<string, string>? labels)
    {
        if (labels is not null
            && labels.TryGetValue(Reeve.Workloads.Abstractions.Domain.WorkloadResourceLabels.InstanceLabelKey, out var value)
            && Guid.TryParse(value, out var id))
        {
            return id;
        }

        return null;
    }

    private static bool IsResourceVersionTooOld(Exception ex)
        => ex switch
        {
            WorkloadWatchResourceVersionTooOldException => true,
            KubernetesException ke when ke.Status is { } status
                && (status.Code == 410 || string.Equals(status.Reason, "Expired", StringComparison.OrdinalIgnoreCase)) => true,
            HttpOperationException hoe when hoe.Response?.StatusCode == HttpStatusCode.Gone => true,
            _ => ex.Message.Contains("too old resource version", StringComparison.OrdinalIgnoreCase),
        };

    private static string SanitizeForApply(string liveJson, KubernetesObjectKind kind)
    {
        using var doc = JsonDocument.Parse(liveJson);
        var root = doc.RootElement;

        var (apiVersion, kindName) = kind switch
        {
            KubernetesObjectKind.Deployment => ("apps/v1", "Deployment"),
            KubernetesObjectKind.DaemonSet => ("apps/v1", "DaemonSet"),
            KubernetesObjectKind.Service => ("v1", "Service"),
            KubernetesObjectKind.Secret => ("v1", "Secret"),
            KubernetesObjectKind.PersistentVolumeClaim => ("v1", "PersistentVolumeClaim"),
            _ => ("v1", kind.ToString()),
        };

        var buffer = new System.IO.MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("apiVersion", apiVersion);
            writer.WriteString("kind", kindName);

            foreach (var property in root.EnumerateObject())
            {
                if (property.NameEquals("apiVersion") || property.NameEquals("kind") || property.NameEquals("status"))
                {
                    continue;
                }

                if (property.NameEquals("metadata") && property.Value.ValueKind == JsonValueKind.Object)
                {
                    writer.WritePropertyName(property.Name);
                    WriteSanitizedMetadata(writer, property.Value);
                    continue;
                }

                property.WriteTo(writer);
            }

            writer.WriteEndObject();
        }

        return System.Text.Encoding.UTF8.GetString(buffer.ToArray());
    }

    private static void WriteSanitizedMetadata(Utf8JsonWriter writer, JsonElement metadata)
    {
        writer.WriteStartObject();
        foreach (var property in metadata.EnumerateObject())
        {
            if (property.NameEquals("managedFields")
                || property.NameEquals("resourceVersion")
                || property.NameEquals("uid")
                || property.NameEquals("generation")
                || property.NameEquals("creationTimestamp")
                || property.NameEquals("selfLink"))
            {
                continue;
            }

            property.WriteTo(writer);
        }

        writer.WriteEndObject();
    }

    private static async Task PatchByKindAsync(
        IKubernetes client, string @namespace, KubernetesObjectKind kind, string name, V1Patch patch, CancellationToken ct)
    {
        switch (kind)
        {
            case KubernetesObjectKind.Deployment:
                await client.AppsV1.PatchNamespacedDeploymentAsync(patch, name, @namespace, cancellationToken: ct).ConfigureAwait(false);
                break;
            case KubernetesObjectKind.DaemonSet:
                await client.AppsV1.PatchNamespacedDaemonSetAsync(patch, name, @namespace, cancellationToken: ct).ConfigureAwait(false);
                break;
            case KubernetesObjectKind.Service:
                await client.CoreV1.PatchNamespacedServiceAsync(patch, name, @namespace, cancellationToken: ct).ConfigureAwait(false);
                break;
            case KubernetesObjectKind.Secret:
                await client.CoreV1.PatchNamespacedSecretAsync(patch, name, @namespace, cancellationToken: ct).ConfigureAwait(false);
                break;
            case KubernetesObjectKind.PersistentVolumeClaim:
                await client.CoreV1.PatchNamespacedPersistentVolumeClaimAsync(patch, name, @namespace, cancellationToken: ct).ConfigureAwait(false);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(kind), kind, "unknown object kind");
        }
    }

    private static AdoptableResource Project(
        AdoptableResourceKind kind, V1ObjectMeta? metadata, string? image)
        => new()
        {
            Kind = kind,
            Name = metadata?.Name ?? string.Empty,
            Image = image,
            Labels = ToOrdinalDictionary(metadata?.Labels),
            Annotations = ToOrdinalDictionary(metadata?.Annotations),
        };

    private static string? FirstContainerImage(IList<V1Container>? containers)
        => containers is { Count: > 0 } ? containers[0].Image : null;

    private static IReadOnlyDictionary<string, string> ToOrdinalDictionary(IDictionary<string, string>? source)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (source is not null)
        {
            foreach (var kv in source)
            {
                if (kv.Value is not null)
                {
                    result[kv.Key] = kv.Value;
                }
            }
        }

        return result;
    }

    private static KubernetesWorkloadRaw BuildRaw(
        V1Deployment? deployment, V1DaemonSet? daemonSet, V1PodList? pods, Corev1EventList? events,
        IReadOnlyList<KubernetesJobRaw> jobs, bool podsRead, IReadOnlySet<string>? existingClaims)
    {
        IReadOnlyList<string> missingClaims = existingClaims is null
            ? []
            : MountedClaimNames(deployment, daemonSet).Where(c => !existingClaims.Contains(c)).ToList();

        var podItems = pods?.Items ?? new List<V1Pod>();
        var (restartCount, observedImage, allRunning, anyFailed) = SummarizePods(podItems);

        int? desired;
        int? ready;
        string kind;
        if (deployment is not null)
        {
            kind = "Deployment";
            desired = deployment.Spec?.Replicas;
            ready = deployment.Status?.ReadyReplicas;
        }
        else
        {
            kind = "DaemonSet";
            desired = daemonSet!.Status?.DesiredNumberScheduled;
            ready = daemonSet.Status?.NumberReady;
        }

        return new KubernetesWorkloadRaw
        {
            Found = true,
            Kind = kind,
            DesiredReplicas = desired,
            ReadyReplicas = ready,
            PodRestartCount = restartCount,
            ObservedImage = observedImage,
            Events = MapEvents(events, deployment, daemonSet, podItems),
            AllPodsRunning = allRunning,
            AnyPodFailed = anyFailed,
            HasPods = podItems.Count > 0,
            PodBlockReason = KubernetesPodBlockReason.Describe(podItems),
            PodsRead = podsRead,
            Jobs = jobs,
            MissingClaims = missingClaims,
        };
    }

    private static IReadOnlyList<string> MountedClaimNames(V1Deployment? deployment, V1DaemonSet? daemonSet)
    {
        var volumes = deployment?.Spec?.Template?.Spec?.Volumes ?? daemonSet?.Spec?.Template?.Spec?.Volumes;
        return volumes is null
            ? []
            : volumes
                .Select(v => v.PersistentVolumeClaim?.ClaimName)
                .OfType<string>()
                .Where(n => n.Length > 0)
                .Distinct(StringComparer.Ordinal)
                .ToList();
    }

    private async Task<IReadOnlySet<string>?> ReadClaimNamesAsync(IKubernetes client, string @namespace, CancellationToken ct)
    {
        try
        {
            var claims = await client.CoreV1.ListNamespacedPersistentVolumeClaimAsync(@namespace, cancellationToken: ct)
                .ConfigureAwait(false);
            if (claims?.Items is not { } items)
            {
                return null;
            }

            return items
                .Select(c => c.Metadata?.Name)
                .OfType<string>()
                .ToHashSet(StringComparer.Ordinal);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Listing claims in {Namespace} failed; reporting no missing volumes this pass.", @namespace);
            return null;
        }
    }

    private static IReadOnlyList<KubernetesJobRaw> MapJobs(V1JobList? jobList)
    {
        var items = jobList?.Items;
        if (items is null || items.Count == 0)
        {
            return Array.Empty<KubernetesJobRaw>();
        }

        var result = new List<KubernetesJobRaw>(items.Count);
        foreach (var job in items)
        {
            var status = job.Status;
            var pinned = job.Spec?.Template?.Spec?.NodeName;
            var nodeName = !string.IsNullOrWhiteSpace(pinned)
                ? pinned
                : job.Metadata?.Labels is { } labels
                    && labels.TryGetValue(Reeve.Workloads.Abstractions.Domain.WorkloadResourceLabels.ScanNodeLabelKey, out var labelled)
                    && !string.IsNullOrWhiteSpace(labelled)
                        ? labelled
                        : null;
            result.Add(new KubernetesJobRaw
            {
                Name = job.Metadata?.Name ?? string.Empty,
                NodeName = nodeName,
                Succeeded = status?.Succeeded ?? 0,
                Failed = status?.Failed ?? 0,
                Active = status?.Active ?? 0,
                Suspended = job.Spec?.Suspend ?? false,
                CompletionTime = status?.CompletionTime is { } completedAt
                    ? new DateTimeOffset(DateTime.SpecifyKind(completedAt, DateTimeKind.Utc), TimeSpan.Zero)
                    : null,
            });
        }

        return result;
    }

    private static (int RestartCount, string? ObservedImage, bool AllRunning, bool AnyFailed) SummarizePods(IList<V1Pod> pods)
    {
        if (pods.Count == 0)
        {
            return (0, null, false, false);
        }

        var maxRestarts = 0;
        string? image = null;
        var allRunning = true;
        var anyFailed = false;

        foreach (var pod in pods)
        {
            var phase = pod.Status?.Phase;
            if (!string.Equals(phase, "Running", StringComparison.Ordinal))
            {
                allRunning = false;
            }

            if (string.Equals(phase, "Failed", StringComparison.Ordinal))
            {
                anyFailed = true;
            }

            foreach (var cs in pod.Status?.ContainerStatuses ?? new List<V1ContainerStatus>())
            {
                if (cs.RestartCount > maxRestarts)
                {
                    maxRestarts = cs.RestartCount;
                }

                image ??= cs.Image;

                var waitingReason = cs.State?.Waiting?.Reason;
                if (string.Equals(waitingReason, "CrashLoopBackOff", StringComparison.Ordinal)
                    || string.Equals(waitingReason, "ImagePullBackOff", StringComparison.Ordinal)
                    || string.Equals(waitingReason, "ErrImagePull", StringComparison.Ordinal))
                {
                    anyFailed = true;
                }

                if (cs.State?.Terminated is { } term && term.ExitCode != 0)
                {
                    anyFailed = true;
                }
            }
        }

        return (maxRestarts, image, allRunning, anyFailed);
    }

    private static IReadOnlyList<(DateTimeOffset, string, string, bool)> MapEvents(
        Corev1EventList? events, V1Deployment? deployment, V1DaemonSet? daemonSet, IList<V1Pod> pods)
    {
        if (events?.Items is not { Count: > 0 } items)
        {
            return [];
        }

        var interestedNames = new HashSet<string>(StringComparer.Ordinal);
        if (deployment?.Metadata?.Name is { } dn) interestedNames.Add(dn);
        if (daemonSet?.Metadata?.Name is { } dsn) interestedNames.Add(dsn);
        foreach (var pod in pods)
        {
            if (pod.Metadata?.Name is { } pn) interestedNames.Add(pn);
        }

        var mapped = new List<(DateTimeOffset At, string Reason, string Message, bool Warning)>();
        foreach (var e in items)
        {
            var involvedName = e.InvolvedObject?.Name;
            if (involvedName is null || !interestedNames.Contains(involvedName))
            {
                continue;
            }

            var at = e.LastTimestamp ?? e.EventTime ?? e.FirstTimestamp ?? e.Metadata?.CreationTimestamp;
            mapped.Add((
                at.HasValue ? new DateTimeOffset(DateTime.SpecifyKind(at.Value, DateTimeKind.Utc)) : DateTimeOffset.MinValue,
                e.Reason ?? string.Empty,
                e.Message ?? string.Empty,
                string.Equals(e.Type, "Warning", StringComparison.Ordinal)));
        }

        return mapped
            .OrderBy(m => m.At)
            .TakeLast(KubernetesStatusMapper.RecentEventCap)
            .Select(m => (m.At, m.Reason, m.Message, m.Warning))
            .ToList();
    }

    private static long? RequestedStorageBytes(V1PersistentVolumeClaim pvc)
    {
        var requests = pvc.Spec?.Resources?.Requests;
        if (requests is not null && requests.TryGetValue("storage", out var q) && q is not null)
        {
            try
            {
                return q.ToInt64();
            }
            catch (Exception)
            {
                return null;
            }
        }

        return null;
    }

    private static string? DesiredStorageString(V1PersistentVolumeClaim pvc)
        => pvc.Spec?.Resources?.Requests is { } r && r.TryGetValue("storage", out var q) ? q.ToString() : null;

    private static string BuildLabelSelector(IReadOnlyDictionary<string, string> selectorLabels)
        => string.Join(
            ',',
            selectorLabels
                .OrderBy(kv => kv.Key, StringComparer.Ordinal)
                .Select(kv => $"{kv.Key}={kv.Value}"));

    private static async Task IgnoreNotFound(Func<Task> action)
    {
        try
        {
            await action().ConfigureAwait(false);
        }
        catch (HttpOperationException ex) when (ex.Response?.StatusCode == HttpStatusCode.NotFound)
        {
        }
    }

    private static string? ParseConflictingFieldManager(string? statusBody)
    {
        if (string.IsNullOrWhiteSpace(statusBody))
        {
            return null;
        }

        string message = statusBody;
        try
        {
            var status = KubernetesJson.Deserialize<V1Status>(statusBody);
            if (!string.IsNullOrEmpty(status?.Message))
            {
                message = status.Message;
            }
        }
        catch (Exception)
        {
        }

        var match = ConflictManagerPattern.Match(message);
        return match.Success ? match.Groups["mgr"].Value : null;
    }

    private static bool IsPermanentRejection(HttpStatusCode? status) =>
        status is { } code
        && (int)code is >= 400 and < 500
        && code is not (HttpStatusCode.RequestTimeout
            or HttpStatusCode.TooManyRequests
            or HttpStatusCode.Unauthorized
            or HttpStatusCode.NotFound);

    private static string Summarize(string body)
    {
        try
        {
            var status = KubernetesJson.Deserialize<V1Status>(body);
            if (!string.IsNullOrEmpty(status?.Message))
            {
                return status.Message;
            }
        }
        catch (Exception)
        {
        }

        return body.Length > 500 ? body[..500] : body;
    }
}
