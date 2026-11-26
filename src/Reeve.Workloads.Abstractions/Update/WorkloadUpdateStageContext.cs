using Reeve.Workloads.Abstractions.Domain.DesiredSpec;
using Reeve.Workloads.Abstractions.Domain.Entities;

namespace Reeve.Workloads.Abstractions.Update;

/// <summary>One workload as a stage sees it: its id, the spec it is moving to, and the spec it is moving from
/// (<c>null</c> for a workload the candidate manifest adds). Declaration order is the enclosing
/// <see cref="WorkloadUpdateStageContext.Workloads"/> list's, so a stage applies forwards and compensates in
/// reverse without re-deriving an order.</summary>
public sealed record WorkloadUpdateStageWorkload
{
    public required string WorkloadId { get; init; }

    /// <summary>The spec this workload ran before the update, or <c>null</c> when the candidate adds it.</summary>
    public required WorkloadDesiredSpec? PreviousSpec { get; init; }

    public required WorkloadDesiredSpec TargetSpec { get; init; }
}

/// <summary>What a stage is given on every call, forwards and backwards. This is the whole of what this library
/// tells a stage, and deliberately so: it is a group of workloads moving between two declared versions, and
/// nothing in it names a module, a plugin, an assembly, a loaded version or a replica.
///
/// A host that registers its own stage resolves whatever it loads from
/// <see cref="GroupId"/> and <see cref="SourceKey"/> itself. The library does not know that anything is loaded.</summary>
public sealed record WorkloadUpdateStageContext
{
    /// <summary>The update intent this walk belongs to.</summary>
    public required Guid IntentId { get; init; }

    /// <summary>The group row whose atomic upgrade this is — the active-intent uniqueness key.</summary>
    public required Guid GroupInstanceId { get; init; }

    /// <summary>The group's declared id. Two ids are carried rather than one because they answer different
    /// questions: <see cref="GroupInstanceId"/> identifies the row, this identifies what the group IS, and a
    /// host stage addressing its own work needs the latter.</summary>
    public required string GroupId { get; init; }

    /// <summary>The host's key for where this group comes from, as the group row records it. Opaque here.</summary>
    public required string SourceKey { get; init; }

    /// <summary>The version being moved FROM, or <c>null</c> (for example away from an adopted pre-Workloads
    /// image, which has no declared version to name).</summary>
    public string? FromVersion { get; init; }

    /// <summary>The version being moved TO.</summary>
    public required string ToVersion { get; init; }

    /// <summary>This intent's order within a cohort, or <c>null</c> for a single intent. It carries no sibling
    /// progress.</summary>
    public WorkloadUpdateCohortPlace? Cohort { get; init; }

    /// <summary>The group's workloads in manifest DECLARATION ORDER: apply forwards, compensate in reverse.</summary>
    public required IReadOnlyList<WorkloadUpdateStageWorkload> Workloads { get; init; }

    internal WorkloadUpdateStageInternals? Internals { get; init; }

    internal WorkloadUpdateStageInternals RequireInternals() =>
        Internals ?? throw new InvalidOperationException(
            $"The stage context for intent {IntentId} carries no intent or plan. A stage this library ships was "
            + "run against a context built outside WorkloadUpdateOrchestrator, which is the only thing that "
            + "may build one.");
}

internal sealed record WorkloadUpdateStageInternals
{
    public required WorkloadUpdateIntent Intent { get; init; }

    public required WorkloadUpdatePlan Plan { get; init; }

    public WorkloadUpdateCohortReading? CohortReading { get; init; }

    public WorkloadGroup? TrackedGroup { get; init; }

    public DateTimeOffset NowUtc { get; init; }

    public Reeve.Pipeline.Pipeline<WorkloadUpdateStageContext>? Pipeline { get; init; }

}
