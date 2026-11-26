using Reeve.Workloads.Abstractions.Contracts;
using Reeve.Workloads.Abstractions.Contracts.Deployment;
using Reeve.Workloads.Abstractions.Domain.DesiredSpec;
using Reeve.Workloads.Abstractions.Domain.Network;

namespace Reeve.Workloads.Abstractions.Domain;

/// <summary>What one workload requires of the node it runs on, derived from its manifest and resolved parameters.
/// Split into a security axis (compared as a subset via <c>Covers</c>) and an informational axis that never escalates.</summary>
/// <remarks>A fact, not a decision: whether anyone accepted these requirements is the host's to record.</remarks>
public sealed record WorkloadPrivilegeRequirements
{
    public required WorkloadKind Kind { get; init; }

    public bool HostNetwork { get; init; }
    public bool Mdns { get; init; }
    public bool Ipv6Required { get; init; }

    private readonly WorkloadNetworkMode? networkMode;

    /// <summary>The required network POSTURE — never the concrete attachment names, which are configuration,
    /// not a security decision. Back-fills to <c>hostNetwork ? hostNetwork : cluster</c> for a profile persisted before this field existed, so no stored value becomes non-<c>Allows</c> after deploy.</summary>
    public WorkloadNetworkMode NetworkMode
    {
        get => networkMode is { } explicitMode && explicitMode != WorkloadNetworkMode.Cluster
            ? explicitMode
            : HostNetwork ? WorkloadNetworkMode.HostNetwork : WorkloadNetworkMode.Cluster;
        init => networkMode = value;
    }

    public bool Privileged { get; init; }
    public bool RunAsRoot { get; init; }
    public bool HostDbus { get; init; }
    public bool TunDevice { get; init; }

    /// <summary>The FACT that the workload joins its device's owning group — never the GID, which renumbers
    /// across distributions and would re-prompt an untouched install after an OS upgrade.</summary>
    public bool JoinsDeviceGroups { get; init; }

    /// <summary>Whether the workload asks to call its host's own API with a credential the host issued
    /// (<c>privileges.callsBackendRest</c>). Security-bearing, so it is compared and diffed like every other
    /// privilege requirement. The library grants and enforces nothing on it: a host that serves such an API
    /// decides what the flag admits, and refuses a workload whose accepted requirements lack it.</summary>
    public bool CallsBackendRest { get; init; }

    /// <summary>The group id the workload joins to own its volumes (<c>privileges.fsGroup</c>), or null when it
    /// declares none. Unlike <see cref="JoinsDeviceGroups"/> this is the number, because the author fixes it in the
    /// manifest and it never renumbers: a different group reaches different group-owned objects, so moving it is
    /// an escalation and dropping it a narrowing.</summary>
    public int? FsGroup { get; init; }

    /// <summary>Required Linux capability names (e.g. <c>NET_ADMIN</c>). Set-subset semantics.</summary>
    public IReadOnlySet<string> Capabilities { get; init; } = new HashSet<string>();

    /// <summary>Required, resolved device paths and/or globs (e.g. <c>/dev/ttyUSB*</c>).</summary>
    public IReadOnlySet<string> DevicePaths { get; init; } = new HashSet<string>();

    /// <summary>Required, resolved D-Bus socket-dir paths and/or globs (e.g. <c>/run/**</c>).</summary>
    public IReadOnlySet<string> DbusPaths { get; init; } = new HashSet<string>();

    /// <summary>Required, resolved read-only host-inspect paths and/or globs (e.g. <c>/sys/bus/usb</c>).
    /// Widening a resolved hostPath is an escalation, like <see cref="DevicePaths"/>/<see cref="DbusPaths"/>.</summary>
    public IReadOnlySet<string> HostPaths { get; init; } = new HashSet<string>();

    /// <summary>Required node-local hostPath persistence, or null when the workload declares none.</summary>
    public NodeLocalStateRequirement? NodeLocalState { get; init; }

    /// <summary>Normalized SHA-256 of the <c>unsupportedPodPatch</c> body, or null when there is none.</summary>
    public string? UnsupportedPodPatchDigest { get; init; }

    /// <summary>The image digest these requirements were derived from — read instead of the ref since a mirror
    /// rewrite changes the ref per registry. Purely informational: read by <c>Compare</c> for a note, never by <c>Allows</c>.</summary>
    public string? ImageDigest { get; init; }

    /// <summary>Requested volumes (informational — never part of the security gate).</summary>
    public IReadOnlyList<VolumeRequirement> Volumes { get; init; } = Array.Empty<VolumeRequirement>();

    /// <summary>Default D-Bus socket-dir path, auto-synthesized when the author does not declare <c>dbusHostPath</c>.</summary>
    public const string DefaultDbusHostPath = "/var/run/dbus";

    /// <summary>Reserved parameter name carrying the D-Bus host path when <c>hostDbus</c> is on.</summary>
    public const string DbusHostPathParameter = "dbusHostPath";
}
