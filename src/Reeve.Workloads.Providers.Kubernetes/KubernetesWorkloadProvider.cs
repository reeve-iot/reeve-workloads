using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using k8s;
using k8s.Autorest;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Reeve.Workloads.Abstractions;
using Reeve.Workloads.Abstractions.Contracts;
using Reeve.Workloads.Abstractions.Contracts.Deployment;
using Reeve.Workloads.Abstractions.Domain;
using Reeve.Workloads.Abstractions.Domain.DesiredSpec;
using Reeve.Workloads.Abstractions.Domain.Network;
using Reeve.Workloads.Abstractions.Providers;
using Reeve.Workloads.Domain;
using Reeve.Workloads.Domain.DesiredSpec;

namespace Reeve.Workloads.Providers.Kubernetes;

internal sealed class KubernetesWorkloadProvider : IWorkloadProvider
{
    public const string FieldManager = "reeve-workloads";

    private const string AutoImagePullSecretSuffix = "-imagepull";

    private const string DockerConfigJsonKey = ".dockerconfigjson";

    private readonly IKubernetesClientFactory _clientFactory;
    private readonly IKubernetesObjectClient _objectClient;
    private readonly KubernetesWorkloadRenderer _renderer;
    private readonly UnsupportedPodPatchApplier _patchApplier;
    private readonly KubernetesPatchRevalidator _patchRevalidator;
    private readonly IKubernetesLogReader _logReader;
    private readonly IKubernetesClusterInspector _clusterInspector;
    private readonly KubernetesWorkloadPreflightChecker _preflightChecker;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly KubernetesNamespaceResolver _namespaceResolver;
    private readonly ILogger<KubernetesWorkloadProvider> _logger;

