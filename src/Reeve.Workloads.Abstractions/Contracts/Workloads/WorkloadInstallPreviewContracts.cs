using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;
using Reeve.Workloads.Abstractions.Contracts.Deployment;

namespace Reeve.Workloads.Abstractions.Contracts;

/// <summary>One Reeve-synthesized (reserved) parameter, described well enough for the install wizard to RENDER it.
/// The set is EXHAUSTIVE, which makes the lockstep guard an equality; <see cref="Renderable"/> keeps the UI scoped.</summary>
public sealed record WorkloadReservedParameterDescriptor
{
    /// <summary>The reserved parameter name exactly as both synthesis sites spell it (e.g. <c>network.mode</c>).</summary>
    [JsonPropertyName("name")]
    public required string Name { get; init; }

    /// <summary>The FIXED type this reserved name is synthesized with — the value the parser returns, and what Rule
    /// 16 refuses an author re-declaration for changing. The wizard keys its widget off this, not the manifest.</summary>
    [JsonPropertyName("type")]
    public required WorkloadParameterType Type { get; init; }

    /// <summary>The closed value set for an <c>enum</c>-typed reserved parameter; absent for every other type.</summary>
    [JsonPropertyName("enumValues")]
    public IReadOnlyList<string>? EnumValues { get; init; }

    /// <summary>The COMPUTED default (for <c>network.mode</c>, the author's declaration mapped). Null when the
    /// reserved name carries no default — a manifest declares an attachment SLOT, never an attachment VALUE.</summary>
    [JsonPropertyName("default")]
    public string? Default { get; init; }

    /// <summary>Reeve-authored hint copy for the control, or null. Never author prose.</summary>
    [JsonPropertyName("hint")]
    public string? Hint { get; init; }

    /// <summary>The network modes the wizard may offer for the requested provider — SERVER-computed, because the
    /// restriction that could remove one is a backend check the frontend has no source for at Parameters time.</summary>
    [JsonPropertyName("applicableModes")]
    public IReadOnlyList<string>? ApplicableModes { get; init; }

    /// <summary>True iff this name is synthesized at the resolved-VALUE site for the requested provider, so
    /// <c>port.*.hostPort</c> is false on Kubernetes and Manual and true on Docker. Every other name is true.</summary>
    [JsonPropertyName("providerApplicable")]
    public required bool ProviderApplicable { get; init; }

    /// <summary>True iff the install wizard renders a control for this descriptor: today <c>network.mode</c> and
    /// <c>network.attachments</c> and nothing else. Every other descriptor is projected but renders no control.</summary>
    [JsonPropertyName("renderable")]
    public required bool Renderable { get; init; }
}

/// <summary>A dangerous JSON pointer found in a verbatim <c>unsupportedPodPatch</c> body, with Reeve's own-words
/// explanation. The FE highlights these red against the verbatim patch.</summary>
public sealed record DangerousPointerAnnotation
{
    /// <summary>The concrete RFC 6901 JSON pointer as it appears in the patch (e.g. <c>/spec/containers/0/securityContext/privileged</c>).</summary>
    [JsonPropertyName("jsonPointer")]
    public required string JsonPointer { get; init; }

    /// <summary>Reeve-authored plain-language explanation (e.g. "runs as root on the node").</summary>
    [JsonPropertyName("explanation")]
    public required string Explanation { get; init; }
}

/// <summary>The Prerequisites-step projection: the resolved provider's preflight checks per workload.
/// <see cref="Ok"/> is false with <see cref="Error"/> when the preview could not run — never a false all-clear.</summary>
public sealed record WorkloadPreflightPreviewResponse
{
    [JsonPropertyName("ok")]
    public required bool Ok { get; init; }

    [JsonPropertyName("error")]
    public string? Error { get; init; }

    /// <summary>The provider whose preflight ran, echoed back.</summary>
    [JsonPropertyName("provider")]
    public required WorkloadProviderId Provider { get; init; }

    /// <summary>One preflight result per declared workload; empty on a blocked preview.</summary>
    [JsonPropertyName("workloads")]
    public IReadOnlyList<WorkloadPreflightProjection> Workloads { get; init; } = [];

    /// <summary>True when EVERY check on EVERY workload passed (roll-up).</summary>
    [JsonPropertyName("allPassed")]
    public bool AllPassed { get; init; }

    /// <summary>True when ANY workload has a failing Error-severity check — deploy is blocked.</summary>
    [JsonPropertyName("hasBlockingError")]
    public bool HasBlockingError { get; init; }

    /// <summary>A blocked or failed preflight preview carrying a reason and no workloads (fail-closed).</summary>
    public static WorkloadPreflightPreviewResponse Blocked(WorkloadProviderId provider, string error) => new()
    {
        Ok = false,
        Error = error,
        Provider = provider,
        AllPassed = false,
        HasBlockingError = true,
    };
}

/// <summary>Per-workload preflight checks.</summary>
public sealed record WorkloadPreflightProjection
{
    [JsonPropertyName("workloadId")]
    public required string WorkloadId { get; init; }

    [JsonPropertyName("checks")]
    public IReadOnlyList<WorkloadPreflightCheckDto> Checks { get; init; } = [];

    [JsonPropertyName("allPassed")]
    public bool AllPassed { get; init; }

    [JsonPropertyName("hasBlockingError")]
    public bool HasBlockingError { get; init; }
}

/// <summary>One preflight check line, mirroring the provider SPI's PreflightCheck on the wire.</summary>
public sealed record WorkloadPreflightCheckDto
{
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    /// <summary>Severity token: <c>info</c>|<c>warning</c>|<c>error</c>.</summary>
    [JsonPropertyName("severity")]
    public required string Severity { get; init; }

    [JsonPropertyName("passed")]
    public required bool Passed { get; init; }

    [JsonPropertyName("message")]
    public required string Message { get; init; }

    /// <summary>Copy-paste remediation (a <c>kubectl …</c> line), when the check offers one.</summary>
    [JsonPropertyName("fixSnippet")]
    public string? FixSnippet { get; init; }
}
