using Microsoft.Extensions.Logging;
using System.Security.Cryptography;
using System.Text;
using Reeve.Workloads.Abstractions;
using Reeve.Workloads.Abstractions.Contracts.Deployment;
using Reeve.Workloads.Abstractions.Domain;
using Reeve.Workloads.Abstractions.Domain.DesiredSpec;
using Reeve.Workloads.Abstractions.Domain.Entities;
using Reeve.Workloads.Abstractions.Providers;
using Reeve.Workloads.Domain;
using Reeve.Workloads.Domain.DesiredSpec;

namespace Reeve.Workloads.Core;

internal sealed class WorkloadParameterEditService : IWorkloadParameterEditService
{
    private readonly IWorkloadInstanceRepository repository;
    private readonly IActiveWorkloadIntentProvider intentProvider;
    private readonly IWorkloadReconcileInputsProvider inputsProvider;
    private readonly IWorkloadReconcileNudge reconcileNudge;
    private readonly IWorkloadClock clock;
    private readonly WorkloadDeviceGroupIdWriter deviceGroupIdWriter;
    private readonly ILogger<WorkloadParameterEditService> logger;
    private readonly WorkloadParameterSecretCodec parameterSecrets;
    private readonly UnpinnedReferenceRolloutGuard referenceRolloutGuard;
    private readonly IWorkloadAuditSink auditSink;
    private readonly IWorkloadProviderRegistry providerRegistry;

    public WorkloadParameterEditService(
        IWorkloadInstanceRepository repository,
        IActiveWorkloadIntentProvider intentProvider,
        IWorkloadReconcileInputsProvider inputsProvider,
        IWorkloadReconcileNudge reconcileNudge,
        IWorkloadClock clock,
        WorkloadDeviceGroupIdWriter deviceGroupIdWriter,
        ILogger<WorkloadParameterEditService> logger,
        WorkloadParameterSecretCodec parameterSecrets,
        UnpinnedReferenceRolloutGuard referenceRolloutGuard,
        IWorkloadAuditSink auditSink,
        IWorkloadProviderRegistry providerRegistry)
    {
        this.repository = repository;
        this.intentProvider = intentProvider;
        this.inputsProvider = inputsProvider;
        this.reconcileNudge = reconcileNudge ?? throw new ArgumentNullException(nameof(reconcileNudge));
        this.clock = clock;
        this.deviceGroupIdWriter = deviceGroupIdWriter;
        this.logger = logger;
        this.parameterSecrets = parameterSecrets ?? throw new ArgumentNullException(nameof(parameterSecrets));
        this.referenceRolloutGuard = referenceRolloutGuard ?? throw new ArgumentNullException(nameof(referenceRolloutGuard));
        this.auditSink = auditSink ?? throw new ArgumentNullException(nameof(auditSink));
        this.providerRegistry = providerRegistry ?? throw new ArgumentNullException(nameof(providerRegistry));
    }

