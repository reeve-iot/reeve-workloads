using Reeve.Workloads.Abstractions.Contracts.Deployment;
using Reeve.Workloads.Core.Health;
using Reeve.Workloads.Core.Session;
using Reeve.Workloads.Domain;

namespace Reeve.Workloads.Core.Tests;

internal sealed class FakeHostHealthContributor : IWorkloadHealthContributor
{
    private readonly Dictionary<Guid, (string? SpecHash, string? AppVersion)> live = [];

    public IReadOnlySet<WorkloadHealthMode> ObservedModes { get; } =
        new HashSet<WorkloadHealthMode> { RecordingRolloutCertifier.SessionMode };

    public bool FaultReads { get; set; }

    public int ReadCount { get; private set; }

    public void SeedMatching(Guid instanceId, string specHash) => live[instanceId] = (specHash, "1.0.0");

    public void SeedWithAppVersion(Guid instanceId, string specHash, string appVersion)
        => live[instanceId] = (specHash, appVersion);

    public void Remove(Guid instanceId) => live.Remove(instanceId);

    public WorkloadHealthContributorDispatch Dispatch() => new([this]);

    public Task<WorkloadHealthContribution> ObserveAsync(
        WorkloadHealthObservation observation, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ReadCount++;

        if (FaultReads)
        {
            return Task.FromResult(new WorkloadHealthContribution { Presence = WorkloadHealthEvaluator.Unknown });
        }

        var found = live.TryGetValue(observation.InstanceId, out var workload);
        var version = found && !string.IsNullOrWhiteSpace(workload.AppVersion) ? workload.AppVersion : null;
        return Task.FromResult(new WorkloadHealthContribution
        {
            Presence = WorkloadHealthEvaluator.FromDefiniteRead(
                found ? workload.SpecHash : null, observation.DesiredSpecHash),
            RunningVersion = version,
            RunningVersionIsOlder = WorkloadVersionOrder.IsOlder(version, observation.InstalledGroupVersion),
        });
    }
}
