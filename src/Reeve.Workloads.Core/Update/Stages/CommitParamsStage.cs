using Reeve.Pipeline;
using Reeve.Workloads.Abstractions.Update;

namespace Reeve.Workloads.Core.Update.Stages;

/// <summary>The last stage: commits the parameters the candidate deploys with, then begins the teardown of every
/// workload the candidate no longer declares. That teardown cannot be undone, so the stage is irreversible once
/// entered: an abandon is refused from here instead of rolling back to a version without those workloads, and a
/// stage that throws is retried (both steps are idempotent).</summary>
internal sealed class CommitParamsStage(IWorkloadUpdateEffects effects, IWorkloadUpdateRowEffects rows)
    : IStage<WorkloadUpdateStageContext>
{
    private readonly IWorkloadUpdateEffects _effects = effects ?? throw new ArgumentNullException(nameof(effects));
    private readonly IWorkloadUpdateRowEffects _rows = rows ?? throw new ArgumentNullException(nameof(rows));

    public string Id => WorkloadUpdateStages.CommitParams;

    public StagePlacement Placement =>
        StagePlacement.After(WorkloadUpdateStages.CommitVersion, WorkloadUpdateStages.CommitParamsOrdinal);

    public StageReversibility Reversibility => StageReversibility.IrreversibleOnceEntered(
        "the new version's parameters are committed and the workloads it no longer declares are being torn down");

    public StageBudget Budget => StageBudget.None;

    public async ValueTask<StageOutcome> UpAsync(WorkloadUpdateStageContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        var plan = context.RequireInternals().Plan;
        await _effects.CommitParamsAsync(plan, cancellationToken).ConfigureAwait(false);
        await _rows.TearDownDroppedRowsAsync(plan, cancellationToken).ConfigureAwait(false);

        return StageOutcome.Done;
    }

    public async ValueTask<StageOutcome> DownAsync(WorkloadUpdateStageContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        await _effects
            .RestorePreviousParamsAsync(context.RequireInternals().Intent, cancellationToken)
            .ConfigureAwait(false);

        return StageOutcome.Done;
    }
}
