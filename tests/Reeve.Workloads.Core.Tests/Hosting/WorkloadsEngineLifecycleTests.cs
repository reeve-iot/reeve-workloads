using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Reeve.Workloads.Core;
using Xunit;

namespace Reeve.Workloads.Core.Tests.Hosting;

public sealed class WorkloadsEngineLifecycleTests
{
    [Fact]
    public async Task It_stops_the_application_when_nothing_ran_the_engine()
    {
        using var lifetime = new FakeHostApplicationLifetime();
        var logger = new RecordingLogger<WorkloadsEngineLifecycle>();
        var lifecycle = Build(new WorkloadsEngineStartSignal(), lifetime, logger, out _, out _);

        await lifecycle.StartAsync(CancellationToken.None);
        lifetime.FireStarted();

        lifetime.StopRequests.Should().Be(1, "a host with no engine has to stop, not carry on serving requests "
            + "over a fleet nothing is reconciling");

        var critical = logger.Entries.Should().ContainSingle(e => e.Level == LogLevel.Critical).Subject;
        critical.Text.Should().Contain("RunReeveWorkloadsAsync");

        await lifecycle.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task It_leaves_the_application_alone_when_the_engine_was_run()
    {
        using var lifetime = new FakeHostApplicationLifetime();
        var logger = new RecordingLogger<WorkloadsEngineLifecycle>();
        var signal = new WorkloadsEngineStartSignal();
        signal.TryMarkStarted().Should().BeTrue();
        var lifecycle = Build(signal, lifetime, logger, out _, out _);

        await lifecycle.StartAsync(CancellationToken.None);
        lifetime.FireStarted();

        lifetime.StopRequests.Should().Be(0);
        logger.Entries.Should().NotContain(e => e.Level == LogLevel.Critical);

        await lifecycle.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task It_starts_nothing_itself()
    {
        using var lifetime = new FakeHostApplicationLifetime();
        var lifecycle = Build(new WorkloadsEngineStartSignal(), lifetime, new RecordingLogger<WorkloadsEngineLifecycle>(), out var log, out _);

        await lifecycle.StartAsync(CancellationToken.None);
        lifetime.FireStarted();

        log.Entries.Should().BeEmpty("the lifecycle neither contends for leadership nor starts a unit");

        await lifecycle.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task It_stops_the_runner_on_shutdown()
    {
        using var lifetime = new FakeHostApplicationLifetime();
        var signal = new WorkloadsEngineStartSignal();
        signal.TryMarkStarted();
        var lifecycle = Build(signal, lifetime, new RecordingLogger<WorkloadsEngineLifecycle>(), out var log, out var runner);

        await lifecycle.StartAsync(CancellationToken.None);
        lifetime.FireStarted();
        await runner.StartAsync(CancellationToken.None);
        await log.WaitForAsync("start:a").Within();

        await lifecycle.StopAsync(CancellationToken.None);

        log.Entries.Should().Contain("resign");
        log.Before("resign").Should().ContainInOrder("start:a", "stop:a");
    }

    private static WorkloadsEngineLifecycle Build(
        WorkloadsEngineStartSignal signal,
        FakeHostApplicationLifetime lifetime,
        RecordingLogger<WorkloadsEngineLifecycle> logger,
        out CallLog log,
        out WorkloadsEngineRunner runner)
    {
        log = new CallLog();
        var election = new FakeLeaderElection(log, new FakeLeadership("first", log));
        runner = new WorkloadsEngineRunner(
            election,
            new[] { new FakeEngineUnit("a", log) },
            Options.Create(new WorkloadsEngineHostingOptions()),
            NullLogger<WorkloadsEngineRunner>.Instance,
            new ImmediateTimeProvider());

        return new WorkloadsEngineLifecycle(signal, runner, lifetime, logger);
    }
}
