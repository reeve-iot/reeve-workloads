using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Reeve.Workloads.Abstractions;
using Reeve.Workloads.Abstractions.Domain.Entities;
using Reeve.Workloads.Abstractions.Update;
using Reeve.Pipeline;
using Reeve.Workloads.Core.Update;
using Xunit;
using static Reeve.Workloads.Core.Tests.Update.WorkloadUpdatePlanBuilder;

namespace Reeve.Workloads.Core.Tests.Update;

public sealed class WorkloadUpdateWalkUnitTests
{
    private static readonly WorkloadUpdateWalkOptions Options = new() { WorkingTickSeconds = 7, IdleFallbackSeconds = 97 };

    private static WorkloadUpdatePlan OneWorkload() => Plan(InstanceGuid, [Item("gateway", InstanceGuid, "sha-new", "sha-old")]);

    [Fact]
    public async Task The_first_tick_walks_an_open_intent_as_far_as_its_stages_go()
    {
        var rig = new Rig();
        var intent = await rig.Harness.StageManualAsync(OneWorkload());

        await rig.StartAsync();
        await rig.Time.NextTimerAsync(1);

        intent.Phase.Should().Be(WorkloadUpdatePhase.Completed, "every stage answered Done, so one tick walks them all");
    }

    [Fact]
    public async Task While_a_stage_is_working_the_walk_asks_again_after_the_working_interval()
    {
        var rig = new Rig();
        rig.Harness.Gate.DefaultVerdict = WorkloadHealthGateVerdict.Pending;
        await rig.Harness.StageManualAsync(OneWorkload());

        await rig.StartAsync();

        (await rig.Time.NextTimerAsync(1)).Should().Be(Options.WorkingTickInterval);
        var asked = rig.Harness.Gate.Requests.Count;
        asked.Should().BeGreaterThan(0);

        rig.Time.FireLatest();
        await rig.Time.NextTimerAsync(2);

        rig.Harness.Gate.Requests.Count.Should().BeGreaterThan(asked, "the working interval elapsed, so the gate is asked again");
    }

    [Fact]
    public async Task With_nothing_open_the_walk_sleeps_for_the_idle_fallback()
    {
        var rig = new Rig();

        await rig.StartAsync();

        (await rig.Time.NextTimerAsync(1)).Should().Be(Options.IdleFallbackInterval);
    }

    [Fact]
    public async Task An_update_that_completes_lets_the_walk_sleep()
    {
        var rig = new Rig();
        await rig.Harness.StageManualAsync(OneWorkload());

        await rig.StartAsync();

        (await rig.Time.NextTimerAsync(1)).Should().Be(Options.IdleFallbackInterval,
            "nothing is open once the only intent completes, so there is nothing to re-ask every few seconds");
    }

    [Fact]
    public async Task A_wake_ticks_at_once_instead_of_waiting_out_the_interval()
    {
        var rig = new Rig();
        await rig.StartAsync();
        await rig.Time.NextTimerAsync(1);
        var intent = await rig.Harness.StageManualAsync(OneWorkload());

        rig.Unit.Wake();
        await rig.Time.NextTimerAsync(2);

        intent.Phase.Should().Be(WorkloadUpdatePhase.Completed, "the idle fallback never elapsed; the wake alone ticked");
    }

    [Fact]
    public async Task An_initiation_served_by_a_peer_wakes_the_walk()
    {
        var rig = new Rig();
        await rig.StartAsync();
        await rig.Time.NextTimerAsync(1);
        var intent = await rig.Harness.StageManualAsync(OneWorkload());

        await rig.Bus.DeliverAsync(
            WorkloadClusterChannels.ReconcileRequest,
            new WorkloadReconcileRequestMessage(null, $"{WorkloadUpdateNudgeReasons.Initiated}:{InstanceGuid:D}"));
        await rig.Time.NextTimerAsync(2);

        intent.Phase.Should().Be(WorkloadUpdatePhase.Completed);
    }