    public KubernetesWorkloadProvider(
        IKubernetesClientFactory clientFactory,
        IKubernetesObjectClient objectClient,
        KubernetesWorkloadRenderer renderer,
        UnsupportedPodPatchApplier patchApplier,
        KubernetesPatchRevalidator patchRevalidator,
        IKubernetesLogReader logReader,
        IKubernetesClusterInspector clusterInspector,
        KubernetesWorkloadPreflightChecker preflightChecker,
        IServiceScopeFactory scopeFactory,
        KubernetesNamespaceResolver namespaceResolver,
        ILogger<KubernetesWorkloadProvider> logger)
    {
        _clientFactory = clientFactory ?? throw new ArgumentNullException(nameof(clientFactory));
        _objectClient = objectClient ?? throw new ArgumentNullException(nameof(objectClient));
        _renderer = renderer ?? throw new ArgumentNullException(nameof(renderer));
        _patchApplier = patchApplier ?? throw new ArgumentNullException(nameof(patchApplier));
        _patchRevalidator = patchRevalidator ?? throw new ArgumentNullException(nameof(patchRevalidator));
        _logReader = logReader ?? throw new ArgumentNullException(nameof(logReader));
        _clusterInspector = clusterInspector ?? throw new ArgumentNullException(nameof(clusterInspector));
        _preflightChecker = preflightChecker ?? throw new ArgumentNullException(nameof(preflightChecker));
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
        _namespaceResolver = namespaceResolver ?? throw new ArgumentNullException(nameof(namespaceResolver));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public WorkloadProviderDescriptor Descriptor => KubernetesProvider.Descriptor;

    public async Task<WorkloadApplyResult> ApplyAsync(
        WorkloadDesiredSpec spec,
        IProgress<WorkloadApplyProgress>? progress,
        CancellationToken cancellationToken = default,
        bool forceConflicts = false)
    {
        ArgumentNullException.ThrowIfNull(spec);

        var settings = await ResolveKubernetesSettingsAsync(cancellationToken).ConfigureAwait(false);
        var ns = _namespaceResolver.Resolve(settings);
        if (string.IsNullOrWhiteSpace(ns))
        {
            return WorkloadApplyResult.Failed(WorkloadApplyReason.Rejected, "workloads namespace not configured");
        }

        var credentials = WorkloadSpecCredentials.Of(spec);
        if (credentials.Refusal is { } credentialReject)
        {
            _logger.LogError("Apply of instance {InstanceId} refused: {Reason}", spec.InstanceId, credentialReject);
            return WorkloadApplyResult.Failed(WorkloadApplyReason.Rejected, credentialReject);
        }

        IKubernetes client;
        try
        {
            client = await _clientFactory.CreateClientAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Apply of instance {InstanceId} could not build the API client.", spec.InstanceId);
            return WorkloadApplyResult.Failed(WorkloadApplyReason.ProviderUnreachable, "could not reach the Kubernetes API server");
        }

        RegistryCredential? autoPullCredential = null;
        string? autoPullSecretName = null;
        if (string.IsNullOrWhiteSpace(settings.ImagePullSecretName))
        {
            autoPullCredential = await ResolveImagePullFallbackAsync(spec.Image, cancellationToken).ConfigureAwait(false);
            if (autoPullCredential is not null)
            {
                autoPullSecretName = spec.SecretName + AutoImagePullSecretSuffix;
            }
        }

        RenderedKubernetesObjects rendered;
        try
        {
            rendered = _renderer.Render(
                spec,
                new KubernetesRenderContext
                {
                    Namespace = ns,
                    ImagePullSecretName = settings.ImagePullSecretName,
                    DefaultStorageClassName = settings.DefaultStorageClassName,
                    AutoImagePullSecretName = autoPullSecretName,
                },
                spec.ScanReadyNodeNames,
                spec.PendingScanRunId);
        }
        catch (WorkloadResourceRenderException ex)
        {
            _logger.LogError(ex, "Apply of instance {InstanceId} refused: {Reason}", spec.InstanceId, ex.Message);
            return WorkloadApplyResult.Failed(WorkloadApplyReason.Rejected, ex.Message);
        }

        if (spec.UnsupportedPodPatch is { } patch)
        {
            if (!settings.AllowUnsupportedOverrides)
            {
                return WorkloadApplyResult.Failed(
                    WorkloadApplyReason.Rejected,
                    "workload uses unsupportedPodPatch but allowUnsupportedOverrides is off");
            }

            try
            {
                rendered = _patchApplier.Apply(rendered, patch.PatchJson);
            }
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
            {
                return WorkloadApplyResult.Failed(
                    WorkloadApplyReason.Rejected,
                    $"unsupportedPodPatch could not be applied: {ex.Message}");
            }

            var violations = _patchRevalidator.Validate(spec, rendered);
            if (violations.Count > 0)
            {
                return WorkloadApplyResult.Failed(
                    WorkloadApplyReason.Rejected,
                    "unsupportedPodPatch escalates beyond the desired spec's resolved privileges: " + string.Join("; ", violations));
            }
        }

        IReadOnlyDictionary<string, string> held;
        try
        {
            held = credentials.Credentials.Count == 0
                ? new Dictionary<string, string>(StringComparer.Ordinal)
                : await _objectClient
                    .ReadSecretValuesAsync(client, ns, spec.SecretName, credentials.Names, cancellationToken)
                    .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Reading the existing workload credentials failed for instance {InstanceId}; not minting.", spec.InstanceId);
            return WorkloadApplyResult.Failed(WorkloadApplyReason.ProviderUnreachable, "could not read the workload's existing credentials");
        }

        IReadOnlyDictionary<string, string> resolved = new Dictionary<string, string>(StringComparer.Ordinal);
        if (credentials.Credentials.Count > 0)
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                resolved = await credentials
                    .ResolveAsync(scope.ServiceProvider.GetRequiredService<IWorkloadCredentialPort>(), spec.InstanceId, held, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Resolving the workload credentials failed for instance {InstanceId}.", spec.InstanceId);
                return WorkloadApplyResult.Failed(WorkloadApplyReason.Error, "could not resolve the workload's credentials");
            }
        }

        rendered.Secret.StringData ??= new System.Collections.Generic.Dictionary<string, string>();
        foreach (var credential in credentials.Credentials)
        {
            foreach (var name in credential.Names)
            {
                rendered.Secret.StringData[name] = resolved.GetValueOrDefault(credential.CredentialId, string.Empty);
            }
        }

        if (WorkloadSpecCredentials.Digest(resolved) is { } credentialDigest
            && (rendered.Deployment?.Spec?.Template ?? rendered.DaemonSet?.Spec?.Template) is { } template)
        {
            template.Metadata ??= new k8s.Models.V1ObjectMeta();
            template.Metadata.Annotations ??= new System.Collections.Generic.Dictionary<string, string>();
            template.Metadata.Annotations[KubernetesWorkloadRenderer.CredentialDigestAnnotationKey] = credentialDigest;
        }

        foreach (var kv in spec.SecretMaterial)
        {
            rendered.Secret.StringData[kv.Key] = kv.Value;
        }

        var secretName = rendered.Secret.Metadata?.Name ?? spec.SecretName;
        var secretOutcome = await _objectClient
            .ServerSideApplyAsync(client, ns, KubernetesObjectKind.Secret, secretName,
                KubernetesJson.Serialize(rendered.Secret), FieldManager, force: forceConflicts, cancellationToken)
            .ConfigureAwait(false);
        if (MapNonSuccess(secretOutcome) is { } secretFail)
        {
            return secretFail;
        }

        if (rendered.ImagePullSecret is { } pullSecret && autoPullCredential is not null)
        {
            pullSecret.StringData ??= new System.Collections.Generic.Dictionary<string, string>();
            pullSecret.StringData[DockerConfigJsonKey] =
                BuildDockerConfigJson(autoPullCredential.HostPattern, autoPullCredential.Username, autoPullCredential.Token);

            var pullSecretName = pullSecret.Metadata?.Name ?? autoPullSecretName!;
            var pullOutcome = await _objectClient
                .ServerSideApplyAsync(client, ns, KubernetesObjectKind.Secret, pullSecretName,
                    KubernetesJson.Serialize(pullSecret), FieldManager, force: forceConflicts, cancellationToken)
                .ConfigureAwait(false);
            if (MapNonSuccess(pullOutcome) is { } pullFail)
            {
                return pullFail;
            }
        }

        var adoptedClaimNames = spec.Volumes
            .Where(v => v.ExistingClaim && !string.IsNullOrWhiteSpace(v.ResourceName))
            .Select(v => v.ResourceName.Trim())
            .ToHashSet(StringComparer.Ordinal);

        var terminatingClaims = new List<string>();
        foreach (var pvc in rendered.PersistentVolumeClaims)
        {
            var isAdoptedClaim = pvc.Metadata?.Name is { Length: > 0 } pvcName && adoptedClaimNames.Contains(pvcName);
            try
            {
                await _objectClient.EnsurePvcAsync(client, ns, pvc, isAdoptedClaim, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (KubernetesPvcTerminatingException ex)
            {
                _logger.LogInformation(
                    "PVC {Pvc} of instance {InstanceId} is still being deleted (Terminating); the rest is applied and the apply retries next pass.",
                    ex.ClaimName, spec.InstanceId);
                terminatingClaims.Add(ex.ClaimName);
            }
            catch (InvalidOperationException ex) when (isAdoptedClaim)
            {
                _logger.LogWarning(ex,
                    "Restore apply for instance {InstanceId} FAILED: adopted claim PVC {Pvc} does not exist (fail-closed).",
                    spec.InstanceId, pvc.Metadata?.Name);
                return WorkloadApplyResult.Failed(WorkloadApplyReason.Rejected, ex.Message);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Ensuring PVC {Pvc} failed for instance {InstanceId}.",
                    pvc.Metadata?.Name, spec.InstanceId);
                return WorkloadApplyResult.Failed(WorkloadApplyReason.ProviderUnreachable,
                    $"could not ensure persistent volume claim '{pvc.Metadata?.Name}'");
            }
        }

        var keptKind = rendered.Deployment is not null ? KubernetesObjectKind.Deployment
            : rendered.DaemonSet is not null ? KubernetesObjectKind.DaemonSet
            : KubernetesObjectKind.Job;
        try
        {
            await _objectClient
                .DeleteOtherWorkloadKindsAsync(
                    client, ns, DefaultWorkloadResourceNamer.Instance.InstanceSelectorLabels(spec.InstanceId), keptKind, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Removing the objects of a previous workload kind failed for instance {InstanceId}.", spec.InstanceId);
            return WorkloadApplyResult.Failed(WorkloadApplyReason.ProviderUnreachable,
                "could not remove this workload's objects of its previous kind (Deployment, DaemonSet or scan Jobs)");
        }

        if (rendered.Deployment is { } deployment)
        {
            var outcome = await _objectClient
                .ServerSideApplyAsync(client, ns, KubernetesObjectKind.Deployment,
                    deployment.Metadata?.Name ?? spec.ResourceName,
                    KubernetesJson.Serialize(deployment), FieldManager, force: forceConflicts, cancellationToken)
                .ConfigureAwait(false);
            if (MapNonSuccess(outcome) is { } fail)
            {
                return fail;
            }
        }
        else if (rendered.DaemonSet is { } daemonSet)
        {
            var outcome = await _objectClient
                .ServerSideApplyAsync(client, ns, KubernetesObjectKind.DaemonSet,
                    daemonSet.Metadata?.Name ?? spec.ResourceName,
                    KubernetesJson.Serialize(daemonSet), FieldManager, force: forceConflicts, cancellationToken)
                .ConfigureAwait(false);
            if (MapNonSuccess(outcome) is { } fail)
            {
                return fail;
            }
        }

        if (rendered.Service is { } service)
        {
            var outcome = await _objectClient
                .ServerSideApplyAsync(client, ns, KubernetesObjectKind.Service,
                    service.Metadata?.Name ?? spec.ResourceName,
                    KubernetesJson.Serialize(service), FieldManager, force: forceConflicts, cancellationToken)
                .ConfigureAwait(false);
            if (MapNonSuccess(outcome) is { } fail)
            {
                return fail;
            }
        }

        if (spec.Kind == WorkloadKind.ScanPerNode && !string.IsNullOrEmpty(spec.PendingScanRunId))
        {
            var currentRunLabel = WorkloadResourceNaming.ScanRunLabelValue(spec.PendingScanRunId);
            await _objectClient
                .DeleteScanJobsByLabelAsync(
                    client, ns, DefaultWorkloadResourceNamer.Instance.InstanceSelectorLabels(spec.InstanceId), currentRunLabel, cancellationToken)
                .ConfigureAwait(false);

            foreach (var job in rendered.Jobs)
            {
                var outcome = await _objectClient
                    .ServerSideApplyAsync(client, ns, KubernetesObjectKind.Job,
                        job.Metadata?.Name ?? spec.ResourceName,
                        KubernetesJson.Serialize(job), FieldManager, force: forceConflicts, cancellationToken)
                    .ConfigureAwait(false);
                if (MapNonSuccess(outcome) is { } fail)
                {
                    return fail;
                }
            }
        }

        if (terminatingClaims.Count > 0)
        {
            return WorkloadApplyResult.Failed(WorkloadApplyReason.ProviderUnreachable,
                $"persistent volume claim(s) {string.Join(", ", terminatingClaims.Select(n => $"'{n}'"))}: still being deleted (Terminating); the apply retries next pass and ensures the claim once the deletion completes");
        }

        return WorkloadApplyResult.Applied(spec.SpecHash);
    }

    public async Task TeardownAsync(WorkloadRef reference, bool retainVolumes, CancellationToken cancellationToken = default)
    {
        var settings = await ResolveKubernetesSettingsAsync(cancellationToken).ConfigureAwait(false);
        var ns = _namespaceResolver.Resolve(settings);
        if (string.IsNullOrWhiteSpace(ns))
        {
            throw new InvalidOperationException(
                $"Teardown of instance {reference.InstanceId} cannot run: the workloads namespace is not configured.");
        }

        var client = await _clientFactory.CreateClientAsync(cancellationToken).ConfigureAwait(false);
        await _objectClient
            .DeleteWorkloadByLabelAsync(client, ns, reference.InstanceSelectorLabels, retainPvc: retainVolumes, cancellationToken)
            .ConfigureAwait(false);
    }

    public Task SuspendAsync(WorkloadRef reference, CancellationToken cancellationToken = default)
        => SetSuspendedAsync(reference, suspended: true, cancellationToken);

    public Task ResumeAsync(WorkloadRef reference, CancellationToken cancellationToken = default)
        => SetSuspendedAsync(reference, suspended: false, cancellationToken);

    private async Task SetSuspendedAsync(WorkloadRef reference, bool suspended, CancellationToken cancellationToken)
    {
        var settings = await ResolveKubernetesSettingsAsync(cancellationToken).ConfigureAwait(false);
        var ns = _namespaceResolver.Resolve(settings);
        if (string.IsNullOrWhiteSpace(ns))
        {
            _logger.LogWarning("{Op} of instance {InstanceId} skipped — workloads namespace not configured.",
                suspended ? "Suspend" : "Resume", reference.InstanceId);
            return;
        }

        try
        {
            var client = await _clientFactory.CreateClientAsync(cancellationToken).ConfigureAwait(false);

            var raw = await _objectClient
                .ReadWorkloadAsync(client, ns, reference.InstanceSelectorLabels, cancellationToken)
                .ConfigureAwait(false);

            if (raw is null)
            {
                _logger.LogDebug("{Op} of instance {InstanceId}: provider unreachable; no-op (the reconciler retries).",
                    suspended ? "Suspend" : "Resume", reference.InstanceId);
                return;
            }

            if (raw is { Found: false, Jobs.Count: > 0 })
            {
                await _objectClient
                    .SetScanJobsSuspendedAsync(client, ns, reference.InstanceSelectorLabels, suspended, cancellationToken)
                    .ConfigureAwait(false);
                return;
            }

            if (!raw.Found)
            {
                _logger.LogDebug("{Op} of instance {InstanceId}: no workload object matches the instance selector; no-op.",
                    suspended ? "Suspend" : "Resume", reference.InstanceId);
                return;
            }

            if (string.Equals(raw.Kind, "DaemonSet", StringComparison.Ordinal))
            {
                await _objectClient
                    .SetDaemonSetSuspendedAsync(client, ns, reference.InstanceSelectorLabels, suspended, reference.Inst8, cancellationToken)
                    .ConfigureAwait(false);
            }
            else
            {
                await _objectClient
                    .ScaleDeploymentAsync(client, ns, reference.InstanceSelectorLabels, suspended ? 0 : 1, cancellationToken)
                    .ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "{Op} of instance {InstanceId} failed (will retry).",
                suspended ? "Suspend" : "Resume", reference.InstanceId);
        }
    }

    public async Task<WorkloadStatus> GetStatusAsync(WorkloadRef reference, CancellationToken cancellationToken = default)
    {
        var settings = await ResolveKubernetesSettingsAsync(cancellationToken).ConfigureAwait(false);
        var ns = _namespaceResolver.Resolve(settings);
        if (string.IsNullOrWhiteSpace(ns))
        {
            return new WorkloadStatus
            {
                Phase = WorkloadProviderPhase.Unknown,
                ProviderReady = false,
                Message = "workloads namespace not configured",
            };
        }

        KubernetesWorkloadRaw? raw;
        try
        {
            var client = await _clientFactory.CreateClientAsync(cancellationToken).ConfigureAwait(false);
            raw = await _objectClient
                .ReadWorkloadAsync(client, ns, reference.InstanceSelectorLabels, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "GetStatus of instance {InstanceId} failed; reporting Unknown.", reference.InstanceId);
            raw = null;
        }

        return KubernetesStatusMapper.Map(raw);
    }

    public async Task<IReadOnlyDictionary<WorkloadRef, WorkloadStatus>> GetStatusesAsync(
        IReadOnlyList<WorkloadRef> references, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(references);

        var settings = await ResolveKubernetesSettingsAsync(cancellationToken).ConfigureAwait(false);
        var ns = _namespaceResolver.Resolve(settings);
        if (string.IsNullOrWhiteSpace(ns))
        {
            return AllUnknown(references, "workloads namespace not configured");
        }

        IReadOnlyDictionary<Guid, KubernetesWorkloadRaw> raws;
        try
        {
            var client = await _clientFactory.CreateClientAsync(cancellationToken).ConfigureAwait(false);
            raws = await _objectClient
                .ReadWorkloadsAsync(client, ns, references.Select(r => r.InstanceId).ToList(), cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Fleet GetStatus of {Count} instances failed; reporting Unknown.", references.Count);
            return AllUnknown(references, "kubernetes API server unreachable — workload status is unknown.");
        }

        var statuses = new Dictionary<WorkloadRef, WorkloadStatus>(references.Count);
        foreach (var reference in references)
        {
            statuses[reference] = KubernetesStatusMapper.Map(
                raws.TryGetValue(reference.InstanceId, out var raw) ? raw : null);
        }

        return statuses;
    }

    private static IReadOnlyDictionary<WorkloadRef, WorkloadStatus> AllUnknown(
        IReadOnlyList<WorkloadRef> references, string message)
    {
        var statuses = new Dictionary<WorkloadRef, WorkloadStatus>(references.Count);
        foreach (var reference in references)
        {
            statuses[reference] = new WorkloadStatus
            {
                Phase = WorkloadProviderPhase.Unknown,
                ProviderReady = false,
                Message = message,
            };
        }

        return statuses;
    }

    public async Task<WorkloadPreflightResult> PreflightAsync(WorkloadDesiredSpec spec, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(spec);

        var settings = await ResolveKubernetesSettingsAsync(cancellationToken).ConfigureAwait(false);
        var ns = _namespaceResolver.Resolve(settings);
        if (string.IsNullOrWhiteSpace(ns))
        {
            return new WorkloadPreflightResult
            {
                Checks = new[]
                {
                    PreflightCheck.Fail(
                        "namespace-configured",
                        PreflightSeverity.Error,
                        "The workloads namespace is not configured (Workloads settings → kubernetes.namespace).",
                        "Set the target namespace in the workload-orchestrator settings."),
                },
            };
        }

        KubernetesClusterInspection inspection;
        IKubernetes? client = null;
        try
        {
            client = await _clientFactory.CreateClientAsync(cancellationToken).ConfigureAwait(false);
            inspection = await _clusterInspector.InspectAsync(client, ns, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Preflight could not build the API client; reporting the API server unreachable.");
            inspection = KubernetesClusterInspection.Unreachable(ex.Message);
        }

        var result = _preflightChecker.Check(spec, settings, inspection);

        if (spec.Kind != WorkloadKind.ScanPerNode || client is null || result.HasBlockingError)
        {
            return result;
        }

        try
        {
            var partition = await _objectClient
                .ListReadyNodesAsync(client, spec.NodeSelector, cancellationToken)
                .ConfigureAwait(false);
            return result with
            {
                ReadyNodeNames = partition.Matching,
                IneligibleNodeNames = partition.Excluded,
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Scan-per-node preflight could not enumerate Ready nodes; reporting a blocking error.");
            var checks = result.Checks.Where(c => c.Id != "scan-nodes-readable").ToList();
            checks.Add(PreflightCheck.Fail(
                "scan-nodes-readable",
                PreflightSeverity.Error,
                "Cluster nodes could not be enumerated for the scan-per-node run — the Ready-node read failed (likely a 403 on cluster-scoped nodes). Grant the read-only ClusterRole so nodes can be listed.",
                fixSnippet: "Grant the backend ServiceAccount cluster-scoped read on nodes (bind the optional read-only ClusterRole)."));
            return result with { Checks = checks };
        }
    }

    public async Task<WorkloadLogs> GetRecentLogsAsync(
        WorkloadRef reference,
        int tailLines,
        bool previousContainer,
        CancellationToken cancellationToken = default)
    {
        var settings = await ResolveKubernetesSettingsAsync(cancellationToken).ConfigureAwait(false);
        var ns = _namespaceResolver.Resolve(settings);
        if (string.IsNullOrWhiteSpace(ns))
        {
            return WorkloadLogs.Empty();
        }

        try
        {
            var client = await _clientFactory.CreateClientAsync(cancellationToken).ConfigureAwait(false);
            return await _logReader
                .ReadAsync(client, ns, reference, tailLines, previousContainer, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "GetRecentLogs of instance {InstanceId} could not build the API client; returning empty logs.", reference.InstanceId);
            return WorkloadLogs.Empty();
        }
    }

    private const string NetworkAttachmentGroup = "k8s.cni.cncf.io";
    private const string NetworkAttachmentVersion = "v1";
    private const string NetworkAttachmentPlural = "network-attachment-definitions";

    public async Task<WorkloadAttachmentExistence> GetAttachmentExistenceAsync(
        IReadOnlyList<string> attachments,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(attachments);

        try
        {
            var settings = await ResolveKubernetesSettingsAsync(cancellationToken).ConfigureAwait(false);
            var ns = _namespaceResolver.Resolve(settings);
            if (string.IsNullOrWhiteSpace(ns))
            {
                return WorkloadAttachmentExistence.Unreadable("the workloads namespace is not configured");
            }

            var client = await _clientFactory.CreateClientAsync(cancellationToken).ConfigureAwait(false);
            var listing = await _objectClient
                .ListNetworkAttachmentDefinitionsAsync(client, ns, cancellationToken).ConfigureAwait(false);

            switch (listing.UnavailableReason)
            {
                case null:
                    var listed = new HashSet<string>(listing.Items.Select(i => i.Name), StringComparer.Ordinal);
                    return WorkloadAttachmentExistence.Read(
                        attachments.Where(name => !listed.Contains(name)).ToList());
                case KubernetesNetworkAttachmentUnavailableReasons.NoneInNamespace:
                case KubernetesNetworkAttachmentUnavailableReasons.MultusNotInstalled:
                    return WorkloadAttachmentExistence.Read([.. attachments]);
                default:
                    return WorkloadAttachmentExistence.Unreadable(
                        $"the network attachment definitions could not be listed ({listing.UnavailableReason})");
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex,
                "Could not list the network attachment definitions to check that [{Attachments}] exist.",
                string.Join(", ", attachments));
            return WorkloadAttachmentExistence.Unreadable("the Kubernetes API server could not be asked which attachments exist");
        }
    }

    private async Task<KubernetesProviderSettings> ResolveKubernetesSettingsAsync(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var settingsService = scope.ServiceProvider.GetRequiredService<IWorkloadOrchestratorSettingsService>();
        var response = await settingsService.GetAsync(ct).ConfigureAwait(false);
        return KubernetesProviderSettings.From(response.Settings);
    }

    private async Task<RegistryCredential?> ResolveImagePullFallbackAsync(string image, CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var resolver = scope.ServiceProvider.GetService<IWorkloadImageRegistryCredentialResolver>();
        if (resolver is null)
        {
            return null;
        }

        var host = ResolveImageRegistryHost(image);
        return host is null ? null : await resolver.ResolveAsync(host, ct).ConfigureAwait(false);
    }

    private static string? ResolveImageRegistryHost(string image)
    {
        if (string.IsNullOrWhiteSpace(image))
        {
            return null;
        }

        var firstSlash = image.IndexOf('/', StringComparison.Ordinal);
        if (firstSlash <= 0)
        {
            return null;
        }

        var firstSegment = image[..firstSlash];
        var looksLikeHost = firstSegment.Contains('.', StringComparison.Ordinal)
            || firstSegment.Contains(':', StringComparison.Ordinal)
            || firstSegment.Equals("localhost", StringComparison.OrdinalIgnoreCase);

        return looksLikeHost ? firstSegment : null;
    }

    private static string BuildDockerConfigJson(string registryHost, string username, string password)
    {
        var auth = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes($"{username}:{password}"));
        var config = new
        {
            auths = new System.Collections.Generic.Dictionary<string, object>
            {
                [registryHost] = new { username, password, auth },
            },
        };
        return System.Text.Json.JsonSerializer.Serialize(config);
    }

    private static WorkloadApplyResult? MapNonSuccess(KubernetesApplyOutcome outcome)
    {
        if (outcome.Succeeded)
        {
            return null;
        }

        if (outcome.IsConflict)
        {
            return WorkloadApplyResult.Conflict(
                outcome.ConflictingFieldManager ?? "unknown",
                outcome.Error);
        }

        if (outcome.Unreachable)
        {
            return WorkloadApplyResult.Failed(WorkloadApplyReason.ProviderUnreachable, outcome.Error);
        }

        if (outcome.Rejected)
        {
            return WorkloadApplyResult.Failed(WorkloadApplyReason.Rejected, outcome.Error);
        }

        return WorkloadApplyResult.Failed(WorkloadApplyReason.Error, outcome.Error);
    }
}
