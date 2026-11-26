using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Reeve.Workloads.Abstractions;
using Reeve.Workloads.Core;
using Xunit;

namespace Reeve.Workloads.Core.Tests.Hosting;

public sealed class WorkloadsEngineRunnerTests
{
    private static readonly TimeSpan Retry = TimeSpan.FromSeconds(7);

    [Fact]
    public async Task It_starts_every_unit_once_it_wins()
    {
        var log = new CallLog();
        var a = new FakeEngineUnit("a", log);
        var b = new FakeEngineUnit("b", log);
        var election = new FakeLeaderElection(log, new FakeLeadership("first", log));
        var runner = Build(election, out _, out _, a, b);

        await runner.StartAsync(CancellationToken.None);
        await log.WaitForAsync("start:b").Within();
        await runner.StopAsync(CancellationToken.None);

        log.Entries.Should().ContainInOrder("acquire", "start:a", "start:b");
        a.Starts.Should().Be(1);
        b.Starts.Should().Be(1);
    }

    [Fact]
    public async Task It_starts_nothing_while_it_has_not_won()
    {
        var log = new CallLog();
        var a = new FakeEngineUnit("a", log);
        var election = new FakeLeaderElection(log);
        var runner = Build(election, out _, out var logger, a);

        await runner.StartAsync(CancellationToken.None);
        await log.WaitForAsync("acquire", times: 3).Within();
        await runner.StopAsync(CancellationToken.None);

        log.Entries.Should().NotContain(e => e.StartsWith("start:", StringComparison.Ordinal));
        a.Starts.Should().Be(0);

        logger.Entries.Should().Contain(e => e.Level == LogLevel.Debug && e.Text.Contains("did not win the leadership", StringComparison.Ordinal));

        logger.Entries.Should().Contain(e => e.Level == LogLevel.Debug && e.Text.Contains("contending for the leadership again in 00:00:07", StringComparison.Ordinal));
    }

    [Fact]
    public async Task It_retries_on_the_configured_interval_until_it_wins()
    {
        var log = new CallLog();
        var a = new FakeEngineUnit("a", log);
        var election = new FakeLeaderElection(log, null, null, new FakeLeadership("third", log));
        var runner = Build(election, out var time, out _, a);

        await runner.StartAsync(CancellationToken.None);
        await log.WaitForAsync("start:a").Within();
        await runner.StopAsync(CancellationToken.None);

        election.Attempts.Should().Be(3, "two losses and the win — and not one attempt more, because a runner "
            + "that kept contending after winning would resign and re-take its own leadership");

        time.RequestedDelays.Should().Equal(new[] { Retry, Retry });
    }

