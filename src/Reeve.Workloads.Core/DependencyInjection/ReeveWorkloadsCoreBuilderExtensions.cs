using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Reeve.Pipeline;
using Reeve.Workloads.Abstractions;
using Reeve.Workloads.Abstractions.Domain.DesiredSpec;
using Reeve.Workloads.Abstractions.Network;
using Reeve.Workloads.Abstractions.Providers;
using Reeve.Workloads.Abstractions.Rendering;
using Reeve.Workloads.Abstractions.Update;
using Reeve.Workloads.Core;
using Reeve.Workloads.Core.HealthGate;
using Reeve.Workloads.Core.Groups;
using Reeve.Workloads.Core.Health;
using Reeve.Workloads.Core.Network;
using Reeve.Workloads.Core.Providers;
using Reeve.Workloads.Core.Rendering;
using Reeve.Workloads.Core.Update;
using Reeve.Workloads.Core.Update.Stages;
using Reeve.Workloads.Core.Watch;

namespace Reeve.Workloads.DependencyInjection;

/// <summary>The engine proper: reconcile loop, lifecycle, update state machine, renderers and the options they
/// read. Providers and the EF stores are separate packages and register themselves.</summary>
public static class ReeveWorkloadsCoreBuilderExtensions
{
    /// <summary>Registers the provider-independent engine. Call the provider extensions after it.</summary>
    /// <param name="builder">The builder returned by <c>AddReeveWorkloads</c>.</param>
    public static ReeveWorkloadsBuilder AddEngine(this ReeveWorkloadsBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        return builder
            .AddApplicationServices()
            .AddGroupManifestServices()
            .AddHostBootstrap()
            .AddOrchestratorSettings()
            .AddNetworkAttachments()
            .AddProviderPlumbing()
            .AddArtifactRendering()
            .AddReconciler()
            .AddUpdateFlow()
            .AddDayTwoOperations()
            .AddRetainedVolumes()
            .AddRepairs()
            .AddEngineHosting();
    }

    /// <summary>Whether provider watch sources should be registered at all. Read here rather than in each
    /// provider so one switch answers for all of them.</summary>
    /// <param name="builder">The builder returned by <c>AddReeveWorkloads</c>.</param>
    /// <returns>The configured value, defaulting to true when unset.</returns>
    public static bool WatchEnabled(this ReeveWorkloadsBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        return builder.Configuration.GetValue<bool?>(
            $"{WorkloadReconcilerOptions.SectionName}:{nameof(WorkloadReconcilerOptions.WatchEnabled)}") ?? true;
    }

    private static ReeveWorkloadsBuilder AddApplicationServices(this ReeveWorkloadsBuilder builder)
    {
        builder.TryAdd<IWorkloadStatusRealtimeNotifier, NullWorkloadStatusRealtimeNotifier>(ServiceLifetime.Singleton);
        builder.TryAdd<IWorkloadParameterReferenceResolver, NoWorkloadParameterReferenceResolver>(ServiceLifetime.Singleton);
        builder.Add<UnpinnedReferenceRolloutGuard, UnpinnedReferenceRolloutGuard>(ServiceLifetime.Scoped);
        builder.Add<IWorkloadReferenceRolloutService, WorkloadReferenceRolloutService>(ServiceLifetime.Scoped);

        builder.Services.AddOptions<ProductImageOptions>()
            .Bind(builder.Configuration.GetSection(ProductImageOptions.SectionName));

        builder.Add<IWorkloadHostAddresses, WorkloadHostAddresses>(ServiceLifetime.Singleton);

        builder
            .AddDefault<IWorkloadClock, SystemWorkloadClock>(ServiceLifetime.Singleton);

        builder.AddDefault<IObservedWorkloadVersionReader, NoObservedWorkloadVersionReader>(ServiceLifetime.Singleton);

        return builder.Add<IDesiredSpecResolver, DesiredSpecResolver>(ServiceLifetime.Scoped);
    }

