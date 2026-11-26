using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using Reeve.Workloads.Abstractions.Providers;

namespace Reeve.Workloads.Providers.Kubernetes;

internal sealed record KubernetesWorkloadCondition
{
    public const string AttachmentMissingId = "attachment-missing";

    public required string Id { get; init; }

    public required string NetworkName { get; init; }

    public string Message => string.Format(
        CultureInfo.InvariantCulture,
        "{0} [{1}]: the pod could not be given a network sandbox — the attachment \"{1}\" could not be attached "
        + "(its NetworkAttachmentDefinition is missing or was renamed, or its CNI plugin is unavailable). Reeve does "
        + "not own this network's lifecycle — re-create the attachment, or update the workload to reference the new name.",
        Id,
        NetworkName);

    public static KubernetesWorkloadCondition AttachmentMissing(string networkName) => new()
    {
        Id = AttachmentMissingId,
        NetworkName = networkName,
    };
}

internal static class KubernetesStatusMapper
{
    public const int RecentEventCap = 20;

    public static WorkloadStatus Map(KubernetesWorkloadRaw? raw)
    {
        var status = MapPhase(raw);
        return raw is { MissingClaims.Count: > 0 } ? status with { MissingVolumes = raw.MissingClaims } : status;
    }

    private static WorkloadStatus MapPhase(KubernetesWorkloadRaw? raw)
    {
        if (raw is null)
        {
            return new WorkloadStatus
            {
                Phase = WorkloadProviderPhase.Unknown,
                ProviderReady = false,
                Message = "Kubernetes API server unreachable — workload status is unknown.",
            };
        }

        if (raw.Jobs.Count > 0)
        {
            var scanEvents = MapEvents(raw.Events);
            var activeNodes = raw.Jobs.Count(j => j.Active >= 1);
            if (activeNodes >= 1)
            {
                return new WorkloadStatus
                {
                    Phase = WorkloadProviderPhase.Running,
                    ProviderReady = false,
                    Message = string.Format(CultureInfo.InvariantCulture, "Scanning — {0} node(s) in progress.", activeNodes),
                    ObservedImage = raw.ObservedImage,
                    RecentEvents = scanEvents,
                };
            }

            var completedNodes = raw.Jobs.Count(j => j.Succeeded >= 1);
            var failedNodeNames = raw.Jobs
                .Where(j => j.Failed >= 1 && j.Succeeded == 0)
                .Select(j => string.IsNullOrWhiteSpace(j.NodeName) ? j.Name : j.NodeName!)
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .OrderBy(name => name, StringComparer.Ordinal)
                .ToList();
            var failedNodes = failedNodeNames.Count;
            var scanRunTerminal = raw.Jobs.All(j => j.Succeeded >= 1 || j.Failed >= 1);
            var heldNodes = raw.Jobs.Count(j => j.Suspended && j.Succeeded == 0 && j.Failed == 0);
            return new WorkloadStatus
            {
                Phase = WorkloadProviderPhase.Ready,
                ProviderReady = true,
                ScanRunTerminal = scanRunTerminal,
                ScanFailedNodeNames = failedNodeNames,
                Message = heldNodes > 0
                    ? string.Format(CultureInfo.InvariantCulture, "Scan suspended — {0} node(s) held.", heldNodes)
                    : completedNodes > 0 || failedNodes > 0
                        ? string.Format(CultureInfo.InvariantCulture, "Scan armed — last run: {0} node(s) completed, {1} failed.", completedNodes, failedNodes)
                        : "Scan armed — ready to scan.",
                ObservedImage = raw.ObservedImage,
                RecentEvents = scanEvents,
            };
        }

        if (!raw.Found)
        {
            return WorkloadStatus.NotFound("No Kubernetes workload object matches the instance label selector.");
        }

        var events = MapEvents(raw.Events);
        var desired = raw.DesiredReplicas;
        var ready = raw.ReadyReplicas;

        if (raw.AnyPodFailed)
        {
            return new WorkloadStatus
            {
                Phase = WorkloadProviderPhase.Failed,
                ProviderReady = false,
                RestartCount = raw.PodRestartCount,
                Message = WithBlockReason(
                    raw.PodRestartCount > 0
                        ? string.Format(CultureInfo.InvariantCulture, "Pod failing (restarts: {0}).", raw.PodRestartCount)
                        : "Pod failing.",
                    raw.PodBlockReason),
                ObservedImage = raw.ObservedImage,
                RecentEvents = events,
            };
        }

        var meetsDesired = desired is { } d && d >= 1 && ready is { } r && r >= d;
        if (meetsDesired && raw.AllPodsRunning)
        {
            return new WorkloadStatus
            {
                Phase = WorkloadProviderPhase.Ready,
                ProviderReady = true,
                RestartCount = raw.PodRestartCount,
                Message = string.Format(CultureInfo.InvariantCulture, "{0}/{1} ready.", ready, desired),
                ObservedImage = raw.ObservedImage,
                RecentEvents = events,
            };
        }

        if (TryDetectAttachmentMissing(raw.Events) is { } attachmentMissing)
        {
            return new WorkloadStatus
            {
                Phase = WorkloadProviderPhase.Failed,
                ProviderReady = false,
                RestartCount = raw.PodRestartCount,
                Message = attachmentMissing.Message,
                ObservedImage = raw.ObservedImage,
                RecentEvents = events,
            };
        }

        if (raw.HasPods)
        {
            return new WorkloadStatus
            {
                Phase = WorkloadProviderPhase.Running,
                ProviderReady = false,
                RestartCount = raw.PodRestartCount,
                Message = WithBlockReason(
                    string.Format(CultureInfo.InvariantCulture, "{0}/{1} ready.", ready ?? 0, desired ?? 0),
                    raw.PodBlockReason),
                ObservedImage = raw.ObservedImage,
                RecentEvents = events,
            };
        }

        if (!raw.PodsRead)
        {
            if (meetsDesired)
            {
                return new WorkloadStatus
                {
                    Phase = WorkloadProviderPhase.Ready,
                    ProviderReady = true,
                    RestartCount = raw.PodRestartCount,
                    Message = string.Format(CultureInfo.InvariantCulture, "{0}/{1} ready.", ready, desired),
                    ObservedImage = raw.ObservedImage,
                    RecentEvents = events,
                };
            }

            return new WorkloadStatus
            {
                Phase = WorkloadProviderPhase.Unknown,
                ProviderReady = false,
                RestartCount = raw.PodRestartCount,
                Message = "Workload object read, but pod status could not be read this pass — status is unknown.",
                ObservedImage = raw.ObservedImage,
                RecentEvents = events,
            };
        }

        return new WorkloadStatus
        {
            Phase = WorkloadProviderPhase.Pending,
            ProviderReady = false,
            RestartCount = raw.PodRestartCount,
            Message = desired is 0
                ? "Scaled to 0 — no pods scheduled."
                : "No pods scheduled yet.",
            ObservedImage = raw.ObservedImage,
            RecentEvents = events,
        };
    }

