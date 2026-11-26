namespace Reeve.Workloads.Abstractions;

/// <summary>What a host tells the library after its settings document changed.</summary>
public interface IWorkloadOrchestratorSettingsChanges
{
    /// <summary>Call on every replica when the document <see cref="IWorkloadOrchestratorSettingsSource"/> serves
    /// has changed. It drops the auto-detect answers, asks the leader for a reconcile pass, and restarts the
    /// watch this replica runs when the effective provider moved.</summary>
    Task ChangedAsync(CancellationToken cancellationToken = default);
}
