using NSubstitute;
using Reeve.Workloads.Abstractions.Providers;
using System.Collections.Concurrent;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Reeve.Workloads.Abstractions;
using Reeve.Workloads.Abstractions.Contracts;
using Reeve.Workloads.Abstractions.Domain.Entities;
using Reeve.Workloads.Abstractions.Update;
using Reeve.Workloads.Core;
using Reeve.Workloads.Providers.Kubernetes;
using Xunit;
using Reeve.Workloads.Providers.Manual;

namespace Reeve.Workloads.Core.Tests;

public sealed class WorkloadRepairsScannerTests
{
    private static readonly DateTimeOffset T0 = new(2026, 7, 6, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Degraded_for_14_minutes_raises_no_issue()
    {
        var h = new Harness();
        var i = h.Seed(WorkloadInstanceState.Degraded, transitionedAt: T0);
        h.Clock.Set(T0.AddMinutes(14));

        await h.Scanner.ScanAsync(CancellationToken.None);

        h.RegistryFor(i.GroupInstanceId).RaisedCodes.Should().BeEmpty(
            "a bad state escalates only AFTER 15 min — 14 min is still within grace");
    }

    [Fact]
    public async Task Degraded_for_16_minutes_raises_a_stuck_warning()
    {
        var h = new Harness();
        var i = h.Seed(WorkloadInstanceState.Degraded, transitionedAt: T0, stateMessage: "bluetoothd absent");
        h.Clock.Set(T0.AddMinutes(16));

        await h.Scanner.ScanAsync(CancellationToken.None);

        var spy = h.RegistryFor(i.GroupInstanceId);
        var raise = spy.Raises.Should().ContainSingle().Subject;
        raise.Code.Should().Be(WorkloadRepairsScanner.StuckCode(i.WorkloadId));
        raise.Severity.Should().Be(WorkloadDiagnosticSeverity.Warning);
        raise.Message.Should().Contain("Degraded").And.Contain("bluetoothd absent");
    }

    [Fact]
    public async Task Failed_for_16_minutes_raises_a_stuck_error()
    {
        var h = new Harness();
        var i = h.Seed(WorkloadInstanceState.Failed, transitionedAt: T0);
        h.Clock.Set(T0.AddMinutes(16));

        await h.Scanner.ScanAsync(CancellationToken.None);

        var spy = h.RegistryFor(i.GroupInstanceId);
        spy.RaisedCodes.Should().ContainSingle().Which.Should().Be(WorkloadRepairsScanner.StuckCode(i.WorkloadId));
        spy.Raises[0].Severity.Should().Be(WorkloadDiagnosticSeverity.Error,
            "a lasting Failed must reach the default Error delivery threshold; Degraded and Unknown stay Warning");
        spy.Raises[0].Message.Should().Contain("Failed");
    }

    [Theory]
    [InlineData(WorkloadStateReason.SpecRejected)]
    [InlineData(WorkloadStateReason.DesiredSpecComputeFailed)]
    public async Task Failed_for_another_reason_than_lost_health_also_raises_a_stuck_error(WorkloadStateReason reason)
    {
        var h = new Harness();
        var i = h.Seed(WorkloadInstanceState.Degraded, transitionedAt: T0);
        i.TransitionTo(WorkloadInstanceState.Failed, reason, "refused", T0);
        h.Clock.Set(T0.AddMinutes(16));

        await h.Scanner.ScanAsync(CancellationToken.None);

        h.RegistryFor(i.GroupInstanceId).Raises.Should().ContainSingle()
            .Which.Severity.Should().Be(WorkloadDiagnosticSeverity.Error);
    }

    [Fact]
    public async Task Failed_that_recovers_to_Degraded_re_raises_at_Warning()
    {
        var h = new Harness();
        var i = h.Seed(WorkloadInstanceState.Failed, transitionedAt: T0);
        h.Clock.Set(T0.AddMinutes(16));
        await h.Scanner.ScanAsync(CancellationToken.None);

        i.TransitionTo(WorkloadInstanceState.Degraded, WorkloadStateReason.HealthLost, null, h.Clock.UtcNow);
        h.Clock.Set(T0.AddMinutes(18));
        await h.Scanner.ScanAsync(CancellationToken.None);

        h.RegistryFor(i.GroupInstanceId).Raises.Select(r => r.Severity).Should().Equal(
            new[] { WorkloadDiagnosticSeverity.Error, WorkloadDiagnosticSeverity.Warning },
            "the severity follows the state while the instance stays in the bad set");
    }

    [Fact]
    public async Task An_issue_a_stale_scan_raised_for_a_deleted_instance_is_cleared_on_the_next_pass()
    {
        var h = new Harness();
        var i = h.Seed(WorkloadInstanceState.Failed, transitionedAt: T0);
        h.Clock.Set(T0.AddMinutes(16));
        await h.Scanner.ScanAsync(CancellationToken.None);
        var spy = h.RegistryFor(i.GroupInstanceId);
        spy.ClearedCodes.Should().NotContain(WorkloadRepairsScanner.StuckCode(i.WorkloadId));

        h.Repository.Remove(i);
        await h.Scanner.ScanAsync(CancellationToken.None);

        spy.ClearedCodes.Should().Contain(WorkloadRepairsScanner.StuckCode(i.WorkloadId));
    }

    [Fact]
    public async Task Unknown_for_16_minutes_raises_a_stuck_warning()
    {
        var h = new Harness();
        var i = h.Seed(WorkloadInstanceState.Unknown, transitionedAt: T0);
        h.Clock.Set(T0.AddMinutes(16));

        await h.Scanner.ScanAsync(CancellationToken.None);

        var raise = h.RegistryFor(i.GroupInstanceId).Raises.Should().ContainSingle().Subject;
        raise.Message.Should().Contain("Unknown");
        raise.Severity.Should().Be(WorkloadDiagnosticSeverity.Warning, "Unknown is not proof the runtime is down");
    }

    [Fact]
    public async Task A_teardown_that_keeps_failing_raises_a_stuck_warning_with_its_reason()
    {
        var h = new Harness();
        var i = h.Seed(WorkloadInstanceState.Terminating, transitionedAt: T0, stateMessage: "Teardown failed: socket missing");
        h.Clock.Set(T0.AddMinutes(16));

        await h.Scanner.ScanAsync(CancellationToken.None);

        var raise = h.RegistryFor(i.GroupInstanceId).Raises.Should().ContainSingle().Subject;
        raise.Message.Should().Contain("Terminating").And.Contain("socket missing");
        raise.FixHint.Should().Contain("retries the removal");
    }

    [Fact]
    public async Task Healthy_instance_never_raises_a_stuck_issue()
    {
        var h = new Harness();
        var i = h.Seed(WorkloadInstanceState.Healthy, transitionedAt: T0);
        h.Clock.Set(T0.AddHours(3));

        await h.Scanner.ScanAsync(CancellationToken.None);

        h.RegistryFor(i.GroupInstanceId).RaisedCodes.Should().BeEmpty();
    }

    [Theory]
    [InlineData(WorkloadInstanceState.Deploying)]
    [InlineData(WorkloadInstanceState.Suspended)]
    [InlineData(WorkloadInstanceState.PendingConfiguration)]
    public async Task Non_bad_states_never_raise_a_stuck_issue(WorkloadInstanceState state)
    {
        var h = new Harness();
        var i = h.Seed(state, transitionedAt: T0);
        h.Clock.Set(T0.AddHours(3));

        await h.Scanner.ScanAsync(CancellationToken.None);

        h.RegistryFor(i.GroupInstanceId).RaisedCodes.Should().BeEmpty(
            "only Degraded/Failed/Unknown/Terminating are stuck-escalation states");
    }

    [Fact]
    public async Task A_stalled_deploy_raises_a_stuck_warning_at_once_naming_the_reason()
    {
        var h = new Harness();
        var i = h.Seed(WorkloadInstanceState.Deploying, transitionedAt: T0);
        i.TransitionTo(WorkloadInstanceState.Degraded, WorkloadStateReason.Provisioning,
            "Deploy stalled for over 10 min: 0/1 ready. Pod not ready: Unschedulable: pod has unbound immediate PersistentVolumeClaims.",
            T0.AddMinutes(10));
        h.Clock.Set(T0.AddMinutes(11));

        await h.Scanner.ScanAsync(CancellationToken.None);

        var raise = h.RegistryFor(i.GroupInstanceId).Raises.Should().ContainSingle().Subject;
        raise.Code.Should().Be(WorkloadRepairsScanner.StuckCode(i.WorkloadId));
        raise.Severity.Should().Be(WorkloadDiagnosticSeverity.Warning);
        raise.Message.Should().Contain("unbound immediate PersistentVolumeClaims");
    }

    [Fact]
    public async Task A_stalled_deploy_that_comes_up_clears_its_stuck_warning()
    {
        var h = new Harness();
        var i = h.Seed(WorkloadInstanceState.Deploying, transitionedAt: T0);
        i.TransitionTo(WorkloadInstanceState.Degraded, WorkloadStateReason.Provisioning,
            "Deploy stalled for over 10 min: 0/1 ready.", T0.AddMinutes(10));
        h.Clock.Set(T0.AddMinutes(11));
        await h.Scanner.ScanAsync(CancellationToken.None);
        h.RegistryFor(i.GroupInstanceId).RaisedCodes.Should().Contain(WorkloadRepairsScanner.StuckCode(i.WorkloadId));

        i.TransitionTo(WorkloadInstanceState.Healthy, WorkloadStateReason.HealthObserved, null, T0.AddMinutes(12));
        h.Clock.Set(T0.AddMinutes(13));
        await h.Scanner.ScanAsync(CancellationToken.None);

        h.RegistryFor(i.GroupInstanceId).ClearedCodes.Should().Contain(WorkloadRepairsScanner.StuckCode(i.WorkloadId));
    }

    [Fact]
    public async Task Recovery_from_stuck_to_healthy_clears_the_issue()
    {
        var h = new Harness();
        var i = h.Seed(WorkloadInstanceState.Degraded, transitionedAt: T0);

        h.Clock.Set(T0.AddMinutes(16));
        await h.Scanner.ScanAsync(CancellationToken.None);
        h.RegistryFor(i.GroupInstanceId).RaisedCodes.Should().Contain(WorkloadRepairsScanner.StuckCode(i.WorkloadId));

        i.TransitionTo(WorkloadInstanceState.Healthy, WorkloadStateReason.HealthObserved, null, h.Clock.UtcNow);

        await h.Scanner.ScanAsync(CancellationToken.None);
        h.RegistryFor(i.GroupInstanceId).ClearedCodes.Should().Contain(WorkloadRepairsScanner.StuckCode(i.WorkloadId));
    }

    [Fact]
    public async Task Oscillating_within_the_bad_set_still_escalates_after_15_min_in_the_bad_set()
    {
        var h = new Harness();
        var i = h.Seed(WorkloadInstanceState.Degraded, transitionedAt: T0);

        h.Clock.Set(T0.AddMinutes(6));
        await h.Scanner.ScanAsync(CancellationToken.None);
        h.RegistryFor(i.GroupInstanceId).RaisedCodes.Should().BeEmpty("6 min in the bad set is still within grace");
        i.TransitionTo(WorkloadInstanceState.Failed, WorkloadStateReason.HealthLost, null, h.Clock.UtcNow);

        h.Clock.Set(T0.AddMinutes(12));
        await h.Scanner.ScanAsync(CancellationToken.None);
        i.TransitionTo(WorkloadInstanceState.Degraded, WorkloadStateReason.HealthLost, null, h.Clock.UtcNow);

        h.Clock.Set(T0.AddMinutes(16));
        await h.Scanner.ScanAsync(CancellationToken.None);

        h.RegistryFor(i.GroupInstanceId).RaisedCodes.Should().Contain(WorkloadRepairsScanner.StuckCode(i.WorkloadId),
            "continuously in the bad set for >15 min escalates even while oscillating within it");
    }

    [Fact]
    public async Task Leaving_and_re_entering_the_bad_set_restarts_the_stuck_clock()
    {
        var h = new Harness();
        var i = h.Seed(WorkloadInstanceState.Degraded, transitionedAt: T0);

        h.Clock.Set(T0.AddMinutes(10));
        await h.Scanner.ScanAsync(CancellationToken.None);
        i.TransitionTo(WorkloadInstanceState.Healthy, WorkloadStateReason.HealthObserved, null, h.Clock.UtcNow);
        await h.Scanner.ScanAsync(CancellationToken.None);

        i.TransitionTo(WorkloadInstanceState.Degraded, WorkloadStateReason.HealthLost, null, h.Clock.UtcNow);
        h.Clock.Set(T0.AddMinutes(12));
        i.TransitionTo(WorkloadInstanceState.Degraded, WorkloadStateReason.HealthLost, null, h.Clock.UtcNow);

        h.Clock.Set(T0.AddMinutes(20));
        await h.Scanner.ScanAsync(CancellationToken.None);

        h.RegistryFor(i.GroupInstanceId).RaisedCodes.Should().NotContain(WorkloadRepairsScanner.StuckCode(i.WorkloadId),
            "a fresh regression after a real recovery starts a new grace window");
    }

    [Fact]
    public async Task Rising_restart_count_raises_an_immediate_crash_loop_warning()
    {
        var h = new Harness();
        var i = h.Seed(WorkloadInstanceState.Healthy, transitionedAt: T0, restartCount: 2);

        await h.Scanner.ScanAsync(CancellationToken.None);
        h.RegistryFor(i.GroupInstanceId).RaisedCodes.Should().NotContain(WorkloadRepairsScanner.CrashLoopCode(i.WorkloadId));

        i.SetRestartCount(5, h.Clock.UtcNow);

        await h.Scanner.ScanAsync(CancellationToken.None);

        var spy = h.RegistryFor(i.GroupInstanceId);
        var raise = spy.Raises.Should().ContainSingle(r => r.Code == WorkloadRepairsScanner.CrashLoopCode(i.WorkloadId)).Subject;
        raise.Severity.Should().Be(WorkloadDiagnosticSeverity.Warning);
        raise.Message.Should().Contain("crash-looping",
            "the crash-loop message names the STABLE condition — the changing count lives in metadata (minor)");
        raise.Message.Should().NotContain("rose from", "the changing count must not appear in the message");
        raise.Message.Should().NotContain("restarts)", "the changing restart count must live in metadata, not the message");
    }

    [Fact]
    public async Task Restart_count_below_threshold_does_not_raise_crash_loop()
    {
        var h = new Harness();
        var i = h.Seed(WorkloadInstanceState.Healthy, transitionedAt: T0, restartCount: 0);

        await h.Scanner.ScanAsync(CancellationToken.None);
        i.SetRestartCount(1, h.Clock.UtcNow);
        h.Clock.Set(T0.AddSeconds(120));
        await h.Scanner.ScanAsync(CancellationToken.None);

        h.RegistryFor(i.GroupInstanceId).RaisedCodes.Should().NotContain(WorkloadRepairsScanner.CrashLoopCode(i.WorkloadId));
    }

    [Fact]
    public async Task Slow_backoff_loop_of_one_restart_per_pass_escalates_over_the_sliding_window()
    {
        var h = new Harness();
        var i = h.Seed(WorkloadInstanceState.Healthy, transitionedAt: T0, restartCount: 0);

        await h.Scanner.ScanAsync(CancellationToken.None);
        h.RegistryFor(i.GroupInstanceId).RaisedCodes.Should().NotContain(WorkloadRepairsScanner.CrashLoopCode(i.WorkloadId));

        i.SetRestartCount(1, h.Clock.UtcNow);
        h.Clock.Set(T0.AddMinutes(5));
        await h.Scanner.ScanAsync(CancellationToken.None);

        i.SetRestartCount(2, h.Clock.UtcNow);
        h.Clock.Set(T0.AddMinutes(10));
        await h.Scanner.ScanAsync(CancellationToken.None);

        i.SetRestartCount(3, h.Clock.UtcNow);
        h.Clock.Set(T0.AddMinutes(15));
        await h.Scanner.ScanAsync(CancellationToken.None);

        var spy = h.RegistryFor(i.GroupInstanceId);
        spy.RaisedCodes.Should().Contain(WorkloadRepairsScanner.CrashLoopCode(i.WorkloadId),
            "a slow backoff loop must still escalate over the sliding window");
        spy.Raises.Should().Contain(r => r.Code == WorkloadRepairsScanner.CrashLoopCode(i.WorkloadId) && r.Severity == WorkloadDiagnosticSeverity.Warning);
    }

    [Fact]
    public async Task A_single_stray_restart_that_never_recurs_re_anchors_and_does_not_raise()
    {
        var h = new Harness();
        var i = h.Seed(WorkloadInstanceState.Healthy, transitionedAt: T0, restartCount: 0);

        await h.Scanner.ScanAsync(CancellationToken.None);
        i.SetRestartCount(1, h.Clock.UtcNow);
        h.Clock.Set(T0.AddMinutes(2));
        await h.Scanner.ScanAsync(CancellationToken.None);

        h.Clock.Set(T0.AddMinutes(20));
        await h.Scanner.ScanAsync(CancellationToken.None);

        i.SetRestartCount(3, h.Clock.UtcNow);
        h.Clock.Set(T0.AddMinutes(22));
        await h.Scanner.ScanAsync(CancellationToken.None);

        h.RegistryFor(i.GroupInstanceId).RaisedCodes.Should().NotContain(WorkloadRepairsScanner.CrashLoopCode(i.WorkloadId),
            "a re-anchored baseline must not carry a stale stray restart into a later window");
    }

    [Fact]
    public async Task Crash_loop_clears_and_re_anchors_once_the_window_elapses_without_further_restarts()
    {
        var h = new Harness();
        var i = h.Seed(WorkloadInstanceState.Healthy, transitionedAt: T0, restartCount: 1);

        await h.Scanner.ScanAsync(CancellationToken.None);
        i.SetRestartCount(5, h.Clock.UtcNow);
        h.Clock.Set(T0.AddMinutes(2));
        await h.Scanner.ScanAsync(CancellationToken.None);
        h.RegistryFor(i.GroupInstanceId).RaisedCodes.Should().Contain(WorkloadRepairsScanner.CrashLoopCode(i.WorkloadId));

        h.Clock.Set(T0.AddMinutes(20));
        await h.Scanner.ScanAsync(CancellationToken.None);
        h.RegistryFor(i.GroupInstanceId).ClearedCodes.Should().Contain(WorkloadRepairsScanner.CrashLoopCode(i.WorkloadId));
    }

    [Fact]
    public async Task Crash_loop_clears_when_the_provider_resets_its_restart_counter_on_a_fresh_deploy()
    {
        var h = new Harness();
        var i = h.Seed(WorkloadInstanceState.Healthy, transitionedAt: T0, restartCount: 1);

        await h.Scanner.ScanAsync(CancellationToken.None);
        i.SetRestartCount(5, h.Clock.UtcNow);
        h.Clock.Set(T0.AddMinutes(2));
        await h.Scanner.ScanAsync(CancellationToken.None);
        h.RegistryFor(i.GroupInstanceId).RaisedCodes.Should().Contain(WorkloadRepairsScanner.CrashLoopCode(i.WorkloadId));

        i.SetRestartCount(0, h.Clock.UtcNow);
        h.Clock.Set(T0.AddMinutes(3));
        await h.Scanner.ScanAsync(CancellationToken.None);
        h.RegistryFor(i.GroupInstanceId).ClearedCodes.Should().Contain(WorkloadRepairsScanner.CrashLoopCode(i.WorkloadId));
    }

    [Fact]
    public async Task Registry_factory_cache_is_pruned_for_plugin_instances_that_no_longer_exist()
    {
        var h = new Harness();
        var pidA = Guid.NewGuid();
        var pidB = Guid.NewGuid();
        var a = h.Seed(WorkloadInstanceState.Degraded, transitionedAt: T0, pluginInstanceId: pidA);
        var b = h.Seed(WorkloadInstanceState.Degraded, transitionedAt: T0, pluginInstanceId: pidB);
        h.Clock.Set(T0.AddMinutes(16));

        await h.Scanner.ScanAsync(CancellationToken.None);
        h.Registries.LivePluginInstanceIds.Should().Contain(new[] { pidA, pidB });

        h.Repository.Remove(b);

        await h.Scanner.ScanAsync(CancellationToken.None);
        h.Registries.LivePluginInstanceIds.Should().Contain(pidA);
        h.Registries.LivePluginInstanceIds.Should().NotContain(pidB,
            "the per-plugin-instance registry cache must be pruned alongside the restart-tracking prune");
    }

    [Fact]
    public async Task Stable_stuck_state_raises_and_pushes_exactly_once_over_many_passes()
    {
        var h = new Harness();
        var i = h.Seed(WorkloadInstanceState.Failed, transitionedAt: T0, stateMessage: "controller offline");

        for (var minute = 16; minute <= 76; minute += 2)
        {
            h.Clock.Set(T0.AddMinutes(minute));
            await h.Scanner.ScanAsync(CancellationToken.None);
        }

        var spy = h.RegistryFor(i.GroupInstanceId);
        spy.Raises.Should().ContainSingle(r => r.Code == WorkloadRepairsScanner.StuckCode(i.WorkloadId),
            "a stable stuck condition must raise/push exactly once, not once per ~2-min pass");
        spy.RaisedCodes.Should().OnlyContain(c => c == WorkloadRepairsScanner.StuckCode(i.WorkloadId));
    }

    [Fact]
    public async Task Stuck_message_prints_the_stored_name_of_a_renamed_reason()
    {
        var h = new Harness();
        var i = h.Seed(WorkloadInstanceState.Degraded, transitionedAt: T0);
        i.TransitionTo(i.State, WorkloadStateReason.RunningVersionBehind, null, T0);
        h.Clock.Set(T0.AddMinutes(16));

        await h.Scanner.ScanAsync(CancellationToken.None);

        h.RegistryFor(i.GroupInstanceId).Raises.Single().Message.Should().EndWith("(reason: RunningVersionBehind).");
    }

    [Fact]
    public async Task Stuck_message_is_stable_across_passes_and_omits_the_per_pass_elapsed_minutes()
    {
        var h = new Harness();
        var i = h.Seed(WorkloadInstanceState.Degraded, transitionedAt: T0, stateMessage: "bluetoothd absent");

        h.Clock.Set(T0.AddMinutes(16));
        await h.Scanner.ScanAsync(CancellationToken.None);
        var firstMessage = h.RegistryFor(i.GroupInstanceId).Raises.Single().Message;

        h.Clock.Set(T0.AddMinutes(46));
        await h.Scanner.ScanAsync(CancellationToken.None);

        firstMessage.Should().Contain("Degraded").And.Contain("bluetoothd absent");
        firstMessage.Should().NotMatchRegex(@"~\d+\s*min",
            "the per-pass elapsed minutes must live in metadata, not the message");
    }

    [Fact]
    public async Task Severity_or_state_change_re_raises_after_a_stable_stuck_condition()
    {
        var h = new Harness();
        var i = h.Seed(WorkloadInstanceState.Degraded, transitionedAt: T0);

        h.Clock.Set(T0.AddMinutes(16));
        await h.Scanner.ScanAsync(CancellationToken.None);
        h.Clock.Set(T0.AddMinutes(18));
        await h.Scanner.ScanAsync(CancellationToken.None);

        var spy = h.RegistryFor(i.GroupInstanceId);
        spy.Raises.Count(r => r.Code == WorkloadRepairsScanner.StuckCode(i.WorkloadId))
            .Should().Be(1, "an unchanged stuck condition does not re-raise");

        i.TransitionTo(WorkloadInstanceState.Failed, WorkloadStateReason.HealthLost, null, h.Clock.UtcNow);
        h.Clock.Set(T0.AddMinutes(20));
        await h.Scanner.ScanAsync(CancellationToken.None);

        spy.Raises.Count(r => r.Code == WorkloadRepairsScanner.StuckCode(i.WorkloadId))
            .Should().Be(2, "a state change within the bad set is a material change and must re-raise");
        var stuck = spy.Raises.Where(r => r.Code == WorkloadRepairsScanner.StuckCode(i.WorkloadId)).ToList();
        stuck.Last().Message.Should().Contain("Failed");
        stuck.Select(r => r.Severity).Should().Equal(
            new[] { WorkloadDiagnosticSeverity.Warning, WorkloadDiagnosticSeverity.Error },
            "Degraded raises a Warning and the move to Failed re-raises once at Error");

        h.Clock.Set(T0.AddMinutes(22));
        await h.Scanner.ScanAsync(CancellationToken.None);

        spy.Raises.Count(r => r.Code == WorkloadRepairsScanner.StuckCode(i.WorkloadId))
            .Should().Be(2, "a steady Failed does not re-push its Error");
    }

    [Fact]
    public async Task Clearing_then_re_entering_the_bad_set_re_raises()
    {
        var h = new Harness();
        var i = h.Seed(WorkloadInstanceState.Degraded, transitionedAt: T0, stateMessage: "flapping");

        h.Clock.Set(T0.AddMinutes(16));
        await h.Scanner.ScanAsync(CancellationToken.None);

        i.TransitionTo(WorkloadInstanceState.Healthy, WorkloadStateReason.HealthObserved, null, h.Clock.UtcNow);
        h.Clock.Set(T0.AddMinutes(18));
        await h.Scanner.ScanAsync(CancellationToken.None);

        i.TransitionTo(WorkloadInstanceState.Degraded, WorkloadStateReason.HealthLost, "flapping", h.Clock.UtcNow);
        h.Clock.Set(T0.AddMinutes(40));
        await h.Scanner.ScanAsync(CancellationToken.None);

        h.RegistryFor(i.GroupInstanceId).Raises.Count(r => r.Code == WorkloadRepairsScanner.StuckCode(i.WorkloadId))
            .Should().Be(2, "a clear then re-enter must raise again even for an identical condition");
    }

    [Fact]
    public async Task An_active_crash_loop_raises_exactly_ONCE_across_climbing_restarts_within_the_window()
    {
        var h = new Harness();
        var i = h.Seed(WorkloadInstanceState.Healthy, transitionedAt: T0, restartCount: 0);

        await h.Scanner.ScanAsync(CancellationToken.None);
        i.SetRestartCount(4, h.Clock.UtcNow);
        h.Clock.Set(T0.AddMinutes(2));
        await h.Scanner.ScanAsync(CancellationToken.None);

        h.Clock.Set(T0.AddMinutes(4));
        await h.Scanner.ScanAsync(CancellationToken.None);

        i.SetRestartCount(5, h.Clock.UtcNow);
        h.Clock.Set(T0.AddMinutes(6));
        await h.Scanner.ScanAsync(CancellationToken.None);
        i.SetRestartCount(6, h.Clock.UtcNow);
        h.Clock.Set(T0.AddMinutes(8));
        await h.Scanner.ScanAsync(CancellationToken.None);

        var spy = h.RegistryFor(i.GroupInstanceId);
        spy.Raises.Count(r => r.Code == WorkloadRepairsScanner.CrashLoopCode(i.WorkloadId))
            .Should().Be(1, "an active crash loop raises once per material step — NOT once per restart (minor)");
    }

    [Fact]
    public async Task Two_workloads_of_one_plugin_get_disjoint_stable_codes()
    {
        var h = new Harness();
        var pluginInstanceId = Guid.NewGuid();
        var a = h.Seed(WorkloadInstanceState.Degraded, transitionedAt: T0, pluginInstanceId: pluginInstanceId, workloadId: "zwave-controller");
        var b = h.Seed(WorkloadInstanceState.Failed, transitionedAt: T0, pluginInstanceId: pluginInstanceId, workloadId: "zwave-secondary");
        h.Clock.Set(T0.AddMinutes(16));

        await h.Scanner.ScanAsync(CancellationToken.None);

        var spy = h.RegistryFor(pluginInstanceId);
        spy.RaisedCodes.Should().Contain(WorkloadRepairsScanner.StuckCode("zwave-controller"))
            .And.Contain(WorkloadRepairsScanner.StuckCode("zwave-secondary"));
        WorkloadRepairsScanner.StuckCode("zwave-controller").Should().NotBe(WorkloadRepairsScanner.StuckCode("zwave-secondary"),
            "the code must disambiguate multiple workloads of one plugin so the (PluginInstanceId, Code) dedup does not collide");
    }

    [Fact]
    public async Task No_park_and_no_rollback_raises_no_parked_issue()
    {
        var h = new Harness();
        var i = h.Seed(WorkloadInstanceState.Healthy, transitionedAt: T0);
        h.Clock.Set(T0.AddDays(30));

        await h.Scanner.ScanAsync(CancellationToken.None);

        h.RegistryFor(i.GroupInstanceId).RaisedCodes
            .Should().NotContain(c => c.Contains("parked", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Custom_stuck_threshold_is_honoured()
    {
        var options = new WorkloadRepairsOptions { StuckWarningAfterSeconds = 60 };
        var h = new Harness(options);
        var i = h.Seed(WorkloadInstanceState.Degraded, transitionedAt: T0);
        h.Clock.Set(T0.AddSeconds(90));

        await h.Scanner.ScanAsync(CancellationToken.None);

        h.RegistryFor(i.GroupInstanceId).RaisedCodes.Should().Contain(WorkloadRepairsScanner.StuckCode(i.WorkloadId));
    }

    [Fact]
    public async Task Manual_version_skew_for_11_minutes_raises_a_warning()
    {
        var h = new Harness();
        var i = h.Seed(WorkloadInstanceState.VersionSkew, transitionedAt: T0, provider: ManualProvider.Id,
            stateMessage: "module 2.6.0 ahead of sidecar 2.5.0");
        h.Clock.Set(T0.AddMinutes(11));

        await h.Scanner.ScanAsync(CancellationToken.None);

        var spy = h.RegistryFor(i.GroupInstanceId);
        var raise = spy.Raises.Should().ContainSingle(r => r.Code == WorkloadRepairsScanner.VersionSkewCode(i.WorkloadId)).Subject;
        raise.Severity.Should().Be(WorkloadDiagnosticSeverity.Warning);
        raise.Message.Should().Contain("skew");
    }

    [Fact]
    public async Task A_third_party_provider_that_materialises_nothing_gets_the_same_skew_warning()
    {
        var handApplied = WorkloadProviderId.Parse("acme.gitops");
        var provider = Substitute.For<IWorkloadProvider>();
        provider.Descriptor.Returns(new WorkloadProviderDescriptor { Id = handApplied, Materializes = false });
        var h = new Harness(providers: new Reeve.Workloads.Core.Providers.WorkloadProviderRegistry([provider]));
        var i = h.Seed(WorkloadInstanceState.VersionSkew, transitionedAt: T0, provider: handApplied);
        h.Clock.Set(T0.AddMinutes(11));

        await h.Scanner.ScanAsync(CancellationToken.None);

        h.RegistryFor(i.GroupInstanceId).RaisedCodes.Should().Contain(WorkloadRepairsScanner.VersionSkewCode(i.WorkloadId));
    }

    [Fact]
    public async Task Manual_version_skew_for_9_minutes_raises_no_issue()
    {
        var h = new Harness();
        var i = h.Seed(WorkloadInstanceState.VersionSkew, transitionedAt: T0, provider: ManualProvider.Id);
        h.Clock.Set(T0.AddMinutes(9));

        await h.Scanner.ScanAsync(CancellationToken.None);

        h.RegistryFor(i.GroupInstanceId).RaisedCodes
            .Should().NotContain(WorkloadRepairsScanner.VersionSkewCode(i.WorkloadId),
                "the manual skew escalates only AFTER 10 min — 9 min is still within grace");
    }

    [Fact]
    public async Task A_managed_provider_version_skew_does_not_raise_the_manual_skew_issue()
    {
        var h = new Harness();
        var i = h.Seed(WorkloadInstanceState.VersionSkew, transitionedAt: T0, provider: KubernetesProvider.Id);
        h.Clock.Set(T0.AddHours(1));

        await h.Scanner.ScanAsync(CancellationToken.None);

        h.RegistryFor(i.GroupInstanceId).RaisedCodes
            .Should().NotContain(WorkloadRepairsScanner.VersionSkewCode(i.WorkloadId),
                "the manual-skew rule targets manual-provider instances only");
    }

    [Fact]
    public async Task Manual_version_skew_raises_exactly_once_across_stable_passes()
    {
        var h = new Harness();
        var i = h.Seed(WorkloadInstanceState.VersionSkew, transitionedAt: T0, provider: ManualProvider.Id);
        var code = WorkloadRepairsScanner.VersionSkewCode(i.WorkloadId);

        for (var minute = 11; minute <= 60; minute += 2)
        {
            h.Clock.Set(T0.AddMinutes(minute));
            await h.Scanner.ScanAsync(CancellationToken.None);
        }

        h.RegistryFor(i.GroupInstanceId).Raises.Count(r => r.Code == code)
            .Should().Be(1, "a stable skew warning raises exactly once, not once per pass (de-spam)");
    }

    [Fact]
    public async Task Recovery_from_manual_version_skew_clears_the_issue()
    {
        var h = new Harness();
        var i = h.Seed(WorkloadInstanceState.VersionSkew, transitionedAt: T0, provider: ManualProvider.Id);
        var code = WorkloadRepairsScanner.VersionSkewCode(i.WorkloadId);

        h.Clock.Set(T0.AddMinutes(11));
        await h.Scanner.ScanAsync(CancellationToken.None);
        h.RegistryFor(i.GroupInstanceId).RaisedCodes.Should().Contain(code);

        i.TransitionTo(WorkloadInstanceState.Deploying, WorkloadStateReason.Redeploying, null, h.Clock.UtcNow);
        h.Clock.Set(T0.AddMinutes(13));
        await h.Scanner.ScanAsync(CancellationToken.None);

        h.RegistryFor(i.GroupInstanceId).ClearedCodes.Should().Contain(code, "leaving VersionSkew clears the manual-skew issue");
    }

    [Fact]
    public async Task Auto_rollback_intent_raises_an_info_parked_issue_that_escalates_to_warning_after_seven_days()
    {
        var h = new Harness();
        var i = h.Seed(WorkloadInstanceState.Degraded, transitionedAt: T0, workloadId: "wl-a");
        h.SeedIntent(i.GroupInstanceId, WorkloadUpdatePhase.RolledBack, createdAt: T0, terminalAt: T0,
            fromVersion: "1.0.0", toVersion: "1.1.0");
        var code = WorkloadRepairsScanner.ParkedCode(i.WorkloadId);

        h.Clock.Set(T0.AddDays(1));
        await h.Scanner.ScanAsync(CancellationToken.None);
        var first = h.RegistryFor(i.GroupInstanceId).Raises.Last(r => r.Code == code);
        first.Severity.Should().Be(WorkloadDiagnosticSeverity.Info);
        first.Metadata!["source"].Should().Be("auto-rollback");
        first.Metadata["parkedVersion"].Should().Be("1.1.0");

        h.Clock.Set(T0.AddDays(8));
        await h.Scanner.ScanAsync(CancellationToken.None);
        h.RegistryFor(i.GroupInstanceId).Raises.Last(r => r.Code == code).Severity.Should().Be(WorkloadDiagnosticSeverity.Warning);
    }

    [Fact]
    public async Task A_newer_completed_intent_supersedes_the_rolled_back_park_and_clears_the_issue()
    {
        var h = new Harness();
        var i = h.Seed(WorkloadInstanceState.Healthy, transitionedAt: T0, workloadId: "wl-a");
        h.SeedIntent(i.GroupInstanceId, WorkloadUpdatePhase.RolledBack, createdAt: T0, terminalAt: T0);
        var code = WorkloadRepairsScanner.ParkedCode(i.WorkloadId);

        h.Clock.Set(T0.AddDays(1));
        await h.Scanner.ScanAsync(CancellationToken.None);
        h.RegistryFor(i.GroupInstanceId).RaisedCodes.Should().Contain(code);

        h.SeedIntent(i.GroupInstanceId, WorkloadUpdatePhase.Completed, createdAt: T0.AddDays(2), terminalAt: T0.AddDays(2));
        h.Clock.Set(T0.AddDays(3));
        await h.Scanner.ScanAsync(CancellationToken.None);

        h.RegistryFor(i.GroupInstanceId).ClearedCodes.Should().Contain(code,
            "a newer Completed intent supersedes the rolled-back park and clears the issue");
    }

    [Fact]
    public async Task Rollback_failed_intent_raises_an_error_with_both_digests_and_no_invented_actions()
    {
        var h = new Harness();
        var i = h.Seed(WorkloadInstanceState.Failed, transitionedAt: T0, workloadId: "wl-a");
        h.SeedIntent(i.GroupInstanceId, WorkloadUpdatePhase.RollbackFailed, createdAt: T0, terminalAt: T0,
            fromVersion: "1.0.0", toVersion: "1.1.0",
            fromDigests: new Dictionary<string, string?> { ["wl-a"] = "sha256:fromfrom" },
            toDigests: new Dictionary<string, string?> { ["wl-a"] = "sha256:tototo" },
            evidence: new WorkloadUpdateEvidence("1.1.0", T0,
                [new WorkloadEvidenceCapture("wl-a", ["rollback pull failed"], [], null)]));
        var code = WorkloadRepairsScanner.RollbackFailedCode(i.WorkloadId);

        h.Clock.Set(T0.AddMinutes(1));
        await h.Scanner.ScanAsync(CancellationToken.None);

        var raise = h.RegistryFor(i.GroupInstanceId).Raises
            .Should().ContainSingle(r => r.Code == code).Subject;
        raise.Severity.Should().Be(WorkloadDiagnosticSeverity.Error, "a failed rollback is terminal-bad, not age-escalated");
        raise.Message.Should().Contain("1.0.0").And.Contain("1.1.0");
        raise.Metadata!["fromDigests"].Should().BeEquivalentTo(
            new Dictionary<string, string?> { ["wl-a"] = "sha256:fromfrom" });
        raise.Metadata["toDigests"].Should().BeEquivalentTo(
            new Dictionary<string, string?> { ["wl-a"] = "sha256:tototo" });
        raise.Metadata["evidence"].Should().BeOfType<WorkloadUpdateEvidence>()
            .Which.Workloads[0].PreviousContainerLogTail.Should().Equal("rollback pull failed");

        raise.Metadata.Should().NotContainKey("actions",
            "nothing implements retry_rollback/force_forward and no reader renders this key");
        raise.FixHint.Should().NotBeNull();
        raise.FixHint.Should().NotContain("Retry rollback").And.NotContain("Force forward");
        raise.FixHint.Should().Contain("Redeploy", "the one control the operator actually has on that tab");
    }

    [Fact]
    public async Task Rollback_failed_quotes_why_the_update_could_not_be_rolled_back()
    {
        var h = new Harness();
        var i = h.Seed(WorkloadInstanceState.Failed, transitionedAt: T0, workloadId: "wl-a");
        const string why = "stage.host.after: the host refused (stage 'stage.module-swap' is irreversible once entered: "
            + "a certified N+1 may have forward-migrated data)";
        h.SeedIntent(i.GroupInstanceId, WorkloadUpdatePhase.RollbackFailed, createdAt: T0, terminalAt: T0, error: why);

        h.Clock.Set(T0.AddMinutes(1));
        await h.Scanner.ScanAsync(CancellationToken.None);

        h.RegistryFor(i.GroupInstanceId).Raises
            .Should().ContainSingle(r => r.Code == WorkloadRepairsScanner.RollbackFailedCode(i.WorkloadId))
            .Which.Message.Should().EndWith($" Why: {why}");
    }

    [Theory]
    [InlineData("failed-forward", "was NOT rolled back")]
    [InlineData("abandoned-twice-before-the-turn", "was NOT rolled back")]
    [InlineData("compensating", "Rollback of the update from '1.0.0' to '1.1.0' FAILED")]
    [InlineData("unreadable", "whether anything was rolled back is not known")]
    [InlineData("none", "Rollback of the update from '1.0.0' to '1.1.0' FAILED")]
    public async Task The_rollback_failed_issue_says_whether_a_rollback_ran_as_the_journal_records_it(string journal, string expected)
    {
        var h = new Harness();
        var i = h.Seed(WorkloadInstanceState.Failed, transitionedAt: T0, workloadId: "wl-a");
        var intent = WorkloadUpdateIntent.Create(Guid.NewGuid(), i.GroupInstanceId, "1.0.0", "1.1.0", T0);
        intent.AdvanceTo(WorkloadUpdatePhase.ApplyingSidecar, T0);
        intent.AdvanceTo(WorkloadUpdatePhase.HealthGate, T0);
        var gate = new Reeve.Pipeline.StageEntry("stage.health-gate", T0, Reeve.Pipeline.PipelineDirection.Forward,
            Reeve.Pipeline.StageEntryState.Working, null);
        switch (journal)
        {
            case "failed-forward":
                intent.AdvanceTo(WorkloadUpdatePhase.SwappingModule, T0);
                intent.RecordStageJournal(new Reeve.Pipeline.PipelineJournal(
                    [new Reeve.Pipeline.StageEntry("stage.host.plugin-roll", T0, Reeve.Pipeline.PipelineDirection.Forward,
                        Reeve.Pipeline.StageEntryState.Failed, "replica-b refused")],
                    Reeve.Pipeline.PipelineDirection.Forward,
                    Reeve.Pipeline.PipelineState.Failed));
                intent.MarkRollbackFailed("replica-b refused", T0);
                break;
            case "abandoned-twice-before-the-turn":
                intent.RecordStageJournal(new Reeve.Pipeline.PipelineJournal(
                    [gate], Reeve.Pipeline.PipelineDirection.Forward, Reeve.Pipeline.PipelineState.Running));
                intent.Abandon("first abandon", T0);
                intent.Abandon("second abandon", T0);
                break;
            case "compensating":
                intent.RecordStageJournal(new Reeve.Pipeline.PipelineJournal(
                    [gate], Reeve.Pipeline.PipelineDirection.Compensating, Reeve.Pipeline.PipelineState.Failed));
                intent.Fail("gate failed", T0);
                intent.MarkRollbackFailed("the sidecar's Down failed", T0);
                break;
            case "unreadable":
                intent.Fail("gate failed", T0);
                intent.MarkRollbackFailed("the sidecar's Down failed", T0);
                typeof(WorkloadUpdateIntent).GetProperty(nameof(WorkloadUpdateIntent.StageJournalDocument))!
                    .SetValue(intent, new Reeve.Workloads.Abstractions.Domain.Entities.Documents.PipelineJournalDocument { Unreadable = true });
                break;
            default:
                intent.Fail("gate failed", T0);
                intent.MarkRollbackFailed("the rollback failed", T0);
                break;
        }

        h.Intents.Add(intent);

        h.Clock.Set(T0.AddMinutes(1));
        await h.Scanner.ScanAsync(CancellationToken.None);

        var message = h.RegistryFor(i.GroupInstanceId).Raises
            .Should().ContainSingle(r => r.Code == WorkloadRepairsScanner.RollbackFailedCode(i.WorkloadId)).Which.Message;
        message.Should().Contain(expected);
        message.Should().NotContain("nothing is running",
            "a refused restore leaves the new version running, and a failed rollback does not say which version runs");
        if (!expected.StartsWith("Rollback", StringComparison.Ordinal))
        {
            message.Should().NotContain("Rollback of the update").And.NotContain("nothing is running")
                .And.NotContain("past the point", "only the journal's failure can say that, and the Why quotes it");
        }
    }

    [Fact]
    public async Task Rollback_failed_does_not_age_escalate_it_stays_error_over_time()
    {
        var h = new Harness();
        var i = h.Seed(WorkloadInstanceState.Failed, transitionedAt: T0, workloadId: "wl-a");
        h.SeedIntent(i.GroupInstanceId, WorkloadUpdatePhase.RollbackFailed, createdAt: T0, terminalAt: T0);
        var code = WorkloadRepairsScanner.RollbackFailedCode(i.WorkloadId);

        h.Clock.Set(T0.AddDays(30));
        await h.Scanner.ScanAsync(CancellationToken.None);

        h.RegistryFor(i.GroupInstanceId).Raises.Single(r => r.Code == code).Severity
            .Should().Be(WorkloadDiagnosticSeverity.Error, "RollbackFailed is a fixed Error — it is not age-escalated");
    }

    [Fact]
    public async Task Rollback_failed_is_cleared_once_a_newer_intent_supersedes_it()
    {
        var h = new Harness();
        var i = h.Seed(WorkloadInstanceState.Failed, transitionedAt: T0, workloadId: "wl-a");
        h.SeedIntent(i.GroupInstanceId, WorkloadUpdatePhase.RollbackFailed, createdAt: T0, terminalAt: T0);
        var code = WorkloadRepairsScanner.RollbackFailedCode(i.WorkloadId);

        h.Clock.Set(T0.AddMinutes(1));
        await h.Scanner.ScanAsync(CancellationToken.None);
        h.RegistryFor(i.GroupInstanceId).RaisedCodes.Should().Contain(code);

        h.SeedIntent(i.GroupInstanceId, WorkloadUpdatePhase.Completed, createdAt: T0.AddDays(1), terminalAt: T0.AddDays(1));
        h.Clock.Set(T0.AddDays(2));
        await h.Scanner.ScanAsync(CancellationToken.None);

        h.RegistryFor(i.GroupInstanceId).ClearedCodes.Should().Contain(code,
            "a newer intent that recovers the instance clears the rollback-failed Error");
    }

    [Fact]
    public async Task An_intent_lookup_fault_does_not_raise_a_spurious_parked_issue_and_keeps_scanning_others()
    {
        var h = new Harness();
        var faulting = h.Seed(WorkloadInstanceState.Healthy, transitionedAt: T0, workloadId: "wl-faulting");
        var healthy = h.Seed(WorkloadInstanceState.Degraded, transitionedAt: T0, workloadId: "wl-other");
        h.Intents.ThrowOnLatestFor = faulting.GroupInstanceId;

        h.Clock.Set(T0.AddMinutes(16));
        await h.Scanner.ScanAsync(CancellationToken.None);

        h.RegistryFor(faulting.GroupInstanceId).RaisedCodes
            .Should().NotContain(WorkloadRepairsScanner.ParkedCode(faulting.WorkloadId),
                "a parked-source lookup fault must not raise a spurious parked issue");

        h.RegistryFor(healthy.GroupInstanceId).RaisedCodes
            .Should().Contain(WorkloadRepairsScanner.StuckCode(healthy.WorkloadId),
                "a fault on one source must not abort the other instances' evaluation");
    }

    [Fact]
    public async Task An_intent_lookup_fault_does_not_clear_an_already_open_parked_issue()
    {
        var h = new Harness();
        var i = h.Seed(WorkloadInstanceState.Healthy, transitionedAt: T0, workloadId: "wl-a");
        h.SeedIntent(i.GroupInstanceId, WorkloadUpdatePhase.RolledBack, createdAt: T0, terminalAt: T0);
        var code = WorkloadRepairsScanner.ParkedCode(i.WorkloadId);

        h.Clock.Set(T0.AddHours(1));
        await h.Scanner.ScanAsync(CancellationToken.None);
        h.RegistryFor(i.GroupInstanceId).RaisedCodes.Should().Contain(code);

        h.Intents.ThrowOnLatestFor = i.GroupInstanceId;
        h.Clock.Set(T0.AddHours(2));
        await h.Scanner.ScanAsync(CancellationToken.None);

        h.RegistryFor(i.GroupInstanceId).ClearedCodes
            .Should().NotContain(code, "a lookup fault must not blindly clear a real open parked issue");
    }

    private sealed class Harness
    {
        private ushort seq;
        public readonly MutableClock Clock = new(T0);
        public readonly InMemoryRepository Repository;
        public readonly InMemoryIntentRepository Intents = new();
        public readonly SpyRegistryFactory Registries = new();
        public readonly WorkloadRepairsScanner Scanner;

        public Harness(
            WorkloadRepairsOptions? options = null,
            IWorkloadProviderRegistry? providers = null)
        {
            Repository = new InMemoryRepository();
            var services = new ServiceCollection();
            services.AddScoped<IWorkloadInstanceRepository>(_ => Repository);
            services.AddScoped<IWorkloadUpdateIntentRepository>(_ => Intents);
            var provider = services.BuildServiceProvider();

            Scanner = new WorkloadRepairsScanner(
                provider.GetRequiredService<IServiceScopeFactory>(),
                Registries,
                providers ?? Reeve.Workloads.Core.Tests.ProviderRegistryFakes.FirstPartyRegistry(),
                Clock,
                Options.Create(options ?? new WorkloadRepairsOptions()),
                NullLogger<WorkloadRepairsScanner>.Instance);
        }

        public WorkloadInstance Seed(
            WorkloadInstanceState state,
            DateTimeOffset transitionedAt,
            Guid? pluginInstanceId = null,
            string? workloadId = null,
            string? stateMessage = null,
            int restartCount = 0,
            WorkloadProviderId? provider = null)
        {
            var pid = pluginInstanceId ?? new Guid(++seq, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);
            var wid = workloadId ?? "wl-" + Guid.NewGuid().ToString("N")[..6];
            var instance = WorkloadInstance.Create(Guid.NewGuid(), pid, "zwave", wid, transitionedAt);
            if (provider is { } p)
            {
                instance.SetProvider(p, transitionedAt);
            }

            DriveTo(instance, state, stateMessage, transitionedAt);
            if (restartCount > 0)
            {
                instance.SetRestartCount(restartCount, transitionedAt);
            }
            Repository.Add(instance);
            return instance;
        }

        public SpyRegistry RegistryFor(Guid pluginInstanceId) => Registries.For(pluginInstanceId);

        public WorkloadUpdateIntent SeedIntent(
            Guid pluginInstanceId,
            WorkloadUpdatePhase phase,
            DateTimeOffset createdAt,
            DateTimeOffset terminalAt,
            string fromVersion = "1.0.0",
            string toVersion = "1.1.0",
            IReadOnlyDictionary<string, string?>? fromDigests = null,
            IReadOnlyDictionary<string, string?>? toDigests = null,
            WorkloadUpdateEvidence? evidence = null,
            string error = "health gate failed")
        {
            var intent = WorkloadUpdateIntent.Create(Guid.NewGuid(), pluginInstanceId, fromVersion, toVersion, createdAt);
            intent.StageSnapshots(
                new WorkloadUpdateSnapshots
                {
                    FromDigests = fromDigests ?? new Dictionary<string, string?> { ["wl-a"] = "sha256:oldold" },
                    ToDigests = toDigests ?? new Dictionary<string, string?> { ["wl-a"] = "sha256:newnew" },
                },
                createdAt);

            switch (phase)
            {
                case WorkloadUpdatePhase.RolledBack:
                    intent.AdvanceTo(WorkloadUpdatePhase.ApplyingSidecar, createdAt);
                    intent.AdvanceTo(WorkloadUpdatePhase.HealthGate, createdAt);
                    intent.Fail(error, createdAt);
                    intent.CaptureEvidence(evidence ?? new WorkloadUpdateEvidence(toVersion, createdAt,
                        [new WorkloadEvidenceCapture("wl-a", ["boom"], [], null)]));
                    intent.AdvanceTo(WorkloadUpdatePhase.RolledBack, terminalAt);
                    break;
                case WorkloadUpdatePhase.RollbackFailed:
                    intent.AdvanceTo(WorkloadUpdatePhase.ApplyingSidecar, createdAt);
                    intent.AdvanceTo(WorkloadUpdatePhase.HealthGate, createdAt);
                    intent.Fail(error, createdAt);
                    intent.CaptureEvidence(evidence ?? new WorkloadUpdateEvidence(toVersion, createdAt,
                        [new WorkloadEvidenceCapture("wl-a", ["boom"], [], null)]));
                    intent.MarkRollbackFailed(error, terminalAt);
                    break;
                case WorkloadUpdatePhase.Completed:
                    intent.AdvanceTo(WorkloadUpdatePhase.ApplyingSidecar, createdAt);
                    intent.AdvanceTo(WorkloadUpdatePhase.HealthGate, createdAt);
                    intent.AdvanceTo(WorkloadUpdatePhase.SwappingModule, createdAt);
                    intent.Complete(terminalAt);
                    break;
                default:
                    throw new InvalidOperationException($"SeedIntent test helper does not drive to {phase}.");
            }

            Intents.Add(intent);
            return intent;
        }

        private static void DriveTo(WorkloadInstance instance, WorkloadInstanceState target, string? message, DateTimeOffset now)
        {
            if (instance.State == target)
            {
                return;
            }

            switch (target)
            {
                case WorkloadInstanceState.PendingConfiguration:
                    return;
                case WorkloadInstanceState.Suspended:
                    instance.TransitionTo(WorkloadInstanceState.Suspended, WorkloadStateReason.OperatorPaused, null, now);
                    return;
                case WorkloadInstanceState.Deploying:
                    instance.TransitionTo(WorkloadInstanceState.Deploying, WorkloadStateReason.Provisioning, null, now);
                    return;
                case WorkloadInstanceState.Healthy:
                    instance.TransitionTo(WorkloadInstanceState.Deploying, WorkloadStateReason.Provisioning, null, now);
                    instance.TransitionTo(WorkloadInstanceState.Healthy, WorkloadStateReason.HealthObserved, null, now);
                    return;
                case WorkloadInstanceState.Degraded:
                    instance.TransitionTo(WorkloadInstanceState.Deploying, WorkloadStateReason.Provisioning, null, now);
                    instance.TransitionTo(WorkloadInstanceState.Degraded, WorkloadStateReason.HealthLost, message, now);
                    return;
                case WorkloadInstanceState.Failed:
                    instance.TransitionTo(WorkloadInstanceState.Deploying, WorkloadStateReason.Provisioning, null, now);
                    instance.TransitionTo(WorkloadInstanceState.Failed, WorkloadStateReason.HealthLost, message, now);
                    return;
                case WorkloadInstanceState.Unknown:
                    instance.TransitionTo(WorkloadInstanceState.Deploying, WorkloadStateReason.Provisioning, null, now);
                    instance.TransitionTo(WorkloadInstanceState.Unknown, WorkloadStateReason.ProviderUnreachable, message, now);
                    return;
                case WorkloadInstanceState.VersionSkew:
                    instance.TransitionTo(WorkloadInstanceState.Deploying, WorkloadStateReason.Provisioning, null, now);
                    instance.TransitionTo(WorkloadInstanceState.Healthy, WorkloadStateReason.HealthObserved, null, now);
                    instance.TransitionTo(WorkloadInstanceState.VersionSkew, WorkloadStateReason.RunningVersionBehind, message, now);
                    return;
                case WorkloadInstanceState.Terminating:
                    instance.TransitionTo(WorkloadInstanceState.Terminating, WorkloadStateReason.Uninstalling, message, now);
                    return;
                default:
                    throw new InvalidOperationException($"Test helper cannot drive to {target}.");
            }
        }
    }

    private sealed class InMemoryRepository : IWorkloadInstanceRepository
    {
        private readonly Dictionary<Guid, WorkloadInstance> store = new();

        public void Add(WorkloadInstance instance) => store[instance.Id] = instance;

        public void Remove(WorkloadInstance instance) => store.Remove(instance.Id);

        public Task<IReadOnlyList<WorkloadInstance>> ListAllAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<WorkloadInstance>>(store.Values.ToList());

        public Task<WorkloadInstance?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
            => Task.FromResult(store.TryGetValue(id, out var i) ? i : null);

        public Task<WorkloadInstance?> GetByWorkloadAsync(Guid pluginInstanceId, string workloadId, CancellationToken cancellationToken = default)
            => Task.FromResult<WorkloadInstance?>(null);
        public Task<IReadOnlyList<WorkloadInstance>> ListByPluginInstanceAsync(Guid pluginInstanceId, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<WorkloadInstance>>(Array.Empty<WorkloadInstance>());
        public Task<IReadOnlyList<WorkloadInstance>> ListByStateAsync(WorkloadInstanceState state, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<WorkloadInstance>>(store.Values.Where(i => i.State == state).ToList());
        public Task AddAsync(WorkloadInstance instance, CancellationToken cancellationToken = default) { Add(instance); return Task.CompletedTask; }
        public Task UpdateAsync(WorkloadInstance instance, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<bool> TrySetRestoreBindingUnresolvedAsync(Guid workloadInstanceId, DateTimeOffset nowUtc, CancellationToken cancellationToken = default) => Task.FromResult(false);
        public Task DeleteAsync(WorkloadInstance instance, CancellationToken cancellationToken = default) { store.Remove(instance.Id); return Task.CompletedTask; }
        public Task<IReadOnlyDictionary<WorkloadInstanceState, int>> CountByStateAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyDictionary<WorkloadInstanceState, int>>(
                store.Values.GroupBy(i => i.State).ToDictionary(g => g.Key, g => g.Count()));
    }

    private sealed class InMemoryIntentRepository : IWorkloadUpdateIntentRepository
    {
        private readonly List<WorkloadUpdateIntent> intents = new();

        public Guid? ThrowOnLatestFor { get; set; }

        public void Add(WorkloadUpdateIntent intent) => intents.Add(intent);

        public Task<WorkloadUpdateIntent?> GetLatestForInstanceAsync(Guid pluginInstanceId, CancellationToken cancellationToken = default)
        {
            if (ThrowOnLatestFor == pluginInstanceId)
            {
                throw new InvalidOperationException("intent store fault");
            }

            return Task.FromResult(intents
                .Where(i => i.GroupInstanceId == pluginInstanceId)
                .OrderByDescending(i => i.CreatedAtUtc)
                .FirstOrDefault());
        }

        public Task<WorkloadUpdateIntent?> GetActiveForInstanceAsync(Guid pluginInstanceId, CancellationToken cancellationToken = default)
            => Task.FromResult(intents.FirstOrDefault(i => i.GroupInstanceId == pluginInstanceId && i.IsActive));
        public Task<Guid?> GetActiveIntentIdAsync(Guid pluginInstanceId, CancellationToken cancellationToken = default)
            => Task.FromResult(intents.Where(i => i.GroupInstanceId == pluginInstanceId && i.IsActive).Select(i => (Guid?)i.Id).FirstOrDefault());
        public Task<IReadOnlyList<WorkloadUpdateIntent>> GetOpenIntentsAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<WorkloadUpdateIntent>>(intents.Where(i => i.IsActive).ToList());
        public Task<WorkloadUpdateIntent?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
            => Task.FromResult(intents.FirstOrDefault(i => i.Id == id));
        public Task<IReadOnlyList<WorkloadUpdateIntent>> GetRecentForInstanceAsync(Guid pluginInstanceId, int limit, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<WorkloadUpdateIntent>>(limit <= 0 ? [] : intents.Where(i => i.GroupInstanceId == pluginInstanceId).OrderByDescending(i => i.CreatedAtUtc).Take(limit).ToList());
        public Task AddAsync(WorkloadUpdateIntent intent, CancellationToken cancellationToken = default) { Add(intent); return Task.CompletedTask; }
        public Task UpdateAsync(WorkloadUpdateIntent intent, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class SpyRegistryFactory : IWorkloadDiagnosticsSinkFactory
    {
        private readonly ConcurrentDictionary<Guid, SpyRegistry> registries = new();

        public IWorkloadDiagnosticsSink Create(Guid instanceId) => For(instanceId);

        public SpyRegistry For(Guid pluginInstanceId) => registries.GetOrAdd(pluginInstanceId, _ => new SpyRegistry());

        public void Prune(IEnumerable<Guid> liveInstanceIds)
        {
            var live = liveInstanceIds.ToHashSet();
            foreach (var id in registries.Keys)
            {
                if (!live.Contains(id))
                {
                    registries.TryRemove(id, out _);
                }
            }
        }

        public IReadOnlyCollection<Guid> LivePluginInstanceIds => registries.Keys.ToList();
    }

    public sealed record RaiseCall(
        string Code,
        WorkloadDiagnosticSeverity Severity,
        string Message,
        string? FixHint,
        string? RepairFlowId,
        IReadOnlyDictionary<string, object?>? Metadata);

    private sealed class SpyRegistry : IWorkloadDiagnosticsSink
    {
        public readonly List<RaiseCall> Raises = new();
        public readonly List<string> ClearedCodes = new();

        public IReadOnlyList<string> RaisedCodes => Raises.Select(r => r.Code).ToList();

        public Task RaiseAsync(WorkloadDiagnostic diagnostic, CancellationToken cancellationToken = default)
        {
            Raises.Add(new RaiseCall(diagnostic.Code, diagnostic.Severity, diagnostic.Message,
                diagnostic.FixHint, diagnostic.RepairFlowId, diagnostic.Metadata));
            return Task.CompletedTask;
        }

        public Task ClearAsync(string code, CancellationToken cancellationToken = default)
        {
            ClearedCodes.Add(code);
            return Task.CompletedTask;
        }
    }

    private sealed class MutableClock(DateTimeOffset now) : IWorkloadClock
    {
        public DateTimeOffset UtcNow { get; private set; } = now;

        public void Set(DateTimeOffset now) => UtcNow = now;
    }
}
