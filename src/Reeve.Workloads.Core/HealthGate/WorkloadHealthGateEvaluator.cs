namespace Reeve.Workloads.Core.HealthGate;

/// <summary>Pure, I/O-free HealthGate tick evaluator: one observation plus the prior clock yields a verdict and the
/// next clock. It PASSES only on the full conjunction its certification mode demands, and every ambiguity fails closed.</summary>
public static class WorkloadHealthGateEvaluator
{
    /// <summary>Evaluates one gate tick. See the type doc for the full behaviour contract.</summary>
    public static HealthGateTickResult Evaluate(
        HealthGateConfig config,
        HealthGateObservation observation,
        HealthGateClockState priorClock,
        DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(priorClock);

        var evidence = observation.Evidence;
        var readiness = observation.Readiness;

        var evidenceUnavailableMatters = config.Certification == HealthGateCertification.HostEvidence
                                        && evidence.Evidence == RolloutEvidence.Kind.Unavailable;
        var paused = evidenceUnavailableMatters
                     || readiness == WorkloadProviderReadiness.Unknown;
        if (paused)
        {
            var pausedClock = priorClock with
            {
                PausedSinceUtc = priorClock.PausedSinceUtc ?? nowUtc,
                ReconnectGraceUntilUtc = priorClock.ReconnectGraceUntilUtc,
            };
            return HealthGateTickResult.Pending(pausedClock, "rollout evidence unavailable — clock paused");
        }

        var clock = ResolvePauseTransition(config, priorClock, nowUtc);

        if (readiness == WorkloadProviderReadiness.FatalCrash)
        {
            return HealthGateTickResult.Failed(
                HealthGateFailureReason.CrashLoop,
                clock,
                $"provider reported a fatal crash loop (restartCount={observation.RestartCount})");
        }

        if (config.Certification == HealthGateCertification.AppliedSpec)
        {
            return HealthGateTickResult.Passed(clock);
        }

        var clockShouldStart = readiness is WorkloadProviderReadiness.Ready
                                            or WorkloadProviderReadiness.Running
                                            or WorkloadProviderReadiness.RetryableCrash;
        if (clockShouldStart && clock.ClockStartedAtUtc is null)
        {
            clock = clock with { ClockStartedAtUtc = nowUtc };
        }

        var outgoingAnswered = false;

        if (evidence.Evidence == RolloutEvidence.Kind.Candidate)
        {
            var candidate = evidence.Candidate!;

            var rollbackNoTimeGate = config.ApplyTimestampUtc == DateTimeOffset.MinValue;

            var isOutgoing = candidate.IsOutgoing && config.OutgoingSpecHash is { Length: > 0 };
            var isPostApply = !isOutgoing
                              && (rollbackNoTimeGate || candidate.ObservedSinceUtc > config.ApplyTimestampUtc);
            if (isOutgoing)
            {
                outgoingAnswered = true;
            }
            if (isPostApply)
            {
                if (candidate.IdentityFailure is { } identityFailure)
                {
                    return HealthGateTickResult.FailedOnHostEvidence(identityFailure, clock, candidate.Detail);
                }

                if (!candidate.Stale)
                {
                    if (readiness == WorkloadProviderReadiness.Ready)
                    {
                        return HealthGateTickResult.Passed(clock with { ReconnectGraceUntilUtc = null, GraceConsumed = false });
                    }

                    clock = clock with { ReconnectGraceUntilUtc = null, GraceConsumed = false };
                    return TimeoutOrPending(config, clock, nowUtc, "qualifying candidate present, awaiting provider-ready");
                }
            }
        }

        if (config.Certification == HealthGateCertification.ProviderReady
            && readiness == WorkloadProviderReadiness.Ready)
        {
            return HealthGateTickResult.Passed(clock with { ReconnectGraceUntilUtc = null, GraceConsumed = false });
        }

        return TimeoutOrPending(
            config,
            clock,
            nowUtc,
            config.Certification == HealthGateCertification.ProviderReady
                ? "provider not ready yet (no candidate evidence required for this health mode)"
                : outgoingAnswered
                    ? "only the outgoing workload answered; no qualifying post-apply candidate yet"
                    : "no qualifying post-apply candidate yet");
    }

    private static HealthGateClockState ResolvePauseTransition(
        HealthGateConfig config, HealthGateClockState priorClock, DateTimeOffset nowUtc)
    {
        if (priorClock.PausedSinceUtc is not { } pausedSince)
        {
            return priorClock;
        }

        var pauseSpan = nowUtc - pausedSince;
        if (pauseSpan < TimeSpan.Zero)
        {
            pauseSpan = TimeSpan.Zero;
        }

        var recovered = priorClock with
        {
            PausedSinceUtc = null,
            AccumulatedPause = priorClock.AccumulatedPause + pauseSpan,
        };

        return priorClock.GraceConsumed
            ? recovered
            : recovered with
            {
                ReconnectGraceUntilUtc = nowUtc + config.ReconnectGrace,
                GraceConsumed = true,
            };
    }

    private static HealthGateTickResult TimeoutOrPending(
        HealthGateConfig config,
        HealthGateClockState clock,
        DateTimeOffset nowUtc,
        string detail)
    {
        if (clock.ClockStartedAtUtc is not { } startedAt)
        {
            return HealthGateTickResult.Pending(clock, detail);
        }

        if (clock.ReconnectGraceUntilUtc is { } graceUntil && nowUtc <= graceUntil)
        {
            return HealthGateTickResult.Pending(clock, detail + " (within post-recovery reconnect grace)");
        }

        var running = (nowUtc - startedAt) - clock.AccumulatedPause;
        if (running > config.Timeout)
        {
            return HealthGateTickResult.Failed(
                HealthGateFailureReason.Timeout,
                clock,
                $"gate timeout: {running:g} of running budget (limit {config.Timeout:g}) elapsed with {detail}");
        }

        return HealthGateTickResult.Pending(clock, detail);
    }
}
