using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using k8s;
using k8s.Models;
using Reeve.Workloads.Abstractions;

namespace Reeve.Workloads.Providers.Kubernetes;

internal interface IKubernetesObjectClient
{
    Task<KubernetesApplyOutcome> ServerSideApplyAsync(
        IKubernetes client,
        string @namespace,
        KubernetesObjectKind kind,
        string name,
        string manifestJson,
        string fieldManager,
        bool force,
        CancellationToken ct);

    Task<IReadOnlyDictionary<string, string>> ReadSecretValuesAsync(
        IKubernetes client,
        string @namespace,
        string secretName,
        IReadOnlyCollection<string> keys,
        CancellationToken ct);

    Task EnsurePvcAsync(
        IKubernetes client,
        string @namespace,
        V1PersistentVolumeClaim pvc,
        bool isAdoptedClaim,
        CancellationToken ct);

    Task<KubernetesWorkloadRaw?> ReadWorkloadAsync(
        IKubernetes client,
        string @namespace,
        IReadOnlyDictionary<string, string> selectorLabels,
        CancellationToken ct);

    async Task<IReadOnlyDictionary<Guid, KubernetesWorkloadRaw>> ReadWorkloadsAsync(
        IKubernetes client,
        string @namespace,
        IReadOnlyList<Guid> instanceIds,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(instanceIds);

        var raws = new Dictionary<Guid, KubernetesWorkloadRaw>(instanceIds.Count);
        foreach (var instanceId in instanceIds)
        {
            var labels = new Dictionary<string, string>
            {
                [Reeve.Workloads.Abstractions.Domain.WorkloadResourceLabels.InstanceLabelKey] = instanceId.ToString("D"),
            };

            if (await ReadWorkloadAsync(client, @namespace, labels, ct).ConfigureAwait(false) is { } raw)
            {
                raws[instanceId] = raw;
            }
        }

        return raws;
    }

    Task DeleteWorkloadByLabelAsync(
        IKubernetes client,
        string @namespace,
        IReadOnlyDictionary<string, string> selectorLabels,
        bool retainPvc,
        CancellationToken ct);

    Task DeleteOtherWorkloadKindsAsync(
        IKubernetes client,
        string @namespace,
        IReadOnlyDictionary<string, string> selectorLabels,
        KubernetesObjectKind keptKind,
        CancellationToken ct);

    Task DeleteScanJobsByLabelAsync(
        IKubernetes client,
        string @namespace,
        IReadOnlyDictionary<string, string> selectorLabels,
        string? exceptRunId,
        CancellationToken ct);

    Task ScaleDeploymentAsync(
        IKubernetes client,
        string @namespace,
        IReadOnlyDictionary<string, string> selectorLabels,
        int replicas,
        CancellationToken ct);

    Task SetDaemonSetSuspendedAsync(
        IKubernetes client,
        string @namespace,
        IReadOnlyDictionary<string, string> selectorLabels,
        bool suspended,
        string inst8,
        CancellationToken ct);

    Task SetScanJobsSuspendedAsync(
        IKubernetes client,
        string @namespace,
        IReadOnlyDictionary<string, string> selectorLabels,
        bool suspended,
        CancellationToken ct);

    Task<IReadOnlyList<AdoptableResource>> ListAdoptableAsync(
        IKubernetes client,
        string @namespace,
        CancellationToken ct);

    Task<string?> ReadObjectManifestAsync(
        IKubernetes client,
        string @namespace,
        KubernetesObjectKind kind,
        string name,
        CancellationToken ct);

    Task PatchResourceMetadataAsync(
        IKubernetes client,
        string @namespace,
        KubernetesObjectKind kind,
        string name,
        IReadOnlyDictionary<string, string> labelsToSet,
        IReadOnlyList<string> labelsToRemove,
        IReadOnlyList<string> annotationsToRemove,
        CancellationToken ct);

    IAsyncEnumerable<WorkloadWatchEvent> WatchWorkloadsAsync(
        IKubernetes client,
        string @namespace,
        string labelSelector,
        CancellationToken ct);

    Task<KubernetesReadyNodePartition> ListReadyNodesAsync(
        IKubernetes client,
        IReadOnlyDictionary<string, string> nodeEligibilitySelector,
        CancellationToken ct);

    Task<KubernetesNetworkAttachmentListResult> ListNetworkAttachmentDefinitionsAsync(
        IKubernetes client,
        string @namespace,
        CancellationToken ct);
}

internal sealed record KubernetesReadyNodePartition
{
    public IReadOnlyList<string> Matching { get; init; } = [];

    public IReadOnlyList<string> Excluded { get; init; } = [];

    public static KubernetesReadyNodePartition Empty { get; } = new();
}

internal static class KubernetesNetworkAttachmentUnavailableReasons
{
    public const string MultusNotInstalled = "multus-not-installed";

    public const string Forbidden = "forbidden";

