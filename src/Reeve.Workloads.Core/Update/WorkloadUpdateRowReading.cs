using Reeve.Pipeline;
using Reeve.Workloads.Abstractions.Domain.Entities;
using Reeve.Workloads.Abstractions.Domain.Entities.Documents;
using Reeve.Workloads.Abstractions.Update;

namespace Reeve.Workloads.Core.Update;

internal enum WorkloadUpdateRowStanding
{
    Recorded,

    Unaccounted,
}

internal sealed record WorkloadUpdateRowReading(WorkloadUpdateRowStanding Standing, PipelineJournal? Journal, string? Why)
{
    public static WorkloadUpdateRowReading Of(WorkloadUpdateIntent intent, Pipeline<WorkloadUpdateStageContext> pipeline)
    {
        ArgumentNullException.ThrowIfNull(intent);
        ArgumentNullException.ThrowIfNull(pipeline);

        var reading = intent.ReadStageJournal();
        if (reading.Verdict == WorkloadDocumentVerdict.Unreadable)
        {
            return new(WorkloadUpdateRowStanding.Unaccounted, null, "its stage journal is unreadable");
        }

        var stored = reading.Verdict == WorkloadDocumentVerdict.Value ? reading.Value : null;
        if (stored is null)
        {
            return new(WorkloadUpdateRowStanding.Unaccounted, null, "it has no stage journal");
        }

        if (intent.IsTerminal)
        {
            return new(WorkloadUpdateRowStanding.Recorded, stored, null);
        }

        if (intent.Phase == WorkloadUpdatePhase.RollingBack)
        {
            return stored is { State: PipelineState.Running }
                ? new(WorkloadUpdateRowStanding.Recorded, stored, null)
                : new(WorkloadUpdateRowStanding.Unaccounted, null, $"its stage journal is {stored.State} on its rollback");
        }

        if (stored is not { Direction: PipelineDirection.Forward, State: PipelineState.Running })
        {
            return new(WorkloadUpdateRowStanding.Unaccounted, null,
                $"its stage journal is {stored.Direction} and {stored.State} on its forward phase {intent.Phase}");
        }

        var projected = PhaseOf(NextForwardStage(stored, pipeline));
        if (projected == intent.Phase)
        {
            return new(WorkloadUpdateRowStanding.Recorded, stored, null);
        }

        return new(WorkloadUpdateRowStanding.Unaccounted, null, Rank(projected) > Rank(intent.Phase)
            ? $"its stage journal is at stage '{NextForwardStage(stored, pipeline)}', ahead of its phase {intent.Phase}"
            : $"its stage journal does not account for its phase {intent.Phase}");
    }

    public static string? NextForwardStage(PipelineJournal journal, Pipeline<WorkloadUpdateStageContext> pipeline)
    {
        var last = journal.Entries.Count > 0 ? journal.Entries[^1] : null;
        if (last is null)
        {
            return pipeline.Order[0];
        }

        if (last.State == StageEntryState.Working)
        {
            return last.StageId;
        }

        var at = IndexOf(pipeline, last.StageId);
        if (at < 0)
        {
            return last.StageId;
        }

        return at + 1 < pipeline.Order.Count ? pipeline.Order[at + 1] : null;
    }

    public static WorkloadUpdatePhase PhaseOf(string? stageId) => stageId switch
    {
        WorkloadUpdateStages.Params => WorkloadUpdatePhase.StagingParams,
        WorkloadUpdateStages.Sidecar or WorkloadUpdateStages.OperatorRedeploy => WorkloadUpdatePhase.ApplyingSidecar,
        WorkloadUpdateStages.HealthGate => WorkloadUpdatePhase.HealthGate,
        _ => WorkloadUpdatePhase.SwappingModule,
    };

    private static int Rank(WorkloadUpdatePhase phase) => phase switch
    {
        WorkloadUpdatePhase.StagingParams => 0,
        WorkloadUpdatePhase.ApplyingSidecar => 1,
        WorkloadUpdatePhase.HealthGate => 2,
        _ => 3,
    };

    private static int IndexOf(Pipeline<WorkloadUpdateStageContext> pipeline, string stageId)
    {
        for (var i = 0; i < pipeline.Order.Count; i++)
        {
            if (string.Equals(pipeline.Order[i], stageId, StringComparison.Ordinal))
            {
                return i;
            }
        }

        return -1;
    }
}