    [Fact]
    public async Task It_stops_every_unit_when_leadership_is_lost()
    {
        var log = new CallLog();
        var a = new FakeEngineUnit("a", log);
        var b = new FakeEngineUnit("b", log);
        var leadership = new FakeLeadership("first", log);
        var election = new FakeLeaderElection(log, leadership);
        var runner = Build(election, out _, out var logger, a, b);

        await runner.StartAsync(CancellationToken.None);
        await log.WaitForAsync("start:b").Within();
        leadership.Lose();
        await log.WaitForAsync("resign").Within();
        await runner.StopAsync(CancellationToken.None);

        log.Before("resign").Should().ContainInOrder("start:a", "start:b", "stop:b", "stop:a");
        leadership.WasDisposed.Should().BeTrue();

        logger.Entries.Should().Contain(e => e.Level == LogLevel.Warning && e.Text.Contains("lost the leadership", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_leadership_lost_while_a_unit_starts_cancels_that_start_and_starts_no_further_unit()
    {
        var log = new CallLog();
        var slow = new FakeEngineUnit("slow", log, startGate: new TaskCompletionSource().Task);
        var next = new FakeEngineUnit("next", log);
        var leadership = new FakeLeadership("first", log);
        var election = new FakeLeaderElection(log, leadership);
        var runner = Build(election, out _, out var logger, slow, next);

        await runner.StartAsync(CancellationToken.None);
        await log.WaitForAsync("start:slow").Within();
        leadership.Lose();
        await log.WaitForAsync("resign").Within();
        await runner.StopAsync(CancellationToken.None);

        slow.StartToken.IsCancellationRequested.Should().BeTrue("a unit starts on a token that a lost lease cancels");
        next.Starts.Should().Be(0, "an engine that lost the lease starts nothing more");
        log.Before("resign").Should().ContainInOrder("start:slow", "stop:next", "stop:slow");
        logger.Entries.Should().Contain(e => e.Level == LogLevel.Warning && e.Text.Contains("while starting", StringComparison.Ordinal));
    }

    [Fact]
    public async Task It_contends_again_after_losing()
    {
        var log = new CallLog();
        var a = new FakeEngineUnit("a", log);
        var first = new FakeLeadership("first", log);
        var election = new FakeLeaderElection(log, first, new FakeLeadership("second", log));
        var runner = Build(election, out _, out _, a);

        await runner.StartAsync(CancellationToken.None);
        await log.WaitForAsync("start:a").Within();
        first.Lose();
        await log.WaitForAsync("start:a", times: 2).Within();
        await runner.StopAsync(CancellationToken.None);

        election.Attempts.Should().BeGreaterThanOrEqualTo(2);
        a.Starts.Should().BeGreaterThanOrEqualTo(2);
    }

    [Fact]
    public async Task It_waits_the_retry_interval_before_contending_again_after_a_leadership_ends()
    {
        var log = new CallLog();
        var a = new FakeEngineUnit("a", log);
        var first = new FakeLeadership("first", log);
        var election = new FakeLeaderElection(log, first, new FakeLeadership("second", log));
        var runner = Build(election, out var time, out _, a);

        await runner.StartAsync(CancellationToken.None);
        await log.WaitForAsync("start:a").Within();
        first.Lose();
        await log.WaitForAsync("start:a", times: 2).Within();
        await runner.StopAsync(CancellationToken.None);

        time.RequestedDelays.Should().Equal(new[] { Retry });
    }

    [Fact]
    public async Task It_resigns_the_leadership_when_a_unit_fails_to_start()
    {
        var log = new CallLog();
        var a = new FakeEngineUnit("a", log);
        var bad = new FakeEngineUnit("bad", log, throwOnStart: true);
        var c = new FakeEngineUnit("c", log);
        var leadership = new FakeLeadership("first", log);
        var election = new FakeLeaderElection(log, leadership);
        var runner = Build(election, out _, out var logger, a, bad, c);

        await runner.StartAsync(CancellationToken.None);
        await log.WaitForAsync("resign").Within();
        await runner.StopAsync(CancellationToken.None);

        log.Entries.Should().ContainInOrder("start:a", "start:bad", "start:c");
        log.Before("resign").Should().ContainInOrder("start:c", "stop:c", "stop:bad", "stop:a");
        leadership.WasDisposed.Should().BeTrue();

        logger.Entries.Should().Contain(e => e.Level == LogLevel.Error && e.Text.Contains("bad", StringComparison.Ordinal));
    }

    [Fact]
    public async Task It_stops_the_remaining_units_when_one_of_them_throws_on_stop()
    {
        var log = new CallLog();
        var a = new FakeEngineUnit("a", log);
        var bad = new FakeEngineUnit("bad", log, throwOnStop: true);
        var c = new FakeEngineUnit("c", log);
        var leadership = new FakeLeadership("first", log);
        var election = new FakeLeaderElection(log, leadership);
        var runner = Build(election, out _, out var logger, a, bad, c);

        await runner.StartAsync(CancellationToken.None);
        await log.WaitForAsync("start:c").Within();
        leadership.Lose();
        await log.WaitForAsync("resign").Within();
        await runner.StopAsync(CancellationToken.None);

        log.Before("resign").Should().ContainInOrder("stop:c", "stop:bad", "stop:a");
        leadership.WasDisposed.Should().BeTrue();
        logger.Entries.Should().Contain(e => e.Level == LogLevel.Error && e.Text.Contains("bad", StringComparison.Ordinal));
    }

    [Fact]
    public async Task It_survives_an_election_that_throws_and_wins_on_a_later_attempt()
    {
        var log = new CallLog();
        var a = new FakeEngineUnit("a", log);
        var election = new FakeLeaderElection(
            log,
            new InvalidOperationException("the lock database is unreachable"),
            new InvalidOperationException("the lock database is unreachable"),
            new FakeLeadership("third", log));
        var runner = Build(election, out _, out var logger, a);

        await runner.StartAsync(CancellationToken.None);
        await log.WaitForAsync("start:a").Within();
        await runner.StopAsync(CancellationToken.None);

        election.Attempts.Should().Be(3, "the loop survived both failures and went on to win");
        a.Starts.Should().Be(1);
        logger.Entries.Count(e => e.Level == LogLevel.Error).Should().Be(2);
        logger.Entries.Should().Contain(e => e.Level == LogLevel.Error && e.Text.Contains("could not contend for the leadership", StringComparison.Ordinal));
    }

    [Fact]
    public async Task It_survives_a_leadership_that_throws_when_it_is_resigned()
    {
        var log = new CallLog();
        var a = new FakeEngineUnit("a", log);
        var first = new FakeLeadership("first", log, disposeFailure: new InvalidOperationException("the unlock failed"));
        var election = new FakeLeaderElection(log, first, new FakeLeadership("second", log));
        var runner = Build(election, out _, out var logger, a);

        await runner.StartAsync(CancellationToken.None);
        await log.WaitForAsync("start:a").Within();
        first.Lose();
        await log.WaitForAsync("start:a", times: 2).Within();
        await runner.StopAsync(CancellationToken.None);

        election.Attempts.Should().BeGreaterThanOrEqualTo(2, "the loop came back from a fault it could not have planned for");
        logger.Entries.Should().Contain(e => e.Level == LogLevel.Critical,
            "a leadership this process may still be holding is the loudest thing the engine has to say");
    }

    [Fact]
    public async Task It_stops_every_unit_on_shutdown()
    {
        var log = new CallLog();
        var a = new FakeEngineUnit("a", log);
        var b = new FakeEngineUnit("b", log);
        var leadership = new FakeLeadership("first", log);
        var election = new FakeLeaderElection(log, leadership);
        var runner = Build(election, out _, out var logger, a, b);

        await runner.StartAsync(CancellationToken.None);
        await log.WaitForAsync("start:b").Within();

        await runner.StopAsync(CancellationToken.None);

        log.Before("resign").Should().ContainInOrder("start:a", "start:b", "stop:b", "stop:a");
        leadership.WasDisposed.Should().BeTrue();

        logger.Entries.Should().Contain(e => e.Text.Contains("loop has stopped", StringComparison.Ordinal));
    }

    [Fact]
    public async Task It_says_the_leadership_may_still_be_held_when_the_resign_fails_during_shutdown()
    {
        var log = new CallLog();
        var a = new FakeEngineUnit("a", log);
        var leadership = new FakeLeadership("first", log, disposeFailure: new OperationCanceledException("the unlock was cancelled"));
        var election = new FakeLeaderElection(log, leadership);
        var runner = Build(election, out _, out var logger, a);

        await runner.StartAsync(CancellationToken.None);
        await log.WaitForAsync("start:a").Within();

        await runner.StopAsync(CancellationToken.None);

        log.Before("resign").Should().ContainInOrder("start:a", "stop:a");
        logger.Entries.Should().Contain(e => e.Level == LogLevel.Warning && e.Text.Contains("may still be held", StringComparison.Ordinal));
        logger.Entries.Should().NotContain(e => e.Level == LogLevel.Critical,
            "the Critical line promises another attempt, and there is not going to be one");
        logger.Entries.Should().Contain(e => e.Text.Contains("loop has stopped", StringComparison.Ordinal));
    }

    [Fact]
    public async Task It_stops_even_when_the_shutdown_budget_is_already_spent()
    {
        var log = new CallLog();
        var a = new FakeEngineUnit("a", log);
        var leadership = new FakeLeadership("first", log);
        var election = new FakeLeaderElection(log, leadership);
        var runner = Build(election, out _, out _, a);

        await runner.StartAsync(CancellationToken.None);
        await log.WaitForAsync("start:a").Within();

        using var spent = new CancellationTokenSource();
        await spent.CancelAsync();
        await runner.StopAsync(spent.Token);

        log.Before("resign").Should().ContainInOrder("start:a", "stop:a");
        leadership.WasDisposed.Should().BeTrue();
    }

    [Fact]
    public async Task It_stops_cleanly_when_it_was_never_started()
    {
        var log = new CallLog();
        var election = new FakeLeaderElection(log, new FakeLeadership("first", log));
        var runner = Build(election, out _, out _, new FakeEngineUnit("a", log));

        await runner.StopAsync(CancellationToken.None);
        await runner.StopAsync(CancellationToken.None);

        log.Entries.Should().BeEmpty("nothing was started, so there is nothing to contend for or to drain");
    }

    [Fact]
    public async Task It_starts_one_loop_however_often_it_is_started()
    {
        var log = new CallLog();
        var a = new FakeEngineUnit("a", log);
        var election = new FakeLeaderElection(log, new FakeLeadership("first", log));
        var runner = Build(election, out _, out _, a);

        await runner.StartAsync(CancellationToken.None);
        await runner.StartAsync(CancellationToken.None);
        await log.WaitForAsync("start:a").Within();
        await runner.StopAsync(CancellationToken.None);

        a.Starts.Should().Be(1);
        log.Entries.Count(e => e == "acquire").Should().Be(1);
    }

    [Fact]
    public async Task A_second_stop_waits_for_the_drain_already_in_flight()
    {
        var log = new CallLog();
        var held = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var a = new FakeEngineUnit("a", log, stopGate: held.Task);
        var election = new FakeLeaderElection(log, new FakeLeadership("first", log));
        var runner = Build(election, out _, out _, a);

        await runner.StartAsync(CancellationToken.None);
        await log.WaitForAsync("start:a").Within();

        var first = runner.StopAsync(CancellationToken.None);
        var second = Task.CompletedTask;
        try
        {
            await log.WaitForAsync("stopping:a").Within();
            second = runner.StopAsync(CancellationToken.None);

            first.IsCompleted.Should().BeFalse();
            second.IsCompleted.Should().BeFalse("the second caller has to wait for the SAME drain, not skip it");
        }
        finally
        {
            held.TrySetResult();
        }

        await Task.WhenAll(first, second).Within();
        log.Before("resign").Should().ContainInOrder("start:a", "stop:a");
    }

    [Fact]
    public async Task A_start_that_races_a_stop_does_not_resurrect_the_engine()
    {
        var log = new CallLog();
        var held = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var a = new FakeEngineUnit("a", log, stopGate: held.Task);
        var election = new FakeLeaderElection(log, new FakeLeadership("first", log), new FakeLeadership("second", log));
        var runner = Build(election, out _, out var logger, a);

        await runner.StartAsync(CancellationToken.None);
        await log.WaitForAsync("start:a").Within();

        var stopping = runner.StopAsync(CancellationToken.None);
        try
        {
            await log.WaitForAsync("stopping:a").Within();
            await runner.StartAsync(CancellationToken.None);
        }
        finally
        {
            held.TrySetResult();
        }

        await stopping.Within();

        a.Starts.Should().Be(1);
        election.Attempts.Should().Be(1);
        logger.Entries.Should().Contain(e => e.Level == LogLevel.Warning && e.Text.Contains("after it had been stopped", StringComparison.Ordinal));
    }

    [Fact]
    public void A_script_the_election_does_not_understand_is_rejected_rather_than_read_as_a_loss()
    {
        var election = new FakeLeaderElection(new CallLog(), Task.CompletedTask);

        var acquire = () => { _ = election.TryAcquireAsync(CancellationToken.None); };

        acquire.Should().Throw<ArgumentException>().WithMessage("*Task*");
    }

    private static WorkloadsEngineRunner Build(
        IWorkloadLeaderElection election,
        out ImmediateTimeProvider time,
        out RecordingLogger<WorkloadsEngineRunner> logger,
        params IWorkloadEngineUnit[] units)
    {
        time = new ImmediateTimeProvider();
        logger = new RecordingLogger<WorkloadsEngineRunner>();
        var options = Options.Create(new WorkloadsEngineHostingOptions { LeaderRetryInterval = Retry });
        return new WorkloadsEngineRunner(election, units, options, logger, time);
    }
}
