using Reeve.Workloads.Abstractions.Contracts.Deployment;
using Reeve.Workloads.Abstractions.Domain;
using Reeve.Workloads.Domain.DesiredSpec;

namespace Reeve.Workloads.Core;

internal static class WorkloadPasswordParameterChanges
{
    public static bool HasChanged(
        WorkloadManifest manifest,
        IReadOnlyDictionary<string, string?> stored,
        IReadOnlyDictionary<string, string?> updated,
        WorkloadParameterSecretCodec codec)
    {
        IReadOnlyDictionary<string, string?> originalValues;
        try { originalValues = codec.Unprotect(manifest, stored); }
        catch (WorkloadParameterResolutionException) { return true; }
        IReadOnlyDictionary<string, string?> updatedValues;
        try { updatedValues = codec.Unprotect(manifest, updated); }
        catch (WorkloadParameterResolutionException) { updatedValues = updated; }
        foreach (var parameter in manifest.Parameters)
        {
            if (parameter.Type != WorkloadParameterType.Password)
            {
                continue;
            }

            originalValues.TryGetValue(parameter.Name, out var original);
            updatedValues.TryGetValue(parameter.Name, out var next);
            original = string.IsNullOrWhiteSpace(original) ? null : original;
            next = string.IsNullOrWhiteSpace(next) ? null : next;
            if (!string.Equals(original, next, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }
}
