using System;
using System.Collections.Generic;
using System.Linq;
using Reeve.Workloads.Abstractions.Domain.DesiredSpec;
using Reeve.Workloads.Abstractions.Rendering;

namespace Reeve.Workloads.Core.Rendering;

/// <summary>Default <see cref="IWorkloadArtifactRendererRegistry"/>: indexes renderers by <see cref="IWorkloadArtifactRenderer.Format"/>.
/// Fails LOUDLY on a mis-wired graph — a duplicate format at construction or an unknown format at render time both throw, rather than silently dropping or shadowing a format.</summary>
public sealed class WorkloadArtifactRendererRegistry : IWorkloadArtifactRendererRegistry
{
    private readonly IReadOnlyDictionary<WorkloadArtifactFormatId, IWorkloadArtifactRenderer> _byFormat;
    private readonly IReadOnlyList<WorkloadArtifactFormatId> _selfContained;

    /// <summary>Indexes <paramref name="renderers"/> by format. Throws when two renderers claim the same
    /// format so a duplicate DI registration never silently shadows one renderer with another.</summary>
    public WorkloadArtifactRendererRegistry(IEnumerable<IWorkloadArtifactRenderer> renderers)
    {
        ArgumentNullException.ThrowIfNull(renderers);

        var map = new Dictionary<WorkloadArtifactFormatId, IWorkloadArtifactRenderer>();
        foreach (var renderer in renderers)
        {
            ArgumentNullException.ThrowIfNull(renderer);
            if (!map.TryAdd(renderer.Format, renderer))
            {
                throw new InvalidOperationException(
                    $"Duplicate workload artifact renderer for format '{renderer.Format}': "
                    + $"'{map[renderer.Format].GetType().Name}' and '{renderer.GetType().Name}' both claim it.");
            }
        }

        _byFormat = map;
        _selfContained = map.Values
            .Where(r => r.IsSelfContained)
            .Select(r => r.Format)
            .Order()
            .ToArray();
    }

    /// <inheritdoc />
    public IReadOnlyCollection<WorkloadArtifactFormatId> SupportedFormats => _byFormat.Keys.ToArray();

    /// <inheritdoc />
    public IReadOnlyList<WorkloadArtifactFormatId> SelfContainedFormats => _selfContained;

    /// <inheritdoc />
    public RenderedWorkloadArtifact Render(
        WorkloadArtifactFormatId format,
        WorkloadDesiredSpec spec,
        WorkloadArtifactRenderOptions options)
    {
        ArgumentNullException.ThrowIfNull(spec);
        ArgumentNullException.ThrowIfNull(options);

        if (!_byFormat.TryGetValue(format, out var renderer))
        {
            throw new InvalidOperationException(
                $"No workload artifact renderer is registered for format '{format}'. "
                + $"Registered formats: {string.Join(", ", _byFormat.Keys.Order())}.");
        }

        return renderer.Render(spec, options);
    }
}
