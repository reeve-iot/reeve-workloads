namespace Reeve.Workloads.Abstractions;

/// <summary>What <see cref="IWorkloadReconcileInputsProvider.GetInputsAsync"/> found for one instance. Several
/// answers, not a nullable, because "the workload is gone" and "it cannot render" call for different replies:
/// the first is a 404 and a quiet no-op, the second is a refusal a reader has to name.</summary>
/// <remarks>At most one of <see cref="Inputs"/> and <see cref="Refusal"/> is set; neither set is
/// <see cref="NotDeclared"/>.</remarks>
public sealed record WorkloadReconcileInputsResult
{
    private WorkloadReconcileInputsResult()
    {
    }

    /// <summary>The installed manifest no longer declares the workload. Nothing is rendered, and nothing is
    /// wrong: teardown is the uninstall flow's.</summary>
    public static WorkloadReconcileInputsResult NotDeclared { get; } = new();

    /// <summary>The inputs the reconciler renders from.</summary>
    public static WorkloadReconcileInputsResult Declared(WorkloadReconcileInputs inputs)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        return new() { Inputs = inputs };
    }

    /// <summary>The workload is declared, but the host refused its bootstrap, so there is nothing to render. A
    /// reader reports the reason rather than treating the workload as gone, so a null or blank reason throws.</summary>
    public static WorkloadReconcileInputsResult HostRefused(string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        return new() { Refusal = reason };
    }

    /// <summary>The workload is declared and not bound to a provider yet, but its group's workloads are bound to
    /// different providers, so no provider can be chosen for it without an operator. <see cref="Refusal"/> carries
    /// the reason, so a reader that names a refusal names this one too; the reconciler holds the instance with it as
    /// the state message (PendingConfiguration, or Failed where that is not reachable) instead of retrying silently. A null or blank reason throws.</summary>
    public static WorkloadReconcileInputsResult ProviderBindingConflict(string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        return new() { Refusal = reason, IsProviderBindingConflict = true };
    }

    /// <summary>The inputs, set only for <see cref="Declared"/>.</summary>
    public WorkloadReconcileInputs? Inputs { get; private init; }

    /// <summary>Why nothing can be rendered: the host's reason for <see cref="HostRefused"/>, the library's for
    /// <see cref="ProviderBindingConflict"/>. Unset otherwise.</summary>
    public string? Refusal { get; private init; }

    /// <summary>True only for <see cref="ProviderBindingConflict"/>.</summary>
    public bool IsProviderBindingConflict { get; private init; }
}
