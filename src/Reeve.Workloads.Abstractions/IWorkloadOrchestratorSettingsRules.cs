using Reeve.Workloads.Abstractions.Contracts;
using Reeve.Workloads.Abstractions.Providers;

namespace Reeve.Workloads.Abstractions;

/// <summary>The pure rules over a settings document: what makes it valid, how it is masked for an operator, how a
/// masked secret resolves against the stored one, and which fields are secret. No I/O, nothing stored.</summary>
public interface IWorkloadOrchestratorSettingsRules
{
    /// <summary>Every reason <paramref name="document"/> is not valid, each naming its field; empty when valid.
    /// A mask sentinel that survived <see cref="ResolveMasked"/> is a problem: it has no secret to stand for.</summary>
    IReadOnlyList<WorkloadSettingsProblem> Validate(WorkloadOrchestratorSettings document);

    /// <summary>The document with every set secret replaced by <see cref="WorkloadSecretMask.Sentinel"/>.</summary>
    WorkloadOrchestratorSettings Mask(WorkloadOrchestratorSettings document);

    /// <summary>The submitted document with each secret sent as <see cref="WorkloadSecretMask.Sentinel"/> replaced
    /// by the one <paramref name="stored"/> holds: a Docker token by its host pattern, a Kubernetes kubeconfig by
    /// its field.</summary>
    /// <exception cref="WorkloadOrchestratorSettingsValidationException">A secret is masked and nothing is stored
    /// to keep. The message names the field.</exception>
    WorkloadOrchestratorSettings ResolveMasked(WorkloadOrchestratorSettings submitted, WorkloadOrchestratorSettings stored);

    /// <summary>Every non-empty secret in <paramref name="document"/>, for a host to protect or redact.</summary>
    IReadOnlyList<WorkloadSecretField> Secrets(WorkloadOrchestratorSettings document);
}

/// <summary>One reason a document is not valid.</summary>
/// <param name="Message">Operator-facing text that names the field.</param>
public sealed record WorkloadSettingsProblem(string Message);

/// <summary>One secret value in a settings document.</summary>
/// <param name="Provider">The provider whose entry holds it.</param>
/// <param name="Path">A JSON pointer (RFC 6901) from the provider's entry to the string, e.g. <c>/kubeconfig</c>.</param>
/// <param name="Value">The value as the document holds it.</param>
public sealed record WorkloadSecretField(WorkloadProviderId Provider, string Path, string Value);
