using FluentAssertions;
using NSubstitute;
using Reeve.Workloads.Abstractions;
using Reeve.Workloads.Abstractions.Contracts;
using Reeve.Workloads.Abstractions.Contracts.Deployment;
using Reeve.Workloads.Abstractions.Providers;
using Reeve.Workloads.Abstractions.Update;
using Reeve.Workloads.Core.HealthGate;
using Reeve.Workloads.Providers.Docker;

namespace Reeve.Workloads.Core.Tests.HealthGate;

public sealed class WorkloadHealthGateTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 10, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid InstanceId = Guid.Parse("7f1d0c2e-4b6a-4c8d-9e10-2f3a4b5c6d7e");

    private static readonly WorkloadRef Reference = new()
    {
        InstanceId = InstanceId,
        GroupId = "acme.zwave",
        WorkloadId = "controller",
        Inst8 = "7f1d0c2e",
        Provider = DockerProvider.Id,
    };

    private static HealthGateConfig Config(
        HealthGateCertification certification = HealthGateCertification.HostEvidence) => new()
        {
            ExpectedSpecHash = "sha256:new",
            ApplyTimestampUtc = Now - TimeSpan.FromMinutes(5),
            CurrentExtensions = WorkloadExtensions.None,
            CandidateExtensions = WorkloadExtensions.None,
            Timeout = TimeSpan.FromMinutes(10),
            Certification = certification,
        };

    private static IWorkloadClock FixedClock()
    {
        var clock = Substitute.For<IWorkloadClock>();
        clock.UtcNow.Returns(Now);
        return clock;
    }

    private static WorkloadHealthGate Gate(
        IRolloutCertifier certifier,
        Func<WorkloadRef, CancellationToken, Task<WorkloadStatus>> probe)
        => new(certifier, probe, FixedClock());

    private sealed class ThrowingCertifier : IRolloutCertifier
    {
        public IReadOnlySet<WorkloadHealthMode> RequiredModes { get; } = new HashSet<WorkloadHealthMode>();

        public Task<RolloutEvidence> CertifyAsync(
            RolloutCertificationRequest request, DateTimeOffset nowUtc,
            CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("presence store unreachable");
    }

    private sealed class FixedCertifier(RolloutEvidence evidence) : IRolloutCertifier
    {
        public IReadOnlySet<WorkloadHealthMode> RequiredModes { get; } = new HashSet<WorkloadHealthMode>();

        public Task<RolloutEvidence> CertifyAsync(
            RolloutCertificationRequest request, DateTimeOffset nowUtc,
            CancellationToken cancellationToken = default)
            => Task.FromResult(evidence);
    }

    private sealed class RecordingCertifier(RolloutEvidence evidence) : IRolloutCertifier
    {
        public IReadOnlySet<WorkloadHealthMode> RequiredModes { get; } = new HashSet<WorkloadHealthMode>();

        public Guid? AskedAbout { get; private set; }

        public DateTimeOffset? AskedAtUtc { get; private set; }

        public Task<RolloutEvidence> CertifyAsync(
            RolloutCertificationRequest request, DateTimeOffset nowUtc,
            CancellationToken cancellationToken = default)
        {
            AskedAbout = request.InstanceId;
            AskedAtUtc = nowUtc;
            return Task.FromResult(evidence);
        }
    }

    private static Task<WorkloadStatus> Ready(WorkloadRef reference, CancellationToken cancellationToken)
        => Task.FromResult(new WorkloadStatus { Phase = WorkloadProviderPhase.Ready, ProviderReady = true });

    private static WorkloadStatus Status(
        WorkloadProviderPhase phase,
        string? message = null,
        int restartCount = 0,
        params WorkloadProviderEvent[] events) => new()
        {
            Phase = phase,
            ProviderReady = phase == WorkloadProviderPhase.Ready,
            RestartCount = restartCount,
            Message = message,
            RecentEvents = events,
        };

    private static WorkloadProviderEvent Event(string reason, string message) => new()
    {
        AtUtc = Now,
        Reason = reason,
        Message = message,
    };

    [Fact]
    public async Task A_certifier_that_throws_pauses_the_clock_while_a_genuine_absence_does_not()
    {
        var faulted = await Gate(new ThrowingCertifier(), Ready).EvaluateTickAsync(
            Reference, InstanceId, Config(), HealthGateClockState.Initial);

        faulted.Verdict.Should().Be(HealthGateVerdict.Pending);
        faulted.Clock.PausedSinceUtc.Should().Be(Now, "an unreadable source must not spend the candidate's budget");

        var absent = await Gate(new FixedCertifier(RolloutEvidence.NoCandidate), Ready).EvaluateTickAsync(
            Reference, InstanceId, Config(), HealthGateClockState.Initial);

        absent.Clock.PausedSinceUtc.Should().BeNull("nothing having answered yet is the candidate's own problem");
        absent.Clock.ClockStartedAtUtc.Should().Be(Now);
    }

    [Fact]
    public async Task A_status_probe_that_throws_pauses_rather_than_reporting_a_crash()
    {
        var gate = Gate(
            new FixedCertifier(RolloutEvidence.NoCandidate),
            (_, _) => throw new HttpRequestException("docker daemon not listening"));

        var result = await gate.EvaluateTickAsync(
            Reference, InstanceId, Config(), HealthGateClockState.Initial);

        result.Verdict.Should().Be(HealthGateVerdict.Pending);
        result.Reason.Should().BeNull("an unreachable provider is not evidence of a crash");
        result.Clock.PausedSinceUtc.Should().Be(Now);
    }

    [Fact]
    public async Task Cancellation_propagates_from_the_certifier_instead_of_degrading_to_a_pause()
    {
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        var certifier = Substitute.For<IRolloutCertifier>();
        certifier.CertifyAsync(
                Arg.Any<RolloutCertificationRequest>(), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
            .Returns<Task<RolloutEvidence>>(_ => throw new OperationCanceledException(cancellation.Token));

        var tick = async () => await Gate(certifier, Ready).EvaluateTickAsync(
            Reference, InstanceId, Config(), HealthGateClockState.Initial, cancellation.Token);

        await tick.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task Cancellation_propagates_from_the_status_probe_instead_of_degrading_to_unknown()
    {
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        var gate = Gate(
            new FixedCertifier(RolloutEvidence.NoCandidate),
            (_, token) => throw new OperationCanceledException(token));

        var tick = async () => await gate.EvaluateTickAsync(
            Reference, InstanceId, Config(), HealthGateClockState.Initial, cancellation.Token);

        await tick.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task The_certifier_is_asked_about_this_instance_at_the_injected_clocks_instant()
    {
        var certifier = new RecordingCertifier(RolloutEvidence.NoCandidate);

        await Gate(certifier, Ready).EvaluateTickAsync(
            Reference, InstanceId, Config(), HealthGateClockState.Initial);

        certifier.AskedAbout.Should().Be(InstanceId);
        certifier.AskedAtUtc.Should().Be(Now);
    }

    [Fact]
    public async Task The_probes_restart_count_reaches_the_crash_verdict()
    {
        var gate = Gate(
            new FixedCertifier(RolloutEvidence.NoCandidate),
            (_, _) => Task.FromResult(Status(WorkloadProviderPhase.Failed, "CrashLoopBackOff", restartCount: 11)));

        var result = await gate.EvaluateTickAsync(
            Reference, InstanceId, Config(), HealthGateClockState.Initial);

        result.Verdict.Should().Be(HealthGateVerdict.Failed);
        result.Reason.Should().Be(HealthGateFailureReason.CrashLoop);
        result.Detail.Should().Contain("restartCount=11");
    }

    [Fact]
    public async Task The_provider_bound_constructor_probes_that_providers_status()
    {
        var provider = Substitute.For<IWorkloadProvider>();
        provider.GetStatusAsync(Arg.Any<WorkloadRef>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Status(WorkloadProviderPhase.Failed, "CrashLoopBackOff")));

        var gate = new WorkloadHealthGate(
            new FixedCertifier(RolloutEvidence.NoCandidate), provider, FixedClock());

        var result = await gate.EvaluateTickAsync(
            Reference, InstanceId, Config(), HealthGateClockState.Initial);

        await provider.Received(1).GetStatusAsync(Reference, Arg.Any<CancellationToken>());
        result.Reason.Should().Be(HealthGateFailureReason.CrashLoop);
    }

    [Theory]
    [InlineData(WorkloadProviderPhase.Unknown, WorkloadProviderReadiness.Unknown)]
    [InlineData(WorkloadProviderPhase.Ready, WorkloadProviderReadiness.Ready)]
    [InlineData(WorkloadProviderPhase.Running, WorkloadProviderReadiness.Running)]
    [InlineData(WorkloadProviderPhase.Pending, WorkloadProviderReadiness.Pulling)]
    [InlineData(WorkloadProviderPhase.NotFound, WorkloadProviderReadiness.Pulling)]
    public void Every_provider_phase_maps_to_the_readiness_its_clock_rule_needs(
        WorkloadProviderPhase phase, WorkloadProviderReadiness expected)
    {
        WorkloadHealthGate.MapReadiness(Status(phase)).Should().Be(expected);
    }

    [Theory]
    [InlineData("ImagePullBackOff", WorkloadProviderReadiness.RetryableCrash)]
    [InlineData("ErrImagePull", WorkloadProviderReadiness.RetryableCrash)]
    [InlineData("Back-off pulling image \"acme/zwave:2\"", WorkloadProviderReadiness.RetryableCrash)]
    [InlineData("imagepullbackoff", WorkloadProviderReadiness.RetryableCrash)]
    [InlineData("CrashLoopBackOff", WorkloadProviderReadiness.FatalCrash)]
    [InlineData("exec format error", WorkloadProviderReadiness.FatalCrash)]
    [InlineData("OOMKilled", WorkloadProviderReadiness.FatalCrash)]
    [InlineData(null, WorkloadProviderReadiness.FatalCrash)]
    public void A_failure_is_retryable_only_when_the_message_says_the_image_is_still_pulling(
        string? message, WorkloadProviderReadiness expected)
    {
        WorkloadHealthGate.MapReadiness(Status(WorkloadProviderPhase.Failed, message)).Should().Be(expected);
    }

    [Fact]
    public void A_pull_backoff_is_recognised_from_an_events_reason_or_message_not_only_the_status_message()
    {
        var byReason = Status(
            WorkloadProviderPhase.Failed, message: null, restartCount: 0,
            Event("ImagePullBackOff", "Back-off restarting failed container"));
        var byMessage = Status(
            WorkloadProviderPhase.Failed, message: null, restartCount: 0,
            Event("Failed", "Error: ErrImagePull"));
        var neither = Status(
            WorkloadProviderPhase.Failed, message: null, restartCount: 0,
            Event("BackOff", "Back-off restarting failed container"));

        WorkloadHealthGate.MapReadiness(byReason).Should().Be(WorkloadProviderReadiness.RetryableCrash);
        WorkloadHealthGate.MapReadiness(byMessage).Should().Be(WorkloadProviderReadiness.RetryableCrash);
        WorkloadHealthGate.MapReadiness(neither).Should().Be(
            WorkloadProviderReadiness.FatalCrash, "a restart backoff is not a pull backoff");
    }

    [Fact]
    public void The_gate_refuses_every_missing_collaborator_at_construction()
    {
        var certifier = new FixedCertifier(RolloutEvidence.NoCandidate);

        var withoutProbe = () => new WorkloadHealthGate(
            certifier, (Func<WorkloadRef, CancellationToken, Task<WorkloadStatus>>)null!, FixedClock());
        var withoutClock = () => new WorkloadHealthGate(certifier, Ready, null!);
        var withoutProvider = () => new WorkloadHealthGate(certifier, (IWorkloadProvider)null!, FixedClock());

        withoutProbe.Should().Throw<ArgumentNullException>();
        withoutClock.Should().Throw<ArgumentNullException>();
        withoutProvider.Should().Throw<ArgumentNullException>();
    }
}
