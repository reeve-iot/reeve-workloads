using System.Text.Json.Serialization;

namespace Reeve.Workloads.Abstractions.Contracts;

/// <summary>A single rendered artifact for one workload of the instance's plugin; a multi-workload plugin
/// renders one of these per workload (keyed by <see cref="WorkloadId"/>).</summary>
public sealed record RenderedWorkloadArtifactDto
{
    /// <summary>The workload id this artifact deploys (the manifest <c>workloads[].id</c>).</summary>
    [JsonPropertyName("workloadId")]
    public required string WorkloadId { get; init; }

    /// <summary>The requested artifact format token: <c>compose</c> | <c>kubernetes</c> | <c>helm-values</c> | <c>env</c>.</summary>
    [JsonPropertyName("format")]
    public required string Format { get; init; }

    /// <summary>The primary rendered document (the compose.yaml / k8s manifest / values fragment / .env body).</summary>
    [JsonPropertyName("primaryContent")]
    public required string PrimaryContent { get; init; }

    /// <summary>A per-workload render error, or null on success. Set (with empty <see cref="PrimaryContent"/>
    /// and no <see cref="SecretOneLiner"/>) when only this workload's render faulted — isolated so the others still render.</summary>
    [JsonPropertyName("error")]
    public string? Error { get; init; }

    /// <summary>The out-of-band secret one-liner (kubernetes reveal-once), or null when the format inlines
    /// the credentials or none was revealed. Never populated without a reveal request.</summary>
    [JsonPropertyName("secretOneLiner")]
    public string? SecretOneLiner { get; init; }

    /// <summary>Operator guidance notes for this artifact (chmod-600 / pull-path / expected-secret).</summary>
    [JsonPropertyName("notes")]
    public IReadOnlyList<string> Notes { get; init; } = [];
}

/// <summary>The 200 body for a manual-mode artifact render: the requested format, whether a live credential was
/// revealed, and the per-workload rendered artifacts.</summary>
public sealed record WorkloadArtifactResponse
{
    /// <summary>The workload instance the artifacts were rendered for (its plugin instance's siblings are included).</summary>
    [JsonPropertyName("instanceId")]
    public required Guid InstanceId { get; init; }

    /// <summary>The requested artifact format token: <c>compose</c> | <c>kubernetes</c> | <c>helm-values</c> | <c>env</c>.</summary>
    [JsonPropertyName("format")]
    public required string Format { get; init; }

    /// <summary>True when a live credential was revealed into the artifacts (only on a reveal request).</summary>
    [JsonPropertyName("secretsRevealed")]
    public required bool SecretsRevealed { get; init; }

    /// <summary>The per-workload rendered artifact(s), one per workload of the instance's plugin (keyed by workloadId).</summary>
    [JsonPropertyName("workloads")]
    public required IReadOnlyList<RenderedWorkloadArtifactDto> Workloads { get; init; }
}