    public async Task<WorkloadParameterEditResult> RebindPasswordReferenceAsync(
        Guid workloadInstanceId,
        string parameterName,
        string expectedStoredLiteral,
        string reference,
        Guid? actorUserId,
        bool isVersionPinned,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(parameterName);
        ArgumentNullException.ThrowIfNull(expectedStoredLiteral);
        ArgumentException.ThrowIfNullOrWhiteSpace(reference);
        if (!reference.StartsWith("ref:", StringComparison.Ordinal))
            return WorkloadParameterEditResult.ValidationFailed("The password binding must be a reference.");

        var instance = await repository.GetByIdAsync(workloadInstanceId, cancellationToken).ConfigureAwait(false);
        if (instance is null) return WorkloadParameterEditResult.NotFound();
        if (instance.IsOwnershipReleased(providerRegistry) || !instance.CanMutate)
            return WorkloadParameterEditResult.ValidationFailed(
                $"The workload instance is {instance.State} and its install-time parameters cannot be rebound.");

        var answer = await inputsProvider.GetInputsAsync(instance, cancellationToken).ConfigureAwait(false);
        if (answer.Refusal is { } refusal) return WorkloadParameterEditResult.HostRefused(refusal);
        if (answer.Inputs is not { } inputs) return WorkloadParameterEditResult.NotFound();
        var parameter = inputs.WorkloadManifest.Parameters.FirstOrDefault(p =>
            string.Equals(p.Name, parameterName, StringComparison.Ordinal));
        if (parameter is null || parameter.Type != WorkloadParameterType.Password)
            return WorkloadParameterEditResult.ValidationFailed("Only a declared password parameter can be rebound.");

        var activeIntentId = await intentProvider.GetActiveIntentIdAsync(instance.GroupInstanceId, cancellationToken)
            .ConfigureAwait(false);
        if (activeIntentId is { } intentId) return WorkloadParameterEditResult.Conflict(intentId);

        var stored = instance.GetResolvedParameters();
        var clearStored = parameterSecrets.Unprotect(inputs.WorkloadManifest, stored);
        if (!clearStored.TryGetValue(parameterName, out var current) || current is null)
            return WorkloadParameterEditResult.PreconditionFailed(
                "The stored password changed since it was read; nothing was written.",
                WorkloadParameterEditFailureCode.StoredValueMismatch);
        if (current.StartsWith("ref:", StringComparison.Ordinal))
            return WorkloadParameterEditResult.PreconditionFailed(
                "The stored password is already a reference; nothing was written.",
                WorkloadParameterEditFailureCode.AlreadyReference);
        if (!CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(current), Encoding.UTF8.GetBytes(expectedStoredLiteral)))
            return WorkloadParameterEditResult.PreconditionFailed(
                "The stored password changed since it was read; nothing was written.",
                WorkloadParameterEditFailureCode.StoredValueMismatch);

        var next = stored.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        next[parameterName] = reference;
        var now = clock.UtcNow;
        try
        {
            referenceRolloutGuard.PinHashToLiteral(instance, parameterName, reference, current, now, isVersionPinned);
        }
        catch (CryptographicException)
        {
            return WorkloadParameterEditResult.ValidationFailed(
                "The reference rollout state could not be protected; nothing was written.");
        }
        instance.SetResolvedParameters(next, now);
        try
        {
            await repository.UpdateAsync(instance, cancellationToken).ConfigureAwait(false);
        }
        catch (WorkloadConcurrencyConflictException)
        {
            return WorkloadParameterEditResult.PreconditionFailed(
                "The workload changed while the password reference was being rebound; nothing was written.",
                WorkloadParameterEditFailureCode.ConcurrentChange);
        }

