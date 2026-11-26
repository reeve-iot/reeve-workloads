namespace Reeve.Workloads.Abstractions.Rendering;

/// <summary>Non-spec render inputs beyond the desired spec, already resolved/validated by the caller. The
/// Kubernetes renderer never inlines <see cref="RevealedCredentials"/> into the manifest (unlike compose/helm) — it emits a separate <c>kubectl create secret</c> one-liner instead.</summary>
public sealed record WorkloadArtifactRenderOptions
{
    /// <summary>Plaintext credentials for a reveal-once render, keyed by the host's credential id; empty for
    /// external-secret mode. Only a credential is ever revealed: every other secret renders a placeholder. The
    /// Kubernetes renderer emits them only in a separate <c>kubectl create secret</c> one-liner, never in the manifest body.</summary>
    public IReadOnlyDictionary<string, string> RevealedCredentials { get; init; } =
        new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>"I manage this Secret externally" toggle: renders only the Secret's name/keys, and never
    /// emits a <c>kubectl create secret</c> one-liner even if <see cref="RevealedCredentials"/> is somehow set.</summary>
    public bool ExternalSecretMode { get; init; }
}
