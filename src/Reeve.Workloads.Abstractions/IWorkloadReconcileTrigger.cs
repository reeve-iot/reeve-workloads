namespace Reeve.Workloads.Abstractions;

/// <summary>Nudges the leader-only reconciler to run an EXISTING pass — a trigger optimization, never a
/// correctness path: the periodic drift pass is the backstop, and a nudge off the leader is a safe no-op.</summary>
public interface IWorkloadReconcileTrigger
{
    /// <summary>Requests a reconcile of one instance, returning as soon as the pass is SCHEDULED. The token may
    /// abort the scheduling but never stops a running pass, which observes the reconciler's own lifetime.</summary>
    Task RequestInstanceAsync(Guid instanceId, string reason, CancellationToken cancellationToken = default);

    /// <summary>Requests a full reconcile of every instance, returning as soon as the pass is SCHEDULED. The token
    /// may abort the scheduling but never stops a running pass.</summary>
    Task RequestAllAsync(string reason, CancellationToken cancellationToken = default);
}

/// <summary>The reasons a host passes to <see cref="IWorkloadReconcileTrigger"/>. The reason is what the
/// reconciler attributes a pass by; a caller that invents its own string is attributed as a local nudge, which
/// is what an unrecognised one is. WIRE-ish: these strings appear in logs and telemetry, so treat them as names.</summary>
public static class WorkloadReconcileReasons
{
    /// <summary>A presence transition the host observed: a workload became present or absent.</summary>
    public const string Presence = "presence";

    /// <summary>A provider's list-watch or event stream saw one instance change.</summary>
    public const string Watch = "watch";

    /// <summary>A provider's watch reconnected and asked for a full pass to catch what it missed.</summary>
    public const string WatchResync = "watch-resync";

    /// <summary>The host told the library its orchestrator settings document changed.</summary>
    public const string SettingsChanged = "settings-changed";
}
