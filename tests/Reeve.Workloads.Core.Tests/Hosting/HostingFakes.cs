using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Reeve.Workloads.Abstractions;
using Reeve.Workloads.Core;

namespace Reeve.Workloads.Core.Tests.Hosting;

internal sealed record LogEntry(LogLevel Level, string Text, Exception? Exception);

internal sealed class RecordingLogger<T> : ILogger<T>
{
    private readonly List<LogEntry> entries = new();

    public IReadOnlyList<LogEntry> Entries { get { lock (entries) { return entries.ToList(); } } }

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        lock (entries)
        {
            entries.Add(new LogEntry(logLevel, formatter(state, exception), exception));
        }
    }
}

internal sealed class CallLog
{
    private readonly List<string> entries = new();
    private readonly List<(Func<IReadOnlyList<string>, bool> Predicate, TaskCompletionSource Completion)> waiters = new();

    public IReadOnlyList<string> Entries { get { lock (entries) { return entries.ToList(); } } }

    public void Record(string entry)
    {
        List<TaskCompletionSource>? satisfied = null;
        lock (entries)
        {
            entries.Add(entry);
            var snapshot = entries.ToList();
            for (var i = waiters.Count - 1; i >= 0; i--)
            {
                if (waiters[i].Predicate(snapshot))
                {
                    (satisfied ??= new List<TaskCompletionSource>()).Add(waiters[i].Completion);
                    waiters.RemoveAt(i);
                }
            }
        }

        if (satisfied is not null)
        {
            foreach (var completion in satisfied)
            {
                completion.TrySetResult();
            }
        }
    }

    public Task WaitForAsync(Func<IReadOnlyList<string>, bool> predicate)
    {
        lock (entries)
        {
            if (predicate(entries.ToList()))
            {
                return Task.CompletedTask;
            }

            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            waiters.Add((predicate, completion));
            return completion.Task;
        }
    }

    public Task WaitForAsync(string entry, int times = 1)
        => WaitForAsync(all => all.Count(e => e == entry) >= times);

    public IReadOnlyList<string> Before(string entry)
    {
        var all = Entries;
        var index = all.ToList().IndexOf(entry);
        return index < 0 ? all : all.Take(index).ToList();
    }
}

internal sealed class FakeLeadership : IWorkloadLeadership
{
    private readonly CancellationTokenSource lost = new();
    private readonly CallLog? log;
    private readonly Exception? disposeFailure;
    private int disposals;

    public FakeLeadership(string name, CallLog? log = null, Exception? disposeFailure = null)
    {
        Name = name;
        this.log = log;
        this.disposeFailure = disposeFailure;
    }

    public string Name { get; }

    public CancellationToken Lost => lost.Token;

    public int Disposals => Volatile.Read(ref disposals);

    public bool WasDisposed => Disposals > 0;

    public void Lose() => lost.Cancel();

    public ValueTask DisposeAsync()
    {
        Interlocked.Increment(ref disposals);
        lost.Cancel();

        log?.Record("resign");

        return disposeFailure is null
            ? ValueTask.CompletedTask
            : ValueTask.FromException(disposeFailure);
    }
}

internal sealed class FakeLeaderElection : IWorkloadLeaderElection
{
    private readonly Queue<object?> script;
    private readonly CallLog? log;
    private int attempts;

    public FakeLeaderElection(CallLog? log, params object?[] answers)
    {
        this.log = log;
        script = new Queue<object?>(answers);
    }

    public int Attempts => Volatile.Read(ref attempts);

    public Task<IWorkloadLeadership?> TryAcquireAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Interlocked.Increment(ref attempts);

        object? answer;
        lock (script)
        {
            answer = script.Count > 0 ? script.Dequeue() : null;
        }

        log?.Record("acquire");

        return answer switch
        {
            null => Task.FromResult<IWorkloadLeadership?>(null),
            Exception failure => Task.FromException<IWorkloadLeadership?>(failure),
            IWorkloadLeadership leadership => Task.FromResult<IWorkloadLeadership?>(leadership),

            _ => throw new ArgumentException(
                $"FakeLeaderElection was scripted with a {answer.GetType()}. An answer is a FakeLeadership (won), "
                + "null (someone else holds it) or an Exception (the election itself failed)."),
        };
    }
}

