using Reeve.Workloads.Abstractions.Domain.DesiredSpec;

namespace Reeve.Workloads.Abstractions.Domain.Entities;

/// <summary>Everything an update intent snapshots when it stages, as values rather than as serialized columns. The
/// intent serializes them itself, so a caller does not have to know which settings each column was written with.</summary>
public sealed record WorkloadUpdateSnapshots
{
    /// <summary>The rendered desired spec of every workload the candidate will deploy, by workload id.</summary>
    public IReadOnlyDictionary<string, WorkloadDesiredSpec>? TargetSpecs { get; init; }

    /// <summary>The same for the version being replaced; a workload the candidate adds has no entry.</summary>
    public IReadOnlyDictionary<string, WorkloadDesiredSpec>? PreviousSpecs { get; init; }

    /// <summary>Each workload's resolved parameters before the update, for the rollback restore. Holds
    /// password-typed values, which is why a settled intent drops it.</summary>
    public IReadOnlyDictionary<string, IReadOnlyDictionary<string, string?>>? PreviousParameters { get; init; }

    /// <summary>Password parameter names from each previous manifest, needed to protect rollback writes.</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<string>> PreviousPasswordParameterNames { get; init; }
        = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);

    /// <summary>Password parameter names in each candidate schema, so active replay can decode the same candidate snapshot.</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<string>> CandidatePasswordParameterNames { get; init; }
        = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);

    /// <summary>Image digest per workload before the update.</summary>
    public IReadOnlyDictionary<string, string?>? FromDigests { get; init; }

    /// <summary>Image digest per workload after it.</summary>
    public IReadOnlyDictionary<string, string?>? ToDigests { get; init; }

    /// <summary>Parameter values the operator submitted with the update, replayed on every tick because the
    /// orchestrator rebuilds the plan each time.</summary>
    public IReadOnlyDictionary<string, string?>? OperatorParameters { get; init; }

    /// <summary>Declared resource workloads the operator agreed to adopt, carried for the same reason.</summary>
    public IReadOnlyList<string>? AdoptedResourceWorkloadIds { get; init; }
}
