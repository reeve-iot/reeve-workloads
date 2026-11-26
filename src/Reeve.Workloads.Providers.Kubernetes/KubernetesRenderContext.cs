namespace Reeve.Workloads.Providers.Kubernetes;

internal sealed record KubernetesRenderContext
{
    public required string Namespace { get; init; }

    public string? ImagePullSecretName { get; init; }

    /// <summary>The StorageClass a fresh PVC is created with when the volume names none; null ⇒ the cluster
    /// default.</summary>
    /// <remarks>Resolved HERE rather than in the desired spec. The field is spec-hash-excluded, so it changes no hash and re-applies nothing, and the provider-agnostic spec factory never reads this provider's settings section.</remarks>
    public string? DefaultStorageClassName { get; init; }

    public string? AutoImagePullSecretName { get; init; }
}
