using Microsoft.Extensions.Logging;
using Reeve.Workloads.Abstractions;
using Reeve.Workloads.Abstractions.Contracts;
using Reeve.Workloads.Abstractions.Domain.DesiredSpec;
using Reeve.Workloads.Abstractions.Domain.Entities;
using Reeve.Workloads.Abstractions.Rendering;

using Reeve.Workloads.Abstractions.Providers;
using Reeve.Workloads.Core.Bootstrap;
using Reeve.Workloads.Domain.DesiredSpec;

namespace Reeve.Workloads.Core;

internal sealed class WorkloadArtifactService : IWorkloadArtifactService
{
    private readonly IWorkloadInstanceRepository _repository;
    private readonly IWorkloadReconcileInputsProvider _inputsProvider;
    private readonly IWorkloadHostBootstrapSource _hostBootstrap;
    private readonly IWorkloadArtifactRendererRegistry _rendererRegistry;
    private readonly IWorkloadCredentialPort _credentialPort;
    private readonly IWorkloadProviderRegistry _providerRegistry;
    private readonly IWorkloadResourceNamerRegistry _namers;
    private readonly IWorkloadAuditSink _auditSink;
    private readonly WorkloadParameterSecretCodec _parameterSecrets;
    private readonly IWorkloadParameterReferenceResolver _parameterReferences;
    private readonly IActiveWorkloadIntentProvider _intents;
    private readonly IWorkloadGroupManifestSource _manifests;
    private readonly IWorkloadGroupRepository _groups;
    private readonly ILogger<WorkloadArtifactService> _logger;

