using Reeve.Workloads.Abstractions.Rendering;

namespace Reeve.Workloads.Abstractions;

/// <summary>Renders deployment artifacts an operator applies by hand, for the registered provider that leaves
/// deployment to the operator. The host is asked for each workload's bootstrap as the operator will apply it, and
/// may refuse. Revealing a live credential is privileged and audited.</summary>
public interface IWorkloadArtifactService
{
    /// <summary>Renders artifacts for the workloads of <paramref name="instanceId"/>'s plugin instance, skipping
    /// one its manifest no longer declares or whose parameters do not resolve. A host refusal of any one workload's
    /// bootstrap refuses the whole render, before any credential is minted. <paramref name="revealSecrets"/>
    /// reveals each workload's OWN instance's credentials, declared or provisioned, and audits
    /// <paramref name="actorUserId"/> once per instance asked; otherwise no credential is emitted. No other secret
    /// is ever revealed.</summary>
    Task<WorkloadArtifactRenderResult> RenderAsync(
        Guid instanceId,
        WorkloadArtifactFormatId format,
        bool revealSecrets,
        Guid? actorUserId,
        CancellationToken cancellationToken = default);
}

/// <summary>The typed outcome of a manual-mode artifact render.</summary>
public enum WorkloadArtifactRenderOutcome
{
    /// <summary>The artifacts rendered successfully.</summary>
    Success = 0,

    /// <summary>The instance id is unknown.</summary>
    NotFound = 1,

    /// <summary>A workload's spec could not be computed (the host refused its bootstrap, or its parameters do not
    /// resolve), so the plugin's artifact set is not rendered at all: a set missing one workload would deploy a plugin
    /// that cannot run. <see cref="WorkloadArtifactRenderResult.Error"/> carries the reason.</summary>
    HostRefused = 3,

    /// <summary>No registered provider leaves deployment to the operator, so there is nothing to render artifacts
    /// for.</summary>
    NoHandAppliedProvider = 4,
}

/// <summary>One workload's entry in a multi-workload render. A single workload's render fault is isolated here
/// (<see cref="Artifact"/> null, <see cref="Error"/> set) and never aborts the whole plugin's artifact set.</summary>
public sealed record RenderedWorkloadArtifactItem
{
    public required string WorkloadId { get; init; }

    /// <summary>The rendered artifact, or null when rendering this workload faulted (see <see cref="Error"/>).</summary>
    public RenderedWorkloadArtifact? Artifact { get; init; }

    /// <summary>A per-workload render error (bad patch, etc.); null on success. Mutually exclusive with <see cref="Artifact"/>.</summary>
    public string? Error { get; init; }
}

/// <summary>Result of <see cref="IWorkloadArtifactService.RenderAsync"/>: on success, per-workload artifacts and the
/// reveal status; otherwise <see cref="Error"/> explains the failure.</summary>
public sealed record WorkloadArtifactRenderResult
{
    public required WorkloadArtifactRenderOutcome Outcome { get; init; }

    public WorkloadArtifactFormatId Format { get; init; }

    /// <summary>True when any workload's artifact carries a live credential.</summary>
    public bool SecretsRevealed { get; init; }

    public IReadOnlyList<RenderedWorkloadArtifactItem> Workloads { get; init; } = [];

    /// <summary>The human-readable failure message for a non-success outcome; null on success.</summary>
    public string? Error { get; init; }

    public static WorkloadArtifactRenderResult NotFound(string message) =>
        new() { Outcome = WorkloadArtifactRenderOutcome.NotFound, Error = message };

    public static WorkloadArtifactRenderResult NoHandAppliedProvider(string message) =>
        new() { Outcome = WorkloadArtifactRenderOutcome.NoHandAppliedProvider, Error = message };

    public static WorkloadArtifactRenderResult HostRefused(string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        return new() { Outcome = WorkloadArtifactRenderOutcome.HostRefused, Error = reason };
    }
}