    [Fact]
    public async Task An_abandon_served_by_a_peer_wakes_the_walk()
    {
        var rig = new Rig();
        await rig.StartAsync();
        await rig.Time.NextTimerAsync(1);

        await rig.Bus.DeliverAsync(
            WorkloadClusterChannels.ReconcileRequest,
            new WorkloadReconcileRequestMessage(null, $"{WorkloadUpdateNudgeReasons.Abandoned}:{InstanceGuid:D}"));

        await rig.Time.NextTimerAsync(2);
    }

    [Theory]
    [InlineData(true, "watch")]
    [InlineData(false, "watch-resync")]
    [InlineData(false, "workload-update-initiatedX")]
    [InlineData(true, "workload-update-initiated:00000000-0000-0000-0000-000000000001")]
    public async Task A_nudge_that_is_not_about_an_update_does_not_wake_the_walk(bool forOneInstance, string reason)
    {
        var rig = new Rig();
        await rig.StartAsync();
        await rig.Time.NextTimerAsync(1);

        await rig.Bus.DeliverAsync(
            WorkloadClusterChannels.ReconcileRequest,
            new WorkloadReconcileRequestMessage(forOneInstance ? Guid.NewGuid() : null, reason));

        await rig.Time.NoTimerWithinAsync(2, TimeSpan.FromMilliseconds(300));
    }

    [Fact]
    public async Task A_wake_while_not_the_leader_does_nothing()
    {
        var rig = new Rig();
        rig.Unit.Wake();

        await rig.StartAsync();
        await rig.Time.NextTimerAsync(1);

        await rig.Time.NoTimerWithinAsync(2, TimeSpan.FromMilliseconds(300));
    }

    [Fact]
    public async Task Stopping_ends_the_loop_and_leaves_the_bus()
    {
        var rig = new Rig();
        await rig.StartAsync();
        await rig.StartAsync();
        rig.Bus.HandlerCount(WorkloadClusterChannels.ReconcileRequest).Should().Be(1, "a second Start is a no-op");
        await rig.Time.NextTimerAsync(1);

        await rig.Unit.StopAsync(CancellationToken.None);
        await rig.Unit.StopAsync(CancellationToken.None);
        rig.Unit.Wake();

        rig.Bus.HandlerCount(WorkloadClusterChannels.ReconcileRequest).Should().Be(0);
        await rig.Time.NoTimerWithinAsync(2, TimeSpan.FromMilliseconds(300));
    }

    [Fact]
    public async Task The_walk_does_not_touch_a_group_while_drift_is_reconciling_it()
    {
        var rig = new Rig();
        var intent = await rig.Harness.StageManualAsync(OneWorkload());
        var drift = await rig.Gate.EnterToReconcileAsync(InstanceGuid, CancellationToken.None);
        var builds = rig.Harness.Planner.BuildCount;

        await rig.StartAsync();
        await rig.Time.NoTimerWithinAsync(1, TimeSpan.FromMilliseconds(300));
        rig.Harness.Planner.BuildCount.Should().Be(builds, "the walk waits for the group before it reads anything");

        drift.Dispose();
        await rig.Time.NextTimerAsync(1);
        intent.Phase.Should().Be(WorkloadUpdatePhase.Completed);
    }

    [Fact]
    public async Task One_intent_faulting_does_not_hold_up_the_others_and_is_retried_on_the_working_tick()
    {
        var rig = new Rig();
        var intent = await rig.Harness.StageManualAsync(OneWorkload());
        var broken = WorkloadUpdateIntent.Create(Guid.NewGuid(), Guid.NewGuid(), "1.0.0", "1.1.0", rig.Harness.Clock.UtcNow);
        await rig.Harness.Repo.AddAsync(broken);
        rig.Intents.ThrowOnRead = broken.Id;

        await rig.StartAsync();

        (await rig.Time.NextTimerAsync(1)).Should().Be(Options.WorkingTickInterval);
        intent.Phase.Should().Be(WorkloadUpdatePhase.Completed);
    }

