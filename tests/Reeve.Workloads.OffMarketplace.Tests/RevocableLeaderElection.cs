using Reeve.Workloads.Abstractions;

namespace Reeve.Workloads.OffMarketplace.Tests;

internal sealed class RevocableLeaderElection : IWorkloadLeaderElection
{
    private readonly TaskCompletionSource resigned = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Lock gate = new();

    private Leadership? held;
    private bool revoked;
    private int acquisitions;

    public int Acquisitions
    {
        get
        {
            lock (gate)
            {
                return acquisitions;
            }
        }
    }

    public async Task AwaitResignedAsync(TimeSpan budget)
    {
        try
        {
            await resigned.Task.WaitAsync(budget);
        }
        catch (TimeoutException)
        {
            throw new TimeoutException(
                $"Waited {budget} for the engine to give back the leadership it holds; it had taken {Acquisitions} "
                + "and resigned none. A runner that does not notice a leadership ending, or does not stop its "
                + "units on the way out, never reaches the resign.");
        }
    }

    public Task<IWorkloadLeadership?> TryAcquireAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        Leadership leadership;
        lock (gate)
        {
            if (revoked || held is not null)
            {
                return Task.FromResult<IWorkloadLeadership?>(null);
            }

            acquisitions++;
            held = leadership = new Leadership(this);
        }

        return Task.FromResult<IWorkloadLeadership?>(leadership);
    }

    public void Revoke()
    {
        Leadership? current;
        lock (gate)
        {
            revoked = true;
            current = held;
        }

        current?.Lose();
    }

    private sealed class Leadership : IWorkloadLeadership
    {
        private readonly RevocableLeaderElection election;

        private readonly CancellationTokenSource lost = new();

        private int disposed;

        public Leadership(RevocableLeaderElection election)
        {
            this.election = election;
            Lost = lost.Token;
        }

        public CancellationToken Lost { get; }

        public void Lose() => lost.Cancel();

        public ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0)
            {
                return ValueTask.CompletedTask;
            }

            lost.Cancel();

            lock (election.gate)
            {
                election.held = null;
            }

            election.resigned.TrySetResult();
            return ValueTask.CompletedTask;
        }
    }
}
