using Reeve.Workloads.Abstractions.Domain.Entities;

namespace Reeve.Workloads.Abstractions;

/// <summary>SignalR-facing notifier for workload-instance status changes, emitted from the single persistence choke
/// point (<c>IWorkloadInstanceRepository.UpdateAsync</c>) so every transition surfaces uniformly with no per-call-site wiring.</summary>
public interface IWorkloadStatusRealtimeNotifier
{
    /// <summary>Pushes a status-changed notification for <paramref name="instance"/>. Must not throw into the
    /// persistence path — implementations swallow transport failures (the next read reconciles).</summary>
    Task NotifyStatusChangedAsync(WorkloadInstance instance, CancellationToken cancellationToken = default);
}