    [Fact]
    public async Task A_failed_read_of_the_open_intents_is_retried_on_the_working_tick()
    {
        var rig = new Rig();
        rig.Intents.ThrowOnList = true;

        await rig.StartAsync();

        (await rig.Time.NextTimerAsync(1)).Should().Be(Options.WorkingTickInterval);
    }

    [Fact]
    public async Task An_intent_that_ended_before_its_turn_is_not_walked()
    {
        var rig = new Rig();
        var intent = await rig.Harness.StageManualAsync(OneWorkload());
        rig.Intents.OnRead = read =>
        {
            read.Fail("abandoned by the operator", rig.Harness.Clock.UtcNow);
            read.MarkRollbackFailed("abandoned by the operator", rig.Harness.Clock.UtcNow);
            rig.Intents.OnRead = null;
        };
        var builds = rig.Harness.Planner.BuildCount;

        await rig.StartAsync();

        (await rig.Time.NextTimerAsync(1)).Should().Be(Options.IdleFallbackInterval);
        rig.Harness.Planner.BuildCount.Should().Be(builds);
    }

    [Fact]
    public async Task A_stage_that_keeps_throwing_past_the_point_of_no_return_is_recorded_and_lets_an_abandon_end_it()
    {
        var rig = new Rig();
        var intent = await rig.Harness.StageManualAsync(OneWorkload());
        var hostCalls = 0;
        rig.Harness.HostStage.OnCall = () =>
        {
            hostCalls++;
            throw new InvalidOperationException("host roll boom");
        };
        var abandon = new WorkloadUpdateAbandonService(rig.Harness.Repo, new RecordingNudge(), new RecordingAuditWriter(),
            rig.Harness.Clock, rig.Harness.Pipeline, NullLogger<WorkloadUpdateAbandonService>.Instance,
            NSubstitute.Substitute.For<IWorkloadGroupRepository>(), NSubstitute.Substitute.For<IWorkloadInstanceRepository>());

        await rig.StartAsync();
        (await rig.Time.NextTimerAsync(1)).Should().Be(Options.WorkingTickInterval);
        var since = rig.Harness.Clock.UtcNow;

        intent.CurrentStage.Should().Be(ScriptedHostStage.StageId, "the irreversible stage was entered before its call threw");
        intent.WalkFault.Should().Contain("host roll boom");
        intent.WalkFaultSinceUtc.Should().Be(since);

        rig.Harness.Clock.Advance(TimeSpan.FromSeconds(30));
        rig.Time.FireLatest();
        await rig.Time.NextTimerAsync(2);
        intent.WalkFaultSinceUtc.Should().Be(since, "the run of failures began with the first one");
        var versionWhileFailing = intent.Version;
        rig.Time.FireLatest();
        await rig.Time.NextTimerAsync(3);
        intent.Version.Should().Be(versionWhileFailing, "the same fault again writes nothing an abandon could lose to");
        (await abandon.AbandonAsync(intent.Id, null)).Status.Should().Be(WorkloadUpdateAbandonStatus.PastThePointOfNoReturn);

        rig.Harness.Clock.Advance(TimeSpan.FromSeconds(30));
        var ended = await abandon.AbandonAsync(intent.Id, null);

        ended.Status.Should().Be(WorkloadUpdateAbandonStatus.Abandoned);
        ended.IsTerminal.Should().BeFalse("the abandon only records the turn; the walk ends it");
        var callsBefore = hostCalls;
        rig.Time.FireLatest();
        await rig.Time.NextTimerAsync(4);

        intent.Outcome.Should().Be(WorkloadUpdateOutcome.RollbackFailed);
        intent.Direction.Should().Be(WorkloadUpdateDirection.Forward);
        intent.Error.Should().Contain("host roll boom").And.Contain("nothing was undone");
        hostCalls.Should().Be(callsBefore, "the walk ends the update instead of calling the stage again");
        rig.Harness.HostStage.Downs.Should().Be(0);
    }

