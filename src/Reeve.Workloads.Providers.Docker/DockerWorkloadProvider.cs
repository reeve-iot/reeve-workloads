using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Docker.DotNet;
using Docker.DotNet.Models;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Reeve.Workloads.Abstractions;
using Reeve.Workloads.Abstractions.Contracts;
using Reeve.Workloads.Abstractions.Domain.Entities;
using Reeve.Workloads.Abstractions.Domain.DesiredSpec;
using Reeve.Workloads.Abstractions.Domain.Network;
using Reeve.Workloads.Abstractions.Providers;
using Reeve.Workloads.Domain.DesiredSpec;

namespace Reeve.Workloads.Providers.Docker;

internal sealed class DockerWorkloadProvider : IWorkloadProvider
{
    private readonly IDockerClientFactory _clientFactory;
    private readonly IDockerImagePuller _imagePuller;
    private readonly IDockerNetworkManager _networkManager;
    private readonly IDockerVolumeManager _volumeManager;
    private readonly IDockerContainerManager _containerManager;
    private readonly IDockerPrivilegeRenderer _privilegeRenderer;
    private readonly IDockerResourceRenderer _resourceRenderer;
    private readonly IDockerDeploymentValidator _deploymentValidator;
    private readonly IDockerDaemonGate _daemonGate;
    private readonly IDockerJournaledRecreate _journaledRecreate;
    private readonly IDockerLogReader _logReader;
    private readonly IDockerRollbackCoordinator _rollbackCoordinator;
    private readonly IDockerStatusMapper _statusMapper;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<DockerWorkloadProvider> _logger;

