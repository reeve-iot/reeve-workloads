using Reeve.Workloads.Abstractions;

namespace Reeve.Workloads.Core;

internal readonly record struct WorkloadReconcileWorkItem(
    Guid InstanceId, WorkloadReconcileTrigger Trigger, bool BarrierPoll = false);

internal readonly record struct WorkloadReconcileCompletion
{
    public bool Applied { get; init; }

    public bool Healthy { get; init; }

    public TimeSpan? RequeueAfter { get; init; }
}

internal enum WorkloadSettleResult
{
    None = 0,

    Settling = 1,

    Settled = 2,

    TimedOut = 3,
}

internal sealed class WorkloadReconcileQueue
{
    private sealed class Entry
    {
        public required Guid Key { get; init; }
        public WorkloadReconcileTrigger Trigger { get; set; }
        public DateTimeOffset DueAt { get; set; }
        public bool Running { get; set; }
        public bool Dirty { get; set; }
        public int ConsecutiveFaults { get; set; }

        public bool Nudged { get; set; }
    }

    private sealed class Settling
    {
        public required DateTimeOffset Deadline { get; init; }
        public DateTimeOffset LastDriveAt { get; set; }
    }

    private readonly object gate = new();
    private readonly Dictionary<Guid, Entry> entries = new();
    private readonly Dictionary<Guid, Settling> settling = new();
    private TaskCompletionSource changed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly IWorkloadClock clock;
    private readonly WorkloadReconcilerOptions options;
    private long waitCycles;

    public WorkloadReconcileQueue(IWorkloadClock clock, WorkloadReconcilerOptions options)
    {
        this.clock = clock;
        this.options = options;
    }

    internal long WaitCycles => Interlocked.Read(ref waitCycles);

    public int SettlingCount { get { lock (gate) { return settling.Count; } } }

    public int Count { get { lock (gate) { return entries.Count; } } }

    private static DateTimeOffset ScheduleNow => DateTimeOffset.UtcNow;

    public bool IsQuiescent { get { lock (gate) { return Quiescent(ScheduleNow); } } }

    public void Enqueue(Guid instanceId, WorkloadReconcileTrigger trigger, TimeSpan delay = default)
    {
        var dueAt = ScheduleNow + (delay > TimeSpan.Zero ? delay : TimeSpan.Zero);
        lock (gate)
        {
            if (entries.TryGetValue(instanceId, out var existing))
            {
                existing.Nudged = true;

                if (existing.Running)
                {
                    existing.Dirty = true;
                }
                else if (dueAt < existing.DueAt)
                {
                    existing.DueAt = dueAt;
                    existing.Trigger = trigger;
                }
            }
            else
            {
                entries[instanceId] = new Entry { Key = instanceId, Trigger = trigger, DueAt = dueAt, Nudged = true };
            }
        }

        SignalChanged();
    }

    public WorkloadReconcileWorkItem? TryTake()
    {
        var now = ScheduleNow;
        lock (gate)
        {
            if (AtWorkerLimit())
            {
                return null;
            }

            var settlingIsFull = SettlingIsFull();
            Entry? best = null;
            foreach (var entry in entries.Values)
            {
                if (!Eligible(entry, settlingIsFull) || entry.DueAt > now)
                {
                    continue;
                }

                if (best is null || entry.DueAt < best.DueAt || (entry.DueAt == best.DueAt && entry.Key.CompareTo(best.Key) < 0))
                {
                    best = entry;
                }
            }

            if (best is null)
            {
                return null;
            }

            best.Running = true;
            best.Dirty = false;

            var barrierPoll = false;
            if (!best.Nudged && settling.TryGetValue(best.Key, out var settlingState))
            {
                if (now - settlingState.LastDriveAt < options.HealthBarrierDriveInterval)
                {
                    barrierPoll = true;
                }
                else
                {
                    settlingState.LastDriveAt = now;
                }
            }

            best.Nudged = false;
            return new WorkloadReconcileWorkItem(best.Key, best.Trigger, barrierPoll);
        }
    }

    public async Task<WorkloadReconcileWorkItem?> DequeueAsync(CancellationToken cancellationToken, bool untilQuiescent = false)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            Task signal;
            lock (gate)
            {
                signal = changed.Task;
            }

            if (TryTake() is { } item)
            {
                return item;
            }

            if (untilQuiescent && IsQuiescent)
            {
                return null;
            }

            Interlocked.Increment(ref waitCycles);

            var wait = TimeUntilTakeable();
            if (wait == TimeSpan.Zero)
            {
                continue;
            }

