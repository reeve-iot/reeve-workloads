using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Reeve.Workloads.Core.Watch;
using Xunit;

namespace Reeve.Workloads.Core.Tests;

public sealed class WorkloadWatchCoalescerTests
{
    private static readonly TimeSpan Window = TimeSpan.FromMilliseconds(50);
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task Burst_of_marks_for_one_instance_coalesces_to_a_single_reconcile()
    {
        var id = Guid.NewGuid();
        var reconciled = new ConcurrentQueue<Guid>();
        var delay = new ControlledDebounce();

        await using var coalescer = new WorkloadWatchCoalescer(
            Window,
            (instanceId, _) => { reconciled.Enqueue(instanceId); return Task.CompletedTask; },
            _ => Task.CompletedTask,
            NullLogger<WorkloadWatchCoalescer>.Instance,
            delay.DelayAsync);

        var cycle = WaitForNextCycle(coalescer);

        for (var i = 0; i < 5; i++)
        {
            coalescer.MarkDirty(id);
        }

        await delay.WaitUntilPendingAsync();
        delay.CompleteNext();
        await cycle;

        reconciled.Should().ContainSingle().Which.Should().Be(id);
    }

    [Fact]
    public async Task Marks_for_distinct_instances_reconcile_each_once()
    {
        var x = Guid.NewGuid();
        var y = Guid.NewGuid();
        var reconciled = new ConcurrentBag<Guid>();
        var delay = new ControlledDebounce();

        await using var coalescer = new WorkloadWatchCoalescer(
            Window,
            (instanceId, _) => { reconciled.Add(instanceId); return Task.CompletedTask; },
            _ => Task.CompletedTask,
            NullLogger<WorkloadWatchCoalescer>.Instance,
            delay.DelayAsync);

        var cycle = WaitForNextCycle(coalescer);

        coalescer.MarkDirty(x);
        coalescer.MarkDirty(x);
        coalescer.MarkDirty(y);

        await delay.WaitUntilPendingAsync();
        delay.CompleteNext();
        await cycle;

        reconciled.Should().BeEquivalentTo(new[] { x, y });
    }

    [Fact]
    public async Task Mark_arriving_during_the_drain_schedules_a_second_window()
    {
        var id = Guid.NewGuid();
        var callCount = 0;
        var remarked = false;
        var delay = new ControlledDebounce();

        WorkloadWatchCoalescer? captured = null;
        await using var coalescer = new WorkloadWatchCoalescer(
            Window,
            (_, _) =>
            {
                Interlocked.Increment(ref callCount);
                if (!remarked)
                {
                    remarked = true;
                    captured!.MarkDirty(id);
                }

                return Task.CompletedTask;
            },
            _ => Task.CompletedTask,
            NullLogger<WorkloadWatchCoalescer>.Instance,
            delay.DelayAsync);
        captured = coalescer;

        var cycle1 = WaitForNextCycle(coalescer);
        coalescer.MarkDirty(id);
        await delay.WaitUntilPendingAsync();
        delay.CompleteNext();
        await cycle1;

        var cycle2 = WaitForNextCycle(coalescer);
        await delay.WaitUntilPendingAsync();
        delay.CompleteNext();
        await cycle2;

        callCount.Should().Be(2);
    }

    [Fact]
    public async Task MarkAllDirty_coalesces_to_a_single_all_signal()
    {
        var allCount = 0;
        var perInstance = 0;
        var delay = new ControlledDebounce();

        await using var coalescer = new WorkloadWatchCoalescer(
            Window,
            (_, _) => { Interlocked.Increment(ref perInstance); return Task.CompletedTask; },
            _ => { Interlocked.Increment(ref allCount); return Task.CompletedTask; },
            NullLogger<WorkloadWatchCoalescer>.Instance,
            delay.DelayAsync);

        var cycle = WaitForNextCycle(coalescer);

        coalescer.MarkAllDirty();
        coalescer.MarkAllDirty();
        coalescer.MarkDirty(Guid.NewGuid());

        await delay.WaitUntilPendingAsync();
        delay.CompleteNext();
        await cycle;

        allCount.Should().Be(1);
        perInstance.Should().Be(0, "an all-reconcile subsumes the per-instance marks in the same window");
    }

