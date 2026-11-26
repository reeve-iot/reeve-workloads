namespace Reeve.Workloads.Abstractions.Providers;

/// <summary>Resolves an opaque workload parameter reference supplied by the host.</summary>
public interface IWorkloadParameterReferenceResolver
{
    /// <summary>Resolves one reference without interpreting its format.</summary>
    ValueTask<WorkloadReferenceResolution> ResolveAsync(string reference, CancellationToken cancellationToken = default);
}

/// <summary>The outcome of resolving a workload parameter reference.</summary>
public abstract record WorkloadReferenceResolution
{
    private WorkloadReferenceResolution() { }

    /// <summary>A reference resolved to its value.</summary>
    public sealed record Success(string Value, bool IsVersionPinned = false) : WorkloadReferenceResolution;

    /// <summary>A reference could not be resolved.</summary>
    public sealed record Failure(WorkloadReferenceFailureKind Kind, string Reason) : WorkloadReferenceResolution;
}

/// <summary>The reason a workload parameter reference could not be resolved.</summary>
public enum WorkloadReferenceFailureKind
{
    NotFound,
    Forbidden,
    Unavailable,
    UnknownProvider,
}
