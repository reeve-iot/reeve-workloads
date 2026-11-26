using System.Runtime.CompilerServices;
using FluentAssertions;
using k8s;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Reeve.Workloads.Abstractions;
using Reeve.Workloads.Abstractions.Contracts;
using Reeve.Workloads.Core.Watch;
using Reeve.Workloads.Providers.Kubernetes;
using Reeve.Workloads.Providers.Kubernetes.Watch;
using Xunit;

namespace Reeve.Workloads.Providers.Kubernetes.Tests;

public sealed class WorkloadKubernetesWatchSourceTests
{
    private static readonly Guid InstanceX = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid InstanceY = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly TimeSpan Timeout5 = TimeSpan.FromSeconds(5);

    [Fact]
    public void Watches_the_kubernetes_provider()
    {
        using var h = new Harness();
        var source = h.Build();

        source.Should().BeAssignableTo<Reeve.Workloads.Core.Watch.IWorkloadWatchSource>();
        source.Provider.Should().Be(KubernetesProvider.Id);
    }

    [Fact]
    public async Task A_blank_configured_namespace_watches_the_pods_own_namespace()
    {
        using var h = new Harness();
        var settings = new WorkloadOrchestratorSettings().WithProvider(KubernetesProvider.Id, new KubernetesProviderSettings());

        var source = h.BuildFromSettings(settings, podNamespace: "smarthome");
        await source.StartAsync(h.Sink, CancellationToken.None);
        await h.WaitUntilAsync(() => h.WatchCallCount >= 1, Timeout5);
        await source.StopAsync(CancellationToken.None);

        h.WatchNamespaces.Should().OnlyContain(n => n == "smarthome");
    }

    [Fact]
    public async Task A_configured_namespace_is_watched_whatever_the_pods_is()
    {
        using var h = new Harness();
        var settings = new WorkloadOrchestratorSettings()
            .WithProvider(KubernetesProvider.Id, new KubernetesProviderSettings { Namespace = "chosen" });

        var source = h.BuildFromSettings(settings, podNamespace: "smarthome");
        await source.StartAsync(h.Sink, CancellationToken.None);
        await h.WaitUntilAsync(() => h.WatchCallCount >= 1, Timeout5);
        await source.StopAsync(CancellationToken.None);

        h.WatchNamespaces.Should().OnlyContain(n => n == "chosen");
    }

