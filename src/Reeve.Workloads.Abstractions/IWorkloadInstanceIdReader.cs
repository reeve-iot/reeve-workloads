namespace Reeve.Workloads.Abstractions;

/// <summary>Reads the ids of workload instances that still have a row. It reads rows, not containers: the
/// persistence package answers it and a provider consumes it, so it can live inside neither.</summary>
public interface IWorkloadInstanceIdReader
{
    /// <summary>Every <c>WorkloadInstance.Id</c> currently persisted.</summary>
    Task<IReadOnlySet<Guid>> GetKnownInstanceIdsAsync(CancellationToken cancellationToken = default);
}
