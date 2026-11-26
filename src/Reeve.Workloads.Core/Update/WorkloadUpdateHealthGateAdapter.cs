using Reeve.Workloads.Abstractions;
using Reeve.Workloads.Abstractions.Domain;
using Reeve.Workloads.Abstractions.Domain.Entities;
using Reeve.Workloads.Abstractions.Providers;
using Reeve.Workloads.Abstractions.Update;
using Reeve.Workloads.Core.HealthGate;

namespace Reeve.Workloads.Core.Update;

internal sealed class WorkloadUpdateHealthGateAdapter : IWorkloadUpdateHealthGate
{
    private readonly IRolloutCertifier? _certifier;
    private readonly IWorkloadProviderRegistry _providerRegistry;
    private readonly IWorkloadClock _clock;

    public WorkloadUpdateHealthGateAdapter(
        IEnumerable<IRolloutCertifier> certifiers,
        IWorkloadProviderRegistry providerRegistry,
        IWorkloadClock clock)
    {
        _certifier = RolloutCertificationPolicy.Single(certifiers);
        _providerRegistry = providerRegistry ?? throw new ArgumentNullException(nameof(providerRegistry));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    public async Task<WorkloadHealthGateTickResult> EvaluateTickAsync(WorkloadHealthGateTickRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var priorClock = FromState(request.PriorClockState);
        var config = new HealthGateConfig
        {
            ExpectedSpecHash = request.ExpectedSpecHash,
            OutgoingSpecHash = request.OutgoingSpecHash,
            ApplyTimestampUtc = request.ApplyTimestampUtc,
            ToVersion = request.ToVersion,
            CurrentExtensions = request.CurrentExtensions,
            CandidateExtensions = request.CandidateExtensions,
            Timeout = request.Timeout,
            Certification = MapCertification(request.Certification),
        };

        var provider = _providerRegistry.TryResolve(request.Reference.Provider);
        var gate = new WorkloadHealthGate(
            _certifier,
            (reference, ct) => provider is null
                ? Task.FromResult(WorkloadStatus.NotFound("no provider registered"))
                : provider.GetStatusAsync(reference, ct),
            _clock);

        var tick = await gate.EvaluateTickAsync(request.Reference, request.InstanceId, config, priorClock, cancellationToken).ConfigureAwait(false);

        return new WorkloadHealthGateTickResult
        {
            Verdict = MapVerdict(tick.Verdict),
            FailureReason = tick.FailureReason,
            Detail = tick.Detail,
            NextClockState = ToState(tick.Clock),
        };
    }

    private static HealthGateCertification MapCertification(WorkloadHealthGateCertification certification) => certification switch
    {
        WorkloadHealthGateCertification.HostEvidence => HealthGateCertification.HostEvidence,
        WorkloadHealthGateCertification.ProviderReady => HealthGateCertification.ProviderReady,
        WorkloadHealthGateCertification.AppliedSpec => HealthGateCertification.AppliedSpec,
        _ => throw new ArgumentOutOfRangeException(
            nameof(certification), certification, "Not a certification this gate knows."),
    };

    private static WorkloadHealthGateVerdict MapVerdict(HealthGateVerdict verdict) => verdict switch
    {
        HealthGateVerdict.Passed => WorkloadHealthGateVerdict.Passed,
        HealthGateVerdict.Failed => WorkloadHealthGateVerdict.Failed,
        _ => WorkloadHealthGateVerdict.Pending,
    };

    public static WorkloadGateClockState ToState(HealthGateClockState clock) => new()
    {
        ClockStartedAtUtc = clock.ClockStartedAtUtc,
        PausedSinceUtc = clock.PausedSinceUtc,
        AccumulatedPause = clock.AccumulatedPause,
        ReconnectGraceUntilUtc = clock.ReconnectGraceUntilUtc,
        GraceConsumed = clock.GraceConsumed,
    };

    public static HealthGateClockState FromState(WorkloadGateClockState state) => new()
    {
        ClockStartedAtUtc = state.ClockStartedAtUtc,
        PausedSinceUtc = state.PausedSinceUtc,
        AccumulatedPause = state.AccumulatedPause,
        ReconnectGraceUntilUtc = state.ReconnectGraceUntilUtc,
        GraceConsumed = state.GraceConsumed,
    };
}