    [Fact]
    public async Task Object_events_map_by_label_to_instance_dirty_and_unlabeled_objects_are_ignored()
    {
        using var h = new Harness();
        h.EnqueueEvents(
            WorkloadWatchEvent.Synced(),
            WorkloadWatchEvent.Changed(InstanceX),
            WorkloadWatchEvent.Changed(instanceId: null),
            WorkloadWatchEvent.Changed(InstanceY));

        var source = h.Build();
        await source.StartAsync(h.Sink, CancellationToken.None);

        await h.Sink.WaitUntilAsync(() => h.Sink.DirtyInstances.Count >= 2, Timeout5);

        h.Sink.DirtyInstances.Should().BeEquivalentTo(new[] { InstanceX, InstanceY });
        h.Sink.DirtyInstances.Should().NotContain(Guid.Empty, "an object without a parseable GUID label is ignored, never mapped to a name");

        await source.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Streaming_established_reports_connected()
    {
        using var h = new Harness();
        h.EnqueueEvents(WorkloadWatchEvent.Synced());

        var source = h.Build();
        await source.StartAsync(h.Sink, CancellationToken.None);

        await h.Sink.WaitUntilAsync(() => h.Sink.LastHealth == WorkloadWatchHealth.Connected, Timeout5);

        h.Sink.LastHealth.Should().Be(WorkloadWatchHealth.Connected);

        await source.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Resource_version_too_old_relists_signals_all_dirty_and_resumes_connected()
    {
        using var h = new Harness();
        h.EnqueueThrow(
            new WorkloadWatchResourceVersionTooOldException("watch resourceVersion expired (410)"),
            WorkloadWatchEvent.Synced());
        h.EnqueueEvents(WorkloadWatchEvent.Synced());

        var source = h.Build();
        await source.StartAsync(h.Sink, CancellationToken.None);

        await h.Sink.WaitUntilAsync(() => h.Sink.AllDirtyCount >= 1, Timeout5);
        await h.WaitUntilAsync(() => h.WatchCallCount >= 2, Timeout5);

        h.Sink.AllDirtyCount.Should().BeGreaterThanOrEqualTo(1, "a 410 re-lists and reconciles all");
        h.Sink.HealthReports.Should().NotContain(WorkloadWatchHealth.Degraded, "a 410 resync is NOT a degrade — the watch stays connected");
        h.Sink.LastHealth.Should().Be(WorkloadWatchHealth.Connected);

        await source.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Stream_fault_degrades_to_poll_and_reconnects_with_capped_exponential_backoff()
    {
        using var h = new Harness();
        h.DefaultStream = (_, ct) => Stream.Throw(new InvalidOperationException("watch stream dropped"), ct);

        var source = h.Build();
        await source.StartAsync(h.Sink, CancellationToken.None);

        await h.WaitUntilAsync(() => h.BackoffDelays.Count >= 6, Timeout5);
        await source.StopAsync(CancellationToken.None);

        h.Sink.HealthReports.Should().Contain(WorkloadWatchHealth.Degraded, "a stream fault degrades the provider to poll");
        h.WatchCallCount.Should().BeGreaterThan(1, "the source reconnects after a fault (never stops the watch)");

        var b = h.BackoffDelays;
        b[0].Should().Be(WorkloadKubernetesWatchSource.InitialReconnectBackoff);
        b[1].Should().Be(WorkloadKubernetesWatchSource.InitialReconnectBackoff * 2);
        b[2].Should().Be(WorkloadKubernetesWatchSource.InitialReconnectBackoff * 4);
        b[3].Should().Be(WorkloadKubernetesWatchSource.InitialReconnectBackoff * 8);
        b.Should().OnlyContain(d => d <= WorkloadKubernetesWatchSource.MaxReconnectBackoff, "backoff is capped");
        b.Should().Contain(WorkloadKubernetesWatchSource.MaxReconnectBackoff, "backoff grows to the cap and holds");
    }

    [Fact]
    public async Task Backoff_resets_after_a_healthy_session_so_routine_watch_closes_reconnect_fast()
    {
        using var h = new Harness();
        h.DefaultStream = (_, ct) => Stream.OfThenEnd(ct, WorkloadWatchEvent.Synced());

        var source = h.Build();
        await source.StartAsync(h.Sink, CancellationToken.None);

        await h.WaitUntilAsync(() => h.BackoffDelays.Count >= 5, Timeout5);
        await source.StopAsync(CancellationToken.None);

        h.BackoffDelays.Should().OnlyContain(
            d => d == WorkloadKubernetesWatchSource.InitialReconnectBackoff,
            "a session that connected then closed routinely reconnects at the initial backoff, not an escalated/capped one");
        h.BackoffDelays.Should().NotContain(
            WorkloadKubernetesWatchSource.MaxReconnectBackoff,
            "the backoff must not pin at the cap while healthy sessions keep connecting");
    }

    [Fact]
    public async Task Graceful_stream_end_is_treated_as_a_disconnect_and_reconnects()
    {
        using var h = new Harness();
        h.DefaultStream = (_, ct) => Stream.OfThenEnd(ct, WorkloadWatchEvent.Synced());

        var source = h.Build();
        await source.StartAsync(h.Sink, CancellationToken.None);

        await h.WaitUntilAsync(() => h.WatchCallCount >= 2, Timeout5);
        await source.StopAsync(CancellationToken.None);

        h.Sink.HealthReports.Should().Contain(WorkloadWatchHealth.Degraded, "a stream that ends is a disconnect → degrade-to-poll");
        h.WatchCallCount.Should().BeGreaterThan(1, "the source reconnects after the stream ends");
    }

    [Fact]
    public async Task Connection_fingerprint_change_restarts_the_watch_against_the_new_client()
    {
        using var h = new Harness();
        h.DefaultStream = (_, ct) => Stream.OfThenBlock(ct, WorkloadWatchEvent.Synced());

        var source = h.Build();
        await source.StartAsync(h.Sink, CancellationToken.None);

        await h.WaitUntilAsync(() => h.WatchClients.Contains(h.ClientA), Timeout5);

        h.CurrentClient = h.ClientB;
        h.ReleaseFingerprintPoll();

        await h.WaitUntilAsync(() => h.WatchClients.Contains(h.ClientB), Timeout5);

        h.WatchClients.Should().Contain(h.ClientB, "the watch restarts against the rebuilt (new-endpoint) client");

        await source.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task A_namespace_change_on_the_same_connection_restarts_the_watch_in_the_new_namespace()
    {
        using var h = new Harness();
        h.DefaultStream = (_, ct) => Stream.OfThenBlock(ct, WorkloadWatchEvent.Synced());

        var source = h.Build();
        await source.StartAsync(h.Sink, CancellationToken.None);

        await h.WaitUntilAsync(() => h.WatchNamespaces.Contains("reeve"), Timeout5);

        h.CurrentNamespace = "elsewhere";
        h.ReleaseFingerprintPoll();

        await h.WaitUntilAsync(() => h.WatchNamespaces.Contains("elsewhere"), Timeout5);

        h.WatchClients.Should().OnlyContain(c => c == h.ClientA, "the connection did not change, only the namespace");

        await source.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Stop_exits_cleanly_and_no_sink_calls_happen_afterwards()
    {
        using var h = new Harness();
        h.DefaultStream = (_, ct) => Stream.OfThenBlock(ct, WorkloadWatchEvent.Synced());

        var source = h.Build();
        await source.StartAsync(h.Sink, CancellationToken.None);
        await h.Sink.WaitUntilAsync(() => h.Sink.LastHealth == WorkloadWatchHealth.Connected, Timeout5);

        await source.StopAsync(CancellationToken.None);

        var dirtyBefore = h.Sink.DirtyInstances.Count;
        var allBefore = h.Sink.AllDirtyCount;
        var healthBefore = h.Sink.HealthReports.Count;

        await Task.Delay(60);

        h.Sink.DirtyInstances.Count.Should().Be(dirtyBefore, "no InstanceDirty after Stop");
        h.Sink.AllDirtyCount.Should().Be(allBefore, "no AllDirty after Stop");
        h.Sink.HealthReports.Count.Should().Be(healthBefore, "no health signal after Stop");
    }

    [Fact]
    public async Task Start_and_stop_are_idempotent()
    {
        using var h = new Harness();
        h.DefaultStream = (_, ct) => Stream.OfThenBlock(ct, WorkloadWatchEvent.Synced());

        var source = h.Build();
        await source.StartAsync(h.Sink, CancellationToken.None);
        await source.StartAsync(h.Sink, CancellationToken.None);
        await h.Sink.WaitUntilAsync(() => h.Sink.LastHealth == WorkloadWatchHealth.Connected, Timeout5);

        await source.StopAsync(CancellationToken.None);
        await source.StopAsync(CancellationToken.None);
    }

    private sealed class Harness : IDisposable
    {
        public IKubernetes ClientA { get; } = Substitute.For<IKubernetes>();
        public IKubernetes ClientB { get; } = Substitute.For<IKubernetes>();
        public volatile IKubernetes? CurrentClient;

        public RecordingSink Sink { get; } = new();

        private readonly object _gate = new();
        private readonly Queue<Func<IKubernetes, CancellationToken, IAsyncEnumerable<WorkloadWatchEvent>>> _streams = new();
        public Func<IKubernetes, CancellationToken, IAsyncEnumerable<WorkloadWatchEvent>> DefaultStream { get; set; }
            = (_, ct) => Stream.OfThenBlock(ct, WorkloadWatchEvent.Synced());

        public int WatchCallCount { get; private set; }
        public List<IKubernetes> WatchClients { get; } = new();
        public List<string> WatchNamespaces { get; } = new();
        public List<TimeSpan> BackoffDelays { get; } = new();

        private readonly IKubernetesClientFactory _factory = Substitute.For<IKubernetesClientFactory>();
        private readonly IKubernetesObjectClient _objectClient = Substitute.For<IKubernetesObjectClient>();

        private TaskCompletionSource _pollGate = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Harness()
        {
            CurrentClient = ClientA;

            _factory.CreateClientAsync(Arg.Any<CancellationToken>())
                .Returns(_ => Task.FromResult(CurrentClient!));

            _objectClient
                .WatchWorkloadsAsync(Arg.Any<IKubernetes>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
                .ReturnsForAnyArgs(ci =>
                {
                    var client = ci.Arg<IKubernetes>();
                    var ct = ci.Arg<CancellationToken>();
                    Func<IKubernetes, CancellationToken, IAsyncEnumerable<WorkloadWatchEvent>> factory;
                    lock (_gate)
                    {
                        WatchCallCount++;
                        WatchClients.Add(client);
                        WatchNamespaces.Add(ci.ArgAt<string>(1));
                        factory = _streams.Count > 0 ? _streams.Dequeue() : DefaultStream;
                    }

                    return factory(client, ct);
                });
        }

        public void EnqueueEvents(params WorkloadWatchEvent[] events)
        {
            lock (_gate)
            {
                _streams.Enqueue((_, ct) => Stream.OfThenBlock(ct, events));
            }
        }

        public void EnqueueThrow(Exception ex, params WorkloadWatchEvent[] leading)
        {
            lock (_gate)
            {
                _streams.Enqueue((_, ct) => Stream.Throw(ex, ct, leading));
            }
        }

        public string? CurrentNamespace { get; set; } = "reeve";

        public void ReleaseFingerprintPoll() => _pollGate.TrySetResult();

        public WorkloadKubernetesWatchSource Build()
        {
            var options = new WorkloadWatchOptions();

            Func<TimeSpan, CancellationToken, Task> backoffDelay = (ts, _) =>
            {
                lock (_gate)
                {
                    BackoffDelays.Add(ts);
                }

                return Task.CompletedTask;
            };

            var firstPoll = true;
            Func<TimeSpan, CancellationToken, Task> pollDelay = (_, ct) =>
            {
                bool first;
                lock (_gate)
                {
                    first = firstPoll;
                    firstPoll = false;
                }

                return first ? _pollGate.Task.WaitAsync(ct) : Task.Delay(System.Threading.Timeout.Infinite, ct);
            };

            return new WorkloadKubernetesWatchSource(
                _factory,
                _objectClient,
                _ => Task.FromResult(CurrentNamespace),
                options,
                NullLogger<WorkloadKubernetesWatchSource>.Instance,
                backoffDelay,
                pollDelay);
        }

        public WorkloadKubernetesWatchSource BuildFromSettings(WorkloadOrchestratorSettings settings, string? podNamespace)
        {
            var reader = Substitute.For<IWorkloadOrchestratorSecretReader>();
            reader.GetDecryptedSettingsAsync(Arg.Any<CancellationToken>()).Returns(settings);
            var scopeFactory = new ServiceCollection()
                .AddSingleton(reader)
                .BuildServiceProvider()
                .GetRequiredService<IServiceScopeFactory>();

            return new WorkloadKubernetesWatchSource(
                _factory,
                _objectClient,
                scopeFactory,
                new KubernetesNamespaceResolver(
                    new InClusterEnvironment(_ => podNamespace is not null, "token", () => podNamespace),
                    new ConfigurationBuilder().Build()),
                Options.Create(new WorkloadWatchOptions()),
                NullLogger<WorkloadKubernetesWatchSource>.Instance);
        }

        public async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
        {
            var deadline = DateTime.UtcNow + timeout;
            while (DateTime.UtcNow < deadline)
            {
                bool ok;
                lock (_gate)
                {
                    ok = condition();
                }

                if (ok)
                {
                    return;
                }

                await Task.Delay(10);
            }

            lock (_gate)
            {
                condition().Should().BeTrue("the awaited condition did not hold within the timeout");
            }
        }

        public void Dispose()
        {
            _pollGate.TrySetResult();
        }
    }

    private sealed class RecordingSink : IWorkloadWatchSink
    {
        private readonly object _gate = new();
        private readonly List<Guid> _dirty = new();
        private readonly List<WorkloadWatchHealth> _health = new();
        private int _allDirty;

        public IReadOnlyList<Guid> DirtyInstances
        {
            get { lock (_gate) { return _dirty.ToList(); } }
        }

        public int AllDirtyCount
        {
            get { lock (_gate) { return _allDirty; } }
        }

        public IReadOnlyList<WorkloadWatchHealth> HealthReports
        {
            get { lock (_gate) { return _health.ToList(); } }
        }

        public WorkloadWatchHealth? LastHealth
        {
            get { lock (_gate) { return _health.Count > 0 ? _health[^1] : (WorkloadWatchHealth?)null; } }
        }

        public void InstanceDirty(Guid instanceId)
        {
            lock (_gate) { _dirty.Add(instanceId); }
        }

        public void AllDirty()
        {
            lock (_gate) { _allDirty++; }
        }

        public void ReportHealth(WorkloadProviderId provider, WorkloadWatchHealth health)
        {
            provider.Should().Be(KubernetesProvider.Id);
            lock (_gate) { _health.Add(health); }
        }

        public async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
        {
            var deadline = DateTime.UtcNow + timeout;
            while (DateTime.UtcNow < deadline)
            {
                if (condition())
                {
                    return;
                }

                await Task.Delay(10);
            }

            condition().Should().BeTrue("the awaited sink condition did not hold within the timeout");
        }
    }

    private static class Stream
    {
        public static async IAsyncEnumerable<WorkloadWatchEvent> OfThenBlock(
            [EnumeratorCancellation] CancellationToken ct, params WorkloadWatchEvent[] events)
        {
            foreach (var e in events)
            {
                ct.ThrowIfCancellationRequested();
                yield return e;
            }

            await Task.Delay(System.Threading.Timeout.Infinite, ct);
        }

        public static async IAsyncEnumerable<WorkloadWatchEvent> OfThenEnd(
            [EnumeratorCancellation] CancellationToken ct, params WorkloadWatchEvent[] events)
        {
            foreach (var e in events)
            {
                ct.ThrowIfCancellationRequested();
                yield return e;
            }

            await Task.Yield();
        }

        public static async IAsyncEnumerable<WorkloadWatchEvent> Throw(
            Exception ex, [EnumeratorCancellation] CancellationToken ct, params WorkloadWatchEvent[] leading)
        {
            foreach (var e in leading)
            {
                ct.ThrowIfCancellationRequested();
                yield return e;
            }

            await Task.Yield();
            throw ex;
        }
    }
}
