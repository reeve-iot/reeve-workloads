using Reeve.Workloads.Abstractions;
using Reeve.Workloads.Abstractions.Contracts.Deployment;
using Reeve.Workloads.Abstractions.Domain;
using Reeve.Workloads.Abstractions.Domain.DesiredSpec;

namespace Reeve.Workloads.Domain.DesiredSpec;

/// <summary>The ONE answer to "is this required parameter filled in?" for a parameter that backs a device slot.
/// Shared so the reconciler's authoritative gate and the lifecycle service's state message cannot diverge.</summary>
public static class WorkloadDeviceSlotSatisfaction
{
    /// <summary>True when the parameter has a value the desired-spec factory would RENDER: a multi-device slot
    /// reads its indexed keys only (its declared key is a phantom), anything else falls back to its own key.</summary>
    public static bool IsSatisfied(
        WorkloadManifest workload,
        string parameterName,
        IReadOnlyDictionary<string, string?> resolved)
    {
        ArgumentNullException.ThrowIfNull(workload);
        ArgumentNullException.ThrowIfNull(resolved);

        var slot = workload.Privileges?.Devices
            .FirstOrDefault(d => string.Equals(d.Parameter, parameterName, StringComparison.Ordinal));

        if (slot is null || slot.MaxCount <= 1)
        {
            return HasValue(resolved, parameterName);
        }

        for (var i = 0; i < slot.MaxCount; i++)
        {
            if (HasValue(resolved, WorkloadParameterResolver.DeviceIndexKey(parameterName, i)))
            {
                return true;
            }
        }

        return false;
    }

    private static bool HasValue(IReadOnlyDictionary<string, string?> resolved, string key)
        => resolved.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value);
}
