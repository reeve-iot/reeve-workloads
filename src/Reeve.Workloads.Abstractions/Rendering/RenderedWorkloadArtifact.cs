using System.Collections.Generic;

namespace Reeve.Workloads.Abstractions.Rendering;

/// <summary>An operator-facing deployment artifact plus its guidance. SECRET SEPARATION: no format inlines a
/// password; the Kubernetes manifest inlines no secret value, while the others may carry the revealed credentials
/// behind a chmod note. Non-secret parameter values are inlined verbatim in every format.</summary>
public sealed record RenderedWorkloadArtifact
{
    /// <summary>Which format this artifact is (matches the producing renderer's <see cref="IWorkloadArtifactRenderer.Format"/>).</summary>
    public required WorkloadArtifactFormatId Format { get; init; }

    /// <summary>The main artifact text. For the Kubernetes format it NEVER contains a secret value.</summary>
    public required string PrimaryContent { get; init; }

    /// <summary>The reveal-once secret-creation one-liner, Kubernetes format only. Keeping the credentials here rather
    /// than in <see cref="PrimaryContent"/> is what keeps the manifest free of secret material.</summary>
    public string? SecretOneLiner { get; init; }

    /// <summary>Operator guidance: the chmod reminder for credential-bearing files, the pull-path caveat for digest
    /// images, and the expected Secret name and keys in external-secret mode.</summary>
    public IReadOnlyList<string> Notes { get; init; } = [];
}
