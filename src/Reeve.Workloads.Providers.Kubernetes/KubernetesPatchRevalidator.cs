using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using k8s;
using k8s.Models;
using Reeve.Workloads.Abstractions.Domain.DesiredSpec;

namespace Reeve.Workloads.Providers.Kubernetes;

internal sealed class KubernetesPatchRevalidator
{
    private const string DropAll = "ALL";

    private const string SeccompRuntimeDefault = "RuntimeDefault";

    private const string SeccompLocalhost = "Localhost";

    private const string NetworksAnnotationKey = "k8s.v1.cni.cncf.io/networks";

    private const string AppArmorAnnotationPrefix = "container.apparmor.security.beta.kubernetes.io/";

    private static readonly HashSet<string> RenderedPodAnnotationKeys = new(StringComparer.Ordinal)
    {
        Reeve.Workloads.Abstractions.Domain.WorkloadResourceLabels.SpecHashLabelKey,
        KubernetesWorkloadRenderer.RestartGenerationAnnotationKey,
        KubernetesWorkloadRenderer.CredentialDigestAnnotationKey,
        NetworksAnnotationKey,
    };

    private const string HostNetworkDnsPolicy = "ClusterFirstWithHostNet";

    private const string DefaultDnsPolicy = "ClusterFirst";

    public IReadOnlyList<string> Validate(WorkloadDesiredSpec desired, RenderedKubernetesObjects patched)
    {
        ArgumentNullException.ThrowIfNull(desired);
        ArgumentNullException.ThrowIfNull(patched);

        var pod = PodSpec(patched);
        if (pod is null)
        {
            return Array.Empty<string>();
        }

        var violations = new List<string>();
        var priv = desired.Privileges;
        var hostPathSources = RenderedHostPathSources(priv);

        ValidateObjectIdentity(desired, patched, violations);
        ValidateObjectMetadata(patched, violations);
        ValidatePodLevel(desired, pod, violations);
        ValidateNetworkAttachment(desired, pod, patched, violations);
        ValidateHostPaths(hostPathSources, pod, violations);
        ValidateVolumeSources(desired, pod, violations);
        ValidateContainers(desired, priv, hostPathSources, pod, violations);
        ValidateSingleInstanceInvariant(patched, violations);

        return violations;
    }

    private static void ValidateObjectIdentity(
        WorkloadDesiredSpec desired, RenderedKubernetesObjects patched, List<string> violations)
    {
        var (kind, name, labels, selector, templateLabels) = patched.Deployment is { } d
            ? ("deployment", d.Metadata?.Name, d.Metadata?.Labels, d.Spec?.Selector, d.Spec?.Template?.Metadata?.Labels)
            : ("daemonSet", patched.DaemonSet?.Metadata?.Name, patched.DaemonSet?.Metadata?.Labels,
               patched.DaemonSet?.Spec?.Selector, patched.DaemonSet?.Spec?.Template?.Metadata?.Labels);

        if (!string.Equals(name, desired.ResourceName, StringComparison.Ordinal))
        {
            violations.Add($"patch renames the {kind} to '{name ?? "<absent>"}', but this workload's resource name is '{desired.ResourceName}' — the apply would land on a DIFFERENT object and overwrite whatever owns that name.");
        }

        ValidateRenderedLabels(kind, "metadata.labels", desired.Labels, labels, violations);
        ValidateRenderedLabels(kind, "spec.template.metadata.labels", desired.Labels, templateLabels, violations);

        var expectedSelector = KubernetesWorkloadRenderer.InstanceSelector(desired);
        if (!SameLabelMap(expectedSelector, selector?.MatchLabels))
        {
            violations.Add($"patch sets the {kind} selector to {{{Describe(selector?.MatchLabels)}}} rather than the rendered {{{Describe(expectedSelector)}}} — it would adopt pods this workload does not own.");
        }

        if (selector?.MatchExpressions is { Count: > 0 })
        {
            violations.Add($"patch adds matchExpressions to the {kind} selector — the renderer emits a single matchLabels equality, and an expression selector widens it to pods of other workloads.");
        }
    }

    private static void ValidateObjectMetadata(RenderedKubernetesObjects patched, List<string> violations)
    {
        var (kind, meta, template) = patched.Deployment is { } d
            ? ("deployment", d.Metadata, d.Spec?.Template?.Metadata)
            : ("daemonSet", patched.DaemonSet?.Metadata, patched.DaemonSet?.Spec?.Template?.Metadata);

        foreach (var key in meta?.Annotations?.Keys ?? Enumerable.Empty<string>())
        {
            if (!IsUserPrivateAnnotationKey(key))
            {
                violations.Add($"patch sets the {kind} annotation '{key}' — the renderer emits no annotation on the workload object, only an unprefixed (user-private) key may be added, and a prefixed one can steer an admission webhook or controller the typed declaration never named.");
            }
        }

        foreach (var (where, m) in new[] { ("metadata", meta), ("spec.template.metadata", template) })
        {
            if (m?.Finalizers is { Count: > 0 } finalizers)
            {
                violations.Add($"patch sets {kind} {where}.finalizers [{string.Join(", ", finalizers)}] — the renderer emits none, and a finalizer nothing in Reeve removes blocks the object's or its pods' deletion, so teardown never completes.");
            }

            if (m?.OwnerReferences is { Count: > 0 } owners)
            {
                violations.Add($"patch sets {kind} {where}.ownerReferences [{string.Join(", ", owners.Select(o => $"{o.Kind}/{o.Name}"))}] — the renderer emits none, and an owner reference hands the object's lifetime to another object's garbage collection.");
            }
        }

        foreach (var key in template?.Annotations?.Keys ?? Enumerable.Empty<string>())
        {
            if (!RenderedPodAnnotationKeys.Contains(key)
                && !IsUserPrivateAnnotationKey(key)
                && !key.StartsWith(PrometheusAnnotationPrefix, StringComparison.Ordinal)
                && !key.StartsWith(AppArmorAnnotationPrefix, StringComparison.Ordinal))
            {
                violations.Add($"patch sets the pod annotation '{key}' — besides the renderer's own spec-hash, restart, credential-digest and network-attachment annotations only a prometheus.io/ scrape key or an unprefixed (user-private) key may be added, and a prefixed one can steer a CNI plugin (for example Multus default-network) or an admission webhook that injects containers or mounts.");
            }
        }
    }

