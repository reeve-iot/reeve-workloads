using Reeve.Workloads.Abstractions.Contracts;

namespace Reeve.Workloads.Core.Watch;

/// <summary>A single provider's watch stream, hosted provider-agnostically by the leader-only owned-unit. Start
/// and Stop MUST be idempotent and must report a transient as Degraded health rather than throwing.</summary>
public interface IWorkloadWatchSource
{
    /// <summary>The provider this source watches — the discriminator under which its health is tracked.</summary>
    WorkloadProviderId Provider { get; }

    /// <summary>Start streaming into <paramref name="sink"/>. Must return promptly after wiring the background
    /// stream rather than blocking on it.</summary>
    Task StartAsync(IWorkloadWatchSink sink, CancellationToken cancellationToken);

    /// <summary>Stop streaming and release the connection. Idempotent.</summary>
    Task StopAsync(CancellationToken cancellationToken);
}

/// <summary>The narrow surface a <see cref="IWorkloadWatchSource"/> raises into. Every member must stay cheap,
/// non-blocking and thread-safe — a source calls them from its stream callback on any thread.</summary>
public interface IWorkloadWatchSink
{
    /// <summary>A watch event mapped to ONE instance GUID by label — schedules a debounced reconcile, coalesced
    /// per instance per window.</summary>
    void InstanceDirty(Guid instanceId);

    /// <summary>A re-list or 410-Gone resync — schedules a debounced reconcile-ALL, which subsumes any
    /// per-instance marks in the same window.</summary>
    void AllDirty();

    /// <summary>Report this provider's watch health. Diagnostic only: the reconcile cadence does not depend on it.</summary>
    void ReportHealth(WorkloadProviderId provider, WorkloadWatchHealth health);
}

/// <summary>A provider watch's liveness. <see cref="Degraded"/> is the zero value so a never-reported provider
/// is treated as NOT live until a source affirmatively reports <see cref="Connected"/>.</summary>
public enum WorkloadWatchHealth
{
    /// <summary>The watch is not live (disconnected / stream ended / 410 / endpoint change / unreachable / never started). Fail-safe default.</summary>
    Degraded = 0,

    /// <summary>The watch stream is confirmed live — events are flowing for this provider.</summary>
    Connected = 1,
}
