using System;
using System.Collections.Generic;
using System.Linq;
using Reeve.Workloads.Abstractions.Domain;
using Reeve.Workloads.Abstractions.Domain.DesiredSpec;

namespace Reeve.Workloads.Providers.Docker;

internal static class DockerProviderConstants
{
    public const string DefaultNetworkName = "reeve-workloads";

    public const string UnixDefaultSocketUri = "unix:///var/run/docker.sock";

    public const string WindowsDefaultSocketUri = "npipe://./pipe/docker_engine";

    public const string MinimumDaemonApiVersion = "1.41";

    public const string MinimumDaemonEngineVersion = "20.10";

    public const string InstanceLabelKey = DockerResourceNamer.InstanceLabelKey;

    public const string InstallationLabelKey = "dev.reeve-iot.installation";

    public const string SpecHashLabelKey = "dev.reeve-iot.spec-hash";

    public const string RestartNonceLabelKey = "dev.reeve-iot.restart-nonce";

    public const string UnhashedEnvDigestLabelKey = "dev.reeve-iot.unhashed-env-digest";

    public static string? UnhashedEnvDigest(IEnumerable<ResolvedEnvVar> env)
    {
        ArgumentNullException.ThrowIfNull(env);

        var flagged = env
            .Where(e => e.ExcludedFromSpecHash)
            .OrderBy(e => e.Name, StringComparer.Ordinal)
            .ThenBy(e => e.Value ?? string.Empty, StringComparer.Ordinal)
            .ToList();
        if (flagged.Count == 0)
        {
            return null;
        }

        using var sha = System.Security.Cryptography.SHA256.Create();
        var buffer = new System.Text.StringBuilder();
        foreach (var e in flagged)
        {
            buffer.Append(e.Name).Append('\0').Append(e.Value ?? string.Empty).Append('\0');
        }

        return Convert.ToHexString(sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(buffer.ToString()))).ToLowerInvariant();
    }

    public const string CredentialDigestLabelKey = "dev.reeve-iot.credential-digest";

    public const string GroupIdLabelKey = "dev.reeve-iot.plugin-type-id";

    public const string WorkloadIdLabelKey = "dev.reeve-iot.workload-id";

    public const string ManagedByLabelKey = "dev.reeve-iot.managed-by";

    public const string ManagedByLabelValue = "reeve-workloads";

    public const long AttachmentGatewayPriority = -100;

    public const string MetricsScrapeLabelKey = "dev.reeve-iot.metrics.scrape";

    public const string MetricsPortLabelKey = "dev.reeve-iot.metrics.port";

    public const string MetricsPathLabelKey = "dev.reeve-iot.metrics.path";

    public static IReadOnlyDictionary<string, string> BuildResourceLabels(WorkloadDesiredSpec spec)
    {
        ArgumentNullException.ThrowIfNull(spec);

        var labels = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [InstanceLabelKey] = spec.InstanceId.ToString("D"),
            [SpecHashLabelKey] = spec.SpecHash,
            [GroupIdLabelKey] = spec.GroupId,
            [WorkloadIdLabelKey] = spec.WorkloadId,
            [ManagedByLabelKey] = ManagedByLabelValue,
        };

        if (UnhashedEnvDigest(spec.Env) is { } unhashedEnvDigest)
        {
            labels[UnhashedEnvDigestLabelKey] = unhashedEnvDigest;
        }

        if (spec.Labels.TryGetValue(WorkloadResourceLabels.InstallationLabelKey, out var installation)
            && !string.IsNullOrWhiteSpace(installation))
        {
            labels[InstallationLabelKey] = installation;
        }

        return labels;
    }
}
