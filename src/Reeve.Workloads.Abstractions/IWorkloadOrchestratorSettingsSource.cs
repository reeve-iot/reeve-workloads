using Reeve.Workloads.Abstractions.Contracts;

namespace Reeve.Workloads.Abstractions;

/// <summary>The orchestrator settings document this replica serves, in plaintext. The host owns it: it loads it
/// from wherever it keeps it, protects its secrets at rest, and hands the library the result.</summary>
/// <remarks>
/// Required: a host with nothing to supply registers a fixed document explicitly. The library reads it on every
/// settings read, reconcile and probe, so an implementation answers from memory and refreshes that copy when its
/// own storage changes; it then calls <see cref="IWorkloadOrchestratorSettingsChanges.ChangedAsync"/>.
/// <c>ReconciliationPaused</c> is state, not configuration, and the host composes it into the document.
/// </remarks>
public interface IWorkloadOrchestratorSettingsSource
{
    /// <summary>The document to serve. Never null; an empty document means all defaults.</summary>
    ValueTask<WorkloadOrchestratorSettings> GetAsync(CancellationToken cancellationToken = default);
}
