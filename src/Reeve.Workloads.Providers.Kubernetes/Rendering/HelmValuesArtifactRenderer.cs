using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Reeve.Workloads.Abstractions.Contracts;
using Reeve.Workloads.Abstractions.Contracts.Deployment;
using Reeve.Workloads.Abstractions.Domain.DesiredSpec;
using Reeve.Workloads.Abstractions.Rendering;
using Reeve.Workloads.Core.Rendering;

namespace Reeve.Workloads.Providers.Kubernetes.Rendering;

/// <summary>Renders a WorkloadDesiredSpec into a Helm values.yaml fragment for a generic workload chart:
/// image, sorted env, resources, ports, persistence, and a securityContext/hostDevices for privileges. Output is deterministic so goldens are stable.</summary>
public sealed class HelmValuesArtifactRenderer : IWorkloadArtifactRenderer
{
    /// <summary>The format this renderer declares: <c>helm-values</c>.</summary>
    public static WorkloadArtifactFormatId FormatId { get; } = WorkloadArtifactFormatId.Parse("helm-values");

    /// <inheritdoc />
    public WorkloadArtifactFormatId Format => FormatId;

    /// <inheritdoc />
    /// <remarks>Values need a chart to be fed to.</remarks>
    public bool IsSelfContained => false;

    /// <inheritdoc />
    public RenderedWorkloadArtifact Render(WorkloadDesiredSpec spec, WorkloadArtifactRenderOptions options)
    {
        ArgumentNullException.ThrowIfNull(spec);
        ArgumentNullException.ThrowIfNull(options);

        var env = WorkloadEnvRendering.Resolve(spec, options);

        var sb = new StringBuilder();
        sb.Append("# Reeve workload Helm values — ").Append(WorkloadArtifactEscaping.YamlPlainOrQuoted(spec.ResourceName)).Append('\n');
        sb.Append("# Feed this to a generic workload chart: helm install <name> <chart> -f values.yaml\n");

        AppendImage(sb, spec);
        AppendEnv(sb, env);
        AppendResources(sb, spec.Resources);
        AppendNetwork(sb, spec.Network);
        AppendUserNamespace(sb, spec);
        AppendPorts(sb, spec);
        AppendMetrics(sb, spec.Observability);
        AppendPersistence(sb, spec);
        AppendConfigMaps(sb, spec);
        AppendSecurity(sb, spec.Privileges);
        AppendNodeLocalState(sb, spec.Privileges);
        AppendHostPaths(sb, spec.Privileges);
        AppendNodeSelector(sb, spec);

        return new RenderedWorkloadArtifact
        {
            Format = Format,
            PrimaryContent = sb.ToString().TrimEnd('\n'),
            SecretOneLiner = null,
            Notes = BuildNotes(spec, env),
        };
    }

    private static void AppendImage(StringBuilder sb, WorkloadDesiredSpec spec)
    {
        sb.Append("image:\n");
        sb.Append("  reference: ").Append(WorkloadArtifactEscaping.YamlScalar(spec.Image)).Append('\n');
    }

    private static void AppendEnv(StringBuilder sb, IReadOnlyList<WorkloadEnvRendering.RenderedEnvEntry> env)
    {
        if (env.Count == 0)
        {
            return;
        }

        sb.Append("env:\n");
        foreach (var e in env)
        {
            sb.Append("  - name: ").Append(WorkloadArtifactEscaping.YamlScalar(e.Name)).Append('\n');
            sb.Append("    value: ").Append(WorkloadArtifactEscaping.YamlScalar(KubernetesEnvValue.Escape(e.Value))).Append('\n');
        }
    }

