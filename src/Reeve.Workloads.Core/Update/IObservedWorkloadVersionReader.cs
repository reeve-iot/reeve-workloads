using Reeve.Workloads.Abstractions.Update;

namespace Reeve.Workloads.Core.Update;

/// <summary>The version the running workload reports, by the host's own evidence, whatever its health mode. Read
/// only by the manual-provider swap-park gate, to confirm the operator redeployed before committing the module.</summary>
public interface IObservedWorkloadVersionReader
{
    /// <summary>The reported version, or null on absence, a blank report or a read fault — fail-safe: callers
    /// treat null as "not yet caught up," never as "caught up."</summary>
    Task<string?> GetObservedAppVersionAsync(Guid instanceId, CancellationToken cancellationToken = default);
}
