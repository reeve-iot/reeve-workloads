using Reeve.Workloads.Domain;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Reeve.Workloads.Abstractions;
using Reeve.Workloads.Abstractions.Contracts;
using Reeve.Workloads.Abstractions.Domain.Entities;
using Reeve.Workloads.Core;
using Reeve.Workloads.Core.Watch;
using Reeve.Workloads.Providers.Kubernetes;
using Xunit;

using Reeve.Workloads.Abstractions.Providers;
using Reeve.Workloads.Providers.Docker;
using Reeve.Workloads.Providers.Manual;

namespace Reeve.Workloads.Core.Tests;

public sealed class WorkloadWatchUnitTests
{
    private static readonly TimeSpan DrainTimeout = TimeSpan.FromSeconds(5);
    private static readonly Guid InstanceX = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid InstanceY = Guid.Parse("22222222-2222-2222-2222-222222222222");


    [Fact]
    public void Registers_as_an_engine_unit_with_the_watcher_key()
    {
        var unit = new Harness().Build();

        unit.Should().BeAssignableTo<IWorkloadEngineUnit>(
            "the engine's own runner starts it leader-only once the library wins its leadership, not the "
            + "host's ownership worker, which would key it by a row in the host's own schema");
        ((IWorkloadEngineUnit)unit).Key.Should().Be(WorkloadWatchUnit.Key);
        WorkloadWatchUnit.Key.Should().Be("workload-watcher");
    }

    [Fact]
    public async Task Source_instance_dirty_burst_coalesces_to_a_single_watch_reconcile()
    {
        var h = new Harness();
        var source = h.AddSource(KubernetesProvider.Id);
        var unit = h.Build(holdDebounceWindow: true);
        await unit.StartAsync(CancellationToken.None);

        source.EmitDirty(InstanceX);
        source.EmitDirty(InstanceX);
        source.EmitDirty(InstanceX);

        var drained = unit.WaitForNextDrainAsync(DrainTimeout);
        h.ReleaseDebounceWindow();
        await drained;

        await h.Trigger.Received(1).RequestInstanceAsync(InstanceX, Arg.Any<string>(), Arg.Any<CancellationToken>());
        await h.Trigger.DidNotReceive().RequestAllAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());