    private static void AppendResources(StringBuilder sb, ResolvedResources? r)
    {
        if (r is null)
        {
            return;
        }

        var requests = new List<(string, string)>();
        var limits = new List<(string, string)>();
        if (!string.IsNullOrWhiteSpace(r.RequestsCpu)) requests.Add(("cpu", r.RequestsCpu));
        if (!string.IsNullOrWhiteSpace(r.RequestsMemory)) requests.Add(("memory", r.RequestsMemory));
        if (!string.IsNullOrWhiteSpace(r.LimitsCpu)) limits.Add(("cpu", r.LimitsCpu));
        if (!string.IsNullOrWhiteSpace(r.LimitsMemory)) limits.Add(("memory", r.LimitsMemory));

        if (requests.Count == 0 && limits.Count == 0)
        {
            return;
        }

        sb.Append("resources:\n");
        if (requests.Count > 0)
        {
            sb.Append("  requests:\n");
            foreach (var (k, v) in requests)
            {
                sb.Append("    ").Append(WorkloadArtifactEscaping.YamlPlainOrQuoted(k)).Append(": ").Append(WorkloadArtifactEscaping.YamlScalar(v)).Append('\n');
            }
        }

        if (limits.Count > 0)
        {
            sb.Append("  limits:\n");
            foreach (var (k, v) in limits)
            {
                sb.Append("    ").Append(WorkloadArtifactEscaping.YamlPlainOrQuoted(k)).Append(": ").Append(WorkloadArtifactEscaping.YamlScalar(v)).Append('\n');
            }
        }
    }

    private static void AppendNetwork(StringBuilder sb, ResolvedNetwork? network)
    {
        if (network?.HostNetwork == true)
        {
            sb.Append("hostNetwork: true\n");
        }

        var attachments = ResolveAttachments(network);
        if (attachments.Count > 0)
        {
            sb.Append("networkAttachments:\n");
            foreach (var name in attachments)
            {
                sb.Append("  - ").Append(WorkloadArtifactEscaping.YamlPlainOrQuoted(name)).Append('\n');
            }
        }
    }

    private static void AppendUserNamespace(StringBuilder sb, WorkloadDesiredSpec spec)
    {
        if (spec.UserNamespace)
        {
            sb.Append("hostUsers: false\n");
        }
    }

    private static IReadOnlyList<string> ResolveAttachments(ResolvedNetwork? network)
    {
        if (network is null || network.HostNetwork || network.Mode != WorkloadNetworkMode.OperatorNetwork)
        {
            return [];
        }

        return network.Attachments;
    }

    private static void AppendMetrics(StringBuilder sb, WorkloadObservability? observability)
    {
        var metrics = observability?.Metrics;
        if (metrics is null)
        {
            return;
        }

        sb.Append("metrics:\n");
        sb.Append("  port: ").Append(metrics.Port.ToString(CultureInfo.InvariantCulture)).Append('\n');
        sb.Append("  path: ").Append(WorkloadArtifactEscaping.YamlPlainOrQuoted(metrics.Path)).Append('\n');
    }

    private static void AppendPorts(StringBuilder sb, WorkloadDesiredSpec spec)
    {
        var ports = spec.Network?.Ports ?? [];
        if (ports.Count == 0)
        {
            return;
        }

        sb.Append("ports:\n");
        foreach (var p in ports.OrderBy(p => p.Name, StringComparer.Ordinal).ThenBy(p => p.Port))
        {
            var proto = p.Protocol.Equals("udp", StringComparison.OrdinalIgnoreCase) ? "UDP" : "TCP";
            sb.Append("  - name: ").Append(WorkloadArtifactEscaping.YamlScalar(p.Name)).Append('\n');
            sb.Append("    containerPort: ").Append(p.Port.ToString(CultureInfo.InvariantCulture)).Append('\n');
            sb.Append("    protocol: ").Append(proto).Append('\n');
        }
    }

    private static void AppendPersistence(StringBuilder sb, WorkloadDesiredSpec spec)
    {
        if (spec.Volumes.Count == 0)
        {
            return;
        }

        sb.Append("persistence:\n");
        foreach (var v in spec.Volumes.OrderBy(v => v.Id, StringComparer.Ordinal))
        {
            sb.Append("  - id: ").Append(WorkloadArtifactEscaping.YamlPlainOrQuoted(v.Id)).Append('\n');
            sb.Append("    mountPath: ").Append(WorkloadArtifactEscaping.YamlPlainOrQuoted(v.MountPath)).Append('\n');

            if (v.ExistingClaim && !string.IsNullOrWhiteSpace(v.ResourceName))
            {
                sb.Append("    existingClaim: ").Append(WorkloadArtifactEscaping.YamlPlainOrQuoted(v.ResourceName.Trim())).Append('\n');
            }
            else
            {
                sb.Append("    size: ").Append(WorkloadArtifactEscaping.YamlScalar(v.Size)).Append('\n');
            }

            sb.Append("    criticality: ").Append(CriticalityWire(v.Criticality)).Append('\n');
            sb.Append("    accessMode: ").Append(WorkloadArtifactEscaping.YamlPlainOrQuoted(v.AccessMode)).Append('\n');
        }
    }