    public WorkloadArtifactService(
        IWorkloadInstanceRepository repository,
        IWorkloadReconcileInputsProvider inputsProvider,
        IWorkloadHostBootstrapSource hostBootstrap,
        IWorkloadArtifactRendererRegistry rendererRegistry,
        IWorkloadCredentialPort credentialPort,
        IWorkloadProviderRegistry providerRegistry,
        IWorkloadResourceNamerRegistry namers,
        IWorkloadAuditSink auditSink,
        WorkloadParameterSecretCodec parameterSecrets,
        IWorkloadParameterReferenceResolver parameterReferences,
        IActiveWorkloadIntentProvider intents,
        IWorkloadGroupManifestSource manifests,
        IWorkloadGroupRepository groups,
        ILogger<WorkloadArtifactService> logger)
    {
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        _inputsProvider = inputsProvider ?? throw new ArgumentNullException(nameof(inputsProvider));
        _hostBootstrap = hostBootstrap ?? throw new ArgumentNullException(nameof(hostBootstrap));
        _rendererRegistry = rendererRegistry ?? throw new ArgumentNullException(nameof(rendererRegistry));
        _credentialPort = credentialPort ?? throw new ArgumentNullException(nameof(credentialPort));
        _providerRegistry = providerRegistry ?? throw new ArgumentNullException(nameof(providerRegistry));
        _namers = namers ?? throw new ArgumentNullException(nameof(namers));
        _auditSink = auditSink ?? throw new ArgumentNullException(nameof(auditSink));
        _parameterSecrets = parameterSecrets ?? throw new ArgumentNullException(nameof(parameterSecrets));
        _parameterReferences = parameterReferences ?? throw new ArgumentNullException(nameof(parameterReferences));
        _intents = intents ?? throw new ArgumentNullException(nameof(intents));
        _manifests = manifests ?? throw new ArgumentNullException(nameof(manifests));
        _groups = groups ?? throw new ArgumentNullException(nameof(groups));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    private WorkloadProviderDescriptor? HandAppliedProvider => _providerRegistry.Descriptors.FirstOrDefault(d => !d.Materializes);

    public async Task<WorkloadArtifactRenderResult> RenderAsync(
        Guid instanceId,
        WorkloadArtifactFormatId format,
        bool revealSecrets,
        Guid? actorUserId,
        CancellationToken cancellationToken = default)
    {
        var instance = await _repository.GetByIdAsync(instanceId, cancellationToken).ConfigureAwait(false);
        if (instance is null)
        {
            return WorkloadArtifactRenderResult.NotFound("Workload instance not found.");
        }

        if (HandAppliedProvider is not { } handApplied)
        {
            return WorkloadArtifactRenderResult.NoHandAppliedProvider(
                "No registered provider leaves deployment to the operator, so there is nothing to render artifacts for.");
        }

        var siblings = await _repository
            .ListByPluginInstanceAsync(instance.GroupInstanceId, cancellationToken).ConfigureAwait(false);

        var specs = new List<(WorkloadInstance Sibling, WorkloadDesiredSpec Spec)>();
        foreach (var sibling in siblings.OrderBy(s => s.WorkloadId, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var (spec, refusal) = await ComputeManualSpecAsync(sibling, handApplied, cancellationToken)
                .ConfigureAwait(false);
            if (refusal is not null)
            {
                _logger.LogWarning(
                    "Artifact render for instance {InstanceId} refused: the spec of workload {WorkloadId} "
                    + "(instance {SiblingId}) could not be computed — {Refusal}. Nothing rendered or minted.",
                    instance.Id, sibling.WorkloadId, sibling.Id, refusal);
                return WorkloadArtifactRenderResult.HostRefused(refusal);
            }

            if (spec is null)
            {
                _logger.LogDebug(
                    "Skipping artifact render for workload {WorkloadId} (instance {SiblingId}) of the render for "
                    + "instance {InstanceId} — no longer declared.",
                    sibling.WorkloadId, sibling.Id, instance.Id);
                continue;
            }

            specs.Add((sibling, spec));
        }

        var items = new List<RenderedWorkloadArtifactItem>();
        var anySecretsRevealed = false;
        foreach (var (sibling, spec) in specs)
        {
            cancellationToken.ThrowIfCancellationRequested();

            IReadOnlyDictionary<string, string> revealed = new Dictionary<string, string>(StringComparer.Ordinal);
            if (revealSecrets && !sibling.IsHeldByHost && !sibling.IsHostHoldPending)
            {
                revealed = await RevealCredentialsAsync(sibling.Id, spec, cancellationToken).ConfigureAwait(false);
                await WriteRevealAuditAsync(sibling.Id, format, actorUserId, cancellationToken).ConfigureAwait(false);
                anySecretsRevealed |= revealed.Count > 0;
            }

            var options = new WorkloadArtifactRenderOptions
            {
                RevealedCredentials = revealed,
                ExternalSecretMode = revealed.Count == 0,
            };

            RenderedWorkloadArtifactItem item;
            try
            {
                var rendered = _rendererRegistry.Render(format, spec, options);
                item = new RenderedWorkloadArtifactItem { WorkloadId = sibling.WorkloadId, Artifact = rendered };
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex,
                    "Rendering the manual {Format} artifact for workload {WorkloadId} (instance {InstanceId}) faulted; "
                    + "surfacing a per-workload error and continuing with the other workloads.",
                    format, sibling.WorkloadId, sibling.Id);
                item = new RenderedWorkloadArtifactItem
                {
                    WorkloadId = sibling.WorkloadId,
                    Artifact = null,
                    Error = $"This workload's {format} artifact could not be rendered: {ex.Message}",
                };
            }

            items.Add(item);
        }

        return new WorkloadArtifactRenderResult
        {
            Outcome = WorkloadArtifactRenderOutcome.Success,
            Format = format,
            SecretsRevealed = anySecretsRevealed,
            Workloads = items,
        };
    }

    private async Task<(WorkloadDesiredSpec? Spec, string? Refusal)> ComputeManualSpecAsync(
        WorkloadInstance instance, WorkloadProviderDescriptor handApplied, CancellationToken cancellationToken)
    {
        var answer = await _inputsProvider.GetInputsAsync(instance, cancellationToken).ConfigureAwait(false);
        if (answer.Refusal is { } refusal)
        {
            return (null, refusal);
        }

        if (answer.Inputs is not { } installed)
        {
            return (null, null);
        }

        var boundToHandApplied = installed.EffectiveProvider.Id == handApplied.Id;
        ActiveWorkloadIntentReplay? replay = null;
        if (boundToHandApplied)
        {
            try
            {
                replay = await _intents
                    .TryGetActiveReplayAsync(instance.GroupInstanceId, instance.WorkloadId, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex,
                    "Reading the active update of workload {WorkloadId} (instance {InstanceId}) failed while rendering manual artifacts.",
                    instance.WorkloadId, instance.Id);
                return (null, $"the active update of workload '{instance.WorkloadId}' could not be read: {ex.Message}");
            }
        }

        var (inputs, versionRefusal) = await AtReplayVersionAsync(instance, installed, replay, cancellationToken)
            .ConfigureAwait(false);
        if (versionRefusal is not null || inputs is null)
        {
            return (null, versionRefusal);
        }

        IReadOnlyDictionary<string, string?> resolvedParameters;
        try
        {
            resolvedParameters = WorkloadParameterResolver.Resolve(
                inputs.WorkloadManifest, ReplayOperatorValues(inputs.ResolvedParameters, replay), handApplied);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex,
                "Parameter resolution failed while rendering manual artifacts for workload {WorkloadId} (instance {InstanceId}).",
                instance.WorkloadId, instance.Id);
            return (null, $"the parameters of workload '{instance.WorkloadId}' do not resolve: {ex.Message}");
        }

        var request = WorkloadHostBootstrapStep.Request(
            instance.Id, instance.GroupId, handApplied.Id, inputs.Namer, inputs.WorkloadManifest,
            WorkloadBootstrapAudience.HandApplied);
        var composed = await WorkloadHostBootstrapStep.ComposeAsync(_hostBootstrap, request, inputs.WorkloadManifest, cancellationToken)
            .ConfigureAwait(false);
        if (composed.Refusal is { } handAppliedRefusal)
        {
            return (null, handAppliedRefusal);
        }

        var bootstrap = WorkloadHostBootstrapStep.Context(request, composed) with
        {
            RestartNonce = inputs.Bootstrap.RestartNonce,
            AppVersion = inputs.Bootstrap.AppVersion,
        };

        var referencesForHash = UnpinnedReferenceRolloutGuard.ProjectReferencesForHash(instance, inputs.ParameterReferencesForHash);
        var handAppliedNamer = _namers.For(handApplied.Id);
        var spec = WorkloadDesiredSpecFactory.ComputeWithParameterReferences(
            inputs.WorkloadManifest,
            inputs.Settings,
            resolvedParameters,
            referencesForHash,
            bootstrap,
            handApplied,
            handAppliedNamer,
            instance.GroupInstanceId,
            instance.GroupId,
            instance.Id,
            inputs.InstallationId,
            inputs.ProductImages);

        if (!boundToHandApplied)
        {
            return (WithLiveVolumeNames(spec, inputs.Namer), null);
        }

        string reconciledHash;
        try
        {
            reconciledHash = await WorkloadReconcileDesiredSpec
                .DesiredSpecHashAsync(_parameterSecrets, _parameterReferences, instance, installed, replay, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (WorkloadParameterResolutionException ex)
        {
            _logger.LogWarning(ex,
                "Parameter resolution failed while computing the reconcile spec hash for workload {WorkloadId} (instance {InstanceId}).",
                instance.WorkloadId, instance.Id);
            return (null, $"the parameters of workload '{instance.WorkloadId}' do not resolve: {ex.Message}");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex,
                "Computing the reconcile spec hash for workload {WorkloadId} (instance {InstanceId}) faulted.",
                instance.WorkloadId, instance.Id);
            return (null, $"the spec of workload '{instance.WorkloadId}' could not be computed: {ex.Message}");
        }

        return (WorkloadDesiredSpecFactory.WithSpecHash(spec, reconciledHash, handAppliedNamer, inputs.InstallationId), null);
    }

    private static WorkloadDesiredSpec WithLiveVolumeNames(WorkloadDesiredSpec spec, IWorkloadResourceNamer liveNamer)
    {
        var subject = new WorkloadNamingSubject { GroupId = spec.GroupId, WorkloadId = spec.WorkloadId, Inst8 = spec.Inst8 };
        return spec with
        {
            Volumes = spec.Volumes
                .Select(v => v.ExistingClaim ? v : v with { ResourceName = liveNamer.VolumeName(subject, v.Id) })
                .ToList(),
        };
    }

    private async Task<(WorkloadReconcileInputs? Inputs, string? Refusal)> AtReplayVersionAsync(
        WorkloadInstance instance,
        WorkloadReconcileInputs installed,
        ActiveWorkloadIntentReplay? replay,
        CancellationToken cancellationToken)
    {
        if (replay?.Version is not { } version
            || string.Equals(version, installed.InstalledGroupVersion, StringComparison.Ordinal))
        {
            return (installed, null);
        }

        var module = await _groups.GetAsync(instance.GroupId, cancellationToken).ConfigureAwait(false);
        var read = module is null
            ? GroupManifestRead.Absent
            : await _manifests.GetAtVersionAsync(module.SourceKey, instance.GroupId, version, cancellationToken)
                .ConfigureAwait(false);
        var workload = read.Manifest?.Workloads
            .FirstOrDefault(candidate => string.Equals(candidate.Id, instance.WorkloadId, StringComparison.Ordinal));
        if (workload is null)
        {
            return (null, $"the manifest of module '{instance.GroupId}' at version '{version}', which the open update "
                + $"deploys, does not declare workload '{instance.WorkloadId}' or could not be read");
        }

        return (installed with
        {
            WorkloadManifest = workload,
            Bootstrap = installed.Bootstrap with { AppVersion = version },
        }, null);
    }

    private static IReadOnlyDictionary<string, string?> ReplayOperatorValues(
        IReadOnlyDictionary<string, string?> stored, ActiveWorkloadIntentReplay? replay)
    {
        if (replay is null)
        {
            return stored;
        }

        if (replay.StoredParameters is { } snapshot)
        {
            return snapshot;
        }

        if (replay.Direction != WorkloadUpdateDirection.Forward || replay.SubmittedParameters.Count == 0)
        {
            return stored;
        }

        var merged = new Dictionary<string, string?>(stored, StringComparer.Ordinal);
        foreach (var (name, value) in replay.SubmittedParameters)
        {
            merged[name] = value;
        }

        return merged;
    }

    private Task<IReadOnlyDictionary<string, string>> RevealCredentialsAsync(
        Guid instanceId, WorkloadDesiredSpec spec, CancellationToken cancellationToken)
    {
        var credentials = WorkloadSpecCredentials.Of(spec);
        return credentials.Refusal is null
            ? credentials.ResolveAsync(_credentialPort, instanceId, held: new Dictionary<string, string>(StringComparer.Ordinal), cancellationToken)
            : Task.FromResult<IReadOnlyDictionary<string, string>>(new Dictionary<string, string>(StringComparer.Ordinal));
    }

    private async Task WriteRevealAuditAsync(Guid instanceId, WorkloadArtifactFormatId format, Guid? actorUserId, CancellationToken cancellationToken)
    {
        try
        {
            await _auditSink.WriteAsync(
                new WorkloadAuditEntry(
                    ActorUserId: actorUserId,
                    Action: "workloads.artifact.revealed",
                    EntityType: "workload-instance",
                    EntityId: instanceId,
                    Metadata: new { instanceId, format = format.PersistedValue }),
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex,
                "Failed to write the workloads.artifact.revealed audit entry for instance {InstanceId} — the render continues but the reveal is un-audited.",
                instanceId);
        }
    }
}

/// <summary>Renders the manual-takeover artifacts through the EXISTING artifact service, BEFORE ownership is
/// released and without rotating a running credential. Any non-success THROWS so the switch unwinds.</summary>
public sealed class WorkloadArtifactTakeoverRenderer : IWorkloadSwitchTakeoverRenderer
{
    /// <summary>The audit action for one takeover-artifact render. Published because it is what a host's
    /// audit log actually contains, so a host asserts on this name rather than hard-coding the string.
    /// Deliberately static readonly, not const: a const is inlined into every consumer, so a later change
    /// to the name would reach nobody until they rebuilt.</summary>
    public static readonly string TakeoverAuditAction = "workloads.switch.takeover-rendered";

