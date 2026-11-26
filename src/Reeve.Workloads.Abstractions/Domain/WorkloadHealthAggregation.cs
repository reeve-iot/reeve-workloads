using System.Collections.Generic;
using System.Linq;
using Reeve.Workloads.Abstractions.Domain.Entities;

namespace Reeve.Workloads.Abstractions.Domain;

/// <summary>Pure aggregate-health ranking for a multi-workload plugin. Transient states rank BELOW every
/// genuinely-bad one, and operator-suspended instances are excluded and chipped separately.</summary>
public static class WorkloadHealthAggregation
{
    private static readonly IReadOnlyList<WorkloadInstanceState> Severity = new[]
    {
        WorkloadInstanceState.Failed,
        WorkloadInstanceState.VersionSkew,
        WorkloadInstanceState.Unknown,
        WorkloadInstanceState.Degraded,
        WorkloadInstanceState.Deploying,
        WorkloadInstanceState.Terminating,
        WorkloadInstanceState.Switching,
        WorkloadInstanceState.AdoptionPending,
        WorkloadInstanceState.PendingConfiguration,
        WorkloadInstanceState.ExternallyManaged,
        WorkloadInstanceState.Healthy
    };

    private static readonly IReadOnlyDictionary<WorkloadInstanceState, int> Rank =
        Severity.Select((state, index) => (state, index))
                .ToDictionary(pair => pair.state, pair => pair.index);

    private static readonly int FallbackRank = Rank[WorkloadInstanceState.Unknown];

    internal static bool IsExplicitlyRanked(WorkloadInstanceState state) => Rank.ContainsKey(state);

    /// <summary>The worst non-suspended state across <paramref name="states"/>, or null when the sequence is
    /// empty or every element is suspended.</summary>
    public static WorkloadInstanceState? AggregateHealth(IEnumerable<WorkloadInstanceState> states)
    {
        ArgumentNullException.ThrowIfNull(states);

        WorkloadInstanceState? worst = null;
        var worstRank = int.MaxValue;

        foreach (var state in states)
        {
            if (state == WorkloadInstanceState.Suspended)
            {
                continue;
            }

            var rank = Rank.TryGetValue(state, out var value) ? value : FallbackRank;
            if (rank < worstRank)
            {
                worstRank = rank;
                worst = state;
            }
        }

        return worst;
    }

    /// <summary>True when any instance is operator-suspended, which drives the paused chip.</summary>
    public static bool AnySuspended(IEnumerable<WorkloadInstanceState> states)
    {
        ArgumentNullException.ThrowIfNull(states);
        return states.Any(state => state == WorkloadInstanceState.Suspended);
    }
}
