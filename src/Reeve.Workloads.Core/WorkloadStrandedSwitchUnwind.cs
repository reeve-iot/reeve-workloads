using Microsoft.Extensions.Logging;
using Reeve.Workloads.Abstractions;
using Reeve.Workloads.Abstractions.Domain.Entities;

namespace Reeve.Workloads.Core;

internal sealed class WorkloadStrandedSwitchUnwind(
    IWorkloadInstanceRepository repository,
    IWorkloadClock clock,
    ILogger logger)
{
    internal static readonly TimeSpan StrandedAfter = TimeSpan.FromMinutes(15);

    public async Task UnwindAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<WorkloadInstance> switching;
        try
        {
            switching = await repository
                .ListByStateAsync(WorkloadInstanceState.Switching, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Failed to load Switching workload instances; skipping the stranded-switch unwind this pass.");
            return;
        }

        var cutoff = clock.UtcNow - StrandedAfter;
        foreach (var instance in switching)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (instance.State == WorkloadInstanceState.Switching && instance.LastTransitionAtUtc <= cutoff)
            {
                await UnwindOneAsync(instance, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private async Task UnwindOneAsync(WorkloadInstance instance, CancellationToken cancellationToken)
    {
        try
        {
            var now = clock.UtcNow;
            var held = instance.IsHostHoldPending;
            instance.TransitionTo(
                WorkloadInstanceState.Degraded,
                WorkloadStateReason.HealthLost,
                "Recovered a workload stranded mid-switch — the managed→manual switch did not complete; " +
                "restored to managed (the switch block is lifted; health is re-derived at the next resync).",
                now);
            if (held)
            {
                instance.TransitionTo(WorkloadInstanceState.Suspended, WorkloadStateReason.SuspendedByHost,
                    "Suspended by the host.", now);
            }

            await repository.UpdateAsync(instance, cancellationToken).ConfigureAwait(false);

            logger.LogWarning(
                "Unwound workload instance {InstanceId} from Switching (entered {SwitchingSince}, more than {Bound} ago) to managed: " +
                "the switch did not complete — the switch block is lifted and managed reconcile/auto-updates resume.",
                instance.Id, instance.LastTransitionAtUtc, StrandedAfter);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex,
                "Failed to unwind stranded Switching workload instance {InstanceId}; leaving it for the next periodic pass.",
                instance.Id);
        }
    }
}
