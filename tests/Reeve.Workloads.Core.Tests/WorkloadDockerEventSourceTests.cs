using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Docker.DotNet.Models;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Reeve.Workloads.Abstractions.Contracts;
using Reeve.Workloads.Core.Watch;
using Reeve.Workloads.Providers.Docker;
using Reeve.Workloads.Providers.Docker.Watch;
using Xunit;

namespace Reeve.Workloads.Core.Tests;

public sealed class WorkloadDockerEventSourceTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);
    private static readonly Guid InstanceX = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private const string LabelKey = "dev.reeve-iot.workload-instance";

    [Theory]
    [InlineData("die")]
    [InlineData("stop")]
    [InlineData("destroy")]
    [InlineData("oom")]
    [InlineData("start")]
    [InlineData("health_status: unhealthy")]
    public async Task Relevant_event_with_a_valid_label_marks_that_instance_dirty(string action)
    {
        var h = new Harness();
        await using var _ = await h.StartAsync();
        var attempt = await h.Stream.NextAttemptAsync(Timeout);

        attempt.Report(ContainerEvent(action, LabelKey, InstanceX.ToString("D")));

        await h.Sink.WaitForDirtyAsync(InstanceX, Timeout);
        h.Sink.DirtyInstances.Should().ContainSingle().Which.Should().Be(InstanceX);
    }

    [Fact]
    public async Task Event_with_no_instance_label_is_ignored()
    {
        var h = new Harness();
        await using var _ = await h.StartAsync();
        var attempt = await h.Stream.NextAttemptAsync(Timeout);

        attempt.Report(ContainerEvent("die", "com.example.other", "whatever"));

        await Task.Delay(50);
        h.Sink.DirtyInstances.Should().BeEmpty("an event without the dev.reeve-iot.workload-instance label maps to no instance");
    }

    [Fact]
    public async Task Event_with_an_invalid_label_guid_is_ignored()
    {
        var h = new Harness();
        await using var _ = await h.StartAsync();
        var attempt = await h.Stream.NextAttemptAsync(Timeout);

        attempt.Report(ContainerEvent("die", LabelKey, "not-a-guid"));

        await Task.Delay(50);
        h.Sink.DirtyInstances.Should().BeEmpty("a label that is not a parseable GUID is not a valid instance identity");
    }

    [Fact]
    public async Task Irrelevant_event_action_is_ignored()
    {
        var h = new Harness();
        await using var _ = await h.StartAsync();
        var attempt = await h.Stream.NextAttemptAsync(Timeout);

        attempt.Report(ContainerEvent("exec_start", LabelKey, InstanceX.ToString("D")));
        attempt.Report(ContainerEvent("create", LabelKey, InstanceX.ToString("D")));

        await Task.Delay(50);
        h.Sink.DirtyInstances.Should().BeEmpty("only die/stop/destroy/health_status/oom/start schedule a reconcile");
    }

    [Fact]
    public async Task Stream_reports_connected_only_after_it_has_settled()
    {
        var h = new Harness();
        await using var _ = await h.StartAsync();
        var attempt = await h.Stream.NextAttemptAsync(Timeout);

        await Task.Delay(50);
        h.Sink.HealthReports.Should().NotContain(WorkloadWatchHealth.Connected,
            "an opened-but-not-yet-settled stream must not optimistically report Connected");

        await h.ConnectSettleDelay.ReleaseOneAsync(Timeout);
        await h.Sink.WaitForHealthAsync(WorkloadWatchHealth.Connected, Timeout);
        h.Sink.LastHealth.Should().Be(WorkloadWatchHealth.Connected);
    }

    [Fact]
    public async Task First_delivered_event_proves_the_stream_is_connected()
    {
        var h = new Harness();
        await using var _ = await h.StartAsync();
        var attempt = await h.Stream.NextAttemptAsync(Timeout);

        attempt.Report(ContainerEvent("start", LabelKey, InstanceX.ToString("D")));

        await h.Sink.WaitForHealthAsync(WorkloadWatchHealth.Connected, Timeout);
        await h.Sink.WaitForDirtyAsync(InstanceX, Timeout);
    }

    [Fact]
    public async Task Daemon_that_faults_on_connect_never_reports_connected_and_backoff_escalates()
    {
        var h = new Harness();
        await using var _ = await h.StartAsync();

        for (var i = 0; i < 4; i++)
        {
            var attempt = await h.Stream.NextAttemptAsync(Timeout);
            attempt.FaultStream(new InvalidOperationException("cannot connect to the docker daemon socket"));
            await h.ReconnectDelay.ReleaseOneAsync(Timeout);
        }

        h.Sink.HealthReports.Should().NotContain(WorkloadWatchHealth.Connected,
            "a daemon that only ever throws on connect is never genuinely connected");

        var d = h.ReconnectDelay.RequestedDelays;
        d.Count.Should().BeGreaterThanOrEqualTo(4);
        d[0].Should().Be(TimeSpan.FromSeconds(1));
        d[1].Should().Be(TimeSpan.FromSeconds(2));
        d[2].Should().Be(TimeSpan.FromSeconds(4));
        d[3].Should().Be(TimeSpan.FromSeconds(8),
            "the backoff escalates across failed connects instead of resetting to ~1s every cycle");
    }

    [Fact]
    public async Task Stream_end_reports_degraded_and_reconnects()
    {
        var h = new Harness();
        await using var _ = await h.StartAsync();

        var attempt1 = await h.Stream.NextAttemptAsync(Timeout);
        await h.ConnectSettleDelay.ReleaseOneAsync(Timeout);
        await h.Sink.WaitForHealthAsync(WorkloadWatchHealth.Connected, Timeout);
        attempt1.EndStream();

        await h.Sink.WaitForHealthAsync(WorkloadWatchHealth.Degraded, Timeout);

        await h.ReconnectDelay.ReleaseOneAsync(Timeout);
        var attempt2 = await h.Stream.NextAttemptAsync(Timeout);
        attempt2.Should().NotBeNull("the source must reconnect after the stream ends — fault ≠ absence, never a teardown");
    }

    [Fact]
    public async Task Stream_fault_reports_degraded_and_reconnects()
    {
        var h = new Harness();
        await using var _ = await h.StartAsync();

        var attempt1 = await h.Stream.NextAttemptAsync(Timeout);
        attempt1.FaultStream(new InvalidOperationException("socket reset by peer"));

        await h.Sink.WaitForHealthAsync(WorkloadWatchHealth.Degraded, Timeout);

        await h.ReconnectDelay.ReleaseOneAsync(Timeout);
        var attempt2 = await h.Stream.NextAttemptAsync(Timeout);
        attempt2.Should().NotBeNull("a faulting stream reconnects with backoff — the drift poll is the backstop meanwhile");
    }

    [Fact]
    public async Task Socket_uri_poll_uses_a_relaxed_interval_to_avoid_hammering_settings()
    {
        var h = new Harness();
        await using var _ = await h.StartAsync();
        await h.Stream.NextAttemptAsync(Timeout);

        await h.SocketPollDelay.WaitForRequestAsync(Timeout);
        h.SocketPollDelay.RequestedDelays[0].Should().Be(TimeSpan.FromSeconds(30),
            "socket edits are rare so the poll must not hammer the scoped settings DbContext every few seconds");
    }

    [Fact]
    public async Task Socket_uri_change_restarts_the_stream_against_the_new_socket()
    {
        var h = new Harness();
        h.SocketUri = "unix:///var/run/docker.sock";
        await using var _ = await h.StartAsync();

        var attempt1 = await h.Stream.NextAttemptAsync(Timeout);
        attempt1.SocketUri.Should().Be("unix:///var/run/docker.sock");
        await h.ConnectSettleDelay.ReleaseOneAsync(Timeout);
        await h.Sink.WaitForHealthAsync(WorkloadWatchHealth.Connected, Timeout);

        h.SocketUri = "tcp://dockerhost:2375";
        await h.SocketPollDelay.ReleaseOneAsync(Timeout);

        var attempt2 = await h.Stream.NextAttemptAsync(Timeout);
        attempt2.SocketUri.Should().Be("tcp://dockerhost:2375",
            "a socket-URI change must restart the stream against the new socket (single connection per socket)");
        attempt1.Token.IsCancellationRequested.Should().BeTrue("the stale stream against the old socket is cancelled");
    }

    [Fact]
    public async Task Stop_exits_cleanly_with_no_further_sink_calls()
    {
        var h = new Harness();
        var source = await h.StartAsync();
        var attempt1 = await h.Stream.NextAttemptAsync(Timeout);
        await h.ConnectSettleDelay.ReleaseOneAsync(Timeout);
        await h.Sink.WaitForHealthAsync(WorkloadWatchHealth.Connected, Timeout);

        await source.DisposeAsync();

        attempt1.Token.IsCancellationRequested.Should().BeTrue("Stop cancels the live stream");
        var dirtyAfterStop = h.Sink.DirtyInstances.Count;

        await FluentActions.Awaiting(() => h.Stream.NextAttemptAsync(TimeSpan.FromMilliseconds(200)))
            .Should().ThrowAsync<OperationCanceledException>("a stopped source opens no new stream");
        h.Sink.DirtyInstances.Count.Should().Be(dirtyAfterStop);
    }

    private static Message ContainerEvent(string action, string labelKey, string labelValue) => new()
    {
        Type = "container",
        Action = action,
        Actor = new Actor
        {
            ID = "cid",
            Attributes = new Dictionary<string, string> { [labelKey] = labelValue },
        },
    };

    private sealed class Harness
    {
        public FakeEventStream Stream { get; } = new();
        public RecordingSink Sink { get; } = new();
        public ManualDelay ReconnectDelay { get; } = new();
        public ManualDelay SocketPollDelay { get; } = new();
        public ManualDelay ConnectSettleDelay { get; } = new();
        public string? SocketUri { get; set; }

        public async Task<SourceHandle> StartAsync()
        {
            var source = new WorkloadDockerEventSource(
                Stream,
                resolveSocketUri: _ => Task.FromResult(SocketUri),
                reconnectDelay: ReconnectDelay.Delay,
                socketPollDelay: SocketPollDelay.Delay,
                connectSettleDelay: ConnectSettleDelay.Delay,
                NullLogger<WorkloadDockerEventSource>.Instance);
            await source.StartAsync(Sink, CancellationToken.None);
            return new SourceHandle(source);
        }
    }

    private sealed class SourceHandle(WorkloadDockerEventSource source) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync() => await source.StopAsync(CancellationToken.None);
    }

    private sealed class FakeEventStream : IDockerEventStream
    {
        private readonly Channel<Attempt> _attempts = Channel.CreateUnbounded<Attempt>();

        public async Task MonitorAsync(string? socketUri, IProgress<Message> events, CancellationToken cancellationToken)
        {
            var attempt = new Attempt(socketUri, events, cancellationToken);
            await _attempts.Writer.WriteAsync(attempt, CancellationToken.None);
            await attempt.WaitAsync();
        }

        public async Task<Attempt> NextAttemptAsync(TimeSpan timeout)
        {
            using var cts = new CancellationTokenSource(timeout);
            return await _attempts.Reader.ReadAsync(cts.Token);
        }

        public sealed class Attempt
        {
            private readonly IProgress<Message> _events;
            private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            private readonly CancellationTokenRegistration _reg;

            public Attempt(string? socketUri, IProgress<Message> events, CancellationToken token)
            {
                SocketUri = socketUri;
                _events = events;
                Token = token;
                _reg = token.Register(() => _completion.TrySetCanceled(token));
            }

            public string? SocketUri { get; }
            public CancellationToken Token { get; }

            public void Report(Message message) => _events.Report(message);
            public void EndStream() => _completion.TrySetResult();
            public void FaultStream(Exception ex) => _completion.TrySetException(ex);

            public async Task WaitAsync()
            {
                try { await _completion.Task; }
                finally { _reg.Dispose(); }
            }
        }
    }

    private sealed class RecordingSink : IWorkloadWatchSink
    {
        private readonly object _gate = new();
        private readonly List<Guid> _dirty = new();
        private readonly List<WorkloadWatchHealth> _health = new();

        public IReadOnlyList<Guid> DirtyInstances { get { lock (_gate) return _dirty.ToList(); } }
        public IReadOnlyList<WorkloadWatchHealth> HealthReports { get { lock (_gate) return _health.ToList(); } }
        public WorkloadWatchHealth? LastHealth { get { lock (_gate) return _health.Count == 0 ? null : _health[^1]; } }

        public void InstanceDirty(Guid instanceId) { lock (_gate) _dirty.Add(instanceId); }
        public void AllDirty() { }
        public void ReportHealth(WorkloadProviderId provider, WorkloadWatchHealth health)
        {
            provider.Should().Be(DockerProvider.Id, "the docker source only ever reports its own provider health");
            lock (_gate) _health.Add(health);
        }

        public Task WaitForDirtyAsync(Guid id, TimeSpan timeout) => WaitAsync(() => DirtyInstances.Contains(id), timeout);
        public Task WaitForHealthAsync(WorkloadWatchHealth h, TimeSpan timeout)
            => WaitAsync(() => { lock (_gate) return _health.Contains(h); }, timeout);

        private static async Task WaitAsync(Func<bool> predicate, TimeSpan timeout)
        {
            var deadline = DateTime.UtcNow + timeout;
            while (DateTime.UtcNow < deadline)
            {
                if (predicate()) return;
                await Task.Delay(10);
            }
            throw new TimeoutException("condition not met within timeout");
        }
    }

    private sealed class ManualDelay
    {
        private readonly ConcurrentQueue<TaskCompletionSource> _pending = new();
        private readonly SemaphoreSlim _arrived = new(0);
        private readonly object _gate = new();
        private readonly List<TimeSpan> _requested = new();

        public IReadOnlyList<TimeSpan> RequestedDelays { get { lock (_gate) return _requested.ToList(); } }

        public Task Delay(TimeSpan duration, CancellationToken cancellationToken)
        {
            lock (_gate) _requested.Add(duration);
            var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var reg = cancellationToken.Register(() => tcs.TrySetCanceled(cancellationToken));
            tcs.Task.ContinueWith(__ => reg.Dispose(), TaskScheduler.Default);
            _pending.Enqueue(tcs);
            _arrived.Release();
            return tcs.Task;
        }

        public async Task ReleaseOneAsync(TimeSpan timeout)
        {
            if (!await _arrived.WaitAsync(timeout))
            {
                throw new TimeoutException("no delay was awaited within the timeout");
            }
            _pending.TryDequeue(out var tcs);
            tcs!.TrySetResult();
        }

        public async Task WaitForRequestAsync(TimeSpan timeout)
        {
            var deadline = DateTime.UtcNow + timeout;
            while (DateTime.UtcNow < deadline)
            {
                lock (_gate)
                {
                    if (_requested.Count > 0) return;
                }
                await Task.Delay(10);
            }
            throw new TimeoutException("no delay was requested within the timeout");
        }
    }
}
