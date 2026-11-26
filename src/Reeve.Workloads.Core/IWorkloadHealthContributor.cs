using System.Collections.Frozen;
using Reeve.Workloads.Abstractions.Contracts.Deployment;
using Reeve.Workloads.Abstractions.Domain;

using Reeve.Workloads.Abstractions;
using Reeve.Workloads.Domain;

namespace Reeve.Workloads.Core;

/// <summary>The host's richer health signal for one instance, under the host health modes it observes: whether
/// what runs matches the desired hash, and the version actually running. Any other mode is answered by
/// <see cref="ProviderReadyHealthContributor"/>.</summary>
public interface IWorkloadHealthContributor
{
    /// <summary>The health modes this contributor answers for. Two registered contributors may not share a mode:
    /// <c>RunReeveWorkloadsAsync</c> refuses to start when they do. A host that does not call it gets no refusal,
    /// and the first registered contributor that observes a mode silently answers for it.</summary>
    IReadOnlySet<WorkloadHealthMode> ObservedModes { get; }

    /// <summary>Observes one instance. Must not throw for a merely-unreachable source: a fault reports
    /// <see cref="WorkloadHealthEvaluator.Unknown"/>, which holds the current state rather than degrading it.</summary>
    Task<WorkloadHealthContribution> ObserveAsync(
        WorkloadHealthObservation observation, CancellationToken cancellationToken = default);
}

/// <summary>What the contributor is asked about. Everything here is already the library's own.</summary>
public sealed record WorkloadHealthObservation
{
    /// <summary>The instance being reconciled.</summary>
    public required Guid InstanceId { get; init; }

    /// <summary>The manifest's health mode, one of the contributor's <see cref="IWorkloadHealthContributor.ObservedModes"/>
    /// unless it is the fallback <see cref="ProviderReadyHealthContributor"/> being asked.</summary>
    public required WorkloadHealthMode HealthMode { get; init; }

    /// <summary>The spec hash the reconciler wants running, which the host compares its own evidence against.</summary>
    public string? DesiredSpecHash { get; init; }

    /// <summary>The module version installed on this host, for the skew comparison the host's own comparator owns.</summary>
    public string? InstalledGroupVersion { get; init; }
}

/// <summary>The host's answer. Absent evidence and a faulted read are different answers, and conflating them
/// downgrades a Healthy instance on a broker blip.</summary>
public readonly record struct WorkloadHealthContribution
{
    /// <summary>The presence anchor: a definite read, a genuine absence, or unknown after a fault.</summary>
    public WorkloadPresenceSignal Presence { get; init; }

    /// <summary>The version the host observes actually running, or null when it cannot tell.</summary>
    public string? RunningVersion { get; init; }

    /// <summary>Whether that running version is strictly older than the installed module, by the HOST's
    /// comparator — the same one its loader uses, which the library has no way to reproduce.</summary>
    public bool RunningVersionIsOlder { get; init; }

    /// <summary>The provider-only answer: no evidence past readiness. The evaluator ignores presence under a
    /// library-owned mode, which is the only kind this is a safe answer for; under a host mode Absent never reaches
    /// Healthy.</summary>
    public static readonly WorkloadHealthContribution None = new()
    {
        Presence = WorkloadPresenceSignal.Absent,
    };
}

/// <summary>The shipped no-op: provider readiness is the whole story. Under any mode the library does not own —
/// an unset one included — it must answer PresentMatching, because None's Absent never reaches Healthy there.</summary>
public sealed class ProviderReadyHealthContributor : IWorkloadHealthContributor
{
    /// <summary>None: this is what every mode no contributor observes falls back to.</summary>
    public IReadOnlySet<WorkloadHealthMode> ObservedModes => FrozenSet<WorkloadHealthMode>.Empty;

    /// <inheritdoc />
    public Task<WorkloadHealthContribution> ObserveAsync(
        WorkloadHealthObservation observation, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(observation);

        return Task.FromResult(observation.HealthMode.IsLibraryOwned
            ? WorkloadHealthContribution.None
            : new WorkloadHealthContribution { Presence = WorkloadPresenceSignal.PresentMatching });
    }
}