            try
            {
                await signal.WaitAsync(wait, cancellationToken).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
            }
        }
    }

    public WorkloadSettleResult Complete(WorkloadReconcileWorkItem item, WorkloadReconcileCompletion completion)
    {
        var now = ScheduleNow;
        var logicalNow = clock.UtcNow;
        WorkloadSettleResult result;
        lock (gate)
        {
            result = CompleteLocked(item, completion, now, logicalNow);
        }

        SignalChanged();
        return result;
    }

    private WorkloadSettleResult CompleteLocked(
        WorkloadReconcileWorkItem item, WorkloadReconcileCompletion completion, DateTimeOffset now, DateTimeOffset logicalNow)
    {
        WorkloadSettleResult result;
        if (!entries.TryGetValue(item.InstanceId, out var entry))
        {
            return WorkloadSettleResult.None;
        }

        entry.Running = false;
        entry.ConsecutiveFaults = 0;

        var requeueAfter = completion.RequeueAfter;
        if (settling.TryGetValue(item.InstanceId, out var state))
        {
            if (completion.Healthy)
            {
                settling.Remove(item.InstanceId);
                result = WorkloadSettleResult.Settled;
            }
            else if (logicalNow >= state.Deadline)
            {
                settling.Remove(item.InstanceId);
                result = WorkloadSettleResult.TimedOut;
            }
            else
            {
                requeueAfter = Soonest(requeueAfter, options.HealthBarrierPollInterval);
                result = WorkloadSettleResult.Settling;
            }
        }
        else if (completion.Applied && !completion.Healthy)
        {
            settling[item.InstanceId] = new Settling { Deadline = logicalNow + options.HealthBarrierTimeout, LastDriveAt = now };
            requeueAfter = Soonest(requeueAfter, options.HealthBarrierPollInterval);
            result = WorkloadSettleResult.Settling;
        }
        else
        {
            result = WorkloadSettleResult.None;
        }

        if (entry.Dirty)
        {
            requeueAfter = Soonest(requeueAfter, TimeSpan.Zero);
        }

        if (requeueAfter is { } delay)
        {
            entry.DueAt = now + delay;
            entry.Dirty = false;
        }
        else
        {
            entries.Remove(item.InstanceId);
        }

        return result;
    }

    public TimeSpan Fault(WorkloadReconcileWorkItem item)
    {
        var now = ScheduleNow;
        var delay = TimeSpan.Zero;
        lock (gate)
        {
            if (entries.TryGetValue(item.InstanceId, out var entry))
            {
                entry.Running = false;
                entry.Dirty = false;
                entry.ConsecutiveFaults++;
                delay = BackoffFor(entry.ConsecutiveFaults);
                entry.DueAt = now + delay;
            }
        }

        SignalChanged();
        return delay;
    }

    public void Clear()
    {
        lock (gate)
        {
            entries.Clear();
            settling.Clear();
        }

        SignalChanged();
    }

    private void SignalChanged()
    {
        TaskCompletionSource previous;
        lock (gate)
        {
            previous = changed;
            changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        previous.TrySetResult();
    }

    private bool Quiescent(DateTimeOffset now)
    {
        if (settling.Count > 0)
        {
            return false;
        }

        foreach (var entry in entries.Values)
        {
            if (entry.Running || entry.DueAt <= now)
            {
                return false;
            }
        }

        return true;
    }

    private TimeSpan BackoffFor(int consecutiveFaults)
    {
        var doublings = Math.Min(consecutiveFaults - 1, 30);
        var scaled = options.RetryBackoffBase * Math.Pow(2, doublings);
        var capped = Math.Min(scaled.TotalMilliseconds, options.RetryBackoffMax.TotalMilliseconds);
        return TimeSpan.FromMilliseconds(capped * (0.5 + (Random.Shared.NextDouble() * 0.5)));
    }

    private bool AtWorkerLimit()
    {
        var running = 0;
        foreach (var entry in entries.Values)
        {
            if (entry.Running)
            {
                running++;
            }
        }

        return running >= options.ReconcileWorkerCount;
    }

    private bool SettlingIsFull() => settling.Count >= options.MaxConcurrentSettling;

    private bool Eligible(Entry entry, bool settlingIsFull)
        => !entry.Running && (!settlingIsFull || settling.ContainsKey(entry.Key));

    private TimeSpan TimeUntilTakeable()
    {
        var now = ScheduleNow;
        lock (gate)
        {
            if (AtWorkerLimit())
            {
                return Timeout.InfiniteTimeSpan;
            }

            var settlingIsFull = SettlingIsFull();
            DateTimeOffset? next = null;
            foreach (var entry in entries.Values)
            {
                if (!Eligible(entry, settlingIsFull))
                {
                    continue;
                }

                if (next is null || entry.DueAt < next)
                {
                    next = entry.DueAt;
                }
            }

            if (next is null)
            {
                return Timeout.InfiniteTimeSpan;
            }

            var wait = next.Value - now;
            return wait > TimeSpan.Zero ? wait : TimeSpan.Zero;
        }
    }

    private static TimeSpan? Soonest(TimeSpan? left, TimeSpan right)
        => left is { } value && value < right ? value : right;
}
