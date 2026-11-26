using Reeve.Workloads.Abstractions;
using Reeve.Workloads.Abstractions.Contracts;
using Reeve.Workloads.Abstractions.Contracts.Deployment;
using Reeve.Workloads.Abstractions.Domain;
using Reeve.Workloads.Abstractions.Domain.DesiredSpec;
using Reeve.Workloads.Abstractions.Domain.Network;
using Reeve.Workloads.Domain.DesiredSpec;
using Reeve.Workloads.Domain.Network;

namespace Reeve.Workloads.Domain;

/// <summary>How a <see cref="WorkloadDeploymentDiff"/> is computed from two deployments. The record is the shape the
/// update preview reads; the computation is the domain's.</summary>
public static class WorkloadDeploymentDiffPolicy
{
    extension(WorkloadDeploymentDiff)
    {
        public static WorkloadDeploymentDiff Compute(
            DeploymentManifest previous,
            IReadOnlyDictionary<string, WorkloadPrivilegeRequirements> previousRequirements,
            IReadOnlyDictionary<string, IReadOnlyDictionary<string, string?>> previousResolvedParameters,
            DeploymentManifest candidate,
            IReadOnlyDictionary<string, WorkloadPrivilegeRequirements> candidateRequirements,
            bool previousUnreadable = false)
        {
            ArgumentNullException.ThrowIfNull(previous);
            ArgumentNullException.ThrowIfNull(previousRequirements);
            ArgumentNullException.ThrowIfNull(previousResolvedParameters);
            ArgumentNullException.ThrowIfNull(candidate);
            ArgumentNullException.ThrowIfNull(candidateRequirements);

            var previousById = IndexWorkloads(previous);
            var candidateById = IndexWorkloads(candidate);

            var added = candidateById.Keys
                .Where(id => !previousById.ContainsKey(id))
                .OrderBy(id => id, StringComparer.Ordinal)
                .ToList();

            var removed = previousById.Keys
                .Where(id => !candidateById.ContainsKey(id))
                .OrderBy(id => id, StringComparer.Ordinal)
                .ToList();

            var perWorkload = new List<WorkloadDiff>();

            foreach (var id in candidateById.Keys.OrderBy(k => k, StringComparer.Ordinal))
            {
                var candWorkload = candidateById[id];

                var candProfile = candidateRequirements.TryGetValue(id, out var cp)
                    ? cp
                    : WorkloadPrivilegeRequirements.FromWorkload(candWorkload, EmptyResolved);

                if (!previousById.TryGetValue(id, out var prevWorkload))
                {
                    var addedNewRequired = ComputeNewRequiredParameters(candWorkload, previousResolvedValues: null);

                    var addedProfileDiff = new WorkloadPrivilegeRequirementsDiff
                    {
                        Security = RequirementChangeKind.Unchanged,
                        EscalatedFields = Array.Empty<string>(),
                        NarrowedFields = Array.Empty<string>(),
                        InformationalVolumeChange = false,
                        VolumeNotes = Array.Empty<string>(),
                    };

                    perWorkload.Add(new WorkloadDiff
                    {
                        WorkloadId = id,
                        IsAdded = true,
                        FromDigest = null,
                        ToDigest = candWorkload.Image.Digest,
                        RequirementsDiff = addedProfileDiff,
                        FromExtensions = null,
                        ToExtensions = candWorkload.Extensions,
                        PreviousUnreadable = previousUnreadable,
                        NewRequiredParameters = addedNewRequired,
                        FromResources = null,
                        ToResources = DeclaredResources(candWorkload),
                        ResourcesChanged = false,
                        OperatorResourceOverrides = null,
                    });
                    continue;
                }

                var previousProfile = previousRequirements.TryGetValue(id, out var prevRequirements)
                    ? prevRequirements
                    : WorkloadPrivilegeRequirements.FromWorkload(prevWorkload, EmptyResolved);

                var profileDiff = WorkloadPrivilegeRequirements.Compare(previousProfile, candProfile);

                var prevValues = previousResolvedParameters.TryGetValue(id, out var pv) ? pv : null;
                var newRequired = ComputeNewRequiredParameters(candWorkload, prevValues);

                var fromResources = DeclaredResources(prevWorkload);
                var toResources = DeclaredResources(candWorkload);

                perWorkload.Add(new WorkloadDiff
                {
                    WorkloadId = id,
                    IsAdded = false,
                    FromDigest = prevWorkload.Image.Digest,
                    ToDigest = candWorkload.Image.Digest,
                    RequirementsDiff = profileDiff,
                    FromExtensions = previousUnreadable ? WorkloadExtensions.None : prevWorkload.Extensions,
                    ToExtensions = candWorkload.Extensions,
                    PreviousUnreadable = previousUnreadable,
                    NewRequiredParameters = newRequired,
                    FromResources = fromResources,
                    ToResources = toResources,
                    ResourcesChanged = ResourcesMoved(fromResources, toResources),
                    OperatorResourceOverrides = StoredResourceOverrides(prevValues, candWorkload),
                });
            }

            return new WorkloadDeploymentDiff
            {
                Workloads = perWorkload,
                AddedWorkloadIds = added,
                RemovedWorkloadIds = removed,
            };
        }

