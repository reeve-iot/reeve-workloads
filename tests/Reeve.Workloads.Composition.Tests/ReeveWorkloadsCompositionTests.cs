using Reeve.Workloads.Domain;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Reeve.Workloads.Abstractions;
using Reeve.Workloads.Abstractions.Contracts;
using Reeve.Workloads.Abstractions.Domain.Entities;
using Reeve.Workloads.Abstractions.Network;
using Reeve.Workloads.Abstractions.Providers;
using Reeve.Workloads.Abstractions.Rendering;
using Reeve.Workloads.Core;
using Reeve.Workloads.Core.Providers;
using Reeve.Workloads.Core.Update;
using Reeve.Workloads.Core.Watch;
using Reeve.Workloads.DependencyInjection;
using Reeve.Workloads.EntityFrameworkCore.Persistence;
using Reeve.Workloads.EntityFrameworkCore.PostgreSql;
using Reeve.Workloads.Providers.Kubernetes;
using Reeve.Workloads.Providers.Manual;
using Reeve.Workloads.Testing;

namespace Reeve.Workloads.Composition.Tests;

public sealed class ReeveWorkloadsCompositionTests
{
    private static readonly string[] ExpectedHostPorts =
    [
        "Microsoft.AspNetCore.DataProtection.IDataProtectionProvider",
        "Reeve.Workloads.Abstractions.IWorkloadGroupManifestSource",
        "Reeve.Workloads.Abstractions.IWorkloadExternalLiveness",
        "Reeve.Workloads.Abstractions.IWorkloadOrchestratorSettingsSource",
    ];

    private static readonly string[] PortsWithALibraryDefault =
    [
        "Reeve.Workloads.Abstractions.IWorkloadActorAccessor",
        "Reeve.Workloads.Abstractions.IWorkloadAuditSink",
        "Reeve.Workloads.Abstractions.IWorkloadCredentialPort",
        "Reeve.Workloads.Abstractions.IWorkloadClusterBus",
        "Reeve.Workloads.Abstractions.IWorkloadLockProvider",
        "Reeve.Workloads.Abstractions.IWorkloadLeaderElection",
        "Reeve.Workloads.Abstractions.IWorkloadDeviceGroupSource",
        "Reeve.Workloads.Abstractions.IWorkloadDiagnosticsAnnouncer",
        "Reeve.Workloads.Abstractions.IWorkloadDiagnosticsSinkFactory",
        "Reeve.Workloads.Abstractions.IWorkloadInstallationIdentity",
        "Reeve.Workloads.Abstractions.Providers.IWorkloadImageRegistryCredentialResolver",
        "Reeve.Workloads.Abstractions.IWorkloadsSchemaMigrator",
        "Reeve.Workloads.Abstractions.IWorkloadClock",
        "Reeve.Workloads.Abstractions.IWorkloadOrchestratorSecretReader",
        "Reeve.Workloads.Abstractions.IWorkloadOrchestratorSettingsService",
        "Reeve.Workloads.Abstractions.IWorkloadHostBootstrapSource",
        "Reeve.Workloads.Core.Update.IObservedWorkloadVersionReader",
    ];

    private static readonly string[] GroupManifestServicePorts =
    [
        "Reeve.Workloads.Abstractions.IWorkloadDeploymentContextSource",
        "Reeve.Workloads.Abstractions.IWorkloadReconcileInputsProvider",
        "Reeve.Workloads.Core.Update.IWorkloadUpdatePlanner",
    ];

    [Fact]
    public void TheAssembledEngine_LeavesExactlyTheDocumentedPortsToTheHost()
    {
        var missing = FullEngine().FindMissingHostPorts();

        missing.Select(m => m.Port.FullName).Should().BeEquivalentTo(ExpectedHostPorts);
    }

    private static readonly string[] DeliberatelyOptionalPorts = [];

    [Fact]
    public void NoUnDeclaredHostPortWouldDegradeSilently()
    {
        FullEngine().FindMissingHostPorts()
            .Where(m => m.EveryRequestIsOptional)
            .Select(m => m.Port.FullName)
            .Should().BeEquivalentTo(DeliberatelyOptionalPorts);
    }

    [Fact]
    public void ValidateHostPorts_RefusesAnEngineTheHostHasNotFinished()
    {
        var act = () => FullEngine().ValidateHostPorts();

        act.Should().Throw<InvalidOperationException>()
            .Which.Message.Should().Contain("IWorkloadGroupManifestSource");
    }

    [Fact]
    public void AHostThatSuppliesTheDataProtectionProvider_NoLongerOwesIt()
    {
        const string Port = "Microsoft.AspNetCore.DataProtection.IDataProtectionProvider";

        Engine(withDataProtection: false).FindMissingHostPorts().Select(m => m.Port.FullName)
            .Should().Contain(Port);
        Engine(withDataProtection: true).FindMissingHostPorts().Select(m => m.Port.FullName)
            .Should().NotContain(Port);

        static ReeveWorkloadsBuilder Engine(bool withDataProtection)
        {
            var services = new ServiceCollection();
            if (withDataProtection)
            {
                services.AddSingleton<IDataProtectionProvider, PassThroughDataProtection>();
            }

            return services
                .AddReeveWorkloads(EmptyConfiguration)
                .AddEngine()
                .AddEntityFrameworkStores(options => options.UseNpgsql(UnopenedConnectionString))
                .AddDockerProvider();
        }
    }

    [Fact]
    public void TheEngineWiresTheReconcileNudgeItself()
    {
        var builder = FullEngine();

        builder.Services.Last(d => d.ServiceType == typeof(IWorkloadReconcileNudge))
            .Should().Match<ServiceDescriptor>(d =>
                d.ImplementationType == typeof(WorkloadReconcileNudge)
                && d.Lifetime == ServiceLifetime.Singleton);
        builder.FindMissingHostPorts().Select(m => m.Port.FullName)
            .Should().NotContain(typeof(IWorkloadReconcileNudge).FullName);
    }

