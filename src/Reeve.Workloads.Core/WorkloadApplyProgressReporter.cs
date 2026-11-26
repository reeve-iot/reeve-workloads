using System;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Reeve.Workloads.Abstractions.Providers;

namespace Reeve.Workloads.Core;

/// <summary>The <see cref="IProgress{T}"/> the reconcile engine passes into <see cref="IWorkloadProvider.ApplyAsync"/>
/// so a long materialization step can surface throttled progress. Reports are marshalled through a bounded (1, drop-oldest) single-reader channel onto ONE pump task, so the persist delegate — writing via the reconciler's SCOPED, non-thread-safe DbContext — runs strictly serially; <see cref="DrainAsync"/> guarantees no persist is in flight once the caller resumes its own DbContext work. Progress is cosmetic — a fault is logged and swallowed.</summary>
public sealed class WorkloadApplyProgressReporter : IProgress<WorkloadApplyProgress>, IAsyncDisposable
{
    private readonly Channel<WorkloadApplyProgress> channel;
    private readonly Task pump;

    public WorkloadApplyProgressReporter(
        Func<WorkloadApplyProgress, CancellationToken, Task> persistAsync,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(persistAsync);
        ArgumentNullException.ThrowIfNull(logger);

        channel = Channel.CreateBounded<WorkloadApplyProgress>(
            new BoundedChannelOptions(1)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true,
                SingleWriter = false,
            });

        pump = PumpAsync(persistAsync, logger, cancellationToken);
    }

    /// <summary>Enqueues the latest snapshot for the pump (non-blocking; drops the previous pending one).</summary>
    public void Report(WorkloadApplyProgress value)
    {
        if (value is not null)
        {
            channel.Writer.TryWrite(value);
        }
    }

    /// <summary>Completes the stream and awaits the pump — after this returns no persist is in flight, so the
    /// caller may safely resume its own use of the shared DbContext. Idempotent: a second call is a no-op.</summary>
    public async Task DrainAsync()
    {
        channel.Writer.TryComplete();
        await pump.ConfigureAwait(false);
    }

    /// <summary>Drains on scope exit so an early/exceptional return never leaves the pump task orphaned.</summary>
    public async ValueTask DisposeAsync() => await DrainAsync().ConfigureAwait(false);

    private async Task PumpAsync(
        Func<WorkloadApplyProgress, CancellationToken, Task> persistAsync,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var progress in channel.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                try
                {
                    await persistAsync(progress, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogDebug(ex,
                        "Failed to persist a workload apply-progress snapshot — continuing (the progress bar is cosmetic).");
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }
}
