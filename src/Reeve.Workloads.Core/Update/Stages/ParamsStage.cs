using Reeve.Pipeline;
using Reeve.Workloads.Abstractions.Update;

namespace Reeve.Workloads.Core.Update.Stages;

internal sealed class ParamsStage(IWorkloadUpdateEffects effects) : IStage<WorkloadUpdateStageContext>
{
    private readonly IWorkloadUpdateEffects _effects = effects ?? throw new ArgumentNullException(nameof(effects));

    public string Id => WorkloadUpdateStages.Params;

    public StagePlacement Placement => StagePlacement.First;

    public StageReversibility Reversibility => StageReversibility.Reversible;

    public StageBudget Budget => StageBudget.None;

    public ValueTask<StageOutcome> UpAsync(WorkloadUpdateStageContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.Cohort is not null)
        {
            var internals = context.RequireInternals();
            var reading = internals.CohortReading
                ?? throw new InvalidOperationException("A cohort member reached stage.params without its cohort reading.");
            if (!reading.TurnOf(internals.Intent))
            {
                var blocker = reading.NearestBlockingMember(internals.Intent)
                    ?? throw new InvalidOperationException("The cohort turn is closed without a blocking member.");
                return ValueTask.FromResult(StageOutcome.Working(
                    $"waiting for its turn: instance {blocker.GroupInstanceId} (member {blocker.CohortOrdinal!.Value + 1} "
                    + $"of {blocker.CohortSize}) is at stage '{blocker.CurrentStage ?? WorkloadUpdateStages.Params}'"));
            }

            internals.Intent.StageTargetSpecs(internals.Plan.TargetSpecs, internals.NowUtc);
        }

        return ValueTask.FromResult(StageOutcome.Done);
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
