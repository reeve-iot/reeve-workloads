using Reeve.Pipeline;
using Reeve.Workloads.Abstractions.Update;

namespace Reeve.Workloads.Core.Update;

internal static class WorkloadUpdatePointsOfNoReturn
{
    public static WorkloadUpdatePointOfNoReturn? Of(Pipeline<WorkloadUpdateStageContext> pipeline)
        => pipeline.FirstIrreversibleStageId is { } stageId
            ? new WorkloadUpdatePointOfNoReturn(stageId, pipeline.FirstIrreversibleReason ?? string.Empty)
            : null;

    private static (WorkloadUpdatePointOfNoReturn Point, DateTimeOffset EnteredAtUtc, string Refusal)? Barrier(
        PipelineJournal journal, Pipeline<WorkloadUpdateStageContext> pipeline)
    {
        foreach (var entry in journal.Entries)
        {
            if (entry.Direction != PipelineDirection.Forward)
            {
                continue;
            }

            if (pipeline.Find(entry.StageId) is not { } stage)
            {
                return (new WorkloadUpdatePointOfNoReturn(entry.StageId, Unregistered), entry.EnteredAtUtc,
                    $"it has entered stage '{entry.StageId}', which this build does not register, so whether that "
                    + "stage can be undone is not known here");
            }

            if (!stage.Reversibility.IsReversible)
            {
                var reason = stage.Reversibility.OperatorReason ?? string.Empty;
                return (new WorkloadUpdatePointOfNoReturn(stage.Id, reason), entry.EnteredAtUtc,
                    $"stage '{stage.Id}' is irreversible once entered: {reason}");
            }
        }

        return null;
    }

    private const string Unregistered = "this build does not register this stage, so whether it can be undone is not "
        + "known here, and this build does not roll the update back past it";

    public static string? RefusalToUnwind(PipelineJournal journal, Pipeline<WorkloadUpdateStageContext> pipeline)
        => Barrier(journal, pipeline)?.Refusal;

    public static string? RefusalToUnwind(
        PipelineJournal journal,
        Pipeline<WorkloadUpdateStageContext> pipeline,
        WorkloadUpdateCohortReading? cohort)
        => cohort is { Committed: true }
            ? "the cohort's version commit already landed; one instance cannot be moved back alone"
            : RefusalToUnwind(journal, pipeline);

    public static (WorkloadUpdatePointOfNoReturn Point, DateTimeOffset EnteredAtUtc)? IrreversibleIn(
        PipelineJournal journal, Pipeline<WorkloadUpdateStageContext> pipeline)
    {
        foreach (var entry in journal.Entries)
        {
            if (entry.Direction == PipelineDirection.Forward
                && pipeline.Find(entry.StageId) is { Reversibility.IsReversible: false } stage)
            {
                return (new WorkloadUpdatePointOfNoReturn(stage.Id, stage.Reversibility.OperatorReason ?? string.Empty),
                    entry.EnteredAtUtc);
            }
        }

        return null;
    }

    public static (WorkloadUpdatePointOfNoReturn Point, DateTimeOffset EnteredAtUtc)? PassedIn(
        PipelineJournal journal, Pipeline<WorkloadUpdateStageContext> pipeline)
        => journal.Direction == PipelineDirection.Forward && Barrier(journal, pipeline) is { } barrier
            ? (barrier.Point, barrier.EnteredAtUtc)
            : null;

    public static string? RefusalToCarryOn(PipelineJournal journal, Pipeline<WorkloadUpdateStageContext> pipeline)
        => WorkloadUpdateRowReading.NextForwardStage(journal, pipeline) is { } next && pipeline.Find(next) is null
            ? $"its walk is at stage '{next}', which this build does not register"
            : null;
}
