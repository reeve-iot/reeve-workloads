using Reeve.Workloads.Abstractions;

namespace Reeve.Workloads.Core.Tests;

internal sealed class FakeDeviceGroupSource : IWorkloadDeviceGroupSource
{
    private readonly Dictionary<string, List<int?>> byPath = new(StringComparer.Ordinal);

    public void With(string devicePath, int? groupId)
    {
        if (!byPath.TryGetValue(devicePath, out var sightings))
        {
            sightings = [];
            byPath[devicePath] = sightings;
        }

        sightings.Add(groupId);
    }

    public Task<IReadOnlyList<int?>> FindDeviceGroupIdsAsync(
        string devicePath, CancellationToken cancellationToken)
        => Task.FromResult<IReadOnlyList<int?>>(
            byPath.TryGetValue(devicePath, out var sightings) ? sightings : []);
}
