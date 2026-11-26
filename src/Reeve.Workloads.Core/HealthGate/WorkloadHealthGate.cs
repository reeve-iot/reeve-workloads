using Reeve.Workloads.Abstractions;
using Reeve.Workloads.Abstractions.Providers;

namespace Reeve.Workloads.Core.HealthGate;

internal sealed class WorkloadHealthGate
{
    private readonly IRolloutCertifier? _certifier;
    private readonly Func<WorkloadRef, CancellationToken, Task<WorkloadStatus>> _statusProbe;
    private readonly IWorkloadClock _clock;

    public WorkloadHealthGate(
        IRolloutCertifier? certifier,
        Func<WorkloadRef, CancellationToken, Task<WorkloadStatus>> statusProbe,
        IWorkloadClock clock)
    {
        _certifier = certifier;
        _statusProbe = statusProbe ?? throw new ArgumentNullException(nameof(statusProbe));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    public WorkloadHealthGate(IRolloutCertifier? certifier, IWorkloadProvider provider, IWorkloadClock clock)
        : this(certifier, StatusProbeFor(provider), clock)
    {
    }

    private static Func<WorkloadRef, CancellationToken, Task<WorkloadStatus>> StatusProbeFor(IWorkloadProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        return (reference, cancellationToken) => provider.GetStatusAsync(reference, cancellationToken);
    }

    public async Task<HealthGateTickResult> EvaluateTickAsync(
        WorkloadRef reference,
        Guid instanceId,
        HealthGateConfig config,
        HealthGateClockState priorClock,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(priorClock);

        var nowUtc = _clock.UtcNow;
        var evidence = await GatherEvidenceAsync(instanceId, config, nowUtc, cancellationToken).ConfigureAwait(false);
        var (readiness, restartCount) = await GatherReadinessAsync(reference, cancellationToken).ConfigureAwait(false);

        var observation = new HealthGateObservation(evidence, readiness, restartCount);
        return WorkloadHealthGateEvaluator.Evaluate(config, observation, priorClock, nowUtc);
    }

    private async Task<RolloutEvidence> GatherEvidenceAsync(
        Guid instanceId,
        HealthGateConfig config,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken)
    {
        if (_certifier is null)
        {
            return RolloutEvidence.NoCandidate;
        }

        try
        {
            var request = new RolloutCertificationRequest
            {
                InstanceId = instanceId,
                ExpectedSpecHash = config.ExpectedSpecHash,
                OutgoingSpecHash = config.OutgoingSpecHash,
                ToVersion = config.ToVersion,
                CurrentExtensions = config.CurrentExtensions,
                CandidateExtensions = config.CandidateExtensions,
            };
            return await _certifier.CertifyAsync(request, nowUtc, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return RolloutEvidence.Unavailable;
        }
    }

    private async Task<(WorkloadProviderReadiness Readiness, int RestartCount)> GatherReadinessAsync(
        WorkloadRef reference,
        CancellationToken cancellationToken)
    {
        WorkloadStatus status;
        try
        {
            status = await _statusProbe(reference, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return (WorkloadProviderReadiness.Unknown, 0);
        }

        return (MapReadiness(status), status.RestartCount);
    }

    public static WorkloadProviderReadiness MapReadiness(WorkloadStatus status)
    {
        ArgumentNullException.ThrowIfNull(status);

        return status.Phase switch
        {
            WorkloadProviderPhase.Unknown => WorkloadProviderReadiness.Unknown,
            WorkloadProviderPhase.Ready => WorkloadProviderReadiness.Ready,
            WorkloadProviderPhase.Running => WorkloadProviderReadiness.Running,
            WorkloadProviderPhase.Failed => ClassifyFailure(status),
            WorkloadProviderPhase.Pending => WorkloadProviderReadiness.Pulling,
            WorkloadProviderPhase.NotFound => WorkloadProviderReadiness.Pulling,
            _ => WorkloadProviderReadiness.Unknown,
        };
    }

    private static WorkloadProviderReadiness ClassifyFailure(WorkloadStatus status)
    {
        return IsImagePullBackoff(status)
            ? WorkloadProviderReadiness.RetryableCrash
            : WorkloadProviderReadiness.FatalCrash;
    }

    private static bool IsImagePullBackoff(WorkloadStatus status)
    {
        if (ContainsPullBackoffToken(status.Message))
        {
            return true;
        }

        foreach (var evt in status.RecentEvents)
        {
            if (ContainsPullBackoffToken(evt.Reason) || ContainsPullBackoffToken(evt.Message))
            {
                return true;
            }
        }

        return false;
    }

    private static bool ContainsPullBackoffToken(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return false;
        }

        return text.Contains("ImagePullBackOff", StringComparison.OrdinalIgnoreCase)
               || text.Contains("ErrImagePull", StringComparison.OrdinalIgnoreCase)
               || text.Contains("pulling image", StringComparison.OrdinalIgnoreCase)
               || text.Contains("ImagePull", StringComparison.OrdinalIgnoreCase);
    }
}
