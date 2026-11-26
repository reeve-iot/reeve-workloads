using Reeve.Workloads.Abstractions.Contracts;
using Reeve.Workloads.Abstractions.Domain;

namespace Reeve.Workloads.Abstractions;

/// <summary>What a write would require of the node, computed the way the write itself would compute it. A host
/// compares the answer with what its operator accepted, and hands the fingerprint back with the write.</summary>
/// <remarks>The library reads these facts and does not judge them: whether a requirement is acceptable is the host's
/// decision, made before it calls <c>IWorkloadLifecycleService</c>, the parameter edit or the update initiator.</remarks>
public interface IWorkloadRequirementsService
{
    /// <summary>The requirements of installing a module at a version, with the operator's install-time values.</summary>
    Task<WorkloadRequirementsResult> ForInstallAsync(
        WorkloadInstallRequirementsQuery query, CancellationToken cancellationToken = default);

    /// <summary>The requirements before and after editing one workload's parameters, merged as the edit merges them.</summary>
    Task<WorkloadRequirementsResult> ForParameterEditAsync(
        Guid workloadInstanceId,
        IReadOnlyDictionary<string, string?> newValues,
        CancellationToken cancellationToken = default);

    /// <summary>The requirements before and after updating a module instance to a version, from the update's own plan
    /// built with the values and adoptions the initiation will submit.</summary>
    Task<WorkloadRequirementsResult> ForUpdateAsync(
        Guid groupInstanceId,
        string targetVersion,
        IReadOnlyDictionary<string, string?>? operatorParameters = null,
        IReadOnlyCollection<string>? adoptDeclaredResourceWorkloadIds = null,
        CancellationToken cancellationToken = default);
}

/// <summary>An install candidate. <see cref="Provider"/> null means the effective provider of the orchestrator settings.</summary>
public sealed record WorkloadInstallRequirementsQuery
{
    public required string GroupId { get; init; }

    /// <summary>Where the manifest comes from: the host's own key, handed to the manifest source unchanged.</summary>
    public required string SourceKey { get; init; }

    /// <summary>The version, or null for a bundled module, which publishes no versions: its bundled manifest is
    /// read, and a module that publishes declarations is not found without one.</summary>
    public string? Version { get; init; }

    /// <summary>The operator's install-time values, applied to every workload the way an install applies them.</summary>
    public IReadOnlyDictionary<string, string?> Parameters { get; init; } = new Dictionary<string, string?>();

    public WorkloadProviderId? Provider { get; init; }
}

/// <summary>Why a requirements read has no answer, or that it has one.</summary>
public enum WorkloadRequirementsStatus
{
    /// <summary><see cref="WorkloadRequirementsResult.Read"/> is set.</summary>
    Found = 0,

    /// <summary>No such instance, module, version or declared workload.</summary>
    NotFound = 1,

    /// <summary>The values cannot be resolved; <see cref="WorkloadRequirementsResult.Message"/> says why, as the write would.</summary>
    Invalid = 2,

    /// <summary>The workload is declared, but the host refused its bootstrap, so there is nothing to read the
    /// requirements from; <see cref="WorkloadRequirementsResult.Message"/> carries the host's reason.</summary>
    HostRefused = 3,
}

/// <summary>A requirements read: an answer, or the reason there is none.</summary>
public sealed record WorkloadRequirementsResult
{
    public required WorkloadRequirementsStatus Status { get; init; }

    public WorkloadRequirementsRead? Read { get; init; }

    public string? Message { get; init; }

    public static WorkloadRequirementsResult Found(WorkloadRequirementsRead read)
        => new() { Status = WorkloadRequirementsStatus.Found, Read = read };

    public static WorkloadRequirementsResult NotFound(string message)
        => new() { Status = WorkloadRequirementsStatus.NotFound, Message = message };

    public static WorkloadRequirementsResult Invalid(string message)
        => new() { Status = WorkloadRequirementsStatus.Invalid, Message = message };

    public static WorkloadRequirementsResult HostRefused(string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        return new() { Status = WorkloadRequirementsStatus.HostRefused, Message = reason };
    }
}

/// <summary>Requirements per workload id before and after a write, what changed, and the fingerprint of after.</summary>
public sealed record WorkloadRequirementsRead
{
    /// <summary>Null for an install, where nothing is deployed yet.</summary>
    public IReadOnlyDictionary<string, WorkloadPrivilegeRequirements>? Before { get; init; }

    public required IReadOnlyDictionary<string, WorkloadPrivilegeRequirements> After { get; init; }

    /// <summary>For each workload present before and after.</summary>
    public required IReadOnlyDictionary<string, WorkloadPrivilegeRequirementsDiff> Changes { get; init; }

    public required IReadOnlyList<string> AddedWorkloadIds { get; init; }

    public required IReadOnlyList<string> RemovedWorkloadIds { get; init; }

    /// <summary>The fingerprint of <see cref="After"/>, which a write carries back as its precondition.</summary>
    public required string Fingerprint { get; init; }
}