    private static string WithBlockReason(string summary, string? blockReason)
        => blockReason is null ? summary : summary + " Pod not ready: " + blockReason;

    public static KubernetesWorkloadCondition? TryReadAttachmentMissing(string? message)
    {
        const string prefix = KubernetesWorkloadCondition.AttachmentMissingId + " [";
        if (message is null || !message.StartsWith(prefix, StringComparison.Ordinal))
        {
            return null;
        }

        var close = message.IndexOf("]:", prefix.Length, StringComparison.Ordinal);
        if (close <= prefix.Length)
        {
            return null;
        }

        return KubernetesWorkloadCondition.AttachmentMissing(message[prefix.Length..close]);
    }

    public static KubernetesWorkloadCondition? TryReadAttachmentMissing(WorkloadStatus? status)
        => TryReadAttachmentMissing(status?.Message);

    private const string SandboxCreationFailedReason = "FailedCreatePodSandBox";

    private static readonly Regex[] AttachmentNamePatterns =
    [
        new("error adding container to network\\s+\"(?<n>[^\"]+)\"", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
        new(
            "network[-_ ]?attachment[-_ ]?definitions?(?:\\.k8s\\.cni\\.cncf\\.io)?\\s+(?:\"(?<n>[^\"]+)\"|(?<n>[A-Za-z0-9][A-Za-z0-9._/-]*))",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
        new("network\\s+\"(?<n>[^\"]+)\"", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
    ];

    private static KubernetesWorkloadCondition? TryDetectAttachmentMissing(
        IReadOnlyList<(DateTimeOffset AtUtc, string Reason, string Message, bool Warning)> events)
    {
        if (events.Count == 0)
        {
            return null;
        }

        foreach (var e in events.OrderByDescending(e => e.AtUtc))
        {
            if (!string.Equals(e.Reason, SandboxCreationFailedReason, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (TryExtractNetworkName(e.Message) is { } networkName)
            {
                return KubernetesWorkloadCondition.AttachmentMissing(networkName);
            }
        }

        return null;
    }

    private static string? TryExtractNetworkName(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return null;
        }

        foreach (var pattern in AttachmentNamePatterns)
        {
            var match = pattern.Match(message);
            if (!match.Success)
            {
                continue;
            }

            var name = match.Groups["n"].Value.Trim().Trim('"', '\'', '.', ',', ';', ':');
            if (name.Length > 0)
            {
                return name;
            }
        }

        return null;
    }

    private static IReadOnlyList<WorkloadProviderEvent> MapEvents(
        IReadOnlyList<(DateTimeOffset AtUtc, string Reason, string Message, bool Warning)> events)
    {
        if (events.Count == 0)
        {
            return [];
        }

        return events
            .OrderBy(e => e.AtUtc)
            .Select(e => new WorkloadProviderEvent
            {
                AtUtc = e.AtUtc,
                Reason = e.Reason,
                Message = e.Message,
                Severity = e.Warning ? WorkloadEventSeverity.Warning : WorkloadEventSeverity.Normal,
            })
            .TakeLast(RecentEventCap)
            .ToList();
    }
}
