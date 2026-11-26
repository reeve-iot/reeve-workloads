using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Reeve.Workloads.Abstractions.Contracts;
using Reeve.Workloads.Abstractions.Domain;
using Reeve.Workloads.Abstractions.Domain.DesiredSpec;
using Reeve.Workloads.Abstractions.Domain.Network;
using Reeve.Workloads.Abstractions.Providers;
using Reeve.Workloads.Abstractions.Rendering;
using Reeve.Workloads.Core.Rendering;
using Reeve.Workloads.Domain;
using Reeve.Workloads.Providers.Docker;

namespace Reeve.Workloads.Providers.Docker.Rendering;

/// <summary>Renders a WorkloadDesiredSpec into a docker-compose file for the manual provider: one
/// service, image passed through verbatim, restart: unless-stopped, sorted env, host-published ports, mapped privileges, and PVC-backed volumes as named volumes using the same naming scheme the future docker provider will manage, so data survives a later adoption.</summary>
public sealed class ComposeArtifactRenderer : IWorkloadArtifactRenderer
{
    /// <summary>The format this renderer declares: <c>compose</c>.</summary>
    public static WorkloadArtifactFormatId FormatId { get; } = WorkloadArtifactFormatId.Parse("compose");

    /// <inheritdoc />
    public WorkloadArtifactFormatId Format => FormatId;

    /// <inheritdoc />
    public bool IsSelfContained => true;

    /// <inheritdoc />
    public RenderedWorkloadArtifact Render(WorkloadDesiredSpec spec, WorkloadArtifactRenderOptions options)
    {
        ArgumentNullException.ThrowIfNull(spec);
        ArgumentNullException.ThrowIfNull(options);

        var envEntries = WorkloadEnvRendering.Resolve(spec, options);
        var namedVolumes = ResolveNamedVolumes(spec);

        var sb = new StringBuilder();
        sb.Append("# Reeve workload compose file — ").Append(WorkloadArtifactEscaping.YamlPlainOrQuoted(spec.ResourceName)).Append('\n');
        sb.Append("# docker compose up -d\n");
        sb.Append("services:\n");
        sb.Append("  ").Append(WorkloadArtifactEscaping.YamlPlainOrQuoted(spec.ResourceName)).Append(":\n");
        sb.Append("    image: ").Append(ComposeValue(spec.Image)).Append('\n');
        sb.Append("    restart: unless-stopped\n");

        AppendNetworkMode(sb, spec.Network);
        AppendServiceNetworks(sb, spec.Network);
        AppendEnvironment(sb, envEntries);
        AppendPorts(sb, spec);
        AppendPrivileges(sb, spec.Privileges);
        AppendVolumeMounts(sb, spec, namedVolumes);

        AppendTopLevelVolumes(sb, namedVolumes);
        AppendTopLevelNetworks(sb, spec.Network);

        return new RenderedWorkloadArtifact
        {
            Format = Format,
            PrimaryContent = sb.ToString().TrimEnd('\n'),
            SecretOneLiner = null,
            Notes = BuildNotes(spec, envEntries),
        };
    }

    private static string ComposeValue(string? value)
        => WorkloadArtifactEscaping.YamlScalar(EscapeInterpolation(value));

    private static string ComposePlainOrQuotedValue(string? value)
        => WorkloadArtifactEscaping.YamlPlainOrQuoted(EscapeInterpolation(value));

    private static string? EscapeInterpolation(string? value)
        => value?.Replace("$", "$$", StringComparison.Ordinal);

    private static void AppendNetworkMode(StringBuilder sb, ResolvedNetwork? network)
    {
        if (network?.HostNetwork == true)
        {
            sb.Append("    network_mode: host\n");
        }
    }

