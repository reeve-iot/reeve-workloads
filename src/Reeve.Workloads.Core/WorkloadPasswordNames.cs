using Reeve.Workloads.Abstractions.Contracts.Deployment;

namespace Reeve.Workloads.Core;

internal static class WorkloadPasswordNames
{
    internal static IReadOnlySet<string> For(WorkloadManifest manifest, IEnumerable<string>? snapshotted = null)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        return snapshotted?.ToHashSet(StringComparer.Ordinal)
            ?? manifest.Parameters.Where(parameter => parameter.Type == WorkloadParameterType.Password)
                .Select(parameter => parameter.Name).ToHashSet(StringComparer.Ordinal);
    }
}
