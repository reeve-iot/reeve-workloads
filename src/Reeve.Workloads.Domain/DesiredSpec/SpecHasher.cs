using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Reeve.Workloads.Abstractions;
using Reeve.Workloads.Abstractions.Contracts;
using Reeve.Workloads.Abstractions.Contracts.Deployment;
using Reeve.Workloads.Abstractions.Domain;
using Reeve.Workloads.Abstractions.Domain.DesiredSpec;

namespace Reeve.Workloads.Domain.DesiredSpec;

/// <summary>The canonical <c>SpecHash</c>: SHA-256 over a deterministic serialization of only what changes what
/// materializes. Lowercase hex, no <c>sha256:</c> prefix, so it is a legal k8s label value.</summary>
public static class SpecHasher
{
    /// <summary>The canonical key the owning plugin type is hashed under. Its literal value is an input to every
    /// persisted spec hash and to every container label stamped from one, so it is declared, never derived.</summary>
    public const string GroupIdCanonicalKey = "pluginTypeId";

    /// <summary>A <see cref="ResolvedEnvKind.SpecHash"/> env is hashed by name only, so this may be called BEFORE
    /// the spec's own SpecHash is filled in. Returns lowercase hex, no prefix.</summary>
    public static string Compute(WorkloadDesiredSpec spec)
        => Compute(spec, null);

