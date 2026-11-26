using System.Collections.Concurrent;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Reeve.Workloads.Abstractions;
using Reeve.Workloads.Abstractions.Contracts;
using Reeve.Workloads.Abstractions.Domain.Entities;
using Reeve.Workloads.Abstractions.Providers;
using Reeve.Workloads.Core.Telemetry;
using Reeve.Workloads.Core.Tests.Telemetry;
using Reeve.Workloads.Core.Update;
using Reeve.Workloads.Core;
using Reeve.Workloads.Core.Watch;
using Reeve.Workloads.Providers.Kubernetes;
using Xunit;

namespace Reeve.Workloads.Core.Tests;

[Collection(WorkloadsTelemetryCollection.Name)]
public sealed class WorkloadReconcilerUnitTests
{
    private const string PluginTypeId = "zwave";

    [Fact]
    public void Registers_as_an_engine_unit_with_the_reconciler_key()
    {
        var h = new Harness();
        var unit = h.Build();

        unit.Should().BeAssignableTo<IWorkloadEngineUnit>();
        ((IWorkloadEngineUnit)unit).Key.Should().Be(WorkloadReconcilerUnit.Key);

        WorkloadReconcilerUnit.Key.Should().Be("workload-reconciler");
    }

    [Fact]
    public async Task Boot_resumes_open_intents_before_the_first_drift_pass()
    {
        var order = new List<string>();
        var h = new Harness();
        h.Resumer.OnResume = () => order.Add("resume");
        h.Engine.OnReconcile = _ => order.Add("reconcile");
        h.SeedInstance();
        var unit = h.Build();

        await unit.StartAsync(CancellationToken.None);
        await unit.BootPassCompletion;

        order.Should().StartWith("resume");
        order.Should().Contain("reconcile");
        order.IndexOf("resume").Should().BeLessThan(order.IndexOf("reconcile"),
            "the leader resumes open intents BEFORE the first drift pass so drift converges on in-flight targets");
        await unit.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task The_reconcile_pass_leaves_open_intents_to_the_walk()
    {
        var h = new Harness();
        var intent = WorkloadUpdateIntent.Create(Guid.NewGuid(), Guid.NewGuid(), "1.0.0", "1.1.0", h.Clock.UtcNow);
        h.IntentRepository.Open.Add(intent);
        h.SeedInstance();
        var unit = h.Build();

        await unit.StartAsync(CancellationToken.None);
        await unit.BootPassCompletion;
        await unit.RunPeriodicPassAsync(CancellationToken.None);

        h.Planner.Called.Should().BeFalse("walking an intent begins by building its plan, and no pass may do that");
        h.Engine.Reconciled.Should().NotBeEmpty("the pass itself still ran");
        await unit.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Drift_of_a_group_the_walk_holds_waits_for_the_walk()
    {
        var h = new Harness();
        var a = h.SeedInstance();
        var unit = h.Build();
        var walk = await h.Gate.EnterToWalkAsync(a.GroupInstanceId, CancellationToken.None);

        var reconcile = unit.ReconcileOneAsync(a.Id, WorkloadReconcileTrigger.BusNudge, CancellationToken.None);
        await Task.Delay(TimeSpan.FromMilliseconds(300));

        h.Engine.CallCountFor(a.Id).Should().Be(0, "drift must not apply while the walk is applying to the group");
        walk.Dispose();
        await reconcile.WaitAsync(TimeSpan.FromSeconds(10));
        h.Engine.CallCountFor(a.Id).Should().Be(1);
    }

    [Fact]
    public async Task Drift_of_another_group_does_not_wait_for_the_walk()
    {
        var h = new Harness();
        var a = h.SeedInstance();
        var unit = h.Build();
        using var walk = await h.Gate.EnterToWalkAsync(Guid.NewGuid(), CancellationToken.None);

        await unit.ReconcileOneAsync(a.Id, WorkloadReconcileTrigger.BusNudge, CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(10));

        h.Engine.CallCountFor(a.Id).Should().Be(1);
    }

    [Fact]
    public async Task Drift_that_waited_for_the_walk_reconciles_the_row_the_walk_left()
    {
        var h = new Harness();
        var before = h.SeedInstance();
        var unit = h.Build();
        var walk = await h.Gate.EnterToWalkAsync(before.GroupInstanceId, CancellationToken.None);

        var reconcile = unit.ReconcileOneAsync(before.Id, WorkloadReconcileTrigger.BusNudge, CancellationToken.None);
        await WaitUntil(() => h.Repository.WholeRowReadCountFor(before.Id) == 1, "drift never read the row");
        var written = WorkloadInstance.Create(
            before.Id, before.GroupInstanceId, PluginTypeId, before.WorkloadId, h.Clock.UtcNow);
        h.Repository.Touch(written);
        walk.Dispose();
        await reconcile.WaitAsync(TimeSpan.FromSeconds(10));

        h.Engine.ReconciledRows.Should().ContainSingle().Which.Should().BeSameAs(written);
        h.Repository.ReloadCountFor(before.Id).Should().Be(1);
    }

    [Fact]
    public async Task Drift_nobody_raced_reads_its_row_once()
    {
        var h = new Harness();
        var a = h.SeedInstance();
        var unit = h.Build();
        var drift = await h.Gate.EnterToReconcileAsync(a.GroupInstanceId, CancellationToken.None);

        var reconcile = unit.ReconcileOneAsync(a.Id, WorkloadReconcileTrigger.BusNudge, CancellationToken.None);
        await WaitUntil(() => h.Repository.WholeRowReadCountFor(a.Id) == 1, "drift never read the row");
        drift.Dispose();
        await reconcile.WaitAsync(TimeSpan.FromSeconds(10));

        h.Engine.CallCountFor(a.Id).Should().Be(1);
        h.Repository.ReloadCountFor(a.Id).Should().Be(0, "a drift reconcile never waits for another drift, and nothing re-reads");
    }

    [Fact]
    public async Task Boot_still_runs_the_first_drift_pass_when_intent_resume_throws()
    {
        var h = new Harness();
        h.Resumer.OnResume = () => throw new InvalidOperationException("resume boom");
        var a = h.SeedInstance();
        var unit = h.Build();

        await unit.StartAsync(CancellationToken.None);
        await unit.BootPassCompletion;

        h.Engine.Reconciled.Should().Contain(a.Id, "a resume fault must not abort boot — the drift pass still runs");
        await unit.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task A_boot_pass_fault_leaves_the_loops_running_and_a_later_trigger_reconciles()
    {
        var h = new Harness();
        var a = h.SeedInstance();
        h.Repository.ThrowOnceOnListAll();

        var unit = h.Build();
        await unit.StartAsync(CancellationToken.None);
        await unit.BootPassCompletion;
        h.Engine.Reconciled.Clear();

        await h.Bus.DeliverAsync(
            WorkloadClusterChannels.ReconcileRequest,
            new WorkloadReconcileRequestMessage(a.Id, "post-boot-fault"));
        await unit.WaitForQueueAsync();

        h.Engine.Reconciled.Should().Contain(a.Id,
            "a boot-pass fault must not latch the unit dead — the wired triggers still drive reconciles");

        await unit.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Bus_nudge_for_one_instance_reconciles_only_that_instance()
    {
        var h = new Harness();
        var a = h.SeedInstance();
        var b = h.SeedInstance();
        var unit = h.Build();
        await unit.StartAsync(CancellationToken.None);
        await unit.WaitForQueueAsync();
        h.Engine.Reconciled.Clear();

        await h.Bus.DeliverAsync(
            WorkloadClusterChannels.ReconcileRequest,
            new WorkloadReconcileRequestMessage(a.Id, "settings-save"));
        await unit.WaitForQueueAsync();

        h.Engine.Reconciled.Should().ContainSingle().Which.Should().Be(a.Id);
        h.Engine.Reconciled.Should().NotContain(b.Id);

        await unit.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Bus_nudge_with_null_instance_reconciles_all_instances()
    {
        var h = new Harness();
        var a = h.SeedInstance();
        var b = h.SeedInstance();
        var unit = h.Build();
        await unit.StartAsync(CancellationToken.None);
        await unit.WaitForQueueAsync();
        h.Engine.Reconciled.Clear();

        await h.Bus.DeliverAsync(
            WorkloadClusterChannels.ReconcileRequest,
            new WorkloadReconcileRequestMessage(null, "global-save"));
        await unit.WaitForQueueAsync();

        h.Engine.Reconciled.Should().Contain(new[] { a.Id, b.Id });

        await unit.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task A_settings_change_saved_on_another_replica_refreshes_the_watch_before_the_pass()
    {
        var h = new Harness();
        var a = h.SeedInstance();
        var unit = h.Build();
        await unit.StartAsync(CancellationToken.None);
        await unit.WaitForQueueAsync();
        h.Engine.Reconciled.Clear();

        await h.Bus.DeliverAsync(
            WorkloadClusterChannels.ReconcileRequest,
            new WorkloadReconcileRequestMessage(null, WorkloadReconcileReasons.SettingsChanged));
        await unit.WaitForQueueAsync();

        h.WatchRefresh.SettingsChanges.Should().Be(1);
        h.Engine.Reconciled.Should().Contain(a.Id);

        await h.Bus.DeliverAsync(
            WorkloadClusterChannels.ReconcileRequest,
            new WorkloadReconcileRequestMessage(null, "global-save"));
        await unit.WaitForQueueAsync();
        h.WatchRefresh.SettingsChanges.Should().Be(1, "only a settings change moves the watched providers");

        await unit.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task A_presence_reason_reconciles_its_instance_promptly()
    {
        var h = new Harness();
        var a = h.SeedInstance();
        var b = h.SeedInstance();
        var unit = h.Build();
        await unit.StartAsync(CancellationToken.None);
        await unit.WaitForQueueAsync();
        h.Engine.Reconciled.Clear();

        await unit.RequestInstanceAsync(a.Id, WorkloadReconcileReasons.Presence, CancellationToken.None);
        await unit.WaitForQueueAsync();

        h.Engine.Reconciled.Should().ContainSingle().Which.Should().Be(a.Id);
        h.Engine.Reconciled.Should().NotContain(b.Id);

        await unit.StopAsync(CancellationToken.None);
    }

    [Fact]
    public void Implements_the_reconcile_trigger_seam()
    {
        var h = new Harness();
        var unit = h.Build();

        unit.Should().BeAssignableTo<IWorkloadReconcileTrigger>(
            "the watch owned-unit nudges the reconciler through this narrow seam without knowing its internals");
    }

    [Fact]
    public async Task A_presence_nudge_is_attributed_to_Presence_and_a_watch_nudge_to_Watch()
    {
        var h = new Harness();
        var recorder = new RecordingLogger();
        var unit = h.Build(recorder);
        await unit.StartAsync(CancellationToken.None);

        await unit.RequestInstanceAsync(Guid.NewGuid(), WorkloadReconcileReasons.Presence, CancellationToken.None);
        await unit.RequestInstanceAsync(Guid.NewGuid(), "watch", CancellationToken.None);
        await unit.WaitForQueueAsync();

        recorder.AllText.Should().Contain($"({WorkloadReconcileTrigger.Presence})",
            "a presence-driven pass must not report as watch-driven");
        recorder.AllText.Should().Contain($"({WorkloadReconcileTrigger.Watch})");

        await unit.StopAsync(CancellationToken.None);
    }

    [Fact]
    public void Watch_is_a_distinct_reconcile_trigger_so_a_watch_pass_is_attributable()
    {
        var all = Enum.GetValues<WorkloadReconcileTrigger>();
        all.Should().Contain(WorkloadReconcileTrigger.Watch);
        ((int)WorkloadReconcileTrigger.Watch).Should().NotBe((int)WorkloadReconcileTrigger.Timer);
        ((int)WorkloadReconcileTrigger.Watch).Should().NotBe((int)WorkloadReconcileTrigger.BusNudge);
        ((int)WorkloadReconcileTrigger.Watch).Should().NotBe((int)WorkloadReconcileTrigger.Presence);
    }

    [Fact]
    public async Task RequestInstanceAsync_reconciles_only_that_instance_via_the_queue()
    {
        var h = new Harness();
        var a = h.SeedInstance();
        var b = h.SeedInstance();
        IWorkloadReconcileTrigger unit = h.Build();
        await ((WorkloadReconcilerUnit)unit).StartAsync(CancellationToken.None);
        await ((WorkloadReconcilerUnit)unit).WaitForQueueAsync();
        h.Engine.Reconciled.Clear();

        await unit.RequestInstanceAsync(a.Id, "watch:MODIFIED", CancellationToken.None);
        await ((WorkloadReconcilerUnit)unit).WaitForQueueAsync();

        h.Engine.Reconciled.Should().ContainSingle().Which.Should().Be(a.Id);
        h.Engine.Reconciled.Should().NotContain(b.Id);

        await ((WorkloadReconcilerUnit)unit).StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task RequestAllAsync_reconciles_all_instances_via_the_offload_path()
    {
        var h = new Harness();
        var a = h.SeedInstance();
        var b = h.SeedInstance();
        IWorkloadReconcileTrigger unit = h.Build();
        await ((WorkloadReconcilerUnit)unit).StartAsync(CancellationToken.None);
        await ((WorkloadReconcilerUnit)unit).WaitForQueueAsync();
        h.Engine.Reconciled.Clear();

        await unit.RequestAllAsync("watch-resync", CancellationToken.None);
        await ((WorkloadReconcilerUnit)unit).WaitForQueueAsync();

        h.Engine.Reconciled.Should().Contain(new[] { a.Id, b.Id });

        await ((WorkloadReconcilerUnit)unit).StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task A_trigger_request_before_the_unit_is_started_is_a_safe_no_op()
    {
        var h = new Harness();
        var a = h.SeedInstance();
        IWorkloadReconcileTrigger unit = h.Build();

        await unit.RequestInstanceAsync(a.Id, "watch:ADDED", CancellationToken.None);
        await unit.RequestAllAsync("watch-resync", CancellationToken.None);
        await ((WorkloadReconcilerUnit)unit).WaitForQueueAsync();

        h.Engine.Reconciled.Should().BeEmpty("a trigger on a not-started (non-leader) reconciler must not reconcile anything");
    }

    [Fact]
    public async Task Concurrent_watch_requests_are_serialized_by_the_queues_concurrency_limit()
    {
        var h = new Harness();
        var a = h.SeedInstance();
        var b = h.SeedInstance();
        var concurrent = 0;
        var maxConcurrent = 0;
        h.Engine.OnReconcileAsync = async _ =>
        {
            var now = Interlocked.Increment(ref concurrent);
            maxConcurrent = Math.Max(maxConcurrent, now);
            await Task.Delay(20);
            Interlocked.Decrement(ref concurrent);
        };
        IWorkloadReconcileTrigger unit = h.Build();
        await ((WorkloadReconcilerUnit)unit).StartAsync(CancellationToken.None);
        await ((WorkloadReconcilerUnit)unit).WaitForQueueAsync();

        await unit.RequestAllAsync("resync-1", CancellationToken.None);
        await unit.RequestInstanceAsync(a.Id, "watch:MODIFIED", CancellationToken.None);
        await unit.RequestAllAsync("resync-2", CancellationToken.None);
        await ((WorkloadReconcilerUnit)unit).WaitForQueueAsync();

        maxConcurrent.Should().Be(1, "the queue admits one run at a time by default — no parallel reconcile path");

        await ((WorkloadReconcilerUnit)unit).StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Terminating_instance_is_delegated_to_the_lifecycle_teardown_not_the_engine()
    {
        var h = new Harness();
        var live = h.SeedInstance(state: WorkloadInstanceState.Healthy);
        var terminating = h.SeedInstance(state: WorkloadInstanceState.Terminating);
        var unit = h.Build();

        await unit.RunPeriodicPassAsync(CancellationToken.None);

        h.Lifecycle.ToreDown.Should().ContainSingle().Which.Should().Be(terminating.Id,
            "a Terminating instance is delegated to CompleteTeardownAsync (the engine never tears it down)");
        h.Engine.Reconciled.Should().Contain(live.Id, "a live instance still reconciles through the engine");
        h.Engine.Reconciled.Should().NotContain(terminating.Id, "the engine is bypassed for a Terminating instance");
    }

    [Fact]
    public async Task Terminating_teardown_that_reports_retry_pending_leaves_the_row_for_the_next_pass()
    {
        var h = new Harness();
        var terminating = h.SeedInstance(state: WorkloadInstanceState.Terminating);
        h.Lifecycle.RetryPending.Add(terminating.Id);
        var unit = h.Build();

        await unit.RunPeriodicPassAsync(CancellationToken.None);
        await unit.RunPeriodicPassAsync(CancellationToken.None);

        h.Lifecycle.ToreDown.Count(id => id == terminating.Id).Should().Be(2,
            "a retry-pending teardown is re-attempted on the next pass (never dropped)");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Every_teardown_attempt_asks_the_watch_to_drop_a_provider_nothing_live_needs(bool retryPending)
    {
        var h = new Harness();
        var terminating = h.SeedInstance(state: WorkloadInstanceState.Terminating);
        terminating.SetProvider(Reeve.Workloads.Providers.Docker.DockerProvider.Id, h.Clock.UtcNow);
        if (retryPending)
        {
            h.Lifecycle.RetryPending.Add(terminating.Id);
        }

        var unit = h.Build();

        await unit.RunPeriodicPassAsync(CancellationToken.None);

        h.WatchRefresh.Providers.Should().Equal([Reeve.Workloads.Providers.Docker.DockerProvider.Id],
            "a row that is now only being torn down, or is gone, may have been the last reason to watch its provider");
    }

    [Fact]
    public async Task An_unbound_terminating_row_asks_the_watch_nothing()
    {
        var h = new Harness();
        h.SeedInstance(state: WorkloadInstanceState.Terminating);
        var unit = h.Build();

        await unit.RunPeriodicPassAsync(CancellationToken.None);

        h.WatchRefresh.Providers.Should().BeEmpty("a row that never deployed was never a reason to watch anything");
        h.WatchRefresh.Bound.Should().BeEmpty();
    }

    [Fact]
    public async Task A_reconciled_bound_instance_asks_the_watch_to_cover_its_provider()
    {
        var h = new Harness();
        var live = h.SeedInstance(state: WorkloadInstanceState.Healthy);
        live.SetProvider(Reeve.Workloads.Providers.Docker.DockerProvider.Id, h.Clock.UtcNow);
        h.SeedInstance(state: WorkloadInstanceState.PendingConfiguration);
        var unit = h.Build();

        await unit.RunPeriodicPassAsync(CancellationToken.None);

        h.WatchRefresh.Bound.Should().Equal([Reeve.Workloads.Providers.Docker.DockerProvider.Id],
            "an apply may just have bound it to a provider the watch dropped or never resolved");
        h.WatchRefresh.Providers.Should().BeEmpty();
    }

    [Fact]
    public async Task A_fleet_pass_reads_the_whole_fleet_once_before_it_queues_anything()
    {
        var h = new Harness();
        var first = h.SeedInstance(state: WorkloadInstanceState.Healthy);
        var second = h.SeedInstance(state: WorkloadInstanceState.Healthy);
        first.SetProvider(KubernetesProvider.Id, h.Clock.UtcNow);
        second.SetProvider(KubernetesProvider.Id, h.Clock.UtcNow);
        var provider = new CountingBatchProvider();
        h.Registry = new Reeve.Workloads.Core.Providers.WorkloadProviderRegistry([provider]);
        h.FleetStatus = NewFleetCache(h);
        var unit = h.Build();

        await unit.RunPeriodicPassAsync(CancellationToken.None);

        provider.BatchCalls.Should().Be(1, "one call for the fleet is the whole finding");
        provider.LastBatchSize.Should().Be(2);
        h.FleetStatus.Count.Should().Be(2, "the answers are what the per-instance reads will be served from");
    }

    [Fact]
    public async Task A_pass_over_an_unmaterialized_fleet_asks_nothing()
    {
        var h = new Harness();
        h.SeedInstance();
        var provider = new CountingBatchProvider();
        h.Registry = new Reeve.Workloads.Core.Providers.WorkloadProviderRegistry([provider]);
        h.FleetStatus = NewFleetCache(h);
        var unit = h.Build();

        await unit.RunPeriodicPassAsync(CancellationToken.None);

        provider.BatchCalls.Should().Be(0);
    }

    private static Reeve.Workloads.Core.Providers.WorkloadFleetStatusCache NewFleetCache(Harness h)
        => new(h.Clock, Options.Create(new WorkloadReconcilerOptions()), NullLogger<Reeve.Workloads.Core.Providers.WorkloadFleetStatusCache>.Instance);

    private sealed class CountingBatchProvider : IWorkloadProvider
    {
        public int BatchCalls { get; private set; }

        public int LastBatchSize { get; private set; }

        public WorkloadProviderDescriptor Descriptor => KubernetesProvider.Descriptor;

        public Task<IReadOnlyDictionary<WorkloadRef, WorkloadStatus>> GetStatusesAsync(
            IReadOnlyList<WorkloadRef> references, CancellationToken cancellationToken = default)
        {
            BatchCalls++;
            LastBatchSize = references.Count;

            var statuses = new Dictionary<WorkloadRef, WorkloadStatus>();
            foreach (var reference in references)
            {
                statuses[reference] = new WorkloadStatus { Phase = WorkloadProviderPhase.Ready, ProviderReady = true };
            }

            return Task.FromResult<IReadOnlyDictionary<WorkloadRef, WorkloadStatus>>(statuses);
        }

        public Task<WorkloadStatus> GetStatusAsync(WorkloadRef reference, CancellationToken cancellationToken = default)
            => throw new NotSupportedException("this test is about the batch");

        public Task<WorkloadApplyResult> ApplyAsync(
            Reeve.Workloads.Abstractions.Domain.DesiredSpec.WorkloadDesiredSpec spec,
            IProgress<WorkloadApplyProgress>? progress,
            CancellationToken cancellationToken = default, bool forceConflicts = false)
            => throw new NotSupportedException();

        public Task TeardownAsync(WorkloadRef reference, bool retainVolumes, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task SuspendAsync(WorkloadRef reference, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task ResumeAsync(WorkloadRef reference, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<WorkloadPreflightResult> PreflightAsync(
            Reeve.Workloads.Abstractions.Domain.DesiredSpec.WorkloadDesiredSpec spec, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<WorkloadLogs> GetRecentLogsAsync(
            WorkloadRef reference, int tailLines, bool previousContainer, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<WorkloadAttachmentExistence> GetAttachmentExistenceAsync(
            IReadOnlyList<string> attachments, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }

    [Fact]
    public async Task Drift_pass_reconciles_all_instances()
    {
        var h = new Harness();
        var a = h.SeedInstance();
        var b = h.SeedInstance();
        var c = h.SeedInstance();
        var unit = h.Build();

        await unit.RunPeriodicPassAsync(CancellationToken.None);

        h.Engine.Reconciled.Should().Contain(new[] { a.Id, b.Id, c.Id });
    }

    [Fact]
    public async Task Applied_instance_blocks_the_pass_until_it_reaches_Healthy_before_the_next()
    {
        var h = new Harness();
        var a = h.SeedInstance(state: WorkloadInstanceState.PendingConfiguration);
        var b = h.SeedInstance(state: WorkloadInstanceState.PendingConfiguration);
        h.Engine.ApplyToDeploying(a.Id);
        h.Engine.BecomeHealthyAfterEngineCalls(a.Id, calls: 2);
        h.Engine.OnReconcile = id =>
        {
            if (id == b.Id)
            {
                h.Repository.GetTracked(a.Id).State.Should().Be(WorkloadInstanceState.Healthy,
                    "B must not be reconciled until A has crossed the health barrier (staggered rollout)");
            }
        };
        var unit = h.Build();

        await unit.RunPeriodicPassAsync(CancellationToken.None);

        h.Engine.Reconciled.Should().Contain(new[] { a.Id, b.Id });
        h.Engine.CallCountFor(a.Id).Should().BeGreaterThanOrEqualTo(2, "the barrier re-drove A through the engine to settle it Healthy");
    }

    [Fact]
    public async Task The_barrier_releases_promptly_when_the_instance_goes_Healthy_and_does_not_burn_the_timeout()
    {
        var h = new Harness(healthBarrierTimeout: TimeSpan.FromDays(1), healthBarrierPoll: TimeSpan.FromMilliseconds(1));
        var a = h.SeedInstance(state: WorkloadInstanceState.PendingConfiguration);
        var b = h.SeedInstance(state: WorkloadInstanceState.PendingConfiguration);
        h.Engine.ApplyToDeploying(a.Id);
        h.Engine.BecomeHealthyAfterEngineCalls(a.Id, calls: 2);
        var unit = h.Build();

        var pass = unit.RunPeriodicPassAsync(CancellationToken.None);
        var completed = await Task.WhenAny(pass, Task.Delay(TimeSpan.FromSeconds(5)));

        completed.Should().BeSameAs(pass,
            "the barrier must release when the re-drive settles the instance Healthy — never burn the full timeout");
        await pass;
        h.Repository.GetTracked(a.Id).State.Should().Be(WorkloadInstanceState.Healthy);
        h.Engine.Reconciled.Should().Contain(b.Id);
    }

    [Fact]
    public async Task A_stuck_instance_does_not_deadlock_the_pass_after_the_barrier_timeout()
    {
        var h = new Harness(healthBarrierTimeout: TimeSpan.FromSeconds(1), healthBarrierPoll: TimeSpan.FromMilliseconds(1));
        var a = h.SeedInstance(state: WorkloadInstanceState.PendingConfiguration);
        var b = h.SeedInstance(state: WorkloadInstanceState.PendingConfiguration);
        h.Engine.ApplyToDeploying(a.Id);
        h.Engine.AdvanceClockOnCall(a.Id, TimeSpan.FromSeconds(1));
        var unit = h.Build();

        await unit.RunPeriodicPassAsync(CancellationToken.None);

        h.Repository.GetTracked(a.Id).State.Should().Be(WorkloadInstanceState.Deploying, "A is still stuck");
        h.Engine.Reconciled.Should().Contain(b.Id, "a stuck instance must not block the pass forever — B is still reconciled");
    }

    [Fact]
    public async Task The_barrier_releases_on_a_row_read_without_re_driving_the_engine()
    {
        var h = new Harness(
            healthBarrierTimeout: TimeSpan.FromDays(1),
            healthBarrierPoll: TimeSpan.FromMilliseconds(1),
            healthBarrierDriveSeconds: 600);
        var a = h.SeedInstance(state: WorkloadInstanceState.PendingConfiguration);
        h.Engine.ApplyToDeploying(a.Id);
        var unit = h.Build();

        var settled = Task.Run(async () =>
        {
            await WaitUntil(() => h.Repository.GetTracked(a.Id).State == WorkloadInstanceState.Deploying);
            h.Repository.GetTracked(a.Id).TransitionTo(
                WorkloadInstanceState.Healthy, WorkloadStateReason.HealthObserved, null, h.Clock.UtcNow);
        });

        var pass = unit.RunPeriodicPassAsync(CancellationToken.None);
        var finished = await Task.WhenAny(pass, Task.Delay(TimeSpan.FromSeconds(10)));
        await settled;

        finished.Should().BeSameAs(pass, "the barrier must release on the row, not wait out a drive interval");
        await pass;
        h.Engine.CallCountFor(a.Id).Should().Be(1,
            "the apply is the only engine call the barrier needed — every tick after it was a row read");
    }

    [Fact]
    public async Task A_nudge_arriving_while_an_instance_settles_takes_the_full_path()
    {
        var h = new Harness(
            healthBarrierTimeout: TimeSpan.FromDays(1),
            healthBarrierPoll: TimeSpan.FromMilliseconds(1),
            healthBarrierDriveSeconds: 600);
        var unit = h.Build();

        await unit.StartAsync(CancellationToken.None);
        await unit.BootPassCompletion;
        try
        {
            var a = h.SeedInstance(state: WorkloadInstanceState.PendingConfiguration);
            h.Engine.ApplyToDeploying(a.Id);
            h.Engine.BecomeHealthyAfterEngineCalls(a.Id, calls: 2);

            await unit.RequestInstanceAsync(a.Id, "watch:ADDED", CancellationToken.None);
            await WaitUntil(() => h.Repository.GetTracked(a.Id).State == WorkloadInstanceState.Deploying);

            await unit.RequestInstanceAsync(a.Id, "watch:MODIFIED", CancellationToken.None);
            await unit.WaitForQueueAsync();

            h.Repository.GetTracked(a.Id).State.Should().Be(WorkloadInstanceState.Healthy);
            h.Engine.CallCountFor(a.Id).Should().BeGreaterThanOrEqualTo(2, "the nudge was answered with a real reconcile");
        }
        finally
        {
            await unit.StopAsync(CancellationToken.None);
        }
    }

    private static async Task WaitUntil(Func<bool> condition, string because = "the condition under test never became true")
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (!condition())
        {
            DateTime.UtcNow.Should().BeBefore(deadline, because);
            await Task.Delay(1);
        }
    }

    [Fact]
    public async Task Without_a_nudge_the_drive_interval_is_what_moves_the_instance()
    {
        var h = new Harness(
            healthBarrierTimeout: TimeSpan.FromDays(1),
            healthBarrierPoll: TimeSpan.FromMilliseconds(1),
            healthBarrierDriveSeconds: 1);
        var a = h.SeedInstance(state: WorkloadInstanceState.PendingConfiguration);
        h.Engine.ApplyToDeploying(a.Id);
        h.Engine.BecomeHealthyAfterEngineCalls(a.Id, calls: 2);
        var unit = h.Build();

        var pass = unit.RunPeriodicPassAsync(CancellationToken.None);
        var finished = await Task.WhenAny(pass, Task.Delay(TimeSpan.FromSeconds(20)));

        finished.Should().BeSameAs(pass, "the drive interval must still carry an instance nobody is watching");
        await pass;
        h.Repository.GetTracked(a.Id).State.Should().Be(WorkloadInstanceState.Healthy);
        h.Engine.CallCountFor(a.Id).Should().BeLessThan(10,
            "a settling second should cost a couple of drives, not one per poll");
    }

    [Fact]
    public async Task A_settling_instance_is_driven_on_its_own_clock_not_on_every_poll()
    {
        var h = new Harness(
            healthBarrierTimeout: TimeSpan.FromSeconds(60),
            healthBarrierPoll: TimeSpan.FromMilliseconds(50),
            healthBarrierDriveSeconds: 1);
        var a = h.SeedInstance(state: WorkloadInstanceState.PendingConfiguration);
        h.Engine.ApplyToDeploying(a.Id);
        var unit = h.Build();

        var pass = unit.RunPeriodicPassAsync(CancellationToken.None);
        try
        {
            await WaitUntil(() => h.Engine.CallCountFor(a.Id) >= 2, "the drive interval never came round");
            var afterOneDrive = h.Engine.CallCountFor(a.Id);
            var reads = h.Repository.ReadCountFor(a.Id);

            await WaitUntil(() => h.Repository.ReadCountFor(a.Id) >= reads + 5, "the barrier stopped polling");

            h.Engine.CallCountFor(a.Id).Should().Be(afterOneDrive,
                "more polls inside one drive interval must cost row reads and no reconciles");
        }
        finally
        {
            h.Clock.Advance(TimeSpan.FromMinutes(2));
            await Task.WhenAny(pass, Task.Delay(TimeSpan.FromSeconds(10)));
        }

        await pass;
    }

    [Fact]
    public async Task The_fleet_pass_lists_the_projection_and_never_materializes_a_row_to_fill_the_queue()
    {
        var h = new Harness();
        h.SeedInstance(state: WorkloadInstanceState.Healthy);
        h.SeedInstance(state: WorkloadInstanceState.Healthy);

        await h.Build().RunPeriodicPassAsync(CancellationToken.None);

        h.Repository.ListRefsCalls.Should().Be(1, "the pass lists once, as the projection");
        h.Repository.ListAllCalls.Should().Be(0,
            "a whole-entity list is exactly the cost the projection removed — nothing in the pass reads a field the projection lacks");
    }

    [Fact]
    public async Task The_fleet_pass_still_enqueues_every_instance_in_the_same_stable_order()
    {
        var h = new Harness();
        var firstGroup = new Guid("11111111-0000-0000-0000-000000000000");
        var secondGroup = new Guid("22222222-0000-0000-0000-000000000000");

        var fourth = h.SeedInstance(pluginInstanceId: secondGroup, workloadId: "a-workload", state: WorkloadInstanceState.Healthy);
        var third = h.SeedInstance(pluginInstanceId: firstGroup, workloadId: "b-workload", state: WorkloadInstanceState.Healthy);
        var twins = new[] { firstGroup, firstGroup }
            .Select(module => h.SeedInstance(pluginInstanceId: module, workloadId: "a-workload", state: WorkloadInstanceState.Healthy))
            .OrderBy(instance => instance.Id)
            .ToArray();

        await h.Build().RunPeriodicPassAsync(CancellationToken.None);

        h.Engine.Reconciled.Should().ContainInOrder(twins[0].Id, twins[1].Id, third.Id, fourth.Id);
    }

    [Fact]
    public async Task A_barrier_poll_reads_the_state_and_never_the_whole_row()
    {
        var h = new Harness(
            healthBarrierTimeout: TimeSpan.FromDays(1),
            healthBarrierPoll: TimeSpan.FromMilliseconds(1),
            healthBarrierDriveSeconds: 600);
        var a = h.SeedInstance(state: WorkloadInstanceState.PendingConfiguration);
        h.Engine.ApplyToDeploying(a.Id);
        var unit = h.Build();

        var settled = Task.Run(async () =>
        {
            await WaitUntil(() => h.Repository.StateReadCountFor(a.Id) >= 3, "the barrier never polled");
            h.Repository.GetTracked(a.Id).TransitionTo(
                WorkloadInstanceState.Healthy, WorkloadStateReason.HealthObserved, null, h.Clock.UtcNow);
        });

        var pass = unit.RunPeriodicPassAsync(CancellationToken.None);
        var finished = await Task.WhenAny(pass, Task.Delay(TimeSpan.FromSeconds(10)));
        await settled;

        finished.Should().BeSameAs(pass, "the barrier must release on the state read");
        await pass;

        h.Repository.StateReadCountFor(a.Id).Should().BeGreaterThanOrEqualTo(3, "every tick after the apply was a state read");
        h.Repository.WholeRowReadCountFor(a.Id).Should().Be(1,
            "the reconcile that applied is the only whole-row read the barrier needed — the ticks cost one column");
    }

    [Fact]
    public async Task A_full_drive_that_finds_the_row_gone_keeps_the_settling_slot_as_it_always_did()
    {
        var h = new Harness(
            healthBarrierTimeout: TimeSpan.FromSeconds(60),
            healthBarrierPoll: TimeSpan.FromMilliseconds(50),
            healthBarrierDriveSeconds: 0);
        var a = h.SeedInstance(state: WorkloadInstanceState.PendingConfiguration);
        h.Engine.ApplyToDeploying(a.Id);
        var unit = h.Build();
        using var capture = new MeterCapture();

        var pass = unit.RunPeriodicPassAsync(CancellationToken.None);
        try
        {
            await WaitUntil(() => h.Repository.GetTracked(a.Id).State == WorkloadInstanceState.Deploying,
                "the apply that opens the barrier never happened");
            var readsBefore = h.Repository.WholeRowReadCountFor(a.Id);
            h.Repository.Remove(a.Id);

            await WaitUntil(() => h.Repository.WholeRowReadCountFor(a.Id) >= readsBefore + 3,
                "a drive that found the row gone released the settling slot instead of holding it");
        }
        finally
        {
            h.Clock.Advance(TimeSpan.FromMinutes(2));
            await Task.WhenAny(pass, Task.Delay(TimeSpan.FromSeconds(10)));
        }

        await pass;
        capture.Values(ReconcileDuration, ("outcome", "barrier-timeout")).Should().NotBeEmpty(
            "the slot was released by the barrier's deadline, which is what a not-Healthy completion means");
    }

    [Fact]
    public async Task A_row_that_vanished_mid_barrier_releases_its_slot_at_once()
    {
        var h = new Harness(
            healthBarrierTimeout: TimeSpan.FromDays(1),
            healthBarrierPoll: TimeSpan.FromMilliseconds(1),
            healthBarrierDriveSeconds: 600);
        var a = h.SeedInstance(state: WorkloadInstanceState.PendingConfiguration);
        h.Engine.ApplyToDeploying(a.Id);
        var recorder = new RecordingLogger();
        var unit = h.Build(recorder);

        var deleted = Task.Run(async () =>
        {
            await WaitUntil(() => h.Repository.GetTracked(a.Id).State == WorkloadInstanceState.Deploying);
            h.Repository.Remove(a.Id);
        });

        var pass = unit.RunPeriodicPassAsync(CancellationToken.None);
        var finished = await Task.WhenAny(pass, Task.Delay(TimeSpan.FromSeconds(10)));
        await deleted;

        finished.Should().BeSameAs(pass, "a barrier waiting on a row that no longer exists waits forever");
        await pass;

        recorder.AllText.Should().Contain("unknown workload instance",
            "a barrier poll that found no row must say so, exactly as the full read does");
    }

    [Fact]
    public async Task A_reconcile_that_changed_nothing_is_timed_as_a_noop()
    {
        var h = new Harness();
        h.SeedInstance(state: WorkloadInstanceState.Healthy);
        using var capture = new MeterCapture();

        await h.Build().RunPeriodicPassAsync(CancellationToken.None);

        capture.Values(ReconcileDuration, ("trigger", "Timer"), ("outcome", "noop")).Should().ContainSingle();
    }

    [Fact]
    public async Task An_instance_that_applied_and_is_waiting_on_health_is_timed_as_settling()
    {
        var h = new Harness(healthBarrierPoll: TimeSpan.FromMilliseconds(1));
        var a = h.SeedInstance(state: WorkloadInstanceState.PendingConfiguration);
        h.Engine.ApplyToDeploying(a.Id);
        h.Engine.BecomeHealthyAfterEngineCalls(a.Id, calls: 2);
        using var capture = new MeterCapture();

        await h.Build().RunPeriodicPassAsync(CancellationToken.None);

        capture.Values(ReconcileDuration, ("outcome", "settling")).Should().NotBeEmpty("the apply opened the barrier");
        capture.Values(ReconcileDuration, ("outcome", "settled")).Should().NotBeEmpty("the re-drive closed it");
    }

    [Fact]
    public async Task An_instance_that_never_reaches_Healthy_is_counted_as_a_barrier_timeout()
    {
        var h = new Harness(healthBarrierTimeout: TimeSpan.FromSeconds(1), healthBarrierPoll: TimeSpan.FromMilliseconds(1));
        var a = h.SeedInstance(state: WorkloadInstanceState.PendingConfiguration);
        h.Engine.ApplyToDeploying(a.Id);
        h.Engine.AdvanceClockOnCall(a.Id, TimeSpan.FromSeconds(1));
        using var capture = new MeterCapture();

        await h.Build().RunPeriodicPassAsync(CancellationToken.None);

        capture.Values(BarrierTimeouts).Should().ContainSingle().Which.Should().Be(1);
        capture.Values(ReconcileDuration, ("outcome", "barrier-timeout")).Should().ContainSingle();
    }

    [Fact]
    public async Task A_reconcile_that_threw_is_counted_as_a_fault_and_timed_as_one()
    {
        var h = new Harness();
        var a = h.SeedInstance(state: WorkloadInstanceState.Healthy);
        h.Engine.OnReconcile = id => { if (id == a.Id) { throw new InvalidOperationException("boom"); } };
        using var capture = new MeterCapture();

        await h.Build().RunPeriodicPassAsync(CancellationToken.None);

        capture.Values(ReconcileErrors, ("kind", "fault")).Should().ContainSingle();
        capture.Values(ReconcileDuration, ("outcome", "fault")).Should().ContainSingle();
    }

    [Fact]
    public async Task A_lost_write_race_is_counted_apart_from_a_fault()
    {
        var h = new Harness();
        var a = h.SeedInstance(state: WorkloadInstanceState.Healthy);
        h.Engine.OnReconcile = id =>
        {
            if (id == a.Id)
            {
                throw new WorkloadConcurrencyConflictException("another writer got there first") { EntityId = a.Id };
            }
        };
        using var capture = new MeterCapture();

        await h.Build().RunPeriodicPassAsync(CancellationToken.None);

        capture.Values(ReconcileErrors, ("kind", "conflict")).Should().ContainSingle();
        capture.Values(ReconcileErrors, ("kind", "fault")).Should().BeEmpty();
    }

    [Fact]
    public async Task A_completed_pass_is_timed_across_its_whole_drain()
    {
        var h = new Harness();
        h.SeedInstance(state: WorkloadInstanceState.Healthy);
        using var capture = new MeterCapture();

        await h.Build().RunPeriodicPassAsync(CancellationToken.None);

        capture.Values(PassDuration, ("trigger", "Timer")).Should().ContainSingle();
    }

    [Fact]
    public async Task A_pass_cut_off_by_a_lost_lease_is_not_timed_at_all()
    {
        var h = new Harness();
        var a = h.SeedInstance(state: WorkloadInstanceState.Healthy);
        using var cancelled = new CancellationTokenSource();
        h.Engine.OnReconcile = id => { if (id == a.Id) { cancelled.Cancel(); } };
        using var capture = new MeterCapture();

        await h.Build().RunPeriodicPassAsync(cancelled.Token);

        capture.Values(PassDuration).Should().BeEmpty();
    }

    [Fact]
    public async Task A_reconcile_opens_a_span_naming_the_instance_and_the_outcome()
    {
        var h = new Harness();
        var a = h.SeedInstance(state: WorkloadInstanceState.Healthy);
        using var spans = new ActivityCapture();

        await h.Build().RunPeriodicPassAsync(CancellationToken.None);

        var span = spans.Stopped.Should()
            .ContainSingle(s => Equals(s.GetTagItem("workload.instance_id"), a.Id)).Subject;
        span.OperationName.Should().Be("workload.reconcile");
        span.GetTagItem("workload.trigger").Should().Be("Timer");
        span.GetTagItem("workload.outcome").Should().Be("noop");
    }

    private const string ReconcileDuration = "reeve.workloads.reconcile.duration";
    private const string ReconcileErrors = "reeve.workloads.reconcile.errors";
    private const string PassDuration = "reeve.workloads.pass.duration";
    private const string BarrierTimeouts = "reeve.workloads.barrier.timeouts";

    [Fact]
    public async Task A_status_only_Transitioned_flap_imposes_no_health_barrier()
    {
        var h = new Harness(healthBarrierTimeout: TimeSpan.FromDays(1), healthBarrierPoll: TimeSpan.FromMilliseconds(1));
        var a = h.SeedInstance(state: WorkloadInstanceState.PendingConfiguration);
        var b = h.SeedInstance(state: WorkloadInstanceState.PendingConfiguration);
        h.Engine.TransitionedFlap(a.Id);
        var unit = h.Build();

        var pass = unit.RunPeriodicPassAsync(CancellationToken.None);
        var completed = await Task.WhenAny(pass, Task.Delay(TimeSpan.FromSeconds(5)));

        completed.Should().BeSameAs(pass,
            "a status-only Transitioned flap applied nothing — the staggered-rollout barrier must not engage");
        await pass;
        h.Engine.Reconciled.Should().Contain(new[] { a.Id, b.Id });
    }

    [Fact]
    public async Task No_drift_no_op_imposes_no_health_barrier()
    {
        var h = new Harness(healthBarrierTimeout: TimeSpan.FromDays(1));
        var a = h.SeedInstance(state: WorkloadInstanceState.Healthy);
        var b = h.SeedInstance(state: WorkloadInstanceState.Healthy);
        var unit = h.Build();

        var pass = unit.RunPeriodicPassAsync(CancellationToken.None);
        var completed = await Task.WhenAny(pass, Task.Delay(TimeSpan.FromSeconds(5)));

        completed.Should().BeSameAs(pass, "a no-drift pass must not block on a health barrier");
        h.Engine.Reconciled.Should().Contain(new[] { a.Id, b.Id });
    }

    [Fact]
    public async Task A_scan_run_with_a_pending_marker_does_not_engage_the_health_barrier()
    {
        var h = new Harness(healthBarrierTimeout: TimeSpan.FromDays(1), healthBarrierPoll: TimeSpan.FromMilliseconds(1));
        var a = h.SeedInstance(state: WorkloadInstanceState.PendingConfiguration);
        var b = h.SeedInstance(state: WorkloadInstanceState.PendingConfiguration);
        a.BeginScanRun("run-" + Guid.NewGuid().ToString("N"), h.Clock.UtcNow);
        h.Engine.ApplyToDeploying(a.Id);
        var unit = h.Build();

        var pass = unit.RunPeriodicPassAsync(CancellationToken.None);
        var completed = await Task.WhenAny(pass, Task.Delay(TimeSpan.FromSeconds(5)));

        completed.Should().BeSameAs(pass,
            "a scan run fans out Jobs that terminate — the continuous-health barrier must not engage or it would burn the full timeout");
        await pass;
        h.Engine.Reconciled.Should().Contain(b.Id, "the scan instance did not engage the barrier, so b is still reconciled");
        h.Engine.CallCountFor(a.Id).Should().Be(1, "the barrier must NOT re-drive a scan run (no continuous-health wait)");
        h.Repository.GetTracked(a.Id).State.Should().Be(WorkloadInstanceState.Deploying,
            "the scan instance is left exactly as the engine drove it — the barrier neither settled nor timed it out");
    }

    [Fact]
    public async Task A_scan_run_that_settles_Healthy_is_left_Healthy_without_a_barrier_timeout()
    {
        var h = new Harness(healthBarrierTimeout: TimeSpan.FromDays(1), healthBarrierPoll: TimeSpan.FromMilliseconds(1));
        var a = h.SeedInstance(state: WorkloadInstanceState.Deploying);
        var b = h.SeedInstance(state: WorkloadInstanceState.PendingConfiguration);
        a.BeginScanRun("run-" + Guid.NewGuid().ToString("N"), h.Clock.UtcNow);
        h.Engine.ScanRunSettlesHealthy(a.Id);
        var unit = h.Build();

        var pass = unit.RunPeriodicPassAsync(CancellationToken.None);
        var completed = await Task.WhenAny(pass, Task.Delay(TimeSpan.FromSeconds(5)));

        completed.Should().BeSameAs(pass, "the barrier must not engage for a scan run");
        await pass;
        h.Repository.GetTracked(a.Id).State.Should().Be(WorkloadInstanceState.Healthy,
            "a completed scan run rests Healthy (armed) — no false Degraded/timeout from the barrier");
        h.Engine.CallCountFor(a.Id).Should().Be(1, "no barrier re-drive for a scan run");
        h.Engine.Reconciled.Should().Contain(b.Id);
    }

    [Fact]
    public async Task Overlapping_passes_are_serialized_by_the_single_flight_gate()
    {
        var h = new Harness();
        var a = h.SeedInstance();
        var gate = new TaskCompletionSource();
        var entered = new TaskCompletionSource();
        var concurrent = 0;
        var maxConcurrent = 0;
        h.Engine.OnReconcileAsync = async _ =>
        {
            var now = Interlocked.Increment(ref concurrent);
            maxConcurrent = Math.Max(maxConcurrent, now);
            entered.TrySetResult();
            await gate.Task;
            Interlocked.Decrement(ref concurrent);
        };
        var unit = h.Build();

        var first = unit.RunPeriodicPassAsync(CancellationToken.None);
        await entered.Task;
        var second = unit.ReconcileOneAsync(a.Id, WorkloadReconcileTrigger.BusNudge, CancellationToken.None);

        second.IsCompleted.Should().BeFalse("the second pass must wait for the first to release the single-flight gate");
        gate.SetResult();
        await Task.WhenAll(first, second);

        maxConcurrent.Should().Be(1, "no two passes ever run concurrently");
    }

    [Fact]
    public async Task A_pass_still_reconciles_every_instance_while_reconciliation_is_paused()
    {
        var h = new Harness();
        var a = h.SeedInstance();
        var b = h.SeedInstance();
        h.Engine.PausedOutcome(a.Id);
        h.Engine.PausedOutcome(b.Id);
        var unit = h.Build();

        await unit.RunPeriodicPassAsync(CancellationToken.None);

        h.Engine.Reconciled.Should().Contain(new[] { a.Id, b.Id },
            "a paused reconciler still runs passes so status reads continue");
    }

    [Fact]
    public async Task A_faulting_reconcile_tick_does_not_kill_the_loop_it_continues_on_the_next_tick()
    {
        var h = new Harness(statusResyncSeconds: 1);
        var a = h.SeedInstance();
        h.Repository.ThrowOnceOnListAll();
        var unit = h.Build();

        await unit.StartAsync(CancellationToken.None);
        await unit.BootPassCompletion;
        h.Engine.Reconciled.Clear();

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(8);
        while (!h.Engine.Reconciled.Contains(a.Id) && DateTime.UtcNow < deadline)
        {
            await Task.Delay(50);
        }

        h.Engine.Reconciled.Should().Contain(a.Id,
            "a timer loop must survive a transient pass fault and reconcile on a later tick (try/catch is inside the loop)");

        await unit.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task StopAsync_is_idempotent_before_and_after_start()
    {
        var h = new Harness();
        var unit = h.Build();

        await unit.StopAsync(CancellationToken.None);

        await unit.StartAsync(CancellationToken.None);
        await unit.StopAsync(CancellationToken.None);
        await unit.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task StartAsync_finishes_resuming_open_intents_before_it_returns()
    {
        var h = new Harness();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        h.Resumer.Gate = gate.Task;
        h.SeedInstance();
        var unit = h.Build();

        var start = unit.StartAsync(CancellationToken.None);
        await h.Resumer.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));

        start.IsCompleted.Should().BeFalse("the update walk unit starts after the reconciler, so the resumer must be done before it can write the same intent");
        h.Engine.Reconciled.Should().BeEmpty();

        gate.SetResult();
        await start.WaitAsync(TimeSpan.FromSeconds(10));
        await unit.BootPassCompletion.WaitAsync(TimeSpan.FromSeconds(10));
        await unit.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task StartAsync_returns_while_the_boot_reconcile_pass_is_still_running()
    {
        var h = new Harness();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        h.Engine.Gate = gate.Task;
        var a = h.SeedInstance();
        var unit = h.Build();

        await unit.StartAsync(CancellationToken.None);
        await h.Engine.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));

        unit.BootPassCompletion.IsCompleted.Should().BeFalse("the boot pass runs behind StartAsync, so the units after this one start without waiting for it");

        gate.SetResult();
        await unit.BootPassCompletion.WaitAsync(TimeSpan.FromSeconds(10));
        h.Engine.Reconciled.Should().Contain(a.Id);

        await unit.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Cancelling_the_start_token_cancels_a_running_boot_pass_and_stop_awaits_it()
    {
        var h = new Harness();
        h.Engine.Gate = new TaskCompletionSource().Task;
        h.SeedInstance();
        var unit = h.Build();
        using var lease = new CancellationTokenSource();

        await unit.StartAsync(lease.Token);
        var bootToken = await h.Engine.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await lease.CancelAsync();

        bootToken.IsCancellationRequested.Should().BeTrue("a lost lease cancels the boot pass, so the old leader stops applying");
        await unit.BootPassCompletion.WaitAsync(TimeSpan.FromSeconds(10));
        await unit.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task A_lease_lost_while_resuming_intents_cancels_the_start()
    {
        var h = new Harness();
        h.Resumer.Gate = new TaskCompletionSource().Task;
        h.SeedInstance();
        var unit = h.Build();
        using var lease = new CancellationTokenSource();

        var start = unit.StartAsync(lease.Token);
        await h.Resumer.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await lease.CancelAsync();

        var act = () => start.WaitAsync(TimeSpan.FromSeconds(10));
        await act.Should().ThrowAsync<OperationCanceledException>();
        await unit.StopAsync(CancellationToken.None);
        h.Engine.Reconciled.Should().BeEmpty();
    }

    [Fact]
    public async Task The_periodic_pass_unwinds_a_switch_stranded_past_the_bound_and_leaves_a_switch_in_flight()
    {
        var h = new Harness();
        var stranded = h.SeedInstance(state: WorkloadInstanceState.Healthy);
        stranded.TransitionTo(WorkloadInstanceState.Switching, WorkloadStateReason.HealthObserved, null, h.Clock.UtcNow);
        h.Clock.Advance(WorkloadStrandedSwitchUnwind.StrandedAfter);
        var inFlight = h.SeedInstance(state: WorkloadInstanceState.Healthy);
        inFlight.TransitionTo(WorkloadInstanceState.Switching, WorkloadStateReason.HealthObserved, null, h.Clock.UtcNow - TimeSpan.FromSeconds(1));
        var unit = h.Build();

        await unit.RunPeriodicPassAsync(CancellationToken.None);

        stranded.State.Should().Be(WorkloadInstanceState.Degraded, "a replica that died between the two switch commits must not freeze the instance until the leader restarts");
        inFlight.State.Should().Be(WorkloadInstanceState.Switching, "a switch younger than the bound may still be running on another replica");
    }

    [Fact]
    public async Task StartAsync_is_idempotent()
    {
        var h = new Harness();
        h.SeedInstance();
        var unit = h.Build();

        await unit.StartAsync(CancellationToken.None);
        await unit.StartAsync(CancellationToken.None);

        await unit.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task StopAsync_disposes_the_bus_subscription()
    {
        var h = new Harness();
        var unit = h.Build();
        await unit.StartAsync(CancellationToken.None);

        h.Bus.HandlerCount(WorkloadClusterChannels.ReconcileRequest).Should().Be(1);

        await unit.StopAsync(CancellationToken.None);

        h.Bus.HandlerCount(WorkloadClusterChannels.ReconcileRequest).Should().Be(0, "StopAsync unsubscribes the bus channel");
    }

    [Fact]
    public async Task StopAsync_drains_an_in_flight_triggered_pass_before_returning()
    {
        var h = new Harness();
        var a = h.SeedInstance();
        var unit = h.Build();
        await unit.StartAsync(CancellationToken.None);
        await unit.WaitForQueueAsync();
        h.Engine.Reconciled.Clear();

        var entered = new TaskCompletionSource();
        var release = new TaskCompletionSource();
        var finished = false;
        h.Engine.OnReconcileAsync = async _ =>
        {
            entered.TrySetResult();
            await release.Task;
            finished = true;
        };

        await h.Bus.DeliverAsync(
            WorkloadClusterChannels.ReconcileRequest,
            new WorkloadReconcileRequestMessage(a.Id, "in-flight"));
        await entered.Task;

        var stop = unit.StopAsync(CancellationToken.None);
        stop.IsCompleted.Should().BeFalse("StopAsync must wait for the in-flight triggered pass to drain");

        release.SetResult();
        await stop;

        finished.Should().BeTrue("the in-flight triggered pass completed before StopAsync returned (drained on handoff)");
    }

    private sealed class RecordingLogger : ILogger<WorkloadReconcilerUnit>
    {
        private readonly List<string> entries = new();

        public string AllText { get { lock (entries) { return string.Join("\n", entries); } } }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            lock (entries)
            {
                entries.Add(formatter(state, exception));
            }
        }
    }

    [Fact]
    public async Task A_lost_write_race_is_re_queued_rather_than_logged_as_a_fault()
    {
        var h = new Harness();
        var a = h.SeedInstance();
        var attempts = 0;
        h.Engine.OnReconcileAsync = _ =>
        {
            if (Interlocked.Increment(ref attempts) == 1)
            {
                throw new WorkloadConcurrencyConflictException("someone else wrote it") { EntityId = a.Id };
            }

            return Task.CompletedTask;
        };
        var recorder = new RecordingLogger();
        var unit = h.Build(recorder);

        await unit.ReconcileOneAsync(a.Id, WorkloadReconcileTrigger.BusNudge, CancellationToken.None);

        recorder.AllText.Should().NotContain("threw",
            "a lost race is the token working, not a fault — it must not read like one");
        recorder.AllText.Should().Contain("lost a write race");
    }

    [Fact]
    public async Task A_lost_write_race_leaves_the_instance_scheduled_to_be_re_read()
    {
        var h = new Harness();
        var a = h.SeedInstance();
        h.Engine.OnReconcileAsync = _ => throw new WorkloadConcurrencyConflictException("someone else wrote it");
        var unit = h.Build();

        await unit.ReconcileOneAsync(a.Id, WorkloadReconcileTrigger.BusNudge, CancellationToken.None);

        unit.QueueDepth.Should().Be(1,
            "the reconcile did not happen, so dropping the key would leave the instance unconverged until the next drift pass");
    }

    private sealed class Harness
    {
        public FakeReconcileEngine Engine { get; }
        public InMemoryRepository Repository { get; }
        public FakeWorkloadClusterBus Bus { get; } = new();
        public MutableClock Clock { get; } = new(new DateTimeOffset(2026, 7, 6, 12, 0, 0, TimeSpan.Zero));
        public FakeIntentResumer Resumer { get; } = new();
        public FakeUpdateIntentRepository IntentRepository { get; } = new();
        public WorkloadGroupGate Gate { get; } = new();
        public FakeLifecycleService Lifecycle { get; } = new();

        public RecordingWatchRefresh WatchRefresh { get; } = new();
        public SpyUpdatePlanner Planner { get; } = new();

        public Reeve.Workloads.Abstractions.Providers.IWorkloadProviderRegistry? Registry { get; set; }

        public Reeve.Workloads.Core.Providers.WorkloadFleetStatusCache? FleetStatus { get; set; }

        private readonly WorkloadReconcilerOptions options;
        private int seq;

        public Harness(
            TimeSpan? healthBarrierTimeout = null,
            TimeSpan? healthBarrierPoll = null,
            int? healthBarrierDriveSeconds = null,
            int? statusResyncSeconds = null,
            bool watchEnabled = true)
        {
            Repository = new InMemoryRepository(Clock);
            Engine = new FakeReconcileEngine(Repository, Clock);
            options = new WorkloadReconcilerOptions
            {
                StatusResyncSeconds = statusResyncSeconds ?? 60,
                WatchEnabled = watchEnabled,
                HealthBarrierTimeoutSeconds = (int)(healthBarrierTimeout ?? TimeSpan.FromSeconds(120)).TotalSeconds,
                HealthBarrierPollMilliseconds = (int)(healthBarrierPoll ?? TimeSpan.FromMilliseconds(500)).TotalMilliseconds,
                HealthBarrierDriveSeconds = healthBarrierDriveSeconds ?? 1,
            };
        }

        public WorkloadInstance SeedInstance(
            WorkloadInstanceState state = WorkloadInstanceState.PendingConfiguration,
            Guid? pluginInstanceId = null,
            string? workloadId = null)
        {
            var groupInstanceId = pluginInstanceId ?? new Guid(++seq, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);
            var instance = WorkloadInstance.Create(
                Guid.NewGuid(), groupInstanceId, PluginTypeId, workloadId ?? "wl-" + Guid.NewGuid().ToString("N")[..6], Clock.UtcNow);
            DriveTo(instance, state, Clock.UtcNow);
            Repository.Add(instance);
            return instance;
        }

        public WorkloadReconcilerUnit Build(ILogger<WorkloadReconcilerUnit>? logger = null)
        {
            var services = new ServiceCollection();
            services.AddScoped<IWorkloadReconcileEngine>(_ => Engine);
            services.AddScoped<IWorkloadInstanceRepository>(_ => Repository);
            services.AddScoped<IWorkloadLifecycleService>(_ => Lifecycle);
            services.AddSingleton<IWorkloadWatchedProviderRefresh>(WatchRefresh);
            services.AddScoped<IWorkloadIntentResumer>(_ => Resumer);
            services.AddScoped<Reeve.Workloads.Abstractions.IWorkloadUpdateIntentRepository>(_ => IntentRepository);

            services.AddScoped<Reeve.Workloads.Core.Update.IWorkloadUpdatePlanner>(_ => Planner);
            if (Registry is { } registry)
            {
                services.AddScoped(_ => registry);
            }
            else
            {
                services.AddScoped<Reeve.Workloads.Abstractions.Providers.IWorkloadProviderRegistry, InertProviderRegistry>();
            }

            if (FleetStatus is { } fleetStatus)
            {
                services.AddSingleton(fleetStatus);
            }

            services.AddScoped<Reeve.Workloads.Abstractions.Update.IWorkloadUpdateHealthGate, InertHealthGate>();
            services.AddScoped<Reeve.Workloads.Abstractions.Update.IWorkloadUpdateEffects, InertUpdateEffects>();
            services.AddScoped(_ => NSubstitute.Substitute.For<Reeve.Workloads.Core.Update.IWorkloadUpdateRowEffects>());
            services.AddScoped<Reeve.Workloads.Abstractions.IWorkloadDiagnosticsSinkFactory, InertRepairsRegistryFactory>();
            services.AddScoped(_ => NSubstitute.Substitute.For<Reeve.Workloads.Core.Update.IObservedWorkloadVersionReader>());
            services.AddScoped(_ => new Reeve.Workloads.Core.Update.WorkloadUpdateOrchestratorOptions());
            services.AddSingleton<Reeve.Workloads.Abstractions.IWorkloadClock>(new MutableClock(DateTimeOffset.UnixEpoch));
            services.AddScoped<ILogger<Reeve.Workloads.Core.Update.WorkloadUpdateOrchestrator>>(
                _ => NullLogger<Reeve.Workloads.Core.Update.WorkloadUpdateOrchestrator>.Instance);
            services.AddScoped(_ => Reeve.Workloads.Abstractions.Update.WorkloadUpdatePipeline.Resolve(
                Reeve.Workloads.Abstractions.Update.WorkloadUpdateStages.Core.Select((id, index) =>
                    (Reeve.Pipeline.IStage<Reeve.Workloads.Abstractions.Update.WorkloadUpdateStageContext>)new InertStage(
                        id,
                        index == 0
                            ? Reeve.Pipeline.StagePlacement.First
                            : Reeve.Pipeline.StagePlacement.After(
                                Reeve.Workloads.Abstractions.Update.WorkloadUpdateStages.Core[index - 1])))));
            services.AddSingleton<Microsoft.Extensions.Hosting.IHostApplicationLifetime>(
                new Reeve.Workloads.Core.Tests.Hosting.FakeHostApplicationLifetime());
            services.AddScoped<Reeve.Workloads.Core.Update.WorkloadUpdateOrchestrator>();

            var provider = services.BuildServiceProvider();

            return new WorkloadReconcilerUnit(
                provider.GetRequiredService<IServiceScopeFactory>(),
                Bus,
                Clock,
                Gate,
                Options.Create(options),
                logger ?? NullLogger<WorkloadReconcilerUnit>.Instance);
        }

        private static void DriveTo(WorkloadInstance instance, WorkloadInstanceState target, DateTimeOffset now)
        {
            if (instance.State == target)
            {
                return;
            }

            switch (target)
            {
                case WorkloadInstanceState.PendingConfiguration:
                    return;
                case WorkloadInstanceState.Deploying:
                    instance.TransitionTo(WorkloadInstanceState.Deploying, WorkloadStateReason.Provisioning, null, now);
                    return;
                case WorkloadInstanceState.Healthy:
                    instance.TransitionTo(WorkloadInstanceState.Deploying, WorkloadStateReason.Provisioning, null, now);
                    instance.TransitionTo(WorkloadInstanceState.Healthy, WorkloadStateReason.HealthObserved, null, now);
                    return;
                case WorkloadInstanceState.Terminating:
                    instance.TransitionTo(WorkloadInstanceState.Terminating, WorkloadStateReason.Uninstalling, null, now);
                    return;
                default:
                    throw new InvalidOperationException($"Test helper cannot drive to {target}.");
            }
        }
    }

    private sealed class FakeReconcileEngine : IWorkloadReconcileEngine
    {
        public readonly List<Guid> Reconciled = new();
        public readonly List<WorkloadInstance> ReconciledRows = new();
        public Action<Guid>? OnReconcile { get; set; }
        public Func<Guid, Task>? OnReconcileAsync { get; set; }
        public Task? Gate { get; set; }
        public TaskCompletionSource<CancellationToken> Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        private readonly InMemoryRepository repository;
        private readonly MutableClock clock;
        private readonly HashSet<Guid> applyToDeploying = new();
        private readonly HashSet<Guid> transitionedFlap = new();
        private readonly HashSet<Guid> scanSettlesHealthy = new();
        private readonly HashSet<Guid> pausedOutcome = new();
        private readonly Dictionary<Guid, int> becomeHealthyAfter = new();
        private readonly Dictionary<Guid, TimeSpan> advanceOnCall = new();
        private readonly ConcurrentDictionary<Guid, int> callCounts = new();

        public FakeReconcileEngine(InMemoryRepository repository, MutableClock clock)
        {
            this.repository = repository;
            this.clock = clock;
        }

        public void ApplyToDeploying(Guid id) => applyToDeploying.Add(id);
        public void TransitionedFlap(Guid id) => transitionedFlap.Add(id);
        public void ScanRunSettlesHealthy(Guid id) => scanSettlesHealthy.Add(id);
        public void PausedOutcome(Guid id) => pausedOutcome.Add(id);
        public void BecomeHealthyAfterEngineCalls(Guid id, int calls) => becomeHealthyAfter[id] = calls;
        public void AdvanceClockOnCall(Guid id, TimeSpan step) => advanceOnCall[id] = step;

        public int CallCountFor(Guid id) => callCounts.TryGetValue(id, out var n) ? n : 0;

        public async Task<WorkloadReconcileOutcome> ReconcileInstanceAsync(WorkloadInstance instance, CancellationToken cancellationToken = default)
        {
            var callNo = callCounts.AddOrUpdate(instance.Id, 1, (_, n) => n + 1);

            lock (Reconciled)
            {
                Reconciled.Add(instance.Id);
                ReconciledRows.Add(instance);
            }
            OnReconcile?.Invoke(instance.Id);
            Entered.TrySetResult(cancellationToken);
            if (Gate is { } gate)
            {
                await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            if (OnReconcileAsync is { } hook)
            {
                await hook(instance.Id).ConfigureAwait(false);
            }

            if (advanceOnCall.TryGetValue(instance.Id, out var step))
            {
                clock.Advance(step);
            }

            if (becomeHealthyAfter.TryGetValue(instance.Id, out var threshold)
                && callNo >= threshold
                && instance.State == WorkloadInstanceState.Deploying)
            {
                instance.TransitionTo(WorkloadInstanceState.Healthy, WorkloadStateReason.HealthObserved, null, clock.UtcNow);
                repository.Touch(instance);
            }

            if (applyToDeploying.Contains(instance.Id))
            {
                if (instance.State == WorkloadInstanceState.PendingConfiguration)
                {
                    instance.TransitionTo(WorkloadInstanceState.Deploying, WorkloadStateReason.Provisioning, "apply", clock.UtcNow);
                    repository.Touch(instance);
                    return new WorkloadReconcileOutcome
                    {
                        InstanceId = instance.Id,
                        Action = WorkloadReconcileAction.Applied,
                        Reason = WorkloadReconcileReason.Provisioned,
                        ResultingState = WorkloadInstanceState.Deploying,
                    };
                }

                return new WorkloadReconcileOutcome
                {
                    InstanceId = instance.Id,
                    Action = WorkloadReconcileAction.Transitioned,
                    Reason = WorkloadReconcileReason.HealthEvaluated,
                    ResultingState = instance.State,
                };
            }

            if (transitionedFlap.Contains(instance.Id))
            {
                if (instance.State == WorkloadInstanceState.PendingConfiguration)
                {
                    instance.TransitionTo(WorkloadInstanceState.Deploying, WorkloadStateReason.Provisioning, "status-flap", clock.UtcNow);
                    repository.Touch(instance);
                }
                return new WorkloadReconcileOutcome
                {
                    InstanceId = instance.Id,
                    Action = WorkloadReconcileAction.Transitioned,
                    Reason = WorkloadReconcileReason.HealthEvaluated,
                    ResultingState = instance.State,
                };
            }

            if (scanSettlesHealthy.Contains(instance.Id))
            {
                if (instance.State != WorkloadInstanceState.Healthy)
                {
                    instance.TransitionTo(WorkloadInstanceState.Healthy, WorkloadStateReason.HealthObserved, "scan complete", clock.UtcNow);
                }
                instance.ClearScanRun(clock.UtcNow);
                repository.Touch(instance);
                return new WorkloadReconcileOutcome
                {
                    InstanceId = instance.Id,
                    Action = WorkloadReconcileAction.Transitioned,
                    Reason = WorkloadReconcileReason.HealthEvaluated,
                    ResultingState = instance.State,
                };
            }

            if (pausedOutcome.Contains(instance.Id))
            {
                return new WorkloadReconcileOutcome
                {
                    InstanceId = instance.Id,
                    Action = WorkloadReconcileAction.DeferredMutation,
                    Reason = WorkloadReconcileReason.ReconciliationPaused,
                    ResultingState = instance.State,
                    PendingApply = true,
                };
            }

            return WorkloadReconcileOutcome.NoOp(instance.Id, WorkloadReconcileReason.HealthEvaluated, instance.State);
        }
    }

    private sealed class InMemoryRepository : IWorkloadInstanceRepository
    {
        private readonly MutableClock clock;
        private readonly Dictionary<Guid, WorkloadInstance> store = new();
        private readonly ConcurrentDictionary<Guid, int> reads = new();
        private readonly ConcurrentDictionary<Guid, int> wholeRowReads = new();
        private readonly ConcurrentDictionary<Guid, int> stateReads = new();
        private readonly ConcurrentDictionary<Guid, int> reloads = new();

        public InMemoryRepository(MutableClock clock) => this.clock = clock;

        private int throwOnceOnList;
        private int listAllCalls;
        private int listRefsCalls;

        public void Add(WorkloadInstance instance) => store[instance.Id] = instance;
        public void Touch(WorkloadInstance instance) => store[instance.Id] = instance;
        public WorkloadInstance GetTracked(Guid id) => store[id];
        public void Remove(Guid id) => store.Remove(id);

        public int ReadCountFor(Guid id) => reads.TryGetValue(id, out var n) ? n : 0;

        public int WholeRowReadCountFor(Guid id) => wholeRowReads.TryGetValue(id, out var n) ? n : 0;

        public int ReloadCountFor(Guid id) => reloads.TryGetValue(id, out var n) ? n : 0;

        public Task<WorkloadInstance?> ReloadAsync(Guid id, CancellationToken cancellationToken = default)
        {
            reloads.AddOrUpdate(id, 1, (_, n) => n + 1);
            return Task.FromResult(store.TryGetValue(id, out var instance) ? instance : null);
        }

        public int StateReadCountFor(Guid id) => stateReads.TryGetValue(id, out var n) ? n : 0;

        public int ListAllCalls => Volatile.Read(ref listAllCalls);

        public int ListRefsCalls => Volatile.Read(ref listRefsCalls);

        public void ThrowOnceOnListAll() => Interlocked.Exchange(ref throwOnceOnList, 1);

        public Task<WorkloadInstance?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
        {
            reads.AddOrUpdate(id, 1, (_, n) => n + 1);
            wholeRowReads.AddOrUpdate(id, 1, (_, n) => n + 1);
            return Task.FromResult(store.TryGetValue(id, out var instance) ? instance : null);
        }

        public Task<WorkloadInstanceState?> TryGetStateAsync(Guid id, CancellationToken cancellationToken = default)
        {
            reads.AddOrUpdate(id, 1, (_, n) => n + 1);
            stateReads.AddOrUpdate(id, 1, (_, n) => n + 1);
            return Task.FromResult(store.TryGetValue(id, out var instance) ? instance.State : (WorkloadInstanceState?)null);
        }

        public Task<IReadOnlyList<WorkloadInstance>> ListAllAsync(CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref listAllCalls);
            ThrowIfArmed();
            return Task.FromResult<IReadOnlyList<WorkloadInstance>>(store.Values.ToList());
        }

        public Task<IReadOnlyList<WorkloadInstanceRef>> ListRefsAsync(CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref listRefsCalls);
            ThrowIfArmed();
            return Task.FromResult<IReadOnlyList<WorkloadInstanceRef>>(
                [.. store.Values.Select(WorkloadInstanceRef.FromInstance)]);
        }

        private void ThrowIfArmed()
        {
            if (Interlocked.Exchange(ref throwOnceOnList, 0) == 1)
            {
                throw new InvalidOperationException("database unreachable (once)");
            }
        }

        public Task<IReadOnlyDictionary<WorkloadInstanceState, int>> CountByStateAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyDictionary<WorkloadInstanceState, int>>(
                store.Values.GroupBy(i => i.State).ToDictionary(g => g.Key, g => g.Count()));

        public Task<WorkloadInstance?> GetByWorkloadAsync(Guid pluginInstanceId, string workloadId, CancellationToken cancellationToken = default) => Task.FromResult<WorkloadInstance?>(null);
        public Task<IReadOnlyList<WorkloadInstance>> ListByPluginInstanceAsync(Guid pluginInstanceId, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<WorkloadInstance>>(Array.Empty<WorkloadInstance>());
        public Task<IReadOnlyList<WorkloadInstance>> ListByStateAsync(WorkloadInstanceState state, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<WorkloadInstance>>(store.Values.Where(i => i.State == state).ToList());
        public Task AddAsync(WorkloadInstance instance, CancellationToken cancellationToken = default) { Add(instance); return Task.CompletedTask; }
        public Task UpdateAsync(WorkloadInstance instance, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<bool> TrySetRestoreBindingUnresolvedAsync(Guid workloadInstanceId, DateTimeOffset nowUtc, CancellationToken cancellationToken = default) => Task.FromResult(false);
        public Task DeleteAsync(WorkloadInstance instance, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }


    private sealed class FakeIntentResumer : IWorkloadIntentResumer
    {
        public Action? OnResume { get; set; }
        public Task? Gate { get; set; }
        public TaskCompletionSource<CancellationToken> Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task ResumeOpenIntentsAsync(CancellationToken cancellationToken = default)
        {
            OnResume?.Invoke();
            Entered.TrySetResult(cancellationToken);
            if (Gate is { } gate)
            {
                await gate.WaitAsync(cancellationToken);
            }
        }
    }

    private sealed class FakeUpdateIntentRepository : Reeve.Workloads.Abstractions.IWorkloadUpdateIntentRepository
    {
        public List<WorkloadUpdateIntent> Open { get; } = new();

        public Task<WorkloadUpdateIntent?> GetActiveForInstanceAsync(Guid pluginInstanceId, CancellationToken cancellationToken = default)
            => Task.FromResult<WorkloadUpdateIntent?>(null);
        public Task<Guid?> GetActiveIntentIdAsync(Guid pluginInstanceId, CancellationToken cancellationToken = default)
            => Task.FromResult<Guid?>(null);
        public Task<IReadOnlyList<WorkloadUpdateIntent>> GetOpenIntentsAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<WorkloadUpdateIntent>>(Open.ToList());
        public Task<WorkloadUpdateIntent?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
            => Task.FromResult<WorkloadUpdateIntent?>(null);
        public Task<WorkloadUpdateIntent?> GetLatestForInstanceAsync(Guid pluginInstanceId, CancellationToken cancellationToken = default)
            => Task.FromResult<WorkloadUpdateIntent?>(null);
        public Task<IReadOnlyList<WorkloadUpdateIntent>> GetRecentForInstanceAsync(Guid pluginInstanceId, int limit, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<WorkloadUpdateIntent>>(Array.Empty<WorkloadUpdateIntent>());
        public Task AddAsync(WorkloadUpdateIntent intent, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task UpdateAsync(WorkloadUpdateIntent intent, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class SpyUpdatePlanner : Reeve.Workloads.Core.Update.IWorkloadUpdatePlanner
    {
        public bool Called { get; private set; }
        public Guid LastPluginInstanceId { get; private set; }

        public Task<Reeve.Workloads.Abstractions.Update.WorkloadUpdatePlan> BuildPlanAsync(
            Guid pluginInstanceId,
            string candidateVersion,
            Guid? intentId,
            IReadOnlyDictionary<string, string?>? operatorParameters = null,
            IReadOnlyCollection<string>? adoptDeclaredResourceWorkloadIds = null,
            Reeve.Workloads.Core.Update.WorkloadUpdatePlanOrigin? origin = null,
            CancellationToken cancellationToken = default)
        {
            Called = true;
            LastPluginInstanceId = pluginInstanceId;
            throw new InvalidOperationException("spy planner — recorded the AdvanceAsync reach, no plan needed");
        }
    }

    private sealed class InertProviderRegistry : Reeve.Workloads.Abstractions.Providers.IWorkloadProviderRegistry
    {
        public IWorkloadProvider? TryResolve(WorkloadProviderId provider) => null;
        public IWorkloadProvider Resolve(WorkloadProviderId provider) => throw new NotSupportedException();
        public IReadOnlyCollection<WorkloadProviderId> RegisteredProviders => Array.Empty<WorkloadProviderId>();

        public IReadOnlyCollection<WorkloadProviderDescriptor> Descriptors
            => [.. RegisteredProviders.Select(Describe)];

        public WorkloadProviderDescriptor Describe(WorkloadProviderId kind)
            => TryResolve(kind)?.Descriptor ?? ShippedProviders.For(kind);
    }

    private sealed record InertStage(string Id, Reeve.Pipeline.StagePlacement Placement)
        : Reeve.Pipeline.IStage<Reeve.Workloads.Abstractions.Update.WorkloadUpdateStageContext>
    {
        public Reeve.Pipeline.StageReversibility Reversibility => Reeve.Pipeline.StageReversibility.Reversible;

        public Reeve.Pipeline.StageBudget Budget => Reeve.Pipeline.StageBudget.None;

        public ValueTask<Reeve.Pipeline.StageOutcome> UpAsync(
            Reeve.Workloads.Abstractions.Update.WorkloadUpdateStageContext context, CancellationToken cancellationToken)
            => throw new NotSupportedException("the spy planner throws before any stage is reached");

        public ValueTask<Reeve.Pipeline.StageOutcome> DownAsync(
            Reeve.Workloads.Abstractions.Update.WorkloadUpdateStageContext context, CancellationToken cancellationToken)
            => throw new NotSupportedException("the spy planner throws before any stage is reached");
    }

    private sealed class InertHealthGate : Reeve.Workloads.Abstractions.Update.IWorkloadUpdateHealthGate
    {
        public Task<Reeve.Workloads.Abstractions.Update.WorkloadHealthGateTickResult> EvaluateTickAsync(
            Reeve.Workloads.Abstractions.Update.WorkloadHealthGateTickRequest request, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }

    private sealed class InertUpdateEffects : Reeve.Workloads.Abstractions.Update.IWorkloadUpdateEffects
    {
        public Task CommitInstalledVersionAsync(string groupId, string targetVersion, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task CommitParamsAsync(Reeve.Workloads.Abstractions.Update.WorkloadUpdatePlan plan, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task RestorePreviousParamsAsync(WorkloadUpdateIntent intent, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class InertRepairsRegistryFactory : Reeve.Workloads.Abstractions.IWorkloadDiagnosticsSinkFactory
    {
        public Reeve.Workloads.Abstractions.IWorkloadDiagnosticsSink Create(Guid instanceId) => throw new NotSupportedException();

        public void Prune(IEnumerable<Guid> liveInstanceIds)
        {
        }
    }

    private sealed class FakeLifecycleService : IWorkloadLifecycleService
    {
        public readonly List<Guid> ToreDown = new();
        public readonly HashSet<Guid> RetryPending = new();

        public Task<WorkloadGroupAdoptOutcome> AdoptGroupAsync(string groupId, string sourceKey, string? installedVersion, CancellationToken cancellationToken = default) => Task.FromResult(WorkloadGroupAdoptOutcome.Unchanged);
        public Task<WorkloadMaterializeResult> MaterializeAsync(WorkloadGroupInstall install, CancellationToken cancellationToken = default) => Task.FromResult(WorkloadMaterializeResult.Materialized);
        public Task SuspendAsync(Guid groupInstanceId, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task ResumeAsync(Guid groupInstanceId, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task SetParametersAsync(Guid workloadInstanceId, IReadOnlyDictionary<string, string?> resolvedParameters, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<bool> PinAttachmentAsync(Guid workloadInstanceId, Reeve.Workloads.Abstractions.Domain.Network.WorkloadAttachmentPin? pin, CancellationToken cancellationToken = default) => Task.FromResult(false);
        public Task BeginUninstallAsync(Guid pluginInstanceId, WorkloadRetention retention, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<WorkloadForgetResult> ForgetAsync(Guid workloadInstanceId, Guid? actorUserId, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<bool> CompleteTeardownAsync(WorkloadInstance instance, CancellationToken cancellationToken = default)
        {
            ToreDown.Add(instance.Id);
            return Task.FromResult(!RetryPending.Contains(instance.Id));
        }
    }

    private sealed class MutableClock : IWorkloadClock
    {
        private readonly object gate = new();
        private DateTimeOffset now;
        public MutableClock(DateTimeOffset start) => now = start;
        public DateTimeOffset UtcNow { get { lock (gate) { return now; } } }
        public void Advance(TimeSpan by) { lock (gate) { now = now.Add(by); } }
    }
}

internal sealed class RecordingWatchRefresh : IWorkloadWatchedProviderRefresh
{
    public ConcurrentQueue<WorkloadProviderId> Providers { get; } = new();

    public ConcurrentQueue<WorkloadProviderId> Bound { get; } = new();

    public int SettingsChanges { get; private set; }

    public Task RefreshAfterSettingsChangeAsync(CancellationToken cancellationToken)
    {
        SettingsChanges++;
        return Task.CompletedTask;
    }

    public Task RefreshAfterTeardownAttemptAsync(WorkloadProviderId provider, CancellationToken cancellationToken)
    {
        Providers.Enqueue(provider);
        return Task.CompletedTask;
    }

    public Task RefreshIfUnwatchedAsync(WorkloadProviderId provider, CancellationToken cancellationToken)
    {
        Bound.Enqueue(provider);
        return Task.CompletedTask;
    }
}
