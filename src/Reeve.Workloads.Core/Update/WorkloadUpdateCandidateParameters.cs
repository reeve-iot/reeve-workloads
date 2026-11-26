using Reeve.Workloads.Abstractions.Domain.DesiredSpec;
using Reeve.Workloads.Abstractions.Contracts.Deployment;
using Reeve.Workloads.Domain.DesiredSpec;

namespace Reeve.Workloads.Core.Update;

internal static class WorkloadUpdateCandidateParameters
{
    internal static IReadOnlyDictionary<string, string?> Merge(
        WorkloadManifest workload,
        IReadOnlyDictionary<string, string?> stored,
        IReadOnlyDictionary<string, string?>? submitted,
        IReadOnlySet<string>? ambiguousKeys)
    {
        if (submitted is null || submitted.Count == 0)
        {
            return stored;
        }

        var forWorkload = WorkloadDeploymentParameterScope.ForWorkload(workload, submitted, ambiguousKeys);
        if (forWorkload.Count == 0)
        {
            return stored;
        }

        var unmasked = WorkloadParameterMaskPolicy.RetainMaskedValues(stored, forWorkload);
        var merged = new Dictionary<string, string?>(stored, StringComparer.Ordinal);
        foreach (var (key, value) in unmasked)
        {
            if (key.StartsWith("existingClaim.", StringComparison.Ordinal))
            {
                continue;
            }

            merged[key] = value;
        }

        return merged;
    }
}
