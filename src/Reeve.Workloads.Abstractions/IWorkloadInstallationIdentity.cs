namespace Reeve.Workloads.Abstractions;

/// <summary>Resolves THIS installation's stable id, stamped into the <c>reeve.io/installation</c> label
/// on every materialized workload object. MUST stay stable across restarts/migrations — the DR adoption path uses it to tell this installation's retained resources from a foreign one's.</summary>
public interface IWorkloadInstallationIdentity
{
    /// <summary>Returns this installation's stable id (lowercase GUID), minting and persisting one on
    /// first read. Never null/empty; idempotent across repeated calls.</summary>
    Task<string> GetInstallationIdAsync(CancellationToken cancellationToken = default);
}
