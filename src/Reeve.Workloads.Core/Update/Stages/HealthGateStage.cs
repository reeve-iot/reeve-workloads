using Reeve.Pipeline;
using Reeve.Workloads.Abstractions;
using Reeve.Workloads.Abstractions.Domain.Entities;
using Reeve.Workloads.Abstractions.Update;

namespace Reeve.Workloads.Core.Update.Stages;

internal sealed class HealthGateStage(
    IWorkloadUpdateHealthGate healthGate,
    IWorkloadClock clock,
    WorkloadUpdateOrchestratorOptions options)
    : IStage<WorkloadUpdateStageContext>
{
    private readonly IWorkloadUpdateHealthGate _healthGate = healthGate ?? throw new ArgumentNullException(nameof(healthGate));
    private readonly IWorkloadClock _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    private readonly WorkloadUpdateOrchestratorOptions _options = options ?? throw new ArgumentNullException(nameof(options));

    public string Id => WorkloadUpdateStages.HealthGate;

    public StagePlacement Placement => StagePlacement.After(WorkloadUpdateStages.OperatorRedeploy);

    public StageReversibility Reversibility => StageReversibility.Reversible;

    public StageBudget Budget => StageBudget.Of(_options.HealthGateStageBudget, StageTimeoutPolicy.FailAndCompensate);

    public async ValueTask<StageOutcome> UpAsync(WorkloadUpdateStageContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        var internals = context.RequireInternals();
        var intent = internals.Intent;
        var plan = internals.Plan;
        var now = _clock.UtcNow;

        var applyTimestamp = intent.SidecarAppliedAtUtc ?? intent.PhaseEnteredAtUtc;

        var pending = new List<string>();

        foreach (var item in plan.Workloads)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var step = WorkloadUpdateSteps.Ensure(intent, item.WorkloadId, now);

            WorkloadHealthGateTickResult tick;
            try
            {
                tick = await _healthGate
                    .EvaluateTickAsync(RequestFor(intent, item, step, applyTimestamp), cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                pending.Add($"{item.WorkloadId} ({ex.GetType().Name})");
                continue;
            }

            step.SetGateClockState(tick.NextClockState, now);

            switch (tick.Verdict)
            {
                case WorkloadHealthGateVerdict.Passed:
                    step.Advance(WorkloadUpdatePhase.HealthGate, WorkloadUpdateStepState.GateWaiting, "gate passed", now);
                    break;

                case WorkloadHealthGateVerdict.Failed:
                    var reason = $"The health gate failed for workload '{item.WorkloadId}' "
                        + $"({tick.FailureReason}): {tick.Detail}".TrimEnd();
                    step.Advance(WorkloadUpdatePhase.HealthGate, WorkloadUpdateStepState.Failed, reason, now);
                    return StageOutcome.Failed(reason);

                default:
                    pending.Add(item.WorkloadId);
                    break;
            }
        }

        if (pending.Count == 0)
        {
            return StageOutcome.Done;
        }

        var healthy = plan.Workloads.Count - pending.Count;
        return StageOutcome.Working(
            $"{healthy} of {plan.Workloads.Count} workloads healthy — waiting for: {string.Join(", ", pending)}");
    }

    public ValueTask<StageOutcome> DownAsync(WorkloadUpdateStageContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        return ValueTask.FromResult(StageOutcome.Done);
    }

    private WorkloadHealthGateTickRequest RequestFor(
        WorkloadUpdateIntent intent,
        WorkloadUpdatePlanItem item,
        WorkloadUpdateIntentStep step,
        DateTimeOffset applyTimestamp)
    {
        var expectedHash = WorkloadUpdateSteps.ExpectedHashFor(step, item);

        var outgoingHash = item.PreviousSpec?.SpecHash is { Length: > 0 } previous
                           && !string.Equals(previous, expectedHash, StringComparison.Ordinal)
            ? previous
            : null;

        return new()
        {
            Reference = item.Reference,
            InstanceId = item.Reference.InstanceId,
            ExpectedSpecHash = expectedHash,
            OutgoingSpecHash = outgoingHash,
            ApplyTimestampUtc = applyTimestamp,
            ToVersion = intent.ToVersion,
            CurrentExtensions = item.CurrentExtensions,
            CandidateExtensions = item.CandidateExtensions,
            Certification = item.Certification,
            Timeout = _options.HealthGateTimeout,
            PriorClockState = step.ReadPriorClockState(),
        };
    }
}
