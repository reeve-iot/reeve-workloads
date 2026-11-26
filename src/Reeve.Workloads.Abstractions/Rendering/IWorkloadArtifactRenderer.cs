using Reeve.Workloads.Abstractions.Domain.DesiredSpec;

namespace Reeve.Workloads.Abstractions.Rendering;

/// <summary>Renders a <see cref="WorkloadDesiredSpec"/> into one deployment artifact per
/// <see cref="WorkloadArtifactFormatId"/>. Implementations must be pure and deterministic, or golden tests flake.</summary>
public interface IWorkloadArtifactRenderer
{
    /// <summary>The single format this renderer produces (its registry discriminator).</summary>
    WorkloadArtifactFormatId Format { get; }

    /// <summary>True when this format alone is enough for an operator to run the workload (a compose file, a
    /// manifest), false for a fragment that needs something else (a chart's values, an env file).</summary>
    /// <remarks>A managed-to-manual switch hands the operator every self-contained format there is and nothing
    /// else. </remarks>
    bool IsSelfContained { get; }

    /// <summary>Renders <paramref name="spec"/> into its <see cref="Format"/> using <paramref name="options"/>
    /// for token handling. Pure; safe to call repeatedly.</summary>
    RenderedWorkloadArtifact Render(WorkloadDesiredSpec spec, WorkloadArtifactRenderOptions options);
}
