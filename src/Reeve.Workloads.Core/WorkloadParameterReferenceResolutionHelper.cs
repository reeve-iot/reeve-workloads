using Reeve.Workloads.Abstractions.Contracts.Deployment;
using Reeve.Workloads.Abstractions.Providers;
using Reeve.Workloads.Domain.DesiredSpec;

namespace Reeve.Workloads.Core;

/// <summary>Resolves password references while retaining the original reference strings for hash construction.</summary>
public static class WorkloadParameterReferenceResolutionHelper
{
    /// <summary>Resolved values are used for rendering; references are kept separately so their resolved value is
    /// never used as a hash input.</summary>
    public sealed record Result(
        IReadOnlyDictionary<string, string?> Values,
        IReadOnlyDictionary<string, string?> ReferencesForHash,
        IReadOnlyDictionary<string, string?> UnpinnedReferences);

    /// <summary>Resolves declared parameters and then asks the host to resolve each password value beginning with
    /// <c>ref:</c>. Reference strings are opaque and passed verbatim to the host.</summary>
    public static ValueTask<Result> ResolveAsync(
        WorkloadManifest workload,
        IReadOnlyDictionary<string, string?> operatorValues,
        WorkloadProviderDescriptor provider,
        IWorkloadParameterReferenceResolver referenceResolver,
        IEnumerable<string>? passwordParameterNames = null,
        CancellationToken cancellationToken = default)
        => ResolveCoreAsync(workload, operatorValues, provider, referenceResolver, passwordParameterNames,
            validateNonPasswordParameters: true, cancellationToken: cancellationToken);

    internal static ValueTask<Result> ResolveForStoredSpecAsync(
        WorkloadManifest workload,
        IReadOnlyDictionary<string, string?> operatorValues,
        WorkloadProviderDescriptor provider,
        IWorkloadParameterReferenceResolver referenceResolver,
        IEnumerable<string>? passwordParameterNames = null,
        CancellationToken cancellationToken = default)
        => ResolveCoreAsync(workload, operatorValues, provider, referenceResolver, passwordParameterNames,
            validateNonPasswordParameters: false, cancellationToken: cancellationToken);

    private static async ValueTask<Result> ResolveCoreAsync(
        WorkloadManifest workload,
        IReadOnlyDictionary<string, string?> operatorValues,
        WorkloadProviderDescriptor provider,
        IWorkloadParameterReferenceResolver referenceResolver,
        IEnumerable<string>? passwordParameterNames,
        bool validateNonPasswordParameters,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(workload);
        ArgumentNullException.ThrowIfNull(operatorValues);
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(referenceResolver);

        var resolvedParameters = validateNonPasswordParameters
            ? WorkloadParameterResolver.Resolve(workload, operatorValues, provider)
            : WorkloadParameterResolver.ResolveForStoredSpec(workload, operatorValues, provider);
        var values = resolvedParameters
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        var references = new Dictionary<string, string?>(StringComparer.Ordinal);
        var unpinnedReferences = new Dictionary<string, string?>(StringComparer.Ordinal);
        var passwordNames = WorkloadPasswordNames.For(workload, passwordParameterNames);
        foreach (var name in passwordNames)
            if (!values.ContainsKey(name) && operatorValues.TryGetValue(name, out var submittedValue))
                values[name] = submittedValue;
        foreach (var parameterName in passwordNames)
        {
            if (!values.TryGetValue(parameterName, out var value)
                || value is null
                || !value.StartsWith("ref:", StringComparison.Ordinal))
            {
                continue;
            }

            cancellationToken.ThrowIfCancellationRequested();
            WorkloadReferenceResolution resolution;
            try
            {
                resolution = await referenceResolver.ResolveAsync(value, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                throw new WorkloadParameterReferenceResolutionException(parameterName, value,
                    WorkloadReferenceFailureKind.Unavailable, ex.Message);
            }

            switch (resolution)
            {
                case WorkloadReferenceResolution.Success success:
                    if (string.IsNullOrWhiteSpace(success.Value))
                    {
                        throw new WorkloadParameterReferenceResolutionException(parameterName, value,
                            WorkloadReferenceFailureKind.NotFound, "The resolved password value is blank.");
                    }
                    values[parameterName] = success.Value;
                    references[parameterName] = value;
                    if (!success.IsVersionPinned) unpinnedReferences[parameterName] = value;
                    break;
                case WorkloadReferenceResolution.Failure failure:
                    throw new WorkloadParameterReferenceResolutionException(parameterName, value, failure.Kind, failure.Reason);
                default:
                    throw new WorkloadParameterReferenceResolutionException(parameterName, value,
                        WorkloadReferenceFailureKind.Unavailable, "The resolver returned an invalid outcome.");
            }
        }

        return new Result(values, references, unpinnedReferences);
    }
}

/// <summary>A password reference could not be resolved; the exception is classified as a configuration hold.</summary>
public sealed class WorkloadParameterReferenceResolutionException : WorkloadParameterResolutionException
{
    /// <summary>Creates a failure that identifies the configured reference and host-provided reason.</summary>
    public WorkloadParameterReferenceResolutionException(
        string parameterName,
        string reference,
        WorkloadReferenceFailureKind kind,
        string reason)
        : base(parameterName, $"Reference '{reference}' could not be resolved ({kind}): {reason}")
    {
        Reference = reference;
        FailureKind = kind;
        FailureReason = reason;
    }

    /// <summary>The configured reference string.</summary>
    public string Reference { get; }

    /// <summary>The typed failure reason.</summary>
    public WorkloadReferenceFailureKind FailureKind { get; }

    /// <summary>The resolver's reason.</summary>
    public string FailureReason { get; }
}
