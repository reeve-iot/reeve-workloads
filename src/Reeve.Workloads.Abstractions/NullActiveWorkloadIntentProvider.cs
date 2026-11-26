using Reeve.Workloads.Abstractions.Domain.DesiredSpec;

namespace Reeve.Workloads.Abstractions;

/// <summary>Default no-op <see cref="IActiveWorkloadIntentProvider"/>: never an active intent, so the resolver
/// always computes the desired spec fresh. A future DB-backed provider will read <c>WorkloadUpdateIntents.TargetSpecJson</c>.</summary>
public sealed class NullActiveWorkloadIntentProvider : IActiveWorkloadIntentProvider
{
    public Task<WorkloadDesiredSpec?> TryGetActiveTargetSpecAsync(
        Guid pluginInstanceId,
        string workloadId,
        CancellationToken cancellationToken)
        => Task.FromResult<WorkloadDesiredSpec?>(null);
}
