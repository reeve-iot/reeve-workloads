using Reeve.Workloads.Abstractions;
using Reeve.Workloads.Abstractions.Domain.DesiredSpec;
using Reeve.Workloads.Abstractions.Domain.Entities;
using Reeve.Workloads.Abstractions.Providers;
using Reeve.Workloads.Domain.DesiredSpec;

namespace Reeve.Workloads.Core;

internal static class WorkloadReconcileDesiredSpec
{
    internal static ValueTask<DeployableParameterBuilder.Result> BuildParametersAsync(
        WorkloadParameterSecretCodec secrets,
        IWorkloadParameterReferenceResolver references,
        WorkloadReconcileInputs inputs,
        ActiveWorkloadIntentReplay? replay,
        CancellationToken cancellationToken)
        => new DeployableParameterBuilder(secrets, references)
            .BuildAsync(inputs.WorkloadManifest,
                replay?.StoredParameters ?? inputs.ResolvedParameters, inputs.EffectiveProvider,
                replay is null ? DeployableParameterApplyContext.Reconcile
                    : replay.Direction == WorkloadUpdateDirection.RollingBack
                        ? DeployableParameterApplyContext.UpdateRollback
                        : DeployableParameterApplyContext.IntentReplay,
                replay?.StoredParameters is null
                    && replay is { Direction: WorkloadUpdateDirection.Forward }
                    ? replay.SubmittedParameters : null,
                storedSchema: inputs.WorkloadManifest,
                passwordParameterNames: replay?.PasswordParameterNames,
                cancellationToken: cancellationToken);

    internal static IReadOnlyDictionary<string, string?> ReferencesForHash(
        WorkloadInstance instance, DeployableParameterBuilder.Result built)
        => UnpinnedReferenceRolloutGuard.ProjectReferencesForHash(instance, built.ReferencesForHash);

    internal static WorkloadDesiredSpec ComputeFresh(
        WorkloadInstance instance,
        WorkloadReconcileInputs inputs,
        IReadOnlyDictionary<string, string?> resolvedParameters,
        IReadOnlyDictionary<string, string?> referencesForHash)
        => WorkloadDesiredSpecFactory.ComputeWithParameterReferences(
            inputs.WorkloadManifest,
            inputs.Settings,
            resolvedParameters,
            referencesForHash,
            inputs.Bootstrap,
            inputs.EffectiveProvider,
            inputs.Namer,
            instance.GroupInstanceId,
            instance.GroupId,
            instance.Id,
            inputs.InstallationId,
            inputs.ProductImages);

    internal static async Task<string> FreshSpecHashAsync(
        WorkloadParameterSecretCodec secrets,
        IWorkloadParameterReferenceResolver references,
        WorkloadInstance instance,
        WorkloadReconcileInputs inputs,
        CancellationToken cancellationToken)
    {
        var built = await BuildParametersAsync(secrets, references, inputs, replay: null, cancellationToken)
            .ConfigureAwait(false);
        return ComputeFresh(instance, inputs, built.Values, ReferencesForHash(instance, built)).SpecHash;
    }

    internal static async Task<string> DesiredSpecHashAsync(
        WorkloadParameterSecretCodec secrets,
        IWorkloadParameterReferenceResolver references,
        WorkloadInstance instance,
        WorkloadReconcileInputs inputs,
        ActiveWorkloadIntentReplay? replay,
        CancellationToken cancellationToken)
        => replay?.Spec.SpecHash
            ?? await FreshSpecHashAsync(secrets, references, instance, inputs, cancellationToken).ConfigureAwait(false);
}
