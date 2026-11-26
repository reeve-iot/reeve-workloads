using Reeve.Workloads.Abstractions;
using Reeve.Workloads.Abstractions.Domain;
using Reeve.Workloads.Abstractions.Domain.DesiredSpec;

namespace Reeve.Workloads.Domain.DesiredSpec;

/// <summary>Thrown by <see cref="WorkloadDesiredSpecFactory"/> for a fail-CLOSED image-resolution error.
/// The reconcile engine treats a compute-time throw non-destructively (no apply/teardown); install-preview surfaces it as a blocking error.</summary>
public sealed class WorkloadImageResolutionException : Exception
{
    public WorkloadImageResolutionException(string message, WorkloadImageResolutionFailure reason)
        : base(message)
    {
        Reason = reason;
    }

    /// <summary>Which resolution rule failed — lets a caller distinguish "not configured" from
    /// "configured but unpinned images are disallowed", two very different operator remedies.</summary>
    public WorkloadImageResolutionFailure Reason { get; }
}

/// <summary>The specific resolution rule that failed (see <see cref="WorkloadImageResolutionException"/>).</summary>
public enum WorkloadImageResolutionFailure
{
    /// <summary>No <c>Workloads:ProductImages</c> entry exists for the component (also rejects a misused external component).</summary>
    NoProductImageConfig,

    /// <summary>The product-image entry exists but has no <c>ref</c> (registry/repository) configured.</summary>
    MissingRef,

    /// <summary>The entry resolved tag-only (no digest) while <c>allowUnpinnedImages</c> is not enabled.</summary>
    UnpinnedImageBlocked,
}
