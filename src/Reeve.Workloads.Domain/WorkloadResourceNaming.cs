using System;
using System.Collections.Generic;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Reeve.Workloads.Abstractions;
using Reeve.Workloads.Abstractions.Contracts;
using Reeve.Workloads.Abstractions.Domain;
using Reeve.Workloads.Abstractions.Domain.Entities;
using Reeve.Workloads.Abstractions.Providers;

namespace Reeve.Workloads.Domain;

/// <summary>The GRAMMAR every resource name is built from: the sanitiser, the length ceiling, the deterministic
/// hash suffix, and the composition that turns segments into an object name.</summary>
/// <remarks>
/// What a given provider names, and what it calls the result, is an <see cref="IWorkloadResourceNamer"/>. The grammar itself stays here, in one copy: these names are persisted and
/// they address live objects, so two implementations of the same sanitiser is two ways to drift away from the
/// names already on disk.
/// </remarks>
public static class WorkloadResourceNaming
{
    /// <summary>DNS label length ceiling (RFC 1035), which is the tightest limit any shipped provider imposes —
    /// a Kubernetes Service name and a label VALUE both stop here.</summary>
    public const int Dns1123MaxLength = 63;

    /// <summary>The prefix every composed object name carries. Public because an ADOPTED claim's scheme can only
    /// be inferred from the shape of its name, and this prefix is the evidence.</summary>
    public const string ObjectNamePrefix = "reeve-wl";

    private const int HashSuffixLength = 6;

    private const string DockerSchemeVolumePrefix = "reeve";

    /// <summary>Composes an object name: <c>reeve-wl-{segments…}</c>, truncated to the DNS ceiling with a
    /// deterministic hash suffix when it does not fit. Segments are expected already sanitised.</summary>
    public static string ComposeObjectName(params string[] segments)
    {
        ArgumentNullException.ThrowIfNull(segments);
        return TruncateForDns(ObjectNamePrefix + "-" + string.Join('-', segments));
    }

    /// <summary>The volume name of the <see cref="RetainedVolumeSchemeId.Docker"/> scheme:
    /// <c>reeve-{pluginTypeId}-{volumeId}-{inst8}</c>, with no <c>reeve-wl</c> prefix and no workload id.</summary>
    /// <remarks>Two namers answer with it: the Docker provider's, whose named volumes on operators' machines already
    /// carry these names, and the manual provider's, whose Compose artifact creates volumes under the same names, so a
    /// manual row records the name the operator's Compose deployment created. It lives here, in one copy, because
    /// both record it on the volumes they retain.</remarks>
    public static string DockerSchemeVolumeName(WorkloadNamingSubject subject, string volumeId)
        => TruncateForDns(string.Join(
            '-',
            DockerSchemeVolumePrefix,
            Sanitize(subject.GroupId),
            Sanitize(volumeId),
            NormalizeInst8(subject.Inst8)));

    /// <summary>Per-node scan-Job name: the object scheme plus a bounded node discriminator (node names are
    /// unbounded, so the raw name is NEVER emitted) plus the run token. UNIQUE per (instance, node, run) — the
    /// idempotency key.</summary>
    public static string ScanPerNodeJobName(string pluginTypeId, string workloadId, string inst8, string nodeName, string runId)
        => ComposeObjectName(
            Sanitize(pluginTypeId),
            Sanitize(workloadId),
            NormalizeInst8(inst8),
            NodeDiscriminator(nodeName),
            Sanitize(runId));

    /// <summary>The per-node suspend selector key: <c>reeve.io/suspended-{inst8}</c> — matches no node.</summary>
    public static string SuspendSelectorKey(string inst8) => $"reeve.io/suspended-{NormalizeInst8(inst8)}";

    /// <summary>The SINGLE source of truth for the physical name a RETAINED volume lives under. An adopted
    /// <c>existingClaim</c> wins verbatim; otherwise the provider's own namer answers.</summary>
    public static string ResolveRetainedResourceName(
        WorkloadInstance instance, string volumeId, IWorkloadResourceNamer namer)
    {
        ArgumentNullException.ThrowIfNull(instance);
        ArgumentNullException.ThrowIfNull(namer);

        if (AdoptedClaim(instance, volumeId) is { } claim)
        {
            return claim;
        }

        return namer.VolumeName(
            new WorkloadNamingSubject
            {
                GroupId = instance.GroupId,
                WorkloadId = instance.WorkloadId,
                Inst8 = instance.Inst8,
            },
            volumeId);
    }

    /// <summary>The volume-naming SCHEME <see cref="ResolveRetainedResourceName"/> resolved under, so a teardown
    /// records the scheme ACTUALLY used.</summary>
    /// <remarks>
    /// <para>
    /// An adopted claim was created by something this engine did not run, so the SHAPE of its name is the only
    /// evidence of what named it: a name carrying <see cref="ObjectNamePrefix"/> came from the object scheme,
    /// and ANY other name is recorded as <see cref="RetainedVolumeSchemeId.Docker"/> — whichever namer is in play.
    /// </para>
    /// <para>
    /// This rule is not generalised to <c>namer.VolumeScheme</c> on purpose. A
    /// Kubernetes instance that adopted a PVC the operator named themselves records it as docker, and the
    /// purge dispatches by that scheme: the Kubernetes deleter never sees it, and the Docker deleter refuses a row
    /// whose provider is not Docker. Those refusals stand between a purge and a PVC this engine never created.
    /// Recording the namer's scheme instead would purge it.
    /// </para>
    /// </remarks>
    public static RetainedVolumeSchemeId ResolveRetainedResourceScheme(
        WorkloadInstance instance, string volumeId, IWorkloadResourceNamer namer)
    {
        ArgumentNullException.ThrowIfNull(instance);
        ArgumentNullException.ThrowIfNull(namer);

        if (AdoptedClaim(instance, volumeId) is { } claim)
        {
            return claim.StartsWith(ObjectNamePrefix + "-", StringComparison.Ordinal)
                ? RetainedVolumeSchemeId.Kubernetes
                : RetainedVolumeSchemeId.Docker;
        }

        return namer.VolumeScheme;
    }

