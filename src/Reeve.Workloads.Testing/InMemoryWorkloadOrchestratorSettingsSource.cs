using Reeve.Workloads.Abstractions;
using Reeve.Workloads.Abstractions.Contracts;

namespace Reeve.Workloads.Testing;

/// <summary>An <see cref="IWorkloadOrchestratorSettingsSource"/> that serves a document held in memory, for test
/// and sample hosts that keep their settings nowhere else.</summary>
/// <remarks>Assigning <see cref="Document"/> does not tell the library; call
/// <see cref="IWorkloadOrchestratorSettingsChanges.ChangedAsync"/> afterwards, as a real host does.</remarks>
public sealed class InMemoryWorkloadOrchestratorSettingsSource : IWorkloadOrchestratorSettingsSource
{
    private volatile WorkloadOrchestratorSettings document;

    /// <summary>A source serving <paramref name="document"/>, or an empty document (all defaults) when none is given.</summary>
    public InMemoryWorkloadOrchestratorSettingsSource(WorkloadOrchestratorSettings? document = null)
        => this.document = document ?? new WorkloadOrchestratorSettings();

    /// <summary>The document served. Never null.</summary>
    public WorkloadOrchestratorSettings Document
    {
        get => document;
        set => document = value ?? throw new ArgumentNullException(nameof(value));
    }

    /// <inheritdoc />
    public ValueTask<WorkloadOrchestratorSettings> GetAsync(CancellationToken cancellationToken = default)
        => ValueTask.FromResult(document);
}
