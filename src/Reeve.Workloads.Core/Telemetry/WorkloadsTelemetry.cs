using System.Diagnostics;
using System.Reflection;

namespace Reeve.Workloads.Core.Telemetry;

/// <summary>The library's single telemetry identity: one meter name and one activity-source name, both
/// <c>Reeve.Workloads</c>. A host turns the whole library on with two lines and no knowledge of what is
/// inside it.</summary>
/// <remarks>Deliberately ONE of each. A name per subsystem reads tidier in this repository and worse in the
/// host, where it becomes a list that has to be kept in step with a library it does not control; the
/// dimension that actually distinguishes a reconcile from a provider call belongs in the instrument name,
/// where a query can reach it. <c>System.Diagnostics.DiagnosticSource</c> is in the framework, so none of
/// this adds a dependency.</remarks>
public static class WorkloadsTelemetry
{
    /// <summary>The meter name to pass to <c>AddMeter</c>, and the activity-source name to pass to
    /// <c>AddSource</c>. One string for both: they describe the same library.</summary>
    public const string Name = "Reeve.Workloads";

    /// <summary>The version reported alongside the meter and the activity source — the informational version
    /// of the assembly, which is what a scrape has to correlate a shape change against.</summary>
    public static string Version { get; } =
        typeof(WorkloadsTelemetry).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? typeof(WorkloadsTelemetry).Assembly.GetName().Version?.ToString()
        ?? "unknown";

    internal static readonly ActivitySource Source = new(Name, Version);

    internal const string ReconcileSpan = "workload.reconcile";

    internal const string ProviderSpan = "workload.provider";
}
