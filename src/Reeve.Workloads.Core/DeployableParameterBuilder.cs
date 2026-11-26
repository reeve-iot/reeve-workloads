using Reeve.Workloads.Abstractions.Contracts.Deployment;
using Reeve.Workloads.Abstractions.Providers;
using Reeve.Workloads.Domain.DesiredSpec;

namespace Reeve.Workloads.Core;

internal enum DeployableParameterApplyContext
{
    Reconcile,
    IntentReplay,
    UpdateCandidate,
    UpdateRollback,
    RollbackTargetRender,
    RolloutApproval,
}

internal sealed class DeployableParameterBuilder(
    WorkloadParameterSecretCodec secrets,
    IWorkloadParameterReferenceResolver references)
{
    internal sealed record Result(
        IReadOnlyDictionary<string, string?> Stored,
        IReadOnlyDictionary<string, string?> Values,
        IReadOnlyDictionary<string, string?> ReferencesForHash,
        IReadOnlyDictionary<string, string?> UnpinnedReferences,
        IReadOnlySet<string> PasswordNames,
        DeployableParameterApplyContext Context)
    {
        internal string? StoredSpecPolicyFallbackParameter { get; init; }
    }

    internal async ValueTask<Result> BuildAsync(
        WorkloadManifest schema,
        IReadOnlyDictionary<string, string?> stored,
        WorkloadProviderDescriptor provider,
        DeployableParameterApplyContext context,
        IReadOnlyDictionary<string, string?>? submitted = null,
        IReadOnlySet<string>? ambiguousKeys = null,
        WorkloadManifest? storedSchema = null,
        IReadOnlyCollection<string>? passwordParameterNames = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(schema);
        ArgumentNullException.ThrowIfNull(stored);
        var merged = submitted is null || submitted.Count == 0
            ? stored
            : Update.WorkloadUpdateCandidateParameters.Merge(schema, stored, submitted, ambiguousKeys);
        var deployStored = passwordParameterNames is null ? secrets.Unprotect(storedSchema ?? schema, stored)
            : secrets.UnprotectNamed(storedSchema ?? schema, stored, passwordParameterNames);
        var deployValues = submitted is null || submitted.Count == 0
            ? deployStored
            : Update.WorkloadUpdateCandidateParameters.Merge(schema, deployStored, submitted, ambiguousKeys);
        if (submitted is not null && passwordParameterNames is { Count: > 0 })
        {
            var declared = schema.Parameters.Select(parameter => parameter.Name).ToHashSet(StringComparer.Ordinal);
            Dictionary<string, string?>? extended = null;
            foreach (var name in passwordParameterNames)
            {
                if (declared.Contains(name) || ambiguousKeys?.Contains(name) == true || !submitted.TryGetValue(name, out var value)
                    || value == "***") continue;
                extended ??= new Dictionary<string, string?>(deployValues, StringComparer.Ordinal);
                extended[name] = value;
            }
            if (extended is not null) deployValues = extended;
        }
        var passwordNames = WorkloadPasswordNames.For(schema, passwordParameterNames);
        var validatesNonPasswordPolicies = context is DeployableParameterApplyContext.Reconcile
            or DeployableParameterApplyContext.UpdateCandidate
            or DeployableParameterApplyContext.RollbackTargetRender;
        string? fallbackParameter = null;
        if (!validatesNonPasswordPolicies)
        {
            try
            {
                _ = WorkloadParameterResolver.Resolve(schema, deployValues, provider);
            }
            catch (WorkloadParameterResolutionException ex) when (!passwordNames.Contains(ex.ParameterName))
            {
                fallbackParameter = ex.ParameterName;
            }
        }
        var resolved = validatesNonPasswordPolicies
            ? await WorkloadParameterReferenceResolutionHelper.ResolveAsync(
                schema, deployValues, provider, references, passwordNames, cancellationToken).ConfigureAwait(false)
            : await WorkloadParameterReferenceResolutionHelper.ResolveForStoredSpecAsync(
                schema, deployValues, provider, references, passwordNames, cancellationToken).ConfigureAwait(false);
        return new Result(merged, resolved.Values, resolved.ReferencesForHash, resolved.UnpinnedReferences, passwordNames, context)
        {
            StoredSpecPolicyFallbackParameter = fallbackParameter,
        };
    }
}