    [Fact]
    public void TheLibraryAnswersExactlyTheDocumentedPorts_AndTheHostStillOwesTheRest()
    {
        var builder = FullEngine();

        builder.FindHostPortsOnLibraryDefaults().Select(p => p.FullName)
            .Should().BeEquivalentTo(PortsWithALibraryDefault);
        builder.FindMissingHostPorts().Select(m => m.Port.FullName)
            .Should().NotIntersectWith(PortsWithALibraryDefault, "a defaulted port is answered, not owed");
    }

    [Fact]
    public void TheGroupManifestServices_AreLibraryRegistrationsNeitherOwedNorDefaulted()
    {
        var builder = FullEngine();

        builder.FindHostPortsOnLibraryDefaults().Select(p => p.FullName)
            .Should().NotIntersectWith(GroupManifestServicePorts);
        builder.FindMissingHostPorts().Select(m => m.Port.FullName)
            .Should().NotIntersectWith(GroupManifestServicePorts);
    }

    [Fact]
    public void TheSingletonManualProvider_ResolvesFromTheRootOverAScopedLivenessPort()
    {
        var builder = FullEngine();
        IServiceCollection services = new ServiceCollection();
        services.AddOptions();
        services.AddLogging();
        services.AddScoped<IWorkloadExternalLiveness, NothingIsLive>();
        services.Add(builder.Services.First(
            d => d.ServiceType == typeof(Reeve.Workloads.Abstractions.Providers.IWorkloadProvider)
                 && d.ImplementationType?.Name == "ManualWorkloadProvider"));

        using var provider = services.BuildServiceProvider(
            new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });

