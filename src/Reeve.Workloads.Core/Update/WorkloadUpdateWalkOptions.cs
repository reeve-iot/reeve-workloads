using System.ComponentModel.DataAnnotations;

namespace Reeve.Workloads.Core.Update;

/// <summary>The update walk's rhythm . A stage that answers <c>Done</c> hands straight to the next in the same
/// tick; these say how soon a stage that answered <c>Working</c> is asked again, and how long the walk sleeps when
/// nothing is open.</summary>
public sealed class WorkloadUpdateWalkOptions
{
    public const string SectionName = "Workloads:UpdateWalk";

    /// <summary>The longest a <c>Working</c> stage waits to be asked again, in seconds. Seconds, not minutes: an
    /// update is somebody waiting on it, and a stage that knows when it is done wakes the walk sooner itself.</summary>
    [Range(1, 300)]
    public int WorkingTickSeconds { get; init; } = 5;

    /// <summary>How long the walk sleeps with no open intent, in seconds. An initiation wakes it at once; this only
    /// bounds how long an intent can wait when the nudge that should have woken the walk was lost.</summary>
    [Range(1, 3600)]
    public int IdleFallbackSeconds { get; init; } = 60;

    public TimeSpan WorkingTickInterval => TimeSpan.FromSeconds(WorkingTickSeconds);

    public TimeSpan IdleFallbackInterval => TimeSpan.FromSeconds(IdleFallbackSeconds);
}
