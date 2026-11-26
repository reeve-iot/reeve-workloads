using System.ComponentModel.DataAnnotations;

namespace Reeve.Workloads.Core;

/// <summary>Tunables for the leader-only reconciler owned-unit. Every default is chosen so a stock deployment needs
/// zero configuration, and the small barrier defaults keep one stagger step from stalling the whole pass.</summary>
public sealed class WorkloadReconcilerOptions
{
    public const string SectionName = "Workloads:Reconciler";

    /// <summary>The cadence in seconds of the periodic full reconcile pass: it reads every instance's runtime status
    /// and re-applies a drifted spec, so it catches a flapping runtime or a drift even with no bus nudge, presence
    /// event or watch event.</summary>
    [Range(1, 3600)]
    public int StatusResyncSeconds { get; init; } = 60;

    /// <summary>Whether the provider-watch reconcile trigger is enabled. The periodic pass always remains the
    /// correctness backstop; watch is a trigger optimization, never a correctness change.</summary>
    public bool WatchEnabled { get; init; } = true;

    /// <summary>The per-step health-barrier timeout in seconds. An applied instance is waited on until Healthy or
    /// this elapses, so a stuck instance cannot block the pass forever.</summary>
    [Range(1, 3600)]
    public int HealthBarrierTimeoutSeconds { get; init; } = 120;

    /// <summary>How often the health barrier re-queries the repository while waiting: small enough to advance
    /// promptly, large enough not to hammer the DB. It is a repository query — one row — rather than a full reconcile, which is what makes half a second an affordable cadence.</summary>
    [Range(10, 60_000)]
    public int HealthBarrierPollMilliseconds { get; init; } = 500;

    /// <summary>How often a settling instance is RE-DRIVEN through the engine rather than merely read.</summary>
    /// <remarks>The barrier polls a row to learn whether the instance became Healthy, but something has to make
    /// it become Healthy. With a watch, that is the watch's nudge, which always takes the full path. Without one
    /// — watch disabled, or a health mode nothing observes externally — this interval is the only thing that
    /// moves the instance, so it must stay well inside <see cref="HealthBarrierTimeoutSeconds"/>. Ten seconds
    /// against a two-minute barrier is twelve drives .</remarks>
    [Range(1, 600)]
    public int HealthBarrierDriveSeconds { get; init; } = 10;

    /// <summary>How many queue workers reconcile in parallel. One reconciles one instance at a time; raising it is a deliberate decision, not a default.</summary>
    [Range(1, 64)]
    public int ReconcileWorkerCount { get; init; } = 1;

    /// <summary>How many instances may be waiting to reach Healthy after an apply at once. This is the health
    /// barrier: one means a fleet-wide re-stamp still rolls rather than blinking every adapter together.</summary>
    [Range(1, 64)]
    public int MaxConcurrentSettling { get; init; } = 1;

    /// <summary>The first retry delay after a reconcile throws; it doubles per consecutive fault up to
    /// <see cref="RetryBackoffMaxSeconds"/>, with jitter.</summary>
    [Range(10, 60_000)]
    public int RetryBackoffBaseMilliseconds { get; init; } = 1_000;

    /// <summary>The cap on that doubling, so an instance that never comes up is still retried on a cadence a
    /// human recognises rather than drifting into hours.</summary>
    [Range(1, 3600)]
    public int RetryBackoffMaxSeconds { get; init; } = 300;

    /// <summary>How long a fleet status read stays usable. A pass primes the cache once and then drains a queue
    /// that can take minutes on a settling instance, so without this the last instance of a long pass would be
    /// driven off a status taken before the first one was applied.</summary>
    [Range(1, 600)]
    public int FleetStatusFreshnessSeconds { get; init; } = 15;

    /// <summary>How long after its deploy started (<c>WorkloadInstance.DeployStartedAtUtc</c>) a materialized workload may
    /// stay not ready before the reconciler reports it Degraded (reason Provisioning) with the provider's reason.</summary>
    [Range(60, 86_400)]
    public int DeployStalledAfterSeconds { get; init; } = 10 * 60;

    public TimeSpan StatusResyncInterval => TimeSpan.FromSeconds(StatusResyncSeconds);
    public TimeSpan HealthBarrierTimeout => TimeSpan.FromSeconds(HealthBarrierTimeoutSeconds);
    public TimeSpan HealthBarrierPollInterval => TimeSpan.FromMilliseconds(HealthBarrierPollMilliseconds);
    public TimeSpan HealthBarrierDriveInterval => TimeSpan.FromSeconds(HealthBarrierDriveSeconds);
    public TimeSpan RetryBackoffBase => TimeSpan.FromMilliseconds(RetryBackoffBaseMilliseconds);
    public TimeSpan RetryBackoffMax => TimeSpan.FromSeconds(RetryBackoffMaxSeconds);
    public TimeSpan FleetStatusFreshness => TimeSpan.FromSeconds(FleetStatusFreshnessSeconds);
    public TimeSpan DeployStalledAfter => TimeSpan.FromSeconds(DeployStalledAfterSeconds);
}