        public static IReadOnlyList<string> MovedResourceAxisParameters(
            WorkloadManifest? previous, WorkloadManifest? candidate)
        {
            if (previous is null || candidate is null)
            {
                return [];
            }

            var from = DeclaredResources(previous);
            var to = DeclaredResources(candidate);
            var moved = new List<string>();

            if (AxisMoved(ResourceQuantityKind.Cpu, from?.RequestsCpu, to?.RequestsCpu))
                moved.Add("resources.requests.cpu");
            if (AxisMoved(ResourceQuantityKind.Memory, from?.RequestsMemory, to?.RequestsMemory))
                moved.Add("resources.requests.memory");
            if (AxisMoved(ResourceQuantityKind.Cpu, from?.LimitsCpu, to?.LimitsCpu))
                moved.Add("resources.limits.cpu");
            if (AxisMoved(ResourceQuantityKind.Memory, from?.LimitsMemory, to?.LimitsMemory))
                moved.Add("resources.limits.memory");

            return moved;
        }
    }

    private static ResolvedResources? DeclaredResources(WorkloadManifest workload)
    {
        var r = workload.Resources;
        if (r is null)
        {
            return null;
        }

        return new ResolvedResources
        {
            RequestsCpu = r.Requests?.Cpu,
            RequestsMemory = r.Requests?.Memory,
            LimitsCpu = r.Limits?.Cpu,
            LimitsMemory = r.Limits?.Memory,
        };
    }

    private static ResolvedResources? StoredResourceOverrides(
        IReadOnlyDictionary<string, string?>? values, WorkloadManifest candidate)
    {
        if (values is null)
        {
            return null;
        }

        string? Read(string key)
            => !candidate.Parameters.Any(p => string.Equals(p.Name, key, StringComparison.Ordinal))
                && values.TryGetValue(key, out var v) && !string.IsNullOrWhiteSpace(v) ? v.Trim() : null;

        var overrides = new ResolvedResources
        {
            RequestsCpu = Read("resources.requests.cpu"),
            RequestsMemory = Read("resources.requests.memory"),
            LimitsCpu = Read("resources.limits.cpu"),
            LimitsMemory = Read("resources.limits.memory"),
        };

        return overrides.RequestsCpu is null && overrides.RequestsMemory is null
            && overrides.LimitsCpu is null && overrides.LimitsMemory is null
                ? null
                : overrides;
    }

    private static bool ResourcesMoved(ResolvedResources? from, ResolvedResources? to)
        => AxisMoved(ResourceQuantityKind.Cpu, from?.RequestsCpu, to?.RequestsCpu)
            || AxisMoved(ResourceQuantityKind.Memory, from?.RequestsMemory, to?.RequestsMemory)
            || AxisMoved(ResourceQuantityKind.Cpu, from?.LimitsCpu, to?.LimitsCpu)
            || AxisMoved(ResourceQuantityKind.Memory, from?.LimitsMemory, to?.LimitsMemory);

    private static bool AxisMoved(ResourceQuantityKind kind, string? from, string? to)
    {
        var fromSet = ResourceQuantityPolicy.TryParseForRender(kind, from, out var fromUnits);
        var toSet = ResourceQuantityPolicy.TryParseForRender(kind, to, out var toUnits);
        if (!fromSet && !toSet)
        {
            return false;
        }

        return fromSet != toSet || fromUnits != toUnits;
    }

    private static readonly IReadOnlyDictionary<string, string> EmptyResolved =
        new Dictionary<string, string>(StringComparer.Ordinal);

    private static Dictionary<string, WorkloadManifest> IndexWorkloads(DeploymentManifest manifest)
    {
        var byId = new Dictionary<string, WorkloadManifest>(StringComparer.Ordinal);
        foreach (var w in manifest.Workloads)
        {
            if (!string.IsNullOrEmpty(w.Id))
                byId[w.Id] = w;
        }
        return byId;
    }

    private static IReadOnlyList<string> ComputeNewRequiredParameters(
        WorkloadManifest candidate,
        IReadOnlyDictionary<string, string?>? previousResolvedValues)
    {
        var unsatisfied = new SortedSet<string>(StringComparer.Ordinal);

        foreach (var p in candidate.Parameters)
        {
            if (string.IsNullOrEmpty(p.Name) || !p.Required)
                continue;

            if (!string.IsNullOrWhiteSpace(p.Default))
                continue;

            if (previousResolvedValues is not null
                && previousResolvedValues.TryGetValue(p.Name, out var prior)
                && !string.IsNullOrWhiteSpace(prior))
            {
                continue;
            }

            unsatisfied.Add(p.Name);
        }

        return unsatisfied.ToList();
    }
}
