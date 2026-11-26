using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Reeve.Workloads.EntityFrameworkCore.Persistence;
using Reeve.Workloads.EntityFrameworkCore.PostgreSql;

namespace Reeve.Workloads.EntityFrameworkCore.PostgreSql.Tests;

public sealed class AdvisoryLockLeadershipProbeTests
{
    private static readonly TimeSpan Probe = TimeSpan.FromMilliseconds(20);

    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task A_probe_that_fails_once_and_then_answers_keeps_the_leadership()
    {
        var server = new ScriptedSession(Answer.Throw, Answer.StillHeld, Answer.StillHeld);
        var log = new RecordingLogger();

        await using var leadership = Leadership(server, log);

        (await server.WaitForCallsAsync(2, Bound)).Should().BeTrue("the failure has to be followed by a retry");
        await Task.Delay(100);

        leadership.Lost.IsCancellationRequested.Should().BeFalse(
            "the session answered the retry and still holds the key, so nothing was lost");
        log.Lines.Should().Contain(
            line => line.Level == LogLevel.Information && line.Message.Contains("still holds the lock"),
            "an operator whose database nearly cost them the leadership should be able to see it happening");
    }

    [Fact]
    public async Task A_probe_that_fails_twice_loses_the_leadership()
    {
        var server = new ScriptedSession(Answer.Throw, Answer.Throw);
        var log = new RecordingLogger();

        await using var leadership = Leadership(server, log);

        (await WaitForCancellationAsync(leadership.Lost, Bound)).Should().BeTrue();
        server.Calls.Should().Be(2, "one probe and one retry, and no third ask after the verdict");
        log.Lines.Should().Contain(
            line => line.Level == LogLevel.Warning && line.Message.Contains("stopped answering, twice"));
    }

    [Fact]
    public async Task A_probe_whose_retry_says_the_key_is_gone_loses_the_leadership()
    {
        var server = new ScriptedSession(Answer.Throw, Answer.NotHeld);
        var log = new RecordingLogger();

        await using var leadership = Leadership(server, log);

        (await WaitForCancellationAsync(leadership.Lost, Bound)).Should().BeTrue();
        server.Calls.Should().Be(2);
        log.Lines.Should().Contain(
            line => line.Level == LogLevel.Warning && line.Message.Contains("answered the retry"),
            "a failed probe and a lost key are different findings and an operator needs to know which happened");
    }

    [Fact]
    public async Task A_probe_answered_with_zero_loses_the_leadership_without_retrying()
    {
        var server = new ScriptedSession(Answer.NotHeld);
        var log = new RecordingLogger();

        await using var leadership = Leadership(server, log);

        (await WaitForCancellationAsync(leadership.Lost, Bound)).Should().BeTrue();
        server.Calls.Should().Be(1, "an answer is not retried; only a failure to get one is");
        log.Lines.Should().Contain(
            line => line.Level == LogLevel.Warning && line.Message.Contains("though it still answers"));
    }

    private static PostgreSqlAdvisoryLockLeaderElection.AdvisoryLockLeadership Leadership(
        ScriptedSession server, ILogger log)
        => new(
            EmptyScope(),
            UnopenedContext(),
            Probe,
            log,
            server.AnswerAsync);

    private static AsyncServiceScope EmptyScope()
        => new ServiceCollection().BuildServiceProvider().CreateAsyncScope();

    private static WorkloadsDbContext UnopenedContext()
        => new(
            new DbContextOptionsBuilder<WorkloadsDbContext>()
                .UseNpgsql("Host=postgresql-package-tests.invalid;Database=workloads;Username=none;Password=none")
                .Options,
            new WorkloadsStoreOptions());

    private static async Task<bool> WaitForCancellationAsync(CancellationToken token, TimeSpan timeout)
    {
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var registration = token.Register(
            static state => ((TaskCompletionSource)state!).TrySetResult(), cancelled);

        return await Task.WhenAny(cancelled.Task, Task.Delay(timeout)).ConfigureAwait(false) == cancelled.Task;
    }

    private enum Answer
    {
        Throw,
        StillHeld,
        NotHeld,
    }

    private sealed class ScriptedSession
    {
        private readonly Answer[] script;
        private readonly TaskCompletionSource reached = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int wanted = int.MaxValue;
        private int calls;

        public ScriptedSession(params Answer[] script) => this.script = script;

        public int Calls => Volatile.Read(ref calls);

        public Task<bool> AnswerAsync(CancellationToken cancellationToken)
        {
            var mine = Interlocked.Increment(ref calls);
            if (mine >= Volatile.Read(ref wanted))
            {
                reached.TrySetResult();
            }

            return script[Math.Min(mine - 1, script.Length - 1)] switch
            {
                Answer.StillHeld => Task.FromResult(true),
                Answer.NotHeld => Task.FromResult(false),
                _ => Task.FromException<bool>(new InvalidOperationException("the statement was cancelled")),
            };
        }

        public async Task<bool> WaitForCallsAsync(int count, TimeSpan timeout)
        {
            Volatile.Write(ref wanted, count);
            if (Calls >= count)
            {
                return true;
            }

            return await Task.WhenAny(reached.Task, Task.Delay(timeout)).ConfigureAwait(false) == reached.Task;
        }
    }

    private sealed record Line(LogLevel Level, string Message);

    private sealed class RecordingLogger : ILogger
    {
        private readonly List<Line> lines = [];

        public IReadOnlyList<Line> Lines
        {
            get
            {
                lock (lines)
                {
                    return lines.ToList();
                }
            }
        }

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            lock (lines)
            {
                lines.Add(new Line(logLevel, formatter(state, exception)));
            }
        }
    }
}
