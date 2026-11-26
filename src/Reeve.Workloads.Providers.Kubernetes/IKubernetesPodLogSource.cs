using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using k8s;

namespace Reeve.Workloads.Providers.Kubernetes;

internal interface IKubernetesPodLogSource
{
    Task<IReadOnlyList<PodLogTarget>> ListPodsAsync(
        IKubernetes client,
        string @namespace,
        IReadOnlyDictionary<string, string> selectorLabels,
        CancellationToken ct);

    Task<string?> ReadPodLogAsync(
        IKubernetes client,
        string @namespace,
        string podName,
        int tailLines,
        bool previous,
        CancellationToken ct);

    Task<IReadOnlyList<(DateTimeOffset AtUtc, string Reason, string Message, bool Warning)>> ListPodEventsAsync(
        IKubernetes client,
        string @namespace,
        string podName,
        CancellationToken ct);
}

internal sealed record PodLogTarget
{
    public required string PodName { get; init; }

    public required int RestartCount { get; init; }

    public string? Phase { get; init; }

    public string? NetworkStatusJson { get; init; }
}
