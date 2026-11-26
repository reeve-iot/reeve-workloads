using System.Globalization;
using Reeve.Workloads.Abstractions;
using Reeve.Workloads.Abstractions.Contracts.Deployment;
using Reeve.Workloads.Abstractions.Domain.DesiredSpec;
using Reeve.Workloads.Domain.DesiredSpec;

namespace Reeve.Workloads.Core;

/// <summary>The ONLY writer of the reserved <c>{deviceKey}:gid</c> values: it turns each <c>group: auto</c> slot's
/// bound device path into the owning group id the node hardware inventory recorded for it. A client can never supply one — <see cref="WorkloadParameterAcceptList"/> refuses a submitted ':' name.</summary>
public sealed class WorkloadDeviceGroupIdWriter
{
    private readonly IWorkloadDeviceGroupSource groups;

    public WorkloadDeviceGroupIdWriter(IWorkloadDeviceGroupSource groups) => this.groups = groups;

    /// <summary>Returns <paramref name="operatorValues"/> with every group-id key of every device slot rewritten:
    /// set when the bound path resolves to one agreed group id, and CLEARED otherwise.</summary>
    public async Task<Dictionary<string, string?>> ApplyAsync(
        WorkloadManifest manifest,
        IReadOnlyDictionary<string, string?> operatorValues,
        IReadOnlyDictionary<string, string?> resolvedParameters,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(operatorValues);
        ArgumentNullException.ThrowIfNull(resolvedParameters);

        var result = new Dictionary<string, string?>(operatorValues, StringComparer.Ordinal);
        if (manifest.Privileges is not { } privileges)
        {
            return result;
        }

        foreach (var device in privileges.Devices)
        {
            if (string.IsNullOrEmpty(device.Parameter))
            {
                continue;
            }

            IEnumerable<string> keys = device.MaxCount > 1
                ? Enumerable.Range(0, device.MaxCount).Select(i => WorkloadParameterResolver.DeviceIndexKey(device.Parameter, i))
                : new[] { device.Parameter };

            foreach (var key in keys)
            {
                var groupIdKey = WorkloadParameterResolver.DeviceGroupIdKey(key);
                result.Remove(groupIdKey);

                if (device.Group != WorkloadDeviceGroup.Auto)
                {
                    continue;
                }

                if (!resolvedParameters.TryGetValue(key, out var path) || string.IsNullOrWhiteSpace(path))
                {
                    continue;
                }

                var groupId = await LookUpGroupIdAsync(path.Trim(), cancellationToken).ConfigureAwait(false);
                if (groupId is { } gid)
                {
                    result[groupIdKey] = gid.ToString(CultureInfo.InvariantCulture);
                }
            }
        }

        return result;
    }

    private async Task<int?> LookUpGroupIdAsync(string devicePath, CancellationToken cancellationToken)
    {
        var sightings = await groups.FindDeviceGroupIdsAsync(devicePath, cancellationToken).ConfigureAwait(false);
        if (sightings.Count == 0)
        {
            return null;
        }

        int? agreed = null;
        foreach (var groupId in sightings)
        {
            if (groupId is null || (agreed is { } already && already != groupId.Value))
            {
                return null;
            }

            agreed = groupId;
        }

        return agreed;
    }
}
