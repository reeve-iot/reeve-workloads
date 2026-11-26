using Reeve.Workloads.Abstractions.Domain.Entities;

namespace Reeve.Workloads.Abstractions;

/// <summary>The default no-op notifier, so non-Api hosts and unit tests need no SignalR dependency; the
/// Api host replaces it with the SignalR-backed implementation.</summary>
public sealed class NullWorkloadStatusRealtimeNotifier : IWorkloadStatusRealtimeNotifier
{
    public static readonly NullWorkloadStatusRealtimeNotifier Instance = new();

    public Task NotifyStatusChangedAsync(WorkloadInstance instance, CancellationToken cancellationToken = default)
        => Task.CompletedTask;
}
