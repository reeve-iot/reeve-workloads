using Reeve.Workloads.Abstractions.Contracts.Deployment;
using Reeve.Workloads.Core.HealthGate;

namespace Reeve.Workloads.Core.Tests;

internal sealed class RecordingRolloutCertifier(params WorkloadHealthMode[] required) : IRolloutCertifier
{
    public static readonly WorkloadHealthMode SessionMode = new("adapter-session");

    public IReadOnlySet<WorkloadHealthMode> RequiredModes { get; } = new HashSet<WorkloadHealthMode>(required);

    public List<RolloutCertificationRequest> Requests { get; } = [];

    public Func<RolloutCertificationRequest, DateTimeOffset, RolloutEvidence> Answer { get; set; }
        = (_, _) => RolloutEvidence.NoCandidate;

    public Task<RolloutEvidence> CertifyAsync(
        RolloutCertificationRequest request, DateTimeOffset nowUtc, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Requests.Add(request);
        return Task.FromResult(Answer(request, nowUtc));
    }
}
