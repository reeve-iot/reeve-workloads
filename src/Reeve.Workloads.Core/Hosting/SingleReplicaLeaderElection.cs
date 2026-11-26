using Microsoft.Extensions.Logging;
using Reeve.Workloads.Abstractions;

namespace Reeve.Workloads.Core;

internal sealed class SingleReplicaLeaderElection : IWorkloadLeaderElection
{
    private readonly ILogger<SingleReplicaLeaderElection> logger;

    private int announced;

    public SingleReplicaLeaderElection(ILogger<SingleReplicaLeaderElection> logger) => this.logger = logger;

    public Task<IWorkloadLeadership?> TryAcquireAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (Interlocked.Exchange(ref announced, 1) == 0)
        {
            logger.LogInformation(
                "Workloads engine leadership taken by the single-replica election: this process assumes it is the " +
                "only one. A second replica would win the same way and run a second engine — register an " +
                "IWorkloadLeaderElection backed by a shared lock before scaling out.");
        }

        return Task.FromResult<IWorkloadLeadership?>(new SingleReplicaLeadership());
    }

    private sealed class SingleReplicaLeadership : IWorkloadLeadership
    {
        private readonly CancellationTokenSource resigned = new();
        private int disposed;

        public SingleReplicaLeadership() => Lost = resigned.Token;

        public CancellationToken Lost { get; }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0)
            {
                return;
            }

            await resigned.CancelAsync().ConfigureAwait(false);
            resigned.Dispose();
        }
    }
}