    private static ReeveWorkloadsBuilder AddGroupManifestServices(this ReeveWorkloadsBuilder builder)
    {
        return builder
            .TryAdd<IWorkloadDeploymentContextSource, GroupManifestDeploymentContextSource>(
                sp => new GroupManifestDeploymentContextSource(
                    sp.GetRequiredService<IWorkloadGroupManifestSource>(),
                    sp.GetRequiredService<IWorkloadGroupRepository>()),
                ServiceLifetime.Scoped)
            .TryAdd<IWorkloadReconcileInputsProvider, GroupManifestReconcileInputsProvider>(
                sp => new GroupManifestReconcileInputsProvider(
                    sp.GetRequiredService<IWorkloadGroupManifestSource>(),
                    sp.GetRequiredService<IWorkloadGroupRepository>(),
                    sp.GetRequiredService<IWorkloadOrchestratorSettingsService>(),
                    sp.GetRequiredService<IWorkloadInstallationIdentity>(),
                    sp.GetRequiredService<IWorkloadProviderRegistry>(),
                    sp.GetRequiredService<IWorkloadResourceNamerRegistry>(),
                    sp.GetRequiredService<IWorkloadHostBootstrapSource>(),
                    sp.GetRequiredService<IOptions<ProductImageOptions>>(),
                    sp.GetRequiredService<ILogger<GroupManifestReconcileInputsProvider>>(),
                    sp.GetRequiredService<WorkloadParameterSecretCodec>(),
                    sp.GetRequiredService<IWorkloadParameterReferenceResolver>(),
                    sp.GetRequiredService<IWorkloadInstanceRepository>()),
                ServiceLifetime.Scoped)
            .TryAdd<IWorkloadUpdatePlanner, GroupManifestUpdatePlanner>(
                sp => new GroupManifestUpdatePlanner(
                    sp.GetRequiredService<IWorkloadGroupManifestSource>(),
                    sp.GetRequiredService<IWorkloadGroupRepository>(),
                    sp.GetRequiredService<IWorkloadInstanceRepository>(),
                    sp.GetRequiredService<IWorkloadOrchestratorSettingsService>(),
                    sp.GetRequiredService<IWorkloadInstallationIdentity>(),
                    sp.GetRequiredService<IWorkloadProviderRegistry>(),
                    sp.GetRequiredService<IWorkloadResourceNamerRegistry>(),
                    sp.GetRequiredService<IWorkloadHostBootstrapSource>(),
                    sp.GetRequiredService<IOptions<ProductImageOptions>>(),
                    sp.GetRequiredService<ILogger<GroupManifestUpdatePlanner>>(),
                    sp.GetRequiredService<IRolloutCertificationPolicy>(),
                    sp.GetRequiredService<WorkloadParameterSecretCodec>(),
                    sp.GetRequiredService<IWorkloadParameterReferenceResolver>()),
                ServiceLifetime.Scoped);
    }

    private static ReeveWorkloadsBuilder AddHostBootstrap(this ReeveWorkloadsBuilder builder)
        => builder.AddDefault<IWorkloadHostBootstrapSource, EmptyWorkloadHostBootstrapSource>(ServiceLifetime.Singleton);

    private static ReeveWorkloadsBuilder AddOrchestratorSettings(this ReeveWorkloadsBuilder builder)
    {
        return builder
            .AddSelf<WorkloadParameterSecretCodec>(ServiceLifetime.Scoped)
            .AddSelf<WorkloadOrchestratorSettingsCache>(ServiceLifetime.Singleton)
            .AddSelf<WorkloadOrchestratorSettingsService>(ServiceLifetime.Scoped)
            .Add<IWorkloadOrchestratorSettingsRules, WorkloadOrchestratorSettingsRules>(ServiceLifetime.Singleton)
            .Add<IWorkloadOrchestratorSettingsJudge, WorkloadOrchestratorSettingsJudge>(ServiceLifetime.Scoped)
            .Add<IWorkloadOrchestratorSettingsChanges, WorkloadOrchestratorSettingsChanges>(ServiceLifetime.Singleton)
            .AddDefault<IWorkloadOrchestratorSettingsService, WorkloadOrchestratorSettingsService>(
                sp => sp.GetRequiredService<WorkloadOrchestratorSettingsService>(), ServiceLifetime.Scoped)
            .AddDefault<IWorkloadOrchestratorSecretReader, WorkloadOrchestratorSettingsService>(
                sp => sp.GetRequiredService<WorkloadOrchestratorSettingsService>(), ServiceLifetime.Scoped);
    }

