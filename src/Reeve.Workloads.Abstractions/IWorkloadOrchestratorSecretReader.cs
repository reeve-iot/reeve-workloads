using Reeve.Workloads.Abstractions.Contracts;

namespace Reeve.Workloads.Abstractions;

/// <summary>The provider plane's read of orchestrator settings with registry-credential tokens in plaintext.
/// Separate from the settings port so no implementer can inherit it by accident.</summary>
public interface IWorkloadOrchestratorSecretReader
{
    /// <summary>Settings with tokens decrypted, for provider-plane use only — never into a response or a log.
    /// Required deliberately: as a default interface method, forgetting it authenticated with the mask.</summary>
    Task<WorkloadOrchestratorSettings> GetDecryptedSettingsAsync(CancellationToken cancellationToken = default);
}
