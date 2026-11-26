using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Reeve.Workloads.Abstractions;
using Reeve.Workloads.Abstractions.Contracts;
using Reeve.Workloads.Abstractions.Contracts.Deployment;
using Reeve.Workloads.Abstractions.Domain;
using Reeve.Workloads.Abstractions.Domain.DesiredSpec;
using Reeve.Workloads.Abstractions.Domain.Entities;

using Reeve.Workloads.Abstractions.Providers;
using Reeve.Workloads.Core.Bootstrap;
using Reeve.Workloads.Core;
using Reeve.Workloads.Domain;

namespace Reeve.Workloads.Core.Groups;


/// <summary>The reconcile inputs, assembled from the manifest source and the module row. Both render paths read the
/// bootstrap map from here.</summary>
public sealed class GroupManifestReconcileInputsProvider : IWorkloadReconcileInputsProvider
{
    /// <summary>The stable sentinel MODULE version for a bundled module whose product image carries no
    /// configured tag. Any stable value is coherent: it rides through as REEVE_APP_VERSION, which is
    /// hash-excluded.</summary>
    public const string BundledGroupVersion = "0.0.0-bundled";

    private readonly IWorkloadGroupManifestSource manifests;
    private readonly IWorkloadGroupRepository groups;
    private readonly IWorkloadOrchestratorSettingsService settingsService;
    private readonly IWorkloadInstallationIdentity installationIdentity;
    private readonly IWorkloadProviderRegistry providerRegistry;
    private readonly IWorkloadResourceNamerRegistry namers;
    private readonly IWorkloadHostBootstrapSource hostBootstrap;
    private readonly ProductImageOptions productImages;
    private readonly WorkloadParameterSecretCodec parameterSecrets;
    private readonly IWorkloadParameterReferenceResolver parameterReferenceResolver;
    private readonly IWorkloadInstanceRepository instances;
    private readonly ILogger<GroupManifestReconcileInputsProvider> logger;

    /// <summary>Builds the provider. <paramref name="instances"/> answers which provider the instance's group is
    /// already bound to, which an instance that is not bound yet joins.</summary>
    public GroupManifestReconcileInputsProvider(
        IWorkloadGroupManifestSource manifests,
        IWorkloadGroupRepository groups,
        IWorkloadOrchestratorSettingsService settingsService,
        IWorkloadInstallationIdentity installationIdentity,
        IWorkloadProviderRegistry providerRegistry,
        IWorkloadResourceNamerRegistry namers,
        IWorkloadHostBootstrapSource hostBootstrap,
        IOptions<ProductImageOptions> productImages,
        ILogger<GroupManifestReconcileInputsProvider> logger,
        WorkloadParameterSecretCodec parameterSecrets,
        IWorkloadParameterReferenceResolver parameterReferenceResolver,
        IWorkloadInstanceRepository instances)
    {
        this.instances = instances ?? throw new ArgumentNullException(nameof(instances));
        this.manifests = manifests ?? throw new ArgumentNullException(nameof(manifests));
        this.groups = groups ?? throw new ArgumentNullException(nameof(groups));
        this.settingsService = settingsService ?? throw new ArgumentNullException(nameof(settingsService));
        this.installationIdentity = installationIdentity ?? throw new ArgumentNullException(nameof(installationIdentity));
        this.providerRegistry = providerRegistry ?? throw new ArgumentNullException(nameof(providerRegistry));
        this.namers = namers ?? throw new ArgumentNullException(nameof(namers));
        this.hostBootstrap = hostBootstrap ?? throw new ArgumentNullException(nameof(hostBootstrap));
        ArgumentNullException.ThrowIfNull(productImages);
        this.productImages = productImages.Value;
        this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
        this.parameterSecrets = parameterSecrets ?? throw new ArgumentNullException(nameof(parameterSecrets));
        this.parameterReferenceResolver = parameterReferenceResolver ?? throw new ArgumentNullException(nameof(parameterReferenceResolver));
    }