    private static ReeveWorkloadsBuilder AddNetworkAttachments(this ReeveWorkloadsBuilder builder)
        => builder
            .Add<INetworkAttachmentEnumerationService, NetworkAttachmentEnumerationService>(ServiceLifetime.Scoped)
            .Add<INetworkAttachmentEvaluator, NetworkAttachmentEvaluator>(ServiceLifetime.Scoped);

    private static ReeveWorkloadsBuilder AddProviderPlumbing(this ReeveWorkloadsBuilder builder)
        => builder
            .Add<ICompiledProviderCatalog, CompiledProviderCatalog>(ServiceLifetime.Singleton)
            .Add<IWorkloadResourceNamerRegistry, WorkloadResourceNamerRegistry>(ServiceLifetime.Singleton)
            .Add<IWorkloadProviderRegistry, WorkloadProviderRegistry>(ServiceLifetime.Singleton);

    private static ReeveWorkloadsBuilder AddArtifactRendering(this ReeveWorkloadsBuilder builder)
        => builder
            .AddEnumerable<IWorkloadArtifactRenderer, EnvArtifactRenderer>(ServiceLifetime.Singleton)
            .Add<IWorkloadArtifactRendererRegistry, WorkloadArtifactRendererRegistry>(ServiceLifetime.Singleton)
            .Add<IWorkloadArtifactService, WorkloadArtifactService>(ServiceLifetime.Scoped);

    private static ReeveWorkloadsBuilder AddReconciler(this ReeveWorkloadsBuilder builder)
    {
        builder.Services.AddOptions<WorkloadReconcilerOptions>()
            .Bind(builder.Configuration.GetSection(WorkloadReconcilerOptions.SectionName))
            .ValidateDataAnnotations();
        builder.Services.AddOptions<WorkloadWatchOptions>()
            .Bind(builder.Configuration.GetSection(WorkloadWatchOptions.SectionName))
            .ValidateDataAnnotations();
        builder.Services.AddOptions<WorkloadProviderTimeoutOptions>()
            .Bind(builder.Configuration.GetSection(WorkloadProviderTimeoutOptions.SectionName))
            .ValidateDataAnnotations();

        builder
            .Add<IActiveWorkloadIntentProvider, WorkloadActiveIntentProvider>(ServiceLifetime.Scoped)
            .Add<IWorkloadIntentResumer, WorkloadIntentResumer>(ServiceLifetime.Scoped)
            .AddSelf<WorkloadFleetStatusCache>(ServiceLifetime.Singleton)
            .AddSelf<WorkloadGroupGate>(ServiceLifetime.Singleton)
            .AddSelf<WorkloadHealthContributorDispatch>(ServiceLifetime.Scoped)
            .AddSelf<WorkloadReconcileEngine>(ServiceLifetime.Scoped)
            .Forward<IWorkloadReconcileEngine, WorkloadReconcileEngine>(ServiceLifetime.Scoped)
            .AddSelf<WorkloadReconcilerUnit>(ServiceLifetime.Singleton)
            .Forward<IWorkloadEngineUnit, WorkloadReconcilerUnit>(ServiceLifetime.Singleton)
            .Forward<IWorkloadReconcileTrigger, WorkloadReconcilerUnit>(ServiceLifetime.Singleton)
            .Add<IWorkloadReconcileNudge, WorkloadReconcileNudge>(ServiceLifetime.Singleton)
            .AddSelf<WorkloadWatchUnit>(ServiceLifetime.Singleton)
            .Forward<IWorkloadEngineUnit, WorkloadWatchUnit>(ServiceLifetime.Singleton)
            .Forward<IWorkloadWatchedProviderRefresh, WorkloadWatchUnit>(ServiceLifetime.Singleton);

        return builder;
    }

