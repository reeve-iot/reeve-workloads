using Microsoft.Extensions.Logging;

namespace Reeve.Workloads.Core.Watch;

/// <summary>Per-instance debounce/coalesce pump for watch-based reconcile: collapses a burst of marks into at
/// most one reconcile per window. Per-callback faults are isolated; a mark during drain joins the next window.</summary>
public sealed class WorkloadWatchCoalescer : IAsyncDisposable
{
    private readonly TimeSpan debounceWindow;
    private readonly Func<Guid, CancellationToken, Task> reconcileInstance;
    private readonly Func<CancellationToken, Task> reconcileAll;
    private readonly Func<TimeSpan, CancellationToken, Task> debounceDelay;
    private readonly Action? onPumpFaulted;
    private readonly ILogger<WorkloadWatchCoalescer> logger;

    private readonly object gate = new();
    private readonly HashSet<Guid> dirty = new();
    private bool allDirty;

    private TaskCompletionSource? workSignal;

    private readonly CancellationTokenSource cts;
    private readonly Task pumpLoop;
    private bool disposed;

    internal event Action? CycleDrained;

    /// <param name="debounceWindow">How long a mark waits for company before the drain runs. Zero drains on the
    /// next pump turn; negative is rejected.</param>
    /// <param name="reconcileInstance">Reconciles one instance. Called once per drained instance id; a throw is
    /// isolated to that id and never stops the pump.</param>
    /// <param name="reconcileAll">Reconciles everything, for a mark that could not name an instance. Collapses
    /// with the per-instance set into a single drain cycle.</param>
    /// <param name="logger">Where a per-callback fault and a pump-loop fault are recorded.</param>
    /// <param name="debounceDelay">Test seam for the window wait. Null uses <see cref="Task.Delay(TimeSpan,
    /// CancellationToken)"/>, which is the production path.</param>
    /// <param name="lifetime">Cancels the pump when the owner shuts down; the disposer cancels its own linked
    /// source regardless.</param>
    /// <param name="onPumpFaulted">Fires once, only on an unexpected pump-loop bug — never on a per-callback
    /// throw.</param>
    public WorkloadWatchCoalescer(
        TimeSpan debounceWindow,
        Func<Guid, CancellationToken, Task> reconcileInstance,
        Func<CancellationToken, Task> reconcileAll,
        ILogger<WorkloadWatchCoalescer> logger,
        Func<TimeSpan, CancellationToken, Task>? debounceDelay = null,
        CancellationToken lifetime = default,
        Action? onPumpFaulted = null)
    {
        if (debounceWindow < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(debounceWindow), debounceWindow, "The debounce window must be non-negative.");
        }

        this.debounceWindow = debounceWindow;
        this.reconcileInstance = reconcileInstance ?? throw new ArgumentNullException(nameof(reconcileInstance));
        this.reconcileAll = reconcileAll ?? throw new ArgumentNullException(nameof(reconcileAll));
        this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
        this.debounceDelay = debounceDelay ?? ((window, ct) => Task.Delay(window, ct));
        this.onPumpFaulted = onPumpFaulted;

        cts = CancellationTokenSource.CreateLinkedTokenSource(lifetime);
        pumpLoop = Task.Run(() => PumpAsync(cts.Token));
    }

    /// <summary>Marks a single instance dirty. Cheap, non-blocking, thread-safe — safe to call from a watch
    /// callback on any thread. Coalesced: repeated calls within one window drive a single reconcile.</summary>
    public void MarkDirty(Guid instanceId)
    {
        TaskCompletionSource? toSignal;
        lock (gate)
        {
            if (disposed)
            {
                return;
            }

            dirty.Add(instanceId);
            toSignal = TakeWorkSignalLocked();
        }

        toSignal?.TrySetResult();
    }

    /// <summary>Signals a reconcile-ALL for the window (a re-list / 410-Gone resync). Repeated calls, and any
    /// per-instance marks in the same window, collapse to a single <c>reconcileAll</c>.</summary>
    public void MarkAllDirty()
    {
        TaskCompletionSource? toSignal;
        lock (gate)
        {
            if (disposed)
            {
                return;
            }

            allDirty = true;
            toSignal = TakeWorkSignalLocked();
        }

        toSignal?.TrySetResult();
    }

    private TaskCompletionSource? TakeWorkSignalLocked()
    {
        var signal = workSignal;
        workSignal = null;
        return signal;
    }

    private async Task PumpAsync(CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                await WaitForWorkAsync(token).ConfigureAwait(false);

                await debounceDelay(debounceWindow, token).ConfigureAwait(false);

                Guid[] instances;
                bool all;
                lock (gate)
                {
                    all = allDirty;
                    allDirty = false;
                    instances = all ? Array.Empty<Guid>() : dirty.ToArray();
                    dirty.Clear();
                }

                if (all)
                {
                    await InvokeAllSafeAsync(token).ConfigureAwait(false);
                }
                else
                {
                    foreach (var instanceId in instances)
                    {
                        token.ThrowIfCancellationRequested();
                        await InvokeInstanceSafeAsync(instanceId, token).ConfigureAwait(false);
                    }
                }

                CycleDrained?.Invoke();
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Workload watch coalescer pump loop faulted and stopped; watch-triggered reconciles stop; the periodic reconcile pass remains.");
            NotifyPumpFaulted();
        }
    }

    private void NotifyPumpFaulted()
    {
        try
        {
            onPumpFaulted?.Invoke();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "The workload watch coalescer pump-fault handler threw; ignoring.");
        }
    }

    private async Task WaitForWorkAsync(CancellationToken token)
    {
        Task wait;
        lock (gate)
        {
            if (dirty.Count > 0 || allDirty)
            {
                return;
            }

            workSignal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            wait = workSignal.Task;
        }

        await wait.WaitAsync(token).ConfigureAwait(false);
    }

    private async Task InvokeInstanceSafeAsync(Guid instanceId, CancellationToken token)
    {
        try
        {
            await reconcileInstance(instanceId, token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Watch-triggered reconcile of workload instance {InstanceId} threw; continuing with the rest of the window.", instanceId);
        }
    }

    private async Task InvokeAllSafeAsync(CancellationToken token)
    {
        try
        {
            await reconcileAll(token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Watch-triggered reconcile-all threw; the periodic reconcile pass remains.");
        }
    }

    /// <summary>Cancels the pump and awaits the loop so no reconcile fires after stop. Idempotent; never
    /// deadlocks — a window in flight observes the cancellation.</summary>
    public async ValueTask DisposeAsync()
    {
        TaskCompletionSource? toCancel;
        lock (gate)
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            toCancel = TakeWorkSignalLocked();
        }

        toCancel?.TrySetCanceled();

        await cts.CancelAsync().ConfigureAwait(false);

        try
        {
            await pumpLoop.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch
        {
        }

        cts.Dispose();
    }
}
