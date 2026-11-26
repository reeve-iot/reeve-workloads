using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Reeve.Workloads.Abstractions;
using Reeve.Workloads.Abstractions.Contracts;
using Reeve.Workloads.Abstractions.Contracts.Deployment;
using Reeve.Workloads.Abstractions.Domain;
using Reeve.Workloads.Abstractions.Domain.DesiredSpec;
using Reeve.Workloads.Abstractions.Domain.Entities;
using Reeve.Workloads.Abstractions.Domain.Entities.Documents;
using Reeve.Workloads.Abstractions.Domain.Network;
using Reeve.Workloads.Abstractions.Providers;
using Reeve.Workloads.Core.Health;
using Reeve.Workloads.Core.Providers;
using Reeve.Workloads.Core.Update;
using Reeve.Workloads.Domain;
using Reeve.Workloads.Domain.DesiredSpec;

namespace Reeve.Workloads.Core;

internal sealed class WorkloadReconcileEngine : IWorkloadReconcileEngine
{
    private readonly IWorkloadReconcileInputsProvider inputsProvider;
    private readonly IDesiredSpecResolver desiredSpecResolver;
    private readonly IWorkloadProviderRegistry providerRegistry;
    private readonly IWorkloadInstanceRepository repository;
    private readonly WorkloadHealthContributorDispatch healthContributors;
    private readonly IWorkloadClock clock;
    private readonly ILogger<WorkloadReconcileEngine> logger;
    private readonly IRetainedVolumeService retainedVolumeService;
    private readonly IActiveWorkloadIntentProvider activeIntentProvider;
    private readonly WorkloadFleetStatusCache? fleetStatus;
    private readonly IWorkloadParameterReferenceResolver parameterReferenceResolver;
    private readonly WorkloadParameterSecretCodec parameterSecrets;
    private readonly UnpinnedReferenceRolloutGuard referenceRolloutGuard;
    private readonly IWorkloadCredentialPort credentialPort;
    private readonly WorkloadReconcilerOptions options;

    public WorkloadReconcileEngine(
        IWorkloadReconcileInputsProvider inputsProvider,
        IDesiredSpecResolver desiredSpecResolver,
        IWorkloadProviderRegistry providerRegistry,
        IWorkloadInstanceRepository repository,
        WorkloadHealthContributorDispatch healthContributors,
        IWorkloadClock clock,
        ILogger<WorkloadReconcileEngine> logger,
        IRetainedVolumeService retainedVolumeService,
        IActiveWorkloadIntentProvider activeIntentProvider,
        IWorkloadParameterReferenceResolver parameterReferenceResolver,
        WorkloadParameterSecretCodec parameterSecrets,
        WorkloadFleetStatusCache? fleetStatus,
        UnpinnedReferenceRolloutGuard referenceRolloutGuard,
        IWorkloadCredentialPort credentialPort,
        IOptions<WorkloadReconcilerOptions>? options = null)
    {
        this.inputsProvider = inputsProvider;
        this.desiredSpecResolver = desiredSpecResolver;
        this.providerRegistry = providerRegistry;
        this.repository = repository;
        this.healthContributors = healthContributors;
        this.clock = clock;
        this.logger = logger;
        this.retainedVolumeService = retainedVolumeService;
        this.activeIntentProvider = activeIntentProvider;
        this.fleetStatus = fleetStatus;
        this.parameterReferenceResolver = parameterReferenceResolver ?? throw new ArgumentNullException(nameof(parameterReferenceResolver));
        this.parameterSecrets = parameterSecrets ?? throw new ArgumentNullException(nameof(parameterSecrets));
        this.referenceRolloutGuard = referenceRolloutGuard ?? throw new ArgumentNullException(nameof(referenceRolloutGuard));
        this.credentialPort = credentialPort ?? throw new ArgumentNullException(nameof(credentialPort));
        this.options = options?.Value ?? new WorkloadReconcilerOptions();
    }

