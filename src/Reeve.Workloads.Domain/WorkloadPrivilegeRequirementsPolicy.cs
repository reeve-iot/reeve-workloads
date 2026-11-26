using Reeve.Workloads.Abstractions;
using Reeve.Workloads.Abstractions.Contracts;
using Reeve.Workloads.Abstractions.Contracts.Deployment;
using Reeve.Workloads.Abstractions.Domain;
using Reeve.Workloads.Abstractions.Domain.DesiredSpec;
using Reeve.Workloads.Abstractions.Domain.Network;
using Reeve.Workloads.Abstractions.Providers;
using Reeve.Workloads.Domain.DesiredSpec;
using Reeve.Workloads.Domain.Network;

namespace Reeve.Workloads.Domain;

/// <summary>The rules over a <see cref="WorkloadPrivilegeRequirements"/>: what it covers, how two compare, and how one is derived
/// from a workload or a desired spec. The record is vocabulary the ports carry; these rules are the domain's.</summary>
public static class WorkloadPrivilegeRequirementsPolicy
{
    extension(WorkloadPrivilegeRequirements profile)
    {
        public bool Covers(WorkloadPrivilegeRequirements desired, out IReadOnlyList<string> violations)
        {
            ArgumentNullException.ThrowIfNull(desired);
            var reasons = new List<string>();

            if (desired.Kind != profile.Kind)
                reasons.Add($"{nameof(WorkloadPrivilegeRequirements.Kind)}: desired '{ToWire(desired.Kind)}' != covered '{ToWire(profile.Kind)}' (kind must match).");

            CheckMode(reasons, desired.NetworkMode, profile.NetworkMode);

            CheckBool(reasons, nameof(profile.HostNetwork), desired.HostNetwork, profile.HostNetwork);
            CheckBool(reasons, nameof(profile.Mdns), desired.Mdns, profile.Mdns);
            CheckBool(reasons, nameof(profile.Ipv6Required), desired.Ipv6Required, profile.Ipv6Required);
            CheckBool(reasons, nameof(profile.Privileged), desired.Privileged, profile.Privileged);
            CheckBool(reasons, nameof(profile.RunAsRoot), desired.RunAsRoot, profile.RunAsRoot);
            CheckBool(reasons, nameof(profile.HostDbus), desired.HostDbus, profile.HostDbus);
            CheckBool(reasons, nameof(profile.TunDevice), desired.TunDevice, profile.TunDevice);
            CheckBool(reasons, nameof(profile.JoinsDeviceGroups), desired.JoinsDeviceGroups, profile.JoinsDeviceGroups);
            CheckBool(reasons, nameof(profile.CallsBackendRest), desired.CallsBackendRest, profile.CallsBackendRest);

            if (desired.FsGroup is { } wantedGroup && profile.FsGroup != wantedGroup)
            {
                reasons.Add(
                    $"{nameof(WorkloadPrivilegeRequirements.FsGroup)}: desired {wantedGroup} != covered {DescribeGroup(profile.FsGroup)}.");
            }

            foreach (var cap in desired.Capabilities.OrderBy(c => c, StringComparer.Ordinal))
            {
                if (!profile.Capabilities.Contains(cap))
                    reasons.Add($"{nameof(WorkloadPrivilegeRequirements.Capabilities)}: desired '{cap}' is not covered.");
            }

            foreach (var path in desired.DevicePaths.OrderBy(p => p, StringComparer.Ordinal))
            {
                if (!PathCovered(profile.DevicePaths, path))
                    reasons.Add($"{nameof(WorkloadPrivilegeRequirements.DevicePaths)}: desired '{path}' matches no covered path or glob.");
            }
            foreach (var path in desired.DbusPaths.OrderBy(p => p, StringComparer.Ordinal))
            {
                if (!PathCovered(profile.DbusPaths, path))
                    reasons.Add($"{nameof(WorkloadPrivilegeRequirements.DbusPaths)}: desired '{path}' matches no covered path or glob.");
            }
            foreach (var path in desired.HostPaths.OrderBy(p => p, StringComparer.Ordinal))
            {
                if (!PathCovered(profile.HostPaths, path))
                    reasons.Add($"{nameof(WorkloadPrivilegeRequirements.HostPaths)}: desired '{path}' matches no covered path or glob.");
            }

            if (desired.NodeLocalState is not null && profile.NodeLocalState is null)
            {
                reasons.Add($"{nameof(WorkloadPrivilegeRequirements.NodeLocalState)}: desired requests node-local state but none is covered.");
            }

            if (!string.Equals(desired.UnsupportedPodPatchDigest, profile.UnsupportedPodPatchDigest, StringComparison.Ordinal))
            {
                reasons.Add(
                    $"Patch: desired unsupportedPodPatch digest '{desired.UnsupportedPodPatchDigest ?? "(none)"}' " +
                    $"!= covered '{profile.UnsupportedPodPatchDigest ?? "(none)"}'.");
            }

            violations = reasons;
            return reasons.Count == 0;
        }

