using System;
using System.Collections.Generic;
using System.Linq;
using Reeve.Workloads.Abstractions.Contracts;
using Reeve.Workloads.Abstractions.Contracts.Deployment;
using Reeve.Workloads.Abstractions.Domain.DesiredSpec;
using Reeve.Workloads.Abstractions.Providers;

namespace Reeve.Workloads.Providers.Kubernetes;

internal sealed class KubernetesWorkloadPreflightChecker
{
    private const string ApiReachableId = "api-reachable";
    private const string PsaEnforcementId = "psa-enforcement";
    private const string ResourceQuotaRequestsId = "resourcequota-requests";
    private const string ArchAffinityId = "arch-affinity";
    private const string SingleNodeSelectorId = "single-node-selector";

    private const string StorageClassTopologyId = "storageclass-topology";

    private const string StorageClassMissingId = "storageclass-missing";

    private const string ScanNodesReadableId = "scan-nodes-readable";
    private const string DeviceGroupResolvedId = "device-group-resolved";
    private const string DevicePassthroughId = "device-passthrough";
    private const string UserNamespacesId = "user-namespaces";

    private const string PsaEnforceLabel = "pod-security.kubernetes.io/enforce";
    private const string ArchLabel = "kubernetes.io/arch";
    private static readonly Version UserNamespacesOnByDefault = new(1, 33);
    private static readonly Version RestrictedExemptsUserNamespaces = new(1, 35);

    private const string PsaRestrictedAllowedCapability = "NET_BIND_SERVICE";

