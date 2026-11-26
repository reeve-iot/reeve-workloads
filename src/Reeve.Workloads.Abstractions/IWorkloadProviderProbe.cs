using Reeve.Workloads.Abstractions.Contracts;

namespace Reeve.Workloads.Abstractions;

/// <summary>SPI for a provider's capability probe (<c>POST …/test</c>). Each implementation handles one
/// <see cref="WorkloadProviderId"/>; when none is registered the settings service returns <see cref="WorkloadProviderProbeResult.ProbeAvailable"/> = false rather than failing.</summary>
public interface IWorkloadProviderProbe
{
    /// <summary>The provider this probe implements.</summary>
    WorkloadProviderId Provider { get; }

    /// <summary>Probes using the supplied (already-decrypted) settings. Implementations must not throw for a
    /// merely-unreachable provider — report reachability via <see cref="WorkloadProviderProbeResult.ProbeAvailable"/> instead.</summary>
    Task<WorkloadProviderProbeResult> ProbeAsync(WorkloadOrchestratorSettings settings, CancellationToken cancellationToken = default);
}