    private static void AppendServiceNetworks(StringBuilder sb, ResolvedNetwork? network)
    {
        var attachments = ResolveAttachments(network);
        if (attachments.Count == 0)
        {
            return;
        }

        sb.Append("    networks:\n");
        sb.Append("      - ").Append(DockerProviderConstants.DefaultNetworkName).Append('\n');
        foreach (var name in attachments)
        {
            sb.Append("      - ").Append(ComposePlainOrQuotedValue(name)).Append('\n');
        }
    }

    private static void AppendTopLevelNetworks(StringBuilder sb, ResolvedNetwork? network)
    {
        var attachments = ResolveAttachments(network);
        if (attachments.Count == 0)
        {
            return;
        }

        sb.Append("networks:\n");
        sb.Append("  ").Append(DockerProviderConstants.DefaultNetworkName).Append(":\n");
        sb.Append("    name: ").Append(ComposeValue(DockerProviderConstants.DefaultNetworkName)).Append('\n');

        foreach (var name in attachments)
        {
            sb.Append("  ").Append(WorkloadArtifactEscaping.YamlPlainOrQuoted(name)).Append(":\n");
            sb.Append("    external: true\n");
            sb.Append("    name: ").Append(ComposeValue(name)).Append('\n');
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

    private static void AppendEnvironment(StringBuilder sb, IReadOnlyList<WorkloadEnvRendering.RenderedEnvEntry> env)
    {
        if (env.Count == 0)
        {
            return;
        }

        sb.Append("    environment:\n");
        foreach (var e in env)
        {
            sb.Append("      ")
                .Append(WorkloadArtifactEscaping.YamlScalar(e.Name))
                .Append(": ")
                .Append(ComposeValue(e.Value))
                .Append('\n');
        }
    }

    private static void AppendPorts(StringBuilder sb, WorkloadDesiredSpec spec)
    {
        if (spec.Network?.HostNetwork == true)
        {
            return;
        }

        var published = (spec.Network?.Ports ?? [])
            .Where(p => p.HostPort is not null)
            .OrderBy(p => p.HostPort!.Value)
            .ThenBy(p => p.Port)
            .ToList();

        if (published.Count == 0)
        {
            return;
        }

        sb.Append("    ports:\n");
        foreach (var p in published)
        {
            var proto = p.Protocol.Equals("udp", StringComparison.OrdinalIgnoreCase) ? "udp" : "tcp";
            sb.Append("      - \"")
              .Append(p.HostPort!.Value.ToString(CultureInfo.InvariantCulture))
              .Append(':')
              .Append(p.Port.ToString(CultureInfo.InvariantCulture))
              .Append('/')
              .Append(proto)
              .Append("\"\n");
        }
    }

    private static void AppendPrivileges(StringBuilder sb, ResolvedPrivileges? priv)
    {
        sb.Append("    cap_drop:\n");
        sb.Append("      - ALL\n");

        if (priv is null)
        {
            return;
        }

        if (DockerPrivilegeRenderer.ContainerUser(priv) is { } user)
        {
            sb.Append("    user: ").Append(ComposeValue(user)).Append('\n');
        }

        if (priv.Privileged)
        {
            sb.Append("    privileged: true\n");
        }

        if (priv.Capabilities.Count > 0)
        {
            sb.Append("    cap_add:\n");
            foreach (var cap in priv.Capabilities.OrderBy(c => c, StringComparer.Ordinal))
            {
                sb.Append("      - ").Append(ComposePlainOrQuotedValue(cap)).Append('\n');
            }
        }

        var devices = new List<string>(priv.DevicePaths.Where(p => !string.IsNullOrEmpty(p)));
        if (priv.TunDevice)
        {
            devices.Add("/dev/net/tun");
        }

        if (devices.Count > 0)
        {
            sb.Append("    devices:\n");
            foreach (var dev in devices.OrderBy(d => d, StringComparer.Ordinal))
            {
                sb.Append("      - ").Append(ComposeValue($"{dev}:{dev}")).Append('\n');
            }
        }

        var groups = DockerSupplementaryGroups.For(priv);
        if (groups.Count > 0)
        {
            sb.Append("    group_add:\n");
            foreach (var gid in groups)
            {
                sb.Append("      - \"").Append(gid.ToString(CultureInfo.InvariantCulture)).Append("\"\n");
            }
        }
    }

    private readonly record struct NamedVolume(string VolumeName, string MountPath, bool ExistingClaim, bool ReadOnly);

    private static IReadOnlyList<NamedVolume> ResolveNamedVolumes(WorkloadDesiredSpec spec)
        => spec.Volumes
            .Select(v => new NamedVolume(
                v.ExistingClaim && !string.IsNullOrWhiteSpace(v.ResourceName)
                    ? v.ResourceName
                    : DockerResourceNamer.Instance.VolumeName(
                        new WorkloadNamingSubject
                        {
                            GroupId = spec.GroupId,
                            WorkloadId = spec.WorkloadId,
                            Inst8 = spec.Inst8,
                        },
                        v.Id),
                v.MountPath,
                v.ExistingClaim && !string.IsNullOrWhiteSpace(v.ResourceName),
                DockerContainerManager.IsReadOnlyAccessMode(v.AccessMode)))
            .OrderBy(v => v.VolumeName, StringComparer.Ordinal)
            .ToList();

    private static void AppendVolumeMounts(StringBuilder sb, WorkloadDesiredSpec spec, IReadOnlyList<NamedVolume> named)
    {
        var lines = new List<string>();
        foreach (var v in named)
        {
            lines.Add("      - " + ComposeValue(v.ReadOnly ? $"{v.VolumeName}:{v.MountPath}:ro" : $"{v.VolumeName}:{v.MountPath}"));
        }

        var nodeLocalState = spec.Privileges?.NodeLocalState;
        if (nodeLocalState is { } nls && !string.IsNullOrEmpty(nls.HostPath))
        {
            lines.Add("      - " + ComposeValue($"{nls.HostPath}:{nls.HostPath}"));
        }

        var dbus = spec.Privileges?.DbusHostPath;
        if (!string.IsNullOrEmpty(dbus))
        {
            lines.Add("      - " + ComposeValue($"{dbus}:{dbus}:ro"));
        }

        foreach (var hp in spec.Privileges?.HostPaths ?? [])
        {
            if (!string.IsNullOrEmpty(hp.HostPath) && !string.IsNullOrEmpty(hp.MountPath))
            {
                lines.Add("      - " + ComposeValue($"{hp.HostPath}:{hp.MountPath}:ro"));
            }
        }

        if (lines.Count == 0)
        {
            return;
        }

        sb.Append("    volumes:\n");
        foreach (var line in lines)
        {
            sb.Append(line).Append('\n');
        }
    }

    private static void AppendTopLevelVolumes(StringBuilder sb, IReadOnlyList<NamedVolume> named)
    {
        if (named.Count == 0)
        {
            return;
        }

        sb.Append("volumes:\n");
        foreach (var v in named)
        {
            sb.Append("  ").Append(WorkloadArtifactEscaping.YamlPlainOrQuoted(v.VolumeName)).Append(":\n");
            if (v.ExistingClaim)
            {
                sb.Append("    external: true\n");
            }

            sb.Append("    name: ").Append(ComposeValue(v.VolumeName)).Append('\n');
        }
    }

    private static IReadOnlyList<string> BuildNotes(
        WorkloadDesiredSpec spec, IReadOnlyList<WorkloadEnvRendering.RenderedEnvEntry> env)
    {
        var notes = new List<string>
        {
            "This compose file may contain the workload's credentials in plaintext — restrict it: chmod 600 the compose file.",
            "The image is pinned by digest (ref@digest); Reeve cannot verify your out-of-band pull path — ensure the digest is reachable from this host.",
        };

        var placeholders = env.Where(e => e.IsSecretPlaceholder).Select(e => e.Name).ToList();
        if (placeholders.Count > 0)
        {
            notes.Add(
                "Set these secret values yourself before `docker compose up`: " + string.Join(", ", placeholders) + ".");
        }

        if (spec.Volumes.Count > 0)
        {
            notes.Add(
                "Named volumes use the Reeve docker naming scheme (reeve-{pluginTypeId}-{volumeId}-{inst8}); "
                + "a later docker-provider adoption reuses the same volumes in place.");
        }

        if (spec.Privileges?.FsGroup is { } fsGroup && spec.Volumes.Count > 0)
        {
            notes.Add(
                "Compose cannot hand a volume to a group. Make each named volume's root group-owned by gid "
                + fsGroup.ToString(CultureInfo.InvariantCulture)
                + " with g+rwx and the setgid bit (a docker-provider install does this before every start); group_add gives the service that gid.");
        }

        var netForPorts = spec.Network;
        if (netForPorts is { HostNetwork: true } && netForPorts.Ports.Any(p => p.HostPort is not null))
        {
            var hostPorts = netForPorts.Ports
                .Where(p => p.HostPort is not null)
                .Select(p => p.HostPort!.Value.ToString(CultureInfo.InvariantCulture))
                .OrderBy(p => p, StringComparer.Ordinal);
            notes.Add(
                "Under host networking the published ports (" + string.Join(", ", hostPorts) + ") are exposed "
                + "implicitly on the host — no `ports:` block is emitted because Docker rejects publishing ports on a "
                + "`network_mode: host` container (conflicting options: port publishing and the container network mode).");
        }

        var attachments = ResolveAttachments(spec.Network);
        if (attachments.Count > 0)
        {
            notes.Add(ManualArtifactNetworkNotes.Disclosure(attachments));
            notes.Add(
                "Each attached network is declared `external: true`, so it MUST already exist on this host under "
                + "exactly that name before `docker compose up` — compose will refuse to start rather than invent a "
                + "look-alike network without your parent-interface/VLAN configuration. The Reeve shared network `"
                + DockerProviderConstants.DefaultNetworkName + "` is retained alongside the attachment(s): that is "
                + "how the sidecar reaches its host's endpoints, so do not remove it from the service.");
        }

        var net = spec.Network;
        if (net is not null && !net.HostNetwork && (net.Mdns || net.Ipv6Required))
        {
            var needs = new List<string>();
            if (net.Mdns) needs.Add("mDNS discovery");
            if (net.Ipv6Required) needs.Add("IPv6");
            notes.Add(
                "This workload requests " + string.Join(" and ", needs)
                + " but is not host-networked — full macvlan/IPv6 compose config is out of scope; "
                + "wire a macvlan/IPv6-enabled network yourself if the sidecar needs it.");
        }

        if (spec.Resources is { } r
            && (!string.IsNullOrWhiteSpace(r.RequestsCpu) || !string.IsNullOrWhiteSpace(r.RequestsMemory)
                || !string.IsNullOrWhiteSpace(r.LimitsCpu) || !string.IsNullOrWhiteSpace(r.LimitsMemory)))
        {
            var parts = new List<string>();
            if (!string.IsNullOrWhiteSpace(r.RequestsCpu)) parts.Add($"cpu request {r.RequestsCpu}");
            if (!string.IsNullOrWhiteSpace(r.RequestsMemory)) parts.Add($"memory request {r.RequestsMemory}");
            if (!string.IsNullOrWhiteSpace(r.LimitsCpu)) parts.Add($"cpu limit {r.LimitsCpu}");
            if (!string.IsNullOrWhiteSpace(r.LimitsMemory)) parts.Add($"memory limit {r.LimitsMemory}");
            notes.Add(
                "Declared resource constraints (" + string.Join(", ", parts) + ") are NOT emitted — compose resource "
                + "syntax is version-sensitive; add `mem_limit`/`cpus` or a `deploy.resources` block to match.");
        }

        return notes;
    }

}
