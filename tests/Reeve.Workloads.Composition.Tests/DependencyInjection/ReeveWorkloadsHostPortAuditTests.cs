using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Reeve.Workloads.DependencyInjection;

namespace Reeve.Workloads.Composition.Tests.DependencyInjection;

public sealed class ReeveWorkloadsHostPortAuditTests
{
    private static readonly IConfiguration EmptyConfiguration = new ConfigurationBuilder().Build();

    [Fact]
    public void APortNothingRegisters_IsReportedAgainstTheTypeThatAsksForIt()
    {
        var builder = new ServiceCollection().AddReeveWorkloads(EmptyConfiguration)
            .Add<IEngineFacade, EngineNeedingOnePort>(ServiceLifetime.Singleton);

        var missing = builder.FindMissingHostPorts();

        missing.Should().ContainSingle()
            .Which.Should().Match<MissingHostPort>(m =>
                m.Port == typeof(IHostSuppliedPort) && m.RequiredBy.Single() == typeof(EngineNeedingOnePort));
    }

    [Fact]
    public void APortTheHostHasRegistered_IsNotReported()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IHostSuppliedPort, HostImplementation>();

        var missing = services.AddReeveWorkloads(EmptyConfiguration)
            .Add<IEngineFacade, EngineNeedingOnePort>(ServiceLifetime.Singleton)
            .FindMissingHostPorts();

