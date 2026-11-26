using Reeve.Workloads.Abstractions.Contracts.Deployment;
using Reeve.Workloads.Abstractions.Domain;
using Reeve.Workloads.Abstractions.Domain.DesiredSpec;
using Reeve.Workloads.Abstractions.Providers;
using Reeve.Workloads.Domain;

namespace Reeve.Workloads.Core;

internal static class WorkloadRequirementsFacts
{
    public static IReadOnlyDictionary<string, WorkloadPrivilegeRequirements> FromSpecs(
        IReadOnlyDictionary<string, WorkloadDesiredSpec> specs)
        => specs.ToDictionary(
            entry => entry.Key,
            entry => WorkloadPrivilegeRequirements.FromDesiredSpec(entry.Value),
            StringComparer.Ordinal);

    public static IReadOnlyDictionary<string, WorkloadPrivilegeRequirements> ForWorkload(
        string workloadId, WorkloadManifest manifest, IReadOnlyDictionary<string, string?> resolved,
        WorkloadProviderDescriptor provider)
        => new Dictionary<string, WorkloadPrivilegeRequirements>(StringComparer.Ordinal)
        {
            [workloadId] = WorkloadPrivilegeRequirements.FromWorkload(manifest, NonNull(resolved), provider),
        };

    public static string Fingerprint(IReadOnlyDictionary<string, WorkloadPrivilegeRequirements> byWorkload)
        => WorkloadPrivilegeRequirementsPolicy.Fingerprint(byWorkload);

    public static IReadOnlyDictionary<string, string> NonNull(IReadOnlyDictionary<string, string?> parameters)
        => parameters
            .Where(entry => entry.Value is not null)
            .ToDictionary(entry => entry.Key, entry => entry.Value!, StringComparer.Ordinal);
}
