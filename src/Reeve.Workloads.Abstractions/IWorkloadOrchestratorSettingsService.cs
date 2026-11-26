using Reeve.Workloads.Abstractions.Contracts;

namespace Reeve.Workloads.Abstractions;

/// <summary>Reads the workload-orchestrator settings the host serves through
/// <see cref="IWorkloadOrchestratorSettingsSource"/>. Secrets are returned MASKED; the plaintext read is
/// <see cref="IWorkloadOrchestratorSecretReader"/>.</summary>
public interface IWorkloadOrchestratorSettingsService
{
    /// <summary>Returns the current settings (secrets masked), the effective/auto-detected provider, and the
    /// compiled-provider catalog. Never emits a plaintext secret.</summary>
    Task<WorkloadOrchestratorSettingsResponse> GetAsync(CancellationToken cancellationToken = default);

    /// <summary>Runs the provider capability probe for the requested (or effective) provider. Returns a
    /// well-formed result with ProbeAvailable = false when no probe is registered — never throws or 404s.</summary>
    Task<WorkloadProviderProbeResult> TestAsync(WorkloadProviderId? requestedProvider, CancellationToken cancellationToken = default);
}

/// <summary>Thrown when a submitted document cannot be resolved, for example a masked secret with nothing stored to
/// keep. Subclasses ArgumentException so it maps to 400 Bad Request automatically.</summary>
public sealed class WorkloadOrchestratorSettingsValidationException(string message) : ArgumentException(message);
