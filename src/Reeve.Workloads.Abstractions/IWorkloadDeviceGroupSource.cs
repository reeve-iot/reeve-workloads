namespace Reeve.Workloads.Abstractions;

/// <summary>Resolves a host device path to the group id each node that sighted it reports. The host owns the
/// inventory behind it, and the port deliberately carries integers rather than the host's inventory entity.</summary>
public interface IWorkloadDeviceGroupSource
{
    /// <summary>One entry per FRESH sighting of <paramref name="devicePath"/>, in node order; empty when unsighted.
    /// A null entry is a sighting carrying no usable group id — a disagreement, never an absent sighting.</summary>
    Task<IReadOnlyList<int?>> FindDeviceGroupIdsAsync(string devicePath, CancellationToken cancellationToken);
}

/// <summary>The shipped no-op a host without a hardware inventory registers: nothing is sighted, so no slot
/// resolves a group id — already the behaviour for an unknown path, which both providers surface in preflight.</summary>
public sealed class NoWorkloadDeviceGroupSource : IWorkloadDeviceGroupSource
{
    /// <inheritdoc />
    public Task<IReadOnlyList<int?>> FindDeviceGroupIdsAsync(string devicePath, CancellationToken cancellationToken)
        => Task.FromResult<IReadOnlyList<int?>>([]);
}
