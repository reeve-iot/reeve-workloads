using Reeve.Workloads.Abstractions.Providers;

namespace Reeve.Workloads.Core;

/// <summary>Fail-closed resolver for hosts that do not provide reference support.</summary>
public sealed class NoWorkloadParameterReferenceResolver : IWorkloadParameterReferenceResolver
{
    /// <inheritdoc />
    public ValueTask<WorkloadReferenceResolution> ResolveAsync(string reference, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult<WorkloadReferenceResolution>(
            new WorkloadReferenceResolution.Failure(
                WorkloadReferenceFailureKind.UnknownProvider,
                "references are not supported by this host"));
    }
}