    private const string PrometheusAnnotationPrefix = "prometheus.io/";

    private static bool IsUserPrivateAnnotationKey(string key) => !key.Contains('/', StringComparison.Ordinal);

    private static void ValidateRenderedLabels(
        string kind, string where, IReadOnlyDictionary<string, string> declared,
        IDictionary<string, string>? patched, List<string> violations)
    {
        if (!SameLabelMap(declared, patched))
        {
            violations.Add($"patch alters {kind} {where} to {{{Describe(patched)}}} — the typed declaration renders {{{Describe(declared)}}}, and these labels are what identifies the object.");
        }
    }

    private static bool SameLabelMap(IEnumerable<KeyValuePair<string, string>> left, IEnumerable<KeyValuePair<string, string>>? right)
    {
        if (right is null)
        {
            return false;
        }

        var l = left.ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);
        var r = right.ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);
        return l.Count == r.Count
               && l.All(kv => r.TryGetValue(kv.Key, out var v) && string.Equals(v, kv.Value, StringComparison.Ordinal));
    }

    private static string Describe(IEnumerable<KeyValuePair<string, string>>? map)
        => map is null
            ? "<absent>"
            : string.Join(", ", map.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => $"{kv.Key}={kv.Value}"));

    private static void ValidatePodLevel(WorkloadDesiredSpec desired, V1PodSpec pod, List<string> violations)
    {
        if (pod.HostNetwork == true && desired.Network?.HostNetwork != true)
        {
            violations.Add("patch sets pod hostNetwork=true, which the typed spec did not declare (network.hostNetwork).");
        }

        if (pod.HostPID == true)
        {
            violations.Add("patch sets pod hostPID=true — the typed schema has no field for host PID namespace sharing, so the desired spec cannot declare it.");
        }

        if (pod.HostIPC == true)
        {
            violations.Add("patch sets pod hostIPC=true — the typed schema has no field for host IPC namespace sharing, so the desired spec cannot declare it.");
        }

        if (!string.IsNullOrEmpty(pod.ServiceAccountName))
        {
            violations.Add($"patch sets pod serviceAccountName='{pod.ServiceAccountName}' — the typed schema has no field for a cluster identity, so the desired spec cannot declare it (runs as a cluster identity).");
        }

        if (!string.IsNullOrEmpty(pod.ServiceAccount))
        {
            violations.Add($"patch sets pod serviceAccount='{pod.ServiceAccount}' (the deprecated alias of serviceAccountName, still honored by the API server) — the typed schema has no field for a cluster identity, so the desired spec cannot declare it.");
        }

        if (pod.AutomountServiceAccountToken == true)
        {
            violations.Add("patch sets pod automountServiceAccountToken=true — workloads run without a cluster identity, so the desired spec cannot declare mounting the service-account token.");
        }

        if (pod.ResourceClaims is { Count: > 0 } claims)
        {
            violations.Add($"patch sets pod resourceClaims [{string.Join(", ", claims.Select(c => c.Name))}] — a dynamic resource claim is allocated by a resource driver that injects devices into the containers, reaching hardware no devicePaths declaration named.");
        }

        if (pod.ShareProcessNamespace == true)
        {
            violations.Add("patch sets pod shareProcessNamespace=true — a co-container could then read the app container's process memory/env (including its credentials), so the desired spec cannot declare it.");
        }

        var resolvedRunAsRoot = desired.Privileges?.RunAsRoot == true;
        var psc = pod.SecurityContext;
        if (psc is not null)
        {
            if (psc.RunAsUser == 0 && !resolvedRunAsRoot)
            {
                violations.Add("patch sets pod securityContext.runAsUser=0 (root), which the containers inherit and the typed spec did not declare (privileges.runAsRoot).");
            }

            if (psc.RunAsNonRoot == false && !resolvedRunAsRoot)
            {
                violations.Add("patch sets pod securityContext.runAsNonRoot=false, which the containers inherit — the typed spec did not declare running as root (privileges.runAsRoot).");
            }

            var resolvedGroups = new HashSet<long>(
                desired.Privileges?.DeviceGroupIds.Select(g => (long)g) ?? Enumerable.Empty<long>());
            if (desired.Privileges?.FsGroup is { } declaredFsGroup)
            {
                resolvedGroups.Add(declaredFsGroup);
            }

            foreach (var group in psc.SupplementalGroups ?? Enumerable.Empty<long?>())
            {
                if (group is not { } gid || !resolvedGroups.Contains(gid))
                {
                    var shown = group is { } value ? value.ToString(CultureInfo.InvariantCulture) : "null";
                    violations.Add($"patch adds pod securityContext.supplementalGroups={shown}, which the typed spec did not declare — a supplementary group opens every device on the node owned by it.");
                }
            }

            if (string.Equals(psc.SeccompProfile?.Type, "Unconfined", StringComparison.Ordinal))
            {
                violations.Add("patch sets pod securityContext.seccompProfile.type=Unconfined, which the containers inherit — the confining baseline the renderer emits is not optional.");
            }

            if (psc.Sysctls is { Count: > 0 })
            {
                violations.Add("patch sets pod securityContext.sysctls — kernel parameters the typed schema has no field for, so the desired spec cannot declare them.");
            }

            if (psc.RunAsGroup == 0 && !resolvedRunAsRoot)
            {
                violations.Add("patch sets pod securityContext.runAsGroup=0 (root group), which the containers inherit and the typed spec did not declare (privileges.runAsRoot).");
            }

            if (psc.FsGroup is { } fsGroup && !resolvedGroups.Contains(fsGroup))
            {
                violations.Add($"patch sets pod securityContext.fsGroup={fsGroup.ToString(CultureInfo.InvariantCulture)}, which the typed spec did not declare — fsGroup re-owns the mounted volumes and joins the container to that group.");
            }

            ValidateConfinementProfiles("pod", psc.AppArmorProfile?.Type, psc.SeLinuxOptions is not null, violations);
        }

        ValidatePodPlacementAndRuntime(desired, pod, violations);
    }

    private static void ValidatePodPlacementAndRuntime(
        WorkloadDesiredSpec desired, V1PodSpec pod, List<string> violations)
    {
        if (!string.IsNullOrEmpty(pod.NodeName))
        {
            violations.Add($"patch sets pod nodeName='{pod.NodeName}' — it assigns the pod directly and bypasses the scheduler, so node taints (including the control-plane taint), the arch affinity and the declared nodeSelector are all skipped.");
        }

        if (pod.DnsConfig is not null)
        {
            violations.Add("patch sets pod dnsConfig — it adds nameservers and search domains to the pod resolver, rewriting name resolution the same way hostAliases would.");
        }

        if (!string.IsNullOrEmpty(pod.RuntimeClassName))
        {
            violations.Add($"patch sets pod runtimeClassName='{pod.RuntimeClassName}' — it selects the container runtime, so a sandboxed workload can be moved onto the unsandboxed one.");
        }

        if (pod.HostUsers == false && !desired.UserNamespace)
        {
            violations.Add("patch sets pod hostUsers=false — it opts the pod out of the host user namespace, which the typed schema never grants.");
        }
        else if (desired.UserNamespace && pod.HostUsers != false)
        {
            violations.Add("patch sets pod hostUsers back on — the operator chose a user namespace for this workload, and the patch would run it with the host's users.");
        }

        if (desired.UserNamespace && (pod.HostNetwork == true || pod.HostPID == true || pod.HostIPC == true))
        {
            violations.Add("patch combines a user namespace with hostNetwork, hostPID or hostIPC, which Kubernetes rejects.");
        }

        if (pod.Tolerations is { Count: > 0 })
        {
            violations.Add("patch adds pod tolerations — they place the workload on nodes tainted to exclude it, including control-plane nodes.");
        }

        if (pod.Affinity is not null && !SameAffinity(pod.Affinity, KubernetesWorkloadRenderer.RenderArchAffinity(desired.Platforms)))
        {
            violations.Add("patch alters pod affinity — node placement beyond the arch affinity the renderer derives from the declared platforms.");
        }

        foreach (var (key, value) in pod.NodeSelector ?? new Dictionary<string, string>())
        {
            if (!desired.NodeSelector.TryGetValue(key, out var declared)
                || !string.Equals(declared, value, StringComparison.Ordinal))
            {
                violations.Add($"patch sets pod nodeSelector '{key}={value}', which the typed spec did not declare.");
            }
        }

        if (pod.HostAliases is { Count: > 0 })
        {
            violations.Add("patch adds pod hostAliases — they rewrite name resolution inside the pod, so a declared endpoint can be pointed elsewhere.");
        }

        if (!string.IsNullOrEmpty(pod.PriorityClassName))
        {
            violations.Add($"patch sets pod priorityClassName='{pod.PriorityClassName}' — it can preempt other workloads, which the typed schema never grants.");
        }

        if (!string.IsNullOrEmpty(pod.SchedulerName)
            && !string.Equals(pod.SchedulerName, "default-scheduler", StringComparison.Ordinal))
        {
            violations.Add($"patch sets pod schedulerName='{pod.SchedulerName}' — placement then answers to something outside the cluster's default scheduler.");
        }
    }

    private static void ValidateNetworkAttachment(
        WorkloadDesiredSpec desired, V1PodSpec pod, RenderedKubernetesObjects patched, List<string> violations)
    {
        var expected = ExpectedNetworksAnnotation(desired, patched);

        string? actual = null;
        if (PodTemplateAnnotations(patched) is { } annotations
            && annotations.TryGetValue(NetworksAnnotationKey, out var patchedValue))
        {
            actual = patchedValue;
        }

        if (expected is null && actual is not null)
        {
            violations.Add($"patch adds the pod annotation '{NetworksAnnotationKey}'='{actual}', which the typed spec did not declare — a network attachment forged after rendering bypasses the declared attachments, and the network-attachment-exists check.");
        }
        else if (expected is not null && actual is null)
        {
            violations.Add($"patch removes the pod annotation '{NetworksAnnotationKey}' (the typed spec declares '{expected}') — the network attachment is a managed unit; stripping it detaches a workload from the network it declares.");
        }
        else if (expected is not null && actual is not null && !string.Equals(expected, actual, StringComparison.Ordinal))
        {
            violations.Add($"patch alters the pod annotation '{NetworksAnnotationKey}' from '{expected}' to '{actual}' — that attaches the workload to a different NetworkAttachmentDefinition than the one declared, with no network-attachment-exists check.");
        }

        ValidateDnsPolicy(desired, pod, violations);
        ValidateAppArmorAnnotations(patched, violations);
    }

    private static void ValidateAppArmorAnnotations(RenderedKubernetesObjects patched, List<string> violations)
    {
        foreach (var (key, value) in PodTemplateAnnotations(patched) ?? new Dictionary<string, string>())
        {
            if (key.StartsWith(AppArmorAnnotationPrefix, StringComparison.Ordinal))
            {
                violations.Add($"patch sets the pod annotation '{key}'='{value}' — the legacy AppArmor surface the kubelet still honors; the renderer emits none, so any value here overrides the node's default profile.");
            }
        }
    }

    private static void ValidateDnsPolicy(WorkloadDesiredSpec desired, V1PodSpec pod, List<string> violations)
    {
        var actual = pod.DnsPolicy;

        if (desired.Network?.HostNetwork == true)
        {
            if (!string.Equals(actual, HostNetworkDnsPolicy, StringComparison.Ordinal))
            {
                var shown = actual is null ? "absent" : $"='{actual}'";
                violations.Add($"patch leaves pod dnsPolicy {shown} — the typed spec declares network.hostNetwork, for which the renderer emits dnsPolicy='{HostNetworkDnsPolicy}'; anything else moves name resolution off the cluster resolver.");
            }

            return;
        }

        if (actual is not null && !string.Equals(actual, DefaultDnsPolicy, StringComparison.Ordinal))
        {
            violations.Add($"patch sets pod dnsPolicy='{actual}', which the typed spec did not declare — the renderer emits no dnsPolicy for a non-hostNetwork workload, so only the cluster default '{DefaultDnsPolicy}' is acceptable.");
        }
    }

    private static string? ExpectedNetworksAnnotation(WorkloadDesiredSpec desired, RenderedKubernetesObjects patched)
    {
        var targetNamespace =
            patched.Deployment?.Metadata?.NamespaceProperty
            ?? patched.DaemonSet?.Metadata?.NamespaceProperty
            ?? string.Empty;

        return KubernetesWorkloadRenderer.RenderNetworksAnnotationValue(desired, targetNamespace);
    }

    private static void ValidateHostPaths(
        IReadOnlyDictionary<string, List<KubernetesWorkloadRenderer.HostPathSource>> hostPathSources,
        V1PodSpec pod, List<string> violations)
    {
        foreach (var volume in pod.Volumes ?? Enumerable.Empty<V1Volume>())
        {
            var rawPath = volume.HostPath?.Path;
            if (string.IsNullOrEmpty(rawPath))
            {
                continue;
            }

            if (HasTraversalSegment(rawPath))
            {
                violations.Add($"patch mounts host path '{rawPath}' which contains a '..' traversal segment — path traversal is never allowed.");
                continue;
            }

            if (!hostPathSources.TryGetValue(NormalizeHostPath(rawPath), out var sources))
            {
                violations.Add($"patch mounts host path '{rawPath}' which is not in the resolved set (nodeLocalState/dbus/tun/devices/hostPaths).");
                continue;
            }

            var patchedType = string.IsNullOrEmpty(volume.HostPath!.Type) ? null : volume.HostPath.Type;
            if (!sources.Any(source => string.Equals(source.HostPathType, patchedType, StringComparison.Ordinal)))
            {
                var rendered = string.Join("', '", sources.Select(source => source.HostPathType ?? "<none>").Distinct(StringComparer.Ordinal));
                violations.Add($"patch sets hostPath type '{patchedType ?? "<none>"}' on host path '{rawPath}', but the typed declaration renders '{rendered}' — the type decides whether the kubelet creates the path and what kind of file it must be.");
            }
        }
    }

    private static bool HasTraversalSegment(string path)
        => path.Split('/').Any(segment => segment == "..");

    private static string NormalizeHostPath(string path)
    {
        if (path.Length > 1 && path.EndsWith('/'))
        {
            return path.TrimEnd('/') is { Length: > 0 } trimmed ? trimmed : "/";
        }

        return path;
    }

    private static void ValidateVolumeSources(WorkloadDesiredSpec desired, V1PodSpec pod, List<string> violations)
    {
        var ownClaims = new HashSet<string>(
            desired.Volumes.Select(v => v.ResourceName), StringComparer.Ordinal);

        foreach (var volume in pod.Volumes ?? Enumerable.Empty<V1Volume>())
        {
            if (UnpermittedSourceKind(volume) is { } kind)
            {
                violations.Add($"patch mounts volume '{volume.Name}' from a '{kind}' source, which the typed schema never grants (only hostPath, emptyDir, configMap, this workload's own secret or PVC, and projected are permitted).");
            }

            if (string.Equals(volume.EmptyDir?.Medium, "Memory", StringComparison.Ordinal))
            {
                violations.Add($"patch mounts volume '{volume.Name}' as an in-memory emptyDir, which the typed schema never grants.");
            }

            var claimName = volume.PersistentVolumeClaim?.ClaimName;
            if (!string.IsNullOrEmpty(claimName) && !ownClaims.Contains(claimName))
            {
                violations.Add($"patch mounts persistent volume claim '{claimName}' (volume '{volume.Name}'), which is not one this workload declared — that is another workload's data.");
            }

            var secretName = volume.Secret?.SecretName;
            if (!string.IsNullOrEmpty(secretName)
                && !string.Equals(secretName, desired.SecretName, StringComparison.Ordinal))
            {
                violations.Add($"patch mounts a secret volume referencing secret '{secretName}', which is not this workload's own secret ('{desired.SecretName}') — in a shared namespace that reads another workload's credentials.");
            }

            var configMapName = volume.ConfigMap?.Name;
            if (!string.IsNullOrEmpty(configMapName)
                && !desired.ConfigMapMounts.Any(m => string.Equals(m.ConfigMapName, configMapName, StringComparison.Ordinal)))
            {
                violations.Add($"patch mounts configMap '{configMapName}' (volume '{volume.Name}'), which this workload never declared — in a shared namespace that reads another workload's configuration.");
            }

            var projectedSources = volume.Projected?.Sources ?? Enumerable.Empty<V1VolumeProjection>();
            foreach (var source in projectedSources)
            {
                if (source.ServiceAccountToken is not null)
                {
                    violations.Add($"patch mounts a projected volume with a serviceAccountToken source (volume '{volume.Name}') — it injects a cluster identity, bypassing the automount gate, so the desired spec cannot declare it.");
                }

                var projectedSecret = source.Secret?.Name;
                if (!string.IsNullOrEmpty(projectedSecret)
                    && !string.Equals(projectedSecret, desired.SecretName, StringComparison.Ordinal))
                {
                    violations.Add($"patch mounts a projected volume with a secret source referencing '{projectedSecret}' (volume '{volume.Name}'), which is not this workload's own secret ('{desired.SecretName}') — it reads another workload's credentials.");
                }

                var projectedConfigMap = source.ConfigMap?.Name;
                if (!string.IsNullOrEmpty(projectedConfigMap)
                    && !desired.ConfigMapMounts.Any(m => string.Equals(m.ConfigMapName, projectedConfigMap, StringComparison.Ordinal)))
                {
                    violations.Add($"patch mounts a projected volume with a configMap source referencing '{projectedConfigMap}' (volume '{volume.Name}'), which this workload never declared.");
                }

                if (UnpermittedProjectionKind(source) is { } projectionKind)
                {
                    violations.Add($"patch mounts a projected volume with a '{projectionKind}' source (volume '{volume.Name}') — projected sources are an allow-list, and this kind was never reviewed.");
                }
            }
        }
    }

    private static bool SameAffinity(V1Affinity? left, V1Affinity? right)
        => (left is null && right is null)
           || (left is not null && right is not null
               && string.Equals(KubernetesJson.Serialize(left), KubernetesJson.Serialize(right), StringComparison.Ordinal));

    private static string? UnpermittedSourceKind(V1Volume volume)
    {
        if (volume.HostPath is not null || volume.EmptyDir is not null || volume.ConfigMap is not null
            || volume.Secret is not null || volume.PersistentVolumeClaim is not null || volume.Projected is not null
            || volume.DownwardAPI is not null)
        {
            return null;
        }

        if (volume.Csi is not null) return "csi";
        if (volume.FlexVolume is not null) return "flexVolume";
        if (volume.Nfs is not null) return "nfs";
        if (volume.Iscsi is not null) return "iscsi";
        if (volume.Cephfs is not null) return "cephfs";
        if (volume.Rbd is not null) return "rbd";
        if (volume.Glusterfs is not null) return "glusterfs";
        if (volume.Fc is not null) return "fc";
        if (volume.AzureDisk is not null) return "azureDisk";
        if (volume.AzureFile is not null) return "azureFile";
        if (volume.Cinder is not null) return "cinder";
        if (volume.Ephemeral is not null) return "ephemeral";

        return "unknown";
    }

    private static string? UnpermittedProjectionKind(V1VolumeProjection source)
    {
        if (source.ConfigMap is not null || source.Secret is not null
            || source.DownwardAPI is not null || source.ServiceAccountToken is not null)
        {
            return null;
        }

        if (source.ClusterTrustBundle is not null) return "clusterTrustBundle";
        if (source.PodCertificate is not null) return "podCertificate";

        return "unknown";
    }

    private static void ValidateContainers(
        WorkloadDesiredSpec desired, ResolvedPrivileges? priv,
        IReadOnlyDictionary<string, List<KubernetesWorkloadRenderer.HostPathSource>> hostPathSources,
        V1PodSpec pod, List<string> violations)
    {
        var resolvedCaps = new HashSet<string>(priv?.Capabilities ?? Enumerable.Empty<string>(), StringComparer.Ordinal);
        var declaredEnv = desired.Env
            .GroupBy(e => e.Name, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

        foreach (var c in EnumerateContainers(pod))
        {
            ValidateContainerPorts(c.Name, c.Ports, violations);
            ValidateContainerSecurityContext(priv, resolvedCaps, c.Name, c.SecurityContext, violations);
            ValidateContainerImageAndEntrypoint(desired, c, violations);
            ValidateContainerEnv(desired, declaredEnv, c, violations);
            ValidateContainerHooksAndProbes(c, violations);
            ValidateContainerVolumeMounts(pod, hostPathSources, c, violations);
            ValidateContainerResources(c, violations);
        }
    }

    private sealed record PatchedContainer(
        string Name,
        string? Image,
        IList<string>? Command,
        IList<string>? Args,
        IList<V1EnvVar>? Env,
        IList<V1EnvFromSource>? EnvFrom,
        V1Lifecycle? Lifecycle,
        V1Probe? LivenessProbe,
        V1Probe? ReadinessProbe,
        V1Probe? StartupProbe,
        V1SecurityContext? SecurityContext,
        IList<V1VolumeMount>? VolumeMounts,
        V1ResourceRequirements? Resources,
        IEnumerable<V1ContainerPort> Ports);

    private static IEnumerable<PatchedContainer> EnumerateContainers(V1PodSpec pod)
    {
        foreach (var c in pod.Containers ?? Enumerable.Empty<V1Container>())
        {
            yield return new PatchedContainer(
                c.Name, c.Image, c.Command, c.Args, c.Env, c.EnvFrom, c.Lifecycle,
                c.LivenessProbe, c.ReadinessProbe, c.StartupProbe, c.SecurityContext,
                c.VolumeMounts, c.Resources,
                c.Ports ?? Enumerable.Empty<V1ContainerPort>());
        }

        foreach (var c in pod.InitContainers ?? Enumerable.Empty<V1Container>())
        {
            yield return new PatchedContainer(
                c.Name, c.Image, c.Command, c.Args, c.Env, c.EnvFrom, c.Lifecycle,
                c.LivenessProbe, c.ReadinessProbe, c.StartupProbe, c.SecurityContext,
                c.VolumeMounts, c.Resources,
                c.Ports ?? Enumerable.Empty<V1ContainerPort>());
        }

        foreach (var c in pod.EphemeralContainers ?? Enumerable.Empty<V1EphemeralContainer>())
        {
            yield return new PatchedContainer(
                c.Name, c.Image, c.Command, c.Args, c.Env, c.EnvFrom, c.Lifecycle,
                c.LivenessProbe, c.ReadinessProbe, c.StartupProbe, c.SecurityContext,
                c.VolumeMounts, c.Resources,
                c.Ports ?? Enumerable.Empty<V1ContainerPort>());
        }
    }

    private static void ValidateContainerImageAndEntrypoint(
        WorkloadDesiredSpec desired, PatchedContainer container, List<string> violations)
    {
        if (!string.IsNullOrEmpty(container.Image)
            && !string.Equals(container.Image, desired.Image, StringComparison.Ordinal))
        {
            violations.Add($"patch sets container '{container.Name}' image to '{container.Image}', which is not the desired, digest-pinned image ('{desired.Image}').");
        }

        if (container.Command is { Count: > 0 })
        {
            violations.Add($"patch overrides the entrypoint (command) of container '{container.Name}' — the desired image would run something else.");
        }

        if (container.Args is { Count: > 0 })
        {
            violations.Add($"patch overrides the arguments (args) of container '{container.Name}' — the desired image would run something else.");
        }
    }

    private static void ValidateContainerEnv(
        WorkloadDesiredSpec desired,
        IReadOnlyDictionary<string, ResolvedEnvVar> declaredEnv,
        PatchedContainer container,
        List<string> violations)
    {
        foreach (var e in container.Env ?? Enumerable.Empty<V1EnvVar>())
        {
            if (declaredEnv.TryGetValue(e.Name, out var declared) && !MatchesDeclaration(desired, declared, e))
            {
                violations.Add($"patch redefines env '{e.Name}' on container '{container.Name}', which the typed spec owns.");
            }

            var secretRef = e.ValueFrom?.SecretKeyRef?.Name;
            if (!string.IsNullOrEmpty(secretRef)
                && !string.Equals(secretRef, desired.SecretName, StringComparison.Ordinal))
            {
                violations.Add($"patch reads env '{e.Name}' on container '{container.Name}' from secret '{secretRef}', which is not this workload's own secret ('{desired.SecretName}').");
            }

            var configMapRef = e.ValueFrom?.ConfigMapKeyRef?.Name;
            if (!string.IsNullOrEmpty(configMapRef)
                && !desired.ConfigMapMounts.Any(m => string.Equals(m.ConfigMapName, configMapRef, StringComparison.Ordinal)))
            {
                violations.Add($"patch reads env '{e.Name}' on container '{container.Name}' from configMap '{configMapRef}', which is not one this workload declares.");
            }
        }

        foreach (var from in container.EnvFrom ?? Enumerable.Empty<V1EnvFromSource>())
        {
            var name = from.SecretRef?.Name ?? from.ConfigMapRef?.Name;
            violations.Add($"patch bulk-imports env on container '{container.Name}' via envFrom ('{name ?? "unnamed"}') — the typed schema grants no wholesale Secret/ConfigMap import.");
        }
    }

    private static bool MatchesDeclaration(WorkloadDesiredSpec desired, ResolvedEnvVar declared, V1EnvVar actual)
        => declared.Kind switch
        {
            ResolvedEnvKind.SecretRef =>
                actual.Value is null
                && string.Equals(actual.ValueFrom?.SecretKeyRef?.Name, desired.SecretName, StringComparison.Ordinal)
                && string.Equals(actual.ValueFrom?.SecretKeyRef?.Key, declared.Name, StringComparison.Ordinal),
            ResolvedEnvKind.SpecHash =>
                actual.ValueFrom is null
                && string.Equals(actual.Value, KubernetesEnvValue.Escape(declared.Value ?? desired.SpecHash), StringComparison.Ordinal),
            _ =>
                actual.ValueFrom is null && string.Equals(actual.Value, KubernetesEnvValue.Escape(declared.Value), StringComparison.Ordinal),
        };

    private static void ValidateContainerHooksAndProbes(PatchedContainer container, List<string> violations)
    {
        foreach (var (position, handler) in new[]
                 {
                     ("postStart", container.Lifecycle?.PostStart),
                     ("preStop", container.Lifecycle?.PreStop),
                 })
        {
            if (HandlerKind(handler?.Exec, handler?.HttpGet, handler?.TcpSocket, handler?.Sleep is not null) is { } kind)
            {
                violations.Add($"patch adds a {position} lifecycle hook of kind '{kind}' to container '{container.Name}' — the typed schema grants no lifecycle hook.");
            }
        }

        foreach (var (position, probe) in new[]
                 {
                     ("liveness", container.LivenessProbe),
                     ("readiness", container.ReadinessProbe),
                     ("startup", container.StartupProbe),
                 })
        {
            var kind = HandlerKind(probe?.Exec, probe?.HttpGet, probe?.TcpSocket, sleep: false)
                       ?? (probe?.Grpc is not null ? "grpc" : null);
            if (kind is not null)
            {
                violations.Add($"patch adds a {position} probe of kind '{kind}' to container '{container.Name}' — the typed schema grants no probe, and the kubelet performs it from the node on a schedule of the patch's choosing.");
            }
        }
    }

    private static string? HandlerKind(V1ExecAction? exec, V1HTTPGetAction? httpGet, V1TCPSocketAction? tcpSocket, bool sleep)
    {
        if (exec is not null) return "exec";
        if (httpGet is not null) return "httpGet";
        if (tcpSocket is not null) return "tcpSocket";
        return sleep ? "sleep" : null;
    }

    private static void ValidateContainerVolumeMounts(
        V1PodSpec pod,
        IReadOnlyDictionary<string, List<KubernetesWorkloadRenderer.HostPathSource>> hostPathSources,
        PatchedContainer container, List<string> violations)
    {
        var sources = (pod.Volumes ?? Enumerable.Empty<V1Volume>())
            .GroupBy(v => v.Name, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

        foreach (var mount in container.VolumeMounts ?? Enumerable.Empty<V1VolumeMount>())
        {
            if (!sources.TryGetValue(mount.Name ?? string.Empty, out var volume))
            {
                violations.Add($"patch mounts '{mount.Name}' at '{mount.MountPath}' in container '{container.Name}', but the pod declares no volume by that name — the mount escapes the reviewed volume set.");
                continue;
            }

            if (MustBeReadOnly(volume, hostPathSources) && mount.ReadOnlyProperty != true)
            {
                violations.Add($"patch mounts volume '{mount.Name}' at '{mount.MountPath}' in container '{container.Name}' WRITABLE — this source is resolved read-only, and read-only-ness lives on the mount.");
            }

            if (!string.IsNullOrEmpty(mount.SubPath) || !string.IsNullOrEmpty(mount.SubPathExpr))
            {
                violations.Add($"patch sets subPath '{mount.SubPath ?? mount.SubPathExpr}' on volume '{mount.Name}' in container '{container.Name}' — the typed schema grants a whole-volume mount, never a path selected inside it.");
            }

            if (!string.IsNullOrEmpty(mount.MountPropagation)
                && !string.Equals(mount.MountPropagation, "None", StringComparison.Ordinal))
            {
                violations.Add($"patch sets mountPropagation '{mount.MountPropagation}' on volume '{mount.Name}' in container '{container.Name}' — propagation carries mounts across the host boundary in one or both directions.");
            }
        }
    }

    private static bool MustBeReadOnly(
        V1Volume volume, IReadOnlyDictionary<string, List<KubernetesWorkloadRenderer.HostPathSource>> hostPathSources)
    {
        if (volume.ConfigMap is not null)
        {
            return true;
        }

        if (volume.HostPath is null)
        {
            return false;
        }

        return string.IsNullOrEmpty(volume.HostPath.Path)
               || !hostPathSources.TryGetValue(NormalizeHostPath(volume.HostPath.Path), out var sources)
               || sources.All(source => source.ReadOnly);
    }

    private static void ValidateContainerResources(PatchedContainer container, List<string> violations)
    {
        if (container.Resources?.Claims is { Count: > 0 } claims)
        {
            violations.Add($"patch asks for resource claims [{string.Join(", ", claims.Select(c => c.Name))}] on container '{container.Name}' — a dynamic resource claim injects devices the typed schema cannot declare.");
        }

        foreach (var (arm, quantities) in new[]
                 {
                     ("limits", container.Resources?.Limits),
                     ("requests", container.Resources?.Requests),
                 })
        {
            foreach (var key in quantities?.Keys ?? Enumerable.Empty<string>())
            {
                if (!string.Equals(key, "cpu", StringComparison.Ordinal)
                    && !string.Equals(key, "memory", StringComparison.Ordinal))
                {
                    violations.Add($"patch asks for resource '{key}' under {arm} on container '{container.Name}' — the typed schema declares only cpu and memory, and an extended resource is granted by a device plugin, not by the desired spec.");
                }
            }
        }
    }

    private static void ValidateContainerPorts(string name, IEnumerable<V1ContainerPort> ports, List<string> violations)
    {
        foreach (var port in ports)
        {
            if (port.HostPort is { } hostPort)
            {
                violations.Add($"patch binds hostPort={hostPort} on container '{name}' — a host-port binding the typed schema never grants (a host surface the desired spec cannot declare).");
            }
        }
    }

    private static void ValidateContainerSecurityContext(
        ResolvedPrivileges? priv, HashSet<string> resolvedCaps, string name, V1SecurityContext? sc, List<string> violations)
    {
        var resolvedPrivileged = priv?.EffectivePrivileged == true;
        var resolvedRunAsRoot = priv?.RunAsRoot == true;

        if (sc is null)
        {
            violations.Add($"patch leaves container '{name}' with no securityContext — the hardened baseline (drop ALL, allowPrivilegeEscalation=false, runAsNonRoot=true) was removed, so the container reverts to the insecure runtime default.");
            return;
        }

        if (sc.Privileged == true && !resolvedPrivileged)
        {
            violations.Add($"patch sets container '{name}' securityContext.privileged=true, which the typed spec did not declare (runs as root on the node).");
        }

        if (!resolvedPrivileged && sc.AllowPrivilegeEscalation != false)
        {
            var actual = sc.AllowPrivilegeEscalation is null ? "absent (defaults to true)" : "true";
            violations.Add($"patch leaves container '{name}' securityContext.allowPrivilegeEscalation {actual} — the hardened baseline sets it false; removing it reverts to the runtime default (escalation allowed).");
        }

        if (sc.RunAsUser == 0 && !resolvedRunAsRoot)
        {
            violations.Add($"patch sets container '{name}' securityContext.runAsUser=0 (root), which the typed spec did not declare (privileges.runAsRoot).");
        }

        if (!resolvedRunAsRoot && sc.RunAsNonRoot != true)
        {
            var actual = sc.RunAsNonRoot is null ? "absent (non-root not enforced)" : "false";
            violations.Add($"patch leaves container '{name}' securityContext.runAsNonRoot {actual} — the hardened baseline sets it true; removing it reverts to the runtime default, so the container may run as root.");
        }

        var drop = sc.Capabilities?.Drop;
        if (drop is null || !drop.Contains(DropAll, StringComparer.Ordinal))
        {
            violations.Add($"patch drops the capabilities.drop 'ALL' baseline on container '{name}' — the renderer deliberately drops ALL; without it the container regains the default capability set (escalation).");
        }

        foreach (var cap in sc.Capabilities?.Add ?? Enumerable.Empty<string>())
        {
            if (!resolvedCaps.Contains(cap))
            {
                violations.Add($"patch adds capability '{cap}' to container '{name}', which is not in the resolved capability set.");
            }
        }

        var seccompType = sc.SeccompProfile?.Type;
        if (sc.SeccompProfile is null)
        {
            violations.Add($"patch leaves container '{name}' securityContext.seccompProfile absent — the hardened baseline sets it RuntimeDefault; removing it reverts to Unconfined (full syscall surface) on clusters without the SeccompDefault kubelet gate.");
        }
        else if (!IsConfiningSeccompType(seccompType))
        {
            violations.Add($"patch sets container '{name}' securityContext.seccompProfile.type='{seccompType}' (not RuntimeDefault/Localhost) — the hardened baseline confines syscalls with RuntimeDefault; Unconfined restores the full syscall surface.");
        }

        if (sc.RunAsGroup == 0 && !resolvedRunAsRoot)
        {
            violations.Add($"patch sets container '{name}' securityContext.runAsGroup=0 (root group), which the typed spec did not declare (privileges.runAsRoot).");
        }

        if (!string.IsNullOrEmpty(sc.ProcMount) && !string.Equals(sc.ProcMount, "Default", StringComparison.Ordinal))
        {
            violations.Add($"patch sets container '{name}' securityContext.procMount='{sc.ProcMount}' — it unmasks the /proc entries the runtime hides by default (kcore, sys, sysrq-trigger).");
        }

        ValidateConfinementProfiles($"container '{name}'", sc.AppArmorProfile?.Type, sc.SeLinuxOptions is not null, violations);
    }

    private static void ValidateConfinementProfiles(
        string scope, string? appArmorType, bool hasSeLinuxOptions, List<string> violations)
    {
        if (string.Equals(appArmorType, "Unconfined", StringComparison.Ordinal))
        {
            violations.Add($"patch sets {scope} securityContext.appArmorProfile.type=Unconfined — it drops the kubelet's default AppArmor confinement, widening the same syscall and file surface the seccomp baseline closes.");
        }

        if (hasSeLinuxOptions)
        {
            violations.Add($"patch sets {scope} securityContext.seLinuxOptions — an SELinux type or level the typed schema has no field for, and a type such as spc_t lifts SELinux confinement entirely.");
        }
    }

    private static bool IsConfiningSeccompType(string? type)
        => string.Equals(type, SeccompRuntimeDefault, StringComparison.Ordinal)
           || string.Equals(type, SeccompLocalhost, StringComparison.Ordinal);

    private static void ValidateSingleInstanceInvariant(RenderedKubernetesObjects patched, List<string> violations)
    {
        if (patched.Deployment?.Spec is { } deploymentSpec)
        {
            ValidateDeploymentSingleInstance(deploymentSpec, violations);
        }

        if (patched.DaemonSet?.Spec is { } daemonSetSpec)
        {
            ValidateDaemonSetSingleInstance(daemonSetSpec, violations);
        }
    }

    private static void ValidateDeploymentSingleInstance(V1DeploymentSpec spec, List<string> violations)
    {
        if (spec.Replicas is { } replicas && replicas > 1)
        {
            violations.Add($"patch sets Deployment spec.replicas={replicas} (>1) — a service/single-node workload must run exactly one pod over its device/PVC; a second replica would double-bind it.");
        }

        var strategyType = spec.Strategy?.Type;
        if (!string.IsNullOrEmpty(strategyType) && !string.Equals(strategyType, "Recreate", StringComparison.Ordinal))
        {
            violations.Add($"patch changes Deployment spec.strategy.type to '{strategyType}' — a single-instance workload must use 'Recreate' so two pods never run over one device/PVC during a roll.");
        }
    }

    private static void ValidateDaemonSetSingleInstance(V1DaemonSetSpec spec, List<string> violations)
    {
        var maxSurge = spec.UpdateStrategy?.RollingUpdate?.MaxSurge;
        if (maxSurge is not null && IsSurging(maxSurge.Value))
        {
            violations.Add($"patch sets DaemonSet spec.updateStrategy.rollingUpdate.maxSurge={maxSurge.Value} (>0) — a per-node single-instance workload would then run two pods on one node during a roll, double-binding the node's device/PVC.");
        }
    }

    private static bool IsSurging(string maxSurge)
    {
        if (string.IsNullOrWhiteSpace(maxSurge))
        {
            return false;
        }

        var trimmed = maxSurge.Trim();
        var numericPart = trimmed.EndsWith('%') ? trimmed[..^1] : trimmed;

        return !int.TryParse(numericPart, out var value) || value > 0;
    }

    private static V1PodSpec? PodSpec(RenderedKubernetesObjects r)
        => r.Deployment?.Spec?.Template?.Spec ?? r.DaemonSet?.Spec?.Template?.Spec;

    private static IDictionary<string, string>? PodTemplateAnnotations(RenderedKubernetesObjects r)
        => r.Deployment is not null
            ? r.Deployment.Spec?.Template?.Metadata?.Annotations
            : r.DaemonSet?.Spec?.Template?.Metadata?.Annotations;

    private static Dictionary<string, List<KubernetesWorkloadRenderer.HostPathSource>> RenderedHostPathSources(ResolvedPrivileges? priv)
        => priv is null
            ? new Dictionary<string, List<KubernetesWorkloadRenderer.HostPathSource>>(StringComparer.Ordinal)
            : KubernetesWorkloadRenderer.HostPathSources(priv)
                .GroupBy(source => NormalizeHostPath(source.HostPath), StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);
}
