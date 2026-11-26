namespace Reeve.Workloads.Abstractions;

/// <summary>The workloads side's clock. Its own type so the engine classes that take one keep a stable signature, but not one a host has to IMPLEMENT: the default reads
/// <see cref="TimeProvider"/>, which is in the BCL, so a host with an application clock registers that.</summary>
public interface IWorkloadClock
{
    /// <summary>The current UTC instant. Injected everywhere, so a test can move it.</summary>
    DateTimeOffset UtcNow { get; }
}
