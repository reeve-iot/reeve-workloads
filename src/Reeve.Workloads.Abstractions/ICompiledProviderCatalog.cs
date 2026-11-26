using Reeve.Workloads.Abstractions.Contracts;

namespace Reeve.Workloads.Abstractions;

/// <summary>The workload providers REGISTERED in this backend — whatever the host wired, first-party or not.
/// Auto-detect and the UI only ever offer what this reports; an id absent from it is one nothing can run.</summary>
public interface ICompiledProviderCatalog
{
    /// <summary>Every registered provider, in registration order.</summary>
    IReadOnlyList<WorkloadProviderInfo> GetProviders();

    /// <summary>True when <paramref name="provider"/> is registered in this backend and selectable.</summary>
    bool IsCompiledIn(WorkloadProviderId provider);
}
