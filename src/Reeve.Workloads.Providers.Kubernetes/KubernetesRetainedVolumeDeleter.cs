using System;
using System.Threading;
using System.Threading.Tasks;
using k8s;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Reeve.Workloads.Abstractions;
using Reeve.Workloads.Abstractions.Contracts;
using Reeve.Workloads.Abstractions.Domain.Entities;
using Reeve.Workloads.Providers.Kubernetes.Relocate;

namespace Reeve.Workloads.Providers.Kubernetes;

internal sealed class KubernetesRetainedVolumeDeleter : IRetainedVolumeSchemeDeleter
{
    private readonly IKubernetesClientFactory clientFactory;
    private readonly IKubernetesPvcInspector pvcInspector;
    private readonly IServiceScopeFactory scopeFactory;
    private readonly KubernetesNamespaceResolver namespaceResolver;
    private readonly ILogger<KubernetesRetainedVolumeDeleter> logger;

    public KubernetesRetainedVolumeDeleter(
        IKubernetesClientFactory clientFactory,
        IKubernetesPvcInspector pvcInspector,
        IServiceScopeFactory scopeFactory,
        KubernetesNamespaceResolver namespaceResolver,
        ILogger<KubernetesRetainedVolumeDeleter> logger)
    {
        this.clientFactory = clientFactory ?? throw new ArgumentNullException(nameof(clientFactory));
        this.pvcInspector = pvcInspector ?? throw new ArgumentNullException(nameof(pvcInspector));
        this.scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
        this.namespaceResolver = namespaceResolver ?? throw new ArgumentNullException(nameof(namespaceResolver));
        this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public RetainedVolumeSchemeId NamingScheme => RetainedVolumeSchemeId.Kubernetes;

    public async Task<bool> TryDeletePhysicalVolumeAsync(Reeve.Workloads.Abstractions.Domain.Entities.RetainedVolume retainedVolume, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(retainedVolume);

        if (retainedVolume.VolumeNamingScheme != RetainedVolumeSchemeId.Kubernetes)
        {
            logger.LogWarning(
                "Purge of retained volume {ResourceName} refused: its naming scheme is {Scheme}, which the Kubernetes " +
                "deleter cannot address — leaving it retained (fail-closed).",
                retainedVolume.ResourceName, retainedVolume.VolumeNamingScheme);
            return false;
        }

        WorkloadOrchestratorSettingsResponse settingsResponse;
        try
        {
            using var scope = scopeFactory.CreateScope();
            var settingsService = scope.ServiceProvider.GetRequiredService<IWorkloadOrchestratorSettingsService>();
            settingsResponse = await settingsService.GetAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Purge of retained volume {ResourceName} could not read orchestrator settings; leaving it retained for a retry.", retainedVolume.ResourceName);
            return false;
        }

        if (settingsResponse.EffectiveProvider != KubernetesProvider.Id)
        {
            logger.LogWarning(
                "Purge of retained k8s volume {ResourceName} refused: the effective provider is {Provider}, not " +
                "Kubernetes — the PVC cannot be freed here. Leaving it retained (fail-closed); switch the provider back " +
                "or delete the volume manually.",
                retainedVolume.ResourceName, settingsResponse.EffectiveProvider);
            return false;
        }

        var ns = namespaceResolver.Resolve(settingsResponse.Settings);
        if (string.IsNullOrWhiteSpace(ns))
        {
            logger.LogWarning("Purge of retained volume {ResourceName} skipped — workloads namespace not configured.", retainedVolume.ResourceName);
            return false;
        }

        IKubernetes client;
        try
        {
            client = await clientFactory.CreateClientAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Purge of retained volume {ResourceName} could not build the API client; leaving it retained for a retry.", retainedVolume.ResourceName);
            return false;
        }

        return await pvcInspector.DeletePvcAsync(client, ns, retainedVolume.ResourceName, cancellationToken).ConfigureAwait(false);
    }
}