        public bool IsSupersetOf(WorkloadPrivilegeRequirements desired, out IReadOnlyList<string> violations)
            => profile.Covers(desired, out violations);

        public string Fingerprint()
        {
            var buffer = new System.IO.MemoryStream();
            using (var writer = new System.Text.Json.Utf8JsonWriter(buffer))
            {
                writer.WriteStartObject();
                writer.WriteString("kind", ToWire(profile.Kind));
                writer.WriteString("networkMode", ToWire(profile.NetworkMode));
                writer.WriteBoolean("hostNetwork", profile.HostNetwork);
                writer.WriteBoolean("mdns", profile.Mdns);
                writer.WriteBoolean("ipv6Required", profile.Ipv6Required);
                writer.WriteBoolean("privileged", profile.Privileged);
                writer.WriteBoolean("runAsRoot", profile.RunAsRoot);
                writer.WriteBoolean("hostDbus", profile.HostDbus);
                writer.WriteBoolean("tunDevice", profile.TunDevice);
                writer.WriteBoolean("joinsDeviceGroups", profile.JoinsDeviceGroups);
                writer.WriteBoolean("callsBackendRest", profile.CallsBackendRest);
                if (profile.FsGroup is { } fsGroup)
                    writer.WriteNumber("fsGroup", fsGroup);
                WriteSortedSet(writer, "capabilities", profile.Capabilities);
                WriteSortedSet(writer, "devicePaths", profile.DevicePaths);
                WriteSortedSet(writer, "dbusPaths", profile.DbusPaths);
                WriteSortedSet(writer, "hostPaths", profile.HostPaths);
                if (profile.NodeLocalState is { } nodeLocalState)
                {
                    writer.WriteString("nodeLocalState", ToWire(nodeLocalState.Criticality));
                }
                else
                {
                    writer.WriteNull("nodeLocalState");
                }

                writer.WriteString("unsupportedPodPatchDigest", profile.UnsupportedPodPatchDigest);
                writer.WriteEndObject();
            }

            return Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(buffer.ToArray()));
        }
    }

    /// <summary>The fingerprint of a whole deployment: SHA-256 over <c>workloadId=fingerprint</c> lines in ordinal
    /// order, so the same requirements on a different workload are a different deployment.</summary>
    public static string Fingerprint(IReadOnlyDictionary<string, WorkloadPrivilegeRequirements> byWorkload)
    {
        ArgumentNullException.ThrowIfNull(byWorkload);
        var lines = byWorkload
            .OrderBy(entry => entry.Key, StringComparer.Ordinal)
            .Select(entry => $"{entry.Key}={entry.Value.Fingerprint()}\n");
        return Convert.ToHexStringLower(
            System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(string.Concat(lines))));
    }

    private static void WriteSortedSet(System.Text.Json.Utf8JsonWriter writer, string name, IReadOnlySet<string> values)
    {
        writer.WriteStartArray(name);
        foreach (var value in values.Order(StringComparer.Ordinal))
        {
            writer.WriteStringValue(value);
        }

        writer.WriteEndArray();
    }

    extension(WorkloadPrivilegeRequirements)
    {
        public static WorkloadPrivilegeRequirementsDiff Compare(WorkloadPrivilegeRequirements old, WorkloadPrivilegeRequirements @new)
        {
            ArgumentNullException.ThrowIfNull(old);
            ArgumentNullException.ThrowIfNull(@new);

            var escalated = new List<string>();
            var narrowed = new List<string>();

            if (old.Kind != @new.Kind)
                escalated.Add($"{nameof(WorkloadPrivilegeRequirements.Kind)}: '{ToWire(old.Kind)}' → '{ToWire(@new.Kind)}'");

            var modeChanged = old.NetworkMode != @new.NetworkMode;
            if (modeChanged)
            {
                var entry = $"{nameof(WorkloadPrivilegeRequirements.NetworkMode)}: '{ToWire(old.NetworkMode)}' → '{ToWire(@new.NetworkMode)}'";
                if (@new.NetworkMode == WorkloadNetworkMode.Cluster)
                    narrowed.Add(entry);
                else
                    escalated.Add(entry);
            }

            if (!modeChanged)
                DiffBool(escalated, narrowed, nameof(WorkloadPrivilegeRequirements.HostNetwork), old.HostNetwork, @new.HostNetwork);
            DiffBool(escalated, narrowed, nameof(WorkloadPrivilegeRequirements.Mdns), old.Mdns, @new.Mdns);
            DiffBool(escalated, narrowed, nameof(WorkloadPrivilegeRequirements.Ipv6Required), old.Ipv6Required, @new.Ipv6Required);
            DiffBool(escalated, narrowed, nameof(WorkloadPrivilegeRequirements.Privileged), old.Privileged, @new.Privileged);
            DiffBool(escalated, narrowed, nameof(WorkloadPrivilegeRequirements.RunAsRoot), old.RunAsRoot, @new.RunAsRoot);
            DiffBool(escalated, narrowed, nameof(WorkloadPrivilegeRequirements.HostDbus), old.HostDbus, @new.HostDbus);
            DiffBool(escalated, narrowed, nameof(WorkloadPrivilegeRequirements.TunDevice), old.TunDevice, @new.TunDevice);
            DiffBool(escalated, narrowed, nameof(WorkloadPrivilegeRequirements.JoinsDeviceGroups), old.JoinsDeviceGroups, @new.JoinsDeviceGroups);
            DiffBool(escalated, narrowed, nameof(WorkloadPrivilegeRequirements.CallsBackendRest), old.CallsBackendRest, @new.CallsBackendRest);

            if (old.FsGroup != @new.FsGroup)
            {
                var entry = $"{nameof(WorkloadPrivilegeRequirements.FsGroup)}: {DescribeGroup(old.FsGroup)} → {DescribeGroup(@new.FsGroup)}";
                if (@new.FsGroup is null)
                    narrowed.Add(entry);
                else
                    escalated.Add(entry);
            }

            DiffSet(escalated, narrowed, nameof(WorkloadPrivilegeRequirements.Capabilities), old.Capabilities, @new.Capabilities);
            DiffSet(escalated, narrowed, nameof(WorkloadPrivilegeRequirements.DevicePaths), old.DevicePaths, @new.DevicePaths);
            DiffSet(escalated, narrowed, nameof(WorkloadPrivilegeRequirements.DbusPaths), old.DbusPaths, @new.DbusPaths);
            DiffSet(escalated, narrowed, nameof(WorkloadPrivilegeRequirements.HostPaths), old.HostPaths, @new.HostPaths);

            var infoNotes = new List<string>();
            DiffNodeLocalState(escalated, narrowed, infoNotes, old.NodeLocalState, @new.NodeLocalState);

            if (!string.Equals(old.UnsupportedPodPatchDigest, @new.UnsupportedPodPatchDigest, StringComparison.Ordinal))
            {
                escalated.Add(
                    $"Patch: unsupportedPodPatch digest '{old.UnsupportedPodPatchDigest ?? "(none)"}' → " +
                    $"'{@new.UnsupportedPodPatchDigest ?? "(none)"}'");
            }

            if (!string.Equals(old.ImageDigest, @new.ImageDigest, StringComparison.Ordinal))
            {
                infoNotes.Add(
                    $"Image: digest '{DescribeDigest(old.ImageDigest)}' → '{DescribeDigest(@new.ImageDigest)}'");
            }

            var security = escalated.Count > 0
                ? RequirementChangeKind.Escalation
                : narrowed.Count > 0 ? RequirementChangeKind.Narrowing : RequirementChangeKind.Unchanged;

            infoNotes.InsertRange(0, DiffVolumes(old.Volumes, @new.Volumes));

            return new WorkloadPrivilegeRequirementsDiff
            {
                Security = security,
                EscalatedFields = escalated,
                NarrowedFields = narrowed,
                InformationalVolumeChange = infoNotes.Count > 0,
                VolumeNotes = infoNotes,
            };
        }

        /// <summary>The requirements of <paramref name="workload"/> on <paramref name="provider"/>: what the manifest declares,
        /// plus what the provider needs to honour it. On a provider whose device passthrough requires privileged mode, a
        /// workload that binds a device path is privileged. These are the requirements a host judges before a write; they
        /// agree with <c>FromDesiredSpec</c> over the spec the same inputs render.</summary>
        public static WorkloadPrivilegeRequirements FromWorkload(
            WorkloadManifest workload, IReadOnlyDictionary<string, string> resolvedParameters, WorkloadProviderDescriptor provider)
        {
            ArgumentNullException.ThrowIfNull(provider);
            var declared = WorkloadPrivilegeRequirements.FromWorkload(workload, resolvedParameters);
            return declared with
            {
                Privileged = declared.Privileged || WorkloadDevicePassthrough.NeedsPrivileged(provider, declared.DevicePaths.Count),
            };
        }

        /// <summary>What <paramref name="workload"/> declares, independent of any provider. A provider can require more to
        /// honour it (see the overload taking a <see cref="WorkloadProviderDescriptor"/>), so judge a write with that one.</summary>
        public static WorkloadPrivilegeRequirements FromWorkload(WorkloadManifest workload, IReadOnlyDictionary<string, string> resolvedParameters)
        {
            ArgumentNullException.ThrowIfNull(workload);
            ArgumentNullException.ThrowIfNull(resolvedParameters);

            var network = workload.Network;
            var privileges = workload.Privileges;

            var devicePaths = new HashSet<string>(StringComparer.Ordinal);
            var joinsDeviceGroups = false;
            if (privileges is not null)
            {
                foreach (var device in privileges.Devices)
                {
                    if (string.IsNullOrEmpty(device.Parameter))
                        continue;

                    IEnumerable<string> keys = device.MaxCount > 1
                        ? Enumerable.Range(0, device.MaxCount).Select(i => WorkloadParameterResolver.DeviceIndexKey(device.Parameter, i))
                        : new[] { device.Parameter };

                    foreach (var key in keys)
                    {
                        if (!resolvedParameters.TryGetValue(key, out var value) || string.IsNullOrWhiteSpace(value))
                            continue;

                        devicePaths.Add(value.Trim());

                        if (device.Group == WorkloadDeviceGroup.Auto)
                            joinsDeviceGroups = true;
                    }
                }
            }

            var hostPaths = new HashSet<string>(StringComparer.Ordinal);
            if (privileges is not null)
            {
                foreach (var hp in privileges.HostPaths)
                {
                    if (resolvedParameters.TryGetValue(hp.Parameter, out var value) && !string.IsNullOrWhiteSpace(value))
                        hostPaths.Add(value.Trim());
                }
            }

            var dbusPaths = new HashSet<string>(StringComparer.Ordinal);
            if (privileges?.HostDbus == true)
            {
                var dbusPath = resolvedParameters.TryGetValue(WorkloadPrivilegeRequirements.DbusHostPathParameter, out var value) && !string.IsNullOrWhiteSpace(value)
                    ? value.Trim()
                    : WorkloadPrivilegeRequirements.DefaultDbusHostPath;
                dbusPaths.Add(dbusPath);
            }

            var capabilities = new HashSet<string>(StringComparer.Ordinal);
            if (privileges is not null)
            {
                foreach (var cap in privileges.Capabilities)
                    capabilities.Add(ToWire(cap));
            }

            NodeLocalStateRequirement? nodeLocalState = privileges?.NodeLocalState is { } nls
                ? new NodeLocalStateRequirement { Criticality = nls.Criticality }
                : null;

            var volumes = workload.Volumes
                .Select(v => new VolumeRequirement { Id = v.Id, Size = v.Size, Criticality = v.Criticality })
                .ToList();

            var mode = DeriveMode(network, resolvedParameters);

            return new WorkloadPrivilegeRequirements
            {
                Kind = workload.Kind,
                HostNetwork = mode == WorkloadNetworkMode.HostNetwork,
                NetworkMode = mode,
                Mdns = network?.Mdns ?? false,
                Ipv6Required = network?.Ipv6Required ?? false,
                Privileged = privileges?.Privileged ?? false,
                RunAsRoot = privileges?.RunAsRoot ?? false,
                HostDbus = privileges?.HostDbus ?? false,
                TunDevice = privileges?.TunDevice ?? false,
                JoinsDeviceGroups = joinsDeviceGroups,
                CallsBackendRest = privileges?.CallsBackendRest ?? false,
                FsGroup = privileges?.FsGroup,
                Capabilities = capabilities,
                DevicePaths = devicePaths,
                DbusPaths = dbusPaths,
                HostPaths = hostPaths,
                NodeLocalState = nodeLocalState,
                UnsupportedPodPatchDigest = WorkloadPatchDigest.Normalize(workload.UnsupportedPodPatch),
                ImageDigest = NullIfBlank(workload.Image?.Digest),
                Volumes = volumes,
            };
        }

        public static WorkloadPrivilegeRequirements FromDesiredSpec(Reeve.Workloads.Abstractions.Domain.DesiredSpec.WorkloadDesiredSpec spec)
        {
            ArgumentNullException.ThrowIfNull(spec);

            var network = spec.Network;
            var privileges = spec.Privileges;

            var capabilities = new HashSet<string>(privileges?.Capabilities ?? [], StringComparer.Ordinal);
            var devicePaths = new HashSet<string>(privileges?.DevicePaths ?? [], StringComparer.Ordinal);

            var hostPaths = new HashSet<string>(StringComparer.Ordinal);
            foreach (var hp in privileges?.HostPaths ?? [])
            {
                if (!string.IsNullOrWhiteSpace(hp.HostPath))
                    hostPaths.Add(hp.HostPath.Trim());
            }

            var dbusPaths = new HashSet<string>(StringComparer.Ordinal);
            if (!string.IsNullOrWhiteSpace(privileges?.DbusHostPath))
            {
                dbusPaths.Add(privileges!.DbusHostPath!.Trim());
            }

            NodeLocalStateRequirement? nodeLocalState = privileges?.NodeLocalState is { } nls
                ? new NodeLocalStateRequirement { Criticality = nls.Criticality }
                : null;

            var volumes = spec.Volumes
                .Select(v => new VolumeRequirement { Id = v.Id, Size = v.Size, Criticality = v.Criticality })
                .ToList();

            string? patchDigest = null;
            if (spec.UnsupportedPodPatch is { } patch && !string.IsNullOrWhiteSpace(patch.PatchJson))
            {
                using var doc = System.Text.Json.JsonDocument.Parse(patch.PatchJson);
                patchDigest = WorkloadPatchDigest.Normalize(doc.RootElement);
            }

            return new WorkloadPrivilegeRequirements
            {
                Kind = spec.Kind,
                HostNetwork = network?.HostNetwork ?? false,
                NetworkMode = network?.Mode ?? WorkloadNetworkMode.Cluster,
                Mdns = network?.Mdns ?? false,
                Ipv6Required = network?.Ipv6Required ?? false,
                Privileged = privileges?.EffectivePrivileged ?? false,
                RunAsRoot = privileges?.RunAsRoot ?? false,
                HostDbus = !string.IsNullOrWhiteSpace(privileges?.DbusHostPath),
                TunDevice = privileges?.TunDevice ?? false,
                JoinsDeviceGroups = privileges?.JoinsDeviceGroups ?? false,
                CallsBackendRest = privileges?.CallsBackendRest ?? false,
                FsGroup = privileges?.FsGroup,
                Capabilities = capabilities,
                DevicePaths = devicePaths,
                DbusPaths = dbusPaths,
                HostPaths = hostPaths,
                NodeLocalState = nodeLocalState,
                UnsupportedPodPatchDigest = patchDigest,
                ImageDigest = DigestFromImageReference(spec.Image),
                Volumes = volumes,
            };
        }
    }

    private static string DescribeDigest(string? digest)
        => string.IsNullOrWhiteSpace(digest) ? "(none)"
            : digest.Length <= 19 ? digest : digest[..19] + "…";

    private static string? NullIfBlank(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string? DigestFromImageReference(string? image)
    {
        if (string.IsNullOrWhiteSpace(image))
        {
            return null;
        }

        var at = image.LastIndexOf('@');
        return at < 0 || at == image.Length - 1 ? null : image[(at + 1)..].Trim();
    }

    private static void CheckMode(List<string> reasons, WorkloadNetworkMode desired, WorkloadNetworkMode covered)
    {
        if (desired == WorkloadNetworkMode.Cluster || desired == covered)
            return;

        reasons.Add(
            $"{nameof(WorkloadPrivilegeRequirements.NetworkMode)}: desired '{ToWire(desired)}' is not covered by '{ToWire(covered)}' "
            + $"(only '{WorkloadNetworkAttachmentGrammar.ClusterMode}' narrows; every other move escalates).");
    }

    private static void CheckBool(List<string> reasons, string field, bool desired, bool covered)
    {
        if (desired && !covered)
            reasons.Add($"{field}: desired requires this privilege/flag but it is not covered.");
    }

    private static readonly char[] GlobMetacharacters = ['*', '?'];

    private static bool PathCovered(IReadOnlySet<string> coveredPaths, string path)
    {
        if (coveredPaths.Contains(path))
            return true;

        if (path.IndexOfAny(GlobMetacharacters) >= 0)
            return false;

        foreach (var pattern in coveredPaths)
        {
            if (WorkloadPathGlob.IsMatch(pattern, path))
                return true;
        }
        return false;
    }

    private static string DescribeGroup(int? group)
        => group is { } gid ? gid.ToString(System.Globalization.CultureInfo.InvariantCulture) : "(none)";

    private static void DiffBool(List<string> escalated, List<string> narrowed, string field, bool old, bool @new)
    {
        if (@new == old)
            return;
        if (@new && !old)
            escalated.Add($"{field}: false → true");
        else
            narrowed.Add($"{field}: true → false");
    }

    private static void DiffSet(List<string> escalated, List<string> narrowed, string field,
        IReadOnlySet<string> old, IReadOnlySet<string> @new)
    {
        var added = @new.Where(x => !old.Contains(x)).OrderBy(x => x, StringComparer.Ordinal).ToList();
        var removed = old.Where(x => !@new.Contains(x)).OrderBy(x => x, StringComparer.Ordinal).ToList();

        if (added.Count > 0)
            escalated.Add($"{field}: added {string.Join(", ", added)}");
        if (removed.Count > 0)
            narrowed.Add($"{field}: removed {string.Join(", ", removed)}");
    }

    private static void DiffNodeLocalState(List<string> escalated, List<string> narrowed, List<string> infoNotes,
        NodeLocalStateRequirement? old, NodeLocalStateRequirement? @new)
    {
        var oldPresent = old is not null;
        var newPresent = @new is not null;

        if (!oldPresent && newPresent)
        {
            escalated.Add($"{nameof(WorkloadPrivilegeRequirements.NodeLocalState)}: absent → present ({ToWire(@new!.Criticality)})");
            return;
        }
        if (oldPresent && !newPresent)
        {
            narrowed.Add($"{nameof(WorkloadPrivilegeRequirements.NodeLocalState)}: present → absent");
            return;
        }

        if (oldPresent && newPresent && old!.Criticality != @new!.Criticality)
        {
            infoNotes.Add(
                $"nodeLocalState criticality {ToWire(old.Criticality)} → {ToWire(@new.Criticality)}");
        }
    }

    private static IReadOnlyList<string> DiffVolumes(IReadOnlyList<VolumeRequirement> old, IReadOnlyList<VolumeRequirement> @new)
    {
        var notes = new List<string>();
        var oldById = LastWinsById(old);
        var newById = LastWinsById(@new);

        foreach (var id in newById.Keys.Where(k => !oldById.ContainsKey(k)).OrderBy(k => k, StringComparer.Ordinal))
            notes.Add($"volume '{id}' added ({newById[id].Size}, {ToWire(newById[id].Criticality)})");

        foreach (var id in oldById.Keys.Where(k => !newById.ContainsKey(k)).OrderBy(k => k, StringComparer.Ordinal))
            notes.Add($"volume '{id}' removed");

        foreach (var id in newById.Keys.Where(oldById.ContainsKey).OrderBy(k => k, StringComparer.Ordinal))
        {
            var before = oldById[id];
            var after = newById[id];
            if (!string.Equals(before.Size, after.Size, StringComparison.Ordinal))
                notes.Add($"volume '{id}' size {before.Size} → {after.Size}");
            if (before.Criticality != after.Criticality)
                notes.Add($"volume '{id}' criticality {ToWire(before.Criticality)} → {ToWire(after.Criticality)}");
        }

        return notes;
    }

    private static Dictionary<string, VolumeRequirement> LastWinsById(IReadOnlyList<VolumeRequirement> volumes)
    {
        var byId = new Dictionary<string, VolumeRequirement>(StringComparer.Ordinal);
        foreach (var volume in volumes)
            byId[volume.Id] = volume;
        return byId;
    }

    private static WorkloadNetworkMode DeriveMode(
        WorkloadNetwork? network, IReadOnlyDictionary<string, string> resolvedParameters)
    {
        var declared = network?.HostNetwork == true
            ? WorkloadNetworkMode.HostNetwork
            : WorkloadNetworkMode.Cluster;

        return resolvedParameters.TryGetValue(WorkloadNetworkAttachmentGrammar.NetworkModeParameter, out var raw)
               && WorkloadNetworkAttachmentGrammar.ParseMode(raw?.Trim()) is { } parsed
            ? parsed
            : declared;
    }

    private static string ToWire(WorkloadKind kind) => kind switch
    {
        WorkloadKind.Service => "service",
        WorkloadKind.SingleNode => "single-node",
        WorkloadKind.PerNode => "per-node",
        WorkloadKind.ScanPerNode => "scan-per-node",
        _ => kind.ToString(),
    };

    private static string ToWire(WorkloadNetworkMode mode) => mode switch
    {
        WorkloadNetworkMode.Cluster => WorkloadNetworkAttachmentGrammar.ClusterMode,
        WorkloadNetworkMode.HostNetwork => WorkloadNetworkAttachmentGrammar.HostNetworkMode,
        WorkloadNetworkMode.OperatorNetwork => WorkloadNetworkAttachmentGrammar.OperatorNetworkMode,
        _ => mode.ToString(),
    };

    private static string ToWire(VolumeCriticality criticality) => criticality switch
    {
        VolumeCriticality.Cache => "cache",
        VolumeCriticality.Recoverable => "recoverable",
        VolumeCriticality.Critical => "critical",
        _ => criticality.ToString(),
    };

    private static string ToWire(WorkloadCapability capability) => capability switch
    {
        WorkloadCapability.NetAdmin => "NET_ADMIN",
        WorkloadCapability.NetRaw => "NET_RAW",
        WorkloadCapability.SysAdmin => "SYS_ADMIN",
        WorkloadCapability.NetBindService => "NET_BIND_SERVICE",
        _ => capability.ToString(),
    };
}
