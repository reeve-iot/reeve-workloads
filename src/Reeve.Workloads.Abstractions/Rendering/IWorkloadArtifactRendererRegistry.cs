using Reeve.Workloads.Abstractions.Domain.DesiredSpec;

namespace Reeve.Workloads.Abstractions.Rendering;

/// <summary>Resolves the <see cref="IWorkloadArtifactRenderer"/> for a requested format. An unknown format or
/// a duplicate registration throws, so a mis-wired DI graph fails loudly instead of dropping a format.</summary>
public interface IWorkloadArtifactRendererRegistry
{
    /// <summary>The set of formats a renderer is registered for (deterministic order).</summary>
    IReadOnlyCollection<WorkloadArtifactFormatId> SupportedFormats { get; }

    /// <summary>The formats whose renderer <see cref="IWorkloadArtifactRenderer.IsSelfContained"/>, in ordinal
    /// order of their ids.</summary>
    IReadOnlyList<WorkloadArtifactFormatId> SelfContainedFormats { get; }

    /// <summary>Renders <paramref name="spec"/> into <paramref name="format"/>, throwing when no renderer is
    /// registered for it.</summary>
    RenderedWorkloadArtifact Render(
        WorkloadArtifactFormatId format,
        WorkloadDesiredSpec spec,
        WorkloadArtifactRenderOptions options);
}