internal sealed class FakeEngineUnit : IWorkloadEngineUnit
{
    private readonly CallLog log;
    private readonly bool throwOnStart;
    private readonly bool throwOnStop;
    private readonly Task? stopGate;
    private readonly Task? startGate;
    private int starts;
    private int stops;

    public FakeEngineUnit(string key, CallLog log, bool throwOnStart = false, bool throwOnStop = false, Task? stopGate = null, Task? startGate = null)
    {
        this.startGate = startGate;
        Key = key;
        this.log = log;
        this.throwOnStart = throwOnStart;
        this.throwOnStop = throwOnStop;
        this.stopGate = stopGate;
    }

    public string Key { get; }

    public int Starts => Volatile.Read(ref starts);

    public int Stops => Volatile.Read(ref stops);

    public CancellationToken StartToken { get; private set; }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref starts);
        StartToken = cancellationToken;
        log.Record($"start:{Key}");
        if (throwOnStart)
        {
            return Task.FromException(new InvalidOperationException($"{Key} refuses to start"));
        }

        return startGate is null ? Task.CompletedTask : startGate.WaitAsync(cancellationToken);
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref stops);
        log.Record($"stop:{Key}");

        if (stopGate is not null)
        {
            log.Record($"stopping:{Key}");
            await stopGate;
        }

        if (throwOnStop)
        {
            throw new InvalidOperationException($"{Key} refuses to stop");
        }
    }
}

internal sealed class FakeHostApplicationLifetime : IHostApplicationLifetime, IDisposable
{
    private readonly CancellationTokenSource started = new();
    private readonly CancellationTokenSource stopping = new();
    private readonly CancellationTokenSource stopped = new();
    private int stopRequests;

    public CancellationToken ApplicationStarted => started.Token;

    public CancellationToken ApplicationStopping => stopping.Token;

    public CancellationToken ApplicationStopped => stopped.Token;

    public int StopRequests => Volatile.Read(ref stopRequests);

    public void StopApplication() => Interlocked.Increment(ref stopRequests);

    public void FireStarted() => started.Cancel();

    public void FireStopping() => stopping.Cancel();

    public void Dispose()
    {
        started.Dispose();
        stopping.Dispose();
        stopped.Dispose();
    }
}

internal sealed class ImmediateTimeProvider : TimeProvider
{
    private readonly List<TimeSpan> requestedDelays = new();

    public IReadOnlyList<TimeSpan> RequestedDelays { get { lock (requestedDelays) { return requestedDelays.ToList(); } } }

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        lock (requestedDelays)
        {
            requestedDelays.Add(dueTime);
        }

        return new ImmediateTimer(callback, state);
    }

    private sealed class ImmediateTimer : ITimer
    {
        private int fired;

        public ImmediateTimer(TimerCallback callback, object? state) => Fire(callback, state);

        public bool Change(TimeSpan dueTime, TimeSpan period) => true;

        public void Dispose()
        {
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        private void Fire(TimerCallback callback, object? state)
        {
            if (Interlocked.Exchange(ref fired, 1) != 0)
            {
                return;
            }

            ThreadPool.UnsafeQueueUserWorkItem(_ => callback(state), null);
        }
    }
}

internal static class HostingWait
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(10);

    public static Task Within(this Task task) => task.WaitAsync(Limit);
}

internal sealed class RecordingLoggerFactory : ILoggerFactory
{
    private readonly List<LogEntry> entries = new();

    public IReadOnlyList<LogEntry> Entries { get { lock (entries) { return entries.ToList(); } } }

    public void AddProvider(ILoggerProvider provider)
    {
    }

    public ILogger CreateLogger(string categoryName) => new Sink(entries);

    public void Dispose()
    {
    }

    private sealed class Sink(List<LogEntry> entries) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            lock (entries)
            {
                entries.Add(new LogEntry(logLevel, formatter(state, exception), exception));
            }
        }
    }
}

internal sealed class RecordingSchemaMigrator : IWorkloadsSchemaMigrator
{
    private readonly CallLog log;
    private readonly Task? gate;

    public RecordingSchemaMigrator(CallLog log, Task? gate = null)
    {
        this.log = log;
        this.gate = gate;
    }

    public async Task MigrateAsync(CancellationToken cancellationToken = default)
    {
        log.Record("migrate");

        if (gate is not null)
        {
            await gate;
        }
    }
}
