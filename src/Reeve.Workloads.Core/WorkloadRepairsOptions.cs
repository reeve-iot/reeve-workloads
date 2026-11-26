using System.ComponentModel.DataAnnotations;

namespace Reeve.Workloads.Core;

/// <summary>Tunables for the leader-only Repairs escalation scanner. Every default matches the binding
/// design constant, so a stock deployment needs zero configuration.</summary>
public sealed class WorkloadRepairsOptions
{
    public const string SectionName = "Workloads:Repairs";

    /// <summary>How often the leader sweeps every instance for stuck-state / crash-loop escalation (~2 min
    /// default — frequent enough to catch a flap, cheap enough for the whole fleet).</summary>
    [Range(1, 3600)]
    public int ScanCadenceSeconds { get; init; } = 120;

    /// <summary>Grace (default 15 min), from <c>LastTransitionAtUtc</c>, before a Degraded/Failed/Unknown instance raises a
    /// stuck alert: Warning, Error for Failed. A stalled deploy (Degraded/Provisioning) raises at once.</summary>
    [Range(1, 86_400)]
    public int StuckWarningAfterSeconds { get; init; } = 15 * 60;

    /// <summary>Rise in <c>RestartCount</c> since the baseline (anchored at the LAST CLEAR, not the previous
    /// pass) that trips a crash-loop Warning. Not gated on the stuck timer.</summary>
    [Range(1, 1000)]
    public int CrashLoopRestartThreshold { get; init; } = 3;

    /// <summary>Sliding window (default 15 min) the crash-loop rise is accumulated over — needed because a real
    /// k8s CrashLoopBackOff (~1 restart/5min) never trips a single-pass delta detector at the ~2 min scan cadence.</summary>
    [Range(1, 86_400)]
    public int CrashLoopWindowSeconds { get; init; } = 15 * 60;

    /// <summary>Window (default 7 days) after which a parked Info issue escalates to Warning. Currently
    /// inert — no code reads it yet; the constant is pre-placed for a future consumer.</summary>
    [Range(1, 31_536_000)]
    public int ParkedWarningAfterSeconds { get; init; } = 7 * 24 * 60 * 60;

    /// <summary>Grace (default 10 min) before a MANUAL-provider instance in VersionSkew raises a Warning.
    /// A managed (kubernetes/docker) VersionSkew is an in-flight update and never raises this.</summary>
    [Range(1, 86_400)]
    public int ManualVersionSkewWarningAfterSeconds { get; init; } = 10 * 60;

    public TimeSpan ScanCadence => TimeSpan.FromSeconds(ScanCadenceSeconds);
    public TimeSpan StuckWarningAfter => TimeSpan.FromSeconds(StuckWarningAfterSeconds);
    public TimeSpan CrashLoopWindow => TimeSpan.FromSeconds(CrashLoopWindowSeconds);
    public TimeSpan ParkedWarningAfter => TimeSpan.FromSeconds(ParkedWarningAfterSeconds);
    public TimeSpan ManualVersionSkewWarningAfter => TimeSpan.FromSeconds(ManualVersionSkewWarningAfterSeconds);
}
