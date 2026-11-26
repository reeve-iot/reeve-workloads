using Reeve.Workloads.Abstractions;
using Reeve.Workloads.Abstractions.Contracts.Deployment;
using Reeve.Workloads.Abstractions.Domain;
using Reeve.Workloads.Abstractions.Domain.Entities;

namespace Reeve.Workloads.Domain;

/// <summary>Decides the target lifecycle state the reconcile engine drives toward. A presence read that FAULTS
/// must arrive as <see cref="Unknown"/>, never as absence, or a blip in the host's store degrades a Healthy instance.</summary>
public static class WorkloadHealthEvaluator
{
    /// <summary>Evaluates the target lifecycle state. The current state and reason are inputs so a not-ready
    /// instance degrades instead of resetting, and an operator-paused one is never auto-resumed.</summary>
    public static WorkloadInstanceState EvaluateTarget(
        WorkloadHealthMode mode,
        bool providerReady,
        WorkloadPresenceSignal presence,
        string? desiredSpecHash,
        WorkloadInstanceState currentState,
        WorkloadStateReason currentReason)
    {
        if (currentState == WorkloadInstanceState.Suspended)
        {
            return WorkloadInstanceState.Suspended;
        }

        if (currentState == WorkloadInstanceState.Failed && currentReason == WorkloadStateReason.SpecRejected)
        {
            return WorkloadInstanceState.Failed;
        }

        return mode.IsLibraryOwned
            ? EvaluateProviderReadyOnly(providerReady, currentState)
            : EvaluateContributedPresence(providerReady, presence, desiredSpecHash, currentState);
    }

    private static WorkloadInstanceState EvaluateContributedPresence(
        bool providerReady,
        WorkloadPresenceSignal presence,
        string? desiredSpecHash,
        WorkloadInstanceState currentState)
    {
        if (!providerReady)
        {
            return currentState == WorkloadInstanceState.Healthy
                ? WorkloadInstanceState.Degraded
                : WorkloadInstanceState.Deploying;
        }

        return presence.Kind switch
        {
            WorkloadPresenceSignalKind.PresentMatching => WorkloadInstanceState.Healthy,

            WorkloadPresenceSignalKind.PresentMismatched => currentState == WorkloadInstanceState.Healthy
                ? WorkloadInstanceState.Degraded
                : WorkloadInstanceState.Deploying,

            WorkloadPresenceSignalKind.Absent => currentState == WorkloadInstanceState.Healthy
                ? WorkloadInstanceState.Degraded
                : WorkloadInstanceState.Deploying,

            WorkloadPresenceSignalKind.Unknown => currentState,

            _ => currentState,
        };
    }

    private static WorkloadInstanceState EvaluateProviderReadyOnly(bool providerReady, WorkloadInstanceState currentState)
    {
        if (providerReady)
        {
            return WorkloadInstanceState.Healthy;
        }

        return currentState == WorkloadInstanceState.Healthy
            ? WorkloadInstanceState.Degraded
            : WorkloadInstanceState.Deploying;
    }

    /// <summary>Builds the presence signal from a definitively-read anchor; <c>null</c> means genuinely absent.
    /// A store FAULT must use <see cref="Unknown"/>, never null.</summary>
    public static WorkloadPresenceSignal FromDefiniteRead(string? presenceSpecHash, string? desiredSpecHash)
        => presenceSpecHash is null
            ? WorkloadPresenceSignal.Absent
            : string.Equals(presenceSpecHash, desiredSpecHash, StringComparison.Ordinal)
                ? WorkloadPresenceSignal.PresentMatching
                : WorkloadPresenceSignal.PresentMismatched;

    /// <summary>The presence signal for a store read that FAULTED — unknown, never treated as offline.</summary>
    public static WorkloadPresenceSignal Unknown => new(WorkloadPresenceSignalKind.Unknown);
}

/// <summary>The presence verdict, not the raw Infrastructure entry: this keeps the evaluator pure Domain and
/// makes the fault-versus-absence distinction explicit at the type level.</summary>
public readonly record struct WorkloadPresenceSignal(WorkloadPresenceSignalKind Kind)
{
    /// <summary>The host observes nothing for the instance (a DEFINITIVE absence — not a fault).</summary>
    public static WorkloadPresenceSignal Absent => new(WorkloadPresenceSignalKind.Absent);

    /// <summary>Present, reporting a spec hash equal to the desired one (identity anchor satisfied).</summary>
    public static WorkloadPresenceSignal PresentMatching => new(WorkloadPresenceSignalKind.PresentMatching);

    /// <summary>Present, reporting a spec hash that differs from the desired one (mid-rollout / stale sidecar).</summary>
    public static WorkloadPresenceSignal PresentMismatched => new(WorkloadPresenceSignalKind.PresentMismatched);
}

/// <summary>The presence verdict for an instance under a host health mode.</summary>
public enum WorkloadPresenceSignalKind
{
    /// <summary>Nothing observed (definitive absence).</summary>
    Absent = 0,

    /// <summary>Present, with a spec hash that matches the desired spec.</summary>
    PresentMatching = 1,

    /// <summary>Present, with a spec hash that differs from the desired spec.</summary>
    PresentMismatched = 2,

    /// <summary>The presence read was inconclusive (store fault) — unknown, never offline.</summary>
    Unknown = 3,
}