    public DockerWorkloadProvider(
        IDockerClientFactory clientFactory,
        IDockerImagePuller imagePuller,
        IDockerNetworkManager networkManager,
        IDockerVolumeManager volumeManager,
        IDockerContainerManager containerManager,
        IDockerPrivilegeRenderer privilegeRenderer,
        IDockerResourceRenderer resourceRenderer,
        IDockerDeploymentValidator deploymentValidator,
        IDockerDaemonGate daemonGate,
        IDockerJournaledRecreate journaledRecreate,
        IDockerLogReader logReader,
        IDockerRollbackCoordinator rollbackCoordinator,
        IDockerStatusMapper statusMapper,
        IServiceScopeFactory scopeFactory,
        ILogger<DockerWorkloadProvider> logger)
    {
        _clientFactory = clientFactory ?? throw new ArgumentNullException(nameof(clientFactory));
        _imagePuller = imagePuller ?? throw new ArgumentNullException(nameof(imagePuller));
        _networkManager = networkManager ?? throw new ArgumentNullException(nameof(networkManager));
        _volumeManager = volumeManager ?? throw new ArgumentNullException(nameof(volumeManager));
        _containerManager = containerManager ?? throw new ArgumentNullException(nameof(containerManager));
        _privilegeRenderer = privilegeRenderer ?? throw new ArgumentNullException(nameof(privilegeRenderer));
        _resourceRenderer = resourceRenderer ?? throw new ArgumentNullException(nameof(resourceRenderer));
        _deploymentValidator = deploymentValidator ?? throw new ArgumentNullException(nameof(deploymentValidator));
        _daemonGate = daemonGate ?? throw new ArgumentNullException(nameof(daemonGate));
        _journaledRecreate = journaledRecreate ?? throw new ArgumentNullException(nameof(journaledRecreate));
        _logReader = logReader ?? throw new ArgumentNullException(nameof(logReader));
        _rollbackCoordinator = rollbackCoordinator ?? throw new ArgumentNullException(nameof(rollbackCoordinator));
        _statusMapper = statusMapper ?? throw new ArgumentNullException(nameof(statusMapper));
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public WorkloadProviderDescriptor Descriptor => DockerProvider.Descriptor;

    public async Task<WorkloadApplyResult> ApplyAsync(
        WorkloadDesiredSpec spec,
        IProgress<WorkloadApplyProgress>? progress,
        CancellationToken cancellationToken = default,
        bool forceConflicts = false)
    {
        ArgumentNullException.ThrowIfNull(spec);

        var settings = await ResolveDockerSettingsAsync(cancellationToken).ConfigureAwait(false);

        if (FirstBlockingReason(_deploymentValidator.Validate(spec)) is { } staticReject)
        {
            return WorkloadApplyResult.Failed(WorkloadApplyReason.Rejected, staticReject);
        }

        var credentials = WorkloadSpecCredentials.Of(spec);
        if (credentials.Refusal is { } credentialReject)
        {
            _logger.LogError("Apply of instance {InstanceId} refused: {Reason}", spec.InstanceId, credentialReject);
            return WorkloadApplyResult.Failed(WorkloadApplyReason.Rejected, credentialReject);
        }

        IDockerClient client;
        try
        {
            client = _clientFactory.CreateClient(settings.SocketUri);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Apply of instance {InstanceId} could not build the docker client.", spec.InstanceId);
            return WorkloadApplyResult.Failed(WorkloadApplyReason.ProviderUnreachable, "could not reach the docker daemon");
        }

        var reference = WorkloadRef.FromDesiredSpec(spec);

        try
        {
            if (FirstBlockingReason(await _daemonGate.EvaluateAsync(client, spec, cancellationToken).ConfigureAwait(false)) is { } gateReject)
            {
                return WorkloadApplyResult.Failed(WorkloadApplyReason.Rejected, gateReject);
            }

            IProgress<DockerPullProgress>? pullProgress = progress is null
                ? null
                : new RelayProgress<DockerPullProgress>(p =>
                    progress.Report(new WorkloadApplyProgress { Percent = p.Percent, Message = p.Message }));

            var pullSettings = await AugmentWithFallbackCredentialAsync(settings, spec.Image, cancellationToken).ConfigureAwait(false);
            var pull = await _imagePuller.PullAsync(client, spec.Image, pullSettings, pullProgress, cancellationToken).ConfigureAwait(false);
            if (!pull.Succeeded)
            {
                return MapApplyFailure(pull.FailureKind, pull.Message);
            }

            var network = await _networkManager.EnsureSharedNetworkAsync(client, settings, cancellationToken).ConfigureAwait(false);
            if (!network.Succeeded || string.IsNullOrWhiteSpace(network.NetworkName))
            {
                return MapApplyFailure(network.FailureKind, network.Message ?? "could not ensure the shared docker network");
            }

            var volumes = await _volumeManager.EnsureVolumesAsync(client, spec, settings, cancellationToken).ConfigureAwait(false);
            if (!volumes.Succeeded)
            {
                return MapApplyFailure(volumes.FailureKind, volumes.Message);
            }

            var privilege = _privilegeRenderer.RenderPrivileges(spec);
            var resource = _resourceRenderer.RenderResources(spec);
            var networking = _networkManager.RenderNetworking(spec, network.NetworkName);

            var current = await _containerManager.FindContainerAsync(client, reference, cancellationToken).ConfigureAwait(false);
            var held = current is null
                ? new Dictionary<string, string>(StringComparer.Ordinal)
                : await _containerManager.ReadHeldEnvAsync(client, current, credentials.Names, cancellationToken).ConfigureAwait(false);
            IReadOnlyDictionary<string, string> resolved = new Dictionary<string, string>(StringComparer.Ordinal);
            if (credentials.Credentials.Count > 0)
            {
                using var scope = _scopeFactory.CreateScope();
                resolved = await credentials
                    .ResolveAsync(scope.ServiceProvider.GetRequiredService<IWorkloadCredentialPort>(), spec.InstanceId, held, cancellationToken)
                    .ConfigureAwait(false);
            }

            var desired = _containerManager.BuildContainerParameters(new DockerContainerRenderContext
            {
                Spec = spec,
                ContainerName = spec.ResourceName,
                Credentials = credentials.Credentials.ToDictionary(
                    c => c.CredentialId, c => resolved.GetValueOrDefault(c.CredentialId, string.Empty), StringComparer.Ordinal),
                SharedNetworkName = network.NetworkName,
                Privilege = privilege,
                Resource = resource,
                NetworkHostConfig = networking.HostConfig,
            });

            if (current is null)
            {
                var created = await DockerAttachmentSequence
                    .CreateConnectAndStartAsync(_containerManager, client, desired, networking.Attachments, cancellationToken)
                    .ConfigureAwait(false);
                if (created.Succeeded)
                {
                    return WorkloadApplyResult.Applied(spec.SpecHash);
                }

                if (created.Container is not null)
                {
                    var rollback = await BestEffortRollbackAsync(client, spec, created.Container, previous: null, cancellationToken).ConfigureAwait(false);
                    return MapApplyFailure(created.FailureKind, DockerJournaledRecreate.WithRollback(created.Message, rollback));
                }

                return MapApplyFailure(created.FailureKind, created.Message);
            }

            return await _journaledRecreate
                .RecreateAsync(client, spec, current, desired, _rollbackCoordinator, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is WorkloadResourceRenderException or WorkloadSecretMaterialMissingException)
        {
            _logger.LogError(ex, "Apply of instance {InstanceId} refused: {Reason}", spec.InstanceId, ex.Message);
            return WorkloadApplyResult.Failed(WorkloadApplyReason.Rejected, ex.Message);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Apply of instance {InstanceId} failed against the docker daemon.", spec.InstanceId);
            return WorkloadApplyResult.Failed(WorkloadApplyReason.ProviderUnreachable, "the docker daemon call failed");
        }
    }

    public async Task TeardownAsync(WorkloadRef reference, bool retainVolumes, CancellationToken cancellationToken = default)
    {
        var settings = await ResolveDockerSettingsAsync(cancellationToken).ConfigureAwait(false);

        var client = _clientFactory.CreateClient(settings.SocketUri);

        var owned = await _containerManager.FindContainersAsync(client, reference, cancellationToken).ConfigureAwait(false);
        foreach (var container in owned)
        {
            await _containerManager.StopContainerAsync(client, container.Id, cancellationToken).ConfigureAwait(false);
            var removed = await _containerManager.RemoveContainerAsync(client, container.Id, cancellationToken).ConfigureAwait(false);
            ThrowIfTeardownStepFailed(removed, $"remove container '{container.Name}'", reference);
        }

        if (!retainVolumes)
        {
            var volumesRemoved = await _volumeManager.RemoveVolumesAsync(client, reference, cancellationToken).ConfigureAwait(false);
            ThrowIfTeardownStepFailed(volumesRemoved, "remove volumes", reference);
        }
    }

    private static void ThrowIfTeardownStepFailed(DockerOperationResult result, string step, WorkloadRef reference)
    {
        if (!result.Succeeded)
        {
            throw new InvalidOperationException(
                $"Teardown of instance {reference.InstanceId} could not {step}: {result.Message}");
        }
    }

    public Task SuspendAsync(WorkloadRef reference, CancellationToken cancellationToken = default)
        => SetRunningAsync(reference, run: false, cancellationToken);

    public Task ResumeAsync(WorkloadRef reference, CancellationToken cancellationToken = default)
        => SetRunningAsync(reference, run: true, cancellationToken);

    private async Task SetRunningAsync(WorkloadRef reference, bool run, CancellationToken cancellationToken)
    {
        var settings = await ResolveDockerSettingsAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            var client = _clientFactory.CreateClient(settings.SocketUri);

            var current = await _containerManager.FindContainerAsync(client, reference, cancellationToken).ConfigureAwait(false);
            if (current is null)
            {
                _logger.LogDebug("{Op} of instance {InstanceId}: no container found; no-op.",
                    run ? "Resume" : "Suspend", reference.InstanceId);
                return;
            }

            if (run)
            {
                await _containerManager.StartContainerAsync(client, current.Id, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await _containerManager.StopContainerAsync(client, current.Id, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "{Op} of instance {InstanceId} failed (will retry).",
                run ? "Resume" : "Suspend", reference.InstanceId);
        }
    }

    public async Task<WorkloadStatus> GetStatusAsync(WorkloadRef reference, CancellationToken cancellationToken = default)
    {
        var settings = await ResolveDockerSettingsAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            var client = _clientFactory.CreateClient(settings.SocketUri);
            return await _statusMapper.GetStatusAsync(client, reference, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "GetStatus of instance {InstanceId} failed; reporting Unknown.", reference.InstanceId);
            return new WorkloadStatus
            {
                Phase = WorkloadProviderPhase.Unknown,
                ProviderReady = false,
                Message = "docker daemon unreachable — workload status is unknown.",
            };
        }
    }

    public async Task<IReadOnlyDictionary<WorkloadRef, WorkloadStatus>> GetStatusesAsync(
        IReadOnlyList<WorkloadRef> references, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(references);

        var settings = await ResolveDockerSettingsAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            var client = _clientFactory.CreateClient(settings.SocketUri);
            return await _statusMapper.GetStatusesAsync(client, references, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Fleet GetStatus of {Count} instances failed; reporting Unknown.", references.Count);

            var unreachable = new Dictionary<WorkloadRef, WorkloadStatus>(references.Count);
            foreach (var reference in references)
            {
                unreachable[reference] = new WorkloadStatus
                {
                    Phase = WorkloadProviderPhase.Unknown,
                    ProviderReady = false,
                    Message = "docker daemon unreachable — workload status is unknown.",
                };
            }

            return unreachable;
        }
    }

    public async Task<WorkloadPreflightResult> PreflightAsync(WorkloadDesiredSpec spec, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(spec);

        var settings = await ResolveDockerSettingsAsync(cancellationToken).ConfigureAwait(false);

        var checks = new List<PreflightCheck>(_deploymentValidator.Validate(spec));

        checks.AddRange(DockerPreflight.SpecDispositionChecks(spec));

        IDockerClient client;
        try
        {
            client = _clientFactory.CreateClient(settings.SocketUri);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Preflight could not build the docker client; reporting the daemon unreachable.");
            checks.Add(PreflightCheck.Fail(
                "docker-daemon-reachable",
                PreflightSeverity.Error,
                "The docker daemon could not be reached (check the configured socketUri).",
                "Verify the docker socket path / DOCKER_HOST and that the daemon is running."));
            return new WorkloadPreflightResult { Checks = checks };
        }

        try
        {
            checks.AddRange(await _daemonGate.EvaluateAsync(client, spec, cancellationToken).ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Preflight daemon checks failed; reporting the daemon unreachable.");
            checks.Add(PreflightCheck.Fail(
                "docker-daemon-reachable",
                PreflightSeverity.Error,
                "The docker daemon preflight checks could not complete (the daemon call failed).",
                "Verify the docker socket path / DOCKER_HOST and that the daemon is running."));
        }

        return new WorkloadPreflightResult { Checks = checks };
    }

    public async Task<WorkloadLogs> GetRecentLogsAsync(
        WorkloadRef reference,
        int tailLines,
        bool previousContainer,
        CancellationToken cancellationToken = default)
    {
        var settings = await ResolveDockerSettingsAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            var client = _clientFactory.CreateClient(settings.SocketUri);
            return await _logReader
                .ReadAsync(client, reference, tailLines, previousContainer, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "GetRecentLogs of instance {InstanceId} could not read logs; returning empty.", reference.InstanceId);
            return WorkloadLogs.Empty();
        }
    }

    public async Task<WorkloadAttachmentExistence> GetAttachmentExistenceAsync(
        IReadOnlyList<string> attachments,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(attachments);

        try
        {
            var settings = await ResolveDockerSettingsAsync(cancellationToken).ConfigureAwait(false);
            var client = _clientFactory.CreateClient(settings.SocketUri);
            var networks = await client.Networks
                .ListNetworksAsync(new NetworksListParameters(), cancellationToken)
                .ConfigureAwait(false);

            var listed = new HashSet<string>(
                (networks ?? []).Where(n => n is not null && !string.IsNullOrEmpty(n.Name)).Select(n => n.Name),
                StringComparer.Ordinal);
            return WorkloadAttachmentExistence.Read(
                attachments.Where(name => !listed.Contains(name)).ToList());
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex,
                "Could not list the docker networks to check that the attachment(s) [{Attachments}] exist.",
                string.Join(", ", attachments));
            return WorkloadAttachmentExistence.Unreadable(
                "the docker daemon could not be asked which networks exist");
        }
    }

    private async Task<DockerProviderSettings> ResolveDockerSettingsAsync(CancellationToken ct)
        => DockerProviderSettings.From(await ResolveSettingsAsync(ct).ConfigureAwait(false));

    private async Task<DockerProviderSettings> AugmentWithFallbackCredentialAsync(
        DockerProviderSettings settings, string image, CancellationToken ct)
    {
        string imageHost;
        try
        {
            imageHost = DockerRegistryAuth.ResolveRegistryHost(image);
        }
        catch (ArgumentException)
        {
            return settings;
        }

        if (DockerRegistryAuth.MatchCredential(settings.RegistryCredentials, imageHost) is not null)
        {
            return settings;
        }

        using var scope = _scopeFactory.CreateScope();
        var resolver = scope.ServiceProvider.GetService<IWorkloadImageRegistryCredentialResolver>();
        if (resolver is null)
        {
            return settings;
        }

        var fallback = await resolver.ResolveAsync(imageHost, ct).ConfigureAwait(false);
        if (fallback is null)
        {
            return settings;
        }

        return settings with { RegistryCredentials = [.. settings.RegistryCredentials, fallback] };
    }

    private async Task<WorkloadOrchestratorSettings> ResolveSettingsAsync(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var settingsService = scope.ServiceProvider.GetRequiredService<IWorkloadOrchestratorSecretReader>();
        return await settingsService.GetDecryptedSettingsAsync(ct).ConfigureAwait(false);
    }

    private async Task<DockerRollbackResult?> BestEffortRollbackAsync(
        IDockerClient client, WorkloadDesiredSpec spec, DockerContainerRef failedCandidate, DockerContainerRef? previous, CancellationToken ct)
    {
        try
        {
            return await _rollbackCoordinator.RollbackAsync(client, spec, failedCandidate, previous, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Rollback of failed candidate for instance {InstanceId} did not complete.", spec.InstanceId);
            return null;
        }
    }

    private static string? FirstBlockingReason(IReadOnlyList<PreflightCheck> checks)
    {
        var blocking = checks.Where(c => !c.Passed && c.Severity == PreflightSeverity.Error).Select(c => c.Message).ToList();
        return blocking.Count == 0 ? null : string.Join("; ", blocking);
    }

    private static WorkloadApplyResult MapApplyFailure(DockerFailureKind kind, string? message) => kind switch
    {
        DockerFailureKind.Unreachable => WorkloadApplyResult.Failed(WorkloadApplyReason.ProviderUnreachable, message),
        DockerFailureKind.Rejected => WorkloadApplyResult.Failed(WorkloadApplyReason.Rejected, message),
        _ => WorkloadApplyResult.Failed(WorkloadApplyReason.Error, message),
    };

    private sealed class RelayProgress<T>(Action<T> onReport) : IProgress<T>
    {
        private readonly Action<T> onReport = onReport;

        public void Report(T value) => onReport(value);
    }
}
