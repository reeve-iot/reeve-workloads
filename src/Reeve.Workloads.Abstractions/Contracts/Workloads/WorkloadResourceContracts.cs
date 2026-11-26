using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace Reeve.Workloads.Abstractions.Contracts;

/// <summary>One resource posture: the four axes, each null when unset.</summary>
public sealed record WorkloadResourceValues
{
    [JsonPropertyName("requestsCpu")]
    public string? RequestsCpu { get; init; }

    [JsonPropertyName("requestsMemory")]
    public string? RequestsMemory { get; init; }

    [JsonPropertyName("limitsCpu")]
    public string? LimitsCpu { get; init; }

    [JsonPropertyName("limitsMemory")]
    public string? LimitsMemory { get; init; }
}

/// <summary>What the panel reads: the author's declaration, the operator's override, and the effective values
/// the reconciler is driving toward.</summary>
public sealed record WorkloadResourcesResponse
{
    [JsonPropertyName("declared")]
    public required WorkloadResourceValues Declared { get; init; }

    [JsonPropertyName("overrides")]
    public required WorkloadResourceValues Overrides { get; init; }

    [JsonPropertyName("effective")]
    public required WorkloadResourceValues Effective { get; init; }

    /// <summary>False on docker, where the provider has no cpu-request primitive and the field is ignored.</summary>
    [JsonPropertyName("requestsCpuApplicable")]
    public required bool RequestsCpuApplicable { get; init; }
}

/// <summary>A total PUT: every axis is sent, and null or blank CLEARS that axis's override.</summary>
public sealed record SetWorkloadResourcesRequest
{
    private const int QuantityMaxLength = 32;

    [JsonPropertyName("requestsCpu")]
    [StringLength(QuantityMaxLength)]
    public string? RequestsCpu { get; init; }

    [JsonPropertyName("requestsMemory")]
    [StringLength(QuantityMaxLength)]
    public string? RequestsMemory { get; init; }

    [JsonPropertyName("limitsCpu")]
    [StringLength(QuantityMaxLength)]
    public string? LimitsCpu { get; init; }

    [JsonPropertyName("limitsMemory")]
    [StringLength(QuantityMaxLength)]
    public string? LimitsMemory { get; init; }
}