    private readonly IWorkloadArtifactService _artifactService;
    private readonly IWorkloadArtifactRendererRegistry _rendererRegistry;
    private readonly IWorkloadAuditSink _auditSink;
    private readonly ILogger<WorkloadArtifactTakeoverRenderer> _logger;

    public WorkloadArtifactTakeoverRenderer(
        IWorkloadArtifactService artifactService,
        IWorkloadArtifactRendererRegistry rendererRegistry,
        IWorkloadAuditSink auditSink,
        ILogger<WorkloadArtifactTakeoverRenderer> logger)
    {
        _artifactService = artifactService ?? throw new ArgumentNullException(nameof(artifactService));
        _rendererRegistry = rendererRegistry ?? throw new ArgumentNullException(nameof(rendererRegistry));
        _auditSink = auditSink ?? throw new ArgumentNullException(nameof(auditSink));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public async Task RenderTakeoverAsync(WorkloadInstance instance, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(instance);

        var formats = _rendererRegistry.SelfContainedFormats;
        if (formats.Count == 0)
        {
            throw new WorkloadSwitchTakeoverRenderException(
                $"No registered artifact renderer produces a deployment an operator can run on its own, so the "
                + $"manual takeover of instance {instance.Id} has nothing to hand over. The managed→manual switch is "
                + "aborted — the workload stays under Reeve management (runtime/data untouched).");
        }

        foreach (var format in formats)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var result = await _artifactService
                .RenderAsync(instance.Id, format, revealSecrets: false, actorUserId: null, cancellationToken)
                .ConfigureAwait(false);

            EnsureRenderable(instance, format, result);

            await WriteTakeoverAuditAsync(instance, format, cancellationToken).ConfigureAwait(false);
        }
    }

