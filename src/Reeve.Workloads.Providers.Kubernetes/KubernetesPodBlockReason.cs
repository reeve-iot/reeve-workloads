using System;
using System.Collections.Generic;
using System.Linq;
using k8s.Models;

namespace Reeve.Workloads.Providers.Kubernetes;

internal static class KubernetesPodBlockReason
{
    public static string? Describe(IList<V1Pod> pods)
    {
        ArgumentNullException.ThrowIfNull(pods);

        var ordered = pods
            .OrderBy(p => IsFailing(p) ? 0 : 1)
            .ThenBy(p => p.Metadata?.Name, StringComparer.Ordinal);
        foreach (var pod in ordered)
        {
            if (DescribeOne(pod) is { } reason)
            {
                return reason;
            }
        }

        return null;
    }

    private static string? DescribeOne(V1Pod pod)
    {
        var status = pod.Status;
        if (status is null || string.Equals(status.Phase, "Succeeded", StringComparison.Ordinal))
        {
            return null;
        }

        if (FindCondition(status, "PodScheduled") is { Status: "False" } scheduled)
        {
            return Compose(scheduled.Reason ?? "Unschedulable", scheduled.Message);
        }

        foreach (var container in Containers(status))
        {
            if (container.State?.Waiting is { Reason: { Length: > 0 } waitingReason } waiting
                && !string.Equals(waitingReason, "PodInitializing", StringComparison.Ordinal))
            {
                return DescribeWaiting(container, waitingReason, waiting.Message);
            }

            if (container.State?.Terminated is { ExitCode: not 0 } terminated)
            {
                return Compose(terminated.Reason ?? "Terminated", $"container {container.Name} exited with code {terminated.ExitCode}");
            }
        }

        if (string.Equals(status.Phase, "Running", StringComparison.Ordinal)
            && FindCondition(status, "Ready") is { Status: "False" } ready)
        {
            return Compose(ready.Reason ?? "NotReady", ready.Message);
        }

        return null;
    }

    private static string DescribeWaiting(V1ContainerStatus container, string reason, string? message)
    {
        if (reason is "ErrImagePull" or "ImagePullBackOff")
        {
            return string.IsNullOrWhiteSpace(container.Image)
                ? $"ImagePullBackOff: container {container.Name} cannot pull its image"
                : $"ImagePullBackOff: container {container.Name} cannot pull image {container.Image}";
        }

        if (reason == "CrashLoopBackOff")
        {
            return container.LastState?.Terminated is { } last
                ? $"CrashLoopBackOff: container {container.Name} exited with code {last.ExitCode}"
                : $"CrashLoopBackOff: container {container.Name} keeps restarting";
        }

        return Compose(reason, message);
    }

    private static bool IsFailing(V1Pod pod)
        => string.Equals(pod.Status?.Phase, "Failed", StringComparison.Ordinal)
           || (pod.Status is { } status && Containers(status).Any(c =>
               c.State?.Waiting?.Reason is "CrashLoopBackOff" or "ImagePullBackOff" or "ErrImagePull"
               || c.State?.Terminated is { ExitCode: not 0 }));

    private static IEnumerable<V1ContainerStatus> Containers(V1PodStatus status)
        => (status.InitContainerStatuses ?? []).Concat(status.ContainerStatuses ?? []);

    private static V1PodCondition? FindCondition(V1PodStatus status, string type)
        => status.Conditions?.FirstOrDefault(c => string.Equals(c.Type, type, StringComparison.Ordinal));

    private static string Compose(string reason, string? message)
        => string.IsNullOrWhiteSpace(message) ? reason : $"{reason}: {message.Trim()}";
}
