using Reeve.Workloads.Abstractions.Domain.DesiredSpec;

namespace Reeve.Workloads.Abstractions;

/// <summary>Resolves the effective desired spec for a workload instance: an active update intent's durably-recorded target spec wins.
/// When no active replay was read in the pass, an answer not derived from <c>computeFresh</c> is ignored and the fresh spec is used.</summary>
public interface IDesiredSpecResolver
{
    /// <summary>Returns the intent's target spec when one is active for <paramref name="pluginInstanceId"/>/
    /// <paramref name="workloadId"/>, else <paramref name="computeFresh"/>(). An answer not derived from <paramref name="computeFresh"/> is used only when the pass read an active replay.</summary>
    Task<WorkloadDesiredSpec> ResolveAsync(
        Guid pluginInstanceId,
        string workloadId,
        Func<WorkloadDesiredSpec> computeFresh,
        CancellationToken cancellationToken);
}
