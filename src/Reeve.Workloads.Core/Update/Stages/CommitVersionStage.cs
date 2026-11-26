using Reeve.Pipeline;
using Reeve.Workloads.Abstractions;
using Reeve.Workloads.Abstractions.Domain.Entities;
using Reeve.Workloads.Abstractions.Update;

namespace Reeve.Workloads.Core.Update.Stages;

internal sealed class CommitVersionStage : IStage<WorkloadUpdateStageContext>
{
    private readonly IWorkloadUpdateEffects _effects;
    private readonly IWorkloadUpdateRowEffects _rows;
    private readonly IWorkloadUpdateIntentRepository? _intents;

    public CommitVersionStage(IWorkloadUpdateEffects effects, IWorkloadUpdateRowEffects rows)
        : this(effects, rows, null)
    {
    }

    public CommitVersionStage(
        IWorkloadUpdateEffects effects,
        IWorkloadUpdateRowEffects rows,
        IWorkloadUpdateIntentRepository? intents)
    {
        _effects = effects ?? throw new ArgumentNullException(nameof(effects));
        _rows = rows ?? throw new ArgumentNullException(nameof(rows));
        _intents = intents;
    }

    public string Id => WorkloadUpdateStages.CommitVersion;

    public StagePlacement Placement => StagePlacement.After(WorkloadUpdateStages.HealthGate);

    public StageReversibility Reversibility => StageReversibility.Reversible;

    public StageBudget Budget => StageBudget.None;

    public async ValueTask<StageOutcome> UpAsync(WorkloadUpdateStageContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        var internals = context.RequireInternals();
        var cohort = context.Cohort is null ? null : await ReadCohortAsync(context, cancellationToken).ConfigureAwait(false);
        if (cohort is { Failing: true })
        {
            var detail = cohort.Diverged
                ? $"the group's version moved to '{cohort.Group.InstalledVersion}' under the cohort"
                : cohort.FailingMember is { } failed
                    ? $"cohort member {failed.GroupInstanceId} failed"
                    : $"cohort has {cohort.Members.Count} of {cohort.Size} member rows";
            return StageOutcome.Failed(detail);
        }

        await _rows.EnsureRowsAsync(internals.Intent, internals.Plan, cancellationToken).ConfigureAwait(false);

        if (cohort is { Committed: true })
        {
            return StageOutcome.Done;
        }

        if (cohort is not null && !cohort.BarrierOpen)
        {
            var waiting = cohort.Members.Where(member => !cohort.Passed(member))
                .OrderBy(member => member.CohortOrdinal)
                .Select(member => $"instance {member.GroupInstanceId} (member {member.CohortOrdinal!.Value + 1} of {member.CohortSize})");
            var count = cohort.Size - cohort.Members.Count(member => cohort.Passed(member));
            return StageOutcome.Working($"waiting for {count} member(s) to pass their gates: {string.Join(", ", waiting)}");
        }

        await _effects
            .CommitInstalledVersionAsync(context.GroupId, context.ToVersion, cancellationToken)
            .ConfigureAwait(false);

        return StageOutcome.Done;
    }

    public async ValueTask<StageOutcome> DownAsync(WorkloadUpdateStageContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (context.Cohort is not null)
        {
            var cohort = await ReadCohortAsync(context, cancellationToken).ConfigureAwait(false);
            if (cohort.Committed)
            {
                return StageOutcome.Failed(
                    "the cohort's version commit already landed; one instance cannot be moved back alone");
            }

            var cohortInternals = context.RequireInternals();
            await _rows.RemoveAddedRowsAsync(cohortInternals.Intent, cohortInternals.Plan, cancellationToken).ConfigureAwait(false);
            return StageOutcome.Done;
        }

        if (string.IsNullOrWhiteSpace(context.FromVersion))
        {
            return StageOutcome.Failed(
                $"Group '{context.GroupId}' declared no version before this update, so there is none to return "
                + $"it to, and its row may still read '{context.ToVersion}'. Nothing here can undo that: put the "
                + "group back on a declared version by hand, or re-run the update once the cause is fixed.");
        }

        await _effects
            .CommitInstalledVersionAsync(context.GroupId, context.FromVersion, cancellationToken)
            .ConfigureAwait(false);

        var internals = context.RequireInternals();
        await _rows.RemoveAddedRowsAsync(internals.Intent, internals.Plan, cancellationToken).ConfigureAwait(false);

        return StageOutcome.Done;
    }

    private async Task<WorkloadUpdateCohortReading> ReadCohortAsync(
        WorkloadUpdateStageContext context,
        CancellationToken cancellationToken)
    {
        var internals = context.RequireInternals();
        if (_intents is null || internals.TrackedGroup is null)
        {
            throw new InvalidOperationException("A cohort commit-version stage requires its tracked group and intent repository.");
        }

        var members = await _intents.GetCohortAsync(context.Cohort!.CohortId, cancellationToken).ConfigureAwait(false);
        var unaccounted = internals.Pipeline is null
            ? new HashSet<Guid>()
            : members.Where(member => WorkloadUpdateRowReading.Of(member, internals.Pipeline).Standing
                    == WorkloadUpdateRowStanding.Unaccounted)
                .Select(member => member.Id)
                .ToHashSet();
        return WorkloadUpdateCohortReading.Create(internals.TrackedGroup, members, unaccounted);
    }
}