    private static ReeveWorkloadsBuilder AddUpdateFlow(this ReeveWorkloadsBuilder builder)
    {
        builder.Services.TryAddSingleton(
            builder.Configuration.GetSection(WorkloadUpdateOrchestratorOptions.SectionName).Get<WorkloadUpdateOrchestratorOptions>()
            ?? new WorkloadUpdateOrchestratorOptions());
        builder.Services.AddOptions<WorkloadUpdateWalkOptions>()
            .Bind(builder.Configuration.GetSection(WorkloadUpdateWalkOptions.SectionName))
            .ValidateDataAnnotations();

        return builder
            .Add<IWorkloadUpdateEffects, WorkloadUpdateEffects>(ServiceLifetime.Scoped)
            .Add<IWorkloadUpdateRowEffects, WorkloadUpdateRowEffects>(ServiceLifetime.Scoped)
            .Add<IWorkloadUpdateReconcileNudge, WorkloadUpdateReconcileNudge>(ServiceLifetime.Scoped)
            .Add<IRolloutCertificationPolicy, RolloutCertificationPolicy>(ServiceLifetime.Scoped)
            .Add<IWorkloadUpdateHealthGate, WorkloadUpdateHealthGateAdapter>(ServiceLifetime.Scoped)
            .Add<IWorkloadUpdateInitiator, WorkloadUpdateInitiator>(ServiceLifetime.Scoped)
            .Add<IWorkloadUpdateCohortInitiator, WorkloadUpdateInitiator>(ServiceLifetime.Scoped)
            .Add<IWorkloadUpdateReadService, WorkloadUpdateReadService>(ServiceLifetime.Scoped)
            .Add<IWorkloadUpdateAbandonService, WorkloadUpdateAbandonService>(ServiceLifetime.Scoped)
            .AddSelf<WorkloadUpdateOrchestrator>(ServiceLifetime.Scoped)
            .AddSelf<WorkloadUpdateWalkUnit>(ServiceLifetime.Singleton)
            .Forward<IWorkloadEngineUnit, WorkloadUpdateWalkUnit>(ServiceLifetime.Singleton)
            .Forward<IWorkloadUpdateWalkTrigger, WorkloadUpdateWalkUnit>(ServiceLifetime.Singleton)
            .AddUpdatePipeline();
    }

    private static ReeveWorkloadsBuilder AddUpdatePipeline(this ReeveWorkloadsBuilder builder)
    {
        builder
            .AddEnumerable<IStage<WorkloadUpdateStageContext>, ParamsStage>(ServiceLifetime.Scoped)
            .AddEnumerable<IStage<WorkloadUpdateStageContext>, SidecarStage>(ServiceLifetime.Scoped)
            .AddEnumerable<IStage<WorkloadUpdateStageContext>, HealthGateStage>(ServiceLifetime.Scoped)
            .AddEnumerable<IStage<WorkloadUpdateStageContext>, OperatorRedeployStage>(ServiceLifetime.Scoped)
            .AddEnumerable<IStage<WorkloadUpdateStageContext>, CommitVersionStage>(ServiceLifetime.Scoped)
            .AddEnumerable<IStage<WorkloadUpdateStageContext>, CommitParamsStage>(ServiceLifetime.Scoped);

        builder.Services.AddScoped(sp =>
            WorkloadUpdatePipeline.Resolve(sp.GetServices<IStage<WorkloadUpdateStageContext>>()));

        return builder;
    }

