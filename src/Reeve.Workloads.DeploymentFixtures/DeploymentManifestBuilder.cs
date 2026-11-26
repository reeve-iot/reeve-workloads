using System.Text.Json;
using Reeve.Workloads.Abstractions.Contracts.Deployment;

namespace Reeve.Workloads.DeploymentFixtures;

/// <summary>Pure, deterministic builders for the <see cref="Reeve.Workloads.Abstractions.Domain.WorkloadDeploymentDiff.Compute"/>
/// inputs. The default image digest is fixed, so an "unchanged" deployment is genuinely unchanged.</summary>
public static class DeploymentManifestBuilder
{
    /// <summary>A deterministic <c>sha256:</c> digest filled with the given character (e.g. <c>Digest('a')</c>).</summary>
    public static string Digest(char c) => "sha256:" + new string(c, 64);

    /// <summary>The default workload image digest (a fixed <c>sha256:aaa…</c>) — so an unchanged workload is unchanged.</summary>
    public static string DefaultDigest => Digest('a');

    /// <summary>A <see cref="WorkloadManifest"/> with non-privileged defaults; every
    /// security/protocol/parameter/volume axis is overridable.</summary>
    public static WorkloadManifest Workload(
        string id = "w",
        string? digest = null,
        WorkloadKind kind = WorkloadKind.SingleNode,
        WorkloadNetwork? network = null,
        WorkloadPrivileges? privileges = null,
        IReadOnlyList<WorkloadVolume>? volumes = null,
        UnsupportedPodPatch? patch = null,
        WorkloadExtensions? extensions = null,
        IReadOnlyList<WorkloadParameter>? parameters = null)
        => new()
        {
            Id = id,
            DisplayName = id.ToUpperInvariant(),
            Image = new WorkloadImage { Ref = "reg/img", Digest = digest ?? DefaultDigest },
            Kind = kind,
            Network = network,
            Privileges = privileges,
            Volumes = volumes ?? Array.Empty<WorkloadVolume>(),
            Health = new WorkloadHealth { Mode = WorkloadHealthMode.Container },
            Extensions = extensions ?? WorkloadExtensions.None,
            Parameters = parameters ?? Array.Empty<WorkloadParameter>(),
            UnsupportedPodPatch = patch,
        };

    /// <summary>Host-extension data from raw JSON, so a fixture can carry a host's namespace without the library
    /// corpus learning its vocabulary. The object is <c>{"&lt;namespace&gt;": { … }}</c>, exactly as authored.</summary>
    public static WorkloadExtensions Extensions(string json)
        => new()
        {
            ByNamespace = JsonDocument.Parse(json).RootElement.EnumerateObject()
                .ToDictionary(p => p.Name, p => p.Value.Clone(), StringComparer.Ordinal),
        };

    /// <summary>Builds a <see cref="DeploymentManifest"/> (schemaVersion 1) over the given workloads.</summary>
    public static DeploymentManifest Manifest(params WorkloadManifest[] workloads)
        => new() { SchemaVersion = 1, Workloads = workloads };

    /// <summary>A <see cref="WorkloadParameter"/>, defaulting to <see cref="WorkloadParameterType.Text"/>;
    /// a <c>network.attachments[].parameter</c> slot must bind to a <c>NetworkAttachment</c>-typed one.</summary>
    public static WorkloadParameter Param(
        string name,
        bool required,
        string? @default = null,
        WorkloadParameterType type = WorkloadParameterType.Text)
        => new() { Name = name, Type = type, Required = required, Default = @default };

    /// <summary>A <see cref="WorkloadNetwork"/> declaration. Attachment order is significant and never
    /// sorted, and every slot must also be declared as a <c>NetworkAttachment</c> parameter.</summary>
    public static WorkloadNetwork Network(
        bool hostNetwork = false,
        IReadOnlyList<string>? attachmentParameters = null)
        => new()
        {
            HostNetwork = hostNetwork,
            Attachments = (attachmentParameters ?? Array.Empty<string>())
                .Select(p => new WorkloadNetworkAttachmentSlot { Parameter = p })
                .ToArray(),
        };

    /// <summary>A <see cref="WorkloadVolume"/> with an explicit id/mount/size/criticality (defaults to Cache).</summary>
    public static WorkloadVolume Volume(
        string id,
        string size,
        string? mountPath = null,
        VolumeCriticality criticality = VolumeCriticality.Cache)
        => new()
        {
            Id = id,
            MountPath = mountPath ?? "/" + id,
            Size = size,
            Criticality = criticality,
        };

    /// <summary>An <see cref="UnsupportedPodPatch"/> from a raw JSON Patch array. Two different bodies
    /// yield different normalized digests, which drives the patch-digest escalation case.</summary>
    public static UnsupportedPodPatch Patch(string rawJsonArray)
    {
        using var doc = JsonDocument.Parse(rawJsonArray);
        var body = doc.RootElement.Clone();
        return new UnsupportedPodPatch { RawJson = body.GetRawText(), Body = body };
    }
}