    [Fact]
    public async Task A_save_lost_to_an_abandon_is_not_recorded_as_a_walk_fault_and_the_next_tick_ends_the_update_naming_the_stage()
    {
        var rig = new Rig();
        var intent = await rig.Harness.StageManualAsync(OneWorkload());
        var abandoned = await LoseTheSaveToAnAbandonAsync(rig, intent);

        abandoned.WalkFault.Should().BeNull("the walk lost its save to the abandon; the walk itself did not fail");
        abandoned.WalkFaultSinceUtc.Should().BeNull();
        intent.WalkFault.Should().BeNull();

        rig.Time.FireLatest();
        await rig.Time.NextTimerAsync(2);

        abandoned.Error.Should().Contain($"Stage '{ScriptedHostStage.StageId}' was still working and may have taken effect");
    }

    [Fact]
    public async Task A_walk_that_moved_the_row_to_rolling_back_and_lost_its_save_is_not_recorded_as_a_walk_fault()
    {
        var rig = new Rig();
        var intent = await rig.Harness.StageManualAsync(OneWorkload());
        rig.Harness.HostStage.KeepWorking();
        rig.Harness.HostStage.OnCall = () =>
        {
            rig.Harness.HostStage.OnCall = null;
            intent.Fail("a cohort peer vetoed the update", rig.Harness.Clock.UtcNow);
            rig.Harness.Repo.FailUpdate = _ => new InvalidOperationException("the row was written by someone else");
        };

        await rig.StartAsync();
        await rig.Time.NextTimerAsync(1);

        intent.Phase.Should().Be(WorkloadUpdatePhase.RollingBack);
        intent.WalkFault.Should().BeNull("the row stands in rolling back, a phase the walk did not find it in");
        intent.WalkFaultSinceUtc.Should().BeNull();
    }

    [Fact]
    public async Task After_a_save_lost_to_an_abandon_the_next_tick_ends_the_update_as_rollback_failed()
    {
        var rig = new Rig();
        var intent = await rig.Harness.StageManualAsync(OneWorkload());
        var abandoned = await LoseTheSaveToAnAbandonAsync(rig, intent);
        var callsBefore = rig.Harness.HostStage.Calls.Count;

        rig.Time.FireLatest();
        await rig.Time.NextTimerAsync(2);

        abandoned.Outcome.Should().Be(WorkloadUpdateOutcome.RollbackFailed);
        abandoned.Direction.Should().Be(WorkloadUpdateDirection.Forward, "no rollback ran");
        abandoned.Error.Should().Contain("The update was abandoned").And.Contain("nothing was undone");
        rig.Harness.HostStage.Calls.Count.Should().Be(callsBefore, "the walk ends the update instead of calling the stage again");
        rig.Harness.HostStage.Downs.Should().Be(0);
    }

    private static async Task<WorkloadUpdateIntent> LoseTheSaveToAnAbandonAsync(Rig rig, WorkloadUpdateIntent intent)
    {
        var now = rig.Harness.Clock.UtcNow;
        var abandoned = WorkloadUpdateIntent.Create(intent.Id, intent.GroupInstanceId, "2.5.0", "2.6.0", now);
        abandoned.AdvanceTo(WorkloadUpdatePhase.ApplyingSidecar, now);
        abandoned.AdvanceTo(WorkloadUpdatePhase.HealthGate, now);
        abandoned.AdvanceTo(WorkloadUpdatePhase.SwappingModule, now);
        string[] entered =
        [
            WorkloadUpdateStages.Params, WorkloadUpdateStages.Sidecar, WorkloadUpdateStages.OperatorRedeploy,
            WorkloadUpdateStages.HealthGate, WorkloadUpdateStages.CommitVersion, ScriptedHostStage.StageId,
        ];
        abandoned.RecordStageJournal(new PipelineJournal(
            [.. entered.Select(id => new StageEntry(id, now, PipelineDirection.Forward,
                id == ScriptedHostStage.StageId ? StageEntryState.Working : StageEntryState.Done, null))],
            PipelineDirection.Forward, PipelineState.Running));
        abandoned.Fail("abandoned by the operator", now);
        rig.Harness.HostStage.KeepWorking();
        rig.Harness.HostStage.OnCall = () =>
        {
            rig.Harness.HostStage.OnCall = null;
            rig.Harness.Repo.FailUpdate = _ => new InvalidOperationException("the row was written by someone else");
            rig.Intents.ReadInstead = abandoned;
        };

        await rig.StartAsync();
        await rig.Time.NextTimerAsync(1);
        rig.Harness.Repo.FailUpdate = null;
        return abandoned;
    }

