using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Reeve.Workloads.Abstractions;
using Reeve.Workloads.Abstractions.Contracts;
using Reeve.Workloads.Abstractions.Domain.Entities;
using Reeve.Workloads.Abstractions.Providers;

namespace Reeve.Workloads.Core.Providers;

internal sealed class WorkloadFleetStatusCache
{
    private readonly IWorkloadClock clock;
    private readonly WorkloadReconcilerOptions options;
    private readonly ILogger<WorkloadFleetStatusCache> logger;

    private readonly object gate = new();
    private readonly Dictionary<WorkloadRef, WorkloadStatus> statuses = new();
    private DateTimeOffset primedAt;

    public WorkloadFleetStatusCache(
        IWorkloadClock clock,
        IOptions<WorkloadReconcilerOptions> options,
        ILogger<WorkloadFleetStatusCache> logger)
    {
        this.clock = clock;
        this.options = options.Value;
        this.logger = logger;
    }

    public int Count { get { lock (gate) { return statuses.Count; } } }

    public void Prime(IReadOnlyDictionary<WorkloadRef, WorkloadStatus> fleet)
    {
        ArgumentNullException.ThrowIfNull(fleet);

        lock (gate)
        {
            statuses.Clear();
            foreach (var (reference, status) in fleet)
            {
                statuses[reference] = status;
            }

            primedAt = clock.UtcNow;
        }
    }

    public async Task PrimeAsync(
        IWorkloadProviderRegistry registry,
        IReadOnlyList<WorkloadInstanceRef> instances,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(instances);

        var byProvider = new Dictionary<WorkloadProviderId, List<WorkloadRef>>();
        foreach (var instance in instances)
        {
            if (instance.Provider is not { } id)
            {
                continue;
            }

            var provider = registry.TryResolve(id);
            if (provider is null || !provider.Descriptor.ReadsStatusInBatches)
            {
                continue;
            }

            if (!byProvider.TryGetValue(id, out var references))
            {
                references = [];
                byProvider[id] = references;
            }

            references.Add(WorkloadRef.FromInstanceRef(instance));
        }

        var fleet = new Dictionary<WorkloadRef, WorkloadStatus>();
        foreach (var (id, references) in byProvider)
        {
            if (references.Count < 2)
            {
                continue;
            }

            try
            {
                var read = await registry.Resolve(id).GetStatusesAsync(references, cancellationToken).ConfigureAwait(false);
                foreach (var (reference, status) in read)
                {
                    fleet[reference] = status;
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "Fleet status read for provider {Provider} faulted; this pass reads instance by instance.", id);
            }
        }

        Prime(fleet);
    }

    public bool TryConsume(WorkloadRef reference, out WorkloadStatus status)
    {
        lock (gate)
        {
            if (!statuses.TryGetValue(reference, out var primed))
            {
                status = default!;
                return false;
            }

            var age = clock.UtcNow - primedAt;
            if (age >= options.FleetStatusFreshness)
            {
                statuses.Clear();
                logger.LogDebug(
                    "Fleet status primed {Age} ago is past its {Freshness} freshness; the rest of this pass reads the provider directly.",
                    age, options.FleetStatusFreshness);
                status = default!;
                return false;
            }

            statuses.Remove(reference);
            status = primed;
            return true;
        }
    }

    public void Clear()
    {
        lock (gate)
        {
            statuses.Clear();
        }
    }
}