    [Fact]
    public async Task A_throwing_callback_for_one_instance_does_not_prevent_the_other()
    {
        var faulting = Guid.NewGuid();
        var healthy = Guid.NewGuid();
        var reconciled = new ConcurrentBag<Guid>();
        var delay = new ControlledDebounce();

        await using var coalescer = new WorkloadWatchCoalescer(
            Window,
            (instanceId, _) =>
            {
                if (instanceId == faulting)
                {
                    throw new InvalidOperationException("boom");
                }

                reconciled.Add(instanceId);
                return Task.CompletedTask;
            },
            _ => Task.CompletedTask,
            NullLogger<WorkloadWatchCoalescer>.Instance,
            delay.DelayAsync);

        var cycle = WaitForNextCycle(coalescer);

        coalescer.MarkDirty(faulting);
        coalescer.MarkDirty(healthy);

        await delay.WaitUntilPendingAsync();
        delay.CompleteNext();
        await cycle;

        reconciled.Should().ContainSingle().Which.Should().Be(healthy);
    }

    [Fact]
    public async Task Dispose_while_a_window_is_pending_completes_without_deadlock()
    {
        var id = Guid.NewGuid();
        var delay = new ControlledDebounce();

        var coalescer = new WorkloadWatchCoalescer(
            Window,
            (_, _) => Task.CompletedTask,
            _ => Task.CompletedTask,
            NullLogger<WorkloadWatchCoalescer>.Instance,
            delay.DelayAsync);

        coalescer.MarkDirty(id);
        await delay.WaitUntilPendingAsync();

        var dispose = coalescer.DisposeAsync().AsTask();
        (await Task.WhenAny(dispose, Task.Delay(TestTimeout))).Should().Be(dispose, "DisposeAsync must not deadlock");
        await dispose;

        await coalescer.DisposeAsync();
        coalescer.MarkDirty(id);
    }

    [Fact]
    public async Task An_unexpected_pump_loop_fault_signals_pump_faulted_instead_of_dying_silently()
    {
        var faulted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Func<TimeSpan, CancellationToken, Task> throwingDelay = (_, _) => throw new InvalidOperationException("pump bug");

        await using var coalescer = new WorkloadWatchCoalescer(
            Window,
            (_, _) => Task.CompletedTask,
            _ => Task.CompletedTask,
            NullLogger<WorkloadWatchCoalescer>.Instance,
            throwingDelay,
            lifetime: default,
            onPumpFaulted: () => faulted.TrySetResult());

        coalescer.MarkDirty(Guid.NewGuid());

        (await Task.WhenAny(faulted.Task, Task.Delay(TestTimeout))).Should().Be(
            faulted.Task, "an unexpected pump-loop fault must signal pump-faulted (never die silently looking alive)");
        await faulted.Task;
    }

    [Fact]
    public async Task A_per_callback_throw_is_isolated_and_does_NOT_signal_pump_faulted()
    {
        var pumpFaulted = false;
        var delay = new ControlledDebounce();

        await using var coalescer = new WorkloadWatchCoalescer(
            Window,
            (_, _) => throw new InvalidOperationException("callback boom"),
            _ => Task.CompletedTask,
            NullLogger<WorkloadWatchCoalescer>.Instance,
            delay.DelayAsync,
            lifetime: default,
            onPumpFaulted: () => Volatile.Write(ref pumpFaulted, true));

        var cycle = WaitForNextCycle(coalescer);
        coalescer.MarkDirty(Guid.NewGuid());
        await delay.WaitUntilPendingAsync();
        delay.CompleteNext();
        await cycle;

        Volatile.Read(ref pumpFaulted).Should().BeFalse(
            "a per-callback throw is isolated and must not be treated as a pump-loop death");
    }

    private static Task WaitForNextCycle(WorkloadWatchCoalescer coalescer)
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void Handler()
        {
            coalescer.CycleDrained -= Handler;
            tcs.TrySetResult();
        }

        coalescer.CycleDrained += Handler;
        return tcs.Task.WaitAsync(TestTimeout);
    }

    private sealed class ControlledDebounce
    {
        private readonly object gate = new();
        private readonly Queue<TaskCompletionSource> pending = new();
        private readonly SemaphoreSlim started = new(0);

        public Task DelayAsync(TimeSpan window, CancellationToken cancellationToken)
        {
            var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            cancellationToken.Register(() => tcs.TrySetCanceled(cancellationToken));
            lock (gate)
            {
                pending.Enqueue(tcs);
            }

            started.Release();
            return tcs.Task;
        }

        public async Task WaitUntilPendingAsync() => await started.WaitAsync(TestTimeout);

        public void CompleteNext()
        {
            TaskCompletionSource tcs;
            lock (gate)
            {
                tcs = pending.Dequeue();
            }

            tcs.TrySetResult();
        }
    }
}
