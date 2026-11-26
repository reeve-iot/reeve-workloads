using Reeve.Workloads.Abstractions.Domain.DesiredSpec;
using Reeve.Workloads.Abstractions.Domain.Entities;

namespace Reeve.Workloads.Abstractions;

/// <summary>Supplies the durably-recorded spec an active update intent is converging on, so the drift pass and boot
/// reconcile follow the update instead of "correcting" it: the target while it moves forward, the previous spec
/// while it rolls back.</summary>
public interface IActiveWorkloadIntentProvider
{
    /// <summary>Returns the active update-intent's durably-recorded target spec for this workload — its previous
    /// spec while the intent is rolling back — or <c>null</c> when no intent is active (the resolver then computes
    /// the desired spec fresh).</summary>
    Task<WorkloadDesiredSpec?> TryGetActiveTargetSpecAsync(
        Guid pluginInstanceId,
        string workloadId,
        CancellationToken cancellationToken);

    /// <summary>Returns the active replay decision, including submitted parameters while moving forward. Implementations
    /// that do not override this method provide no submitted values, retaining the host-value behavior.</summary>
    async Task<ActiveWorkloadIntentReplay?> TryGetActiveReplayAsync(
        Guid pluginInstanceId,
        string workloadId,
        CancellationToken cancellationToken)
    {
        var spec = await TryGetActiveTargetSpecAsync(pluginInstanceId, workloadId, cancellationToken).ConfigureAwait(false);
        return spec is null ? null : new ActiveWorkloadIntentReplay(
            spec, null, new Dictionary<string, string?>());
    }

    /// <summary>Returns the id of the active update intent for <paramref name="pluginInstanceId"/>, or <c>null</c>
    /// when none; day-2 lifecycle commands are rejected with a 409 echoing this id while one is in flight.</summary>
    Task<Guid?> GetActiveIntentIdAsync(Guid pluginInstanceId, CancellationToken cancellationToken)
        => Task.FromResult<Guid?>(null);
}