    public async Task<WorkloadReconcileOutcome> ReconcileInstanceAsync(
        WorkloadInstance instance, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(instance);

        if (instance.IsHeldByHost || instance.IsHostHoldPending)
        {
            await RevokeHeldCredentialsAsync(instance, cancellationToken).ConfigureAwait(false);
        }

        var answer = await inputsProvider.GetInputsAsync(instance, cancellationToken).ConfigureAwait(false);
        if (answer is { IsProviderBindingConflict: true, Refusal: { } conflict })
        {
            return await HoldOnProviderBindingConflictAsync(instance, conflict, cancellationToken).ConfigureAwait(false);
        }

        if (answer.Refusal is { } refusal)
        {
            logger.LogWarning(
                "Workload {InstanceId} ({GroupId}/{WorkloadId}): the host refused its bootstrap — {Refusal}. Reconcile "
                + "no-op; nothing applied or saved.",
                instance.Id, instance.GroupId, instance.WorkloadId, refusal);
            return WorkloadReconcileOutcome.NoOp(
                instance.Id, WorkloadReconcileReason.HostRefusedBootstrap, instance.State, refusal);
        }

        if (answer.Inputs is not { } inputs)
        {
            logger.LogInformation(
                "Workload {InstanceId} ({GroupId}/{WorkloadId}) is no longer declared — reconcile no-op (the update that dropped it or the uninstall tears it down).",
                instance.Id, instance.GroupId, instance.WorkloadId);
            return WorkloadReconcileOutcome.NoOp(instance.Id, WorkloadReconcileReason.WorkloadNotDeclared, instance.State);
        }

        ActiveWorkloadIntentReplay? replay;
        try
        {
            replay = await activeIntentProvider.TryGetActiveReplayAsync(
                instance.GroupInstanceId, instance.WorkloadId, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return await ClassifyComputeFailureAsync(instance, ex, cancellationToken).ConfigureAwait(false);
        }

        IReadOnlyDictionary<string, string?> resolvedParameters;
        IReadOnlyDictionary<string, string?> referencesForHash;
        IReadOnlyDictionary<string, string?> unpinnedReferences;
        IReadOnlySet<string> passwordNames;
        try
        {
            var built = await WorkloadReconcileDesiredSpec
                .BuildParametersAsync(parameterSecrets, parameterReferenceResolver, inputs, replay, cancellationToken)
                .ConfigureAwait(false);
            if (built.StoredSpecPolicyFallbackParameter is { } fallbackParameter)
            {
                logger.LogWarning(
                    "Intent replay for workload {InstanceId} found non-password parameter {ParameterName} invalid under the installed schema; using the staged spec values.",
                    instance.Id, fallbackParameter);
            }
            resolvedParameters = built.Values;
            referencesForHash = WorkloadReconcileDesiredSpec.ReferencesForHash(instance, built);
            unpinnedReferences = built.UnpinnedReferences;
            passwordNames = built.PasswordNames;
        }
        catch (WorkloadParameterResolutionException ex)
        {
            return await ClassifyComputeFailureAsync(instance, ex, cancellationToken).ConfigureAwait(false);
        }

        var providerKind = instance.Provider ?? inputs.EffectiveProvider.Id;
        var provider = providerRegistry.TryResolve(providerKind);
        if (provider is null)
        {
            logger.LogDebug(
                "No IWorkloadProvider registered for '{Provider}' — workload {InstanceId} reconcile no-op.",
                providerKind, instance.Id);
            return WorkloadReconcileOutcome.NoOp(instance.Id, WorkloadReconcileReason.NoProviderAvailable, instance.State);
        }

        WorkloadDesiredSpec desired;
        WorkloadDesiredSpec? intentTarget;
        WorkloadDesiredSpec manifestFresh;
        WorkloadDesiredSpec replayHostValues;
        IReadOnlyDictionary<string, string?> renderParameters = resolvedParameters;
        try
        {
            intentTarget = replay?.Spec;
            manifestFresh = WorkloadReconcileDesiredSpec.ComputeFresh(instance, inputs, resolvedParameters, referencesForHash);
            replayHostValues = manifestFresh;
            var tookFresh = false;
            desired = intentTarget ?? await desiredSpecResolver.ResolveAsync(
                instance.GroupInstanceId,
                instance.WorkloadId,
                () =>
                {
                    tookFresh = true;
                    return manifestFresh;
                },
                cancellationToken).ConfigureAwait(false);
            if (replay is null && !tookFresh)
            {
                desired = manifestFresh;
                logger.LogInformation(
                    "Workload {InstanceId}: desired spec used this pass's fresh manifest after an unconfirmed intent read.",
                    instance.Id);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return await ClassifyComputeFailureAsync(instance, ex, cancellationToken).ConfigureAwait(false);
        }


        desired = WorkloadStoredSpecs.WithHostValuesOf(desired, replayHostValues);

        await EnforceSuspendResumeAsync(instance, provider, cancellationToken).ConfigureAwait(false);

        if (inputs.WorkloadManifest.Kind == WorkloadKind.ScanPerNode
            && instance.HasPendingScanRun
            && instance.State != WorkloadInstanceState.Suspended
            && !instance.IsReconcileFrozen)
        {
            if (desired.Kind == WorkloadKind.ScanPerNode)
            {
                return await OrchestrateScanRunAsync(instance, inputs, desired, renderParameters, unpinnedReferences, passwordNames, provider, cancellationToken)
                    .ConfigureAwait(false);
            }

            logger.LogWarning(
                "Scan run {RunId} for workload {InstanceId} is NOT fanning out this pass: the manifest says {ManifestKind} "
                + "but the desired spec carrying the run is {DesiredKind} — the workload's kind is changing under an in-flight "
                + "update. Retaining the marker and creating NO Jobs; the run resumes once the two agree.",
                instance.PendingScanRunId, instance.Id, inputs.WorkloadManifest.Kind, desired.Kind);
        }

        var apply = await ApplyDriftIfAllowedAsync(instance, inputs, desired, renderParameters, unpinnedReferences,
                passwordNames, replay is not null, provider, cancellationToken)
            .ConfigureAwait(false);
        if (apply.ShortCircuit is { } shortCircuit)
        {
            await repository.UpdateAsync(instance, cancellationToken).ConfigureAwait(false);
            return shortCircuit;
        }

        if (apply.ConfigurationSatisfied
            && instance is { State: WorkloadInstanceState.PendingConfiguration, StateReason: WorkloadStateReason.ConfigRequired })
        {
            logger.LogInformation(
                "Workload {InstanceId}: its configuration resolves again and the applied spec is the desired one — leaving "
                + "PendingConfiguration to observe the runtime.",
                instance.Id);
            TransitionSafely(instance, WorkloadInstanceState.Deploying, WorkloadStateReason.Provisioning,
                "Configuration resolved; observing the running workload.", clock.UtcNow, observed: true);
        }

        var driven = await ReadStatusAndDriveAsync(instance, inputs, desired.SpecHash, cancellationToken,
                declaredVolumes: apply.Applied ? null : desired.Volumes,
                mayRecreateMissingObject: !apply.Applied && replay is null,
                appliedThisPass: apply.Applied)
            .ConfigureAwait(false);
        if (apply.ReferenceHold is { } referenceHold)
        {
            TransitionSafely(instance, WorkloadInstanceState.PendingConfiguration, WorkloadStateReason.ConfigRequired,
                referenceHold.Message, clock.UtcNow);
            await repository.UpdateAsync(instance, cancellationToken).ConfigureAwait(false);
            return referenceHold with { ResultingState = instance.State };
        }
        if (apply.Applied)
        {
            return driven with { Action = WorkloadReconcileAction.Applied, Reason = WorkloadReconcileReason.Provisioned };
        }

        if (apply.Deferred is { } deferred)
        {
            if (deferred.Reason == WorkloadReconcileReason.UnknownNoMutation
                && driven.ResultingState != WorkloadInstanceState.Unknown)
            {
                return driven with { PendingApply = deferred.PendingApply };
            }

            return driven with
            {
                Action = deferred.Action,
                Reason = deferred.Reason,
                PendingApply = deferred.PendingApply,
            };
        }

        return driven;
    }

    private readonly record struct ApplyGateResult(
        WorkloadReconcileOutcome? ShortCircuit,
        bool Applied,
        WorkloadReconcileOutcome? Deferred,
        WorkloadReconcileOutcome? ReferenceHold = null,
        bool ConfigurationSatisfied = false)
    {
        public static ApplyGateResult Proceed => new(null, Applied: false, null);
        public static ApplyGateResult InSync => new(null, Applied: false, null, ConfigurationSatisfied: true);
        public static ApplyGateResult AppliedThenProceed => new(null, Applied: true, null);
        public static ApplyGateResult Short(WorkloadReconcileOutcome outcome) => new(outcome, Applied: false, null);

        public static ApplyGateResult DeferredButRead(WorkloadReconcileOutcome outcome) => new(null, Applied: false, outcome);
        public static ApplyGateResult HoldReference(WorkloadReconcileOutcome outcome) => new(null, Applied: false, outcome, outcome);
    }

    private bool Materializes(WorkloadProviderId provider) => providerRegistry.Describe(provider).Materializes;

    private async Task MarkAdoptedRetainedVolumesReattachedAsync(
        WorkloadInstance instance, WorkloadDesiredSpec desired, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (!Materializes(desired.Provider))
        {
            logger.LogInformation(
                "Manual handoff apply of workload {InstanceId} does NOT mark its adopted retained volume(s) reattached — " +
                "nothing is mounted until the operator applies the artifact; the retained row stays active/offerable.",
                instance.Id);
            return;
        }

        var adoptedNames = desired.Volumes
            .Where(v => v.ExistingClaim && !string.IsNullOrWhiteSpace(v.ResourceName))
            .Select(v => v.ResourceName.Trim())
            .ToHashSet(StringComparer.Ordinal);
        if (adoptedNames.Count == 0)
        {
            return;
        }

        try
        {
            var candidates = await retainedVolumeService
                .ListActiveByPluginTypeAsync(instance.GroupId, cancellationToken)
                .ConfigureAwait(false);

            var marked = 0;
            foreach (var row in candidates)
            {
                if (!adoptedNames.Contains(row.ResourceName))
                {
                    continue;
                }

                await retainedVolumeService.MarkReattachedAsync(row.Id, now, cancellationToken).ConfigureAwait(false);
                marked++;
            }

            if (marked > 0)
            {
                logger.LogInformation(
                    "Marked {Count} retained volume(s) reattached after a successful restore-adoption apply of workload {InstanceId} (existingClaim).",
                    marked, instance.Id);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex,
                "Failed to mark adopted retained volumes reattached after applying workload {InstanceId} — continuing (bookkeeping only).",
                instance.Id);
        }
    }

    private async Task EnforceSuspendResumeAsync(
        WorkloadInstance instance, IWorkloadProvider provider, CancellationToken cancellationToken)
    {
        if (instance.State == WorkloadInstanceState.Suspended)
        {
            if (!instance.IsReconcileFrozen)
            {
                await EnsureSuspendedAsync(instance, provider, HeldReason(instance).ToString(), cancellationToken)
                    .ConfigureAwait(false);
            }

            return;
        }

        if (instance.State == WorkloadInstanceState.Deploying
            && instance.StateReason is WorkloadStateReason.OperatorResumed or WorkloadStateReason.ResumedByHost)
        {
            await EnsureResumedAsync(instance, provider, instance.StateReason.ToString(), cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private async Task RevokeHeldCredentialsAsync(WorkloadInstance instance, CancellationToken cancellationToken)
    {
        try
        {
            await credentialPort.RevokeAllAsync(instance.Id, actorUserId: null, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex,
                "Failed to revoke the credentials of host-held workload {InstanceId} — the next pass revokes again.",
                instance.Id);
        }
    }

    private static WorkloadReconcileReason HeldReason(WorkloadInstance instance)
        => instance.StateReason == WorkloadStateReason.OperatorPaused
            ? WorkloadReconcileReason.OperatorPaused
            : WorkloadReconcileReason.SuspendedByHost;

    private async Task EnsureSuspendedAsync(
        WorkloadInstance instance, IWorkloadProvider provider, string cause, CancellationToken cancellationToken)
    {
        if (instance.Provider is null)
        {
            return;
        }

        try
        {
            await provider.SuspendAsync(WorkloadRef.FromInstance(instance), cancellationToken).ConfigureAwait(false);
            logger.LogDebug("Suspended workload {InstanceId} on the provider ({Cause}).", instance.Id, cause);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex,
                "Failed to suspend workload {InstanceId} on the provider ({Cause}) — leaving the state, the reconciler retries next pass.",
                instance.Id, cause);
        }
    }

    private async Task EnsureResumedAsync(
        WorkloadInstance instance, IWorkloadProvider provider, string cause, CancellationToken cancellationToken)
    {
        if (instance.Provider is null)
        {
            return;
        }

        try
        {
            await provider.ResumeAsync(WorkloadRef.FromInstance(instance), cancellationToken).ConfigureAwait(false);
            logger.LogDebug("Resumed workload {InstanceId} on the provider ({Cause}).", instance.Id, cause);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex,
                "Failed to resume workload {InstanceId} on the provider ({Cause}) — leaving the state, the reconciler retries next pass.",
                instance.Id, cause);
        }
    }

    private async Task<WorkloadReconcileOutcome> HoldOnProviderBindingConflictAsync(
        WorkloadInstance instance, string conflict, CancellationToken cancellationToken)
    {
        var before = instance.State;
        var message = $"No provider can be chosen for this workload: {conflict}";
        logger.LogWarning(
            "Workload {InstanceId} ({GroupId}/{WorkloadId}): {Conflict} Holding it until an operator resolves it.",
            instance.Id, instance.GroupId, instance.WorkloadId, conflict);
        var (target, reason) = instance.IsLegalTransition(
                WorkloadInstanceState.PendingConfiguration, WorkloadStateReason.ConfigRequired)
            ? (WorkloadInstanceState.PendingConfiguration, WorkloadStateReason.ConfigRequired)
            : (WorkloadInstanceState.Failed, WorkloadStateReason.DesiredSpecComputeFailed);
        TransitionSafely(instance, target, reason, message, clock.UtcNow);
        await repository.UpdateAsync(instance, cancellationToken).ConfigureAwait(false);
        return new WorkloadReconcileOutcome
        {
            InstanceId = instance.Id,
            Action = instance.State == before ? WorkloadReconcileAction.NoOp : WorkloadReconcileAction.Transitioned,
            Reason = target == WorkloadInstanceState.PendingConfiguration
                ? WorkloadReconcileReason.ConfigurationRequired
                : WorkloadReconcileReason.DesiredSpecComputeFailed,
            ResultingState = instance.State,
            Message = message,
        };
    }

    private async Task<WorkloadReconcileOutcome> ClassifyComputeFailureAsync(
        WorkloadInstance instance, Exception exception, CancellationToken cancellationToken)
    {
        var now = clock.UtcNow;

        if (exception is WorkloadParameterReferenceResolutionException { FailureKind: WorkloadReferenceFailureKind.Unavailable })
        {
            logger.LogWarning(exception,
                "Workload {InstanceId}: a password reference could not be resolved because the secret store is unavailable — "
                + "keeping state {State}, nothing applied or saved; the next pass resolves again.",
                instance.Id, instance.State);
            return WorkloadReconcileOutcome.NoOp(
                instance.Id, WorkloadReconcileReason.ParameterReferenceUnavailable, instance.State, exception.Message);
        }

        var isConfigProblem = exception is WorkloadParameterResolutionException;

        if (isConfigProblem)
        {
            logger.LogWarning(exception,
                "Desired-spec compute failed for workload {InstanceId} on a resolvable config problem — holding PendingConfiguration.",
                instance.Id);
            TransitionSafely(instance, WorkloadInstanceState.PendingConfiguration, WorkloadStateReason.ConfigRequired,
                $"Desired spec cannot be computed — fix the configuration: {exception.Message}", now);
            await repository.UpdateAsync(instance, cancellationToken).ConfigureAwait(false);
            return new WorkloadReconcileOutcome
            {
                InstanceId = instance.Id,
                Action = instance.State == WorkloadInstanceState.PendingConfiguration
                    ? WorkloadReconcileAction.NoOp
                    : WorkloadReconcileAction.Transitioned,
                Reason = WorkloadReconcileReason.ConfigurationRequired,
                ResultingState = instance.State,
                Message = $"Desired spec cannot be computed — fix the configuration: {exception.Message}",
            };
        }

        logger.LogError(exception,
            "Desired-spec compute failed for workload {InstanceId} — marking Failed (not a crash, not a teardown).",
            instance.Id);
        TransitionSafely(instance, WorkloadInstanceState.Failed, WorkloadStateReason.DesiredSpecComputeFailed,
            $"Desired spec compute failed: {exception.Message}", now);
        await repository.UpdateAsync(instance, cancellationToken).ConfigureAwait(false);
        return new WorkloadReconcileOutcome
        {
            InstanceId = instance.Id,
            Action = instance.State == WorkloadInstanceState.Failed
                ? WorkloadReconcileAction.NoOp
                : WorkloadReconcileAction.Transitioned,
            Reason = WorkloadReconcileReason.DesiredSpecComputeFailed,
            ResultingState = instance.State,
            Message = $"Desired spec compute failed: {exception.Message}",
        };
    }

    private async Task<ApplyGateResult> ApplyDriftIfAllowedAsync(
        WorkloadInstance instance,
        WorkloadReconcileInputs inputs,
        WorkloadDesiredSpec desired,
        IReadOnlyDictionary<string, string?> renderParameters,
        IReadOnlyDictionary<string, string?> unpinnedReferences,
        IReadOnlySet<string> passwordNames,
        bool hasValidatedIntentReplay,
        IWorkloadProvider provider,
        CancellationToken cancellationToken)
    {
        var now = clock.UtcNow;
        var reappliesHeldSpec = instance.AppliedSpecHash is null
            && instance.DeployStartedAtUtc is not null
            && string.Equals(instance.DesiredSpecHash, desired.SpecHash, StringComparison.Ordinal);

        if (await CompareAttachmentPinAsync(instance, inputs, desired, provider, cancellationToken)
                .ConfigureAwait(false) is { } networkPark)
        {
            return ApplyGateResult.Short(networkPark);
        }

        var referencesChecked = instance.CanMutate
            && instance.State is not (WorkloadInstanceState.ExternallyManaged or WorkloadInstanceState.Suspended)
            && !inputs.Settings.ReconciliationPaused
            && !instance.ObserveOnly;
        if (referencesChecked)
        {
            var referenceCheck = referenceRolloutGuard.Check(instance, unpinnedReferences, renderParameters, passwordNames);
            if (referenceCheck.ChangedParameter is { } changedParameter)
            {
                var message = referenceCheck.PendingState is { } pending
                    ? $"Password reference rollout state is unavailable ({pending}); restore the Data Protection key ring and approve or pin the reference before applying."
                    : $"Password parameter '{changedParameter}' reference value changed; roll out to apply.";
                instance.SetDesiredSpecHash(desired.SpecHash, now);
                TransitionSafely(instance, WorkloadInstanceState.PendingConfiguration, WorkloadStateReason.ConfigRequired,
                    message, now);
                logger.LogWarning("Workload {InstanceId} is held: {Message}", instance.Id, message);
                await repository.UpdateAsync(instance, cancellationToken).ConfigureAwait(false);
                var held = new WorkloadReconcileOutcome
                {
                    InstanceId = instance.Id,
                    Action = WorkloadReconcileAction.DeferredMutation,
                    Reason = WorkloadReconcileReason.ConfigurationRequired,
                    ResultingState = instance.State,
                    PendingApply = true,
                    Message = message,
                };
                return ApplyGateResult.HoldReference(held);
            }
        }

        var drift = !string.Equals(instance.AppliedSpecHash, desired.SpecHash, StringComparison.Ordinal);
        if (!drift)
        {
            if (instance.AppliedSpecHash is not null
                && Materializes(desired.Provider)
                && desired.Volumes.Any(v => v.ExistingClaim && !string.IsNullOrWhiteSpace(v.ResourceName)))
            {
                await MarkAdoptedRetainedVolumesReattachedAsync(instance, desired, now, cancellationToken).ConfigureAwait(false);
            }

            return referencesChecked && instance.AppliedSpecHash is not null
                ? ApplyGateResult.InSync
                : ApplyGateResult.Proceed;
        }

        if (instance.LastRejectedApplyAtUtc is { } lastRejectedAt
            && now - lastRejectedAt < RejectedSpecRetryCooloff)
        {
            logger.LogDebug(
                "Workload {InstanceId} was rejected by the provider {Elapsed} ago; not re-applying the identical "
                + "spec until the {Cooloff} cool-off elapses.",
                instance.Id, now - lastRejectedAt, RejectedSpecRetryCooloff);
            return ApplyGateResult.Proceed;
        }

        if (instance.State == WorkloadInstanceState.ExternallyManaged)
        {
            instance.SetDesiredSpecHash(desired.SpecHash, now);
            return ApplyGateResult.DeferredButRead(new WorkloadReconcileOutcome
            {
                InstanceId = instance.Id,
                Action = WorkloadReconcileAction.DeferredMutation,
                Reason = WorkloadReconcileReason.ObserveOnly,
                ResultingState = instance.State,
                PendingApply = true,
                Message = "Instance is externally managed (ownership released) — desired spec recorded as pending, runtime untouched.",
            });
        }

        if (instance.State == WorkloadInstanceState.Suspended)
        {
            instance.SetDesiredSpecHash(desired.SpecHash, now);
            var held = HeldReason(instance);
            return ApplyGateResult.DeferredButRead(new WorkloadReconcileOutcome
            {
                InstanceId = instance.Id,
                Action = WorkloadReconcileAction.DeferredMutation,
                Reason = held,
                ResultingState = instance.State,
                PendingApply = true,
                Message = held == WorkloadReconcileReason.OperatorPaused
                    ? "Instance is operator-paused — desired spec recorded as pending, runtime untouched."
                    : "Instance is suspended by the host — desired spec recorded as pending, runtime untouched.",
            });
        }

        if (inputs.Settings.ReconciliationPaused)
        {
            instance.SetDesiredSpecHash(desired.SpecHash, now);
            return ApplyGateResult.DeferredButRead(new WorkloadReconcileOutcome
            {
                InstanceId = instance.Id,
                Action = WorkloadReconcileAction.DeferredMutation,
                Reason = WorkloadReconcileReason.ReconciliationPaused,
                ResultingState = instance.State,
                PendingApply = true,
                Message = "Reconciliation is paused — desired spec recorded as pending, runtime untouched.",
            });
        }

        if (instance.ObserveOnly)
        {
            instance.SetDesiredSpecHash(desired.SpecHash, now);
            return ApplyGateResult.DeferredButRead(new WorkloadReconcileOutcome
            {
                InstanceId = instance.Id,
                Action = WorkloadReconcileAction.DeferredMutation,
                Reason = WorkloadReconcileReason.ObserveOnly,
                ResultingState = instance.State,
                PendingApply = true,
                Message = "Instance is observe-only — desired spec recorded as pending, runtime untouched.",
            });
        }

        if (!instance.CanMutate)
        {
            instance.SetDesiredSpecHash(desired.SpecHash, now);
            var held = new WorkloadReconcileOutcome
            {
                InstanceId = instance.Id,
                Action = WorkloadReconcileAction.DeferredMutation,
                Reason = WorkloadReconcileReason.UnknownNoMutation,
                ResultingState = instance.State,
                PendingApply = true,
                Message = "Instance cannot mutate in its current state — desired spec recorded as pending.",
            };
            return instance.State == WorkloadInstanceState.Unknown
                ? ApplyGateResult.DeferredButRead(held)
                : ApplyGateResult.Short(held);
        }

        var restoreHold = instance.RestoreBindingUnresolved;
        if (!restoreHold
            && !instance.WasDeployed
            && !desired.Volumes.Any(v => v.ExistingClaim && !string.IsNullOrWhiteSpace(v.ResourceName)))
        {
            try
            {
                if (await retainedVolumeService.AnyActiveClaimByInstanceAsync(instance.Id, cancellationToken).ConfigureAwait(false))
                {
                    restoreHold = true;
                    instance.SetRestoreBindingUnresolved(true, now);
                    logger.LogWarning(
                        "Workload {InstanceId} holds an unadopted restore claim but its desired spec carries NO adopted claim (a lost restore binding) — re-deriving the restore hold and RE-SETTING the durable flag (self-heal).",
                        instance.Id);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex,
                    "Could not re-derive the restore hold for workload {InstanceId} (backstop) — proceeding with the ordinary gates.",
                    instance.Id);
            }
        }

        if (restoreHold)
        {
            instance.SetDesiredSpecHash(desired.SpecHash, now);
            logger.LogInformation(
                "Workload {InstanceId} has an UNRESOLVED restore binding — holding PendingConfiguration, no apply, until uninstall+reinstall.",
                instance.Id);
            const string restoreHoldMessage =
                "Restore could not be applied — the instance is held and will NOT deploy a fresh empty volume. " +
                "Uninstall and reinstall to restore (or install without restoring); a restore binding cannot be re-supplied.";
            TransitionSafely(instance, WorkloadInstanceState.PendingConfiguration, WorkloadStateReason.ConfigRequired,
                restoreHoldMessage, now);
            return ApplyGateResult.Short(new WorkloadReconcileOutcome
            {
                InstanceId = instance.Id,
                Action = WorkloadReconcileAction.DeferredMutation,
                Reason = WorkloadReconcileReason.ConfigurationRequired,
                ResultingState = instance.State,
                PendingApply = true,
                Message = restoreHoldMessage,
            });
        }

        IReadOnlyList<string> missingRequired = hasValidatedIntentReplay
            ? Array.Empty<string>()
            : MissingRequiredParameters(inputs, renderParameters);
        if (missingRequired.Count > 0)
        {
            instance.SetDesiredSpecHash(desired.SpecHash, now);
            var summary = string.Join(", ", missingRequired);
            logger.LogInformation(
                "Workload {InstanceId} is missing required parameter(s) [{Missing}] — holding PendingConfiguration (no apply).",
                instance.Id, summary);
            TransitionSafely(instance, WorkloadInstanceState.PendingConfiguration, WorkloadStateReason.ConfigRequired,
                $"Required parameter(s) not yet set: {summary}", now);
            return ApplyGateResult.Short(new WorkloadReconcileOutcome
            {
                InstanceId = instance.Id,
                Action = WorkloadReconcileAction.DeferredMutation,
                Reason = WorkloadReconcileReason.ConfigurationRequired,
                ResultingState = instance.State,
                PendingApply = true,
                Message = $"Required parameter(s) not yet set: {summary}",
            });
        }

        if (!CanApplyFrom(instance.State))
        {
            instance.SetDesiredSpecHash(desired.SpecHash, now);
            logger.LogInformation(
                "Workload {InstanceId} is in state {State} which cannot apply a runtime yet — recording pending hash, no apply.",
                instance.Id, instance.State);
            return ApplyGateResult.Short(new WorkloadReconcileOutcome
            {
                InstanceId = instance.Id,
                Action = WorkloadReconcileAction.DeferredMutation,
                Reason = WorkloadReconcileReason.ConfigurationRequired,
                ResultingState = instance.State,
                PendingApply = true,
                Message = $"Instance state {instance.State} cannot apply a runtime yet — desired spec recorded as pending.",
            });
        }

        if (instance.Provider is null)
        {
            instance.SetProvider(desired.Provider, now);
        }

        instance.SetDesiredSpecHash(desired.SpecHash, now);
        if (instance.State == WorkloadInstanceState.PendingConfiguration)
        {
            TransitionSafely(instance, WorkloadInstanceState.Deploying, WorkloadStateReason.Provisioning,
                "Applying desired spec.", now);
        }

        WorkloadApplyResult applyResult;
        var progress = new WorkloadApplyProgressReporter(
            async (snapshot, ct) =>
            {
                instance.SetProvisioningProgress(snapshot.Percent, snapshot.Message, clock.UtcNow);
                await repository.UpdateAsync(instance, ct).ConfigureAwait(false);
            },
            logger,
            cancellationToken);
        try
        {
            applyResult = await provider.ApplyAsync(desired, progress, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex,
                "Apply THREW for workload {InstanceId} — treating as a failed apply (provider bound on attempt; drift persists, status pass classifies).",
                instance.Id);
            StartDeployOfFailedNewSpec();
            RecordDeployFailure(ex.Message);
            return ApplyGateResult.Proceed;
        }
        finally
        {
            await progress.DrainAsync().ConfigureAwait(false);
        }

        if (!applyResult.Succeeded)
        {
            logger.LogWarning(
                "Apply failed for workload {InstanceId}: {Reason} {Message}",
                instance.Id, applyResult.Reason, applyResult.Message);

            if (applyResult.Reason is WorkloadApplyReason.Rejected or WorkloadApplyReason.FieldManagerConflict)
            {
                instance.MarkSpecRejected(now);
                TransitionSafely(
                    instance,
                    WorkloadInstanceState.Failed,
                    WorkloadStateReason.SpecRejected,
                    applyResult.Message ?? "The provider rejected the desired spec.",
                    now);
            }

            StartDeployOfFailedNewSpec();
            RecordDeployFailure(applyResult.Message ?? applyResult.Reason.ToString());
            return ApplyGateResult.Proceed;
        }

        if (applyResult.AppliedSpecHash is { } echoed && !string.Equals(echoed, desired.SpecHash, StringComparison.Ordinal))
        {
            logger.LogWarning(
                "Provider echoed a DIFFERENT applied spec hash for workload {InstanceId}: desired={DesiredHash} applied={AppliedHash}. Pinning to the desired hash to avoid an apply storm.",
                instance.Id, desired.SpecHash, echoed);
        }

        instance.MarkSpecAccepted(now);
        instance.SetAppliedSpecHash(desired.SpecHash, now);
        referenceRolloutGuard?.RecordApplied(instance, unpinnedReferences, renderParameters, passwordNames, now);
        if (!(reappliesHeldSpec && HoldsDeployState()))
        {
            TransitionSafely(instance, WorkloadInstanceState.Deploying, WorkloadStateReason.Provisioning,
                "Applying desired spec.", now);
        }

        await MarkAdoptedRetainedVolumesReattachedAsync(instance, desired, now, cancellationToken).ConfigureAwait(false);

        return ApplyGateResult.AppliedThenProceed;

        bool HoldsDeployState() => instance.State is WorkloadInstanceState.Deploying or WorkloadInstanceState.Degraded;

        void StartDeployOfFailedNewSpec()
        {
            if (!reappliesHeldSpec && instance.AppliedSpecHash is null && HoldsDeployState())
            {
                instance.StartDeployClock(now);
            }
        }

        void RecordDeployFailure(string detail)
        {
            if (instance.AppliedSpecHash is null && HoldsDeployState())
            {
                TransitionSafely(instance, instance.State, instance.StateReason, $"Apply failed: {detail}", now, observed: true);
            }
        }
    }

    private async Task<WorkloadReconcileOutcome?> CompareAttachmentPinAsync(
        WorkloadInstance instance,
        WorkloadReconcileInputs inputs,
        WorkloadDesiredSpec desired,
        IWorkloadProvider provider,
        CancellationToken cancellationToken)
    {
        if (desired.Network is not { Mode: WorkloadNetworkMode.OperatorNetwork } network)
        {
            return null;
        }

        if (!Materializes(desired.Provider))
        {
            return null;
        }

        if (inputs.Settings.ReconciliationPaused
            || instance.ObserveOnly
            || instance.State == WorkloadInstanceState.ExternallyManaged
            || instance.State == WorkloadInstanceState.Suspended)
        {
            return null;
        }

        var pinned = instance.ReadAttachmentPin();
        if (pinned.Verdict == WorkloadDocumentVerdict.Absent)
        {
            return null;
        }

        var attachments = network.Attachments;
        var named = attachments.Count == 0 ? "(none)" : string.Join(", ", attachments);

        if (pinned.Value is not { } pin)
        {
            logger.LogWarning(
                "Workload {InstanceId} applies an operatorNetwork spec ({Attachments}) but its attachment pin cannot be read — refusing the apply (fail-closed).",
                instance.Id, named);
            return AttachmentPark(
                instance,
                desired,
                WorkloadReconcileReason.AttachmentConfigDrifted,
                $"This workload is attached to the operator network(s) {named}, but the network configuration accepted for it cannot be read. " +
                "It is applied once the network is accepted again.");
        }

        if (pin.Mode != network.Mode
            || !pin.AttachmentNames.SequenceEqual(attachments, StringComparer.Ordinal))
        {
            logger.LogWarning(
                "Workload {InstanceId}'s attachment pin describes {PinnedMode}/[{PinnedNames}] but the spec about to apply is {DesiredMode}/[{DesiredNames}] — refusing the apply.",
                instance.Id, pin.Mode, string.Join(", ", pin.AttachmentNames), network.Mode, named);
            return AttachmentPark(
                instance,
                desired,
                WorkloadReconcileReason.AttachmentConfigDrifted,
                $"The network configuration accepted for this workload no longer matches the network(s) it is about to be attached to ({named}). " +
                "It is applied once the network is accepted again.");
        }

        WorkloadAttachmentExistence existence;
        try
        {
            existence = await provider
                .GetAttachmentExistenceAsync(attachments, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex,
                "Checking whether the attachment(s) of workload {InstanceId} ({Attachments}) exist THREW — parking retryable (never proceeding on an unverified network).",
                instance.Id, named);
            return AttachmentPark(
                instance,
                desired,
                WorkloadReconcileReason.AttachmentExistenceUnverifiable,
                $"The network(s) {named} could not be checked for existence before applying. " +
                "This is retried automatically; the workload is left untouched meanwhile.");
        }

        if (existence.Status != WorkloadAttachmentExistenceStatus.Read)
        {
            var unreadable = existence.Status == WorkloadAttachmentExistenceStatus.Unreadable;
            logger.LogWarning(
                "The attachment existence check for workload {InstanceId} ({Attachments}) came back {Status} ({Detail}) — refusing the apply.",
                instance.Id, named, existence.Status, existence.Detail);
            return AttachmentPark(
                instance,
                desired,
                unreadable
                    ? WorkloadReconcileReason.AttachmentExistenceUnverifiable
                    : WorkloadReconcileReason.AttachmentConfigDrifted,
                unreadable
                    ? $"The network(s) {named} could not be checked for existence before applying. " +
                      "This is retried automatically; the workload is left untouched meanwhile."
                    : $"The provider cannot look up the network(s) {named}. " +
                      "It is applied once the provider can look attachments up.");
        }

        if (existence.MissingAttachments.Count > 0)
        {
            var gone = string.Join(", ", existence.MissingAttachments);
            logger.LogWarning(
                "The pinned attachment(s) {Missing} do not exist for workload {InstanceId} — refusing the apply until they exist.",
                gone, instance.Id);
            return AttachmentPark(
                instance,
                desired,
                WorkloadReconcileReason.AttachmentConfigDrifted,
                $"The network(s) {gone} do not exist. " +
                "The workload is applied once they exist.");
        }

        return null;
    }

    private WorkloadReconcileOutcome AttachmentPark(
        WorkloadInstance instance,
        WorkloadDesiredSpec desired,
        WorkloadReconcileReason reason,
        string message)
    {
        instance.SetDesiredSpecHash(desired.SpecHash, clock.UtcNow);
        return new WorkloadReconcileOutcome
        {
            InstanceId = instance.Id,
            Action = WorkloadReconcileAction.DeferredMutation,
            Reason = reason,
            ResultingState = instance.State,
            PendingApply = true,
            Message = message,
        };
    }

    private static readonly TimeSpan RejectedSpecRetryCooloff = TimeSpan.FromSeconds(30);

    private const string ScanArmedMessage = "Scan armed — idle between runs (no scan in progress).";
    private const string ScanningMessage = "Scanning nodes — a per-node scan Job is in progress.";
    private const string ScanNoTargetsMessage =
        "Scan skipped — no eligible Ready nodes to scan (NoTargets); the marker was cleared and no Jobs were created.";
    private const string ScanFailureMessage =
        "Scan could not complete this pass — the node enumeration was unreadable or the apply did not land; the run remains pending and will retry on a later pass.";

    private static string FormatIneligibleReport(IReadOnlyList<string>? ineligibleNodeNames)
        => ineligibleNodeNames is { Count: > 0 } skipped
            ? $" {skipped.Count} Ready node(s) were skipped as ineligible by the node-eligibility selector and got no Job: {string.Join(", ", skipped)}. Label those nodes for the attachment's parent interface (or widen the selector) if they should be scanned."
            : string.Empty;

    private static string FormatPartialScanMessage(IReadOnlyList<string> failedNodeNames)
        => $"Scan armed — the last run FAILED on {failedNodeNames.Count} node(s), which contributed no results: "
            + $"{string.Join(", ", failedNodeNames)}. Check those nodes' scan logs and re-run the scan.";

    private async Task<WorkloadReconcileOutcome> OrchestrateScanRunAsync(
        WorkloadInstance instance,
        WorkloadReconcileInputs inputs,
        WorkloadDesiredSpec desired,
        IReadOnlyDictionary<string, string?> renderParameters,
        IReadOnlyDictionary<string, string?> unpinnedReferences,
        IReadOnlySet<string> passwordNames,
        IWorkloadProvider provider,
        CancellationToken cancellationToken)
    {
        var now = clock.UtcNow;
        var runId = instance.PendingScanRunId;

        WorkloadPreflightResult preflight;
        try
        {
            preflight = await provider.PreflightAsync(desired, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex,
                "Scan run {RunId} for workload {InstanceId}: engine-time Ready-node enumeration FAULTED — retaining the marker, creating NO Jobs (not NoTargets; a later pass retries).",
                runId, instance.Id);
            return await DriveScanOutcomeAsync(instance, inputs, desired.SpecHash, cancellationToken, ScanFailureMessage)
                .ConfigureAwait(false);
        }

        if (preflight.HasBlockingError)
        {
            logger.LogWarning(
                "Scan run {RunId} for workload {InstanceId}: the cluster node list was UNREADABLE (preflight blocking error / RBAC 403) — retaining the marker, creating NO Jobs (loud fail; NOT NoTargets).",
                runId, instance.Id);
            return await DriveScanOutcomeAsync(instance, inputs, desired.SpecHash, cancellationToken, ScanFailureMessage)
                .ConfigureAwait(false);
        }

        var readyNodes = preflight.ReadyNodeNames;

        var scanSpec = desired with
        {
            PendingScanRunId = runId,
            ScanReadyNodeNames = readyNodes,
            ScanIneligibleNodeNames = preflight.IneligibleNodeNames,
        };

        if (readyNodes is null || readyNodes.Count == 0)
        {
            instance.ClearScanRun(now);
            logger.LogInformation(
                "Scan run {RunId} for workload {InstanceId}: engine-time enumeration found ZERO eligible Ready nodes — NoTargets/Skipped, creating NO Jobs, and clearing the marker VACUOUSLY this pass.{IneligibleReport}",
                runId, instance.Id, FormatIneligibleReport(scanSpec.ScanIneligibleNodeNames));
            return await DriveScanOutcomeAsync(
                    instance, inputs, scanSpec.SpecHash, cancellationToken,
                    ScanNoTargetsMessage + FormatIneligibleReport(scanSpec.ScanIneligibleNodeNames))
                .ConfigureAwait(false);
        }

        if (scanSpec.ScanIneligibleNodeNames is { Count: > 0 } skipped)
        {
            logger.LogInformation(
                "Scan run {RunId} for workload {InstanceId}: fanning out over {EligibleCount} eligible node(s); {SkippedCount} Ready node(s) were SKIPPED as INELIGIBLE by the node-eligibility selector and get NO Job: {SkippedNodes}.",
                runId, instance.Id, readyNodes.Count, skipped.Count, string.Join(", ", skipped));
        }

        if (instance.Provider is null)
        {
            instance.SetProvider(scanSpec.Provider, now);
        }

        instance.SetDesiredSpecHash(scanSpec.SpecHash, now);

        if (referenceRolloutGuard is not null)
        {
            var check = referenceRolloutGuard.Check(instance, unpinnedReferences, renderParameters, passwordNames);
            if (check.ChangedParameter is { } changed)
            {
                var message = $"Password parameter '{changed}' reference value changed; roll out to apply.";
                TransitionSafely(instance, WorkloadInstanceState.PendingConfiguration, WorkloadStateReason.ConfigRequired, message, now);
                await repository.UpdateAsync(instance, cancellationToken).ConfigureAwait(false);
                return new WorkloadReconcileOutcome
                {
                    InstanceId = instance.Id,
                    Action = WorkloadReconcileAction.DeferredMutation,
                    Reason = WorkloadReconcileReason.ConfigurationRequired,
                    ResultingState = instance.State,
                    PendingApply = true,
                    Message = message,
                };
            }
        }

        WorkloadApplyResult applyResult;
        var progress = new WorkloadApplyProgressReporter(
            async (snapshot, ct) =>
            {
                instance.SetProvisioningProgress(snapshot.Percent, snapshot.Message, clock.UtcNow);
                await repository.UpdateAsync(instance, ct).ConfigureAwait(false);
            },
            logger,
            cancellationToken);
        try
        {
            applyResult = await provider.ApplyAsync(scanSpec, progress, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex,
                "Scan run {RunId} for workload {InstanceId}: ApplyAsync THREW — retaining the marker; the next pass (or a new leader) resumes the SAME run-id idempotently (create-only-missing).",
                runId, instance.Id);
            return await DriveScanOutcomeAsync(instance, inputs, scanSpec.SpecHash, cancellationToken, ScanFailureMessage)
                .ConfigureAwait(false);
        }
        finally
        {
            await progress.DrainAsync().ConfigureAwait(false);
        }

        if (!applyResult.Succeeded)
        {
            logger.LogWarning(
                "Scan run {RunId} for workload {InstanceId}: ApplyAsync failed ({Reason} {Message}) — retaining the marker; the next pass re-applies the SAME run-id (create-only-missing).",
                runId, instance.Id, applyResult.Reason, applyResult.Message);
            return await DriveScanOutcomeAsync(instance, inputs, scanSpec.SpecHash, cancellationToken, ScanFailureMessage)
                .ConfigureAwait(false);
        }

        instance.SetAppliedSpecHash(scanSpec.SpecHash, now);
        referenceRolloutGuard?.RecordApplied(instance, unpinnedReferences, renderParameters, passwordNames, now);

        return await ReadStatusAndDriveAsync(
                instance, inputs, scanSpec.SpecHash, cancellationToken, consumeScanMarkerOnTerminal: true,
                appliedThisPass: true)
            .ConfigureAwait(false);
    }

    private async Task<WorkloadReconcileOutcome> DriveScanOutcomeAsync(
        WorkloadInstance instance,
        WorkloadReconcileInputs inputs,
        string? desiredSpecHash,
        CancellationToken cancellationToken,
        string message)
    {
        var driven = await ReadStatusAndDriveAsync(instance, inputs, desiredSpecHash, cancellationToken)
            .ConfigureAwait(false);
        return driven with { Message = message };
    }

    private static bool IsScanRunTerminal(WorkloadStatus status)
        => status.Phase == WorkloadProviderPhase.NotFound
           || (status.Phase == WorkloadProviderPhase.Ready && status.ScanRunTerminal);

    private static IReadOnlyList<string> MissingRequiredParameters(
        WorkloadReconcileInputs inputs, IReadOnlyDictionary<string, string?> resolvedSet)
    {
        var missing = new List<string>();
        foreach (var parameter in inputs.WorkloadManifest.Parameters)
        {
            if (!parameter.Required || string.IsNullOrEmpty(parameter.Name))
            {
                continue;
            }

            if (!WorkloadDeviceSlotSatisfaction.IsSatisfied(inputs.WorkloadManifest, parameter.Name, resolvedSet))
            {
                missing.Add(parameter.Name);
            }
        }

        return missing;
    }

    private static bool CanApplyFrom(WorkloadInstanceState state)
        => state is WorkloadInstanceState.PendingConfiguration
            or WorkloadInstanceState.Deploying
            or WorkloadInstanceState.Healthy
            or WorkloadInstanceState.Degraded
            or WorkloadInstanceState.Failed
            or WorkloadInstanceState.VersionSkew
            or WorkloadInstanceState.ExternallyManaged
            or WorkloadInstanceState.AdoptionPending;

    private async Task<WorkloadReconcileOutcome> ReadStatusAndDriveAsync(
        WorkloadInstance instance,
        WorkloadReconcileInputs inputs,
        string? desiredSpecHash,
        CancellationToken cancellationToken,
        bool consumeScanMarkerOnTerminal = false,
        IReadOnlyList<ResolvedVolume>? declaredVolumes = null,
        bool mayRecreateMissingObject = false,
        bool appliedThisPass = false)
    {
        if (instance.Provider is null)
        {
            await repository.UpdateAsync(instance, cancellationToken).ConfigureAwait(false);
            return WorkloadReconcileOutcome.NoOp(instance.Id, WorkloadReconcileReason.NoProviderAvailable, instance.State);
        }

        var provider = providerRegistry.TryResolve(instance.Provider.Value);
        if (provider is null)
        {
            await repository.UpdateAsync(instance, cancellationToken).ConfigureAwait(false);
            return WorkloadReconcileOutcome.NoOp(instance.Id, WorkloadReconcileReason.NoProviderAvailable, instance.State);
        }

        var reference = WorkloadRef.FromInstance(instance);

        WorkloadStatus status;
        if (fleetStatus is not null
            && fleetStatus.TryConsume(reference, out var primed)
            && !appliedThisPass
            && !(primed.Phase == WorkloadProviderPhase.NotFound && instance.AppliedSpecHash is not null))
        {
            status = primed;
        }
        else
        {
            try
            {
                status = await provider.GetStatusAsync(reference, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return await MarkUnreachableAsync(instance, ex, cancellationToken).ConfigureAwait(false);
            }
        }

        if (status.Phase == WorkloadProviderPhase.Unknown)
        {
            return await MarkUnreachableAsync(instance, exception: null, cancellationToken).ConfigureAwait(false);
        }

        MarkMissingVolumesAsDrift(instance, inputs, status, declaredVolumes);

        var missingObject = MissingObjectCorrection.None;
        if (status.Phase == WorkloadProviderPhase.NotFound)
        {
            missingObject = await DecideMissingObjectCorrectionAsync(instance, inputs, mayRecreateMissingObject, cancellationToken)
                .ConfigureAwait(false);
        }

        return await DriveStateAsync(instance, inputs, desiredSpecHash, status, cancellationToken, consumeScanMarkerOnTerminal,
                missingObject)
            .ConfigureAwait(false);
    }

    private enum MissingObjectCorrection
    {
        None,
        Recreate,
        StillMissing,
        HeldByObserveOnly,
        HeldByPausedReconciliation,
        RemovedBeforeHealthy,
    }

    private static bool ReportsMissingAfterDeploy(WorkloadInstance instance)
        => instance.AppliedSpecHash is not null
            || (instance.State is WorkloadInstanceState.Failed or WorkloadInstanceState.Unknown
                && instance.StateReason != WorkloadStateReason.SpecRejected);

    private static bool HealthySinceDeploy(WorkloadInstance instance)
        => instance.LastHealthyAtUtc is { } healthy
            && (instance.DeployStartedAtUtc is not { } started || healthy >= started);

    private static MissingObjectCorrection? HeldCorrection(WorkloadInstance instance, WorkloadReconcileInputs inputs)
        => instance.ObserveOnly ? MissingObjectCorrection.HeldByObserveOnly
            : inputs.Settings.ReconciliationPaused ? MissingObjectCorrection.HeldByPausedReconciliation
            : null;

    private async Task<MissingObjectCorrection> DecideMissingObjectCorrectionAsync(
        WorkloadInstance instance,
        WorkloadReconcileInputs inputs,
        bool mayRecreate,
        CancellationToken cancellationToken)
    {
        if (!mayRecreate
            || !ReportsMissingAfterDeploy(instance)
            || inputs.WorkloadManifest.Kind == WorkloadKind.ScanPerNode
            || instance.Provider is not { } providerId
            || !Materializes(providerId)
            || instance.IsReconcileFrozen)
        {
            return MissingObjectCorrection.None;
        }

        if (instance.AppliedSpecHash is null)
        {
            return HeldCorrection(instance, inputs) ?? MissingObjectCorrection.StillMissing;
        }

        if (instance.State is not (WorkloadInstanceState.Deploying
                or WorkloadInstanceState.Healthy
                or WorkloadInstanceState.Degraded
                or WorkloadInstanceState.Failed
                or WorkloadInstanceState.VersionSkew))
        {
            return MissingObjectCorrection.None;
        }

        if (HeldCorrection(instance, inputs) is { } held)
        {
            return held;
        }

        try
        {
            if (await activeIntentProvider.GetActiveIntentIdAsync(instance.GroupInstanceId, cancellationToken)
                    .ConfigureAwait(false) is { } intentId)
            {
                logger.LogInformation(
                    "Workload {InstanceId} has no runtime object while update {IntentId} is in flight — leaving it to the update.",
                    instance.Id, intentId);
                return MissingObjectCorrection.None;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex,
                "Could not tell whether an update is in flight for workload {InstanceId} — not recreating its missing runtime object this pass.",
                instance.Id);
            return MissingObjectCorrection.None;
        }

        if (!HealthySinceDeploy(instance))
        {
            logger.LogWarning(
                "Workload {InstanceId}'s runtime object is gone and the workload has not been healthy since it was last deployed — not recreating it.",
                instance.Id);
            return MissingObjectCorrection.RemovedBeforeHealthy;
        }

        return MissingObjectCorrection.Recreate;
    }

    private void MarkMissingVolumesAsDrift(
        WorkloadInstance instance, WorkloadReconcileInputs inputs, WorkloadStatus status,
        IReadOnlyList<ResolvedVolume>? declaredVolumes)
    {
        if (declaredVolumes is null
            || status.MissingVolumes.Count == 0
            || instance.AppliedSpecHash is null
            || !instance.CanMutate
            || instance.IsReconcileFrozen
            || instance.State == WorkloadInstanceState.Suspended
            || instance.ObserveOnly
            || inputs.Settings.ReconciliationPaused)
        {
            return;
        }

        var missing = status.MissingVolumes
            .Where(name => declaredVolumes.Any(volume => string.Equals(volume.ResourceName, name, StringComparison.Ordinal)))
            .ToArray();
        if (missing.Length == 0)
        {
            return;
        }

        logger.LogWarning(
            "Workload {InstanceId} mounts volume(s) {Missing} that do not exist — clearing the applied spec hash so the next pass re-applies and recreates them.",
            instance.Id, string.Join(", ", missing));
        instance.SetAppliedSpecHash(null, clock.UtcNow);
    }

    private async Task<WorkloadReconcileOutcome> MarkUnreachableAsync(
        WorkloadInstance instance, Exception? exception, CancellationToken cancellationToken)
    {
        var now = clock.UtcNow;

        if (instance.State == WorkloadInstanceState.Suspended)
        {
            logger.LogDebug(
                "Provider unreachable for suspended workload {InstanceId} ({Reason}) — keeping it suspended (the hold must survive a provider blip).",
                instance.Id, instance.StateReason.ToString());
            await repository.UpdateAsync(instance, cancellationToken).ConfigureAwait(false);
            return new WorkloadReconcileOutcome
            {
                InstanceId = instance.Id,
                Action = WorkloadReconcileAction.NoOp,
                Reason = HeldReason(instance),
                ResultingState = instance.State,
                Message = "Suspended — provider blip ignored; the hold is kept.",
            };
        }

        if (instance.IsReconcileFrozen)
        {
            logger.LogDebug(
                "Provider status unavailable for ownership-frozen workload {InstanceId} ({State}) — holding the frozen state (a provider blip must never move a mid-switch/disowned instance).",
                instance.Id, instance.State);
            await repository.UpdateAsync(instance, cancellationToken).ConfigureAwait(false);
            return new WorkloadReconcileOutcome
            {
                InstanceId = instance.Id,
                Action = WorkloadReconcileAction.NoOp,
                Reason = WorkloadReconcileReason.ObserveOnly,
                ResultingState = instance.State,
                Message = "Ownership frozen — provider blip ignored; the frozen state is held.",
            };
        }

        if (exception is not null)
        {
            logger.LogWarning(exception, "Provider unreachable for workload {InstanceId} — marking Unknown.", instance.Id);
        }
        else
        {
            logger.LogWarning("Provider reported Unknown for workload {InstanceId} — marking Unknown.", instance.Id);
        }

        TransitionSafely(instance, WorkloadInstanceState.Unknown, WorkloadStateReason.ProviderUnreachable,
            exception?.Message ?? "Provider is unreachable.", now);
        await repository.UpdateAsync(instance, cancellationToken).ConfigureAwait(false);

        return new WorkloadReconcileOutcome
        {
            InstanceId = instance.Id,
            Action = WorkloadReconcileAction.MarkedUnknown,
            Reason = WorkloadReconcileReason.ProviderUnreachable,
            ResultingState = instance.State,
            Message = exception?.Message,
        };
    }

    private async Task<WorkloadReconcileOutcome> DriveStateAsync(
        WorkloadInstance instance,
        WorkloadReconcileInputs inputs,
        string? desiredSpecHash,
        WorkloadStatus status,
        CancellationToken cancellationToken,
        bool consumeScanMarkerOnTerminal = false,
        MissingObjectCorrection missingObject = MissingObjectCorrection.None)
    {
        var now = clock.UtcNow;
        var entryState = instance.State;

        if (status.RestartCount != instance.RestartCount)
        {
            instance.SetRestartCount(status.RestartCount, now);
        }

        if (consumeScanMarkerOnTerminal && instance.HasPendingScanRun && IsScanRunTerminal(status))
        {
            var failedNodes = status.ScanFailedNodeNames;
            if (failedNodes.Count > 0)
            {
                logger.LogWarning(
                    "Scan run {RunId} for workload {InstanceId}: the node-Job set is fully observed TERMINAL (provider phase {Phase}) but {FailedCount} node(s) FAILED and contributed no scan results: {FailedNodes} — consuming the marker and recording the run as PARTIAL.",
                    instance.PendingScanRunId, instance.Id, status.Phase, failedNodes.Count, string.Join(", ", failedNodes));
            }
            else
            {
                logger.LogInformation(
                    "Scan run {RunId} for workload {InstanceId}: the required node-Job set is fully observed TERMINAL (provider phase {Phase}, completion-evidence gated) — consuming/clearing the marker this pass.",
                    instance.PendingScanRunId, instance.Id, status.Phase);
            }

            instance.CompleteScanRun(failedNodes, now);
        }

        var (target, reason, message) = await ClassifyAsync(instance, inputs, desiredSpecHash, status, cancellationToken)
            .ConfigureAwait(false);
        message = missingObject switch
        {
            MissingObjectCorrection.Recreate => $"{message} Reeve recreates it.",
            MissingObjectCorrection.StillMissing => $"{message} Reeve has not been able to apply it yet and keeps trying.",
            MissingObjectCorrection.RemovedBeforeHealthy =>
                $"{message} It was removed before it became healthy, so Reeve does not recreate it again. Find what removes it, then redeploy it.",
            MissingObjectCorrection.HeldByObserveOnly => $"{message} Observe only is on, so Reeve does not recreate it.",
            MissingObjectCorrection.HeldByPausedReconciliation => $"{message} Reconciliation is paused, so Reeve does not recreate it.",
            _ => message,
        };

        TransitionSafely(instance, target, reason, message, now, observed: true);
        if (missingObject == MissingObjectCorrection.Recreate)
        {
            logger.LogWarning(
                "Workload {InstanceId}'s runtime object is gone — clearing the applied spec hash so the next pass recreates it.",
                instance.Id);
            instance.SetAppliedSpecHash(null, now);
        }

        await repository.UpdateAsync(instance, cancellationToken).ConfigureAwait(false);

        var action = instance.State == entryState
            ? WorkloadReconcileAction.NoOp
            : WorkloadReconcileAction.Transitioned;

        return new WorkloadReconcileOutcome
        {
            InstanceId = instance.Id,
            Action = action,
            Reason = WorkloadReconcileReason.HealthEvaluated,
            ResultingState = instance.State,
            Message = message,
            RequeueAfter = missingObject == MissingObjectCorrection.Recreate ? TimeSpan.Zero : null,
        };
    }

    private async Task<(WorkloadInstanceState Target, WorkloadStateReason Reason, string? Message)> ClassifyAsync(
        WorkloadInstance instance,
        WorkloadReconcileInputs inputs,
        string? desiredSpecHash,
        WorkloadStatus status,
        CancellationToken cancellationToken)
    {
        if (instance.State == WorkloadInstanceState.Switching)
        {
            return (WorkloadInstanceState.Switching, instance.StateReason,
                instance.StateMessage
                ?? "Managed→manual ownership switch in progress — the reconciler observes but does not drive a mid-switch instance.");
        }

        if (inputs.WorkloadManifest.Kind == WorkloadKind.ScanPerNode)
        {
            if (instance.State == WorkloadInstanceState.Suspended)
            {
                var heldMessage = instance.StateMessage ?? "Scan suspended — held pending operator/host resume.";
                var message = string.IsNullOrEmpty(status.Message)
                    ? heldMessage
                    : $"{heldMessage} Provider reports: {status.Message}";
                return (WorkloadInstanceState.Suspended, instance.StateReason, message);
            }

            if (instance.State == WorkloadInstanceState.ExternallyManaged
                && Materializes(instance.Provider ?? inputs.EffectiveProvider.Id))
            {
                return (WorkloadInstanceState.ExternallyManaged, WorkloadStateReason.ExternalHealthObserved,
                    status.Message
                    ?? "Externally managed — Reeve observes but does not manage the disowned runtime (ownership released; managed provider preserved).");
            }

            if (status.Phase == WorkloadProviderPhase.Running)
            {
                return (WorkloadInstanceState.Deploying, WorkloadStateReason.Provisioning, ScanningMessage);
            }

            var failedNodes = instance.GetLastScanFailedNodes();
            return (WorkloadInstanceState.Healthy, WorkloadStateReason.HealthObserved,
                failedNodes.Count > 0 ? FormatPartialScanMessage(failedNodes) : ScanArmedMessage);
        }

        switch (status.Phase)
        {
            case WorkloadProviderPhase.Failed:
                return (WorkloadInstanceState.Failed, WorkloadStateReason.HealthLost,
                    status.Message ?? "Provider reports the workload failed.");

            case WorkloadProviderPhase.NotFound:
                if (ReportsMissingAfterDeploy(instance))
                {
                    return (WorkloadInstanceState.Failed, WorkloadStateReason.HealthLost,
                        status.Message ?? "Provider no longer finds the deployed workload.");
                }

                return FirstDeployStalled(instance, inputs)
                    ? (WorkloadInstanceState.Degraded, WorkloadStateReason.Provisioning,
                        $"Deploy stalled for over {StalledMinutes} min: {instance.StateMessage ?? "nothing was applied yet."}")
                    : (instance.State, instance.StateReason, instance.StateMessage);

            case WorkloadProviderPhase.Pending:
            case WorkloadProviderPhase.Running:
            case WorkloadProviderPhase.Ready:
            default:
                var providerKind = instance.Provider ?? inputs.EffectiveProvider.Id;

                if (instance.State == WorkloadInstanceState.ExternallyManaged
                    && Materializes(providerKind))
                {
                    return (WorkloadInstanceState.ExternallyManaged, WorkloadStateReason.ExternalHealthObserved,
                        status.Message
                        ?? "Externally managed — Reeve observes but does not manage the disowned runtime (ownership released; managed provider preserved).");
                }

                var contribution = await ObserveHealthAsync(instance, inputs, desiredSpecHash, cancellationToken)
                    .ConfigureAwait(false);
                var versionSkew = await TryClassifyVersionSkewAsync(instance, inputs, contribution, cancellationToken).ConfigureAwait(false);
                if (versionSkew is { } skew)
                {
                    return skew;
                }

                if (!Materializes(providerKind)
                    && status.Phase == WorkloadProviderPhase.Ready
                    && status.ProviderReady)
                {
                    if (contribution.Presence.Kind == WorkloadPresenceSignalKind.PresentMismatched)
                    {
                        return (WorkloadInstanceState.Deploying, WorkloadStateReason.Provisioning,
                            "The externally deployed runtime reports a different spec than the current one — re-render "
                            + "the deployment artifacts and redeploy them.");
                    }

                    return (WorkloadInstanceState.ExternallyManaged, WorkloadStateReason.ExternalHealthObserved,
                        status.Message ?? "Externally deployed runtime is live; Reeve observes but does not manage it.");
                }

                var target = WorkloadHealthEvaluator.EvaluateTarget(
                    inputs.WorkloadManifest.Health.Mode,
                    status.ProviderReady,
                    contribution.Presence,
                    desiredSpecHash,
                    instance.State,
                    instance.StateReason);

                if (target is WorkloadInstanceState.Deploying or WorkloadInstanceState.Degraded)
                {
                    var heldOnUnknownPresence = !inputs.WorkloadManifest.Health.Mode.IsLibraryOwned
                        && status.ProviderReady
                        && contribution.Presence.Kind == WorkloadPresenceSignalKind.Unknown;
                    return heldOnUnknownPresence
                        ? (instance.State, instance.StateReason, instance.StateMessage)
                        : ClassifyNotReady(instance, status, Materializes(providerKind), inputs.WorkloadManifest.HostRequirements);
                }

                return target == WorkloadInstanceState.Healthy
                    ? (target, WorkloadStateReason.HealthObserved, "Provider-ready and health observed.")
                    : (target, instance.StateReason, instance.StateMessage);
        }
    }

    private int StalledMinutes => (int)Math.Round(options.DeployStalledAfter.TotalMinutes);

    private bool FirstDeployStalled(WorkloadInstance instance, WorkloadReconcileInputs inputs)
        => instance is { State: WorkloadInstanceState.Deploying, AppliedSpecHash: null }
            && Materializes(instance.Provider ?? inputs.EffectiveProvider.Id)
            && clock.UtcNow - (instance.DeployStartedAtUtc ?? instance.LastTransitionAtUtc) > options.DeployStalledAfter;

    private (WorkloadInstanceState, WorkloadStateReason, string?) ClassifyNotReady(
        WorkloadInstance instance, WorkloadStatus status, bool materializes, IReadOnlyList<HostRequirement> hostRequirements)
    {
        var deployStartedAt = instance.DeployStartedAtUtc ?? instance.LastTransitionAtUtc;
        var providerDetail = status.ProviderReady || string.IsNullOrWhiteSpace(status.Message) ? null : status.Message;
        if (instance.LastHealthyAtUtc is { } lastHealthy && lastHealthy >= deployStartedAt)
        {
            const string lostHealth = "Health signal lost while running.";
            var lost = providerDetail is null ? lostHealth : $"{lostHealth} {providerDetail}";
            return (WorkloadInstanceState.Degraded, WorkloadStateReason.HealthLost,
                EnrichDegradedMessageWithHostRequirements(lost, hostRequirements));
        }

        if (materializes && clock.UtcNow - deployStartedAt > options.DeployStalledAfter)
        {
            var minutes = StalledMinutes;
            var detail = status.ProviderReady
                ? "the runtime is up, but no qualifying health signal arrived."
                : providerDetail ?? "the provider reports it not ready.";
            return (WorkloadInstanceState.Degraded, WorkloadStateReason.Provisioning,
                EnrichDegradedMessageWithHostRequirements($"Deploy stalled for over {minutes} min: {detail}", hostRequirements));
        }

        return (WorkloadInstanceState.Deploying, WorkloadStateReason.Provisioning, "Waiting for a qualifying health signal.");
    }

    private static string? EnrichDegradedMessageWithHostRequirements(
        string? baseMessage, IReadOnlyList<HostRequirement> hostRequirements)
    {
        if (hostRequirements.Count == 0)
        {
            return baseMessage;
        }

        var names = hostRequirements
            .Select(r => string.IsNullOrWhiteSpace(r.Name) ? HostRequirementKindLabel(r.Kind) : r.Name!)
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        if (names.Count == 0)
        {
            return baseMessage;
        }

        var prefix = string.IsNullOrWhiteSpace(baseMessage) ? string.Empty : baseMessage!.TrimEnd() + " ";
        return prefix
            + $"The manifest declares host prerequisites ({string.Join(", ", names)}) — verify they are present on the node.";
    }

    private static string HostRequirementKindLabel(HostRequirementKind kind) => kind switch
    {
        HostRequirementKind.DbusService => "dbus-service",
        HostRequirementKind.KernelModule => "kernel-module",
        HostRequirementKind.UdevSerial => "udev-serial",
        HostRequirementKind.Multicast => "multicast",
        _ => kind.ToString(),
    };

    private async Task<(WorkloadInstanceState, WorkloadStateReason, string?)?> TryClassifyVersionSkewAsync(
        WorkloadInstance instance,
        WorkloadReconcileInputs inputs,
        WorkloadHealthContribution contribution,
        CancellationToken cancellationToken)
    {
        if (inputs.WorkloadManifest.Health.Mode.IsLibraryOwned
            || string.IsNullOrWhiteSpace(inputs.InstalledGroupVersion))
        {
            return null;
        }

        try
        {
            var target = await activeIntentProvider
                .TryGetActiveTargetSpecAsync(instance.GroupInstanceId, instance.WorkloadId, cancellationToken)
                .ConfigureAwait(false);
            if (target is not null)
            {
                return null;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogDebug(ex,
                "VersionSkew active-intent check faulted for workload {InstanceId} — skipping skew classification.",
                instance.Id);
            return null;
        }

        if (contribution.RunningVersion is not { } runningVersion || !contribution.RunningVersionIsOlder)
        {
            return null;
        }

        var banner =
            $"Integration degraded — the plugin module was updated to {inputs.InstalledGroupVersion} but the running " +
            $"sidecar is still at {runningVersion}. It stays degraded until the update completes (finish the pending " +
            "configuration / re-run the update).";
        logger.LogWarning(
            "Workload {InstanceId} in VERSION SKEW: installed module {GroupVersion} is ahead of the running sidecar {SidecarVersion} with no active intent — banner surfaced.",
            instance.Id, inputs.InstalledGroupVersion, runningVersion);

        return (WorkloadInstanceState.VersionSkew, WorkloadStateReason.RunningVersionBehind, banner);
    }

    private Task<WorkloadHealthContribution> ObserveHealthAsync(
        WorkloadInstance instance,
        WorkloadReconcileInputs inputs,
        string? desiredSpecHash,
        CancellationToken cancellationToken)
        => healthContributors.ObserveAsync(
            new WorkloadHealthObservation
            {
                InstanceId = instance.Id,
                HealthMode = inputs.WorkloadManifest.Health.Mode,
                DesiredSpecHash = desiredSpecHash,
                InstalledGroupVersion = inputs.InstalledGroupVersion,
            },
            cancellationToken);

    private void TransitionSafely(
        WorkloadInstance instance,
        WorkloadInstanceState target,
        WorkloadStateReason reason,
        string? message,
        DateTimeOffset now,
        bool observed = false)
    {
        if (!instance.IsLegalTransition(target, reason))
        {
            logger.LogDebug(
                "Skipping illegal transition {From} → {To} for workload {InstanceId} (kept current state).",
                instance.State, target, instance.Id);
            return;
        }

        if (observed)
        {
            instance.ObserveTransitionTo(target, reason, message, now);
        }
        else
        {
            instance.TransitionTo(target, reason, message, now);
        }
    }
}