    /// <inheritdoc />
    public async Task<WorkloadReconcileInputsResult> GetInputsAsync(
        WorkloadInstance instance, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(instance);

        var read = await InstalledGroupManifestRead.ReadAsync(manifests, groups, instance.GroupId, cancellationToken)
            .ConfigureAwait(false);
        var deployment = ForTheApplyPath(read);
        var workload = deployment?.Workloads
            .FirstOrDefault(candidate => string.Equals(candidate.Id, instance.WorkloadId, StringComparison.Ordinal));
        if (workload is null)
        {
            logger.LogDebug(
                "No resolvable declared workload '{WorkloadId}' for module '{GroupId}' at installed version "
                + "'{InstalledVersion}' — instance {InstanceId} treated as no-op/HOLD (no fallback to a newer manifest).",
                instance.WorkloadId, instance.GroupId, read.InstalledVersion ?? "(unknown)", instance.Id);
            return WorkloadReconcileInputsResult.NotDeclared;
        }

        var installedGroupVersion = read.InstalledVersion ?? BundledVersionFor(workload);

        var settings = await settingsService.GetAsync(cancellationToken).ConfigureAwait(false);
        var installationId = await installationIdentity.GetInstallationIdAsync(cancellationToken).ConfigureAwait(false);
        var (chosen, conflict) = instance.Provider is { } bound
            ? (bound, null)
            : await ProviderForUnboundAsync(instance, settings.EffectiveProvider, cancellationToken).ConfigureAwait(false);
        if (conflict is not null)
        {
            return WorkloadReconcileInputsResult.ProviderBindingConflict(conflict);
        }

        var provider = chosen;
        var namer = namers.For(provider);
        var request = WorkloadHostBootstrapStep.Request(
            instance.Id, instance.GroupId, provider, namer, workload, WorkloadBootstrapAudience.Runtime);
        var composed = await WorkloadHostBootstrapStep.ComposeAsync(hostBootstrap, request, workload, cancellationToken)
            .ConfigureAwait(false);
        if (composed.Refusal is { } refusal)
        {
            return WorkloadReconcileInputsResult.HostRefused(refusal);
        }

        return WorkloadReconcileInputsResult.Declared(new WorkloadReconcileInputs
        {
            WorkloadManifest = workload,
            ResolvedParameters = instance.GetResolvedParameters(),
            ParameterReferencesForHash = new Dictionary<string, string?>(),
            Settings = settings.Settings,
            ProductImages = productImages,
            Bootstrap = WorkloadHostBootstrapStep.Context(request, composed) with
            {
                RestartNonce = instance.RestartGeneration,
                AppVersion = installedGroupVersion,
            },
            EffectiveProvider = providerRegistry.Describe(provider),
            Namer = namer,
            InstallationId = installationId,
            InstalledGroupVersion = installedGroupVersion,
        });
    }

    private async Task<(WorkloadProviderId Provider, string? Conflict)> ProviderForUnboundAsync(
        WorkloadInstance instance, WorkloadProviderId effectiveProvider, CancellationToken cancellationToken)
    {
        var rows = await instances.ListByPluginInstanceAsync(instance.GroupInstanceId, cancellationToken).ConfigureAwait(false);
        var bound = rows
            .Select(row => row.Provider)
            .OfType<WorkloadProviderId>()
            .Distinct()
            .ToList();
        return bound.Count switch
        {
            0 => (effectiveProvider, null),
            1 => (bound[0], null),
            _ => (effectiveProvider,
                $"Module instance {instance.GroupInstanceId} has workloads bound to different providers "
                + $"({string.Join(", ", bound.Order())}); workload {instance.WorkloadId} is not bound yet and cannot "
                + "be given one. Uninstall and reinstall the module to deploy all its workloads on one provider."),
        };
    }

    internal static DeploymentManifest? ForTheApplyPath(InstalledGroupManifestRead read)
    {
        if (read.HighestAvailable is null)
        {
            return read.Refused ? null : read.Bundled;
        }

        return read.InstalledVersion is not null ? read.AtInstalledVersion : null;
    }

    private string BundledVersionFor(WorkloadManifest workload)
    {
        var component = workload.Image.Component;
        if (!string.IsNullOrWhiteSpace(component)
            && productImages.Get(component)?.Tag is { } tag
            && !string.IsNullOrWhiteSpace(tag))
        {
            return tag.Trim();
        }

        return BundledGroupVersion;
    }
}
