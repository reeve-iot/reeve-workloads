using System.Collections.Generic;
using k8s.Models;

namespace Reeve.Workloads.Providers.Kubernetes;

internal sealed record RenderedKubernetesObjects
{
    public V1Deployment? Deployment { get; init; }

    public V1DaemonSet? DaemonSet { get; init; }

    public V1Service? Service { get; init; }

    public IReadOnlyList<V1PersistentVolumeClaim> PersistentVolumeClaims { get; init; } = [];

    public IReadOnlyList<V1Job> Jobs { get; init; } = [];

    public required V1Secret Secret { get; init; }

    public V1Secret? ImagePullSecret { get; init; }
}
