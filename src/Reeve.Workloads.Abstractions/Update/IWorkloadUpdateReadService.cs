using Reeve.Workloads.Abstractions.Contracts;

namespace Reeve.Workloads.Abstractions.Update;

/// <summary>The read/preview surface for the workload update flow: diff preview, status list and intent detail. No DB writes and no intent staged anywhere; the diff preview returns blocked rather than throws.</summary>
public interface IWorkloadUpdateReadService
{
    /// <summary>Computes the read-only requirements diff for upgrading to <paramref name="candidateVersion"/>. Returns
    /// a BLOCKED result — never throws, never an empty "no changes" all-clear — on any fault. Stages no intent.</summary>
    Task<WorkloadUpdateDiffResponse> GetDiffPreviewAsync(
        Guid pluginInstanceId,
        string candidateVersion,
        CancellationToken cancellationToken = default);

    /// <summary>The active + recent update-intent summaries. The active (non-terminal) intent, if any, is first,
    /// then the most recent terminal intents newest-first.</summary>
    Task<WorkloadUpdateStatusResponse> GetStatusAsync(
        Guid pluginInstanceId,
        int recentLimit = 10,
        CancellationToken cancellationToken = default);

    /// <summary>The detail projection for one intent: phase, per-workload steps, digests, error, and for a
    /// rolled-back intent the parsed rollback evidence. Null when no intent with that id exists.</summary>
    Task<WorkloadUpdateIntentResponse?> GetIntentAsync(
        Guid intentId,
        CancellationToken cancellationToken = default);
}
