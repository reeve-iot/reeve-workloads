using System.ComponentModel.DataAnnotations;

namespace Reeve.Workloads.Core;

/// <summary>How hard the engine tries to become leader, and how often the holder proves it still is.</summary>
public sealed class WorkloadsEngineHostingOptions
{
    public const string SectionName = "Workloads:EngineHosting";

    /// <summary>How long a replica that lost the contest waits before trying again.</summary>
    [Range(typeof(TimeSpan), "00:00:00.100", "00:10:00")]
    public TimeSpan LeaderRetryInterval { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>How often the holder proves it STILL HOLDS the leadership — not merely that its session answers, which is a weaker thing. A lock-backed election asks whether this
    /// session is still the holder, so a lock released behind its back is detected within one interval. An
    /// election with nothing to probe ignores this.</summary>
    [Range(typeof(TimeSpan), "00:00:00.100", "00:10:00")]
    public TimeSpan LeadershipProbeInterval { get; set; } = TimeSpan.FromSeconds(5);
}