        provider.GetServices<Reeve.Workloads.Abstractions.Providers.IWorkloadProvider>().Should().NotBeEmpty();
    }

    [Fact]
    public void EveryGroupManifestService_ResolvesOnceItsHostPortsAreSupplied()
    {
        var builder = FullEngine();
        IServiceCollection services = new ServiceCollection();
        services.AddOptions();
        services.AddScoped<IWorkloadGroupManifestSource, NoWorkloadGroupManifestSource>();
        foreach (var reached in ReachedByTheGroupManifestServices)
        {
            services.Add(builder.Services.Last(d => d.ServiceType == reached));
        }

        services.AddScoped<IWorkloadOrchestratorSettingsService, DefaultSettingsOnly>();
        services.AddScoped<IWorkloadInstallationIdentity, NoInstallationIdentity>();
        services.AddScoped<IWorkloadInstanceRepository, NoWorkloadInstances>();
        services.AddSingleton<IDataProtectionProvider, PassThroughDataProtection>();
        services.Add(builder.Services.Last(d => d.ServiceType == typeof(WorkloadParameterSecretCodec)));
        services.Add(builder.Services.Last(d => d.ServiceType == typeof(IWorkloadParameterReferenceResolver)));
        services.AddScoped<IWorkloadGroupRepository, NoWorkloadGroups>();
        services.Add(builder.Services.Last(
            d => d.ServiceType == typeof(Reeve.Workloads.Core.HealthGate.IRolloutCertificationPolicy)));
        services.AddLogging();
        foreach (var port in GroupManifestServicePorts)
        {
            services.Add(builder.Services.Last(d => d.ServiceType.FullName == port));
        }

        using var provider = services.BuildServiceProvider(
            new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
        using var scope = provider.CreateScope();

        foreach (var port in GroupManifestServicePorts)
        {
            var type = builder.Services.Last(d => d.ServiceType.FullName == port).ServiceType;
            scope.ServiceProvider.GetRequiredService(type).Should().NotBeNull(port);
        }
    }

    private static readonly Type[] ReachedByTheSettingsDefault =
    [
        typeof(WorkloadOrchestratorSettingsService),
        typeof(IWorkloadOrchestratorSettingsRules),
        typeof(ICompiledProviderCatalog),
        typeof(IWorkloadProviderRegistry),
        typeof(IWorkloadInstanceRepository),
        typeof(IWorkloadStatusRealtimeNotifier),
    ];

    private static readonly Type[] ReachedByTheGroupManifestServices =
    [
        typeof(IWorkloadHostBootstrapSource),
        typeof(IWorkloadProviderRegistry),
        typeof(IWorkloadResourceNamerRegistry),
    ];

    [Fact]
    public void ACertifierAndAContributorTheHostSupplies_AreAskedForThroughTheCollectionsThatTakeThem()
    {
        var builder = new ServiceCollection()
            .AddReeveWorkloads(EmptyConfiguration, ports => ports
                .Use<Reeve.Workloads.Core.HealthGate.IRolloutCertifier, NoEvidenceCertifier>(ServiceLifetime.Scoped)
                .Use<IWorkloadHealthContributor, NoSignalContributor>(ServiceLifetime.Scoped))
            .AddEngine()
            .AddEntityFrameworkStores(options => options.UseNpgsql(UnopenedConnectionString))
            .AddDockerProvider();

        builder.FindSuppliedPortsTheEngineDoesNotAskFor().Should().BeEmpty();
        FullEngine().FindMissingHostPorts().Select(m => m.Port)
            .Should().NotContain([typeof(Reeve.Workloads.Core.HealthGate.IRolloutCertifier), typeof(IWorkloadHealthContributor)],
                "none of either is a whole answer");
    }

    private sealed class NoEvidenceCertifier : Reeve.Workloads.Core.HealthGate.IRolloutCertifier
    {
        public IReadOnlySet<Reeve.Workloads.Abstractions.Contracts.Deployment.WorkloadHealthMode> RequiredModes { get; }
            = new HashSet<Reeve.Workloads.Abstractions.Contracts.Deployment.WorkloadHealthMode>();

        public Task<Reeve.Workloads.Core.HealthGate.RolloutEvidence> CertifyAsync(
            Reeve.Workloads.Core.HealthGate.RolloutCertificationRequest request,
            DateTimeOffset nowUtc,
            CancellationToken cancellationToken = default)
            => Task.FromResult(Reeve.Workloads.Core.HealthGate.RolloutEvidence.NoCandidate);
    }

    private sealed class NoSignalContributor : IWorkloadHealthContributor
    {
        public IReadOnlySet<Reeve.Workloads.Abstractions.Contracts.Deployment.WorkloadHealthMode> ObservedModes { get; }
            = new HashSet<Reeve.Workloads.Abstractions.Contracts.Deployment.WorkloadHealthMode>();

        public Task<WorkloadHealthContribution> ObserveAsync(
            WorkloadHealthObservation observation, CancellationToken cancellationToken = default)
            => Task.FromResult(WorkloadHealthContribution.None);
    }

    private sealed class NothingIsLive : IWorkloadExternalLiveness
    {
        public ValueTask<bool> IsLiveAsync(Guid instanceId, CancellationToken cancellationToken = default)
            => ValueTask.FromResult(false);
    }

    [Fact]
    public void AHostPortRegisteredAFTERTheEntryPoint_WinsOverTheLibraryDefault()
    {
        var builder = FullEngine();
        builder.Services.AddScoped<IWorkloadAuditSink, HostAuditSink>();

        using var provider = builder.Services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        scope.ServiceProvider.GetRequiredService<IWorkloadAuditSink>().Should().BeOfType<HostAuditSink>();
        builder.FindHostPortsOnLibraryDefaults().Should().NotContain(typeof(IWorkloadAuditSink));
    }

    [Fact]
    public void AHostPortRegisteredBEFORETheEntryPoint_AlsoWins()
    {
        var services = new ServiceCollection();
        services.AddScoped<IWorkloadAuditSink, HostAuditSink>();
        var builder = services.AddReeveWorkloads(EmptyConfiguration).AddEngine();

        using var provider = builder.Services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        scope.ServiceProvider.GetRequiredService<IWorkloadAuditSink>().Should().BeOfType<HostAuditSink>();
        builder.FindHostPortsOnLibraryDefaults().Should().NotContain(typeof(IWorkloadAuditSink));
    }

    [Fact]
    public void ThePostgreSqlElection_WinsOverTheCoreDefault_CALLEDAFTERIt()
    {
        var builder = FullEngine().AddPostgreSqlLeaderElection();

        var resolved = builder.Services.Last(d => d.ServiceType == typeof(IWorkloadLeaderElection));
        resolved.ImplementationType.Should().Be(typeof(PostgreSqlAdvisoryLockLeaderElection));
        resolved.Lifetime.Should().Be(ServiceLifetime.Singleton, "the port and the Core default are both singletons");
        builder.FindHostPortsOnLibraryDefaults().Should().NotContain(typeof(IWorkloadLeaderElection),
            "a host with this package is no longer running on the single-replica answer, and the boot audit "
            + "exists to say which behaviours were declined");
    }

    [Fact]
    public void ThePostgreSqlElection_WinsOverTheCoreDefault_CALLEDBEFOREIt()
    {
        var builder = new ServiceCollection()
            .AddReeveWorkloads(EmptyConfiguration)
            .AddPostgreSqlLeaderElection()
            .AddEngine();

        builder.Services.Where(d => d.ServiceType == typeof(IWorkloadLeaderElection))
            .Select(d => d.ImplementationType)
            .Should().ContainSingle(
                "the Core default must not even be registered behind it, or a later Add would resurrect it")
            .Which.Should().Be(typeof(PostgreSqlAdvisoryLockLeaderElection));
    }

    [Fact]
    public void ThePostgreSqlElection_IsConstructibleFromWhatAHostAlreadyHas()
    {
        var builder = FullEngine().AddPostgreSqlLeaderElection();
        IServiceCollection services = new ServiceCollection();
        services.AddOptions();
        services.AddLogging();
        services.Add(builder.Services.Last(d => d.ServiceType == typeof(IWorkloadLeaderElection)));

        using var provider = services.BuildServiceProvider();

        provider.GetRequiredService<IWorkloadLeaderElection>()
            .Should().BeOfType<PostgreSqlAdvisoryLockLeaderElection>();
    }

    [Fact]
    public void ThePostgreSqlElection_RefusesWhatItCannotWorkWithout()
    {
        using var container = new ServiceCollection().BuildServiceProvider();
        var scopes = container.GetRequiredService<IServiceScopeFactory>();
        var options = Options.Create(new WorkloadsEngineHostingOptions());
        var logger = NullLogger<PostgreSqlAdvisoryLockLeaderElection>.Instance;

        FluentActions.Invoking(() => new PostgreSqlAdvisoryLockLeaderElection(null!, options, logger))
            .Should().Throw<ArgumentNullException>();
        FluentActions.Invoking(() => new PostgreSqlAdvisoryLockLeaderElection(scopes, null!, logger))
            .Should().Throw<ArgumentNullException>();
        FluentActions.Invoking(() => new PostgreSqlAdvisoryLockLeaderElection(scopes, options, null!))
            .Should().Throw<ArgumentNullException>();
        FluentActions.Invoking(() => ReeveWorkloadsPostgreSqlBuilderExtensions.AddPostgreSqlLeaderElection(null!))
            .Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void EveryLibraryDefault_IsConstructibleAtTheLifetimeItWasRegisteredAt()
    {
        var builder = FullEngine();
        IServiceCollection services = new ServiceCollection();
        services.AddOptions();
        services.AddSingleton(new WorkloadsStoreOptions());
        services.AddDbContext<WorkloadsDbContext>(options => options.UseNpgsql(UnopenedConnectionString));
        services.AddLogging();
        foreach (var port in builder.FindHostPortsOnLibraryDefaults())
        {
            services.Add(builder.Services.Last(d => d.ServiceType == port));
        }

        foreach (var reached in ReachedByTheSettingsDefault)
        {
            services.Add(builder.Services.Last(d => d.ServiceType == reached));
        }

        services.AddSingleton<IWorkloadOrchestratorSettingsSource>(new InMemoryWorkloadOrchestratorSettingsSource());
        services.AddSingleton<IDataProtectionProvider, PassThroughDataProtection>();

        using var provider = services.BuildServiceProvider(
            new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
        using var scope = provider.CreateScope();

        foreach (var port in builder.FindHostPortsOnLibraryDefaults())
        {
            scope.ServiceProvider.GetRequiredService(port).Should().NotBeNull(port.Name);
        }
    }

    [Fact]
    public void APortAnsweredInTheCallback_WinsOverTheDefaultAndIsRecordedAsSupplied()
    {
        var builder = new ServiceCollection()
            .AddReeveWorkloads(EmptyConfiguration, ports => ports
                .Use<IWorkloadAuditSink, HostAuditSink>(ServiceLifetime.Scoped))
            .AddEngine();

        using var provider = builder.Services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        scope.ServiceProvider.GetRequiredService<IWorkloadAuditSink>().Should().BeOfType<HostAuditSink>();
        builder.SuppliedPorts.Should().Contain(typeof(IWorkloadAuditSink));
        builder.FindHostPortsOnLibraryDefaults().Should().NotContain(typeof(IWorkloadAuditSink));
    }

    [Fact]
    public void TheTwoArgumentOverloadStillExists_AndComposesTheSameEngine()
    {
        var withCallback = new ServiceCollection().AddReeveWorkloads(EmptyConfiguration, _ => { }).AddEngine();
        var without = new ServiceCollection().AddReeveWorkloads(EmptyConfiguration).AddEngine();

        withCallback.FindMissingHostPorts().Select(m => m.Port.FullName)
            .Should().BeEquivalentTo(without.FindMissingHostPorts().Select(m => m.Port.FullName));
        without.SuppliedPorts.Should().BeEmpty();
    }

    [Fact]
    public void UseExisting_PointsThePortAtTheSameInstanceAsTheConcreteType()
    {
        var builder = new ServiceCollection()
            .AddReeveWorkloads(EmptyConfiguration, ports =>
            {
                ports.Services.AddScoped<HostAuditSink>();
                ports.UseExisting<IWorkloadAuditSink, HostAuditSink>(ServiceLifetime.Scoped);
            })
            .AddEngine();

        using var provider = builder.Services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        scope.ServiceProvider.GetRequiredService<IWorkloadAuditSink>()
            .Should().BeSameAs(scope.ServiceProvider.GetRequiredService<HostAuditSink>());
    }

    [Fact]
    public void TryUse_LosesToAnEarlierRegistration_WhereUseWouldOverturnIt()
    {
        var tried = new ServiceCollection();
        tried.AddScoped<IWorkloadAuditSink, HostAuditSink>();
        var triedBuilder = tried.AddReeveWorkloads(EmptyConfiguration, ports => ports
            .TryUse<IWorkloadAuditSink, OtherAuditSink>(ServiceLifetime.Scoped));

        var used = new ServiceCollection();
        used.AddScoped<IWorkloadAuditSink, HostAuditSink>();
        used.AddReeveWorkloads(EmptyConfiguration, ports => ports
            .Use<IWorkloadAuditSink, OtherAuditSink>(ServiceLifetime.Scoped));

        using var triedProvider = triedBuilder.Services.BuildServiceProvider();
        using var triedScope = triedProvider.CreateScope();
        using var usedProvider = used.BuildServiceProvider();
        using var usedScope = usedProvider.CreateScope();

        triedScope.ServiceProvider.GetRequiredService<IWorkloadAuditSink>().Should().BeOfType<HostAuditSink>();
        usedScope.ServiceProvider.GetRequiredService<IWorkloadAuditSink>().Should().BeOfType<OtherAuditSink>();
        triedBuilder.SuppliedPorts.Should().Contain(typeof(IWorkloadAuditSink),
            "a port answered with TryUse is still an answer, whoever ends up winning");
    }

    [Fact]
    public void APortSuppliedThatNothingAsksFor_IsReportedRatherThanResolvedInSilence()
    {
        var builder = new ServiceCollection()
            .AddReeveWorkloads(EmptyConfiguration, ports => ports
                .Use<INothingAsksForThis, NothingAsksForThis>(ServiceLifetime.Singleton))
            .AddEngine();

        builder.FindSuppliedPortsTheEngineDoesNotAskFor().Should().Contain(typeof(INothingAsksForThis));
    }

    [Fact]
    public void EveryPortReeveShapedCompositionSupplies_IsOneTheEngineActuallyAsksFor()
    {
        var builder = new ServiceCollection()
            .AddReeveWorkloads(EmptyConfiguration, ports => ports
                .Use<IWorkloadAuditSink, HostAuditSink>(ServiceLifetime.Scoped))
            .AddEngine()
            .AddEntityFrameworkStores(options => options.UseNpgsql(UnopenedConnectionString))
            .AddDockerProvider()
            .AddKubernetesProvider()
            .AddManualProvider();

        builder.FindSuppliedPortsTheEngineDoesNotAskFor().Should().BeEmpty();
    }

    [Fact]
    public void TheManualProvider_NamesVolumesExactlyAsTheDockerProviderDoes()
    {
        using var provider = FullEngine().Services.BuildServiceProvider();
        var namers = provider.GetRequiredService<IWorkloadResourceNamerRegistry>();
        var subject = new WorkloadNamingSubject { GroupId = "zwave", WorkloadId = "zwave-js", Inst8 = "abcdef12" };

        var manual = namers.For(ManualProvider.Id);
        manual.VolumeName(subject, "state")
            .Should().Be(namers.For(Reeve.Workloads.Providers.Docker.DockerProvider.Id).VolumeName(subject, "state"));
        manual.VolumeScheme.Should().Be(RetainedVolumeSchemeId.Docker);
        manual.ObjectName(subject).Should().Be(DefaultWorkloadResourceNamer.Instance.ObjectName(subject));
    }

    [Fact]
    public async Task TheDefaultCredentialResolver_AnswersExactlyWhatAnUnregisteredPortDid()
    {
        var resolver = new NoWorkloadImageRegistryCredentialResolver();

        (await resolver.ResolveAsync("registry.example.com")).Should().BeNull();
        (await resolver.ResolveAsync("ghcr.io")).Should().BeNull();
    }

    [Fact]
    public void TheClockDefault_ReadsTheHostsTimeProviderWhenThereIsOne()
    {
        var services = new ServiceCollection();
        services.AddSingleton<TimeProvider>(new FrozenTimeProvider(
            new DateTimeOffset(2031, 4, 5, 6, 7, 8, TimeSpan.Zero)));
        var builder = services.AddReeveWorkloads(EmptyConfiguration).AddEngine();

        using var provider = builder.Services.BuildServiceProvider();

        provider.GetRequiredService<IWorkloadClock>().UtcNow
            .Should().Be(new DateTimeOffset(2031, 4, 5, 6, 7, 8, TimeSpan.Zero));
        provider.GetRequiredService<IWorkloadClock>().Should().BeOfType<SystemWorkloadClock>();
    }

    [Fact]
    public void TheClockDefault_FallsBackToRealTimeWithNoHostTimeProvider()
    {
        var builder = new ServiceCollection().AddReeveWorkloads(EmptyConfiguration).AddEngine();

        using var provider = builder.Services.BuildServiceProvider();

        provider.GetRequiredService<IWorkloadClock>().UtcNow
            .Should().BeCloseTo(DateTimeOffset.UtcNow, TimeSpan.FromMinutes(1));
    }

    [Fact]
    public void TheClock_IsStillReportedAsStandingOnALibraryDefault()
    {
        FullEngine().FindHostPortsOnLibraryDefaults().Should().Contain(typeof(IWorkloadClock));
    }

    [Fact]
    public void AHostClockRegisteredBEFORETheEntryPoint_StillBeatsTheFactoryDefault()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IWorkloadClock, HostClock>();

        var builder = services.AddReeveWorkloads(EmptyConfiguration).AddEngine();

        using var provider = builder.Services.BuildServiceProvider();

        provider.GetRequiredService<IWorkloadClock>().Should().BeOfType<HostClock>();
        builder.FindHostPortsOnLibraryDefaults().Should().NotContain(typeof(IWorkloadClock));
    }

    private sealed class FrozenTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class HostClock : IWorkloadClock
    {
        public DateTimeOffset UtcNow => new(2030, 1, 1, 0, 0, 0, TimeSpan.Zero);
    }

    private sealed class HostAuditSink : IWorkloadAuditSink
    {
        public Task WriteAsync(WorkloadAuditEntry entry, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }

    private sealed class OtherAuditSink : IWorkloadAuditSink
    {
        public Task WriteAsync(WorkloadAuditEntry entry, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }

    private interface INothingAsksForThis;

    private sealed class NoWorkloadGroups : IWorkloadGroupRepository
    {
        public Task<Reeve.Workloads.Abstractions.Domain.Entities.WorkloadGroup?> GetAsync(
            string groupId, CancellationToken cancellationToken = default)
            => Task.FromResult<Reeve.Workloads.Abstractions.Domain.Entities.WorkloadGroup?>(null);

        public Task<bool> TryAddAsync(
            Reeve.Workloads.Abstractions.Domain.Entities.WorkloadGroup module, CancellationToken cancellationToken = default)
            => Task.FromResult(false);

        public Task UpdateAsync(
            Reeve.Workloads.Abstractions.Domain.Entities.WorkloadGroup module, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }

    private sealed class NoWorkloadInstances : IWorkloadInstanceRepository
    {
        public Task<Reeve.Workloads.Abstractions.Domain.Entities.WorkloadInstance?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
            => Task.FromResult<Reeve.Workloads.Abstractions.Domain.Entities.WorkloadInstance?>(null);

        public Task<Reeve.Workloads.Abstractions.Domain.Entities.WorkloadInstance?> GetByWorkloadAsync(
            Guid pluginInstanceId, string workloadId, CancellationToken cancellationToken = default)
            => Task.FromResult<Reeve.Workloads.Abstractions.Domain.Entities.WorkloadInstance?>(null);

        public Task<IReadOnlyList<Reeve.Workloads.Abstractions.Domain.Entities.WorkloadInstance>> ListByPluginInstanceAsync(
            Guid pluginInstanceId, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<Reeve.Workloads.Abstractions.Domain.Entities.WorkloadInstance>>([]);

        public Task<IReadOnlyList<Reeve.Workloads.Abstractions.Domain.Entities.WorkloadInstance>> ListByStateAsync(
            Reeve.Workloads.Abstractions.Domain.Entities.WorkloadInstanceState state, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<Reeve.Workloads.Abstractions.Domain.Entities.WorkloadInstance>>([]);

        public Task<IReadOnlyList<Reeve.Workloads.Abstractions.Domain.Entities.WorkloadInstance>> ListAllAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<Reeve.Workloads.Abstractions.Domain.Entities.WorkloadInstance>>([]);

        public Task<IReadOnlyDictionary<Reeve.Workloads.Abstractions.Domain.Entities.WorkloadInstanceState, int>> CountByStateAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyDictionary<Reeve.Workloads.Abstractions.Domain.Entities.WorkloadInstanceState, int>>(
                new Dictionary<Reeve.Workloads.Abstractions.Domain.Entities.WorkloadInstanceState, int>());

        public Task AddAsync(Reeve.Workloads.Abstractions.Domain.Entities.WorkloadInstance instance, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task UpdateAsync(Reeve.Workloads.Abstractions.Domain.Entities.WorkloadInstance instance, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<bool> TrySetRestoreBindingUnresolvedAsync(
            Guid workloadInstanceId, DateTimeOffset nowUtc, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task DeleteAsync(Reeve.Workloads.Abstractions.Domain.Entities.WorkloadInstance instance, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }

    private sealed class DefaultSettingsOnly : IWorkloadOrchestratorSettingsService
    {
        public Task<Reeve.Workloads.Abstractions.Contracts.WorkloadOrchestratorSettingsResponse> GetAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(new Reeve.Workloads.Abstractions.Contracts.WorkloadOrchestratorSettingsResponse
            {
                Settings = new Reeve.Workloads.Abstractions.Contracts.WorkloadOrchestratorSettings(),
                EffectiveProvider = Reeve.Workloads.Providers.Manual.ManualProvider.Id,
                AutoDetected = true,
                Providers = [],
            });

        public Task<Reeve.Workloads.Abstractions.Contracts.WorkloadProviderProbeResult> TestAsync(
            Reeve.Workloads.Abstractions.Contracts.WorkloadProviderId? requestedProvider, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }

    private sealed class NoInstallationIdentity : IWorkloadInstallationIdentity
    {
        public Task<string> GetInstallationIdAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(string.Empty);
    }

    private sealed class PassThroughDataProtection : IDataProtectionProvider, IDataProtector
    {
        public IDataProtector CreateProtector(string purpose) => this;

        public byte[] Protect(byte[] plaintext) => plaintext;

        public byte[] Unprotect(byte[] protectedData) => protectedData;
    }

    private sealed class NothingAsksForThis : INothingAsksForThis;

    [Fact]
    public void EachProviderContributesItsOwnArm_AndNoneSwallowsAnother()
    {
        var services = FullEngine().Services;

        services.Count(d => d.ServiceType == typeof(IWorkloadProvider)).Should().Be(3);
        services.Count(d => d.ServiceType == typeof(IWorkloadProviderProbe)).Should().Be(2);
        services.Count(d => d.ServiceType == typeof(IWorkloadHostAddressContributor)).Should().Be(2);
        services.Count(d => d.ServiceType == typeof(INetworkAttachmentSource)).Should().Be(2);
        services.Count(d => d.ServiceType == typeof(IWorkloadArtifactRenderer)).Should().Be(4);
    }

    [Fact]
    public void CallingAnExtensionTwice_DoesNotDoubleTheProviderArms()
    {
        var services = new ServiceCollection()
            .AddReeveWorkloads(EmptyConfiguration)
            .AddEngine()
            .AddDockerProvider()
            .AddDockerProvider()
            .Services;

        services.Count(d => d.ServiceType == typeof(IWorkloadProvider)).Should().Be(1);
        services.Count(d => d.ServiceType == typeof(IWorkloadArtifactRenderer)).Should().Be(2);
    }

    [Fact]
    public void AProviderPackageNotReferenced_LeavesTheRegistryEmptyRatherThanBroken()
    {
        var services = new ServiceCollection().AddReeveWorkloads(EmptyConfiguration).AddEngine().Services;

        services.Should().Contain(d => d.ServiceType == typeof(IWorkloadProviderRegistry));
        services.Should().NotContain(d => d.ServiceType == typeof(IWorkloadProvider));
    }

    [Fact]
    public void EveryEngineUnitTheEngineSchedules_IsRegisteredExactlyOnce()
    {
        var units = FullEngine().Services
            .Where(d => d.ServiceType == typeof(IWorkloadEngineUnit))
            .Select(ForwardedTypeOf)
            .ToList();

        units.Should().NotBeEmpty().And.OnlyHaveUniqueItems();
    }

    [Fact]
    public void TheWalkTrigger_IsTheWalkUnitTheEngineStarts()
    {
        var trigger = FullEngine().Services.Should()
            .ContainSingle(d => d.ServiceType == typeof(Reeve.Workloads.Abstractions.Update.IWorkloadUpdateWalkTrigger))
            .Subject;

        ForwardedTypeOf(trigger).Should().Be<Reeve.Workloads.Core.Update.WorkloadUpdateWalkUnit>();
        FullEngine().Services.Should().ContainSingle(d =>
            d.ServiceType == typeof(Reeve.Workloads.Core.Update.WorkloadUpdateWalkUnit)
            && d.Lifetime == ServiceLifetime.Singleton);
    }

    [Fact]
    public void TheEngineUnits_AreRegisteredInStartOrder()
    {
        var units = FullEngine().Services
            .Where(d => d.ServiceType == typeof(IWorkloadEngineUnit))
            .Select(ForwardedTypeOf)
            .ToList();

        units.Should().Equal(
            typeof(WorkloadReconcilerUnit),
            typeof(WorkloadWatchUnit),
            typeof(Reeve.Workloads.Core.Update.WorkloadUpdateWalkUnit),
            typeof(WorkloadRetainedScanUnit),
            typeof(WorkloadRepairsUnit));
    }

    [Fact]
    public void TheEngineHostsItself_ThroughOneLifecycleInstance()
    {
        var services = FullEngine().Services;

        var hosted = services.Should().ContainSingle(d => d.ServiceType == typeof(IHostedService)).Subject;
        hosted.ImplementationType.Should().BeNull("a type registration is a second instance; this must resolve the one already in the container");
        ForwardedTypeOf(hosted).Should().Be<WorkloadsEngineLifecycle>();

        foreach (var singleton in new[]
                 {
                     typeof(WorkloadsEngineLifecycle),
                     typeof(WorkloadsEngineRunner),
                     typeof(WorkloadsEngineStartSignal),
                 })
        {
            services.Should().ContainSingle(d => d.ServiceType == singleton)
                .Which.Lifetime.Should().Be(ServiceLifetime.Singleton, singleton.Name);
        }
    }

    private static Type ForwardedTypeOf(ServiceDescriptor descriptor)
    {
        var probe = new AnsweringNothingProvider();
        try
        {
            descriptor.ImplementationFactory!(probe);
        }
        catch (InvalidOperationException)
        {
        }

        return probe.Requested!;
    }

    private sealed class AnsweringNothingProvider : IServiceProvider
    {
        public Type? Requested { get; private set; }

        public object? GetService(Type serviceType)
        {
            Requested = serviceType;
            return null;
        }
    }

    [Fact]
    public void TheRealTakeoverRenderer_WinsOverTheNoOpDefault()
    {
        FullEngine().Services.Last(d => d.ServiceType == typeof(IWorkloadSwitchTakeoverRenderer))
            .ImplementationType.Should().Be<WorkloadArtifactTakeoverRenderer>();
    }

    [Fact]
    public void WatchSources_AreRegisteredByDefault()
        => FullEngine().Services.Count(d => d.ServiceType == typeof(IWorkloadWatchSource)).Should().Be(2);

    [Fact]
    public void WatchSources_DisappearWhenTheSwitchIsOff()
    {
        var configuration = Configuration(("Workloads:Reconciler:WatchEnabled", "false"));

        var services = new ServiceCollection()
            .AddReeveWorkloads(configuration)
            .AddEngine()
            .AddDockerProvider()
            .AddKubernetesProvider()
            .Services;

        services.Should().NotContain(d => d.ServiceType == typeof(IWorkloadWatchSource));
    }

    [Fact]
    public void TheEngineBindsItsOwnOptionsSections()
    {
        var configuration = Configuration(
            ("Workloads:Reconciler:StatusResyncSeconds", "42"),
            ("Workloads:EngineHosting:LeaderRetryInterval", "00:00:01.500"));

        using var provider = new ServiceCollection()
            .AddReeveWorkloads(configuration)
            .AddEngine()
            .Services
            .BuildServiceProvider();

        provider.GetRequiredService<IOptions<WorkloadReconcilerOptions>>()
            .Value.StatusResyncSeconds.Should().Be(42);
        provider.GetRequiredService<IOptions<WorkloadsEngineHostingOptions>>()
            .Value.LeaderRetryInterval.Should().Be(TimeSpan.FromSeconds(1.5));
    }

    [Fact]
    public void EachShippedProviderAnswersHostAddresses_AndManualAnswersNone()
    {
        using var provider = FullEngine().Services.BuildServiceProvider();
        var addresses = provider.GetRequiredService<IWorkloadHostAddresses>();
        var backend = new HostEndpoint("reeve-backend", 8080, "home");

        addresses.AddressOf(Reeve.Workloads.Providers.Docker.DockerProvider.Id, backend).Should().Be("reeve-backend:8080");
        addresses.AddressOf(KubernetesProvider.Id, backend).Should().Be("reeve-backend.home.svc.cluster.local:8080");
        addresses.AddressOf(ManualProvider.Id, backend).Should().BeNull();
    }

    [Fact]
    public void TheManualProvider_RegistersNoCapabilityProbe()
    {
        FullEngine().Services
            .Where(d => d.ServiceType == typeof(IWorkloadProviderProbe))
            .Select(d => d.ImplementationType?.Assembly)
            .Should().NotContain(typeof(ManualProvider).Assembly);
    }

    [Fact]
    public void AnInvalidEngineHostingSection_FailsOnReadRatherThanContendingEveryMillisecond()
    {
        var configuration = Configuration(("Workloads:EngineHosting:LeaderRetryInterval", "00:00:00"));

        using var provider = new ServiceCollection()
            .AddReeveWorkloads(configuration)
            .AddEngine()
            .Services
            .BuildServiceProvider();

        var act = () => provider.GetRequiredService<IOptions<WorkloadsEngineHostingOptions>>().Value;

        act.Should().Throw<OptionsValidationException>();
    }

    [Fact]
    public void AnInvalidOptionsSection_FailsOnReadRatherThanRunningOnADefault()
    {
        var configuration = Configuration(("Workloads:Reconciler:StatusResyncSeconds", "-1"));

        using var provider = new ServiceCollection()
            .AddReeveWorkloads(configuration)
            .AddEngine()
            .Services
            .BuildServiceProvider();

        var act = () => provider.GetRequiredService<IOptions<WorkloadReconcilerOptions>>().Value;

        act.Should().Throw<OptionsValidationException>();
    }

    [Fact]
    public void TheEngineBindsAndValidatesTheUpdateWalkSection()
    {
        using var configured = new ServiceCollection()
            .AddReeveWorkloads(Configuration(("Workloads:UpdateWalk:WorkingTickSeconds", "2")))
            .AddEngine()
            .Services
            .BuildServiceProvider();
        using var zero = new ServiceCollection()
            .AddReeveWorkloads(Configuration(("Workloads:UpdateWalk:WorkingTickSeconds", "0")))
            .AddEngine()
            .Services
            .BuildServiceProvider();

        configured.GetRequiredService<IOptions<Reeve.Workloads.Core.Update.WorkloadUpdateWalkOptions>>()
            .Value.WorkingTickInterval.Should().Be(TimeSpan.FromSeconds(2));
        var act = () => zero.GetRequiredService<IOptions<Reeve.Workloads.Core.Update.WorkloadUpdateWalkOptions>>().Value;
        act.Should().Throw<OptionsValidationException>();
    }

    [Fact]
    public void TheEngineBindsTheUpdateOrchestratorSection()
    {
        using var configured = new ServiceCollection()
            .AddReeveWorkloads(Configuration(
                ("Workloads:UpdateOrchestrator:HealthGateTimeout", "00:15:00"),
                ("Workloads:UpdateOrchestrator:HealthGateMaximumWait", "02:00:00")))
            .AddEngine()
            .Services
            .BuildServiceProvider();

        var options = configured.GetRequiredService<WorkloadUpdateOrchestratorOptions>();

        options.HealthGateTimeout.Should().Be(TimeSpan.FromMinutes(15));
        options.HealthGateMaximumWait.Should().Be(TimeSpan.FromHours(2));
        options.RollbackTimeout.Should().Be(TimeSpan.FromMinutes(5));
    }

    [Fact]
    public void ThePurgeGoesThroughTheSchemeDispatcher_WhichNoProviderReplaces()
    {
        FullEngine().Services.Last(d => d.ServiceType == typeof(IRetainedVolumeDeleter))
            .ImplementationType!.Name.Should().Be("SchemeDispatchingRetainedVolumeDeleter");
    }

    [Fact]
    public void EachProviderRegistersItsOwnRetainedVolumeSchemeDeleter()
    {
        var kubernetes = FullEngine().Services.Where(d => d.ServiceType == typeof(IRetainedVolumeSchemeDeleter))
            .Select(d => d.ImplementationType).ToList();
        var docker = DockerOnlyEngine().Services.Where(d => d.ServiceType == typeof(IRetainedVolumeSchemeDeleter))
            .Select(d => d.ImplementationType).ToList();

        kubernetes.Should().Contain(typeof(KubernetesRetainedVolumeDeleter));
        docker.Should().ContainSingle().Which!.Name.Should().Be("DockerRetainedVolumeDeleter");
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task ThePurgeOfEachSchemeReachesItsProvidersDeleter(bool docker, bool kubernetes)
    {
        var builder = new ServiceCollection()
            .AddReeveWorkloads(EmptyConfiguration)
            .AddEngine()
            .AddEntityFrameworkStores(options => options.UseNpgsql(UnopenedConnectionString));
        if (docker)
        {
            builder.AddDockerProvider();
        }

        if (kubernetes)
        {
            builder.AddKubernetesProvider();
        }

        var doubles = new Dictionary<RetainedVolumeSchemeId, RecordingSchemeDeleter>();
        foreach (var descriptor in builder.Services.Where(d => d.ServiceType == typeof(IRetainedVolumeSchemeDeleter)).ToList())
        {
            builder.Services.Remove(descriptor);
            var scheme = descriptor.ImplementationType!.Name.StartsWith("Docker", StringComparison.Ordinal)
                ? RetainedVolumeSchemeId.Docker
                : RetainedVolumeSchemeId.Kubernetes;
            var recording = new RecordingSchemeDeleter(scheme);
            doubles[scheme] = recording;
            builder.Services.AddScoped<IRetainedVolumeSchemeDeleter>(_ => recording);
        }

        builder.Services.AddLogging();
        using var provider = builder.Services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        var deleter = scope.ServiceProvider.GetRequiredService<IRetainedVolumeDeleter>();

        doubles.Keys.Should().BeEquivalentTo(
            new[] { docker ? RetainedVolumeSchemeId.Docker : default, kubernetes ? RetainedVolumeSchemeId.Kubernetes : default }
                .Where(scheme => !scheme.IsEmpty));
        foreach (var scheme in new[] { RetainedVolumeSchemeId.Docker, RetainedVolumeSchemeId.Kubernetes })
        {
            var row = RetainedVolume.Record(
                Guid.NewGuid(), "zwave", "zwave-js", "state", $"volume-{scheme}", "1Gi",
                Reeve.Workloads.Abstractions.Contracts.Deployment.VolumeCriticality.Critical, null,
                WorkloadProviderId.Parse("any"), scheme, Guid.NewGuid(), Guid.NewGuid(), DateTimeOffset.UnixEpoch);

            var freed = await deleter.TryDeletePhysicalVolumeAsync(row);

            freed.Should().Be(doubles.ContainsKey(scheme));
            if (doubles.TryGetValue(scheme, out var owner))
            {
                owner.Deleted.Should().ContainSingle().Which.Should().Be($"volume-{scheme}");
            }
        }
    }

    private sealed class RecordingSchemeDeleter(RetainedVolumeSchemeId scheme) : IRetainedVolumeSchemeDeleter
    {
        public List<string> Deleted { get; } = [];

        public RetainedVolumeSchemeId NamingScheme => scheme;

        public Task<bool> TryDeletePhysicalVolumeAsync(RetainedVolume retainedVolume, CancellationToken cancellationToken = default)
        {
            Deleted.Add(retainedVolume.ResourceName);
            return Task.FromResult(true);
        }
    }

    [Fact]
    public void ADockerOnlyEngine_OwesNoPortTheDocumentedListDoesNotAlreadyName()
    {
        DockerOnlyEngine().FindMissingHostPorts().Select(m => m.Port.FullName)
            .Should().BeSubsetOf(ExpectedHostPorts);
    }

    private const string UnopenedConnectionString =
        "Host=composition-tests.invalid;Database=workloads;Username=none;Password=none";

    private static readonly IConfiguration EmptyConfiguration = new ConfigurationBuilder().Build();

    private static IConfiguration Configuration(params (string Key, string Value)[] values)
        => new ConfigurationBuilder()
            .AddInMemoryCollection(values.Select(v => new KeyValuePair<string, string?>(v.Key, v.Value)))
            .Build();

    private static ReeveWorkloadsBuilder FullEngine()
    {
        var builder = new ServiceCollection()
            .AddReeveWorkloads(EmptyConfiguration)
            .AddEngine()
            .AddEntityFrameworkStores(options => options.UseNpgsql(UnopenedConnectionString))
            .AddDockerProvider()
            .AddKubernetesProvider()
            .AddManualProvider();
        return builder;
    }

    private static ReeveWorkloadsBuilder DockerOnlyEngine()
    {
        var builder = new ServiceCollection()
            .AddReeveWorkloads(EmptyConfiguration)
            .AddEngine()
            .AddEntityFrameworkStores(options => options.UseNpgsql(UnopenedConnectionString))
            .AddDockerProvider();
        return builder;
    }
}
