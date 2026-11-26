using FluentAssertions;
using Reeve.Pipeline;
using Reeve.Workloads.Abstractions.Update;
using Reeve.Workloads.Core.Update;
using Reeve.Workloads.Core.Update.Stages;
using Xunit;

namespace Reeve.Workloads.Core.Tests.Update.Stages;

public sealed class HealthGateStageTests
{
    private static readonly WorkloadUpdateOrchestratorOptions Options = new() { HealthGateTimeout = TimeSpan.FromMinutes(7) };

    private static HealthGateStage Stage(out ScriptedHealthGate gate, out MutableClock clock)
    {
        gate = new ScriptedHealthGate(new CallLog());
        clock = new MutableClock(new DateTimeOffset(2026, 9, 22, 12, 0, 0, TimeSpan.Zero));
        return new HealthGateStage(gate, clock, Options);
    }

    [Fact]
    public void It_follows_the_operators_redeploy_and_is_bounded_by_the_absolute_cap_not_the_pause_excluded_timeout()
    {
        var stage = Stage(out _, out _);

        stage.Id.Should().Be(WorkloadUpdateStages.HealthGate);
        stage.Placement.AfterId.Should().Be(WorkloadUpdateStages.OperatorRedeploy);
        stage.Reversibility.IsReversible.Should().BeTrue();
        stage.Budget.Limit.Should().Be(Options.HealthGateMaximumWait);
        stage.Budget.Limit.Should().BeGreaterThan(Options.HealthGateTimeout,
            "an image pull or a paused span must not burn the pause-excluded gate timeout through the stage budget");
        stage.Budget.Policy.Should().Be(StageTimeoutPolicy.FailAndCompensate,
            "a gate that never certifies means the candidate is not healthy, which is a failure to unwind from");
    }

    [Fact]
    public void A_maximum_wait_below_the_gate_timeout_is_raised_to_it()
    {
        var options = new WorkloadUpdateOrchestratorOptions
        {
            HealthGateTimeout = TimeSpan.FromMinutes(20),
            HealthGateMaximumWait = TimeSpan.FromMinutes(10),
        };

        new HealthGateStage(new ScriptedHealthGate(new CallLog()), new MutableClock(DateTimeOffset.UnixEpoch), options)
            .Budget.Limit.Should().Be(TimeSpan.FromMinutes(20));
    }

    [Fact]
    public async Task Up_finishes_when_every_workload_has_passed()
    {
        var stage = Stage(out var gate, out _);
        gate.DefaultVerdict = WorkloadHealthGateVerdict.Passed;
        var context = await StageContextFactory.BuildAsync();

        var outcome = await stage.UpAsync(context, CancellationToken.None);

        outcome.Kind.Should().Be(StageOutcomeKind.Done);
        gate.Requests.Should().HaveCount(2, "the gate is ticked once per workload");
    }

    [Fact]
    public async Task Up_keeps_working_and_says_how_many_are_still_unhealthy()
    {
        var stage = Stage(out var gate, out _);
        gate.DefaultVerdict = WorkloadHealthGateVerdict.Passed;
        gate.VerdictsByWorkload["beta"] = new Queue<WorkloadHealthGateVerdict>([WorkloadHealthGateVerdict.Pending]);
        var context = await StageContextFactory.BuildAsync();

        var outcome = await stage.UpAsync(context, CancellationToken.None);

        outcome.Kind.Should().Be(StageOutcomeKind.Working);
        outcome.Detail.Should().Be("1 of 2 workloads healthy — waiting for: beta");
    }

    [Fact]
    public async Task Up_fails_naming_the_workload_and_what_the_gate_said()
    {
        var stage = Stage(out var gate, out _);
        gate.DefaultVerdict = WorkloadHealthGateVerdict.Passed;
        gate.VerdictsByWorkload["alpha"] = new Queue<WorkloadHealthGateVerdict>([WorkloadHealthGateVerdict.Failed]);
        var context = await StageContextFactory.BuildAsync();

        var outcome = await stage.UpAsync(context, CancellationToken.None);

        outcome.Kind.Should().Be(StageOutcomeKind.Failed);
        outcome.Detail.Should().Contain("alpha").And.Contain("SpecHashMismatch");
    }

    [Fact]
    public async Task A_single_failure_stops_the_pass_rather_than_ticking_the_rest()
    {
        var stage = Stage(out var gate, out _);
        gate.VerdictsByWorkload["alpha"] = new Queue<WorkloadHealthGateVerdict>([WorkloadHealthGateVerdict.Failed]);
        var context = await StageContextFactory.BuildAsync();

        await stage.UpAsync(context, CancellationToken.None);

        gate.Requests.Should().ContainSingle().Which.Reference.WorkloadId.Should().Be("alpha");
    }

    [Fact]
    public async Task The_gate_clock_survives_a_tick_boundary()
    {
        var stage = Stage(out var gate, out _);
        gate.DefaultVerdict = WorkloadHealthGateVerdict.Pending;
        var context = await StageContextFactory.BuildAsync();

        await stage.UpAsync(context, CancellationToken.None);
        await stage.UpAsync(context, CancellationToken.None);

        gate.Requests.Where(request => request.Reference.WorkloadId == "alpha")
            .Select(request => (int)request.PriorClockState.AccumulatedPause.TotalSeconds)
            .Should().Equal([0, 1], "the second tick is handed back what the first one produced");
    }

    [Fact]
    public async Task The_gate_is_asked_about_the_candidate_side_and_never_the_previous_one()
    {
        var stage = Stage(out var gate, out _);
        var plan = StageContextFactory.PlanWithDistinctSides();
        var context = await StageContextFactory.BuildAsync(plan);

        await stage.UpAsync(context, CancellationToken.None);

        var request = gate.Requests[0];
        request.ToVersion.Should().Be(plan.ToVersion);
        request.CandidateExtensions.Should().BeSameAs(plan.Workloads[0].CandidateExtensions);
        request.CurrentExtensions.Should().BeSameAs(plan.Workloads[0].CurrentExtensions);
        request.Certification.Should().Be(WorkloadHealthGateCertification.ProviderReady);
        request.Timeout.Should().Be(Options.HealthGateTimeout);
    }

    [Fact]
    public async Task A_gate_that_throws_keeps_the_walk_working_rather_than_passing_it()
    {
        var gate = new ThrowingHealthGate();
        var stage = new HealthGateStage(gate, new MutableClock(DateTimeOffset.UnixEpoch), Options);
        var context = await StageContextFactory.BuildAsync();

        var outcome = await stage.UpAsync(context, CancellationToken.None);

        outcome.Kind.Should().Be(StageOutcomeKind.Working);
    }

    [Fact]
    public async Task Down_does_nothing_because_the_gate_applied_nothing()
    {
        var stage = Stage(out var gate, out _);
        var context = await StageContextFactory.BuildAsync();

        var outcome = await stage.DownAsync(context, CancellationToken.None);

        outcome.Kind.Should().Be(StageOutcomeKind.Done);
        gate.Requests.Should().BeEmpty();
    }

    private sealed class ThrowingHealthGate : IWorkloadUpdateHealthGate
    {
        public Task<WorkloadHealthGateTickResult> EvaluateTickAsync(
            WorkloadHealthGateTickRequest request, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("the gateway is unreachable");
    }
}
