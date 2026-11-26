using FluentAssertions;
using Reeve.Workloads.Abstractions.Domain;
using Reeve.Workloads.Abstractions.Domain.Entities;

namespace Reeve.Workloads.Abstractions.Tests;

public sealed class WorkloadHealthAggregationTests
{
    private static readonly WorkloadInstanceState[] WorstToBest =
    [
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
    ];

    [Fact]
    public void Empty_aggregates_to_null()
    {
        WorkloadHealthAggregation.AggregateHealth(Array.Empty<WorkloadInstanceState>()).Should().BeNull();
    }

    [Fact]
    public void Single_state_aggregates_to_itself()
    {
        foreach (var state in WorstToBest)
        {
            WorkloadHealthAggregation.AggregateHealth([state]).Should().Be(state);
        }
    }

    [Fact]
    public void Worst_of_a_pair_always_wins_regardless_of_order()
    {
        for (var i = 0; i < WorstToBest.Length; i++)
        {
            for (var j = 0; j < WorstToBest.Length; j++)
            {
                var worse = WorstToBest[Math.Min(i, j)];
                WorkloadHealthAggregation.AggregateHealth([WorstToBest[i], WorstToBest[j]])
                    .Should().Be(worse, $"worst of ({WorstToBest[i]}, {WorstToBest[j]}) must win");
            }
        }
    }

    [Fact]
    public void Failed_dominates_everything()
    {
        var all = WorstToBest.ToList();
        all.Add(WorkloadInstanceState.Suspended);

        WorkloadHealthAggregation.AggregateHealth(all).Should().Be(WorkloadInstanceState.Failed);
    }

    [Fact]
    public void Healthy_only_when_all_healthy()
    {
        WorkloadHealthAggregation.AggregateHealth(
            [WorkloadInstanceState.Healthy, WorkloadInstanceState.Healthy]).Should().Be(WorkloadInstanceState.Healthy);
    }

    [Fact]
    public void Terminating_is_ranked_and_never_masks_a_failed_critical_workload()
    {
        WorkloadHealthAggregation.AggregateHealth(
            [WorkloadInstanceState.Terminating, WorkloadInstanceState.Failed])
            .Should().Be(WorkloadInstanceState.Failed, "a terminating workload must never mask a Failed critical one");

        WorkloadHealthAggregation.AggregateHealth(
            [WorkloadInstanceState.Terminating, WorkloadInstanceState.Degraded])
            .Should().Be(WorkloadInstanceState.Degraded);
        WorkloadHealthAggregation.AggregateHealth(
            [WorkloadInstanceState.Terminating, WorkloadInstanceState.Unknown])
            .Should().Be(WorkloadInstanceState.Unknown);

        WorkloadHealthAggregation.AggregateHealth([WorkloadInstanceState.Terminating])
            .Should().Be(WorkloadInstanceState.Terminating);

        WorkloadHealthAggregation.AggregateHealth(
            [WorkloadInstanceState.Terminating, WorkloadInstanceState.Healthy])
            .Should().Be(WorkloadInstanceState.Terminating);
    }

    [Fact]
    public void Every_non_suspended_state_is_explicitly_in_the_ranking_table()
    {
        foreach (var state in Enum.GetValues<WorkloadInstanceState>())
        {
            if (state == WorkloadInstanceState.Suspended)
            {
                continue;
            }

            WorkloadHealthAggregation.IsExplicitlyRanked(state)
                .Should().BeTrue($"{state} must have an explicit rank in the aggregate table (not FallbackRank)");
        }

        WorkloadHealthAggregation.IsExplicitlyRanked(WorkloadInstanceState.Suspended).Should().BeFalse();
    }

    [Fact]
    public void The_default_of_the_state_enum_matches_no_member_and_ranks_as_a_fault()
    {
        Enum.IsDefined(default(WorkloadInstanceState)).Should().BeFalse("the enum deliberately has no zero member");
        WorkloadHealthAggregation.IsExplicitlyRanked(default).Should().BeFalse();

        WorkloadHealthAggregation.AggregateHealth([default, WorkloadInstanceState.Failed])
            .Should().Be(WorkloadInstanceState.Failed, "an unranked state must never dominate a real Failed");
        WorkloadHealthAggregation.AggregateHealth([default, WorkloadInstanceState.VersionSkew])
            .Should().Be(WorkloadInstanceState.VersionSkew, "nor the state ranked just above Unknown");
        WorkloadHealthAggregation.AggregateHealth([default, WorkloadInstanceState.Degraded])
            .Should().Be(default(WorkloadInstanceState), "but it outranks the state just below Unknown");
        WorkloadHealthAggregation.AggregateHealth([default, WorkloadInstanceState.Healthy])
            .Should().Be(default(WorkloadInstanceState), "and never hides behind a Healthy sibling");
    }

    [Fact]
    public void Switching_is_ranked_low_and_never_masks_a_genuine_health_state()
    {
        WorkloadHealthAggregation.AggregateHealth(
            [WorkloadInstanceState.Switching, WorkloadInstanceState.Degraded])
            .Should().Be(WorkloadInstanceState.Degraded, "a mid-switch workload must never mask a genuine Degraded one");

        WorkloadHealthAggregation.AggregateHealth(
            [WorkloadInstanceState.Switching, WorkloadInstanceState.Failed])
            .Should().Be(WorkloadInstanceState.Failed);
        WorkloadHealthAggregation.AggregateHealth(
            [WorkloadInstanceState.Switching, WorkloadInstanceState.Unknown])
            .Should().Be(WorkloadInstanceState.Unknown);

        WorkloadHealthAggregation.AggregateHealth([WorkloadInstanceState.Switching])
            .Should().Be(WorkloadInstanceState.Switching);

        WorkloadHealthAggregation.AggregateHealth(
            [WorkloadInstanceState.Switching, WorkloadInstanceState.Healthy])
            .Should().Be(WorkloadInstanceState.Switching);
    }

    [Fact]
    public void Suspended_is_excluded_and_never_masks_a_failed_critical_workload()
    {
        WorkloadHealthAggregation.AggregateHealth(
            [WorkloadInstanceState.Suspended, WorkloadInstanceState.Failed])
            .Should().Be(WorkloadInstanceState.Failed);

        WorkloadHealthAggregation.AggregateHealth(
            [WorkloadInstanceState.Suspended, WorkloadInstanceState.Healthy])
            .Should().Be(WorkloadInstanceState.Healthy);
    }

    [Fact]
    public void Only_suspended_aggregates_to_null()
    {
        WorkloadHealthAggregation.AggregateHealth(
            [WorkloadInstanceState.Suspended, WorkloadInstanceState.Suspended]).Should().BeNull();
    }

    [Fact]
    public void AnySuspended_reflects_presence_of_a_paused_instance()
    {
        WorkloadHealthAggregation.AnySuspended([WorkloadInstanceState.Healthy]).Should().BeFalse();
        WorkloadHealthAggregation.AnySuspended([WorkloadInstanceState.Healthy, WorkloadInstanceState.Suspended])
            .Should().BeTrue();
        WorkloadHealthAggregation.AnySuspended(Array.Empty<WorkloadInstanceState>()).Should().BeFalse();
    }
}