    /// <summary>Computes the hash and includes opaque parameter references while excluding their resolved values.</summary>
    public static string Compute(WorkloadDesiredSpec spec, IReadOnlyDictionary<string, string?>? parameterReferencesForHash)
    {
        ArgumentNullException.ThrowIfNull(spec);
        var canonical = Canonicalize(spec, parameterReferencesForHash);
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(canonical));
        return Convert.ToHexStringLower(bytes);
    }

    private static string Canonicalize(WorkloadDesiredSpec spec, IReadOnlyDictionary<string, string?>? parameterReferencesForHash)
    {
        var buffer = new System.Buffers.ArrayBufferWriter<byte>();
        using (var w = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = false }))
        {
            w.WriteStartObject();

            if (parameterReferencesForHash is { Count: > 0 })
            {
                w.WritePropertyName("parameterReferences");
                w.WriteStartObject();
                foreach (var reference in parameterReferencesForHash.OrderBy(pair => pair.Key, StringComparer.Ordinal))
                {
                    w.WriteString(reference.Key, reference.Value);
                }
                w.WriteEndObject();
            }

            w.WriteString(GroupIdCanonicalKey, spec.GroupId);
            w.WriteString("workloadId", spec.WorkloadId);
            w.WriteString("inst8", spec.Inst8);
            w.WriteString("provider", spec.Provider.Value);

            w.WriteString("image", spec.Image);
            WriteSortedStringArray(w, "platforms", spec.Platforms);

            w.WriteString("kind", KindWire(spec.Kind));

            w.WriteStartObject("nodeSelector");
            foreach (var kv in spec.NodeSelector.OrderBy(kv => kv.Key, StringComparer.Ordinal))
                w.WriteString(kv.Key, kv.Value);
            w.WriteEndObject();

            if (spec.Resources is { } r)
            {
                w.WriteStartObject("resources");
                WriteNullableString(w, "requestsCpu", r.RequestsCpu);
                WriteNullableString(w, "requestsMemory", r.RequestsMemory);
                WriteNullableString(w, "limitsCpu", r.LimitsCpu);
                WriteNullableString(w, "limitsMemory", r.LimitsMemory);
                w.WriteEndObject();
            }

            if (spec.Network is { } net)
            {
                w.WriteStartObject("network");
                w.WriteBoolean("hostNetwork", net.HostNetwork);
                w.WriteBoolean("mdns", net.Mdns);
                w.WriteBoolean("ipv6Required", net.Ipv6Required);

                if (net.Attachments.Count > 0)
                {
                    w.WriteStartArray("attachments");
                    foreach (var attachment in net.Attachments)
                        w.WriteStringValue(attachment);
                    w.WriteEndArray();
                }

                w.WriteStartArray("ports");
                foreach (var p in net.Ports.OrderBy(p => p.Name, StringComparer.Ordinal))
                {
                    w.WriteStartObject();
                    w.WriteString("name", p.Name);
                    w.WriteNumber("port", p.Port);
                    w.WriteString("protocol", p.Protocol);
                    if (p.HostPort is { } hp)
                        w.WriteNumber("hostPort", hp);
                    w.WriteEndObject();
                }
                w.WriteEndArray();
                w.WriteEndObject();
            }

            if (spec.Privileges is { } priv)
            {
                w.WriteStartObject("privileges");
                WriteSortedStringArray(w, "capabilities", priv.Capabilities);
                w.WriteBoolean("privileged", priv.Privileged);
                if (priv.DevicePassthroughPrivileged)
                    w.WriteBoolean("devicePassthroughPrivileged", true);
                w.WriteBoolean("runAsRoot", priv.RunAsRoot);
                if (priv.FsGroup is { } fsGroup)
                    w.WriteNumber("fsGroup", fsGroup);
                w.WriteBoolean("tunDevice", priv.TunDevice);
                WriteNullableString(w, "dbusHostPath", priv.DbusHostPath);
                WriteSortedStringArray(w, "devicePaths", priv.DevicePaths);
                if (priv.DeviceGroupIds.Count > 0)
                {
                    w.WriteStartArray("deviceGroupIds");
                    foreach (var gid in priv.DeviceGroupIds.OrderBy(g => g))
                        w.WriteNumberValue(gid);
                    w.WriteEndArray();
                }
                if (priv.HostPaths.Count > 0)
                {
                    w.WriteStartArray("hostPaths");
                    foreach (var hp in priv.HostPaths.OrderBy(h => h.MountPath, StringComparer.Ordinal).ThenBy(h => h.HostPath, StringComparer.Ordinal))
                    {
                        w.WriteStartObject();
                        w.WriteString("hostPath", hp.HostPath);
                        w.WriteString("mountPath", hp.MountPath);
                        w.WriteEndObject();
                    }
                    w.WriteEndArray();
                }
                if (priv.NodeLocalState is { } nls)
                {
                    w.WriteStartObject("nodeLocalState");
                    w.WriteString("hostPath", nls.HostPath);
                    w.WriteString("criticality", CriticalityWire(nls.Criticality));
                    w.WriteEndObject();
                }
                w.WriteEndObject();
            }

            if (spec.UserNamespace)
                w.WriteBoolean("userNamespace", true);

            w.WriteStartArray("volumes");
            foreach (var v in spec.Volumes.OrderBy(v => v.Id, StringComparer.Ordinal))
            {
                w.WriteStartObject();
                w.WriteString("id", v.Id);
                w.WriteString("mountPath", v.MountPath);
                w.WriteString("size", v.Size);
                w.WriteString("criticality", CriticalityWire(v.Criticality));
                w.WriteString("accessMode", v.AccessMode);
                w.WriteEndObject();
            }
            w.WriteEndArray();

            w.WriteStartArray("configMapMounts");
            foreach (var cm in spec.ConfigMapMounts.OrderBy(c => c.MountPath, StringComparer.Ordinal))
            {
                w.WriteStartObject();
                w.WriteString("mountPath", cm.MountPath);
                w.WriteString("configMapName", cm.ConfigMapName);
                w.WriteEndObject();
            }
            w.WriteEndArray();

            w.WriteStartObject("health");
            w.WriteString("mode", spec.Health.Mode.Value);
            w.WriteNumber("heartbeatSeconds", spec.Health.HeartbeatSeconds);
            if (spec.Health.Http is { } http)
            {
                w.WriteStartObject("http");
                w.WriteNumber("port", http.Port);
                WriteNullableString(w, "livenessPath", http.LivenessPath);
                WriteNullableString(w, "readinessPath", http.ReadinessPath);
                w.WriteEndObject();
            }
            w.WriteEndObject();

            if (spec.Observability?.Metrics is { } metrics)
            {
                w.WriteStartObject("observability");
                w.WriteStartObject("metrics");
                w.WriteNumber("port", metrics.Port);
                w.WriteString("path", metrics.Path);
                w.WriteEndObject();
                w.WriteEndObject();
            }

            w.WriteStartArray("env");
            foreach (var e in spec.Env.OrderBy(e => e.Name, StringComparer.Ordinal))
            {
                if (string.Equals(e.Name, WorkloadDesiredSpecFactory.AppVersionEnv, StringComparison.Ordinal))
                    continue;

                if (e.ExcludedFromSpecHash)
                    continue;

                w.WriteStartObject();
                w.WriteString("name", e.Name);
                switch (e.Kind)
                {
                    case ResolvedEnvKind.Literal:
                        w.WriteString("kind", "literal");
                        WriteNullableString(w, "value", e.Value);
                        break;
                    case ResolvedEnvKind.SecretRef:
                        w.WriteString("kind", "secretRef");
                        WriteNullableString(w, "secretRef", e.SecretRef);
                        break;
                    case ResolvedEnvKind.SpecHash:
                        w.WriteString("kind", "specHash");
                        break;
                }
                w.WriteEndObject();
            }
            w.WriteEndArray();

            if (spec.UnsupportedPodPatch is { } patch)
            {
                using var doc = JsonDocument.Parse(patch.PatchJson);
                w.WriteString("unsupportedPodPatch", WorkloadPatchDigest.Normalize(doc.RootElement));
            }

            // spec-hash-excluded: WorkloadDesiredSpec.GroupInstanceId -- the owning plugin instance is what the spec is looked up BY; nothing materialized takes its shape from it.
            // spec-hash-excluded: WorkloadDesiredSpec.InstanceId -- Inst8 is its first 8 hex chars and is hashed above; folding in both would only restate one identity twice.
            // spec-hash-excluded: WorkloadDesiredSpec.ResourceName -- the object's name, derived from identity that is already hashed. A rename is a migration, not drift.
            // spec-hash-excluded: WorkloadDesiredSpec.SecretName -- the per-instance Secret's name, derived from the same identity as ResourceName and never a rendering input of its own.
            // spec-hash-excluded: WorkloadDesiredSpec.Labels -- the label plane is rewritten on every apply from values that are themselves hashed; a label edit must not roll the pod.
            // spec-hash-excluded: WorkloadDesiredSpec.HostRequirements -- declarative and never machine-verified, so nothing materializes from it and a change to it cannot drift.
            // spec-hash-excluded: WorkloadDesiredSpec.RestartNonce -- an operator RESTART is not config drift; the k8s renderer stamps it separately, which is what makes a Restart actually roll.
            // spec-hash-excluded: WorkloadDesiredSpec.SecretMaterial -- secret VALUES. Hashing them would turn a token rotation into a rollout and put a digest of the material into every label.
            // spec-hash-excluded: WorkloadDesiredSpec.PendingScanRunId -- a SEPARATE gating axis the engine reads directly: Jobs are created iff it is non-null, which drift must not second-guess.
            // spec-hash-excluded: WorkloadDesiredSpec.ScanReadyNodeNames -- a live node set is a runtime input; a node rebooting would otherwise read as a configuration change.
            // spec-hash-excluded: WorkloadDesiredSpec.ScanIneligibleNodeNames -- a REPORT only. The fan-out renders ScanReadyNodeNames, and a node in neither list is simply not Ready.
            // spec-hash-excluded: WorkloadDesiredSpec.UserNamespaceSkippedForHostNetwork -- report only; rendered value is UserNamespace.
            // spec-hash-excluded: WorkloadDesiredSpec.UserNamespaceSkippedForDevicePassthrough -- report only; rendered value is UserNamespace.
            // spec-hash-excluded: WorkloadDesiredSpec.SpecHash -- self-referential; the hash cannot contain itself.
            // spec-hash-excluded: WorkloadDesiredSpec.ProvisionedCredentials -- provisioned outside spec.Env at apply.

            // spec-hash-excluded: ResolvedEnvVar.CredentialId -- which credential backs a SecretRef; the ref name is hashed, and rotating or renaming the id must not move the hash

            // spec-hash-excluded: ResolvedNetwork.Mode -- determined by the two fields that ARE hashed: the factory sets HostNetwork iff the mode is hostNetwork and fills Attachments only in operatorNetwork, and both renderers refuse every other pairing. A key here would move every hash in the fleet for no new fact.

            // spec-hash-excluded: ResolvedPrivileges.CallsBackendRest -- security-bearing but not a rendering input; it is read only as a privilege requirement.
            // spec-hash-excluded: ResolvedPrivileges.EffectivePrivileged -- derived from Privileged and DevicePassthroughPrivileged, both hashed above.
            // spec-hash-excluded: ResolvedPrivileges.JoinsDeviceGroups -- the requirement FACT that a slot bound a device. The GIDs it produces are the rendering input, and those are hashed above.

            // spec-hash-excluded: ResolvedVolume.ResourceName -- on a restore this is the RETAINED physical claim name; a rebind of the name must not roll the pod.
            // spec-hash-excluded: ResolvedVolume.ExistingClaim -- informational: whether the claim was adopted or created changes nothing in the rendered pod.
            // spec-hash-excluded: ResolvedVolume.StorageClassName -- carried for CREATE only, so a default-class change must never poison drift.

            w.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    private static void WriteSortedStringArray(Utf8JsonWriter w, string name, IReadOnlyList<string> values)
    {
        w.WriteStartArray(name);
        foreach (var v in values.OrderBy(x => x, StringComparer.Ordinal))
            w.WriteStringValue(v);
        w.WriteEndArray();
    }

    private static void WriteNullableString(Utf8JsonWriter w, string name, string? value)
    {
        if (value is null)
            w.WriteNull(name);
        else
            w.WriteString(name, value);
    }

    private static string KindWire(WorkloadKind kind) => kind switch
    {
        WorkloadKind.Service => "service",
        WorkloadKind.SingleNode => "single-node",
        WorkloadKind.PerNode => "per-node",
        WorkloadKind.ScanPerNode => "scan-per-node",
        _ => kind.ToString(),
    };

    private static string CriticalityWire(VolumeCriticality c) => c switch
    {
        VolumeCriticality.Cache => "cache",
        VolumeCriticality.Recoverable => "recoverable",
        VolumeCriticality.Critical => "critical",
        _ => c.ToString(),
    };
}
