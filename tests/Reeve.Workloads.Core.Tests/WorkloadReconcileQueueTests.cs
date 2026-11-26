using FluentAssertions;
using Reeve.Workloads.Core;
using Reeve.Workloads.Core.Tests.Update;
using Xunit;

namespace Reeve.Workloads.Core.Tests;

public sealed class WorkloadReconcileQueueTests
{
    private static readonly Guid A = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid B = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly DateTimeOffset T0 = new(2026, 9, 12, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void The_same_instance_enqueued_twice_is_one_entry()
    {
        var queue = NewQueue(out _);

        queue.Enqueue(A, WorkloadReconcileTrigger.Watch);
        queue.Enqueue(A, WorkloadReconcileTrigger.BusNudge);

        queue.Count.Should().Be(1, "two nudges for one instance are one piece of work, not two");
        queue.TryTake().Should().NotBeNull();
        queue.TryTake().Should().BeNull("the second nudge merged into the first rather than queueing behind it");
    }

    [Fact]
    public void A_storm_during_a_run_produces_exactly_one_follow_up()
    {
        var queue = NewQueue(out _);
        queue.Enqueue(A, WorkloadReconcileTrigger.Watch);
        var running = queue.TryTake()!.Value;

        for (var i = 0; i < 500; i++)
        {
            queue.Enqueue(A, WorkloadReconcileTrigger.Watch);
        }

        queue.Complete(running, default);

        queue.TryTake().Should().NotBeNull("the storm is not lost — one follow-up run is owed");
        queue.TryTake().Should().BeNull("…and exactly one, however many events arrived");
    }

    [Fact]
    public void A_quiet_run_leaves_nothing_scheduled()
    {
        var queue = NewQueue(out _);
        queue.Enqueue(A, WorkloadReconcileTrigger.Timer);

        queue.Complete(queue.TryTake()!.Value, default);

        queue.Count.Should().Be(0, "nothing asked for a follow-up, so the key leaves the queue entirely");
        queue.IsQuiescent.Should().BeTrue();
    }

    [Fact]
    public void One_worker_means_one_run_at_a_time()
    {
        var queue = NewQueue(out _);
        queue.Enqueue(A, WorkloadReconcileTrigger.Timer);
        queue.Enqueue(B, WorkloadReconcileTrigger.Timer);

        queue.TryTake().Should().NotBeNull();

        queue.TryTake().Should().BeNull(
            "the concurrency limit is the queue's, not the worker count's — a fleet pass drains beside the workers");
    }

    [Fact]
    public void Raising_the_worker_count_admits_that_many_runs()
    {
        var queue = NewQueue(out _, workers: 2);
        queue.Enqueue(A, WorkloadReconcileTrigger.Timer);
        queue.Enqueue(B, WorkloadReconcileTrigger.Timer);

        queue.TryTake().Should().NotBeNull();
        queue.TryTake().Should().NotBeNull("two workers were configured, so two runs are admissible");
    }

    [Fact]
    public void An_applied_instance_holds_the_only_settling_slot()
    {
        var queue = NewQueue(out _);
        queue.Enqueue(A, WorkloadReconcileTrigger.Timer);
        queue.Enqueue(B, WorkloadReconcileTrigger.Timer);

        var settled = queue.Complete(queue.TryTake()!.Value, Applied(healthy: false));

        settled.Should().Be(WorkloadSettleResult.Settling);
        queue.SettlingCount.Should().Be(1);
        queue.TryTake().Should().BeNull(
            "B must wait: a fleet-wide re-stamp that applied to everything at once is exactly what the barrier prevents");
    }

    [Fact]
    public void Reaching_Healthy_releases_the_slot_and_lets_the_fleet_move_on()
    {
        var queue = NewQueue(out _);
        queue.Enqueue(A, WorkloadReconcileTrigger.Timer);
        queue.Enqueue(B, WorkloadReconcileTrigger.Timer);
        var first = queue.TryTake()!.Value;
        queue.Complete(first, Applied(healthy: false));

        Thread.Sleep(15);
        var redrive = queue.TryTake();
        redrive.Should().NotBeNull("only the settling key may run while the slot is held — and it must, or nothing advances it");
        redrive!.Value.InstanceId.Should().Be(first.InstanceId);

        queue.Complete(redrive.Value, Applied(healthy: true)).Should().Be(WorkloadSettleResult.Settled);

        queue.SettlingCount.Should().Be(0);
        queue.TryTake().Should().NotBeNull("the slot is free, so the next instance runs");
    }

    [Fact]
    public void A_settling_key_nobody_asked_about_is_answered_with_a_poll()
    {
        var queue = NewQueue(out _);
        queue.Enqueue(A, WorkloadReconcileTrigger.Timer);
        queue.Complete(queue.TryTake()!.Value, Applied(healthy: false));

        Thread.Sleep(15);
        var tick = queue.TryTake();

        tick.Should().NotBeNull();
        tick!.Value.BarrierPoll.Should().BeTrue(
            "the instance was just driven; within the drive interval the barrier only needs to READ the row");
    }

    [Fact]
    public void A_nudge_is_never_answered_with_a_poll()
    {
        var queue = NewQueue(out _);
        queue.Enqueue(A, WorkloadReconcileTrigger.Timer);
        queue.Complete(queue.TryTake()!.Value, Applied(healthy: false));

        queue.Enqueue(A, WorkloadReconcileTrigger.BusNudge);

        Thread.Sleep(15);
        var tick = queue.TryTake();

        tick.Should().NotBeNull();
        tick!.Value.BarrierPoll.Should().BeFalse(
            "a key that was ASKED for takes the full path, settling or not");
    }

    [Fact]
    public void The_nudge_is_spent_by_the_take_that_answered_it()
    {
        var queue = NewQueue(out _);
        queue.Enqueue(A, WorkloadReconcileTrigger.Timer);
        queue.Complete(queue.TryTake()!.Value, Applied(healthy: false));
        queue.Enqueue(A, WorkloadReconcileTrigger.BusNudge);

        Thread.Sleep(15);
        var nudged = queue.TryTake()!.Value;
        nudged.BarrierPoll.Should().BeFalse();
        queue.Complete(nudged, Applied(healthy: false));

        Thread.Sleep(15);
        queue.TryTake()!.Value.BarrierPoll.Should().BeTrue("nothing has asked for this key since the nudge was answered");
    }

    [Fact]
    public void A_stuck_instance_gives_its_slot_back_when_the_barrier_deadline_passes()
    {
        var queue = NewQueue(out var clock);
        queue.Enqueue(A, WorkloadReconcileTrigger.Timer);
        queue.Enqueue(B, WorkloadReconcileTrigger.Timer);
        var first = queue.TryTake()!.Value;
        queue.Complete(first, Applied(healthy: false));

        clock.Advance(TimeSpan.FromMinutes(5));
        Thread.Sleep(15);
        var redrive = queue.TryTake()!.Value;

        queue.Complete(redrive, Applied(healthy: false)).Should().Be(WorkloadSettleResult.TimedOut);
        queue.SettlingCount.Should().Be(0,
            "one instance that never comes up must not hold the fleet forever — the same trade the old timeout made");
    }

    [Fact]
    public void A_status_only_pass_imposes_no_barrier()
    {
        var queue = NewQueue(out _);
        queue.Enqueue(A, WorkloadReconcileTrigger.Timer);
        queue.Enqueue(B, WorkloadReconcileTrigger.Timer);

        queue.Complete(queue.TryTake()!.Value, new WorkloadReconcileCompletion { Applied = false, Healthy = false })
            .Should().Be(WorkloadSettleResult.None);

        queue.SettlingCount.Should().Be(0);
        queue.TryTake().Should().NotBeNull("nothing was applied, so nothing has to settle before the next instance");
    }

    [Fact]
    public void A_faulting_instance_backs_off_further_each_time()
    {
        var queue = NewQueue(out _);
        var delays = new List<TimeSpan>();

        for (var attempt = 0; attempt < 4; attempt++)
        {
            queue.Enqueue(A, WorkloadReconcileTrigger.Timer);
            delays.Add(queue.Fault(queue.TryTake()!.Value));
        }

        delays[3].Should().BeGreaterThan(delays[0] + delays[0],
            "an instance that never comes up must stop being retried at full cost — that is the finding");
        delays[3].Should().BeGreaterThan(TimeSpan.FromMilliseconds(20),
            "the fourth delay is four doublings up from a 10 ms base, so no amount of jitter puts it near the first");
    }

    [Fact]
    public void The_backoff_is_capped()
    {
        var queue = NewQueue(out _);

        for (var attempt = 0; attempt < 25; attempt++)
        {
            queue.Enqueue(A, WorkloadReconcileTrigger.Timer);
            var delay = queue.Fault(queue.TryTake()!.Value);
            delay.Should().BeLessThanOrEqualTo(TimeSpan.FromSeconds(2),
                "doubling without a cap drifts into hours and the instance is never retried on a cadence a human recognises");
        }
    }

    [Fact]
    public void A_successful_run_forgets_the_faults_before_it()
    {
        var queue = NewQueue(out _);
        queue.Enqueue(A, WorkloadReconcileTrigger.Timer);
        for (var attempt = 0; attempt < 6; attempt++)
        {
            queue.Fault(queue.TryTake()!.Value);
            Thread.Sleep(5);
            queue.Enqueue(A, WorkloadReconcileTrigger.Timer);
        }

        queue.Complete(queue.TryTake()!.Value, default);
        queue.Enqueue(A, WorkloadReconcileTrigger.Timer);
        var afterRecovery = queue.Fault(queue.TryTake()!.Value);

        afterRecovery.Should().BeLessThan(TimeSpan.FromMilliseconds(20),
            "an instance that recovered and then failed once is a first failure, not a seventh");
    }

    [Fact]
    public void A_backed_off_instance_does_not_hold_a_pass_open()
    {
        var queue = NewQueue(out _);
        queue.Enqueue(A, WorkloadReconcileTrigger.Timer);

        queue.Fault(queue.TryTake()!.Value);

        queue.IsQuiescent.Should().BeTrue(
            "a key waiting out its backoff is work for later; a drift pass that waited for it would never return");
        queue.Count.Should().Be(1, "…but it is still scheduled — quiet is not the same as forgotten");
    }

    [Fact]
    public void RequeueAfter_from_the_engine_keeps_the_key_scheduled()
    {
        var queue = NewQueue(out _);
        queue.Enqueue(A, WorkloadReconcileTrigger.Timer);

        queue.Complete(
            queue.TryTake()!.Value,
            new WorkloadReconcileCompletion { RequeueAfter = TimeSpan.FromMilliseconds(5) });

        queue.Count.Should().Be(1);
        queue.TryTake().Should().BeNull("it is not due yet");
        Thread.Sleep(15);
        queue.TryTake().Should().NotBeNull("the engine asked to be called again, and it was");
    }

    [Fact]
    public void The_settling_poll_wins_when_the_engine_asks_for_something_later()
    {
        var queue = NewQueue(out _);
        queue.Enqueue(A, WorkloadReconcileTrigger.Timer);

        queue.Complete(
            queue.TryTake()!.Value,
            new WorkloadReconcileCompletion { Applied = true, RequeueAfter = TimeSpan.FromHours(1) });

        Thread.Sleep(15);
        queue.TryTake().Should().NotBeNull(
            "nothing else advances a settling instance toward Healthy, so the barrier's poll cannot be deferred by an hour");
    }

    [Fact]
    public void Nothing_is_handed_out_before_it_is_due()
    {
        var queue = NewQueue(out _);

        queue.Enqueue(A, WorkloadReconcileTrigger.Timer, TimeSpan.FromSeconds(30));

        queue.TryTake().Should().BeNull();
        queue.IsQuiescent.Should().BeTrue();
    }

    [Fact]
    public void Clear_drops_the_queue_and_the_settling_set()
    {
        var queue = NewQueue(out _);
        queue.Enqueue(A, WorkloadReconcileTrigger.Timer);
        queue.Complete(queue.TryTake()!.Value, Applied(healthy: false));

        queue.Clear();

        queue.Count.Should().Be(0);
        queue.SettlingCount.Should().Be(0, "a stopped unit must not hand its successor a half-held barrier");
    }

    [Fact]
    public async Task Dequeue_returns_null_once_the_queue_is_quiet()
    {
        var queue = NewQueue(out _);

        var taken = await queue.DequeueAsync(CancellationToken.None, untilQuiescent: true);

        taken.Should().BeNull("this is how a fleet pass knows it is done without any worker being present");
    }

    [Fact]
    public async Task Dequeue_wakes_on_an_enqueue_rather_than_waiting_out_the_next_due_time()
    {
        var queue = NewQueue(out _);
        queue.Enqueue(A, WorkloadReconcileTrigger.Timer, TimeSpan.FromMinutes(10));
        var waiting = queue.DequeueAsync(CancellationToken.None);

        queue.Enqueue(B, WorkloadReconcileTrigger.BusNudge);

        var taken = await waiting.WaitAsync(TimeSpan.FromSeconds(5));
        taken!.Value.InstanceId.Should().Be(B, "a nudge must not sit behind an unrelated key's ten-minute timer");
    }

    [Fact]
    public void A_settling_instance_holds_a_pass_open()
    {
        var queue = NewQueue(out _);
        queue.Enqueue(A, WorkloadReconcileTrigger.Timer);

        queue.Complete(queue.TryTake()!.Value, Applied(healthy: false));

        queue.IsQuiescent.Should().BeFalse(
            "the re-drive is a few milliseconds away, and a drift pass that returned now would have skipped the barrier");
    }

    [Fact]
    public void An_already_settling_instance_cannot_have_its_re_drive_deferred_by_the_engine()
    {
        var queue = NewQueue(out _);
        queue.Enqueue(A, WorkloadReconcileTrigger.Timer);
        queue.Complete(queue.TryTake()!.Value, Applied(healthy: false));
        Thread.Sleep(15);

        queue.Complete(
            queue.TryTake()!.Value,
            new WorkloadReconcileCompletion { Applied = true, RequeueAfter = TimeSpan.FromHours(1) });

        Thread.Sleep(15);
        queue.TryTake().Should().NotBeNull("the settling poll still wins on the second re-drive, not only the first");
    }

    [Fact]
    public void A_run_that_stays_scheduled_still_forgets_the_faults_before_it()
    {
        var queue = NewQueue(out _);
        for (var attempt = 0; attempt < 6; attempt++)
        {
            queue.Enqueue(A, WorkloadReconcileTrigger.Timer);
            queue.Fault(queue.TryTake()!.Value);
            Thread.Sleep(2);
        }

        queue.Enqueue(A, WorkloadReconcileTrigger.Timer);
        queue.Complete(queue.TryTake()!.Value, Applied(healthy: false));
        Thread.Sleep(15);
        var afterRecovery = queue.Fault(queue.TryTake()!.Value);

        afterRecovery.Should().BeLessThan(TimeSpan.FromMilliseconds(20),
            "the instance reconciled successfully, so the next failure is its first — not its seventh");
    }

    [Fact]
    public async Task A_waiter_does_not_spin_while_the_workers_are_saturated()
    {
        var queue = NewQueue(out _);
        queue.Enqueue(A, WorkloadReconcileTrigger.Timer);
        queue.Enqueue(B, WorkloadReconcileTrigger.Timer);
        queue.TryTake().Should().NotBeNull("A is now running, which is the whole worker budget");

        using var cts = new CancellationTokenSource();
        var before = queue.WaitCycles;
        var waiting = queue.DequeueAsync(cts.Token);
        await Task.Delay(200);
        var cycles = queue.WaitCycles - before;

        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);

        cycles.Should().BeLessThan(5,
            "nothing can be taken until the run in flight ends, and that signals — so the waiter parks once "
            + "instead of turning the loop for 200 ms");
    }

