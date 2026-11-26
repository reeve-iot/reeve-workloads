using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Reeve.Workloads.Abstractions.Contracts.Deployment;
using Reeve.Workloads.Domain;

namespace Reeve.Workloads.Core.Health;

internal sealed class WorkloadHealthContributorDispatch
{
    private static readonly ProviderReadyHealthContributor ProviderReady = new();

    private readonly IReadOnlyList<IWorkloadHealthContributor> contributors;
    private readonly ILogger logger;

    public WorkloadHealthContributorDispatch(
        IEnumerable<IWorkloadHealthContributor> contributors,
        ILogger<WorkloadHealthContributorDispatch>? logger = null)
    {
        this.contributors = (contributors ?? throw new ArgumentNullException(nameof(contributors))).ToList();
        this.logger = logger ?? (ILogger)NullLogger.Instance;
    }

    public async Task<WorkloadHealthContribution> ObserveAsync(
        WorkloadHealthObservation observation, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(observation);

        var contributor = For(observation.HealthMode);
        try
        {
            return await contributor.ObserveAsync(observation, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogDebug(ex,
                "Health contributor faulted for workload {InstanceId} — treating as UNKNOWN, not offline.",
                observation.InstanceId);
            return new WorkloadHealthContribution { Presence = WorkloadHealthEvaluator.Unknown };
        }
    }

    private IWorkloadHealthContributor For(WorkloadHealthMode mode)
    {
        foreach (var contributor in contributors)
        {
            if (contributor.ObservedModes.Contains(mode))
            {
                return contributor;
            }
        }

        return ProviderReady;
    }
}
