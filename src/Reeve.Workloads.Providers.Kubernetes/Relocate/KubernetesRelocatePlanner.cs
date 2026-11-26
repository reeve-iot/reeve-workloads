using System;
using System.Collections.Generic;
using Reeve.Workloads.Abstractions.Contracts.Deployment;
using Reeve.Workloads.Core.Relocate;

namespace Reeve.Workloads.Providers.Kubernetes.Relocate;

internal static class KubernetesRelocatePlanner
{
    public static RelocatePlan Plan(
        Guid instanceId,
        IReadOnlyDictionary<string, string> newNodeSelector,
        IReadOnlyList<RelocateVolumeFact> volumes)
    {
        ArgumentNullException.ThrowIfNull(newNodeSelector);
        ArgumentNullException.ThrowIfNull(volumes);

        var nodeBound = new List<NodeBoundVolume>();
        var adoptedBlocking = new List<NodeBoundVolume>();
        var unverified = new List<UnverifiedVolume>();
        foreach (var v in volumes)
        {
            if (v.BindingUnknown || (!string.IsNullOrWhiteSpace(v.BoundNode) && v.BoundNodeLabels is null))
            {
                unverified.Add(new UnverifiedVolume
                {
                    VolumeId = v.VolumeId,
                    Criticality = v.Criticality,
                    Description = v.Description ?? string.Empty,
                    PvcName = v.PvcName,
                });
                continue;
            }

            if (string.IsNullOrWhiteSpace(v.BoundNode)
                || v.BoundNodeLabels is null
                || NewSelectorAdmits(v.BoundNodeLabels, newNodeSelector))
            {
                continue;
            }

            var descriptor = new NodeBoundVolume
            {
                VolumeId = v.VolumeId,
                Criticality = v.Criticality,
                Description = v.Description ?? string.Empty,
                PvcName = v.PvcName,
                BoundNode = v.BoundNode,
            };

            (v.IsAdoptedClaim ? adoptedBlocking : nodeBound).Add(descriptor);
        }

        return new RelocatePlan
        {
            InstanceId = instanceId,
            NewNodeSelector = new Dictionary<string, string>(newNodeSelector, StringComparer.Ordinal),
            NodeBoundVolumes = nodeBound,
            AdoptedVolumesBlockingRelocate = adoptedBlocking,
            UnverifiedVolumes = unverified,
        };
    }

    private static bool NewSelectorAdmits(
        IReadOnlyDictionary<string, string> nodeLabels,
        IReadOnlyDictionary<string, string> newNodeSelector)
    {
        foreach (var (key, value) in newNodeSelector)
        {
            if (!nodeLabels.TryGetValue(key, out var actual) || !string.Equals(actual, value, StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }
}

internal sealed record RelocateVolumeFact
{
    public required string VolumeId { get; init; }

    public required VolumeCriticality Criticality { get; init; }

    public string? Description { get; init; }

    public required string PvcName { get; init; }

    public string? BoundNode { get; init; }

    public IReadOnlyDictionary<string, string>? BoundNodeLabels { get; init; }

    public bool IsAdoptedClaim { get; init; }

    public bool BindingUnknown { get; init; }
}
