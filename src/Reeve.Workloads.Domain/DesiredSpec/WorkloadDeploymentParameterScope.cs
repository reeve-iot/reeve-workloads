using Reeve.Workloads.Abstractions;
using Reeve.Workloads.Abstractions.Contracts.Deployment;
using Reeve.Workloads.Abstractions.Domain;
using Reeve.Workloads.Abstractions.Domain.DesiredSpec;
using Reeve.Workloads.Abstractions.Domain.Network;
using Reeve.Workloads.Domain.Network;

namespace Reeve.Workloads.Domain.DesiredSpec;

/// <summary>Which of a flat deployment-parameter map belongs to ONE workload. Pure over the manifest, and the
/// same answer for an install and an update — a name that routes differently between them is a defect.</summary>
public static class WorkloadDeploymentParameterScope
{
    /// <summary>The keys this workload declares, plus the reserved ones it accepts: its own
    /// <c>existingClaim.{volumeId}</c> bindings, the network pair when it reserves them, and the indexed device
    /// keys a multi-slot declaration synthesizes. Everything else is dropped.</summary>
    /// <remarks>The network pair MUST be included where it is reserved, or the operator's choice never persists
    /// and the re-seed backstop has nothing to read next update.</remarks>
    public static IReadOnlyDictionary<string, string?> ForWorkload(
        WorkloadManifest workload,
        IReadOnlyDictionary<string, string?> allParameters,
        IReadOnlySet<string>? ambiguousExistingClaimKeys = null)
    {
        ArgumentNullException.ThrowIfNull(workload);
        ArgumentNullException.ThrowIfNull(allParameters);

        var declaredNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var parameter in workload.Parameters)
        {
            if (!string.IsNullOrEmpty(parameter.Name))
            {
                declaredNames.Add(parameter.Name);
            }
        }

        if (ReservesTheNetworkPair(workload))
        {
            declaredNames.Add(WorkloadNetworkAttachmentGrammar.NetworkModeParameter);
            declaredNames.Add(WorkloadNetworkAttachmentGrammar.NetworkAttachmentsParameter);
        }
        declaredNames.Add(WorkloadParameterResolver.UserNamespaceParameterName);

        var existingClaimNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var volume in workload.Volumes)
        {
            if (!string.IsNullOrEmpty(volume.Id))
            {
                existingClaimNames.Add($"existingClaim.{volume.Id}");
            }
        }

        if (workload.Privileges is { } privileges)
        {
            foreach (var device in privileges.Devices)
            {
                if (string.IsNullOrEmpty(device.Parameter) || device.MaxCount <= 1)
                {
                    continue;
                }

                for (var i = 0; i < device.MaxCount; i++)
                {
                    declaredNames.Add(WorkloadParameterResolver.DeviceIndexKey(device.Parameter, i));
                }
            }
        }

        var filtered = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var kv in allParameters)
        {
            if (ambiguousExistingClaimKeys?.Contains(kv.Key) == true)
            {
                continue;
            }

            if (declaredNames.Contains(kv.Key) || existingClaimNames.Contains(kv.Key))
            {
                filtered[kv.Key] = kv.Value;
            }
        }

        return filtered;
    }

    /// <summary>The reserved <c>existingClaim.{volumeId}</c> keys that are AMBIGUOUS — a volume id declared by
    /// more than one workload, so binding it would cross-mount both onto one claim.</summary>
    public static IReadOnlySet<string> AmbiguousExistingClaimKeys(DeploymentManifest deployment)
    {
        ArgumentNullException.ThrowIfNull(deployment);

        var countByVolumeId = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var workload in deployment.Workloads)
        {
            foreach (var volumeId in workload.Volumes
                         .Select(volume => volume.Id)
                         .Where(id => !string.IsNullOrEmpty(id))
                         .Distinct(StringComparer.Ordinal))
            {
                countByVolumeId[volumeId] = countByVolumeId.TryGetValue(volumeId, out var count) ? count + 1 : 1;
            }
        }

        return countByVolumeId
            .Where(kv => kv.Value > 1)
            .Select(kv => $"existingClaim.{kv.Key}")
            .ToHashSet(StringComparer.Ordinal);
    }

    private static bool ReservesTheNetworkPair(WorkloadManifest workload)
        => workload.Network is { } network && (network.HostNetwork || network.Attachments.Count > 0);
}
