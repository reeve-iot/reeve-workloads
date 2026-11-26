using System.ComponentModel.DataAnnotations;

namespace Reeve.Workloads.Core.Watch;

/// <summary>Tunables for the leader-only workload watcher, bound from <c>Workloads:Watch</c>. The on/off
/// toggle and drift cadence live on the reconciler options; this holds only the watcher-local pump tunable.</summary>
public sealed class WorkloadWatchOptions
{
    public const string SectionName = "Workloads:Watch";

    /// <summary>Per-instance debounce window in milliseconds: long enough to collapse the flurry of events one
    /// apply provokes into a single reconcile, short enough that convergence stays prompt.</summary>
    [Range(0, 60_000)]
    public int DebounceMilliseconds { get; init; } = 250;

    /// <summary>The debounce window as a <see cref="TimeSpan"/> for the coalescer.</summary>
    public TimeSpan DebounceWindow => TimeSpan.FromMilliseconds(DebounceMilliseconds);
}
