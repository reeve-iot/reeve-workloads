using Reeve.Workloads.Abstractions;
using Reeve.Workloads.Abstractions.Domain.Entities;
using Reeve.Workloads.Abstractions.Providers;
using Reeve.Workloads.Domain.DesiredSpec;

namespace Reeve.Workloads.Core;

internal sealed class WorkloadReferenceRolloutService(
    IWorkloadInstanceRepository repository,
    IWorkloadReconcileInputsProvider inputsProvider,
    IActiveWorkloadIntentProvider activeIntentProvider,
    IWorkloadParameterReferenceResolver referenceResolver,
    WorkloadParameterSecretCodec parameterSecrets,
    IWorkloadClock clock,
    IWorkloadReconcileNudge reconcileNudge,
    UnpinnedReferenceRolloutGuard guard) : IWorkloadReferenceRolloutService
{
    public async Task<IReadOnlyList<WorkloadReferenceRolloutStatus>> GetPendingAsync(
        Guid workloadInstanceId, CancellationToken cancellationToken = default)
    {
        var instance = await repository.GetByIdAsync(workloadInstanceId, cancellationToken).ConfigureAwait(false)
            ?? throw new KeyNotFoundException($"Workload instance '{workloadInstanceId}' was not found.");
        var result = await inputsProvider.GetInputsAsync(instance, cancellationToken).ConfigureAwait(false);
        if (result.Inputs is not { } inputs) return [];
        var replay = await activeIntentProvider.TryGetActiveReplayAsync(
            instance.GroupInstanceId, instance.WorkloadId, cancellationToken).ConfigureAwait(false);
        var candidateParameters = replay?.StoredParameters ?? inputs.ResolvedParameters;
        DeployableParameterBuilder.Result candidate;
        try
        {
            candidate = await new DeployableParameterBuilder(parameterSecrets, referenceResolver)
                .BuildAsync(inputs.WorkloadManifest, candidateParameters, inputs.EffectiveProvider,
                    replay is null ? DeployableParameterApplyContext.Reconcile
                        : replay.Direction == WorkloadUpdateDirection.RollingBack
                            ? DeployableParameterApplyContext.UpdateRollback
                            : DeployableParameterApplyContext.IntentReplay,
                    replay?.StoredParameters is null
                        && replay is { Direction: Reeve.Workloads.Abstractions.Domain.Entities.WorkloadUpdateDirection.Forward }
                        ? replay.SubmittedParameters : null,
                    passwordParameterNames: replay?.PasswordParameterNames,
                    cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (WorkloadParameterReferenceResolutionException ex)
        {
            return [new(ex.ParameterName, ex.Reference, WorkloadReferenceRolloutState.ResolutionFailed)];
        }
        catch (WorkloadParameterResolutionException)
        {
            return [];
        }
        return guard.GetPending(instance, candidate.UnpinnedReferences, candidate.Values, candidate.PasswordNames)
            .Select(item => new WorkloadReferenceRolloutStatus(item.ParameterName, item.Reference, item.State))
            .ToArray();
    }

    public async Task ApproveCurrentAsync(Guid workloadInstanceId, CancellationToken cancellationToken = default)
    {
        var instance = await repository.GetByIdAsync(workloadInstanceId, cancellationToken).ConfigureAwait(false)
            ?? throw new KeyNotFoundException($"Workload instance '{workloadInstanceId}' was not found.");
        var result = await inputsProvider.GetInputsAsync(instance, cancellationToken).ConfigureAwait(false);
        if (result.Inputs is not { } inputs)
            throw new InvalidOperationException("Current workload inputs are unavailable; the reference rollout was not approved.");
        var replay = await activeIntentProvider.TryGetActiveReplayAsync(
            instance.GroupInstanceId, instance.WorkloadId, cancellationToken).ConfigureAwait(false);
        var candidate = await new DeployableParameterBuilder(parameterSecrets, referenceResolver)
            .BuildAsync(inputs.WorkloadManifest, replay?.StoredParameters ?? inputs.ResolvedParameters, inputs.EffectiveProvider,
                replay?.Direction == WorkloadUpdateDirection.RollingBack
                    ? DeployableParameterApplyContext.UpdateRollback
                    : DeployableParameterApplyContext.RolloutApproval,
                replay?.StoredParameters is null
                    && replay is { Direction: Reeve.Workloads.Abstractions.Domain.Entities.WorkloadUpdateDirection.Forward }
                    ? replay.SubmittedParameters : null,
                passwordParameterNames: replay?.PasswordParameterNames,
                cancellationToken: cancellationToken).ConfigureAwait(false);
        await guard.ApproveAsync(instance, candidate.UnpinnedReferences, candidate.Values, candidate.PasswordNames,
            repository, clock, cancellationToken).ConfigureAwait(false);
        await reconcileNudge.NudgeInstanceAsync(instance.Id, "workload-reference-rollout", cancellationToken).ConfigureAwait(false);
    }
}
