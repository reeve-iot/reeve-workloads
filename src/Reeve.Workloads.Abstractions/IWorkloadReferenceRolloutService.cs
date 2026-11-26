namespace Reeve.Workloads.Abstractions;

/// <summary>Approves the exact currently resolved values of unpinned password references for one instance.</summary>
public interface IWorkloadReferenceRolloutService
{
    /// <summary>
    /// Lists pending reference rollout states. A resolver failure may be listed before pinning is known, and only
    /// the first resolver failure is returned. An empty list means no rollout status was found or the current
    /// candidate could not be evaluated because of another parameter/configuration fault. The gate may defer
    /// enforcement while mutation is disabled.
    /// </summary>
    Task<IReadOnlyList<WorkloadReferenceRolloutStatus>> GetPendingAsync(Guid workloadInstanceId, CancellationToken cancellationToken = default);

    Task ApproveCurrentAsync(Guid workloadInstanceId, CancellationToken cancellationToken = default);
}

/// <summary>Status of one password reference or prior reference binding for an instance.</summary>
/// <param name="ParameterName">The password parameter name.</param>
/// <param name="Reference">The current opaque reference, or null when the binding was removed or replaced by a literal or pinned reference.</param>
/// <param name="State">The rollout state requiring host attention.</param>
public sealed record WorkloadReferenceRolloutStatus(string ParameterName, string? Reference, WorkloadReferenceRolloutState State);

/// <summary>The reason a password reference or prior binding is listed for rollout attention.</summary>
public enum WorkloadReferenceRolloutState
{
    /// <summary>The current reference binding or resolved value has not been approved.</summary>
    AwaitingApproval,

    /// <summary>The host resolver could not resolve the current reference; approving cannot clear this state.</summary>
    ResolutionFailed,

    /// <summary>The protected approval state or its Data Protection key could not be read; approval can recover it.</summary>
    ApprovalStateUnavailable,
}