    private static string? AdoptedClaim(WorkloadInstance instance, string volumeId)
        => instance.GetResolvedParameters().TryGetValue($"existingClaim.{volumeId}", out var claim)
            && !string.IsNullOrWhiteSpace(claim)
            ? claim!.Trim()
            : null;

    /// <summary>The full k8s label set for a per-node scan Job: the identity labels plus the node and run labels, so
    /// the (instance, node, run) triple is recoverable from labels ALONE. Every added value is DNS-safe and ≤ 63.</summary>
    /// <remarks>Built on the DEFAULT namer's label set: a scan Job is a Kubernetes object, and the arm that runs
    /// scans ships no namer of its own because the default IS its scheme.</remarks>
    public static IReadOnlyDictionary<string, string> ScanPerNodeJobLabels(
        Guid instanceId,
        string installationId,
        string specHash,
        string nodeName,
        string runId,
        string? criticality = null)
        => StampScanLabels(
            DefaultWorkloadResourceNamer.Instance.Labels(new WorkloadLabelSubject
            {
                InstanceId = instanceId,
                InstallationId = installationId,
                SpecHash = specHash,
                Criticality = criticality,
            }),
            nodeName,
            runId);

    /// <summary>The SINGLE source of truth for STAMPING the two scan authority labels onto an already-materialised
    /// base set: two parallel stampers would silently drift the contract the recovery guarantee depends on.</summary>
    public static IReadOnlyDictionary<string, string> StampScanLabels(
        IReadOnlyDictionary<string, string> baseLabels,
        string nodeName,
        string runId)
    {
        ArgumentNullException.ThrowIfNull(baseLabels);

        return new Dictionary<string, string>(baseLabels)
        {
            [WorkloadResourceLabels.ScanNodeLabelKey] = TruncateForDns(Sanitize(nodeName)),
            [WorkloadResourceLabels.ScanRunLabelKey] = ScanRunLabelValue(runId)
        };
    }

    /// <summary>The SINGLE source of truth for the VALUE stamped under <see cref="WorkloadResourceLabels.ScanRunLabelKey"/>, so the
    /// reaper's <c>notin</c> delete-selector and the label the Jobs actually carry share one transform.</summary>
    public static string ScanRunLabelValue(string runId) => TruncateForDns(Sanitize(runId));

    /// <summary>The per-node suspend nodeSelector fragment: <c>{ reeve.io/suspended-{inst8} = "true" }</c>.</summary>
    public static IReadOnlyDictionary<string, string> SuspendNodeSelector(string inst8)
        => new Dictionary<string, string> { [SuspendSelectorKey(inst8)] = "true" };

    /// <summary>Returns <paramref name="name"/> unchanged when it fits, else truncates and appends a deterministic
    /// <c>-{6-char-hash}</c> of the FULL untruncated name, so two distinct long inputs never collide.</summary>
    public static string TruncateForDns(string name, int maxLen = Dns1123MaxLength)
    {
        ArgumentNullException.ThrowIfNull(name);
        if (name.Length <= maxLen)
        {
            return name;
        }

        var hash = ShortHash(name);
        var keep = maxLen - HashSuffixLength - 1;
        if (keep < 1)
        {
            return hash.Length <= maxLen ? hash : hash[..maxLen];
        }

        var head = name[..keep].TrimEnd('-');
        return $"{head}-{hash}";
    }

    /// <summary>Sanitises a free-form id into a DNS-safe segment: lowercased, non-<c>[a-z0-9-]</c> collapsed to a
    /// single hyphen, outer hyphens stripped. An input that sanitises to empty becomes <c>x</c>.</summary>
    public static string Sanitize(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "x";
        }

        var sb = new StringBuilder(value.Length);
        var lastWasHyphen = false;
        foreach (var ch in value.Trim().ToLowerInvariant())
        {
            if ((ch >= 'a' && ch <= 'z') || (ch >= '0' && ch <= '9'))
            {
                sb.Append(ch);
                lastWasHyphen = false;
            }
            else if (!lastWasHyphen)
            {
                sb.Append('-');
                lastWasHyphen = true;
            }
        }

        var result = sb.ToString().Trim('-');
        return result.Length == 0 ? "x" : result;
    }

    /// <summary>Normalises the instance discriminator, which every name embeds and none may omit.</summary>
    public static string NormalizeInst8(string inst8)
    {
        if (string.IsNullOrWhiteSpace(inst8))
        {
            throw new ArgumentException("inst8 is required.", nameof(inst8));
        }

        return Sanitize(inst8);
    }

    private static string ShortHash(string value)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        var sb = new StringBuilder(HashSuffixLength);
        for (var i = 0; sb.Length < HashSuffixLength; i++)
        {
            sb.Append(bytes[i].ToString("x2", CultureInfo.InvariantCulture));
        }

        return sb.ToString()[..HashSuffixLength];
    }

    private static string NodeDiscriminator(string nodeName) => ShortHash(Sanitize(nodeName));
}