    private static void EnsureRenderable(WorkloadInstance instance, WorkloadArtifactFormatId format, WorkloadArtifactRenderResult result)
    {
        if (result.Outcome != WorkloadArtifactRenderOutcome.Success)
        {
            throw new WorkloadSwitchTakeoverRenderException(
                $"The manual-takeover {format} artifact for instance {instance.Id} could not be rendered "
                + $"({result.Outcome}: {result.Error ?? "no detail"}). The managed→manual switch is aborted — "
                + "the workload stays under Reeve management (runtime/data untouched).");
        }

        if (result.Workloads.Count == 0)
        {
            throw new WorkloadSwitchTakeoverRenderException(
                $"The manual-takeover {format} render for instance {instance.Id} produced no artifacts — the switch "
                + "is aborted (there is nothing to hand the operator).");
        }

        var faulted = result.Workloads.FirstOrDefault(w => w.Artifact is null);
        if (faulted is not null)
        {
            throw new WorkloadSwitchTakeoverRenderException(
                $"The manual-takeover {format} artifact for workload '{faulted.WorkloadId}' (instance {instance.Id}) "
                + $"faulted: {faulted.Error ?? "no detail"}. The switch is aborted so the operator is never handed an "
                + "incomplete takeover set.");
        }
    }

    private async Task WriteTakeoverAuditAsync(WorkloadInstance instance, WorkloadArtifactFormatId format, CancellationToken cancellationToken)
    {
        try
        {
            await _auditSink.WriteAsync(
                new WorkloadAuditEntry(
                    ActorUserId: null,
                    Action: TakeoverAuditAction,
                    EntityType: "workload-instance",
                    EntityId: instance.Id,
                    Metadata: new
                    {
                        instanceId = instance.Id,
                        format = format.PersistedValue,
                        tokenRevealed = false,
                    }),
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex,
                "workloads.switch: failed to write the {Action} audit entry for instance {InstanceId} ({Format}) — the render succeeded but is un-audited.",
                TakeoverAuditAction, instance.Id, format);
        }
    }
}

/// <summary>Thrown when the manual-takeover artifacts cannot be fully rendered, so the switch service ABORTS and
/// unwinds instead of releasing ownership — a render failure must never strand the workload.</summary>
public sealed class WorkloadSwitchTakeoverRenderException : InvalidOperationException
{
    public WorkloadSwitchTakeoverRenderException(string message) : base(message) { }
}
