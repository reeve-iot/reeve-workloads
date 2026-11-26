using FluentAssertions;
using Xunit;

namespace Reeve.Workloads.Core.Tests;

public sealed class WorkloadGroupGateTests
{
    private static readonly Guid Group = Guid.NewGuid();
    private static readonly Guid OtherGroup = Guid.NewGuid();

    [Fact]
    public async Task Two_drift_reconciles_of_one_group_hold_it_together()
    {
        var gate = new WorkloadGroupGate();

        using var first = await gate.EnterToReconcileAsync(Group, CancellationToken.None);
        var second = gate.EnterToReconcileAsync(Group, CancellationToken.None);

        second.IsCompleted.Should().BeTrue("drift of two instances in one group never waited on each other");
        (await second).Dispose();
    }

    [Fact]
    public async Task A_walk_waits_for_a_drift_reconcile_of_its_group_to_finish()
    {
        var gate = new WorkloadGroupGate();

        var drift = await gate.EnterToReconcileAsync(Group, CancellationToken.None);
        var walk = gate.EnterToWalkAsync(Group, CancellationToken.None);

        walk.IsCompleted.Should().BeFalse("a walk must not apply while drift is applying to the same group");
        drift.Dispose();
        (await walk.WaitAsync(TimeSpan.FromSeconds(5))).Dispose();
    }

    [Fact]
    public async Task A_drift_reconcile_waits_for_a_walk_of_its_group_to_finish()
    {
        var gate = new WorkloadGroupGate();

        var walk = await gate.EnterToWalkAsync(Group, CancellationToken.None);
        var drift = gate.EnterToReconcileAsync(Group, CancellationToken.None);

        drift.IsCompleted.Should().BeFalse("drift must not apply while the walk is applying to the same group");
        walk.Dispose();
        (await drift.WaitAsync(TimeSpan.FromSeconds(5))).Dispose();
    }

    [Fact]
    public async Task Two_walks_of_one_group_never_overlap()
    {
        var gate = new WorkloadGroupGate();

        var first = await gate.EnterToWalkAsync(Group, CancellationToken.None);
        var second = gate.EnterToWalkAsync(Group, CancellationToken.None);

        second.IsCompleted.Should().BeFalse();
        first.Dispose();
        (await second.WaitAsync(TimeSpan.FromSeconds(5))).Dispose();
    }

    [Fact]
    public async Task Different_groups_never_wait_on_each_other()
    {
        var gate = new WorkloadGroupGate();

        using var walk = await gate.EnterToWalkAsync(Group, CancellationToken.None);
        var otherWalk = gate.EnterToWalkAsync(OtherGroup, CancellationToken.None);
        var otherDrift = gate.EnterToReconcileAsync(OtherGroup, CancellationToken.None);

        otherWalk.IsCompleted.Should().BeTrue("the gate is keyed by group; nothing else waits on it");
        (await otherWalk).Dispose();
        otherDrift.IsCompleted.Should().BeTrue();
        (await otherDrift).Dispose();
    }

    [Fact]
    public async Task A_drift_reconcile_that_arrives_after_a_waiting_walk_waits_behind_it()
    {
        var gate = new WorkloadGroupGate();

        var early = await gate.EnterToReconcileAsync(Group, CancellationToken.None);
        var walk = gate.EnterToWalkAsync(Group, CancellationToken.None);
        var late = gate.EnterToReconcileAsync(Group, CancellationToken.None);

        late.IsCompleted.Should().BeFalse("a walk already waiting is not overtaken by drift that came later");
        early.Dispose();
        var walkHold = await walk.WaitAsync(TimeSpan.FromSeconds(5));
        late.IsCompleted.Should().BeFalse();
        walkHold.Dispose();
        (await late.WaitAsync(TimeSpan.FromSeconds(5))).Dispose();
    }

    [Fact]
    public async Task Every_drift_reconcile_queued_behind_a_walk_is_let_in_when_it_ends()
    {
        var gate = new WorkloadGroupGate();

        var walk = await gate.EnterToWalkAsync(Group, CancellationToken.None);
        var first = gate.EnterToReconcileAsync(Group, CancellationToken.None);
        var second = gate.EnterToReconcileAsync(Group, CancellationToken.None);

        walk.Dispose();

        var firstHold = await first.WaitAsync(TimeSpan.FromSeconds(5));
        var secondHold = await second.WaitAsync(TimeSpan.FromSeconds(5));
        firstHold.Dispose();
        secondHold.Dispose();
    }

    [Fact]
    public async Task A_cancelled_wait_leaves_the_queue_and_lets_the_next_one_in()
    {
        var gate = new WorkloadGroupGate();
        using var cancel = new CancellationTokenSource();

        var drift = await gate.EnterToReconcileAsync(Group, CancellationToken.None);
        var walk = gate.EnterToWalkAsync(Group, cancel.Token);
        var laterDrift = gate.EnterToReconcileAsync(Group, CancellationToken.None);

        await cancel.CancelAsync();

        await FluentActions.Awaiting(() => walk.WaitAsync(TimeSpan.FromSeconds(5)))
            .Should().ThrowAsync<OperationCanceledException>();
        (await laterDrift.WaitAsync(TimeSpan.FromSeconds(5))).Dispose();
        drift.Dispose();
        gate.GroupCount.Should().Be(0, "a group nobody holds or waits for is forgotten, cancelled waits included");
    }

    [Fact]
    public async Task A_group_is_forgotten_once_nobody_holds_it()
    {
        var gate = new WorkloadGroupGate();

        (await gate.EnterToWalkAsync(Group, CancellationToken.None)).Dispose();
        (await gate.EnterToReconcileAsync(OtherGroup, CancellationToken.None)).Dispose();

        gate.GroupCount.Should().Be(0, "one entry per group ever updated would grow for the life of the leader");
    }

    [Fact]
    public async Task Releasing_a_hold_twice_releases_it_once()
    {
        var gate = new WorkloadGroupGate();

        var walk = await gate.EnterToWalkAsync(Group, CancellationToken.None);
        walk.Dispose();
        var next = await gate.EnterToWalkAsync(Group, CancellationToken.None);
        walk.Dispose();

        var drift = gate.EnterToReconcileAsync(Group, CancellationToken.None);
        drift.IsCompleted.Should().BeFalse("a second Dispose of a spent hold must not release somebody else's");
        next.Dispose();
        (await drift.WaitAsync(TimeSpan.FromSeconds(5))).Dispose();
    }

    [Fact]
    public async Task Only_a_walk_ending_moves_the_epoch()
    {
        var gate = new WorkloadGroupGate();
        var before = gate.WalkEpoch;

        (await gate.EnterToReconcileAsync(Group, CancellationToken.None)).Dispose();
        gate.WalkEpoch.Should().Be(before);

        var walk = await gate.EnterToWalkAsync(Group, CancellationToken.None);
        gate.WalkEpoch.Should().Be(before, "the walk has written nothing a reader could have missed until it ends");
        walk.Dispose();
        gate.WalkEpoch.Should().NotBe(before);
    }
}
