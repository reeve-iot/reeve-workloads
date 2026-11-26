using Reeve.Pipeline;
using Reeve.Workloads.Abstractions.Domain.Entities;
using Reeve.Workloads.Abstractions.Domain.Entities.Documents;

namespace Reeve.Workloads.Abstractions.Update;

internal sealed record WorkloadUpdateCohortReading(
    WorkloadGroup Group,
    IReadOnlyList<WorkloadUpdateIntent> Members,
    IReadOnlySet<Guid> UnaccountedMemberIds)
{
    private WorkloadUpdateIntent? First => Members.OrderBy(member => member.CohortOrdinal).FirstOrDefault();

    public string? From => First?.FromVersion;
    public string To => First?.ToVersion ?? string.Empty;
    public int Size => First?.CohortSize ?? 0;

    public bool Passed(WorkloadUpdateIntent member)
        => member.ReadStageJournal() is
            { Verdict: WorkloadDocumentVerdict.Value, Value.Entries: var entries }
            && entries.Any(entry => entry.StageId == WorkloadUpdateStages.CommitVersion
                && entry.Direction == PipelineDirection.Forward);

    public bool Committed => string.Equals(Group.InstalledVersion, To, StringComparison.Ordinal);

    public bool Diverged => !string.Equals(Group.InstalledVersion, From, StringComparison.Ordinal)
        && !Committed;

    public WorkloadUpdateIntent? FailingMember => Members.FirstOrDefault(member => IsCompensating(member)
        || (member.IsTerminal && member.Outcome != WorkloadUpdateOutcome.Completed)
        || UnaccountedMemberIds.Contains(member.Id));

    public bool Failing => !Committed && (Diverged || FailingMember is not null || Members.Count < Size);

    public bool TurnOf(WorkloadUpdateIntent member)
        => Members.Where(other => other.CohortOrdinal.GetValueOrDefault() < member.CohortOrdinal.GetValueOrDefault()).All(Passed);

    public bool BarrierOpen => Members.Count == Size && Members.All(Passed);

    public WorkloadUpdateIntent? NearestBlockingMember(WorkloadUpdateIntent member)
        => Members.Where(other => other.CohortOrdinal < member.CohortOrdinal && !Passed(other))
            .OrderByDescending(other => other.CohortOrdinal)
            .FirstOrDefault();

    public static WorkloadUpdateCohortReading Create(
        WorkloadGroup group,
        IReadOnlyList<WorkloadUpdateIntent> members,
        IReadOnlySet<Guid>? unaccountedMemberIds = null)
    {
        ArgumentNullException.ThrowIfNull(group);
        ArgumentNullException.ThrowIfNull(members);
        return new WorkloadUpdateCohortReading(group, members, unaccountedMemberIds ?? new HashSet<Guid>());
    }

    private static bool IsCompensating(WorkloadUpdateIntent member)
        => member.ReadStageJournal() is
            { Verdict: WorkloadDocumentVerdict.Value, Value.Direction: PipelineDirection.Compensating };

}