        await auditSink.WriteAsync(new WorkloadAuditEntry(
            actorUserId,
            "workload.password_reference_rebound",
            "WorkloadInstance",
            instance.Id,
            new { ParameterName = parameterName, Reference = reference }), CancellationToken.None).ConfigureAwait(false);
        return WorkloadParameterEditResult.Success();
    }

    public async Task<WorkloadParameterEditResult> UpdateParametersAsync(
        Guid workloadInstanceId,
        IReadOnlyDictionary<string, string?> newValues,
        Guid? actorUserId,
        WorkloadWritePrecondition precondition,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(newValues);
        ArgumentNullException.ThrowIfNull(precondition);
        ArgumentException.ThrowIfNullOrWhiteSpace(precondition.RequirementsFingerprint, nameof(precondition));

        var instance = await repository.GetByIdAsync(workloadInstanceId, cancellationToken).ConfigureAwait(false);
        if (instance is null)
        {
            return WorkloadParameterEditResult.NotFound();
        }

        if (instance.IsOwnershipReleased(providerRegistry) && !instance.IsHeldByHost)
        {
            logger.LogInformation(
                "workloads.parameter_edit: instance={InstanceId} REJECTED — Reeve released ownership of the instance ({State}); a disowned/mid-switch instance is re-adopted first, not edited day-2 (no state mutated).",
                instance.Id, instance.State);
            return WorkloadParameterEditResult.ValidationFailed(
                $"Reeve released ownership of the workload instance ({instance.State}) and its install-time parameters cannot be " +
                "edited. Re-adopt a disowned or mid-switch instance first.");
        }

        if (instance.IsHeldByHost)
        {
            logger.LogInformation(
                "workloads.parameter_edit: instance={InstanceId} REJECTED — the host holds it suspended; only the host resumes it (no state mutated).",
                instance.Id);
            return WorkloadParameterEditResult.ValidationFailed(
                "The host holds the workload instance suspended; its install-time parameters cannot be edited until the host resumes it.");
        }

        if (!instance.CanMutate)
        {
            logger.LogInformation(
                "workloads.parameter_edit: instance={InstanceId} REJECTED — the instance is {State}; its install-time parameters cannot be edited (no state mutated).",
                instance.Id, instance.State);
            return WorkloadParameterEditResult.ValidationFailed(
                $"The workload instance is {instance.State} and its install-time parameters cannot be edited.");
        }

        var answer = await inputsProvider.GetInputsAsync(instance, cancellationToken).ConfigureAwait(false);
        if (answer.Refusal is { } refusal)
        {
            return WorkloadParameterEditResult.HostRefused(refusal);
        }

        if (answer.Inputs is not { } inputs)
        {
            return WorkloadParameterEditResult.NotFound();
        }

        var manifest = inputs.WorkloadManifest;
        var provider = inputs.EffectiveProvider;
        newValues = WorkloadParameterResolver.WithoutUnsupportedUserNamespace(newValues, manifest, provider);

        var activeIntentId = await intentProvider
            .GetActiveIntentIdAsync(instance.GroupInstanceId, cancellationToken)
            .ConfigureAwait(false);
        if (activeIntentId is { } intentId)
        {
            return WorkloadParameterEditResult.Conflict(intentId);
        }

        var rejectedRestoreBindings = newValues.Keys
            .Where(k => k.StartsWith("existingClaim.", StringComparison.Ordinal))
            .OrderBy(k => k, StringComparer.Ordinal)
            .ToList();
        if (rejectedRestoreBindings.Count > 0)
        {
            logger.LogWarning(
                "workloads.parameter_edit: instance={InstanceId} rejected — reserved restore binding(s) {Keys} are install-time only and cannot be applied via a day-2 parameter edit (fail-closed; no state mutated).",
                instance.Id, string.Join(", ", rejectedRestoreBindings));
            return WorkloadParameterEditResult.ValidationFailed(
                $"Parameter(s) {string.Join(", ", rejectedRestoreBindings)} bind a retained volume (restore) and are " +
                "install-time only — a restore cannot be applied to an already-materialized instance via a parameter " +
                "edit. Uninstall and reinstall to restore into a fresh instance.");
        }

        var rejectedResizeOverrides = newValues.Keys
            .Where(k => k.StartsWith(WorkloadDesiredSpecFactory.VolumeSizeOverrideParameterPrefix, StringComparison.Ordinal))
            .OrderBy(k => k, StringComparer.Ordinal)
            .ToList();
        if (rejectedResizeOverrides.Count > 0)
        {
            logger.LogWarning(
                "workloads.parameter_edit: instance={InstanceId} rejected — reserved volume-size override(s) {Keys} are set only via the upward-only resize endpoint, not a day-2 parameter edit (fail-closed; no state mutated).",
                instance.Id, string.Join(", ", rejectedResizeOverrides));
            return WorkloadParameterEditResult.ValidationFailed(
                $"Parameter(s) {string.Join(", ", rejectedResizeOverrides)} set a volume's requested size and are " +
                "reserved — a resize must go through the host's upward-only volume-resize operation, not a " +
                "parameter edit.");
        }

        var rejectedResourceOverrides = newValues.Keys
            .Where(k => WorkloadParameterResolver.ResourceOverrideParameterNames.Contains(k, StringComparer.Ordinal))
            .Where(k => !manifest.Parameters.Any(p => string.Equals(p.Name, k, StringComparison.Ordinal)))
            .OrderBy(k => k, StringComparer.Ordinal)
            .ToList();
        if (rejectedResourceOverrides.Count > 0)
        {
            logger.LogWarning(
                "workloads.parameter_edit: instance={InstanceId} rejected — reserved resource override(s) {Keys} are set only via the resources endpoint, not a day-2 parameter edit (fail-closed; no state mutated).",
                instance.Id, string.Join(", ", rejectedResourceOverrides));
            return WorkloadParameterEditResult.ValidationFailed(
                $"Parameter(s) {string.Join(", ", rejectedResourceOverrides)} set the workload's CPU/memory sizing and " +
                "are reserved — change them through the host's resources operation, not a parameter edit.");
        }

        var stored = instance.GetResolvedParameters();
        var merged = WorkloadParameterEditMerge.Merge(stored, newValues);

        IReadOnlyDictionary<string, string?> newResolved;
        try
        {
            newResolved = WorkloadParameterResolver.Resolve(manifest, merged, provider);
        }
        catch (WorkloadParameterResolutionException ex)
        {
            return WorkloadParameterEditResult.ValidationFailed(ex.Message);
        }

        try
        {
            WorkloadParameterAcceptList.ThrowIfUndeclared(instance.WorkloadId, newValues.Keys, newResolved);
        }
        catch (WorkloadParameterValidationException ex)
        {
            logger.LogWarning(
                "workloads.parameter_edit: instance={InstanceId} rejected — undeclared parameter name(s) submitted; install and the day-2 edit share one accept-list (fail-closed; no state mutated). {Message}",
                instance.Id, ex.Message);
            return WorkloadParameterEditResult.ValidationFailed(ex.Message);
        }

        var invalidPlacement = new List<string>();
        foreach (var name in WorkloadDesiredSpecFactory.NodeSelectorParameterNames(manifest))
        {
            if (!newValues.TryGetValue(name, out var raw)
                || string.IsNullOrWhiteSpace(raw)
                || string.Equals(raw, WorkloadParameterMaskPolicy.MaskSentinel, StringComparison.Ordinal))
            {
                continue;
            }

            invalidPlacement.AddRange(WorkloadNodeSelectorGrammar.DescribeInvalidLabels(raw));
        }

        if (WorkloadNodeSelectorGrammar.RefusalMessage(invalidPlacement) is { } placementRefusal)
        {
            logger.LogWarning(
                "workloads.parameter_edit: instance={InstanceId} rejected — the submitted node selector is not a valid Kubernetes placement (fail-closed; no state mutated). {Problems}",
                instance.Id, string.Join(" ", invalidPlacement));
            return WorkloadParameterEditResult.ValidationFailed(placementRefusal);
        }

        if (precondition.AttachmentPins.Keys.Any(id => !string.Equals(id, instance.WorkloadId, StringComparison.Ordinal)))
        {
            return WorkloadParameterEditResult.PreconditionFailed(
                $"A parameter edit of workload '{instance.WorkloadId}' pins only that workload; nothing was written.");
        }

        var fingerprint = WorkloadRequirementsFacts.Fingerprint(
            WorkloadRequirementsFacts.ForWorkload(instance.WorkloadId, manifest, newResolved, provider));
        if (!string.Equals(fingerprint, precondition.RequirementsFingerprint, StringComparison.Ordinal))
        {
            logger.LogInformation(
                "workloads.parameter_edit: instance={InstanceId} rejected — its requirements are not the ones the host judged (no state mutated).",
                instance.Id);
            return WorkloadParameterEditResult.PreconditionFailed(
                $"The requirements of workload '{instance.WorkloadId}' changed since they were judged; nothing was written.");
        }

        merged = await deviceGroupIdWriter
            .ApplyAsync(manifest, merged, newResolved, cancellationToken)
            .ConfigureAwait(false);

        var now = clock.UtcNow;
        var restarted = WorkloadPasswordParameterChanges.HasChanged(manifest, stored, merged, parameterSecrets);
        instance.SetResolvedParameters(parameterSecrets.ProtectAgainstStored(manifest, merged, stored), now);
        instance.SetAppliedSpecHash(null, now);
        if (restarted)
        {
            instance.MarkRestart(now);
        }

        if (precondition.AttachmentPins.TryGetValue(instance.WorkloadId, out var pin))
        {
            instance.PinAttachment(pin, now);
        }

        instance.BeginOperatorRedeploy("Applying operator parameter edit.", now);

        await repository.UpdateAsync(instance, cancellationToken).ConfigureAwait(false);
        await NudgeReconcileAsync(instance.Id, cancellationToken).ConfigureAwait(false);

        logger.LogInformation(
            "workloads.parameter_edit: instance={InstanceId} actor={ActorUserId} applied → state={State} restarted={Restarted}",
            instance.Id,
            actorUserId,
            instance.State,
            restarted);

        return WorkloadParameterEditResult.Success();
    }

    private Task NudgeReconcileAsync(Guid instanceId, CancellationToken cancellationToken)
        => reconcileNudge.NudgeInstanceAsync(instanceId, "workload-parameter-edit", cancellationToken);

}