    private static void AppendConfigMaps(StringBuilder sb, WorkloadDesiredSpec spec)
    {
        if (spec.ConfigMapMounts.Count == 0)
        {
            return;
        }

        sb.Append("configMaps:\n");
        foreach (var m in spec.ConfigMapMounts
            .OrderBy(m => m.MountPath, StringComparer.Ordinal)
            .ThenBy(m => m.ConfigMapName, StringComparer.Ordinal))
        {
            sb.Append("  - name: ").Append(WorkloadArtifactEscaping.YamlScalar(m.ConfigMapName)).Append('\n');
            sb.Append("    mountPath: ").Append(WorkloadArtifactEscaping.YamlPlainOrQuoted(m.MountPath)).Append('\n');
            sb.Append("    readOnly: true\n");
        }
    }

    private static void AppendSecurity(StringBuilder sb, ResolvedPrivileges? priv)
    {
        if (priv is null)
        {
            return;
        }

        sb.Append("securityContext:\n");
        sb.Append("  privileged: ").Append(priv.EffectivePrivileged ? "true" : "false").Append('\n');
        sb.Append("  runAsNonRoot: ").Append(priv.RunAsRoot ? "false" : "true").Append('\n');
        if (priv.Capabilities.Count > 0)
        {
            sb.Append("  capabilities:\n");
            sb.Append("    add:\n");
            foreach (var cap in priv.Capabilities.OrderBy(c => c, StringComparer.Ordinal))
            {
                sb.Append("      - ").Append(WorkloadArtifactEscaping.YamlPlainOrQuoted(cap)).Append('\n');
            }
        }

        var devices = new List<string>(priv.DevicePaths.Where(p => !string.IsNullOrEmpty(p)));
        if (priv.TunDevice)
        {
            devices.Add("/dev/net/tun");
        }

        if (devices.Count > 0)
        {
            sb.Append("hostDevices:\n");
            foreach (var dev in devices.OrderBy(d => d, StringComparer.Ordinal))
            {
                sb.Append("  - ").Append(WorkloadArtifactEscaping.YamlPlainOrQuoted(dev)).Append('\n');
            }
        }

        if (priv.DeviceGroupIds.Count > 0 || priv.FsGroup is not null)
        {
            sb.Append("podSecurityContext:\n");
            if (priv.FsGroup is { } fsGroup)
            {
                sb.Append("  fsGroup: ").Append(fsGroup.ToString(CultureInfo.InvariantCulture)).Append('\n');
                sb.Append("  fsGroupChangePolicy: OnRootMismatch\n");
            }

            if (priv.DeviceGroupIds.Count > 0)
            {
                sb.Append("  supplementalGroups:\n");
                foreach (var gid in priv.DeviceGroupIds.OrderBy(g => g))
                {
                    sb.Append("    - ").Append(gid.ToString(CultureInfo.InvariantCulture)).Append('\n');
                }
            }
        }

        if (!string.IsNullOrEmpty(priv.DbusHostPath))
        {
            sb.Append("hostDbusPath: ").Append(WorkloadArtifactEscaping.YamlPlainOrQuoted(priv.DbusHostPath)).Append('\n');
        }
    }

    private static void AppendNodeLocalState(StringBuilder sb, ResolvedPrivileges? priv)
    {
        var nls = priv?.NodeLocalState;
        if (nls is null || string.IsNullOrEmpty(nls.HostPath))
        {
            return;
        }

        sb.Append("nodeLocalState:\n");
        sb.Append("  hostPath: ").Append(WorkloadArtifactEscaping.YamlPlainOrQuoted(nls.HostPath)).Append('\n');
        sb.Append("  criticality: ").Append(CriticalityWire(nls.Criticality)).Append('\n');
    }

