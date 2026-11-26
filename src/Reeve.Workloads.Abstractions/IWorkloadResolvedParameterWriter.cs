namespace Reeve.Workloads.Abstractions;

/// <summary>The resolved parameters of several workload instances of ONE module, as a single commit. A per-instance
/// save leaves some rows written when a later one fails — a state the update's own rollback cannot describe.</summary>
public interface IWorkloadResolvedParameterWriter
{
    /// <summary>Applies <paramref name="byWorkloadId"/> and commits once. A workload with no entry is left
    /// alone — it has nothing to re-affirm — and so is one with no instance row yet. Returns the rows written.</summary>
    Task<int> SetResolvedParametersAsync(
        Guid groupInstanceId,
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, string?>> byWorkloadId,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken = default);
}