    public const string ProviderUnavailable = "provider-unavailable";

    public const string NoneInNamespace = "none-in-namespace";
}

internal sealed record KubernetesNetworkAttachment
{
    public required string Name { get; init; }
}

internal sealed record KubernetesNetworkAttachmentListResult
{
    public IReadOnlyList<KubernetesNetworkAttachment> Items { get; init; } = [];

    public string? UnavailableReason { get; init; }

    public static KubernetesNetworkAttachmentListResult Populated(IReadOnlyList<KubernetesNetworkAttachment> items) =>
        new() { Items = items };

    public static KubernetesNetworkAttachmentListResult Unavailable(string reason) =>
        new() { UnavailableReason = reason };
}

internal enum KubernetesObjectKind
{
    Deployment = 0,
    DaemonSet = 1,
    Service = 2,
    Secret = 3,

    PersistentVolumeClaim = 4,

    Job = 5,
}

internal static class AdoptableResourceKindMapping
{
    public static AdoptableResourceKind ToAdoptableKind(this KubernetesObjectKind kind)
        => Enum.IsDefined(kind)
            && Enum.TryParse<AdoptableResourceKind>(kind.ToString(), out var mapped)
            && Enum.IsDefined(mapped)
            ? mapped
            : throw new ArgumentOutOfRangeException(nameof(kind), kind, "no adoptable-resource kind for this object kind");

    public static KubernetesObjectKind ToKubernetesKind(this AdoptableResourceKind kind)
        => Enum.IsDefined(kind)
            && Enum.TryParse<KubernetesObjectKind>(kind.ToString(), out var mapped)
            && Enum.IsDefined(mapped)
            ? mapped
            : throw new ArgumentOutOfRangeException(nameof(kind), kind, "no Kubernetes object kind for this adoptable kind");
}

internal sealed record KubernetesApplyOutcome
{
    public bool Succeeded { get; init; }

    public bool IsConflict { get; init; }

    public string? ConflictingFieldManager { get; init; }

    public bool Unreachable { get; init; }

    public bool Rejected { get; init; }

    public string? Error { get; init; }

    public static KubernetesApplyOutcome Ok() => new() { Succeeded = true };

    public static KubernetesApplyOutcome Conflict(string? conflictingFieldManager) => new()
    {
        IsConflict = true,
        ConflictingFieldManager = conflictingFieldManager,
    };

    public static KubernetesApplyOutcome UnreachableOutcome(string? error = null) => new()
    {
        Unreachable = true,
        Error = error,
    };

    public static KubernetesApplyOutcome RejectedOutcome(string error) => new()
    {
        Rejected = true,
        Error = error,
    };

    public static KubernetesApplyOutcome ErrorOutcome(string error) => new() { Error = error };
}

internal sealed record KubernetesWorkloadRaw
{
    public bool Found { get; init; }

    public string? Kind { get; init; }

    public int? DesiredReplicas { get; init; }

    public int? ReadyReplicas { get; init; }

    public int PodRestartCount { get; init; }

    public string? ObservedImage { get; init; }

    public IReadOnlyList<(DateTimeOffset AtUtc, string Reason, string Message, bool Warning)> Events { get; init; } = [];

    public bool AllPodsRunning { get; init; }

    public bool AnyPodFailed { get; init; }

    public bool HasPods { get; init; }

    public string? PodBlockReason { get; init; }

    public bool PodsRead { get; init; } = true;

    public IReadOnlyList<KubernetesJobRaw> Jobs { get; init; } = [];

    public IReadOnlyList<string> MissingClaims { get; init; } = [];

    public static KubernetesWorkloadRaw NotFound() => new() { Found = false };
}

internal sealed record KubernetesJobRaw
{
    public required string Name { get; init; }

    public string? NodeName { get; init; }

    public int Succeeded { get; init; }

    public int Failed { get; init; }

    public int Active { get; init; }

    public bool Suspended { get; init; }

    public DateTimeOffset? CompletionTime { get; init; }
}

internal enum WorkloadWatchEventType
{
    Synced = 0,

    InstanceChanged = 1,
}

internal sealed record WorkloadWatchEvent
{
    public required WorkloadWatchEventType Type { get; init; }

    public Guid? InstanceId { get; init; }

    public string? ResourceVersion { get; init; }

    public static WorkloadWatchEvent Synced(string? resourceVersion = null) =>
        new() { Type = WorkloadWatchEventType.Synced, ResourceVersion = resourceVersion };

    public static WorkloadWatchEvent Changed(Guid? instanceId, string? resourceVersion = null) =>
        new() { Type = WorkloadWatchEventType.InstanceChanged, InstanceId = instanceId, ResourceVersion = resourceVersion };
}

internal sealed class WorkloadWatchResourceVersionTooOldException : Exception
{
    public WorkloadWatchResourceVersionTooOldException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }
}