    private static readonly IReadOnlySet<string> PsaBaselineAllowedCapabilities = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "AUDIT_WRITE", "CHOWN", "DAC_OVERRIDE", "FOWNER", "FSETID", "KILL", "MKNOD",
        "NET_BIND_SERVICE", "SETFCAP", "SETGID", "SETPCAP", "SETUID", "SYS_CHROOT",
    };

    private enum PsaLevel
    {
        None,
        Baseline,
        Restricted,

        Unknown,
    }

    public WorkloadPreflightResult Check(
        WorkloadDesiredSpec spec,
        KubernetesProviderSettings settings,
        KubernetesClusterInspection cluster)
    {
        ArgumentNullException.ThrowIfNull(spec);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(cluster);

        if (!cluster.ApiReachable)
        {
            var msg = "Cannot reach the Kubernetes API server.";
            if (!string.IsNullOrWhiteSpace(cluster.ApiError))
            {
                msg += $" ({cluster.ApiError})";
            }

            return new WorkloadPreflightResult
            {
                Checks =
                [
                    PreflightCheck.Fail(
                        ApiReachableId,
                        PreflightSeverity.Error,
                        msg,
                        fixSnippet: "Verify the backend ServiceAccount can reach the API server (in-cluster config / kubeconfig) and that RBAC allows list/get on nodes, namespaces, storageclasses and resourcequotas."),
                ],
            };
        }

        var checks = new List<PreflightCheck>
        {
            PreflightCheck.Pass(ApiReachableId, "Kubernetes API server is reachable."),
        };

        checks.Add(CheckPsaEnforcement(spec, cluster));
        checks.Add(CheckResourceQuotaRequests(spec, cluster));
        checks.Add(CheckArchAffinity(spec, cluster));

        if (spec.Privileges is { DevicePaths.Count: > 0 } devicePriv)
        {
            checks.Add(CheckDevicePassthrough(devicePriv));
        }

        if (spec.Privileges is { JoinsDeviceGroups: true, DeviceGroupIds.Count: 0, RunAsRoot: false } groupPriv)
        {
            checks.Add(UnresolvedDeviceGroup(groupPriv));
        }

        if (spec.UserNamespace || spec.UserNamespaceSkippedForHostNetwork || spec.UserNamespaceSkippedForDevicePassthrough)
        {
            checks.Add(CheckUserNamespaces(spec, cluster));
        }

        if (spec.Kind == WorkloadKind.SingleNode)
        {
            checks.Add(CheckSingleNodeSelector(spec, cluster));
        }

        if (spec.Kind == WorkloadKind.ScanPerNode)
        {
            checks.Add(CheckScanNodesReadable(cluster));
        }

        if (spec.Volumes.Count > 0)
        {
            checks.AddRange(CheckStorageClasses(spec, settings, cluster));
        }

        return new WorkloadPreflightResult { Checks = checks };
    }

    private static PreflightCheck Inconclusive(string id, string message) =>
        PreflightCheck.Fail(id, PreflightSeverity.Info, message);


    private static PreflightCheck CheckPsaEnforcement(WorkloadDesiredSpec spec, KubernetesClusterInspection cluster)
    {
        if (!cluster.NamespacePsaAvailable)
        {
            return Inconclusive(
                PsaEnforcementId,
                "Namespace Pod Security Admission level unknown — RBAC may not permit reading it (grant the optional read-only ClusterRole to evaluate this check).");
        }

        var enforce = cluster.NamespacePsaLabels.TryGetValue(PsaEnforceLabel, out var raw) ? raw : null;
        var level = ParsePsaLevel(enforce);

        if (level == PsaLevel.Unknown)
        {
            return Inconclusive(
                PsaEnforcementId,
                $"Namespace Pod Security Admission enforce level '{enforce}' is unknown (unrecognized value) — Pod Security Admission fails closed on an unparseable level, so this workload's compatibility could not be evaluated.");
        }

        if (level == PsaLevel.None)
        {
            return PreflightCheck.Pass(
                PsaEnforcementId,
                "No blocking Pod Security Admission enforcement on the target namespace.");
        }

        var violations = PsaViolations(spec, level, cluster.ServerVersion);
        if (violations.Count > 0)
        {
            return PreflightCheck.Fail(
                PsaEnforcementId,
                PreflightSeverity.Error,
                $"This namespace enforces the '{enforce}' Pod Security Standard, which this workload violates: {string.Join("; ", violations)}.",
                fixSnippet:
                    "Relax enforcement on a dedicated namespace, e.g.:\n" +
                    "  kubectl label namespace <ns> pod-security.kubernetes.io/enforce=privileged --overwrite\n" +
                    "or point the workload orchestrator at a dedicated namespace that allows the required pod security " +
                    "(and/or drop the added capabilities / root / hostPath the workload requests).");
        }

        return PreflightCheck.Pass(
            PsaEnforcementId,
            $"Namespace enforces the '{enforce}' Pod Security Standard and this workload is compatible.");
    }

    private static PsaLevel ParsePsaLevel(string? enforce)
    {
        if (string.IsNullOrWhiteSpace(enforce))
        {
            return PsaLevel.None;
        }

        if (string.Equals(enforce, "restricted", StringComparison.OrdinalIgnoreCase))
        {
            return PsaLevel.Restricted;
        }

        if (string.Equals(enforce, "baseline", StringComparison.OrdinalIgnoreCase))
        {
            return PsaLevel.Baseline;
        }

        if (string.Equals(enforce, "privileged", StringComparison.OrdinalIgnoreCase))
        {
            return PsaLevel.None;
        }

        return PsaLevel.Unknown;
    }

    private static IReadOnlyList<string> PsaViolations(WorkloadDesiredSpec spec, PsaLevel level, Version? serverVersion)
    {
        var violations = new List<string>();

        if (spec.Network?.HostNetwork == true)
        {
            violations.Add("hostNetwork");
        }

        var p = spec.Privileges;
        if (p is not null)
        {
            if (p.EffectivePrivileged)
            {
                violations.Add("privileged container");
            }

            if (p.DbusHostPath is not null || p.TunDevice || p.DevicePaths.Count > 0 || p.NodeLocalState is not null)
            {
                violations.Add("hostPath/host-device mounts");
            }

            var restrictedExempts = spec.UserNamespace && serverVersion is { } v && v >= RestrictedExemptsUserNamespaces;
            if (p.RunAsRoot && level == PsaLevel.Restricted && !restrictedExempts)
            {
                violations.Add("runAsRoot (restricted requires runAsNonRoot)");
            }

            var disallowedCaps = DisallowedCapabilities(p.Capabilities, level);
            if (disallowedCaps.Count > 0)
            {
                var allowedHint = level == PsaLevel.Restricted
                    ? $"only {PsaRestrictedAllowedCapability} is addable under restricted"
                    : "outside the baseline capability allowlist";
                violations.Add($"added Linux capabilities [{string.Join(", ", disallowedCaps)}] ({allowedHint})");
            }
        }

        return violations;
    }

    private static PreflightCheck CheckUserNamespaces(WorkloadDesiredSpec spec, KubernetesClusterInspection cluster)
    {
        if (spec.UserNamespaceSkippedForHostNetwork)
        {
            return PreflightCheck.Fail(
                UserNamespacesId,
                PreflightSeverity.Warning,
                "security.userNamespace is on, but this workload runs on the host network, which Kubernetes cannot combine with a user namespace; it runs without one.",
                fixSnippet: "Set network.mode to cluster or operatorNetwork for this workload to give it a user namespace.");
        }

        if (spec.UserNamespaceSkippedForDevicePassthrough)
        {
            return PreflightCheck.Fail(
                UserNamespacesId,
                PreflightSeverity.Warning,
                "security.userNamespace is on, but this workload binds a device, which Kubernetes opens only from a privileged " +
                "container, and a privileged container in a user namespace holds no host privilege; it runs without one.",
                fixSnippet: "Leave the device slots unbound for this workload to give it a user namespace.");
        }

        var problems = new List<string>();
        if (cluster.ServerVersion is { } version && version < UserNamespacesOnByDefault)
        {
            problems.Add($"the API server is Kubernetes {version.Major}.{version.Minor}, where the UserNamespacesSupport gate is off by default and hostUsers: false may be dropped without an error");
        }

        if (cluster.NodesAvailable)
        {
            var refusing = cluster.Nodes
                .Where(n => n.Schedulable && n.UserNamespacesSupported == false && NodeMatchesSelector(n, spec.NodeSelector))
                .Select(n => n.Name)
                .OrderBy(n => n, StringComparer.Ordinal)
                .ToList();
            if (refusing.Count > 0)
            {
                problems.Add($"node(s) {string.Join(", ", refusing)} report that their runtime does not support user namespaces, and the pod fails to start if it is scheduled there");
            }
        }

        if (problems.Count == 0)
        {
            var unknowns = new List<string>();
            if (cluster.ServerVersion is null)
            {
                unknowns.Add("the API server version could not be read or parsed");
            }
            if (!cluster.NodesAvailable)
            {
                unknowns.Add("the node list could not be read");
            }

            var message = "No reported incompatibility rules out a user namespace here; the kernel, volume filesystems and device permissions are not checked.";
            if (unknowns.Count > 0)
            {
                message += $" Cluster information unavailable: {string.Join("; ", unknowns)}.";
            }

            return PreflightCheck.Pass(UserNamespacesId, message);
        }

        return PreflightCheck.Fail(UserNamespacesId, PreflightSeverity.Warning,
            $"This workload runs in its own user namespace, but {string.Join("; ", problems)}.",
            fixSnippet: "Turn security.userNamespace off for this workload, or set nodeSelector to nodes that support user namespaces.");
    }

    private static IReadOnlyList<string> DisallowedCapabilities(IReadOnlyList<string> caps, PsaLevel level)
    {
        if (caps is null || caps.Count == 0)
        {
            return [];
        }

        return caps
            .Where(c => !string.IsNullOrWhiteSpace(c) && !IsAllowedCapability(c, level))
            .ToList();
    }

    private static bool IsAllowedCapability(string capability, PsaLevel level)
    {
        var normalized = capability.Trim();
        if (normalized.StartsWith("CAP_", StringComparison.OrdinalIgnoreCase))
        {
            normalized = normalized["CAP_".Length..];
        }

        return level == PsaLevel.Restricted
            ? string.Equals(normalized, PsaRestrictedAllowedCapability, StringComparison.OrdinalIgnoreCase)
            : PsaBaselineAllowedCapabilities.Contains(normalized);
    }


    private static PreflightCheck CheckResourceQuotaRequests(WorkloadDesiredSpec spec, KubernetesClusterInspection cluster)
    {
        if (!cluster.ResourceQuotaAvailable)
        {
            return Inconclusive(
                ResourceQuotaRequestsId,
                "Namespace ResourceQuota requirements unknown — RBAC may not permit reading them (grant resourcequotas get/list to evaluate this check).");
        }

        var declaresResources =
            spec.Resources is { } r &&
            (!string.IsNullOrWhiteSpace(r.RequestsCpu) || !string.IsNullOrWhiteSpace(r.RequestsMemory) ||
             !string.IsNullOrWhiteSpace(r.LimitsCpu) || !string.IsNullOrWhiteSpace(r.LimitsMemory));

        if (cluster.NamespaceHasResourceQuotaRequiringRequests && !declaresResources)
        {
            return PreflightCheck.Fail(
                ResourceQuotaRequestsId,
                PreflightSeverity.Error,
                "This namespace has a ResourceQuota requiring resource requests/limits, but this workload declares none.",
                fixSnippet:
                    "Set the workload's resource requests or limits (the 'resources' parameter group), e.g. requests.cpu=100m, requests.memory=128Mi, " +
                    "so the pod satisfies the namespace ResourceQuota.");
        }

        return PreflightCheck.Pass(
            ResourceQuotaRequestsId,
            cluster.NamespaceHasResourceQuotaRequiringRequests
                ? "Namespace ResourceQuota requires resource requests/limits and this workload declares them."
                : "No ResourceQuota in the namespace requires resource requests.");
    }


    private static PreflightCheck CheckArchAffinity(WorkloadDesiredSpec spec, KubernetesClusterInspection cluster)
    {
        if (spec.Platforms.Count == 0)
        {
            return PreflightCheck.Pass(ArchAffinityId, "No image-architecture constraint to evaluate.");
        }

        if (!cluster.NodesAvailable)
        {
            return Inconclusive(
                ArchAffinityId,
                "Cluster nodes could not be read — architecture compatibility unknown (grant the optional read-only ClusterRole to evaluate this check).");
        }

        if (cluster.Nodes.Count == 0)
        {
            return Inconclusive(
                ArchAffinityId,
                "The cluster reported no nodes — architecture compatibility could not be evaluated.");
        }

        var imageArchs = spec.Platforms
            .Select(ArchOf)
            .Where(a => !string.IsNullOrEmpty(a))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (imageArchs.Count == 0)
        {
            return PreflightCheck.Pass(ArchAffinityId, "No image-architecture constraint to evaluate.");
        }

        var candidates = cluster.Nodes.Where(n => n.Schedulable && NodeMatchesSelector(n, spec.NodeSelector)).ToList();

        if (candidates.Count == 0)
        {
            return Inconclusive(
                ArchAffinityId,
                "No schedulable node matches the workload's node selector, so image architecture could not be evaluated — adjust the node selector (or label/uncordon a target node) so it matches at least one schedulable node, then re-run.");
        }

        var candidateArchs = candidates
            .Select(n => n.Arch)
            .Where(a => !string.IsNullOrEmpty(a))
            .Select(a => a!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var hasMatch = candidateArchs.Any(a => imageArchs.Contains(a, StringComparer.OrdinalIgnoreCase));
        if (hasMatch)
        {
            return PreflightCheck.Pass(
                ArchAffinityId,
                $"At least one schedulable node matches the image architecture(s) [{string.Join(", ", imageArchs)}].");
        }

        var haveArchs = candidateArchs.Count > 0
            ? string.Join(", ", candidateArchs.OrderBy(a => a, StringComparer.Ordinal))
            : "none";

        return PreflightCheck.Fail(
            ArchAffinityId,
            PreflightSeverity.Warning,
            $"No schedulable node matches the image architecture(s) [{string.Join(", ", imageArchs)}]. " +
            $"Candidate node architecture(s): [{haveArchs}]. If the image is genuinely single-arch this will leave the " +
            "pod unschedulable (Pending) or crash-looping — proceed only if you are adding a matching node or the image is multi-arch.",
            fixSnippet:
                "Add a node of a matching architecture, publish a multi-arch image that covers your nodes, " +
                "or adjust the workload nodeSelector so it targets nodes of the image architecture.");
    }


    private static PreflightCheck CheckSingleNodeSelector(WorkloadDesiredSpec spec, KubernetesClusterInspection cluster)
    {
        if (!cluster.NodesAvailable)
        {
            return Inconclusive(
                SingleNodeSelectorId,
                "Cluster nodes could not be read — the single-node selector could not be resolved (grant the optional read-only ClusterRole to evaluate this check).");
        }

        var matching = cluster.Nodes.Where(n => n.Schedulable && NodeMatchesSelector(n, spec.NodeSelector)).ToList();

        if (matching.Count == 1)
        {
            return PreflightCheck.Pass(
                SingleNodeSelectorId,
                $"Single-node selector matches exactly one schedulable node ('{matching[0].Name}').");
        }

        var cordonedMatches = cluster.Nodes.Where(n => !n.Schedulable && NodeMatchesSelector(n, spec.NodeSelector)).ToList();

        if (matching.Count == 0)
        {
            var cordonedClause = cordonedMatches.Count > 0
                ? $" A matching node ({string.Join(", ", cordonedMatches.Select(n => n.Name))}) is cordoned or NoSchedule-tainted, so it is not schedulable."
                : string.Empty;

            return PreflightCheck.Fail(
                SingleNodeSelectorId,
                PreflightSeverity.Warning,
                $"The single-node selector matches no schedulable node.{cordonedClause} The pod will stay Pending until a schedulable node matches — proceed if you are about to label or uncordon the target node.",
                fixSnippet: cordonedMatches.Count > 0
                    ? "Uncordon the matching node (kubectl uncordon <node-name>) or remove the blocking taint so the pod can schedule there."
                    : "Label the target node so the selector matches exactly one node, e.g.:\n" +
                      $"  {LabelNodeHint(spec.NodeSelector)}");
        }

        return PreflightCheck.Pass(
            SingleNodeSelectorId,
            $"The single-node selector matches {matching.Count} schedulable nodes ({string.Join(", ", matching.Select(n => n.Name))}) — " +
            "Kubernetes schedules the single replica on one of them. Pin it to a specific node via nodePin (nodeSelector) " +
            "only if this workload needs one particular node (e.g. a local radio/dongle or node-local state).");
    }

    private static PreflightCheck CheckScanNodesReadable(KubernetesClusterInspection cluster)
    {
        if (!cluster.NodesAvailable)
        {
            return PreflightCheck.Fail(
                ScanNodesReadableId,
                PreflightSeverity.Error,
                "Cluster nodes could not be read (403) — a scan-per-node run cannot enumerate its Ready target nodes, so it would silently create zero Jobs (mistakable for a legitimate zero-node result). Grant the read-only ClusterRole so nodes can be listed.",
                fixSnippet:
                    "Grant the backend ServiceAccount cluster-scoped read on nodes, e.g. bind the optional read-only ClusterRole:\n" +
                    "  kubectl create clusterrolebinding reeve-nodes-read --clusterrole=reeve-workloads-readonly --serviceaccount=<ns>:<sa>");
        }

        return PreflightCheck.Pass(
            ScanNodesReadableId,
            "Cluster nodes are readable — the scan-per-node run can enumerate its Ready target nodes.");
    }


    private static PreflightCheck CheckDevicePassthrough(ResolvedPrivileges privileges)
    {
        var devices = string.Join(", ", privileges.DevicePaths.OrderBy(d => d, StringComparer.Ordinal));
        return PreflightCheck.Pass(
            DevicePassthroughId,
            $"This workload binds {devices} and runs privileged: Kubernetes mounts a device as a hostPath, and the " +
            "container runtime's device cgroup refuses to open it in an unprivileged container. The namespace must " +
            "admit privileged pods.");
    }

    private static PreflightCheck UnresolvedDeviceGroup(ResolvedPrivileges privileges)
    {
        var devices = privileges.DevicePaths.Count > 0
            ? string.Join(", ", privileges.DevicePaths.OrderBy(d => d, StringComparer.Ordinal))
            : "its bound device";

        return PreflightCheck.Fail(
            DeviceGroupResolvedId,
            PreflightSeverity.Warning,
            $"This workload joins the owning group of {devices}, but no group id could be resolved for it, " +
            "so the pod gets no supplementalGroups entry. The container will fail to open the device (EACCES) unless " +
            "its image runs as root.",
            fixSnippet:
                "Run the discovery scan on the node that holds the device — the group id is read from that node's hardware " +
                "inventory — then re-run this preflight. Nodes reporting conflicting group ids for the device also yield none.");
    }


    private static IEnumerable<PreflightCheck> CheckStorageClasses(
        WorkloadDesiredSpec spec,
        KubernetesProviderSettings settings,
        KubernetesClusterInspection cluster)
    {
        if (!cluster.StorageClassesAvailable)
        {
            const string unreadable = "Cluster StorageClasses could not be read (grant the optional read-only ClusterRole to evaluate this check)";
            return
            [
                Inconclusive(StorageClassMissingId, $"{unreadable} — whether the volumes' StorageClasses exist is unknown."),
                Inconclusive(StorageClassTopologyId, $"{unreadable} — volume topology binding unknown."),
            ];
        }

        var resolved = new List<ClusterStorageClassInfo>();
        var missing = new List<string>();

        foreach (var volume in spec.Volumes)
        {
            var effective = volume.StorageClassName ?? settings.DefaultStorageClassName;

            var sc = effective is not null
                ? cluster.StorageClasses.FirstOrDefault(s => string.Equals(s.Name, effective, StringComparison.Ordinal))
                : cluster.StorageClasses.FirstOrDefault(s => s.IsDefault);

            if (sc is null)
            {
                missing.Add(effective ?? "(cluster default: none marked)");
            }
            else
            {
                resolved.Add(sc);
            }
        }

        return [CheckStorageClassMissing(missing), CheckStorageClassTopology(resolved)];
    }

    private static PreflightCheck CheckStorageClassMissing(List<string> missing)
    {
        if (missing.Count == 0)
        {
            return PreflightCheck.Pass(StorageClassMissingId, "Every workload volume's StorageClass exists on the cluster.");
        }

        var names = string.Join(", ", missing.Distinct(StringComparer.Ordinal).Select(n => $"'{n}'"));
        return PreflightCheck.Fail(
            StorageClassMissingId,
            PreflightSeverity.Warning,
            $"StorageClass {names} not found on the cluster — the PVC will stay Pending until a matching class exists.",
            fixSnippet:
                "Create/name an existing StorageClass for the volume (volumes[].storageClassName or the provider default), " +
                "or mark a cluster default StorageClass (storageclass.kubernetes.io/is-default-class=true).");
    }

    private static PreflightCheck CheckStorageClassTopology(List<ClusterStorageClassInfo> resolved)
    {
        if (resolved.Count == 0)
        {
            return Inconclusive(
                StorageClassTopologyId,
                "No volume's StorageClass exists on the cluster, so volume topology binding could not be evaluated — see storageclass-missing.");
        }

        var topologyBound = resolved.Where(IsTopologyBound).Select(sc => sc.Name).Distinct(StringComparer.Ordinal).ToList();
        if (topologyBound.Count > 0)
        {
            var names = string.Join(", ", topologyBound.Select(n => $"'{n}'"));
            return PreflightCheck.Fail(
                StorageClassTopologyId,
                PreflightSeverity.Warning,
                $"StorageClass {names} is topology-bound (WaitForFirstConsumer/local-path); the PVC will pin to the node where the pod first schedules — node loss strands the volume.",
                fixSnippet:
                    "This is expected for a single-node/homelab default. For HA use a network/replicated StorageClass " +
                    "(Immediate binding) so the volume is not tied to one node.");
        }

        return PreflightCheck.Pass(
            StorageClassTopologyId,
            "Workload volumes use StorageClass(es) that are not topology-bound.");
    }

    private static bool IsTopologyBound(ClusterStorageClassInfo sc)
    {
        if (string.Equals(sc.VolumeBindingMode, "WaitForFirstConsumer", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var provisioner = sc.Provisioner ?? string.Empty;
        return provisioner.Contains("local-path", StringComparison.OrdinalIgnoreCase)
            || provisioner.Contains("rancher.io/local-path", StringComparison.OrdinalIgnoreCase);
    }


    private static bool NodeMatchesSelector(ClusterNodeInfo node, IReadOnlyDictionary<string, string> selector)
    {
        if (selector.Count == 0)
        {
            return true;
        }

        foreach (var (key, value) in selector)
        {
            if (!node.Labels.TryGetValue(key, out var nodeValue) || !string.Equals(nodeValue, value, StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    private static string ArchOf(string platform)
    {
        if (string.IsNullOrEmpty(platform))
        {
            return string.Empty;
        }

        var slash = platform.LastIndexOf('/');
        return slash < 0 ? platform : platform[(slash + 1)..];
    }

    private static string LabelNodeHint(IReadOnlyDictionary<string, string> selector)
    {
        var first = selector.FirstOrDefault();
        return first.Key is null
            ? "kubectl label node <node-name> <key>=<value>"
            : $"kubectl label node <node-name> {first.Key}={first.Value}";
    }
}
