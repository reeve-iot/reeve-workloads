namespace Reeve.Workloads.Core;

internal sealed class WorkloadGroupGate
{
    private readonly object sync = new();
    private readonly Dictionary<Guid, GroupState> groups = new();
    private long walkEpoch;

    public long WalkEpoch => Interlocked.Read(ref walkEpoch);

    internal int GroupCount
    {
        get
        {
            lock (sync)
            {
                return groups.Count;
            }
        }
    }

    public Task<IDisposable> EnterToReconcileAsync(Guid group, CancellationToken cancellationToken)
        => EnterAsync(group, exclusive: false, cancellationToken);

    public Task<IDisposable> EnterToWalkAsync(Guid group, CancellationToken cancellationToken)
        => EnterAsync(group, exclusive: true, cancellationToken);

    private Task<IDisposable> EnterAsync(Guid group, bool exclusive, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        lock (sync)
        {
            if (!groups.TryGetValue(group, out var state))
            {
                state = new GroupState();
                groups[group] = state;
            }

            if (state.Waiters.Count == 0 && state.Admits(exclusive))
            {
                state.Grant(exclusive);
                return Task.FromResult<IDisposable>(new Hold(this, group, exclusive));
            }

            var waiter = new Waiter(exclusive);
            state.Waiters.AddLast(waiter);
            if (cancellationToken.CanBeCanceled)
            {
                waiter.Registration = cancellationToken.Register(() => Abandon(group, waiter, cancellationToken));
            }

            return waiter.Granted.Task;
        }
    }

    private void Abandon(Guid group, Waiter waiter, CancellationToken cancellationToken)
    {
        lock (sync)
        {
            if (!groups.TryGetValue(group, out var state) || !state.Waiters.Remove(waiter))
            {
                return;
            }

            waiter.Granted.TrySetCanceled(cancellationToken);

            GrantWaiters(group, state);
        }
    }

    private void Release(Guid group, bool exclusive)
    {
        lock (sync)
        {
            var state = groups[group];
            if (exclusive)
            {
                state.Exclusive = false;
                Interlocked.Increment(ref walkEpoch);
            }
            else
            {
                state.Shared--;
            }

            GrantWaiters(group, state);
        }
    }

    private void GrantWaiters(Guid group, GroupState state)
    {
        while (state.Waiters.First is { } head && state.Admits(head.Value.Exclusive))
        {
            state.Waiters.RemoveFirst();
            var waiter = head.Value;
            waiter.Registration.Unregister();
            state.Grant(waiter.Exclusive);
            waiter.Granted.TrySetResult(new Hold(this, group, waiter.Exclusive));
        }

        if (state.IsIdle)
        {
            groups.Remove(group);
        }
    }

    private sealed class GroupState
    {
        public int Shared;
        public bool Exclusive;
        public readonly LinkedList<Waiter> Waiters = new();

        public bool IsIdle => Shared == 0 && !Exclusive && Waiters.Count == 0;

        public bool Admits(bool exclusive) => exclusive ? Shared == 0 && !Exclusive : !Exclusive;

        public void Grant(bool exclusive)
        {
            if (exclusive)
            {
                Exclusive = true;
            }
            else
            {
                Shared++;
            }
        }
    }

    private sealed class Waiter(bool exclusive)
    {
        public bool Exclusive { get; } = exclusive;

        public TaskCompletionSource<IDisposable> Granted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public CancellationTokenRegistration Registration { get; set; }
    }

    private sealed class Hold(WorkloadGroupGate gate, Guid group, bool exclusive) : IDisposable
    {
        private int released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref released, 1) == 0)
            {
                gate.Release(group, exclusive);
            }
        }
    }
}
