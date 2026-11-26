using System.Text.Json;
using Acme.Workloads.Nomad;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Reeve.Workloads.Abstractions;
using Reeve.Workloads.Abstractions.Contracts;
using Reeve.Workloads.Abstractions.Domain.Entities;
using Reeve.Workloads.Abstractions.Providers;
using Reeve.Workloads.DependencyInjection;
using Reeve.Workloads.Domain;
using Reeve.Workloads.Testing;

namespace Reeve.Workloads.ThirdPartyProvider.Tests;

public sealed class ThirdPartyProviderAcceptanceTests
{
    private static readonly IConfiguration EmptyConfiguration =
        new ConfigurationBuilder().AddInMemoryCollection([]).Build();

    [Fact]
    public void AThirdPartyProvider_AddsNoObligationOfItsOwnToTheHost()
    {
        var withNomad = new ServiceCollection()
            .AddReeveWorkloads(EmptyConfiguration)
            .AddEngine()
            .AddNomadProvider();

        var bare = new ServiceCollection()
            .AddReeveWorkloads(EmptyConfiguration)
            .AddEngine();

        withNomad.FindMissingHostPorts().Select(m => m.Port.FullName)
            .Should().BeEquivalentTo(bare.FindMissingHostPorts().Select(m => m.Port.FullName));
    }

    [Fact]
    public void TheRegistry_ResolvesAProviderItWasNeverToldAbout_AndReportsItsOwnTraits()
    {
        using var host = Host();

        var registry = host.GetRequiredService<IWorkloadProviderRegistry>();

        registry.TryResolve(NomadProvider.Id).Should().NotBeNull();
        registry.Describe(NomadProvider.Id).PublishesHostPorts.Should().BeTrue();
        registry.Describe(NomadProvider.Id).RequiresRfc1123AttachmentNames.Should().BeFalse();
    }

    [Fact]
    public async Task ASecretInAThirdPartyProvidersSettings_IsMaskedOnRead()
    {
        var source = new InMemoryWorkloadOrchestratorSettingsSource(new WorkloadOrchestratorSettings
        {
            Provider = NomadProvider.Id,
            Providers = Entry(new NomadProviderSettings { Address = "http://nomad:4646", Token = "s3cr3t" }),
        });
        using var host = Host(source);
        using var scope = host.CreateScope();

        var read = await scope.ServiceProvider.GetRequiredService<IWorkloadOrchestratorSettingsService>().GetAsync(CancellationToken.None);
        var nomad = NomadProviderSettings.From(read.Settings);

        nomad.Token.Should().Be("***");
        nomad.Address.Should().Be("http://nomad:4646", "a non-secret field is never masked");
    }

    [Fact]
    public void ASecretInAThirdPartyProvidersSettings_IsNamedToTheHostForProtection()
    {
        using var host = Host();
        var rules = host.GetRequiredService<IWorkloadOrchestratorSettingsRules>();

        var secrets = rules.Secrets(new WorkloadOrchestratorSettings
        {
            Provider = NomadProvider.Id,
            Providers = Entry(new NomadProviderSettings { Token = "s3cr3t" }),
        });

        secrets.Should().ContainSingle().Which.Should().Be(new WorkloadSecretField(NomadProvider.Id, "/token", "s3cr3t"));
    }

    [Fact]
    public void AMaskedSecretInAThirdPartyProvidersSettings_ResolvesAgainstTheStoredOne()
    {
        using var host = Host();
        var rules = host.GetRequiredService<IWorkloadOrchestratorSettingsRules>();
        var stored = new WorkloadOrchestratorSettings { Providers = Entry(new NomadProviderSettings { Token = "s3cr3t" }) };
        var submitted = new WorkloadOrchestratorSettings { Providers = Entry(new NomadProviderSettings { Token = "***" }) };

        NomadProviderSettings.From(rules.ResolveMasked(submitted, stored)).Token.Should().Be("s3cr3t");
    }

    [Fact]
    public async Task AutoDetect_SelectsTheOnlyProviderThatReportsItselfAvailable()
    {
        var registeredFirst = Substitute.For<IWorkloadProvider>();
        registeredFirst.Descriptor.Returns(new WorkloadProviderDescriptor { Id = WorkloadProviderId.Parse("registered-first") });
        using var host = Host(registeredFirst: registeredFirst);
        using var scope = host.CreateScope();

        var read = await scope.ServiceProvider.GetRequiredService<IWorkloadOrchestratorSettingsService>()
            .GetAsync(CancellationToken.None);

        read.EffectiveProvider.Should().Be(NomadProvider.Id);
    }

    [Fact]
    public void TheNamerAThirdPartyProviderRegisters_IsTheOneTheEngineAsksForItsWorkloads()
    {
        using var host = Host();

        var namers = host.GetRequiredService<IWorkloadResourceNamerRegistry>();

        namers.For(NomadProvider.Id).Should().BeOfType<NomadResourceNamer>();
        namers.For(WorkloadProviderId.Parse("some-other")).Should().NotBeOfType<NomadResourceNamer>();
    }

    [Fact]
    public void AVolumeRetainedFromANomadInstance_RecordsTheNameAndSchemeNomadGaveIt()
    {
        using var host = Host();

        var instance = WorkloadInstance.Create(Guid.NewGuid(), Guid.NewGuid(), "acme.zwave", "controller", DateTimeOffset.UnixEpoch);
        instance.SetProvider(NomadProvider.Id, DateTimeOffset.UnixEpoch);
        var namer = host.GetRequiredService<IWorkloadResourceNamerRegistry>().For(NomadProvider.Id);

        WorkloadResourceNaming.ResolveRetainedResourceName(instance, "state", namer)
            .Should().Be($"nomad-acme-zwave-state-{instance.Inst8}");
        WorkloadResourceNaming.ResolveRetainedResourceScheme(instance, "state", namer)
            .Should().Be(NomadResourceNamer.VolumeSchemeId);
    }

    private static IReadOnlyDictionary<WorkloadProviderId, JsonElement> Entry(NomadProviderSettings settings)
        => new Dictionary<WorkloadProviderId, JsonElement>
        {
            [NomadProvider.Id] = JsonSerializer.SerializeToElement(settings),
        };

    private static ServiceProvider Host(IWorkloadOrchestratorSettingsSource? source = null, IWorkloadProvider? registeredFirst = null)
    {
        var services = new ServiceCollection();
        if (registeredFirst is not null)
        {
            services.AddSingleton(registeredFirst);
        }

        services.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddSingleton<IDataProtectionProvider>(new EphemeralDataProtectionProvider());
        services.AddSingleton(Substitute.For<IWorkloadInstanceRepository>());

        services
            .AddReeveWorkloads(EmptyConfiguration, ports => ports.Use<IWorkloadOrchestratorSettingsSource>(
                _ => source ?? new InMemoryWorkloadOrchestratorSettingsSource(), ServiceLifetime.Singleton))
            .AddEngine()
            .AddNomadProvider();

        return services.BuildServiceProvider();
    }
}
