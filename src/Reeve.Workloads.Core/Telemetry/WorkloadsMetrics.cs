using System.Diagnostics.Metrics;
using Reeve.Workloads.Abstractions.Contracts;

namespace Reeve.Workloads.Core.Telemetry;

/// <summary>Every instrument this library publishes, on one meter named <see cref="WorkloadsTelemetry.Name"/>.
/// Until this existed an <c>ILogger</c> was the only output, so a host could not answer "is the pass keeping
/// cadence", "which instance is eating the barrier" or "how long does apply take on this provider" without
/// grepping text.</summary>
/// <remarks>STATIC, like the <see cref="System.Diagnostics.ActivitySource"/> beside it, and for the same two
/// reasons. A <see cref="Meter"/> owned by the DI container would have to reach the reconciler and the
/// provider registry through their constructors, and both are public types whose signatures a minor release
/// must not change. And the scoping it would buy is scoping this library has no use for: a meter named after
/// the library describes the library, of which a host runs exactly one.
/// <para>It takes no <c>IMeterFactory</c> either — that lives in
/// <c>Microsoft.Extensions.Diagnostics.Abstractions</c>, and a package reference is a real cost to every
/// consumer for a convenience this type does not need. <c>System.Diagnostics.DiagnosticSource</c> is already
/// in the framework.</para></remarks>
public static class WorkloadsMetrics
{
    private static readonly Meter Meter;
    private static readonly Histogram<double> ReconcileDuration;
    private static readonly Counter<long> ReconcileErrors;
    private static readonly Histogram<double> PassDuration;
    private static readonly Histogram<double> ProviderCallDuration;
    private static readonly Counter<long> BarrierTimeouts;

    private static volatile Func<(int Depth, int Settling)>? queueSource;

    static WorkloadsMetrics()
    {
        Meter = new Meter(WorkloadsTelemetry.Name, WorkloadsTelemetry.Version);

        ReconcileDuration = Meter.CreateHistogram<double>(
            "reeve.workloads.reconcile.duration", "s",
            "How long one reconcile of one workload instance took, by trigger and outcome.");

        ReconcileErrors = Meter.CreateCounter<long>(
            "reeve.workloads.reconcile.errors", "{error}",
            "Reconciles that did not complete, by trigger and kind (a lost write race is not a fault).");

        PassDuration = Meter.CreateHistogram<double>(
            "reeve.workloads.pass.duration", "s",
            "How long a fleet-wide pass took from enqueue to a quiet queue, by trigger.");

        ProviderCallDuration = Meter.CreateHistogram<double>(
            "reeve.workloads.provider.call.duration", "s",
            "How long one call into a provider took, by provider, operation and outcome.");

        BarrierTimeouts = Meter.CreateCounter<long>(
            "reeve.workloads.barrier.timeouts", "{timeout}",
            "Instances that applied a spec and never reached Healthy within the stagger barrier.");

        Meter.CreateObservableGauge(
            "reeve.workloads.queue.depth", () => Observe(state => state.Depth), "{instance}",
            "Workload instances with reconcile work scheduled, running or waiting on a backoff.");

        Meter.CreateObservableGauge(
            "reeve.workloads.queue.settling", () => Observe(state => state.Settling), "{instance}",
            "Workload instances that applied a spec and are being polled until they report Healthy.");
    }

    /// <summary>Points the two queue gauges at a reconcile queue. Called once by the reconciler that owns the
    /// queue; before it is called the gauges report nothing at all, which is the honest answer for a host
    /// whose reconciler is not the leader and has no queue to describe.</summary>
    public static void ObserveQueue(Func<(int Depth, int Settling)> source) => queueSource = source;

    /// <summary>One reconcile of one instance. <paramref name="outcome"/> is the small closed vocabulary the
    /// reconciler uses — applied, settling, noop, conflict, fault, unknown — so a dashboard can separate work
    /// that changed something from work that only looked.</summary>
    public static void Reconciled(WorkloadReconcileTrigger trigger, string outcome, TimeSpan elapsed)
        => ReconcileDuration.Record(
            elapsed.TotalSeconds,
            new KeyValuePair<string, object?>("trigger", trigger.ToString()),
            new KeyValuePair<string, object?>("outcome", outcome));

    /// <summary>A reconcile that did not complete. <paramref name="kind"/> separates a thrown fault from a
    /// lost write race, which is the concurrency token doing its job rather than an error to page on.</summary>
    public static void ReconcileFailed(WorkloadReconcileTrigger trigger, string kind)
        => ReconcileErrors.Add(
            1,
            new KeyValuePair<string, object?>("trigger", trigger.ToString()),
            new KeyValuePair<string, object?>("kind", kind));

    /// <summary>One fleet-wide pass, measured from the first enqueue to a quiet queue — the number that
    /// answers "is the reconciler keeping cadence".</summary>
    public static void PassCompleted(WorkloadReconcileTrigger trigger, TimeSpan elapsed)
        => PassDuration.Record(
            elapsed.TotalSeconds,
            new KeyValuePair<string, object?>("trigger", trigger.ToString()));

    /// <summary>One call into a provider's SPI, recorded at the registry so it covers a provider this
    /// repository did not write.</summary>
    public static void ProviderCalled(WorkloadProviderId provider, string operation, string outcome, TimeSpan elapsed)
        => ProviderCallDuration.Record(
            elapsed.TotalSeconds,
            new KeyValuePair<string, object?>("provider", provider.Value),
            new KeyValuePair<string, object?>("operation", operation),
            new KeyValuePair<string, object?>("outcome", outcome));

    /// <summary>An instance released its settling slot on the timeout rather than by becoming Healthy.</summary>
    public static void BarrierTimedOut() => BarrierTimeouts.Add(1);

    private static IEnumerable<Measurement<int>> Observe(Func<(int Depth, int Settling), int> select)
    {
        if (queueSource is not { } source)
        {
            yield break;
        }

        yield return new Measurement<int>(select(source()));
    }
}
