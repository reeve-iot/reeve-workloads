using Reeve.Workloads.Abstractions.Domain.DesiredSpec;

namespace Reeve.Workloads.Abstractions;

/// <summary>Default <see cref="IDesiredSpecResolver"/>. While an intent is active its target spec wins
/// (converge-to-target); otherwise the caller's fresh computation is returned.</summary>
public sealed class DesiredSpecResolver : IDesiredSpecResolver
{
    private readonly IActiveWorkloadIntentProvider _intentProvider;

    public DesiredSpecResolver(IActiveWorkloadIntentProvider intentProvider)
    {
        _intentProvider = intentProvider ?? throw new ArgumentNullException(nameof(intentProvider));
    }

    public async Task<WorkloadDesiredSpec> ResolveAsync(
        Guid pluginInstanceId,
        string workloadId,
        Func<WorkloadDesiredSpec> computeFresh,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(computeFresh);

        var intentTarget = await _intentProvider
            .TryGetActiveTargetSpecAsync(pluginInstanceId, workloadId, cancellationToken)
            .ConfigureAwait(false);

        return intentTarget ?? computeFresh();
    }
}
