using FluentAssertions;
using Reeve.Workloads.Abstractions.Contracts.Deployment;
using Reeve.Workloads.Abstractions.Update;
using Reeve.Workloads.Core.HealthGate;

namespace Reeve.Workloads.Core.Tests.HealthGate;

public sealed class WorkloadHealthGateEvaluatorTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 10, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Applied = Now - TimeSpan.FromMinutes(5);
    private static readonly TimeSpan Budget = TimeSpan.FromMinutes(10);

    private static HealthGateConfig Config(
        HealthGateCertification certification = HealthGateCertification.HostEvidence,
        DateTimeOffset? appliedAtUtc = null,
        TimeSpan? timeout = null,
        TimeSpan? reconnectGrace = null,
        string? outgoingSpecHash = null) => new()
        {
            OutgoingSpecHash = outgoingSpecHash,
            ExpectedSpecHash = "sha256:new",
            ApplyTimestampUtc = appliedAtUtc ?? Applied,
            CurrentExtensions = WorkloadExtensions.None,
            CandidateExtensions = WorkloadExtensions.None,
            Timeout = timeout ?? Budget,
            ReconnectGrace = reconnectGrace ?? TimeSpan.FromSeconds(90),
            Certification = certification,
        };

    private static RolloutCandidate GoodCandidate(
        DateTimeOffset? connectedAtUtc = null,
        bool stale = false,
        string? identityFailure = null,
        string? detail = null) => new()
        {
            ObservedSinceUtc = connectedAtUtc ?? Applied + TimeSpan.FromSeconds(30),
            Stale = stale,
            IdentityFailure = identityFailure,
            Detail = detail,
        };

    private static HealthGateObservation Observed(
        RolloutEvidence? evidence = null,
        WorkloadProviderReadiness readiness = WorkloadProviderReadiness.Ready,
        int restartCount = 0)
        => new(evidence ?? RolloutEvidence.From(GoodCandidate()), readiness, restartCount);

    [Fact]
    public void An_unreadable_certifier_pauses_a_session_gate_instead_of_burning_the_budget()
    {
        var exhausted = HealthGateClockState.Initial with { ClockStartedAtUtc = Now - Budget - TimeSpan.FromHours(1) };

        var result = WorkloadHealthGateEvaluator.Evaluate(
            Config(), Observed(RolloutEvidence.Unavailable), exhausted, Now);

        result.Verdict.Should().Be(HealthGateVerdict.Pending);
        result.Clock.PausedSinceUtc.Should().Be(Now);
        result.Detail.Should().Contain("clock paused");
    }

    [Fact]
    public void A_pause_already_in_progress_keeps_the_instant_it_began()
    {
        var began = Now - TimeSpan.FromMinutes(3);
        var paused = HealthGateClockState.Initial with { PausedSinceUtc = began };

        var result = WorkloadHealthGateEvaluator.Evaluate(
            Config(), Observed(RolloutEvidence.Unavailable), paused, Now);

        result.Clock.PausedSinceUtc.Should().Be(began);
    }

    [Theory]
    [InlineData(HealthGateCertification.ProviderReady)]
    [InlineData(HealthGateCertification.AppliedSpec)]
    public void A_certification_needing_no_candidate_does_not_pause_on_unavailable_evidence(
        HealthGateCertification certification)
    {
        var result = WorkloadHealthGateEvaluator.Evaluate(
            Config(certification), Observed(RolloutEvidence.Unavailable), HealthGateClockState.Initial, Now);

        result.Verdict.Should().Be(HealthGateVerdict.Passed);
        result.Clock.PausedSinceUtc.Should().BeNull();
    }

    [Theory]
    [InlineData(HealthGateCertification.HostEvidence)]
    [InlineData(HealthGateCertification.ProviderReady)]
    [InlineData(HealthGateCertification.AppliedSpec)]
    public void An_unreachable_provider_pauses_every_certification_mode(HealthGateCertification certification)
    {
        var result = WorkloadHealthGateEvaluator.Evaluate(
            Config(certification),
            Observed(readiness: WorkloadProviderReadiness.Unknown),
            HealthGateClockState.Initial,
            Now);

        result.Verdict.Should().Be(HealthGateVerdict.Pending);
        result.Clock.PausedSinceUtc.Should().Be(Now);
    }

    [Fact]
    public void A_fatal_crash_fails_an_applied_spec_gate_that_would_otherwise_pass_on_the_apply_alone()
    {
        var result = WorkloadHealthGateEvaluator.Evaluate(
            Config(HealthGateCertification.AppliedSpec),
            Observed(RolloutEvidence.NoCandidate, WorkloadProviderReadiness.FatalCrash, restartCount: 7),
            HealthGateClockState.Initial,
            Now);

        result.Verdict.Should().Be(HealthGateVerdict.Failed);
        result.Reason.Should().Be(HealthGateFailureReason.CrashLoop);
        result.Detail.Should().Contain("restartCount=7", "the count is the operator's only handle on how bad it is");
    }

    [Fact]
    public void An_applied_spec_gate_passes_with_nothing_answering_and_nothing_running()
    {
        var result = WorkloadHealthGateEvaluator.Evaluate(
            Config(HealthGateCertification.AppliedSpec),
            Observed(RolloutEvidence.NoCandidate, WorkloadProviderReadiness.Pulling),
            HealthGateClockState.Initial,
            Now);

        result.Verdict.Should().Be(HealthGateVerdict.Passed);
    }

    [Fact]
    public void A_still_pulling_workload_never_starts_the_clock_so_the_budget_cannot_expire()
    {
        var result = WorkloadHealthGateEvaluator.Evaluate(
            Config(),
            Observed(RolloutEvidence.NoCandidate, WorkloadProviderReadiness.Pulling),
            HealthGateClockState.Initial,
            Now + Budget + TimeSpan.FromHours(1));

        result.Verdict.Should().Be(HealthGateVerdict.Pending);
        result.Clock.ClockStartedAtUtc.Should().BeNull();
    }

    [Theory]
    [InlineData(WorkloadProviderReadiness.Ready, true)]
    [InlineData(WorkloadProviderReadiness.Running, true)]
    [InlineData(WorkloadProviderReadiness.RetryableCrash, true)]
    [InlineData(WorkloadProviderReadiness.Pulling, false)]
    public void The_clock_starts_on_a_phase_under_way_and_not_on_a_pull(
        WorkloadProviderReadiness readiness, bool expectStarted)
    {
        var result = WorkloadHealthGateEvaluator.Evaluate(
            Config(), Observed(RolloutEvidence.NoCandidate, readiness), HealthGateClockState.Initial, Now);

        result.Clock.ClockStartedAtUtc.Should().Be(expectStarted ? Now : null);
    }

    [Fact]
    public void An_already_started_clock_is_not_restarted_by_a_later_tick()
    {
        var startedAt = Now - TimeSpan.FromMinutes(2);
        var running = HealthGateClockState.Initial with { ClockStartedAtUtc = startedAt };

        var result = WorkloadHealthGateEvaluator.Evaluate(
            Config(), Observed(RolloutEvidence.NoCandidate, WorkloadProviderReadiness.Running), running, Now);

        result.Clock.ClockStartedAtUtc.Should().Be(startedAt);
    }

    [Fact]
    public void A_pre_apply_candidate_carrying_a_fatal_mismatch_does_not_fail_the_gate()
    {
        var outgoing = GoodCandidate(
            connectedAtUtc: Applied - TimeSpan.FromMinutes(1),
            identityFailure: "SpecHashMismatch",
            detail: "the outgoing sidecar");

        var result = WorkloadHealthGateEvaluator.Evaluate(
            Config(),
            Observed(RolloutEvidence.From(outgoing), WorkloadProviderReadiness.Running),
            HealthGateClockState.Initial,
            Now);

        result.Verdict.Should().Be(HealthGateVerdict.Pending);
        result.Reason.Should().BeNull();
        result.Detail.Should().Contain("no qualifying post-apply candidate");
    }

    [Theory]
    [InlineData(WorkloadProviderReadiness.Running)]
    [InlineData(WorkloadProviderReadiness.Ready)]
    public void A_post_apply_candidate_the_certifier_names_as_outgoing_neither_fails_nor_passes_the_gate(
        WorkloadProviderReadiness readiness)
    {
        var outgoing = GoodCandidate(connectedAtUtc: Applied + TimeSpan.FromSeconds(4)) with { IsOutgoing = true };

        var result = WorkloadHealthGateEvaluator.Evaluate(
            Config(outgoingSpecHash: "sha256:old"),
            Observed(RolloutEvidence.From(outgoing), readiness),
            HealthGateClockState.Initial,
            Now);

        result.Verdict.Should().Be(HealthGateVerdict.Pending);
        result.Reason.Should().BeNull();
        result.Detail.Should().Contain("no qualifying post-apply candidate");
    }

    [Fact]
    public void An_outgoing_candidate_that_keeps_answering_runs_into_the_timeout()
    {
        var outgoing = GoodCandidate(connectedAtUtc: Applied + TimeSpan.FromSeconds(4)) with { IsOutgoing = true };
        var started = HealthGateClockState.Initial with { ClockStartedAtUtc = Now - Budget - TimeSpan.FromSeconds(1) };

        var result = WorkloadHealthGateEvaluator.Evaluate(
            Config(outgoingSpecHash: "sha256:old"),
            Observed(RolloutEvidence.From(outgoing), WorkloadProviderReadiness.Running),
            started,
            Now);

        result.Verdict.Should().Be(HealthGateVerdict.Failed);
        result.Reason.Should().Be(HealthGateFailureReason.Timeout);
        result.Detail.Should().Contain("only the outgoing workload answered",
            "an operator has to tell a new pod that never came from an old one that kept answering");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void A_candidate_marked_outgoing_is_still_evaluated_when_no_outgoing_spec_was_named(string? outgoingSpecHash)
    {
        var wrong = GoodCandidate(connectedAtUtc: Applied + TimeSpan.FromSeconds(4), identityFailure: "SpecHashMismatch")
            with { IsOutgoing = true };

        var failed = WorkloadHealthGateEvaluator.Evaluate(
            Config(outgoingSpecHash: outgoingSpecHash),
            Observed(RolloutEvidence.From(wrong), WorkloadProviderReadiness.Running),
            HealthGateClockState.Initial,
            Now);
        var passed = WorkloadHealthGateEvaluator.Evaluate(
            Config(outgoingSpecHash: outgoingSpecHash),
            Observed(RolloutEvidence.From(GoodCandidate() with { IsOutgoing = true })),
            HealthGateClockState.Initial,
            Now);

        failed.Verdict.Should().Be(HealthGateVerdict.Failed);
        failed.Reason.Should().Be(HealthGateFailureReason.HostEvidence);
        passed.Verdict.Should().Be(HealthGateVerdict.Passed);
    }

    [Fact]
    public void A_candidate_that_is_outgoing_and_carries_an_identity_failure_keeps_the_gate_waiting()
    {
        var outgoing = GoodCandidate(connectedAtUtc: Applied + TimeSpan.FromSeconds(4), identityFailure: "SpecHashMismatch")
            with { IsOutgoing = true };

        var result = WorkloadHealthGateEvaluator.Evaluate(
            Config(outgoingSpecHash: "sha256:old"),
            Observed(RolloutEvidence.From(outgoing), WorkloadProviderReadiness.Ready),
            HealthGateClockState.Initial,
            Now);

        result.Verdict.Should().Be(HealthGateVerdict.Pending);
        result.Reason.Should().BeNull();
    }

    [Theory]
    [InlineData("SpecHashMismatch")]
    [InlineData("ProtocolVersionMismatch")]
    [InlineData("AppVersionMismatch")]
    [InlineData("ApiSchemaMismatch")]
    public void A_post_apply_identity_mismatch_fails_with_its_own_reason_before_provider_ready(string failure)
    {
        var wrong = GoodCandidate(identityFailure: failure, detail: "carried into diagnostics");

        var result = WorkloadHealthGateEvaluator.Evaluate(
            Config(),
            Observed(RolloutEvidence.From(wrong), WorkloadProviderReadiness.Running),
            HealthGateClockState.Initial,
            Now);

        result.Verdict.Should().Be(HealthGateVerdict.Failed);
        result.Reason.Should().Be(HealthGateFailureReason.HostEvidence);
        result.HostFailureCode.Should().Be(failure);
        result.FailureReason.Should().Be(failure, "the host's code is reported verbatim");
        result.Detail.Should().Be("carried into diagnostics");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void A_blank_identity_code_still_fails_the_gate_and_reads_HostEvidence(string blank)
    {
        var wrong = GoodCandidate(identityFailure: blank);

        var result = WorkloadHealthGateEvaluator.Evaluate(
            Config(),
            Observed(RolloutEvidence.From(wrong), WorkloadProviderReadiness.Running),
            HealthGateClockState.Initial,
            Now);

        result.Verdict.Should().Be(HealthGateVerdict.Failed);
        result.Reason.Should().Be(HealthGateFailureReason.HostEvidence);
        result.HostFailureCode.Should().Be(blank);
        result.FailureReason.Should().Be("HostEvidence");
    }

    [Fact]
    public void The_failure_reasons_keep_their_numbers()
    {
        ((int)HealthGateFailureReason.Timeout).Should().Be(0);
        ((int)HealthGateFailureReason.CrashLoop).Should().Be(4);
        ((int)HealthGateFailureReason.HostEvidence).Should().Be(6);
    }

    [Fact]
    public void A_rollback_window_certifies_a_candidate_whose_connect_time_is_unknown()
    {
        var undisturbed = GoodCandidate(connectedAtUtc: DateTimeOffset.MinValue);

        var rollback = WorkloadHealthGateEvaluator.Evaluate(
            Config(appliedAtUtc: DateTimeOffset.MinValue),
            Observed(RolloutEvidence.From(undisturbed)),
            HealthGateClockState.Initial,
            Now);

        rollback.Verdict.Should().Be(HealthGateVerdict.Passed);

        var forward = WorkloadHealthGateEvaluator.Evaluate(
            Config(), Observed(RolloutEvidence.From(undisturbed)), HealthGateClockState.Initial, Now);

        forward.Verdict.Should().Be(HealthGateVerdict.Pending);
    }

    [Fact]
    public void A_long_standing_instance_is_certified_under_a_rollback_window()
    {
        var result = WorkloadHealthGateEvaluator.Evaluate(
            Config(appliedAtUtc: DateTimeOffset.MinValue),
            Observed(RolloutEvidence.From(GoodCandidate(connectedAtUtc: Now - TimeSpan.FromDays(3)))),
            HealthGateClockState.Initial,
            Now);

        result.Verdict.Should().Be(HealthGateVerdict.Passed);
    }

    [Fact]
    public void A_qualifying_candidate_plus_provider_ready_passes_and_returns_the_grace()
    {
        var spent = HealthGateClockState.Initial with
        {
            ClockStartedAtUtc = Now - TimeSpan.FromMinutes(1),
            ReconnectGraceUntilUtc = Now + TimeSpan.FromSeconds(10),
            GraceConsumed = true,
        };

        var result = WorkloadHealthGateEvaluator.Evaluate(Config(), Observed(), spent, Now);

        result.Verdict.Should().Be(HealthGateVerdict.Passed);
        result.Clock.ReconnectGraceUntilUtc.Should().BeNull();
        result.Clock.GraceConsumed.Should().BeFalse();
    }

    [Fact]
    public void A_qualifying_candidate_without_provider_ready_keeps_waiting()
    {
        var running = HealthGateClockState.Initial with { ClockStartedAtUtc = Now - TimeSpan.FromMinutes(1) };

        var result = WorkloadHealthGateEvaluator.Evaluate(
            Config(), Observed(readiness: WorkloadProviderReadiness.Running), running, Now);

        result.Verdict.Should().Be(HealthGateVerdict.Pending);
        result.Detail.Should().Contain("awaiting provider-ready");
        result.Clock.GraceConsumed.Should().BeFalse("real progress returns the tolerance window");
    }

    [Fact]
    public void A_stale_candidate_neither_passes_nor_fails_even_with_the_provider_ready()
    {
        var quiet = GoodCandidate(stale: true);
        var running = HealthGateClockState.Initial with { ClockStartedAtUtc = Now - TimeSpan.FromMinutes(1) };

        var result = WorkloadHealthGateEvaluator.Evaluate(
            Config(), Observed(RolloutEvidence.From(quiet)), running, Now);

        result.Verdict.Should().Be(HealthGateVerdict.Pending);
        result.Reason.Should().BeNull();
    }

    [Fact]
    public void A_provider_ready_gate_passes_with_no_candidate_evidence_at_all()
    {
        var result = WorkloadHealthGateEvaluator.Evaluate(
            Config(HealthGateCertification.ProviderReady),
            Observed(RolloutEvidence.NoCandidate),
            HealthGateClockState.Initial,
            Now);

        result.Verdict.Should().Be(HealthGateVerdict.Passed);
    }

    [Fact]
    public void A_provider_ready_gate_does_not_pass_while_the_provider_is_only_running()
    {
        var result = WorkloadHealthGateEvaluator.Evaluate(
            Config(HealthGateCertification.ProviderReady),
            Observed(RolloutEvidence.NoCandidate, WorkloadProviderReadiness.Running),
            HealthGateClockState.Initial,
            Now);

        result.Verdict.Should().Be(HealthGateVerdict.Pending);
        result.Detail.Should().Contain("provider not ready yet");
    }

    [Theory]
    [InlineData(60, HealthGateVerdict.Pending)]
    [InlineData(40, HealthGateVerdict.Failed)]
    public void The_running_budget_excludes_accumulated_pause(int accumulatedPauseSeconds, HealthGateVerdict expected)
    {
        var clock = HealthGateClockState.Initial with
        {
            ClockStartedAtUtc = Now - TimeSpan.FromSeconds(100),
            AccumulatedPause = TimeSpan.FromSeconds(accumulatedPauseSeconds),
        };

        var result = WorkloadHealthGateEvaluator.Evaluate(
            Config(timeout: TimeSpan.FromSeconds(50)),
            Observed(RolloutEvidence.NoCandidate),
            clock,
            Now);

        result.Verdict.Should().Be(expected);
    }

    [Theory]
    [InlineData(50, HealthGateVerdict.Pending)]
    [InlineData(51, HealthGateVerdict.Failed)]
    public void The_timeout_fires_only_on_strictly_exceeding_the_budget(
        int elapsedSeconds, HealthGateVerdict expected)
    {
        var clock = HealthGateClockState.Initial with
        {
            ClockStartedAtUtc = Now - TimeSpan.FromSeconds(elapsedSeconds),
        };

        var result = WorkloadHealthGateEvaluator.Evaluate(
            Config(timeout: TimeSpan.FromSeconds(50)),
            Observed(RolloutEvidence.NoCandidate),
            clock,
            Now);

        result.Verdict.Should().Be(expected);
        if (expected == HealthGateVerdict.Failed)
        {
            result.Reason.Should().Be(HealthGateFailureReason.Timeout);
        }
    }

    [Theory]
    [InlineData(10, HealthGateVerdict.Pending)]
    [InlineData(-10, HealthGateVerdict.Failed)]
    public void A_reconnect_grace_suspends_the_timeout_only_while_it_is_open(
        int graceOffsetSeconds, HealthGateVerdict expected)
    {
        var clock = HealthGateClockState.Initial with
        {
            ClockStartedAtUtc = Now - Budget - TimeSpan.FromMinutes(1),
            ReconnectGraceUntilUtc = Now + TimeSpan.FromSeconds(graceOffsetSeconds),
            GraceConsumed = true,
        };

        var result = WorkloadHealthGateEvaluator.Evaluate(
            Config(), Observed(RolloutEvidence.NoCandidate), clock, Now);

        result.Verdict.Should().Be(expected);
    }

    [Fact]
    public void The_first_recovery_arms_one_reconnect_grace_and_records_it_as_spent()
    {
        var recovering = HealthGateClockState.Initial with
        {
            ClockStartedAtUtc = Now - TimeSpan.FromMinutes(1),
            PausedSinceUtc = Now - TimeSpan.FromSeconds(30),
        };

        var result = WorkloadHealthGateEvaluator.Evaluate(
            Config(reconnectGrace: TimeSpan.FromSeconds(90)),
            Observed(RolloutEvidence.NoCandidate),
            recovering,
            Now);

        result.Clock.PausedSinceUtc.Should().BeNull();
        result.Clock.ReconnectGraceUntilUtc.Should().Be(Now + TimeSpan.FromSeconds(90));
        result.Clock.GraceConsumed.Should().BeTrue();
    }

    [Fact]
    public void A_second_recovery_does_not_re_arm_the_grace_it_already_spent()
    {
        var alreadyLapsed = Now - TimeSpan.FromMinutes(2);
        var flapping = HealthGateClockState.Initial with
        {
            ClockStartedAtUtc = Now - TimeSpan.FromMinutes(5),
            PausedSinceUtc = Now - TimeSpan.FromSeconds(30),
            ReconnectGraceUntilUtc = alreadyLapsed,
            GraceConsumed = true,
        };

        var result = WorkloadHealthGateEvaluator.Evaluate(
            Config(reconnectGrace: TimeSpan.FromSeconds(90)),
            Observed(RolloutEvidence.NoCandidate),
            flapping,
            Now);

        result.Clock.ReconnectGraceUntilUtc.Should().Be(alreadyLapsed);
        result.Clock.GraceConsumed.Should().BeTrue();
    }

    [Fact]
    public void A_pause_that_ends_adds_its_span_to_the_accumulator()
    {
        var recovering = HealthGateClockState.Initial with
        {
            ClockStartedAtUtc = Now - TimeSpan.FromMinutes(5),
            PausedSinceUtc = Now - TimeSpan.FromSeconds(30),
            AccumulatedPause = TimeSpan.FromSeconds(45),
        };

        var result = WorkloadHealthGateEvaluator.Evaluate(
            Config(), Observed(RolloutEvidence.NoCandidate), recovering, Now);

        result.Clock.AccumulatedPause.Should().Be(TimeSpan.FromSeconds(75));
    }

    [Fact]
    public void A_pause_that_reads_as_negative_under_clock_skew_credits_nothing()
    {
        var skewed = HealthGateClockState.Initial with
        {
            ClockStartedAtUtc = Now - TimeSpan.FromMinutes(5),
            PausedSinceUtc = Now + TimeSpan.FromMinutes(10),
            AccumulatedPause = TimeSpan.FromSeconds(45),
        };

        var result = WorkloadHealthGateEvaluator.Evaluate(
            Config(), Observed(RolloutEvidence.NoCandidate), skewed, Now);

        result.Clock.AccumulatedPause.Should().Be(TimeSpan.FromSeconds(45));
    }

    [Fact]
    public void The_evaluator_refuses_a_null_config_or_clock_state()
    {
        var evaluateWithoutConfig = () => WorkloadHealthGateEvaluator.Evaluate(
            null!, Observed(), HealthGateClockState.Initial, Now);
        var evaluateWithoutClock = () => WorkloadHealthGateEvaluator.Evaluate(
            Config(), Observed(), null!, Now);

        evaluateWithoutConfig.Should().Throw<ArgumentNullException>();
        evaluateWithoutClock.Should().Throw<ArgumentNullException>();
    }
}
