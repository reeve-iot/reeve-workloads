using Reeve.Pipeline;
using Reeve.Workloads.Abstractions.Update;

namespace Reeve.Workloads.DeploymentFixtures;

/// <summary>What a composed update pipeline must satisfy, asked of the pipeline a host actually resolved — the one
/// its container hands out as <c>Pipeline&lt;WorkloadUpdateStageContext&gt;</c> — so every answer follows the stages
/// the library and the host registered instead of restating an order that could drift from them. Pure and
/// deterministic.</summary>
public static class UpdatePipelineConformance
{
    /// <summary>The library's own stages that are missing from <paramref name="pipeline"/> or run before one declared
    /// ahead of them in <see cref="WorkloadUpdateStages.Core"/>; empty when the library walks as it declares. A host
    /// stage sits between the version commit and the parameter commit, which resolution already enforces.</summary>
    public static IReadOnlyList<string> CoreStagesOutOfOrder(Pipeline<WorkloadUpdateStageContext> pipeline)
    {
        ArgumentNullException.ThrowIfNull(pipeline);

        var position = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < pipeline.Order.Count; i++)
        {
            position[pipeline.Order[i]] = i;
        }

        var outOfOrder = new List<string>();
        for (var i = 0; i < WorkloadUpdateStages.Core.Count; i++)
        {
            var stage = WorkloadUpdateStages.Core[i];
            if (!position.TryGetValue(stage, out var at))
            {
                outOfOrder.Add(stage);
                continue;
            }

            var runsEarly = WorkloadUpdateStages.Core
                .Take(i)
                .Any(ahead => position.TryGetValue(ahead, out var aheadAt) && aheadAt > at);
            if (runsEarly)
            {
                outOfOrder.Add(stage);
            }
        }

        return outOfOrder;
    }

    /// <summary>The stages of <paramref name="pipeline"/> that cannot be unwound once entered, in the order the walk
    /// reaches them: the first is the update's point of no return. For the library's stages alone that is the parameter
    /// commit, which tears down the workloads the new version no longer declares.</summary>
    public static IReadOnlyList<string> IrreversibleStages(Pipeline<WorkloadUpdateStageContext> pipeline)
    {
        ArgumentNullException.ThrowIfNull(pipeline);

        return pipeline.Order
            .Where(id => pipeline.Find(id) is { Reversibility.IsReversible: false })
            .ToList();
    }
}
