using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Reeve.Workloads.Abstractions;

namespace Reeve.Workloads.Core;

internal sealed class WorkloadsEngineRunner
{
    private readonly IWorkloadLeaderElection election;

    private readonly IReadOnlyList<IWorkloadEngineUnit> units;

    private readonly WorkloadsEngineHostingOptions options;
    private readonly ILogger<WorkloadsEngineRunner> logger;
    private readonly TimeProvider time;

    private readonly SemaphoreSlim gate = new(1, 1);

    private CancellationTokenSource? shutdown;
    private Task? loop;
    private Task? draining;
    private bool stopped;

    public WorkloadsEngineRunner(
        IWorkloadLeaderElection election,
        IEnumerable<IWorkloadEngineUnit> units,
        IOptions<WorkloadsEngineHostingOptions> options,
        ILogger<WorkloadsEngineRunner> logger)
        : this(election, units, options, logger, TimeProvider.System)
    {
    }

    public WorkloadsEngineRunner(
        IWorkloadLeaderElection election,
        IEnumerable<IWorkloadEngineUnit> units,
        IOptions<WorkloadsEngineHostingOptions> options,
        ILogger<WorkloadsEngineRunner> logger,
        TimeProvider timeProvider)
    {
        this.election = election;
        this.units = units.ToList();
        this.options = options.Value;
        this.logger = logger;
        this.time = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await gate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            if (stopped)
            {
                logger.LogWarning("The workloads engine was asked to start after it had been stopped; ignoring.");
                return;
            }

            if (loop is not null)
            {
                return;
            }

            shutdown = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var token = shutdown.Token;

            loop = Task.Run(() => RunAsync(token), CancellationToken.None);
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        Task drain;

        await gate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            stopped = true;

            if (draining is not null)
            {
                drain = draining;
            }
            else if (loop is null || shutdown is null)
            {
                return;
            }
            else
            {
                var running = loop;
                var source = shutdown;
                loop = null;
                shutdown = null;

                drain = draining = Task.Run(() => DrainAsync(running, source), CancellationToken.None);
            }
        }
        finally
        {
            gate.Release();
        }

        await drain.ConfigureAwait(false);
    }

    private static async Task DrainAsync(Task running, CancellationTokenSource source)
    {
        await source.CancelAsync().ConfigureAwait(false);
        try
        {
            await running.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }

        source.Dispose();
    }

    private async Task RunAsync(CancellationToken shutdownToken)
    {
        var first = true;

        while (!shutdownToken.IsCancellationRequested)
        {
            if (!first)
            {
                logger.LogDebug("The workloads engine is contending for the leadership again in {Interval}.", options.LeaderRetryInterval);

                if (!await WaitBeforeRetryingAsync(shutdownToken).ConfigureAwait(false))
                {
                    break;
                }
            }

            first = false;

            try
            {
                await RunOnePassAsync(shutdownToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                if (shutdownToken.IsCancellationRequested)
                {
                    logger.LogWarning(
                        ex,
                        "The workloads engine faulted while shutting down, most often resigning its leadership over "
                        + "a connection that was already going away. The engine is stopping. This leadership may "
                        + "still be held by this process until its session or lease expires, and no other replica "
                        + "can take over before then.");
                    break;
                }

                logger.LogCritical(
                    ex,
                    "The workloads engine's leadership pass faulted. The engine is NOT stopping: it waits {Interval} "
                    + "and contends again. If this repeats, the leadership may still be held by this process and no "
                    + "replica can take over.",
                    options.LeaderRetryInterval);
            }
        }

        logger.LogInformation("The workloads engine's loop has stopped.");
    }

    private async Task RunOnePassAsync(CancellationToken shutdownToken)
    {
        var leadership = await TryAcquireAsync(shutdownToken).ConfigureAwait(false);
        if (leadership is null)
        {
            logger.LogDebug("The workloads engine did not win the leadership.");
            return;
        }

        try
        {
            using var unitLifetime = CancellationTokenSource.CreateLinkedTokenSource(shutdownToken, leadership.Lost);
            var allStarted = await StartUnitsAsync(unitLifetime.Token).ConfigureAwait(false);
            if (!allStarted && leadership.Lost.IsCancellationRequested && !shutdownToken.IsCancellationRequested)
            {
                logger.LogWarning(
                    "The workloads engine lost the leadership while starting its units. Stopping all {Count} units and contending again in {Interval}.",
                    units.Count,
                    options.LeaderRetryInterval);
            }

            if (allStarted)
            {
                logger.LogInformation("Workloads engine holds the leadership and is running {Count} units.", units.Count);
                await WaitUntilLostOrShutdownAsync(leadership.Lost, shutdownToken).ConfigureAwait(false);

                if (leadership.Lost.IsCancellationRequested && !shutdownToken.IsCancellationRequested)
                {
                    logger.LogWarning(
                        "The workloads engine lost the leadership. Stopping all {Count} units and contending again in {Interval}.",
                        units.Count,
                        options.LeaderRetryInterval);
                }
            }

            await StopUnitsAsync().ConfigureAwait(false);
        }
        finally
        {
            await leadership.DisposeAsync().ConfigureAwait(false);
        }
    }

    private async Task<IWorkloadLeadership?> TryAcquireAsync(CancellationToken shutdownToken)
    {
        try
        {
            return await election.TryAcquireAsync(shutdownToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (shutdownToken.IsCancellationRequested)
        {
            return null;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "The workloads engine could not contend for the leadership.");
            return null;
        }
    }

    private async Task<bool> WaitBeforeRetryingAsync(CancellationToken shutdownToken)
    {
        try
        {
            await Task.Delay(options.LeaderRetryInterval, time, shutdownToken).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    private async Task<bool> StartUnitsAsync(CancellationToken unitLifetime)
    {
        var allStarted = true;

        foreach (var unit in units)
        {
            if (unitLifetime.IsCancellationRequested)
            {
                return false;
            }

            try
            {
                await unit.StartAsync(unitLifetime).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (unitLifetime.IsCancellationRequested)
            {
                return false;
            }
            catch (Exception ex)
            {
                allStarted = false;
                logger.LogError(
                    ex,
                    "Workloads engine unit {Unit} failed to start. The engine is resigning its leadership: a leader "
                    + "running some of its units is worse than no leader, because nothing else takes over while it "
                    + "holds the lock.",
                    unit.Key);
            }
        }

        return allStarted;
    }

    private async Task StopUnitsAsync()
    {
        for (var i = units.Count - 1; i >= 0; i--)
        {
            var unit = units[i];
            try
            {
                await unit.StopAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Workloads engine unit {Unit} failed to stop cleanly; the remaining units are stopped anyway.", unit.Key);
            }
        }
    }

    private static async Task WaitUntilLostOrShutdownAsync(CancellationToken lost, CancellationToken shutdownToken)
    {
        if (lost.IsCancellationRequested || shutdownToken.IsCancellationRequested)
        {
            return;
        }

        using var either = CancellationTokenSource.CreateLinkedTokenSource(lost, shutdownToken);
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = either.Token.Register(
            static state => ((TaskCompletionSource)state!).TrySetResult(),
            reached);

        await reached.Task.ConfigureAwait(false);
    }
}
