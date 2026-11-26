using System.ComponentModel.DataAnnotations;

namespace Reeve.Workloads.Core;

/// <summary>Tunables for the retained-volume informational scan, bound from <c>Workloads:RetainedScan</c>. The
/// defaults are the shipped constants, so a stock deployment needs no configuration.</summary>
public sealed class WorkloadRetainedScanOptions
{
    public const string SectionName = "Workloads:RetainedScan";

    /// <summary>Age threshold in days: a retained volume older than this surfaces in the informational Repairs
    /// item.</summary>
    [Range(1, 3650)]
    public int ThresholdDays { get; init; } = 90;

    /// <summary>Scan cadence in hours. Retained volumes never expire, so there is no need to sweep often.</summary>
    [Range(1, 168)]
    public int ScanIntervalHours { get; init; } = 6;

    /// <summary>The age threshold as a <see cref="TimeSpan"/>.</summary>
    public TimeSpan Threshold => TimeSpan.FromDays(ThresholdDays);

    /// <summary>The scan cadence as a <see cref="TimeSpan"/>.</summary>
    public TimeSpan ScanInterval => TimeSpan.FromHours(ScanIntervalHours);
}