    [Fact]
    public async Task A_stage_answer_clears_the_walk_fault()
    {
        var rig = new Rig();
        var intent = await rig.Harness.StageManualAsync(OneWorkload());
        rig.Harness.HostStage.OnCall = () => throw new InvalidOperationException("host roll boom");

        await rig.StartAsync();
        await rig.Time.NextTimerAsync(1);
        intent.WalkFault.Should().NotBeNull();

        rig.Harness.HostStage.OnCall = null;
        rig.Harness.HostStage.KeepWorking();
        rig.Time.FireLatest();
        await rig.Time.NextTimerAsync(2);

        intent.IsTerminal.Should().BeFalse();
        intent.WalkFault.Should().BeNull("the stage answered, so the walk is moving again");
        intent.WalkFaultSinceUtc.Should().BeNull();
    }

    [Fact]
    public async Task A_plan_that_cannot_be_built_is_recorded_as_the_walk_fault()
    {
        var rig = new Rig();
        var intent = await rig.Harness.StageManualAsync(OneWorkload());
        rig.Harness.Planner.Override = () => throw new InvalidOperationException("manifest unavailable");

        await rig.StartAsync();

        (await rig.Time.NextTimerAsync(1)).Should().Be(Options.WorkingTickInterval);
        intent.Phase.Should().Be(WorkloadUpdatePhase.StagingParams);
        intent.WalkFault.Should().Be("plan unavailable: manifest unavailable");
        intent.WalkFaultSinceUtc.Should().Be(rig.Harness.Clock.UtcNow);
    }

    private sealed class Rig
    {
        public Rig()
        {
            Harness = new OrchestratorHarness(OneWorkload());
            Intents = new ScriptedIntents(Harness.Repo);
            var services = new ServiceCollection();
            services.AddScoped<IWorkloadUpdateIntentRepository>(_ => Intents);
            services.AddScoped(_ => Harness.Orchestrator);
            services.AddSingleton<IWorkloadClock>(Harness.Clock);
            Unit = new WorkloadUpdateWalkUnit(
                services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(),
                Bus,
                Gate,
                Microsoft.Extensions.Options.Options.Create(Options),
                NullLogger<WorkloadUpdateWalkUnit>.Instance,
                Time);
        }

        public OrchestratorHarness Harness { get; }

        public ScriptedIntents Intents { get; }

        public FakeWorkloadClusterBus Bus { get; } = new();

        public WorkloadGroupGate Gate { get; } = new();

        public ManualTimeProvider Time { get; } = new();

        public WorkloadUpdateWalkUnit Unit { get; }

        public Task StartAsync() => Unit.StartAsync(CancellationToken.None);
    }

    private sealed class ScriptedIntents(InMemoryIntentRepository inner) : IWorkloadUpdateIntentRepository
    {
        public Guid? ThrowOnRead { get; set; }

        public bool ThrowOnList { get; set; }

        public Action<WorkloadUpdateIntent>? OnRead { get; set; }

        public WorkloadUpdateIntent? ReadInstead { get; set; }

