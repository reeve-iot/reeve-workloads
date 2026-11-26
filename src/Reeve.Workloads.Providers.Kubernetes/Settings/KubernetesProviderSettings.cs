using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using System.Text.Json.Serialization;
using Reeve.Workloads.Abstractions.Contracts;

namespace Reeve.Workloads.Providers.Kubernetes;

/// <summary>How the Kubernetes provider connects to its cluster. <see cref="InCluster"/> is ordinal 0 so an
/// absent field deserializes unchanged; ordinals are APPEND-ONLY, never reordered.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum KubernetesConnectionMode
{
    /// <summary><c>inCluster</c>: the in-cluster ServiceAccount (the production default, unchanged). The dev/e2e
    /// <c>Workloads:Kubernetes:AllowKubeconfig</c>+<c>KubeconfigPath</c> env seam is consulted ONLY in this mode.</summary>
    [JsonStringEnumMemberName("inCluster")]
    InCluster = 0,

    /// <summary><c>remoteKubeconfig</c>: the operator connects to a remote cluster with an inline, self-contained
    /// kubeconfig held in <see cref="KubernetesProviderSettings.Kubeconfig"/>, superseding the dev-only env seam.</summary>
    [JsonStringEnumMemberName("remoteKubeconfig")]
    RemoteKubeconfig = 1,
}

/// <summary>Kubernetes provider settings. A key of the entry this record does not own is the host's: the codec
/// keeps it in the stored row without reading it.</summary>
public sealed record KubernetesProviderSettings
{
    /// <summary>Target namespace; blank means the app's own namespace when the connection is in-cluster, and
    /// no namespace on a remote kubeconfig. Read it through <see cref="KubernetesNamespaceResolver"/>.</summary>
    [JsonPropertyName("namespace")]
    public string? Namespace { get; init; }

    /// <summary>How the provider connects to its target cluster. A document with no <c>connectionMode</c> loads
    /// as <see cref="KubernetesConnectionMode.InCluster"/>; <c>remoteKubeconfig</c> makes <see cref="Kubeconfig"/> the source.</summary>
    [JsonPropertyName("connectionMode")]
    public KubernetesConnectionMode ConnectionMode { get; init; } = KubernetesConnectionMode.InCluster;

    /// <summary>The inline kubeconfig used when <see cref="ConnectionMode"/> is RemoteKubeconfig. SECRET (masked on read,
    /// protected at rest by the host, `***` resolves to the stored value); MUST be self-contained — the backend cannot read `*-file` path references.</summary>
    [JsonPropertyName("kubeconfig")]
    [MaxLength(65536)]
    public string? Kubeconfig { get; init; }

    /// <summary>Optional context name to select within <see cref="Kubeconfig"/>; null uses the kubeconfig's
    /// <c>current-context</c>. Not a secret.</summary>
    [JsonPropertyName("kubeconfigContext")]
    [MaxLength(253)]
    public string? KubeconfigContext { get; init; }

    /// <summary>Default StorageClass name for PVCs; null ⇒ cluster default.</summary>
    /// <remarks>Applied at RENDER time, not when the desired spec is built. <c>ResolvedVolume.StorageClassName</c>
    /// is spec-hash-excluded precisely because a default-class change must not poison drift, so nothing is lost
    /// by resolving it here — and the spec factory, which is provider-agnostic, stops reading a k8s setting.</remarks>
    [JsonPropertyName("defaultStorageClassName")]
    public string? DefaultStorageClassName { get; init; }

    /// <summary>imagePullSecret to attach to workload pods; null ⇒ none.</summary>
    [JsonPropertyName("imagePullSecretName")]
    public string? ImagePullSecretName { get; init; }

    /// <summary>Global escape-hatch toggle for <c>unsupportedPodPatch</c>. Default false.</summary>
    [JsonPropertyName("allowUnsupportedOverrides")]
    public bool AllowUnsupportedOverrides { get; init; }

    /// <summary>This provider's entry, or all-defaults when the operator has configured none.</summary>
    public static KubernetesProviderSettings From(WorkloadOrchestratorSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return From(settings.Entry(KubernetesProvider.Id));
    }

    internal static KubernetesProviderSettings From(JsonElement entry)
        => entry.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null
            ? new KubernetesProviderSettings()
            : entry.Deserialize<KubernetesProviderSettings>() ?? new KubernetesProviderSettings();
}