        missing.Should().BeEmpty();
    }

    [Fact]
    public void TheHostRegistrationCounts_WhicheverSideOfTheEngineItLandsOn()
    {
        var services = new ServiceCollection();
        var builder = services.AddReeveWorkloads(EmptyConfiguration).Add<IEngineFacade, EngineNeedingOnePort>(ServiceLifetime.Singleton);
        services.AddSingleton<IHostSuppliedPort, HostImplementation>();

        builder.FindMissingHostPorts().Should().BeEmpty();
    }

    [Fact]
    public void OnePortWantedByTwoEngineTypes_IsOneObligationNamingBoth()
    {
        var missing = new ServiceCollection().AddReeveWorkloads(EmptyConfiguration)
            .Add<IEngineFacade, EngineNeedingOnePort>(ServiceLifetime.Singleton)
            .Add<ISecondEngineFacade, SecondEngineNeedingTheSamePort>(ServiceLifetime.Singleton)
            .FindMissingHostPorts();

        missing.Should().ContainSingle()
            .Which.RequiredBy.Should().BeEquivalentTo(
                new[] { typeof(EngineNeedingOnePort), typeof(SecondEngineNeedingTheSamePort) });
    }

    [Fact]
    public void ACollectionOfPorts_IsNeverAnObligation()
    {
        var missing = new ServiceCollection().AddReeveWorkloads(EmptyConfiguration)
            .Add<IEngineFacade, EngineNeedingAContributorCollection>(ServiceLifetime.Singleton)
            .FindMissingHostPorts();

        missing.Should().BeEmpty();
    }

    [Fact]
    public void APortEveryCallSiteWouldAcceptAsAbsent_IsFlaggedOptional()
    {
        var missing = new ServiceCollection().AddReeveWorkloads(EmptyConfiguration)
            .Add<IEngineFacade, EngineWithAnOptionalPort>(ServiceLifetime.Singleton)
            .FindMissingHostPorts();

        missing.Should().ContainSingle().Which.EveryRequestIsOptional.Should().BeTrue();
    }

    [Fact]
    public void APortOptionalAtOneSiteAndRequiredAtAnother_IsNotOptional()
    {
        var missing = new ServiceCollection().AddReeveWorkloads(EmptyConfiguration)
            .Add<IEngineFacade, EngineWithAnOptionalPort>(ServiceLifetime.Singleton)
            .Add<ISecondEngineFacade, SecondEngineNeedingTheSamePort>(ServiceLifetime.Singleton)
            .FindMissingHostPorts();

        missing.Should().ContainSingle().Which.EveryRequestIsOptional.Should().BeFalse();
    }

    [Fact]
    public void ANonPortConstructorArgument_IsNotAnObligation()
    {
        var missing = new ServiceCollection().AddReeveWorkloads(EmptyConfiguration)
            .Add<IEngineFacade, EngineNeedingOnlyPlainArguments>(ServiceLifetime.Singleton)
            .FindMissingHostPorts();

        missing.Should().BeEmpty();
    }

    [Fact]
    public void APortPulledOutOfAScope_IsAnObligationThoughNoConstructorNamesIt()
    {
        var missing = new ServiceCollection().AddReeveWorkloads(EmptyConfiguration)
            .Add<IEngineFacade, EngineResolvingFromAScope>(ServiceLifetime.Singleton)
            .FindMissingHostPorts();

        missing.Should().ContainSingle()
            .Which.Should().Match<MissingHostPort>(m =>
                m.Port == typeof(IHostSuppliedPort) && !m.EveryRequestIsOptional);
    }

    [Fact]
    public void AScopeResolutionInsideAnAsyncMethod_IsSeenThroughTheStateMachine()
    {
        var missing = new ServiceCollection().AddReeveWorkloads(EmptyConfiguration)
            .Add<IEngineFacade, EngineResolvingInsideAnAsyncMethod>(ServiceLifetime.Singleton)
            .FindMissingHostPorts();

        missing.Should().ContainSingle().Which.Port.Should().Be(typeof(IOtherHostSuppliedPort));
    }

    [Fact]
    public void AScopeResolutionThroughGetService_IsFlaggedOptional()
    {
        var missing = new ServiceCollection().AddReeveWorkloads(EmptyConfiguration)
            .Add<IEngineFacade, EngineProbingWithGetService>(ServiceLifetime.Singleton)
            .FindMissingHostPorts();

        missing.Should().ContainSingle().Which.EveryRequestIsOptional.Should().BeTrue();
    }

    [Fact]
    public void AScopeResolutionThePortIsSupplied_IsNotReported()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IHostSuppliedPort, HostImplementation>();

        services.AddReeveWorkloads(EmptyConfiguration)
            .Add<IEngineFacade, EngineResolvingFromAScope>(ServiceLifetime.Singleton)
            .FindMissingHostPorts()
            .Should().BeEmpty();
    }

    [Fact]
    public void AFactoryRegisteredEngineService_ContributesNoObligation()
    {
        var services = new ServiceCollection();
        var builder = services.AddReeveWorkloads(EmptyConfiguration).Add<IEngineFacade, EngineNeedingOnlyPlainArguments>(ServiceLifetime.Singleton);
        services.AddSingleton<ISecondEngineFacade>(_ => new SecondEngineNeedingTheSamePort(new HostImplementation()));

        builder.FindMissingHostPorts().Should().BeEmpty();
    }

    [Fact]
    public void ValidateHostPorts_NamesEveryMissingPortInOneFailure()
    {
        var builder = new ServiceCollection().AddReeveWorkloads(EmptyConfiguration)
            .Add<IEngineFacade, EngineNeedingOnePort>(ServiceLifetime.Singleton)
            .Add<ISecondEngineFacade, SecondEngineNeedingASecondPort>(ServiceLifetime.Singleton);

        var act = () => builder.ValidateHostPorts();

        act.Should().Throw<InvalidOperationException>()
            .Which.Message.Should().Contain(nameof(IHostSuppliedPort))
            .And.Contain(nameof(IOtherHostSuppliedPort))
            .And.Contain("2 host port(s)");
    }

    [Fact]
    public void ValidateHostPorts_PassesOnceEveryPortIsSupplied()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IHostSuppliedPort, HostImplementation>();

        var builder = services.AddReeveWorkloads(EmptyConfiguration).Add<IEngineFacade, EngineNeedingOnePort>(ServiceLifetime.Singleton);

        builder.Invoking(b => b.ValidateHostPorts()).Should().NotThrow();
    }

    [Fact]
    public void ValidateHostPorts_CanBeToldToTolerateAnOptionalGap()
    {
        var builder = new ServiceCollection().AddReeveWorkloads(EmptyConfiguration)
            .Add<IEngineFacade, EngineWithAnOptionalPort>(ServiceLifetime.Singleton);

        builder.Invoking(b => b.ValidateHostPorts(includeOptional: false)).Should().NotThrow();
        builder.Invoking(b => b.ValidateHostPorts()).Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void TryAdd_LeavesAHostOverrideInPlace()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IEngineFacade, HostOverrideOfAnEngineService>();

        services.AddReeveWorkloads(EmptyConfiguration).TryAdd<IEngineFacade, EngineNeedingOnlyPlainArguments>(ServiceLifetime.Singleton);

        services.Should().ContainSingle(d => d.ServiceType == typeof(IEngineFacade))
            .Which.ImplementationType.Should().Be<HostOverrideOfAnEngineService>();
    }

    [Fact]
    public void AddEnumerable_AppendsEachDistinctImplementation()
    {
        var services = new ServiceCollection();

        services.AddReeveWorkloads(EmptyConfiguration)
            .AddEnumerable<IContributor, FirstContributor>(ServiceLifetime.Singleton)
            .AddEnumerable<IContributor, SecondContributor>(ServiceLifetime.Singleton)
            .AddEnumerable<IContributor, SecondContributor>(ServiceLifetime.Singleton);

        services.Count(d => d.ServiceType == typeof(IContributor)).Should().Be(2);
    }

    [Fact]
    public void AFactoryRegisteredDefault_IsStillReportedAsALibraryDefault()
    {
        var builder = new ServiceCollection().AddReeveWorkloads(EmptyConfiguration);
        builder.AddDefault<IHostSuppliedPort, LibraryDefaultPort>(
            _ => new LibraryDefaultPort(), ServiceLifetime.Singleton);

        builder.FindHostPortsOnLibraryDefaults().Should().Contain(typeof(IHostSuppliedPort));
    }

    [Fact]
    public void AHostRegistrationStillTakesAFactoryDefaultOffTheList()
    {
        var builder = new ServiceCollection().AddReeveWorkloads(EmptyConfiguration);
        builder.AddDefault<IHostSuppliedPort, LibraryDefaultPort>(
            _ => new LibraryDefaultPort(), ServiceLifetime.Singleton);
        builder.Services.AddSingleton<IHostSuppliedPort, HostImplementation>();

        builder.FindHostPortsOnLibraryDefaults().Should().NotContain(typeof(IHostSuppliedPort));
    }

    [Fact]
    public void AFactoryRegisteredDefault_StillYieldsToAHostThatGotThereFirst()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IHostSuppliedPort, HostImplementation>();
        var builder = services.AddReeveWorkloads(EmptyConfiguration);
        builder.AddDefault<IHostSuppliedPort, LibraryDefaultPort>(
            _ => new LibraryDefaultPort(), ServiceLifetime.Singleton);

        builder.Services.Should().ContainSingle(d => d.ServiceType == typeof(IHostSuppliedPort))
            .Which.ImplementationType.Should().Be<HostImplementation>();
        builder.FindHostPortsOnLibraryDefaults().Should().NotContain(typeof(IHostSuppliedPort));
    }

    public interface IEngineFacade
    {
    }

    public interface ISecondEngineFacade
    {
    }

    public interface IHostSuppliedPort
    {
    }

    public interface IOtherHostSuppliedPort
    {
    }

    public interface IContributor
    {
    }

    private sealed class EngineNeedingOnePort(IHostSuppliedPort port) : IEngineFacade
    {
        public IHostSuppliedPort Port { get; } = port;
    }

    private sealed class SecondEngineNeedingTheSamePort(IHostSuppliedPort port) : ISecondEngineFacade
    {
        public IHostSuppliedPort Port { get; } = port;
    }

    private sealed class SecondEngineNeedingASecondPort(IOtherHostSuppliedPort port) : ISecondEngineFacade
    {
        public IOtherHostSuppliedPort Port { get; } = port;
    }

    private sealed class EngineNeedingAContributorCollection(IEnumerable<IContributor> contributors) : IEngineFacade
    {
        public IEnumerable<IContributor> Contributors { get; } = contributors;
    }

    private sealed class EngineWithAnOptionalPort(IHostSuppliedPort? port = null) : IEngineFacade
    {
        public IHostSuppliedPort? Port { get; } = port;
    }

    private sealed class EngineNeedingOnlyPlainArguments(
        string name, IServiceProvider services, HostImplementation concrete) : IEngineFacade
    {
        public string Name { get; } = name;

        public IServiceProvider Services { get; } = services;

        public HostImplementation Concrete { get; } = concrete;
    }

    private sealed class EngineResolvingFromAScope(IServiceScopeFactory scopes) : IEngineFacade
    {
        public IHostSuppliedPort Work()
        {
            using var scope = scopes.CreateScope();
            return scope.ServiceProvider.GetRequiredService<IHostSuppliedPort>();
        }
    }

    private sealed class EngineResolvingInsideAnAsyncMethod(IServiceScopeFactory scopes) : IEngineFacade
    {
        public async Task<IOtherHostSuppliedPort> WorkAsync()
        {
            await Task.Yield();
            using var scope = scopes.CreateScope();
            return scope.ServiceProvider.GetRequiredService<IOtherHostSuppliedPort>();
        }
    }

    [Fact]
    public void ALibraryTryAddWithAFactory_AnswersThePortWithoutClaimingTheHostDeclinedAnything()
    {
        var builder = new ServiceCollection().AddReeveWorkloads(EmptyConfiguration)
            .Add<IEngineFacade, EngineNeedingOnePort>(ServiceLifetime.Singleton);
        builder.TryAdd<IHostSuppliedPort, LibraryImplementation>(_ => new LibraryImplementation(), ServiceLifetime.Scoped);

        builder.DefaultedPorts.Should().NotContain(typeof(IHostSuppliedPort));
        builder.FindHostPortsOnLibraryDefaults().Should().NotContain(typeof(IHostSuppliedPort));
        builder.FindMissingHostPorts().Select(m => m.Port).Should().NotContain(typeof(IHostSuppliedPort));
    }

    [Fact]
    public void AHostRegistrationBeforeOrAfterALibraryTryAddWithAFactoryWins()
    {
        var before = new ServiceCollection();
        before.AddScoped<IHostSuppliedPort, HostImplementation>();
        before.AddReeveWorkloads(EmptyConfiguration)
            .TryAdd<IHostSuppliedPort, LibraryImplementation>(_ => new LibraryImplementation(), ServiceLifetime.Scoped);
        before.Last(d => d.ServiceType == typeof(IHostSuppliedPort)).ImplementationType.Should().Be(typeof(HostImplementation));

        var after = new ServiceCollection().AddReeveWorkloads(EmptyConfiguration);
        after.TryAdd<IHostSuppliedPort, LibraryImplementation>(_ => new LibraryImplementation(), ServiceLifetime.Scoped);
        after.Services.AddScoped<IHostSuppliedPort, HostImplementation>();
        after.Services.Last(d => d.ServiceType == typeof(IHostSuppliedPort)).ImplementationType.Should().Be(typeof(HostImplementation));
    }

    private sealed class LibraryImplementation : IHostSuppliedPort
    {
    }

    private sealed class EngineProbingWithGetService(IServiceScopeFactory scopes) : IEngineFacade
    {
        public IHostSuppliedPort? Work()
        {
            using var scope = scopes.CreateScope();
            return scope.ServiceProvider.GetService<IHostSuppliedPort>();
        }
    }

    private sealed class HostOverrideOfAnEngineService : IEngineFacade
    {
    }

    private sealed class HostImplementation : IHostSuppliedPort
    {
    }

    private sealed class LibraryDefaultPort : IHostSuppliedPort
    {
    }

    private sealed class FirstContributor : IContributor
    {
    }

    private sealed class SecondContributor : IContributor
    {
    }
}
