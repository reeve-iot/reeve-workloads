using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Reeve.Workloads.Abstractions;
using Reeve.Workloads.Abstractions.Contracts;
using Reeve.Workloads.Abstractions.Contracts.Deployment;
using Reeve.Workloads.Abstractions.Domain;
using Reeve.Workloads.Abstractions.Domain.DesiredSpec;
using Reeve.Workloads.Abstractions.Domain.Entities;
using Reeve.Workloads.Abstractions.Domain.Network;
using Reeve.Workloads.Abstractions.Providers;
using Reeve.Workloads.Domain;
using Reeve.Workloads.Core.Update;
using Reeve.Workloads.Domain.DesiredSpec;

namespace Reeve.Workloads.Core;

internal sealed class WorkloadLifecycleService : IWorkloadLifecycleService, IWorkloadDroppedWorkloadTeardown
{
    private const int ConflictReapplyAttempts = 3;

    private readonly IWorkloadDeploymentContextSource contextSource;
    private readonly IWorkloadInstanceRepository repository;
    private readonly IActiveWorkloadIntentProvider intentProvider;
    private readonly IWorkloadGroupRepository modules;
    private readonly IWorkloadProviderRegistry providerRegistry;
    private readonly IWorkloadResourceNamerRegistry namers;
    private readonly ICompiledProviderCatalog compiledProviderCatalog;
    private readonly IRetainedVolumeService retainedVolumeService;
    private readonly IWorkloadReconcileNudge reconcileNudge;
    private readonly IWorkloadClock clock;
    private readonly ILogger<WorkloadLifecycleService> logger;
    private readonly WorkloadDeviceGroupIdWriter deviceGroupIdWriter;
    private readonly IWorkloadCredentialPort credentialPort;
    private readonly IWorkloadRequirementsService requirements;
    private readonly IWorkloadOrchestratorSettingsService settingsService;
    private readonly IWorkloadAuditSink auditSink;
    private readonly WorkloadParameterSecretCodec parameterSecrets;
    private readonly IWorkloadDiagnosticsSinkFactory diagnostics;
    private readonly TimeSpan stuckTeardownAfter;

    public WorkloadLifecycleService(
        IWorkloadDeploymentContextSource contextSource,
        IWorkloadInstanceRepository repository,
        IActiveWorkloadIntentProvider intentProvider,
        IWorkloadGroupRepository modules,
        IWorkloadProviderRegistry providerRegistry,
        IWorkloadResourceNamerRegistry namers,
        ICompiledProviderCatalog compiledProviderCatalog,
        IRetainedVolumeService retainedVolumeService,
        IWorkloadReconcileNudge reconcileNudge,
        IWorkloadClock clock,
        ILogger<WorkloadLifecycleService> logger,
        WorkloadDeviceGroupIdWriter deviceGroupIdWriter,
        IWorkloadAuditSink auditSink,
        IWorkloadCredentialPort credentialPort,
        IWorkloadRequirementsService requirements,
        IWorkloadOrchestratorSettingsService settingsService,
        WorkloadParameterSecretCodec parameterSecrets,
        IWorkloadDiagnosticsSinkFactory diagnostics,
        IOptions<WorkloadRepairsOptions> repairsOptions)
    {
        this.diagnostics = diagnostics ?? throw new ArgumentNullException(nameof(diagnostics));
        stuckTeardownAfter = (repairsOptions ?? throw new ArgumentNullException(nameof(repairsOptions))).Value.StuckWarningAfter;
        this.contextSource = contextSource;
        this.repository = repository;
        this.intentProvider = intentProvider;
        this.modules = modules ?? throw new ArgumentNullException(nameof(modules));
        this.providerRegistry = providerRegistry;
        this.namers = namers ?? throw new ArgumentNullException(nameof(namers));
        this.compiledProviderCatalog = compiledProviderCatalog;
        this.retainedVolumeService = retainedVolumeService;
        this.reconcileNudge = reconcileNudge ?? throw new ArgumentNullException(nameof(reconcileNudge));
        this.clock = clock;
        this.logger = logger;
        this.deviceGroupIdWriter = deviceGroupIdWriter;
        this.credentialPort = credentialPort;
        this.requirements = requirements ?? throw new ArgumentNullException(nameof(requirements));
        this.settingsService = settingsService;
        this.parameterSecrets = parameterSecrets ?? throw new ArgumentNullException(nameof(parameterSecrets));
        this.auditSink = auditSink;
    }