        await unit.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Distinct_instances_in_one_window_each_reconcile_once()
    {
        var h = new Harness { EffectiveProvider = DockerProvider.Id };
        var source = h.AddSource(DockerProvider.Id);
        var unit = h.Build(holdDebounceWindow: true);
        await unit.StartAsync(CancellationToken.None);

        source.EmitDirty(InstanceX);
        source.EmitDirty(InstanceY);

        var drained = unit.WaitForNextDrainAsync(DrainTimeout);
        h.ReleaseDebounceWindow();
        await drained;

        await h.Trigger.Received(1).RequestInstanceAsync(InstanceX, Arg.Any<string>(), Arg.Any<CancellationToken>());
        await h.Trigger.Received(1).RequestInstanceAsync(InstanceY, Arg.Any<string>(), Arg.Any<CancellationToken>());

        await unit.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Source_all_dirty_triggers_reconcile_all_and_subsumes_instance_marks()
    {
        var h = new Harness();
        var source = h.AddSource(KubernetesProvider.Id);
        var unit = h.Build(holdDebounceWindow: true);
        await unit.StartAsync(CancellationToken.None);

        source.EmitDirty(InstanceX);
        source.EmitAll();

        var drained = unit.WaitForNextDrainAsync(DrainTimeout);
        h.ReleaseDebounceWindow();
        await drained;

        await h.Trigger.Received(1).RequestAllAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        await h.Trigger.DidNotReceive().RequestInstanceAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<CancellationToken>());

        await unit.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Only_the_kubernetes_source_is_started_when_kubernetes_is_the_effective_provider()
    {
        var h = new Harness { EffectiveProvider = KubernetesProvider.Id };
        var k8s = h.AddSource(KubernetesProvider.Id);
        var docker = h.AddSource(DockerProvider.Id);
        var unit = h.Build();

        await unit.StartAsync(CancellationToken.None);

        k8s.StartCount.Should().Be(1, "the effective provider's source is started");
        docker.StartCount.Should().Be(0, "a non-effective provider's source is NOT started (no pointless failing watch)");

        await unit.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Only_the_docker_source_is_started_when_docker_is_the_effective_provider()
    {
        var h = new Harness { EffectiveProvider = DockerProvider.Id };
        var k8s = h.AddSource(KubernetesProvider.Id);
        var docker = h.AddSource(DockerProvider.Id);
        var unit = h.Build();
        await unit.StartAsync(CancellationToken.None);

        docker.StartCount.Should().Be(1, "docker is effective ⇒ its source is started");
        k8s.StartCount.Should().Be(0, "the non-effective k8s source is NOT started (so it cannot report Degraded)");



        await unit.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Manual_effective_provider_starts_no_source()
    {
        var h = new Harness { EffectiveProvider = ManualProvider.Id };
        var k8s = h.AddSource(KubernetesProvider.Id);
        var docker = h.AddSource(DockerProvider.Id);
        var unit = h.Build();

        await unit.StartAsync(CancellationToken.None);

        k8s.StartCount.Should().Be(0, "Manual provider ⇒ no watch-capable source matches");
        docker.StartCount.Should().Be(0);

        await unit.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task A_provider_an_instance_is_bound_to_is_watched_even_when_auto_detect_answers_another()
    {
        var h = new Harness { EffectiveProvider = ManualProvider.Id };
        h.BoundProviders.Add(DockerProvider.Id);
        var k8s = h.AddSource(KubernetesProvider.Id);
        var docker = h.AddSource(DockerProvider.Id);
        var unit = h.Build();

        await unit.StartAsync(CancellationToken.None);

        docker.StartCount.Should().Be(1,
            "a deployed workload stays on the provider it is bound to, so its watch must not follow a passing auto-detect answer");
        k8s.StartCount.Should().Be(0, "no instance is bound to Kubernetes and it is not effective");

        await unit.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task A_provider_only_terminating_instances_are_bound_to_is_not_watched()
    {
        var h = new Harness { EffectiveProvider = KubernetesProvider.Id };
        h.TerminatingBoundProviders.Add(DockerProvider.Id);
        var k8s = h.AddSource(KubernetesProvider.Id);
        var docker = h.AddSource(DockerProvider.Id);
        var unit = h.Build();

        await unit.StartAsync(CancellationToken.None);

        k8s.StartCount.Should().Be(1);
        docker.StartCount.Should().Be(0,
            "a row that is only being torn down has nothing left to watch, and an unreachable runtime it waits on would keep the fleet on the poll");

        await unit.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task A_teardown_attempt_that_leaves_a_provider_with_no_live_instance_stops_its_watch()
    {
        var h = new Harness { EffectiveProvider = KubernetesProvider.Id };
        h.BoundProviders.Add(DockerProvider.Id);
        var k8s = h.AddSource(KubernetesProvider.Id);
        var docker = h.AddSource(DockerProvider.Id);
        var unit = h.Build();
        await unit.StartAsync(CancellationToken.None);

        h.BoundProviders.Clear();
        h.TerminatingBoundProviders.Add(DockerProvider.Id);
        await unit.RefreshAfterTeardownAttemptAsync(DockerProvider.Id, CancellationToken.None);

        docker.StopCount.Should().Be(1);
        docker.StartCount.Should().Be(1, "nothing live is bound to Docker any more");

        await unit.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task A_teardown_attempt_while_live_rows_are_still_bound_does_not_restart_the_source()
    {
        var h = new Harness { EffectiveProvider = KubernetesProvider.Id };
        h.BoundProviders.Add(DockerProvider.Id);
        h.TerminatingBoundProviders.Add(DockerProvider.Id);
        var docker = h.AddSource(DockerProvider.Id);
        var unit = h.Build();
        await unit.StartAsync(CancellationToken.None);

        await unit.RefreshAfterTeardownAttemptAsync(DockerProvider.Id, CancellationToken.None);

        docker.StopCount.Should().Be(0, "a live workload is still bound to Docker");
        docker.StartCount.Should().Be(1);

        await unit.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task A_bind_to_a_provider_whose_source_is_not_running_starts_it()
    {
        var h = new Harness { EffectiveProvider = ManualProvider.Id };
        var docker = h.AddSource(DockerProvider.Id);
        var unit = h.Build();
        await unit.StartAsync(CancellationToken.None);
        docker.StartCount.Should().Be(0, "sanity: nothing is bound to Docker and it is not effective");

        h.BoundProviders.Add(DockerProvider.Id);
        await unit.RefreshIfUnwatchedAsync(DockerProvider.Id, CancellationToken.None);

        docker.StartCount.Should().Be(1, "a workload just bound to Docker must not run unwatched until a leader restart");

        await unit.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task A_bind_recovers_a_watch_whose_resolution_failed_at_leader_start()
    {
        var h = new Harness { EffectiveProvider = KubernetesProvider.Id, ThrowOnSettingsGet = true };
        var k8s = h.AddSource(KubernetesProvider.Id);
        var unit = h.Build();
        await unit.StartAsync(CancellationToken.None);
        k8s.StartCount.Should().Be(0, "sanity: the resolution failed");

        h.ThrowOnSettingsGet = false;
        h.BoundProviders.Add(KubernetesProvider.Id);
        await unit.RefreshIfUnwatchedAsync(KubernetesProvider.Id, CancellationToken.None);

        k8s.StartCount.Should().Be(1);

        await unit.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task A_bind_to_an_already_watched_provider_resolves_nothing()
    {
        var h = new Harness { EffectiveProvider = KubernetesProvider.Id };
        var k8s = h.AddSource(KubernetesProvider.Id);
        var docker = h.AddSource(DockerProvider.Id);
        var unit = h.Build();
        await unit.StartAsync(CancellationToken.None);

        h.EffectiveProvider = DockerProvider.Id;
        await unit.RefreshIfUnwatchedAsync(KubernetesProvider.Id, CancellationToken.None);

        docker.StartCount.Should().Be(0, "the bound provider is already watched, so the set is not re-read");
        k8s.StopCount.Should().Be(0);

        await unit.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task A_teardown_attempt_for_a_provider_without_a_running_source_changes_nothing()
    {
        var h = new Harness { EffectiveProvider = KubernetesProvider.Id };
        var k8s = h.AddSource(KubernetesProvider.Id);
        var unit = h.Build();
        await unit.StartAsync(CancellationToken.None);

        await unit.RefreshAfterTeardownAttemptAsync(DockerProvider.Id, CancellationToken.None);

        k8s.StopCount.Should().Be(0);
        k8s.StartCount.Should().Be(1);

        await unit.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task A_move_of_the_effective_provider_to_one_without_a_source_leaves_the_bound_providers_watch_running()
    {
        var h = new Harness { EffectiveProvider = DockerProvider.Id };
        h.BoundProviders.Add(DockerProvider.Id);
        var docker = h.AddSource(DockerProvider.Id);
        var unit = h.Build();
        await unit.StartAsync(CancellationToken.None);

        h.EffectiveProvider = ManualProvider.Id;
        await unit.RestartIfProviderMovedAsync(CancellationToken.None);

        docker.StopCount.Should().Be(0, "the sources to run did not change, so the healthy one is not restarted");
        docker.StartCount.Should().Be(1);

        await unit.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task A_move_of_the_effective_provider_adds_its_source_and_keeps_the_bound_providers_watch()
    {
        var h = new Harness { EffectiveProvider = DockerProvider.Id };
        h.BoundProviders.Add(DockerProvider.Id);
        var docker = h.AddSource(DockerProvider.Id);
        var k8s = h.AddSource(KubernetesProvider.Id);
        var unit = h.Build();
        await unit.StartAsync(CancellationToken.None);

        h.EffectiveProvider = KubernetesProvider.Id;
        await unit.RestartIfProviderMovedAsync(CancellationToken.None);

        k8s.StartCount.Should().Be(1, "new installs now land on the new effective provider");
        (docker.StartCount - docker.StopCount).Should().Be(1, "the workloads bound to Docker are still watched");

        await unit.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task An_unresolvable_effective_provider_starts_no_source_and_the_unit_stays_up()
    {
        var h = new Harness { ThrowOnSettingsGet = true };
        var k8s = h.AddSource(KubernetesProvider.Id);
        var unit = h.Build();

        var start = async () => await unit.StartAsync(CancellationToken.None);
        await start.Should().NotThrowAsync();

        k8s.StartCount.Should().Be(0, "an unresolvable effective provider starts no source (poll-only backstop)");

        await unit.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Watch_disabled_starts_inert_with_no_sources()
    {
        var h = new Harness { WatchEnabled = false };
        var source = h.AddSource(KubernetesProvider.Id);
        var unit = h.Build();

        await unit.StartAsync(CancellationToken.None);

        source.StartCount.Should().Be(0, "WatchEnabled=false ⇒ no sources are hosted");

        await unit.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task No_registered_sources_is_inert()
    {
        var h = new Harness();
        var unit = h.Build();

        await unit.StartAsync(CancellationToken.None);

        await h.Trigger.DidNotReceive().RequestAllAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());

        await unit.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Source_throwing_on_start_is_isolated_and_the_unit_stays_up()
    {
        var h = new Harness { EffectiveProvider = KubernetesProvider.Id };
        var bad = h.AddSource(KubernetesProvider.Id, throwOnStart: true);
        var good = h.AddSource(KubernetesProvider.Id);
        var unit = h.Build(holdDebounceWindow: true);

        var start = async () => await unit.StartAsync(CancellationToken.None);
        await start.Should().NotThrowAsync();

        good.StartCount.Should().Be(1, "the good source is still started");

        good.EmitDirty(InstanceX);
        var drained = unit.WaitForNextDrainAsync(DrainTimeout);
        h.ReleaseDebounceWindow();
        await drained;
        await h.Trigger.Received(1).RequestInstanceAsync(InstanceX, Arg.Any<string>(), Arg.Any<CancellationToken>());

        await unit.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Start_and_Stop_are_idempotent()
    {
        var h = new Harness();
        var source = h.AddSource(KubernetesProvider.Id);
        var unit = h.Build();

        await unit.StartAsync(CancellationToken.None);
        await unit.StartAsync(CancellationToken.None);
        source.StartCount.Should().Be(1);

        await unit.StopAsync(CancellationToken.None);
        await unit.StopAsync(CancellationToken.None);
        source.StopCount.Should().Be(1);
    }

    [Fact]
    public async Task Stop_drains_the_coalescer_and_stops_sources()
    {
        var h = new Harness();
        var source = h.AddSource(KubernetesProvider.Id);
        var unit = h.Build();
        await unit.StartAsync(CancellationToken.None);

        await unit.StopAsync(CancellationToken.None);

        source.StopCount.Should().Be(1, "the source is stopped on handoff");

        source.EmitDirty(InstanceX);
        await Task.Delay(50);
        await h.Trigger.DidNotReceive().RequestInstanceAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    private sealed class Harness
    {
        public IWorkloadReconcileTrigger Trigger { get; } = Substitute.For<IWorkloadReconcileTrigger>();
        public bool WatchEnabled { get; init; } = true;
        public int DebounceMilliseconds { get; init; }

        public WorkloadProviderId EffectiveProvider { get; set; } = KubernetesProvider.Id;

        public bool ThrowOnSettingsGet { get; set; }

        public List<WorkloadProviderId> BoundProviders { get; } = new();

        public List<WorkloadProviderId> TerminatingBoundProviders { get; } = new();

        private readonly List<FakeWatchSource> _sources = new();
        private readonly TaskCompletionSource _debounceGate = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public FakeWatchSource AddSource(WorkloadProviderId provider, bool throwOnStart = false)
        {
            var source = new FakeWatchSource(provider, throwOnStart);
            _sources.Add(source);
            return source;
        }

        public void ReleaseDebounceWindow() => _debounceGate.TrySetResult();

        public WorkloadWatchUnit Build(bool holdDebounceWindow = false)
        {
            var reconcilerOptions = Options.Create(new WorkloadReconcilerOptions { WatchEnabled = WatchEnabled });
            var watchOptions = Options.Create(new WorkloadWatchOptions { DebounceMilliseconds = DebounceMilliseconds });

            Func<TimeSpan, CancellationToken, Task> delay = holdDebounceWindow
                ? (_, ct) => _debounceGate.Task.WaitAsync(ct)
                : (_, _) => Task.CompletedTask;

            var services = new ServiceCollection();
            services.AddScoped<IWorkloadOrchestratorSettingsService>(_ => new FakeSettingsService(EffectiveProvider, ThrowOnSettingsGet));
            services.AddScoped(_ => WatchTestInstances.BoundTo(BoundProviders, TerminatingBoundProviders));
            var scopeFactory = services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();

            return new WorkloadWatchUnit(
                Trigger,
                _sources,
                scopeFactory,
                reconcilerOptions,
                watchOptions,
                NullLogger<WorkloadWatchUnit>.Instance,
                NullLogger<WorkloadWatchCoalescer>.Instance,
                delay);
        }
    }

    private sealed class FakeSettingsService : IWorkloadOrchestratorSettingsService
    {
        private readonly WorkloadProviderId _effectiveProvider;
        private readonly bool _throwOnGet;

        public FakeSettingsService(WorkloadProviderId effectiveProvider, bool throwOnGet)
        {
            _effectiveProvider = effectiveProvider;
            _throwOnGet = throwOnGet;
        }

        public Task<WorkloadOrchestratorSettingsResponse> GetAsync(CancellationToken cancellationToken = default)
        {
            if (_throwOnGet)
            {
                throw new InvalidOperationException("settings unavailable");
            }

            return Task.FromResult(new WorkloadOrchestratorSettingsResponse
            {
                Settings = new WorkloadOrchestratorSettings(),
                EffectiveProvider = _effectiveProvider,
                AutoDetected = false,
                Providers = Array.Empty<WorkloadProviderInfo>(),
            });
        }

        public Task<WorkloadProviderProbeResult> TestAsync(WorkloadProviderId? requestedProvider, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }

    private sealed class FakeWatchSource : IWorkloadWatchSource
    {
        private IWorkloadWatchSink? _sink;

        public FakeWatchSource(WorkloadProviderId provider, bool throwOnStart)
        {
            Provider = provider;
            _throwOnStart = throwOnStart;
        }

        private readonly bool _throwOnStart;

        public WorkloadProviderId Provider { get; }
        public int StartCount { get; private set; }
        public int StopCount { get; private set; }

        public Task StartAsync(IWorkloadWatchSink sink, CancellationToken cancellationToken)
        {
            StartCount++;
            if (_throwOnStart)
            {
                throw new InvalidOperationException("watch source failed to connect");
            }

            _sink = sink;
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken)
        {
            StopCount++;
            return Task.CompletedTask;
        }

        public void EmitDirty(Guid instanceId) => _sink!.InstanceDirty(instanceId);
        public void EmitAll() => _sink!.AllDirty();
    }
}

internal static class WatchTestInstances
{
    private static readonly DateTimeOffset At = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    public static IWorkloadInstanceRepository BoundTo(
        IEnumerable<WorkloadProviderId> providers,
        IEnumerable<WorkloadProviderId>? terminatingProviders = null)
    {
        var rows = providers.Select(provider => Row(provider, terminating: false))
            .Concat((terminatingProviders ?? []).Select(provider => Row(provider, terminating: true)))
            .ToList();
        var repository = Substitute.For<IWorkloadInstanceRepository>();
        repository.ListAllAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<WorkloadInstance>>(rows));
        repository.ListRefsAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<WorkloadInstanceRef>>([.. rows.Select(WorkloadInstanceRef.FromInstance)]));
        return repository;
    }

    private static WorkloadInstance Row(WorkloadProviderId provider, bool terminating)
    {
        var row = WorkloadInstance.Create(Guid.NewGuid(), Guid.NewGuid(), "acme.group", "workload", At);
        row.SetProvider(provider, At);
        if (terminating)
        {
            row.TransitionTo(WorkloadInstanceState.Terminating, WorkloadStateReason.Uninstalling, null, At);
        }

        return row;
    }
}