    private static void AppendHostPaths(StringBuilder sb, ResolvedPrivileges? priv)
    {
        var hostPaths = priv?.HostPaths;
        if (hostPaths is null || hostPaths.Count == 0)
        {
            return;
        }

        sb.Append("hostPaths:\n");
        foreach (var hp in hostPaths.OrderBy(h => h.MountPath, StringComparer.Ordinal).ThenBy(h => h.HostPath, StringComparer.Ordinal))
        {
            if (string.IsNullOrEmpty(hp.HostPath) || string.IsNullOrEmpty(hp.MountPath))
            {
                continue;
            }

            sb.Append("  - hostPath: ").Append(WorkloadArtifactEscaping.YamlPlainOrQuoted(hp.HostPath)).Append('\n');
            sb.Append("    mountPath: ").Append(WorkloadArtifactEscaping.YamlPlainOrQuoted(hp.MountPath)).Append('\n');
            sb.Append("    readOnly: true\n");
        }
    }

    private static void AppendNodeSelector(StringBuilder sb, WorkloadDesiredSpec spec)
    {
        if (spec.NodeSelector.Count == 0)
        {
            return;
        }

        sb.Append("nodeSelector:\n");
        foreach (var kv in spec.NodeSelector.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            sb.Append("  ").Append(WorkloadArtifactEscaping.YamlPlainOrQuoted(kv.Key)).Append(": ").Append(WorkloadArtifactEscaping.YamlScalar(kv.Value)).Append('\n');
        }
    }

    private static IReadOnlyList<string> BuildNotes(
        WorkloadDesiredSpec spec, IReadOnlyList<WorkloadEnvRendering.RenderedEnvEntry> env)
    {
        var notes = new List<string>
        {
            "If this values file carries the revealed token, do NOT commit it to git — treat it as a secret and restrict access (chmod 600).",
        };

        var placeholders = env.Where(e => e.IsSecretPlaceholder).Select(e => e.Name).ToList();
        if (placeholders.Count > 0)
        {
            notes.Add(
                "Supply these secret values via your chart's secret mechanism (not this file): "
                + string.Join(", ", placeholders) + ".");
            notes.Add(
                "If you type a value over a placeholder in this file instead, write every $ in it as $$: the kubelet "
                + "expands $(NAME) in env values and turns $$ into $; the values this file already carries are escaped.");
        }

        var adoptedClaimNames = spec.Volumes
            .Where(v => v.ExistingClaim && !string.IsNullOrWhiteSpace(v.ResourceName))
            .Select(v => v.ResourceName.Trim())
            .Distinct(StringComparer.Ordinal)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();
        if (adoptedClaimNames.Count > 0)
        {
            notes.Add(
                "RESTORE: the volume(s) you chose to restore are rendered as existingClaim references, NOT a fresh "
                + "`size:` provision. The following volume(s) must already exist and your chart MUST bind them as "
                + "existing claims (do NOT let the chart provision fresh storage — that would deploy an empty volume and "
                + "lose the restore): " + string.Join(", ", adoptedClaimNames) + ". If your workload chart cannot "
                + "consume an existing claim, do NOT deploy it this way — the retained data would be silently discarded.");
        }

        var attachments = ResolveAttachments(spec.Network);
        if (attachments.Count > 0)
        {
            notes.Add(ManualArtifactNetworkNotes.Disclosure(attachments));
            notes.Add(
                "The `networkAttachments:` list names the network(s) only — your chart MUST wire them (on "
                + "Kubernetes that is the Multus `k8s.v1.cni.cncf.io/networks` pod annotation, one entry per name in "
                + "the SAME order). Each network must already exist; a chart that silently drops the list would "
                + "deploy the workload on the cluster network instead, with no error anywhere.");
        }

        var net = spec.Network;
        if (net is not null && !net.HostNetwork && (net.Mdns || net.Ipv6Required))
        {
            var needs = new List<string>();
            if (net.Mdns) needs.Add("mDNS discovery");
            if (net.Ipv6Required) needs.Add("IPv6");
            notes.Add(
                "This workload requests " + string.Join(" and ", needs)
                + " but is not host-networked — wire the equivalent (macvlan/multus, IPv6 dual-stack) in your chart; "
                + "Reeve cannot encode that generically.");
        }

        return notes;
    }

    private static string CriticalityWire(VolumeCriticality c) => c switch
    {
        VolumeCriticality.Cache => "cache",
        VolumeCriticality.Recoverable => "recoverable",
        VolumeCriticality.Critical => "critical",
        _ => c.ToString().ToLowerInvariant(),
    };

}