    [Fact]
    public async Task A_waiter_does_not_spin_while_the_settling_set_is_full()
    {
        var queue = NewQueue(out _, pollMilliseconds: 5_000);
        queue.Enqueue(A, WorkloadReconcileTrigger.Timer);
        queue.Enqueue(B, WorkloadReconcileTrigger.Timer);
        queue.Complete(queue.TryTake()!.Value, Applied(healthy: false));
        queue.SettlingCount.Should().Be(1, "B is now barred by the slot rather than by the worker limit");

        using var cts = new CancellationTokenSource();
        var before = queue.WaitCycles;
        var waiting = queue.DequeueAsync(cts.Token);
        await Task.Delay(200);
        var cycles = queue.WaitCycles - before;

        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);

        cycles.Should().BeLessThan(5,
            "B being due is not B being takeable — the honest wait is the settling key's re-drive, five seconds out");
    }

    [Fact]
    public async Task A_waiter_barred_by_the_settling_slot_still_wakes_at_the_settling_key_s_re_drive()
    {
        var queue = NewQueue(out _, pollMilliseconds: 60);
        queue.Enqueue(A, WorkloadReconcileTrigger.Timer);
        queue.Enqueue(B, WorkloadReconcileTrigger.Timer);
        queue.Complete(queue.TryTake()!.Value, Applied(healthy: false));

        var taken = await queue.DequeueAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));

        taken!.Value.InstanceId.Should().Be(A,
            "the settling key's own re-drive is the only thing that can free the slot, so it is what the wait is for");
    }

    [Fact]
    public async Task A_waiter_barred_by_the_worker_limit_wakes_when_the_run_completes()
    {
        var queue = NewQueue(out _, pollMilliseconds: 5_000);
        queue.Enqueue(A, WorkloadReconcileTrigger.Timer);
        queue.Enqueue(B, WorkloadReconcileTrigger.Timer);
        var running = queue.TryTake()!.Value;

        var waiting = queue.DequeueAsync(CancellationToken.None);
        await Task.Delay(50);
        waiting.IsCompleted.Should().BeFalse("the worker budget is spent");

        queue.Complete(running, default);

        var taken = await waiting.WaitAsync(TimeSpan.FromSeconds(5));
        taken!.Value.InstanceId.Should().Be(B);
    }

    [Fact]
    public async Task A_waiter_barred_by_the_worker_limit_wakes_when_the_run_faults()
    {
        var queue = NewQueue(out _, pollMilliseconds: 5_000);
        queue.Enqueue(A, WorkloadReconcileTrigger.Timer);
        queue.Enqueue(B, WorkloadReconcileTrigger.Timer);
        var running = queue.TryTake()!.Value;

        var waiting = queue.DequeueAsync(CancellationToken.None);
        await Task.Delay(50);
        waiting.IsCompleted.Should().BeFalse();

        queue.Fault(running);

        var taken = await waiting.WaitAsync(TimeSpan.FromSeconds(5));
        taken!.Value.InstanceId.Should().Be(B, "A is backing off; B was waiting on the worker, not on the clock");
    }

    [Fact]
    public async Task A_waiter_barred_by_the_settling_slot_wakes_when_the_slot_is_released()
    {
        var queue = NewQueue(out _, workers: 2, pollMilliseconds: 5_000);
        queue.Enqueue(A, WorkloadReconcileTrigger.Timer);
        queue.Enqueue(B, WorkloadReconcileTrigger.Timer);
        queue.Complete(queue.TryTake()!.Value, Applied(healthy: false));

        queue.Enqueue(A, WorkloadReconcileTrigger.Watch);
        var redrive = queue.TryTake()!.Value;
        redrive.InstanceId.Should().Be(A);

        var waiting = queue.DequeueAsync(CancellationToken.None);
        await Task.Delay(50);
        waiting.IsCompleted.Should().BeFalse("one of two workers is busy, so it is the settling slot that bars B");

        queue.Complete(redrive, Applied(healthy: true));

        var taken = await waiting.WaitAsync(TimeSpan.FromSeconds(5));
        taken!.Value.InstanceId.Should().Be(B);
        queue.SettlingCount.Should().Be(0);
    }

    [Fact]
    public async Task A_parked_drain_returns_when_the_queue_is_cleared()
    {
        var queue = NewQueue(out _, pollMilliseconds: 30_000);
        queue.Enqueue(A, WorkloadReconcileTrigger.Timer);
        queue.Enqueue(B, WorkloadReconcileTrigger.Timer);
        queue.Complete(queue.TryTake()!.Value, Applied(healthy: false));

        var waiting = queue.DequeueAsync(CancellationToken.None, untilQuiescent: true);
        await Task.Delay(50);
        waiting.IsCompleted.Should().BeFalse("a settling instance holds a pass open");

        queue.Clear();

        (await waiting.WaitAsync(TimeSpan.FromSeconds(5))).Should().BeNull(
            "the settling set is gone, so the pass is quiet and the drain is done");
    }

    [Fact]
    public async Task A_parked_waiter_observes_its_own_cancellation()
    {
        var queue = NewQueue(out _, pollMilliseconds: 5_000);
        queue.Enqueue(A, WorkloadReconcileTrigger.Timer);
        queue.Enqueue(B, WorkloadReconcileTrigger.Timer);
        queue.TryTake().Should().NotBeNull();

        using var cts = new CancellationTokenSource();
        var waiting = queue.DequeueAsync(cts.Token);
        await Task.Delay(50);
        waiting.IsCompleted.Should().BeFalse();

        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
    }

    [Fact]
    public async Task A_second_worker_is_admitted_while_the_first_is_running()
    {
        var queue = NewQueue(out _, workers: 2, pollMilliseconds: 5_000);
        queue.Enqueue(A, WorkloadReconcileTrigger.Timer);
        queue.Enqueue(B, WorkloadReconcileTrigger.Timer);
        queue.TryTake().Should().NotBeNull();

        var taken = await queue.DequeueAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(1));

        taken!.Value.InstanceId.Should().Be(B);
    }

    private static WorkloadReconcileCompletion Applied(bool healthy)
        => new() { Applied = true, Healthy = healthy };

    private static WorkloadReconcileQueue NewQueue(out MutableClock clock, int workers = 1, int pollMilliseconds = 10)
    {
        clock = new MutableClock(T0);
        return new WorkloadReconcileQueue(
            clock,
            new WorkloadReconcilerOptions
            {
                ReconcileWorkerCount = workers,
                MaxConcurrentSettling = 1,
                HealthBarrierPollMilliseconds = pollMilliseconds,
                HealthBarrierTimeoutSeconds = 60,
                RetryBackoffBaseMilliseconds = 10,
                RetryBackoffMaxSeconds = 2,
            });
    }
}