    public async Task<WorkloadGroupAdoptOutcome> AdoptGroupAsync(
        string groupId,
        string sourceKey,
        string? installedVersion,
        CancellationToken cancellationToken = default)
    {
        var candidate = WorkloadGroup.Record(groupId, sourceKey, installedVersion, clock.UtcNow);

        var existing = await modules.GetAsync(candidate.GroupId, cancellationToken).ConfigureAwait(false);
        if (existing is null)
        {
            if (await modules.TryAddAsync(candidate, cancellationToken).ConfigureAwait(false))
            {
                logger.LogInformation(
                    "Adopted module '{GroupId}' from '{SourceKey}' at version '{Version}'.",
                    candidate.GroupId, candidate.SourceKey, candidate.InstalledVersion ?? "(none)");
                return WorkloadGroupAdoptOutcome.Created;
            }

            existing = await modules.GetAsync(candidate.GroupId, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException(
                    $"Module '{candidate.GroupId}' was refused an insert and has no row to compare with.");
        }

        if (!string.Equals(existing.InstalledVersion, candidate.InstalledVersion, StringComparison.Ordinal))
        {
            logger.LogWarning(
                "Module '{GroupId}' is recorded at '{Recorded}'; adopting it at '{Adopted}' was refused. Moving a version is an update.",
                existing.GroupId, existing.InstalledVersion ?? "(none)", candidate.InstalledVersion ?? "(none)");
            return WorkloadGroupAdoptOutcome.VersionMismatch;
        }

        if (!string.Equals(existing.SourceKey, candidate.SourceKey, StringComparison.Ordinal))
        {
            logger.LogWarning(
                "Module '{GroupId}' is recorded from '{Recorded}'; adopting it from '{Adopted}' was refused.",
                existing.GroupId, existing.SourceKey, candidate.SourceKey);
            return WorkloadGroupAdoptOutcome.SourceMismatch;
        }

        return WorkloadGroupAdoptOutcome.Unchanged;
    }

    public async Task<WorkloadMaterializeResult> MaterializeAsync(
        WorkloadGroupInstall install, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(install);
        if (install.GroupInstanceId == Guid.Empty)
        {
            throw new ArgumentException("A module instance id is required.", nameof(install));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(install.RequirementsFingerprint, nameof(install));

        var candidate = WorkloadGroup.Record(install.GroupId, install.SourceKey, install.Version, clock.UtcNow);

        var existingGroup = await modules.GetAsync(candidate.GroupId, cancellationToken).ConfigureAwait(false);
        var reinstall = false;
        if (existingGroup is not null && !IsSameInstall(existingGroup, candidate))
        {
            if (await AnyWorkloadOfGroupAsync(candidate.GroupId, cancellationToken).ConfigureAwait(false))
            {
                return RefuseAnotherGroupRow(existingGroup, candidate);
            }

            reinstall = true;
        }

        var read = await requirements.ForInstallAsync(
            new WorkloadInstallRequirementsQuery
            {
                GroupId = candidate.GroupId,
                SourceKey = candidate.SourceKey,
                Version = candidate.InstalledVersion,
                Parameters = install.Parameters,
            },
            cancellationToken).ConfigureAwait(false);
        switch (read.Status)
        {
            case WorkloadRequirementsStatus.NotFound:
                return WorkloadMaterializeResult.Refused(WorkloadMaterializeOutcome.NotDeclared, read.Message!);
            case WorkloadRequirementsStatus.Invalid:
                return WorkloadMaterializeResult.Refused(WorkloadMaterializeOutcome.PreconditionFailed, read.Message!);
        }

        if (!string.Equals(read.Read!.Fingerprint, install.RequirementsFingerprint, StringComparison.Ordinal))
        {
            logger.LogInformation(
                "Materialize of module instance {GroupInstanceId} ('{GroupId}') refused: its requirements are not the ones the host judged.",
                install.GroupInstanceId, candidate.GroupId);
            return WorkloadMaterializeResult.Refused(
                WorkloadMaterializeOutcome.PreconditionFailed,
                $"The requirements of '{candidate.GroupId}' changed since they were judged; nothing was written.");
        }

        var undeclaredPins = install.AttachmentPins.Keys.Where(id => !read.Read.After.ContainsKey(id)).ToList();
        if (undeclaredPins.Count > 0)
        {
            return WorkloadMaterializeResult.Refused(
                WorkloadMaterializeOutcome.NotDeclared,
                $"A pin names workload(s) '{string.Join("', '", undeclaredPins)}' that '{candidate.GroupId}' does not declare; nothing was written.");
        }

        if (reinstall)
        {
            logger.LogInformation(
                "Module '{GroupId}' has no workload row left; reinstalling it from '{SourceKey}' at '{Version}' (was '{Recorded}' at '{RecordedVersion}').",
                candidate.GroupId, candidate.SourceKey, candidate.InstalledVersion ?? "(none)",
                existingGroup!.SourceKey, existingGroup.InstalledVersion ?? "(none)");
            existingGroup.Reinstall(candidate.SourceKey, candidate.InstalledVersion, clock.UtcNow);
            await modules.UpdateAsync(existingGroup, cancellationToken).ConfigureAwait(false);
        }
        else if (existingGroup is null && !await modules.TryAddAsync(candidate, cancellationToken).ConfigureAwait(false))
        {
            var winner = await modules.GetAsync(candidate.GroupId, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException(
                    $"Module '{candidate.GroupId}' was refused an insert and has no row to compare with.");
            if (!IsSameInstall(winner, candidate))
            {
                return RefuseAnotherGroupRow(winner, candidate);
            }
        }

        var context = await contextSource.GetForGroupAsync(candidate.GroupId, cancellationToken).ConfigureAwait(false);
        if (context is null)
        {
            return WorkloadMaterializeResult.Refused(
                WorkloadMaterializeOutcome.NotDeclared,
                $"Module '{candidate.GroupId}' declares no deployment at its installed version.");
        }

        var now = clock.UtcNow;
        foreach (var workload in context.Deployment.Workloads)
        {
            install.AttachmentPins.TryGetValue(workload.Id, out var pin);
            var existing = await repository.GetByWorkloadAsync(install.GroupInstanceId, workload.Id, cancellationToken)
                .ConfigureAwait(false);
            if (existing is not null)
            {
                if (pin is not null)
                {
                    existing.PinAttachment(pin, now);
                    await repository.UpdateAsync(existing, cancellationToken).ConfigureAwait(false);
                }

                continue;
            }

            var created = WorkloadInstance.Create(Guid.NewGuid(), install.GroupInstanceId, context.GroupId, workload.Id, now);
            created.PinAttachment(pin, now);

            if (WorkloadHasRestoreBinding(workload, install.Parameters))
            {
                created.SetRestoreBindingUnresolved(true, now);
            }

            await repository.AddAsync(created, cancellationToken).ConfigureAwait(false);
        }

        if (!install.SuppressReconcileNudge)
        {
            await NudgeReconcileAllAsync("workload-materialize", cancellationToken).ConfigureAwait(false);
        }

        return WorkloadMaterializeResult.Materialized;
    }

    private static bool IsSameInstall(WorkloadGroup recorded, WorkloadGroup candidate)
        => string.Equals(recorded.InstalledVersion, candidate.InstalledVersion, StringComparison.Ordinal)
           && string.Equals(recorded.SourceKey, candidate.SourceKey, StringComparison.Ordinal);

    private async Task<bool> AnyWorkloadOfGroupAsync(string groupId, CancellationToken cancellationToken)
        => (await repository.ListAllAsync(cancellationToken).ConfigureAwait(false))
            .Any(instance => string.Equals(instance.GroupId, groupId, StringComparison.OrdinalIgnoreCase));

    private WorkloadMaterializeResult RefuseAnotherGroupRow(WorkloadGroup recorded, WorkloadGroup candidate)
    {
        if (!string.Equals(recorded.InstalledVersion, candidate.InstalledVersion, StringComparison.Ordinal))
        {
            logger.LogWarning(
                "Module '{GroupId}' is recorded at '{Recorded}'; materializing it at '{Requested}' was refused. Moving a version is an update.",
                recorded.GroupId, recorded.InstalledVersion ?? "(none)", candidate.InstalledVersion ?? "(none)");
            return WorkloadMaterializeResult.Refused(
                WorkloadMaterializeOutcome.VersionMismatch,
                $"Module '{recorded.GroupId}' is installed at {recorded.InstalledVersion ?? "(bundled)"}; moving it to {candidate.InstalledVersion ?? "(bundled)"} is an update.");
        }

        return WorkloadMaterializeResult.Refused(
            WorkloadMaterializeOutcome.SourceMismatch,
            $"Module '{recorded.GroupId}' is installed from '{recorded.SourceKey}', not '{candidate.SourceKey}'.");
    }

    public async Task SuspendAsync(Guid groupInstanceId, CancellationToken cancellationToken = default)
    {
        var now = clock.UtcNow;
        foreach (var instance in await repository.ListByPluginInstanceAsync(groupInstanceId, cancellationToken).ConfigureAwait(false))
        {
            await WriteReapplyingOnConflictAsync(instance, row => SuspendInstanceAsync(row, now, cancellationToken), "suspend",
                cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task SuspendInstanceAsync(WorkloadInstance instance, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (instance.State == WorkloadInstanceState.Terminating)
        {
            return;
        }

        if (instance.State == WorkloadInstanceState.Switching)
        {
            await RevokeCredentialsAsync(instance, cancellationToken).ConfigureAwait(false);
            if (!instance.IsHostHoldPending)
            {
                instance.TransitionTo(WorkloadInstanceState.Switching, WorkloadStateReason.SuspendedByHost,
                    "Withdrawn by the host mid-switch; held when the switch ends.", now);
                await repository.UpdateAsync(instance, cancellationToken).ConfigureAwait(false);
            }

            await NudgeReconcileAsync(instance.Id, "workload-host-suspend", cancellationToken).ConfigureAwait(false);
            return;
        }

        await RevokeCredentialsAsync(instance, cancellationToken).ConfigureAwait(false);
        if (!instance.IsHeldByHost)
        {
            var reason = instance.State == WorkloadInstanceState.ExternallyManaged
                ? WorkloadStateReason.SuspendedByHostExternallyManaged
                : WorkloadStateReason.SuspendedByHost;
            instance.TransitionTo(WorkloadInstanceState.Suspended, reason, "Suspended by the host.", now);
            await repository.UpdateAsync(instance, cancellationToken).ConfigureAwait(false);
        }

        if (!instance.IsReconcileFrozen)
        {
            await SuspendOnProviderAsync(instance, cancellationToken).ConfigureAwait(false);
        }

        await NudgeReconcileAsync(instance.Id, "workload-host-suspend", cancellationToken).ConfigureAwait(false);
    }

    public async Task ResumeAsync(Guid groupInstanceId, CancellationToken cancellationToken = default)
    {
        var now = clock.UtcNow;
        foreach (var instance in await repository.ListByPluginInstanceAsync(groupInstanceId, cancellationToken).ConfigureAwait(false))
        {
            await WriteReapplyingOnConflictAsync(instance, row => ResumeInstanceAsync(row, now, cancellationToken), "resume",
                cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task ResumeInstanceAsync(WorkloadInstance instance, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (instance.IsHostHoldPending)
        {
            instance.TransitionTo(WorkloadInstanceState.Switching, WorkloadStateReason.HealthObserved,
                "Managed→manual provider switch in progress.", now);
            instance.SetAppliedSpecHash(null, now);
            await repository.UpdateAsync(instance, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (!instance.IsHeldByHost)
        {
            return;
        }

        if (instance.IsReconcileFrozen)
        {
            instance.TransitionTo(WorkloadInstanceState.ExternallyManaged, WorkloadStateReason.ResumedByHost,
                "Resumed by the host.", now);
        }
        else
        {
            instance.TransitionTo(WorkloadInstanceState.Deploying, WorkloadStateReason.ResumedByHost, "Resumed by the host.", now);
            instance.SetAppliedSpecHash(null, now);
        }

        await repository.UpdateAsync(instance, cancellationToken).ConfigureAwait(false);
        await NudgeReconcileAsync(instance.Id, "workload-host-resume", cancellationToken).ConfigureAwait(false);
    }

    private async Task WriteReapplyingOnConflictAsync(
        WorkloadInstance instance,
        Func<WorkloadInstance, Task> step,
        string operation,
        CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await step(instance).ConfigureAwait(false);
                return;
            }
            catch (WorkloadConcurrencyConflictException conflict) when (conflict.EntityId == instance.Id)
            {
                var stored = await repository.ReloadAsync(instance.Id, cancellationToken).ConfigureAwait(false);
                if (stored is null)
                {
                    return;
                }

                if (attempt >= ConflictReapplyAttempts)
                {
                    throw;
                }

                logger.LogDebug(
                    "The {Operation} write of workload instance {InstanceId} lost a write race; re-read, re-applying ({Attempt}/{Attempts}).",
                    operation, instance.Id, attempt, ConflictReapplyAttempts);
                instance = stored;
            }
        }
    }

    private async Task SuspendOnProviderAsync(WorkloadInstance instance, CancellationToken cancellationToken)
    {
        if (instance.Provider is not { } bound || providerRegistry.TryResolve(bound) is not { } provider)
        {
            return;
        }

        try
        {
            await provider.SuspendAsync(WorkloadRef.FromInstance(instance), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex,
                "Suspending workload instance {InstanceId} on its provider failed — the reconciler retries it every pass.",
                instance.Id);
        }
    }

    public async Task SetParametersAsync(
        Guid workloadInstanceId,
        IReadOnlyDictionary<string, string?> resolvedParameters,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(resolvedParameters);

        var instance = await repository.GetByIdAsync(workloadInstanceId, cancellationToken).ConfigureAwait(false);
        if (instance is null)
        {
            logger.LogDebug("SetParameters for unknown workload instance {InstanceId} — ignored.", workloadInstanceId);
            return;
        }

        if (!instance.CanMutate)
        {
            throw new WorkloadParameterValidationException(
                $"Workload instance {instance.Id} is {instance.State}; parameters cannot be edited in this state.");
        }

        var validation = await ValidateParametersAsync(instance, resolvedParameters, cancellationToken).ConfigureAwait(false);
        var applicable = validation.Applicable ?? resolvedParameters;

        var toPersist = validation.Manifest is { } gidManifest && validation.Resolved is { } gidResolved
            ? await deviceGroupIdWriter.ApplyAsync(gidManifest, applicable, gidResolved, cancellationToken).ConfigureAwait(false)
            : applicable;

        var now = clock.UtcNow;
        var stored = instance.GetResolvedParameters();
        var passwordChanged = validation.Manifest is { } manifest
            && WorkloadPasswordParameterChanges.HasChanged(manifest, stored, toPersist, parameterSecrets);
        instance.SetResolvedParameters(validation.Manifest is { } persistManifest
            ? parameterSecrets.ProtectAgainstStored(persistManifest, toPersist, stored)
            : toPersist, now);
        var rollsPassword = passwordChanged && instance.WasDeployed && !instance.IsOwnershipReleased(providerRegistry);
        var activeIntentId = rollsPassword
            ? await intentProvider.GetActiveIntentIdAsync(instance.GroupInstanceId, cancellationToken).ConfigureAwait(false)
            : null;
        if (rollsPassword && activeIntentId is null && !(instance.Provider is { } bound && !providerRegistry.Describe(bound).Materializes))
        {
            instance.SetAppliedSpecHash(null, now);
            instance.MarkRestart(now);
        }

        if (instance.RestoreBindingUnresolved && validation.ExistingClaimsClaimed)
        {
            instance.SetRestoreBindingUnresolved(false, now);
            logger.LogInformation(
                "Cleared the restore-binding hold for workload instance {InstanceId} — the requested restore source was " +
                "verified and atomically claimed, so the deploy will adopt the retained volume.",
                instance.Id);
        }

        await repository.UpdateAsync(instance, cancellationToken).ConfigureAwait(false);
        await NudgeReconcileAsync(instance.Id, "workload-set-parameters", cancellationToken).ConfigureAwait(false);
    }

    public async Task<bool> PinAttachmentAsync(
        Guid workloadInstanceId,
        WorkloadAttachmentPin? pin,
        CancellationToken cancellationToken = default)
    {
        var instance = await repository.GetByIdAsync(workloadInstanceId, cancellationToken).ConfigureAwait(false);
        if (instance is null)
        {
            logger.LogDebug("PinAttachment for unknown workload instance {InstanceId} — ignored.", workloadInstanceId);
            return false;
        }

        instance.PinAttachment(pin, clock.UtcNow);
        await repository.UpdateAsync(instance, cancellationToken).ConfigureAwait(false);

        await NudgeReconcileAsync(instance.Id, "workload-pin-attachment", cancellationToken).ConfigureAwait(false);
        return true;
    }

    private readonly record struct ParameterValidation(
        bool ExistingClaimsClaimed,
        WorkloadManifest? Manifest,
        IReadOnlyDictionary<string, string?>? Resolved,
        IReadOnlyDictionary<string, string?>? Applicable);

    private async Task<ParameterValidation> ValidateParametersAsync(
        WorkloadInstance instance,
        IReadOnlyDictionary<string, string?> resolvedParameters,
        CancellationToken cancellationToken)
    {
        var context = await contextSource.GetForGroupAsync(instance.GroupId, cancellationToken)
            .ConfigureAwait(false);
        var manifest = context?.Deployment.Workloads
            .FirstOrDefault(w => string.Equals(w.Id, instance.WorkloadId, StringComparison.Ordinal));
        if (manifest is null)
        {
            var restoreBindings = resolvedParameters
                .Where(kv => kv.Key.StartsWith("existingClaim.", StringComparison.Ordinal) && !string.IsNullOrWhiteSpace(kv.Value))
                .Select(kv => kv.Key)
                .OrderBy(name => name, StringComparer.Ordinal)
                .ToList();
            if (restoreBindings.Count > 0)
            {
                logger.LogWarning(
                    "Deployment manifest for workload instance {InstanceId} is transiently unresolvable while a restore binding " +
                    "({Bindings}) is requested — PARKING the restore (fail-closed) rather than persisting it unvalidated. " +
                    "The install retries once the manifest scan recovers.",
                    instance.Id, string.Join(", ", restoreBindings));
                throw new WorkloadParameterValidationException(
                    $"Could not verify the requested restore source: the deployment manifest for workload instance {instance.Id} " +
                    "is not currently resolvable (a transient scan miss). The restore was parked rather than deploying a fresh " +
                    "volume — retry once the plugin's manifest is available, or install without restoring.");
            }

            return new ParameterValidation(false, null, null, null);
        }

        var provider = await ResolveEffectiveProviderAsync(instance, cancellationToken).ConfigureAwait(false);
        var applicable = WorkloadParameterResolver.WithoutUnsupportedUserNamespace(resolvedParameters, manifest, provider);
        IReadOnlyDictionary<string, string?> resolved;
        try
        {
            resolved = WorkloadParameterResolver.Resolve(manifest, applicable, provider);
        }
        catch (WorkloadParameterResolutionException ex)
        {
            throw new WorkloadParameterValidationException(ex.Message, ex);
        }

        WorkloadParameterAcceptList.ThrowIfUndeclared(instance.WorkloadId, applicable.Keys, resolved);

        var invalidPlacement = new List<string>();
        foreach (var name in WorkloadDesiredSpecFactory.NodeSelectorParameterNames(manifest))
        {
            if (resolvedParameters.TryGetValue(name, out var raw) && !string.IsNullOrWhiteSpace(raw))
            {
                invalidPlacement.AddRange(WorkloadNodeSelectorGrammar.DescribeInvalidLabels(raw));
            }
        }

        if (WorkloadNodeSelectorGrammar.RefusalMessage(invalidPlacement) is { } placementRefusal)
        {
            throw new WorkloadParameterValidationException(placementRefusal);
        }

        var lateExistingClaims = resolvedParameters
            .Where(kv => kv.Key.StartsWith("existingClaim.", StringComparison.Ordinal) && !string.IsNullOrWhiteSpace(kv.Value))
            .Select(kv => kv.Key)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToList();
        if (instance.WasDeployed && lateExistingClaims.Count > 0)
        {
            throw new WorkloadParameterValidationException(
                $"Parameter(s) {string.Join(", ", lateExistingClaims)} bind a retained volume (restore) and are " +
                $"install-time only — workload instance {instance.Id} has already been deployed, so a restore binding " +
                "cannot be applied. Uninstall and reinstall to restore into a fresh instance.");
        }

        var claimed = await ValidateExistingClaimsAsync(instance, resolvedParameters, cancellationToken).ConfigureAwait(false);
        return new ParameterValidation(claimed, manifest, resolved, applicable);
    }

    private async Task<bool> ValidateExistingClaimsAsync(
        WorkloadInstance instance,
        IReadOnlyDictionary<string, string?> resolvedParameters,
        CancellationToken cancellationToken)
    {
        var claims = resolvedParameters
            .Where(kv => kv.Key.StartsWith("existingClaim.", StringComparison.Ordinal) && !string.IsNullOrWhiteSpace(kv.Value))
            .Select(kv => (Key: kv.Key, Value: kv.Value!.Trim()))
            .ToList();
        if (claims.Count == 0)
        {
            return false;
        }

        foreach (var claim in claims)
        {
            if (!IsValidDns1123Subdomain(claim.Value))
            {
                throw new WorkloadParameterValidationException(
                    $"Restore binding '{claim.Key}' value '{claim.Value}' is not a valid volume/PVC name " +
                    "(must be a DNS-1123 subdomain: lowercase alphanumeric, '-' or '.', ≤253 chars).");
            }
        }

        var duplicateValues = claims
            .GroupBy(c => c.Value, StringComparer.Ordinal)
            .Where(g => g.Count() > 1)
            .Select(g => $"'{g.Key}' (on {string.Join(", ", g.Select(c => c.Key).OrderBy(k => k, StringComparer.Ordinal))})")
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToList();
        if (duplicateValues.Count > 0)
        {
            throw new WorkloadParameterValidationException(
                $"Restore binding value(s) {string.Join(", ", duplicateValues)} are bound to more than one " +
                "existingClaim key in the same request — a single retained volume cannot be restored into multiple " +
                "workload volumes (fail-closed).");
        }

        IReadOnlyList<RetainedVolume> active;
        try
        {
            active = await retainedVolumeService
                .ListActiveByPluginTypeForInstanceAsync(instance.GroupId, instance.Id, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex,
                "Could not list active retained volumes to validate a restore binding for workload instance {InstanceId} — rejecting the restore (fail-closed).",
                instance.Id);
            throw new WorkloadParameterValidationException(
                "Could not verify the requested restore source (retained-volume lookup failed) — the restore was " +
                "rejected rather than deploying a fresh volume. Retry, or install without restoring.");
        }

        var effectiveProvider = await ResolveEffectiveProviderAsync(instance, cancellationToken).ConfigureAwait(false);
        var byName = active
            .GroupBy(r => r.ResourceName, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);

        var invalid = new List<string>();
        var matchedRows = new List<RetainedVolume>();
        foreach (var claim in claims)
        {
            var targetVolumeId = claim.Key["existingClaim.".Length..];
            var match = byName.TryGetValue(claim.Value, out var rows)
                ? rows.FirstOrDefault(r =>
                    string.Equals(r.VolumeId, targetVolumeId, StringComparison.Ordinal)
                    && r.Provider == effectiveProvider.Id)
                : null;
            if (match is null)
            {
                invalid.Add($"{claim.Key}='{claim.Value}'");
                continue;
            }

            matchedRows.Add(match);
        }

        if (invalid.Count > 0)
        {
            var providerName = effectiveProvider.Id.IsEmpty ? "unknown" : effectiveProvider.Label;
            throw new WorkloadParameterValidationException(
                $"Restore binding(s) {string.Join(", ", invalid)} do not name an active retained volume of plugin " +
                $"type '{instance.GroupId}' matching the target volume id and effective provider " +
                $"({providerName}) — the volume may have been purged, never existed, belong to a different " +
                "plugin/provider, be already claimed by another install, or target the wrong volume. The restore was " +
                "rejected (fail-closed); a fresh install must omit existingClaim.");
        }

        var claimedThisRequest = new List<Guid>();
        try
        {
            foreach (var row in matchedRows)
            {
                bool won;
                try
                {
                    won = await retainedVolumeService
                        .TryClaimForRestoreAsync(row.Id, instance.Id, cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogWarning(ex,
                        "Could not atomically claim retained volume {RetainedVolumeId} for restore into workload instance {InstanceId} — rejecting (fail-closed).",
                        row.Id, instance.Id);
                    throw new WorkloadParameterValidationException(
                        "Could not reserve the requested restore source (retained-volume claim failed) — the restore was " +
                        "rejected rather than risking a double-adoption. Retry, or install without restoring.");
                }

                if (!won)
                {
                    throw new WorkloadParameterValidationException(
                        $"Restore binding '{row.ResourceName}' is already claimed by another install of plugin type " +
                        $"'{instance.GroupId}' — a retained volume can be adopted by at most one instance " +
                        "(fail-closed). Choose a different retained volume, or install without restoring.");
                }

                claimedThisRequest.Add(row.Id);
            }
        }
        catch
        {
            if (claimedThisRequest.Count > 0)
            {
                try
                {
                    var released = await retainedVolumeService.ReleaseClaimsForInstanceAsync(instance.Id, cancellationToken).ConfigureAwait(false);
                    logger.LogInformation(
                        "Released {Count} restore claim(s) held by workload instance {InstanceId} after a later claim/validation failed (all-or-nothing).",
                        released, instance.Id);
                }
                catch (Exception releaseEx) when (releaseEx is not OperationCanceledException)
                {
                    logger.LogWarning(releaseEx,
                        "Could not release the claims held by workload instance {InstanceId} after a failed restore — a stranded claim only blocks re-offering; the rejection stands.",
                        instance.Id);
                }
            }

            throw;
        }

        return true;
    }

    private async Task<WorkloadProviderDescriptor> ResolveEffectiveProviderAsync(
        WorkloadInstance instance, CancellationToken cancellationToken)
    {
        if (instance.Provider is { } bound)
        {
            return providerRegistry.Describe(bound);
        }

        try
        {
            var settings = await settingsService.GetAsync(cancellationToken).ConfigureAwait(false);
            return providerRegistry.Describe(settings.EffectiveProvider);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex,
                "Could not resolve the effective workload provider for install-time restore validation of workload instance {InstanceId} — falling back to the default traits, under which no restore binding can match (best-effort).",
                instance.Id);
        }

        return new WorkloadProviderDescriptor { Id = default };
    }

    private static bool IsValidDns1123Subdomain(string value)
    {
        if (string.IsNullOrEmpty(value) || value.Length > 253)
        {
            return false;
        }

        foreach (var label in value.Split('.'))
        {
            if (label.Length == 0 || label[0] == '-' || label[^1] == '-')
            {
                return false;
            }

            foreach (var ch in label)
            {
                var ok = (ch >= 'a' && ch <= 'z') || (ch >= '0' && ch <= '9') || ch == '-';
                if (!ok)
                {
                    return false;
                }
            }
        }

        return true;
    }

    public async Task BeginUninstallAsync(
        Guid pluginInstanceId,
        WorkloadRetention retention,
        CancellationToken cancellationToken = default)
    {
        var instances = await repository.ListByPluginInstanceAsync(pluginInstanceId, cancellationToken).ConfigureAwait(false);
        if (instances.Count == 0)
        {
            return;
        }

        var context = await contextSource.GetForGroupAsync(instances[0].GroupId, cancellationToken)
            .ConfigureAwait(false);

        var now = clock.UtcNow;
        var unrevokedFrozen = new List<Guid>();
        foreach (var instance in instances)
        {
            try
            {
                await WriteReapplyingOnConflictAsync(
                    instance,
                    row => BeginUninstallInstanceAsync(
                        row, retention, DeclarationOf(row, context), WhyUndeclared(context), UninstallingMessage, now,
                        unrevokedFrozen, cancellationToken),
                    "uninstall",
                    cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex,
                    "Failed to begin uninstall of workload instance {InstanceId} — continuing with the remaining instances (never strand the rest un-Terminating).",
                    instance.Id);
            }
        }

        await NudgeReconcileAllAsync("workload-uninstall", cancellationToken).ConfigureAwait(false);

        if (unrevokedFrozen.Count > 0)
        {
            throw new InvalidOperationException(
                $"The credentials of disowned workload instance(s) {string.Join(", ", unrevokedFrozen)} could not be "
                + "revoked; their rows are kept and the uninstall must be retried.");
        }
    }

    public async Task BeginTeardownAsync(
        WorkloadInstance instance,
        WorkloadManifest? lastDeclaration,
        string whyUndeclared,
        string droppingVersion,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(instance);

        var unrevokedFrozen = new List<Guid>();
        await WriteReapplyingOnConflictAsync(
            instance,
            row => BeginUninstallInstanceAsync(
                row, WorkloadRetention.RetainAll, lastDeclaration, whyUndeclared,
                $"Version {droppingVersion} of the plugin no longer declares this workload — tearing it down, keeping its data.",
                clock.UtcNow, unrevokedFrozen, cancellationToken),
            "dropped-workload teardown",
            cancellationToken).ConfigureAwait(false);
        await NudgeReconcileAsync(instance.Id, "workload-dropped", cancellationToken).ConfigureAwait(false);

        if (unrevokedFrozen.Count > 0)
        {
            throw new InvalidOperationException(
                $"The credentials of disowned workload instance {instance.Id}, which version {droppingVersion} "
                + "no longer declares, could not be revoked; its row is kept and the teardown is retried.");
        }
    }

    private const string UninstallingMessage = "Plugin instance uninstalled — tearing down.";

    private static WorkloadManifest? DeclarationOf(WorkloadInstance instance, WorkloadDeploymentContext? context)
        => context?.Deployment.Workloads
            .FirstOrDefault(w => string.Equals(w.Id, instance.WorkloadId, StringComparison.Ordinal));

    private static string WhyUndeclared(WorkloadDeploymentContext? context)
        => context is null
            ? "the installed manifest cannot be read"
            : "the installed version does not declare it: an update that dropped workloads without tearing them down "
                + "removed it from the plugin and left its row behind";

    private async Task BeginUninstallInstanceAsync(
        WorkloadInstance instance,
        WorkloadRetention retention,
        WorkloadManifest? declaration,
        string whyUndeclared,
        string terminatingMessage,
        DateTimeOffset now,
        List<Guid> unrevokedFrozen,
        CancellationToken cancellationToken)
    {
        if (IsOperatorsRuntime(instance))
        {
            if (!await RevokeCredentialsAsync(instance, cancellationToken).ConfigureAwait(false))
            {
                unrevokedFrozen.Add(instance.Id);
                return;
            }

            await ReleaseRestoreClaimsAsync(instance.Id, cancellationToken).ConfigureAwait(false);
            await repository.DeleteAsync(instance, cancellationToken).ConfigureAwait(false);
            await WorkloadRepairsScanner.ClearOpenIssuesAsync(diagnostics, instance, logger, cancellationToken).ConfigureAwait(false);
            await WriteDisownedUninstallRetentionAuditAsync(instance).ConfigureAwait(false);
            logger.LogInformation(
                "Uninstalling disowned workload instance {InstanceId} ({State}, provider {Provider}) — Reeve dropped its DB record; the operator-owned runtime is RETAINED, never torn down.",
                instance.Id, instance.State, instance.Provider);
            return;
        }

        instance.SetRetention(retention, now);

        if (retention == WorkloadRetention.RetainAll && instance.WasDeployed)
        {
            instance.SnapshotRetainedVolumes(BuildRetainedSnapshot(instance, declaration, whyUndeclared), now);
        }
        else
        {
            instance.SnapshotRetainedVolumes(null, now);
        }

        if (WorkloadInstanceStateMachine.IsValidTransition(instance.State, WorkloadInstanceState.Terminating))
        {
            instance.TransitionTo(WorkloadInstanceState.Terminating, WorkloadStateReason.Uninstalling,
                terminatingMessage, now);
        }
        else
        {
            logger.LogWarning(
                "Cannot transition workload instance {InstanceId} ({State}) to Terminating on uninstall — leaving as-is; the teardown pass will still tear it down.",
                instance.Id, instance.State);
        }

        await repository.UpdateAsync(instance, cancellationToken).ConfigureAwait(false);

        await RevokeCredentialsAsync(instance, cancellationToken).ConfigureAwait(false);
    }

    public async Task<WorkloadForgetResult> ForgetAsync(
        Guid workloadInstanceId,
        Guid? actorUserId,
        CancellationToken cancellationToken = default)
    {
        var instance = await repository.GetByIdAsync(workloadInstanceId, cancellationToken).ConfigureAwait(false);
        for (var attempt = 1; ; attempt++)
        {
            if (instance is null)
            {
                return new WorkloadForgetResult { Outcome = WorkloadForgetOutcome.NotFound };
            }

            if (await RefuseForgetAsync(instance, cancellationToken).ConfigureAwait(false) is { } refusal)
            {
                return refusal;
            }

            var revokeError = await RevokeCredentialsReportingFailureAsync(instance, cancellationToken).ConfigureAwait(false);
            try
            {
                await repository.DeleteAsync(instance, cancellationToken).ConfigureAwait(false);
            }
            catch (WorkloadConcurrencyConflictException conflict)
                when (conflict.EntityId == instance.Id && attempt < ConflictReapplyAttempts)
            {
                instance = await repository.ReloadAsync(workloadInstanceId, cancellationToken).ConfigureAwait(false);
                continue;
            }

            var leftBehind = new WorkloadForgetLeftBehind
            {
                InstanceId = instance.Id,
                GroupInstanceId = instance.GroupInstanceId,
                GroupId = instance.GroupId,
                WorkloadId = instance.WorkloadId,
                Provider = instance.Provider,
                Retention = instance.Retention,
                TerminatingSinceUtc = instance.LastTransitionAtUtc,
                LastTeardownMessage = instance.StateMessage,
                Volumes = await DeclaredVolumesAsync(instance, CancellationToken.None).ConfigureAwait(false),
                RestoreClaims = await HeldRestoreClaimsAsync(instance, CancellationToken.None).ConfigureAwait(false),
                CredentialRevokeError = revokeError,
            };
            await WorkloadRepairsScanner.ClearOpenIssuesAsync(diagnostics, instance, logger, CancellationToken.None)
                .ConfigureAwait(false);
            await WriteForgetAuditAsync(leftBehind, actorUserId).ConfigureAwait(false);
            logger.LogWarning(
                "Forgot workload instance {InstanceId} ({WorkloadId}, provider {Provider}), whose teardown could not complete: its row is deleted and whatever runs on the provider is left to the operator.",
                instance.Id, instance.WorkloadId, instance.Provider);
            return new WorkloadForgetResult { Outcome = WorkloadForgetOutcome.Forgotten, LeftBehind = leftBehind };
        }
    }

    private async Task<IReadOnlyList<RetainedVolumeSnapshotItem>> DeclaredVolumesAsync(
        WorkloadInstance instance, CancellationToken cancellationToken)
    {
        var snapshot = instance.GetRetainedVolumesSnapshot();
        if (snapshot.Count > 0)
        {
            return snapshot;
        }

        try
        {
            var context = await contextSource.GetForGroupAsync(instance.GroupId, cancellationToken).ConfigureAwait(false);
            return DeclarationOf(instance, context) is { } declaration
                ? BuildRetainedSnapshot(instance, declaration, WhyUndeclared(context))
                : snapshot;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex,
                "Could not read the declared volumes of forgotten workload instance {InstanceId}; the result lists none.",
                instance.Id);
            return snapshot;
        }
    }

    private async Task<IReadOnlyList<string>> HeldRestoreClaimsAsync(
        WorkloadInstance instance, CancellationToken cancellationToken)
    {
        try
        {
            var rows = await retainedVolumeService
                .ListActiveByPluginTypeForInstanceAsync(instance.GroupId, instance.Id, cancellationToken)
                .ConfigureAwait(false);
            return rows
                .Where(row => row.ClaimedByWorkloadInstanceId == instance.Id)
                .Select(row => row.ResourceName)
                .ToList();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex,
                "Could not list the restore claims kept for forgotten workload instance {InstanceId}; the result lists none.",
                instance.Id);
            return Array.Empty<string>();
        }
    }

    private async Task<WorkloadForgetResult?> RefuseForgetAsync(WorkloadInstance instance, CancellationToken cancellationToken)
    {
        if (instance.State != WorkloadInstanceState.Terminating)
        {
            return new WorkloadForgetResult { Outcome = WorkloadForgetOutcome.NotTearingDown, BlockingState = instance.State };
        }

        if (await intentProvider.GetActiveIntentIdAsync(instance.GroupInstanceId, cancellationToken).ConfigureAwait(false)
            is { } intentId)
        {
            return new WorkloadForgetResult { Outcome = WorkloadForgetOutcome.UpdateInProgress, ActiveIntentId = intentId };
        }

        var eligibleAt = instance.LastTransitionAtUtc + stuckTeardownAfter;
        return clock.UtcNow <= eligibleAt
            ? new WorkloadForgetResult { Outcome = WorkloadForgetOutcome.TeardownNotStuck, EligibleAtUtc = eligibleAt }
            : null;
    }

    private async Task WriteForgetAuditAsync(WorkloadForgetLeftBehind leftBehind, Guid? actorUserId)
    {
        try
        {
            await auditSink.WriteAsync(
                new WorkloadAuditEntry(
                    ActorUserId: actorUserId,
                    Action: "workloads.forget",
                    EntityType: "workload-instance",
                    EntityId: leftBehind.InstanceId,
                    Metadata: new
                    {
                        instanceId = leftBehind.InstanceId,
                        pluginInstanceId = leftBehind.GroupInstanceId,
                        pluginTypeId = leftBehind.GroupId,
                        workloadId = leftBehind.WorkloadId,
                        provider = leftBehind.Provider?.ToString(),
                        retention = leftBehind.Retention.ToString(),
                        terminatingSinceUtc = leftBehind.TerminatingSinceUtc,
                        lastTeardownMessage = leftBehind.LastTeardownMessage,
                        volumesLeft = leftBehind.Volumes.Select(v => v.ResourceName).ToArray(),
                        restoreClaimsKept = leftBehind.RestoreClaims,
                        credentialRevokeError = leftBehind.CredentialRevokeError,
                    }),
                CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex,
                "Failed to write the forget audit entry for workload instance {InstanceId} — the row is already deleted.",
                leftBehind.InstanceId);
        }
    }

    private static bool IsOperatorsRuntime(WorkloadInstance instance)
        => instance.IsReconcileFrozen && instance.State != WorkloadInstanceState.Switching;

    public async Task<bool> CompleteTeardownAsync(WorkloadInstance instance, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(instance);

        var current = await repository.GetByIdAsync(instance.Id, cancellationToken).ConfigureAwait(false);
        if (current is null)
        {
            return true;
        }

        if (current.State != WorkloadInstanceState.Terminating)
        {
            return false;
        }

        if (await RevokeCredentialsReportingFailureAsync(current, cancellationToken).ConfigureAwait(false) is { } revokeFailure)
        {
            await RecordTeardownFailureAsync(current,
                    $"Teardown waits for the workload's credentials to be revoked; revoking them failed and it retries: {revokeFailure}",
                    cancellationToken)
                .ConfigureAwait(false);
            return false;
        }

        var physicallyRetainedDueToClaim = false;

        var provider = current.Provider is { } bound ? providerRegistry.TryResolve(bound) : null;
        if (provider is not null && current.Provider is not null)
        {
            var retainVolumes = current.Retention == WorkloadRetention.RetainAll;

            if (!retainVolumes)
            {
                physicallyRetainedDueToClaim = await AnyRetainedVolumeClaimedByAnotherInstanceAsync(current, cancellationToken)
                    .ConfigureAwait(false);
                retainVolumes = physicallyRetainedDueToClaim;
                if (physicallyRetainedDueToClaim)
                {
                    logger.LogWarning(
                        "PurgeAll teardown of workload instance {InstanceId} would physically delete a volume another " +
                        "instance is currently RESTORING (claimed) — RETAINING the physical volume(s) instead of destroying them.",
                        current.Id);
                }
            }

            try
            {
                await provider.TeardownAsync(WorkloadRef.FromInstance(current), retainVolumes, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                logger.LogWarning(ex,
                    "Teardown of workload instance {InstanceId} failed: {Reason} — leaving it Terminating for retry.",
                    current.Id, ex.Message);
                await RecordTeardownFailureAsync(current, $"Teardown failed: {ex.Message}", cancellationToken)
                    .ConfigureAwait(false);
                return false;
            }
        }
        else if (current.Provider is { } boundProvider)
        {
            if (compiledProviderCatalog.IsCompiledIn(boundProvider))
            {
                logger.LogWarning(
                    "Provider '{Provider}' is compiled-in but not resolvable while tearing down workload instance {InstanceId} — leaving it Terminating for retry (never deleting a possibly-live runtime).",
                    boundProvider, current.Id);
                await RecordTeardownFailureAsync(current,
                        $"Teardown waits for provider '{boundProvider}', which is not available.", cancellationToken)
                    .ConfigureAwait(false);
                return false;
            }

            logger.LogInformation(
                "Provider '{Provider}' is not compiled into this build — completing teardown of workload instance {InstanceId} by deleting the row (no runtime can exist to delete).",
                boundProvider, current.Id);
        }

        await ReleaseRestoreClaimsAsync(current.Id, cancellationToken).ConfigureAwait(false);

        if (current.Retention == WorkloadRetention.RetainAll && current.WasDeployed)
        {
            var recorded = await TryRecordRetainedVolumesAsync(current, cancellationToken).ConfigureAwait(false);
            if (!recorded)
            {
                logger.LogWarning(
                    "Failed to durably record retained volumes for workload instance {InstanceId} (RetainAll) — leaving it Terminating so the next pass retries (the PVC survives; its pointer must not be lost).",
                    current.Id);
                await RecordTeardownFailureAsync(current,
                        "Teardown removed the workload but could not record its retained volumes; it retries.", cancellationToken)
                    .ConfigureAwait(false);
                return false;
            }
        }

        if (current.Retention == WorkloadRetention.PurgeAll && !physicallyRetainedDueToClaim)
        {
            await MarkRetainedVolumesPurgedAsync(current.Id, clock.UtcNow, cancellationToken).ConfigureAwait(false);
        }

        await repository.DeleteAsync(current, cancellationToken).ConfigureAwait(false);
        await WorkloadRepairsScanner.ClearOpenIssuesAsync(diagnostics, current, logger, cancellationToken).ConfigureAwait(false);
        logger.LogInformation("Workload instance {InstanceId} torn down (retention {Retention}) and removed.",
            current.Id, current.Retention);
        return true;
    }

    private async Task RecordTeardownFailureAsync(WorkloadInstance instance, string message, CancellationToken cancellationToken)
    {
        try
        {
            instance.TransitionTo(WorkloadInstanceState.Terminating, instance.StateReason, message, clock.UtcNow);
            await repository.UpdateAsync(instance, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex,
                "Recording the teardown failure of workload instance {InstanceId} failed; the next pass records it again.",
                instance.Id);
        }
    }

    private async Task ReleaseRestoreClaimsAsync(Guid workloadInstanceId, CancellationToken cancellationToken)
    {
        try
        {
            var released = await retainedVolumeService
                .ReleaseClaimsForInstanceAsync(workloadInstanceId, cancellationToken)
                .ConfigureAwait(false);
            if (released > 0)
            {
                logger.LogInformation(
                    "Released {Count} restore claim(s) held by abandoned/uninstalled workload instance {InstanceId} — the retained volume(s) are offerable again.",
                    released, workloadInstanceId);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex,
                "Failed to release restore claim(s) for workload instance {InstanceId} — continuing the teardown (a stranded claim only blocks re-offering the row; the row-delete is load-bearing).",
                workloadInstanceId);
        }
    }

    private async Task<bool> RevokeCredentialsAsync(WorkloadInstance instance, CancellationToken cancellationToken) =>
        await RevokeCredentialsReportingFailureAsync(instance, cancellationToken).ConfigureAwait(false) is null;

    private async Task<string?> RevokeCredentialsReportingFailureAsync(WorkloadInstance instance, CancellationToken cancellationToken)
    {
        try
        {
            await credentialPort.RevokeAllAsync(instance.Id, actorUserId: null, cancellationToken)
                .ConfigureAwait(false);
            logger.LogInformation("Revoked the credentials of workload instance {InstanceId}.", instance.Id);
            return null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex,
                "Failed to revoke the credentials of workload instance {InstanceId}.",
                instance.Id);
            return ex.Message;
        }
    }

    private async Task WriteDisownedUninstallRetentionAuditAsync(WorkloadInstance instance)
    {
        try
        {
            await auditSink.WriteAsync(
                new WorkloadAuditEntry(
                    ActorUserId: null,
                    Action: "workloads.uninstall.runtime-retained",
                    EntityType: "workload-instance",
                    EntityId: instance.Id,
                    Metadata: new
                    {
                        instanceId = instance.Id,
                        pluginInstanceId = instance.GroupInstanceId,
                        pluginTypeId = instance.GroupId,
                        workloadId = instance.WorkloadId,
                        provider = instance.Provider?.ToString(),
                        recordDropped = true,
                        runtimeRetained = true,
                    }),
                CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex,
                "Failed to write the disowned-uninstall retention audit for workload instance {InstanceId} — continuing (the row-drop is the load-bearing action; the audit is bookkeeping).",
                instance.Id);
        }
    }

    private async Task<bool> AnyRetainedVolumeClaimedByAnotherInstanceAsync(
        WorkloadInstance instance, CancellationToken cancellationToken)
    {
        try
        {
            var names = new HashSet<string>(StringComparer.Ordinal);

            var ownRows = await retainedVolumeService
                .ListActiveBySourceInstanceAsync(instance.Id, cancellationToken)
                .ConfigureAwait(false);
            foreach (var row in ownRows)
            {
                if (!string.IsNullOrWhiteSpace(row.ResourceName))
                {
                    names.Add(row.ResourceName);
                }
            }

            foreach (var item in instance.GetRetainedVolumesSnapshot())
            {
                if (!string.IsNullOrWhiteSpace(item.ResourceName))
                {
                    names.Add(item.ResourceName);
                }
            }

            foreach (var name in names)
            {
                if (await retainedVolumeService
                        .AnyActiveClaimForResourceNameAsync(name, excludingClaimingInstanceId: instance.Id, cancellationToken)
                        .ConfigureAwait(false))
                {
                    return true;
                }
            }

            return false;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex,
                "Could not check whether workload instance {InstanceId}'s retained volumes are claimed by another restore before a PurgeAll teardown — proceeding with the operator's retention choice (best-effort).",
                instance.Id);
            return false;
        }
    }

    private async Task MarkRetainedVolumesPurgedAsync(
        Guid instanceId, DateTimeOffset nowUtc, CancellationToken cancellationToken)
    {
        try
        {
            var purged = await retainedVolumeService
                .MarkPurgedForInstanceAsync(instanceId, nowUtc, cancellationToken)
                .ConfigureAwait(false);
            if (purged > 0)
            {
                logger.LogInformation(
                    "Marked {Count} retained volume(s) purged for PurgeAll-uninstalled workload instance {InstanceId}.",
                    purged, instanceId);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex,
                "Failed to mark retained volumes purged for PurgeAll-uninstalled workload instance {InstanceId} — continuing the teardown (bookkeeping only).",
                instanceId);
        }
    }

    private async Task<bool> TryRecordRetainedVolumesAsync(WorkloadInstance instance, CancellationToken cancellationToken)
    {
        var snapshot = instance.GetRetainedVolumesSnapshot();
        if (snapshot.Count == 0)
        {
            return true;
        }

        var inputs = snapshot
            .Select(volume => new RetainedVolumeInput
            {
                VolumeId = volume.VolumeId,
                ResourceName = volume.ResourceName,
                VolumeNamingScheme = volume.VolumeNamingScheme,
                Size = volume.Size,
                Criticality = volume.Criticality,
                Description = volume.Description,
            })
            .ToList();

        try
        {
            var written = await retainedVolumeService
                .RecordRetainedAsync(instance, inputs, clock.UtcNow, cancellationToken)
                .ConfigureAwait(false);
            if (written > 0)
            {
                logger.LogInformation(
                    "Recorded {Count} retained volume(s) for workload instance {InstanceId} (RetainAll).",
                    written, instance.Id);
            }

            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex,
                "Durable retained-volume write failed for workload instance {InstanceId} (RetainAll).",
                instance.Id);
            return false;
        }
    }

    private IReadOnlyList<RetainedVolumeSnapshotItem> BuildRetainedSnapshot(
        WorkloadInstance instance,
        WorkloadManifest? manifest,
        string whyUndeclared)
    {
        if (manifest is null)
        {
            logger.LogError(
                "Tearing down workload instance {InstanceId} ({GroupId}/{WorkloadId}) keeping its data, but no manifest "
                + "names its volumes ({WhyUndeclared}), so they stay on the runtime and NO retained-volume record is "
                + "written. Find them on the runtime by the workload-instance label carrying this instance id.",
                instance.Id, instance.GroupId, instance.WorkloadId, whyUndeclared);
            return Array.Empty<RetainedVolumeSnapshotItem>();
        }

        if (manifest.Volumes.Count == 0)
        {
            return Array.Empty<RetainedVolumeSnapshotItem>();
        }

        return manifest.Volumes
            .Select(volume => new RetainedVolumeSnapshotItem
            {
                VolumeId = volume.Id,
                ResourceName = ResolveVolumeResourceName(instance, volume.Id),
                VolumeNamingScheme = WorkloadResourceNaming.ResolveRetainedResourceScheme(
                    instance, volume.Id, namers.For(instance.Provider ?? default)),
                Size = volume.Size,
                Criticality = volume.Criticality,
                Description = volume.Description,
            })
            .ToList();
    }

    private string ResolveVolumeResourceName(WorkloadInstance instance, string volumeId)
        => WorkloadResourceNaming.ResolveRetainedResourceName(
            instance, volumeId, namers.For(instance.Provider ?? default));

    private static bool WorkloadHasRestoreBinding(
        WorkloadManifest workload,
        IReadOnlyDictionary<string, string?>? installDeploymentParameters)
    {
        if (installDeploymentParameters is null || installDeploymentParameters.Count == 0)
        {
            return false;
        }

        foreach (var volume in workload.Volumes)
        {
            if (string.IsNullOrEmpty(volume.Id))
            {
                continue;
            }

            if (installDeploymentParameters.TryGetValue($"existingClaim.{volume.Id}", out var value)
                && !string.IsNullOrWhiteSpace(value))
            {
                return true;
            }
        }

        return false;
    }

    private Task NudgeReconcileAllAsync(string reason, CancellationToken cancellationToken)
        => reconcileNudge.NudgeAllAsync(reason, cancellationToken);

    private Task NudgeReconcileAsync(Guid instanceId, string reason, CancellationToken cancellationToken)
        => reconcileNudge.NudgeInstanceAsync(instanceId, reason, cancellationToken);
}

/// <summary>Thrown by <see cref="WorkloadLifecycleService.SetParametersAsync"/> when an install-time parameter
/// edit is rejected at save (a bad path prefix, or the instance can't be mutated) — derives from <see cref="ArgumentException"/> so it maps to a 400, not a 500.</summary>
public sealed class WorkloadParameterValidationException : ArgumentException
{
    public WorkloadParameterValidationException(string message)
        : base(message)
    {
    }

    public WorkloadParameterValidationException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