        public Task<IReadOnlyList<WorkloadUpdateIntent>> GetOpenIntentsAsync(CancellationToken cancellationToken = default)
            => ThrowOnList ? throw new InvalidOperationException("list boom") : inner.GetOpenIntentsAsync(cancellationToken);

        public async Task<WorkloadUpdateIntent?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
        {
            if (id == ThrowOnRead)
            {
                throw new InvalidOperationException("read boom");
            }

            if (ReadInstead is { } instead && instead.Id == id)
            {
                return instead;
            }

            var intent = await inner.GetByIdAsync(id, cancellationToken);
            if (intent is not null)
            {
                OnRead?.Invoke(intent);
            }

            return intent;
        }

        public Task<WorkloadUpdateIntent?> GetActiveForInstanceAsync(Guid pluginInstanceId, CancellationToken cancellationToken = default)
            => inner.GetActiveForInstanceAsync(pluginInstanceId, cancellationToken);

        public Task<Guid?> GetActiveIntentIdAsync(Guid pluginInstanceId, CancellationToken cancellationToken = default)
            => inner.GetActiveIntentIdAsync(pluginInstanceId, cancellationToken);

        public Task<WorkloadUpdateIntent?> GetLatestForInstanceAsync(Guid pluginInstanceId, CancellationToken cancellationToken = default)
            => inner.GetLatestForInstanceAsync(pluginInstanceId, cancellationToken);

        public Task<IReadOnlyList<WorkloadUpdateIntent>> GetRecentForInstanceAsync(Guid pluginInstanceId, int limit, CancellationToken cancellationToken = default)
            => inner.GetRecentForInstanceAsync(pluginInstanceId, limit, cancellationToken);

        public Task AddAsync(WorkloadUpdateIntent intent, CancellationToken cancellationToken = default)
            => inner.AddAsync(intent, cancellationToken);

        public Task UpdateAsync(WorkloadUpdateIntent intent, CancellationToken cancellationToken = default)
            => inner.UpdateAsync(intent, cancellationToken);
    }
}

internal sealed class ManualTimeProvider : TimeProvider
{
    private readonly object sync = new();
    private readonly List<(TimeSpan Due, ManualTimer Timer)> timers = new();
    private readonly List<TaskCompletionSource> waiters = new();

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new ManualTimer(callback, state);
        lock (sync)
        {
            timers.Add((dueTime, timer));
            foreach (var waiter in waiters)
            {
                waiter.TrySetResult();
            }

            waiters.Clear();
        }

        return timer;
    }

    public async Task<TimeSpan> NextTimerAsync(int ordinal)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (true)
        {
            Task changed;
            lock (sync)
            {
                if (timers.Count >= ordinal)
                {
                    return timers[ordinal - 1].Due;
                }

                var waiter = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                waiters.Add(waiter);
                changed = waiter.Task;
            }

            var remaining = deadline - DateTime.UtcNow;
            if (remaining <= TimeSpan.Zero || await Task.WhenAny(changed, Task.Delay(remaining)) != changed)
            {
                throw new TimeoutException($"timer {ordinal} was never created (have {timers.Count}).");
            }
        }
    }

    public async Task NoTimerWithinAsync(int ordinal, TimeSpan window)
    {
        await Task.Delay(window);
        lock (sync)
        {
            timers.Count.Should().BeLessThan(ordinal, "no tick should have run");
        }
    }

    public void FireLatest()
    {
        ManualTimer timer;
        lock (sync)
        {
            timer = timers[^1].Timer;
        }

        timer.Fire();
    }

    private sealed class ManualTimer(TimerCallback callback, object? state) : ITimer
    {
        private int disposed;

        public void Fire()
        {
            if (Volatile.Read(ref disposed) == 0)
            {
                callback(state);
            }
        }

        public bool Change(TimeSpan dueTime, TimeSpan period) => true;

        public void Dispose() => Interlocked.Exchange(ref disposed, 1);

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
