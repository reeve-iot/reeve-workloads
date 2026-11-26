using Reeve.Workloads.Abstractions;

namespace Reeve.Workloads.Core;

/// <summary>Real time, from <see cref="TimeProvider"/>. Not a no-op standing in for behaviour a host owes:
/// a host with no clock of its own gets the right answer, and one with a test clock still overrides.</summary>
public sealed class SystemWorkloadClock : IWorkloadClock
{
    private readonly TimeProvider timeProvider;

    public SystemWorkloadClock()
        : this(TimeProvider.System)
    {
    }

    public SystemWorkloadClock(TimeProvider timeProvider)
        => this.timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));

    /// <inheritdoc />
    public DateTimeOffset UtcNow => timeProvider.GetUtcNow();
}
