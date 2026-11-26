using Reeve.Workloads.Domain;

namespace Reeve.Workloads.Core;

internal static class WorkloadParameterEditMerge
{
    public static Dictionary<string, string?> Merge(
        IReadOnlyDictionary<string, string?> stored, IReadOnlyDictionary<string, string?> newValues)
    {
        ArgumentNullException.ThrowIfNull(stored);
        ArgumentNullException.ThrowIfNull(newValues);

        var posted = WorkloadParameterMaskPolicy.RetainMaskedValues(stored, newValues);

        var merged = new Dictionary<string, string?>(stored, StringComparer.Ordinal);
        foreach (var (key, value) in posted)
        {
            merged[key] = value;
        }

        foreach (var key in newValues.Keys)
        {
            if (!posted.ContainsKey(key))
            {
                merged.Remove(key);
            }
        }

        return merged;
    }
}