    private static ReeveWorkloadsBuilder AddDayTwoOperations(this ReeveWorkloadsBuilder builder)
        => builder
            .AddSelf<WorkloadLifecycleService>(ServiceLifetime.Scoped)
            .Forward<IWorkloadLifecycleService, WorkloadLifecycleService>(ServiceLifetime.Scoped)
            .Forward<IWorkloadDroppedWorkloadTeardown, WorkloadLifecycleService>(ServiceLifetime.Scoped)
            .Add<IWorkloadOperationsService, WorkloadOperationsService>(ServiceLifetime.Scoped)
            .Add<IWorkloadResourceLimitsService, WorkloadResourceLimitsService>(ServiceLifetime.Scoped)
            .Add<IWorkloadParameterEditService, WorkloadParameterEditService>(ServiceLifetime.Scoped)
            .Add<IWorkloadRequirementsService, WorkloadRequirementsService>(ServiceLifetime.Scoped)
            .AddSelf<WorkloadDeviceGroupIdWriter>(ServiceLifetime.Scoped)
            .Add<IWorkloadProviderSwitchService, WorkloadProviderSwitchService>(ServiceLifetime.Scoped)
            .Add<IWorkloadManagedOwnershipReleaser, NoOpWorkloadManagedOwnershipReleaser>(ServiceLifetime.Scoped)
            .Add<IWorkloadSwitchTakeoverRenderer, NoOpWorkloadSwitchTakeoverRenderer>(ServiceLifetime.Scoped)
            .Add<IWorkloadSwitchTakeoverRenderer, WorkloadArtifactTakeoverRenderer>(ServiceLifetime.Scoped);

    private static ReeveWorkloadsBuilder AddRetainedVolumes(this ReeveWorkloadsBuilder builder)
    {
        builder.Services.AddOptions<WorkloadRetainedScanOptions>()
            .Bind(builder.Configuration.GetSection(WorkloadRetainedScanOptions.SectionName))
            .ValidateDataAnnotations();

        return builder
            .Add<IRetainedVolumeDeleter, SchemeDispatchingRetainedVolumeDeleter>(ServiceLifetime.Scoped)
            .Add<IRetainedVolumeService, RetainedVolumeService>(ServiceLifetime.Scoped)
            .AddSelf<WorkloadRetainedScanUnit>(ServiceLifetime.Singleton)
            .Forward<IWorkloadEngineUnit, WorkloadRetainedScanUnit>(ServiceLifetime.Singleton);
    }

    private static ReeveWorkloadsBuilder AddRepairs(this ReeveWorkloadsBuilder builder)
    {
        builder.Services.AddOptions<WorkloadRepairsOptions>()
            .Bind(builder.Configuration.GetSection(WorkloadRepairsOptions.SectionName))
            .ValidateDataAnnotations();

        return builder
            .AddSelf<WorkloadRepairsScanner>(ServiceLifetime.Singleton)
            .AddSelf<WorkloadRepairsUnit>(ServiceLifetime.Singleton)
            .Forward<IWorkloadEngineUnit, WorkloadRepairsUnit>(ServiceLifetime.Singleton);
    }

    private static ReeveWorkloadsBuilder AddEngineHosting(this ReeveWorkloadsBuilder builder)
    {
        builder.Services.AddOptions<WorkloadsEngineHostingOptions>()
            .Bind(builder.Configuration.GetSection(WorkloadsEngineHostingOptions.SectionName))
            .ValidateDataAnnotations();

        builder
            .AddDefault<IWorkloadLeaderElection, SingleReplicaLeaderElection>(ServiceLifetime.Singleton)
            .AddSelf<WorkloadsEngineRunner>(ServiceLifetime.Singleton)
            .AddSelf<WorkloadsEngineStartSignal>(ServiceLifetime.Singleton)
            .AddSelf<WorkloadsEngineLifecycle>(ServiceLifetime.Singleton);

        builder.Services.AddHostedService(sp => sp.GetRequiredService<WorkloadsEngineLifecycle>());

        return builder;
    }
}
