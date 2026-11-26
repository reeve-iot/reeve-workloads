using System;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Docker.DotNet;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Reeve.Workloads.Abstractions;
using Reeve.Workloads.Abstractions.Contracts;
using Reeve.Workloads.Abstractions.Providers;
using Reeve.Workloads.Core;
using Reeve.Workloads.Providers.Docker;
using Reeve.Workloads.Providers.Kubernetes;
using Reeve.Workloads.Providers.Manual;
using Xunit;

namespace Reeve.Workloads.Core.Tests;

public sealed class DockerAutoDetectTests
{
    private readonly SwitchableSettingsSource source = new();
    private readonly ICompiledProviderCatalog catalog = Substitute.For<ICompiledProviderCatalog>();
    private readonly IInClusterEnvironment env = Substitute.For<IInClusterEnvironment>();

    public DockerAutoDetectTests()
    {
        catalog.GetProviders().Returns(Array.Empty<WorkloadProviderInfo>());
        catalog.IsCompiledIn(KubernetesProvider.Id).Returns(true);
        catalog.IsCompiledIn(ManualProvider.Id).Returns(true);
    }

    private WorkloadOrchestratorSettingsService CreateService(IWorkloadProviderAutoDetect? docker = null)
        => new(
            source, new WorkloadOrchestratorSettingsRules(catalog, Array.Empty<IWorkloadProviderSettingsCodec>()), catalog,
            Array.Empty<IWorkloadProviderProbe>(),
            docker is null
                ? [new KubernetesAutoDetect(env), new ManualAutoDetect()]
                : [new KubernetesAutoDetect(env), docker, new ManualAutoDetect()]);

    private void Stored(WorkloadOrchestratorSettings settings) => source.Document = settings;

    private sealed class FakeDockerAutoDetect(bool available) : IWorkloadProviderAutoDetect
    {
        public WorkloadProviderId Provider => DockerProvider.Id;

        public int Priority => IWorkloadProviderAutoDetect.LocalRuntimePriority;

        public int Calls { get; private set; }

        public string? SeenSocketUri { get; private set; }

        public Task<bool> IsAvailableAsync(JsonElement providerSettings, CancellationToken cancellationToken = default)
        {
            Calls++;
            SeenSocketUri = providerSettings.ValueKind is JsonValueKind.Object
                            && providerSettings.TryGetProperty("socketUri", out var uri)
                            && uri.ValueKind is JsonValueKind.String
                ? uri.GetString()
                : null;
            return Task.FromResult(available);
        }
    }

    [Fact]
    public async Task InCluster_resolves_kubernetes_even_when_the_docker_socket_is_available()
    {
        env.IsInCluster.Returns(true);
        catalog.IsCompiledIn(DockerProvider.Id).Returns(true);
        var docker = new FakeDockerAutoDetect(available: true);
        Stored(new WorkloadOrchestratorSettings { Provider = null });

        var response = await CreateService(docker).GetAsync(CancellationToken.None);

        response.AutoDetected.Should().BeTrue();
        response.EffectiveProvider.Should().Be(KubernetesProvider.Id);
        docker.Calls.Should().Be(0);
    }

    [Fact]
    public async Task NotInCluster_docker_registered_and_socket_live_resolves_docker()
    {
        env.IsInCluster.Returns(false);
        catalog.IsCompiledIn(DockerProvider.Id).Returns(true);
        var docker = new FakeDockerAutoDetect(available: true);
        Stored(new WorkloadOrchestratorSettings { Provider = null }
            .WithProvider(DockerProvider.Id, new DockerProviderSettings { SocketUri = "unix:///var/run/docker.sock" }));

        var response = await CreateService(docker).GetAsync(CancellationToken.None);

        response.AutoDetected.Should().BeTrue();
        response.EffectiveProvider.Should().Be(DockerProvider.Id);
        docker.SeenSocketUri.Should().Be("unix:///var/run/docker.sock");
    }

    [Fact]
    public async Task NotInCluster_an_unavailable_socket_falls_through_to_manual()
    {
        env.IsInCluster.Returns(false);
        catalog.IsCompiledIn(DockerProvider.Id).Returns(true);
        var docker = new FakeDockerAutoDetect(available: false);
        Stored(new WorkloadOrchestratorSettings { Provider = null });

        var response = await CreateService(docker).GetAsync(CancellationToken.None);

        response.AutoDetected.Should().BeTrue();
        response.EffectiveProvider.Should().Be(ManualProvider.Id);
        docker.Calls.Should().Be(1);
    }

    [Fact]
    public async Task NotInCluster_with_no_docker_arm_at_all_resolves_manual()
    {
        env.IsInCluster.Returns(false);
        catalog.IsCompiledIn(DockerProvider.Id).Returns(true);
        Stored(new WorkloadOrchestratorSettings { Provider = null });

        var response = await CreateService(docker: null).GetAsync(CancellationToken.None);

        response.AutoDetected.Should().BeTrue();
        response.EffectiveProvider.Should().Be(ManualProvider.Id);
    }

    [Fact]
    public async Task An_arm_whose_provider_is_not_registered_is_never_consulted()
    {
        env.IsInCluster.Returns(false);
        catalog.IsCompiledIn(DockerProvider.Id).Returns(false);
        var docker = new FakeDockerAutoDetect(available: true);
        Stored(new WorkloadOrchestratorSettings { Provider = null });

        var response = await CreateService(docker).GetAsync(CancellationToken.None);

        response.EffectiveProvider.Should().Be(ManualProvider.Id);
        docker.Calls.Should().Be(0);
    }

    [Fact]
    public async Task Explicit_provider_always_wins_over_an_available_docker_socket()
    {
        env.IsInCluster.Returns(false);
        catalog.IsCompiledIn(DockerProvider.Id).Returns(true);
        var docker = new FakeDockerAutoDetect(available: true);
        Stored(new WorkloadOrchestratorSettings { Provider = ManualProvider.Id });

        var response = await CreateService(docker).GetAsync(CancellationToken.None);

        response.AutoDetected.Should().BeFalse();
        response.EffectiveProvider.Should().Be(ManualProvider.Id);
        docker.Calls.Should().Be(0);
    }

    [Fact]
    public async Task With_nothing_claiming_availability_the_first_registered_provider_is_the_answer()
    {
        env.IsInCluster.Returns(false);
        catalog.GetProviders().Returns(
        [
            new WorkloadProviderInfo { Provider = KubernetesProvider.Id, CompiledIn = true },
        ]);
        Stored(new WorkloadOrchestratorSettings { Provider = null });

        var service = new WorkloadOrchestratorSettingsService(
            source, new WorkloadOrchestratorSettingsRules(catalog, Array.Empty<IWorkloadProviderSettingsCodec>()), catalog,
            Array.Empty<IWorkloadProviderProbe>(),
            [new KubernetesAutoDetect(env)]);

        var response = await service.GetAsync(CancellationToken.None);

        response.AutoDetected.Should().BeTrue();
        response.EffectiveProvider.Should().Be(KubernetesProvider.Id);
    }

    [Fact]
    public async Task Two_arms_of_equal_priority_break_the_tie_on_the_provider_id_not_on_registration_order()
    {
        env.IsInCluster.Returns(false);
        catalog.IsCompiledIn(DockerProvider.Id).Returns(true);
        catalog.IsCompiledIn(WorkloadProviderId.Parse("acme")).Returns(true);
        Stored(new WorkloadOrchestratorSettings { Provider = null });

        var docker = new FakeDockerAutoDetect(available: true);
        var acme = new AlwaysAvailable(WorkloadProviderId.Parse("acme"), IWorkloadProviderAutoDetect.LocalRuntimePriority);

        var forwards = await Resolve([docker, acme]);
        var backwards = await Resolve([acme, new FakeDockerAutoDetect(available: true)]);

        forwards.Should().Be(WorkloadProviderId.Parse("acme"), "'acme' sorts before 'docker'");
        backwards.Should().Be(forwards, "the registration order must not decide it");
    }

    private async Task<WorkloadProviderId> Resolve(IWorkloadProviderAutoDetect[] arms)
    {
        var service = new WorkloadOrchestratorSettingsService(
            source, new WorkloadOrchestratorSettingsRules(catalog, Array.Empty<IWorkloadProviderSettingsCodec>()), catalog,
            Array.Empty<IWorkloadProviderProbe>(),
            arms);
        return (await service.GetAsync(CancellationToken.None)).EffectiveProvider;
    }

    private sealed class AlwaysAvailable(WorkloadProviderId provider, int priority) : IWorkloadProviderAutoDetect
    {
        public WorkloadProviderId Provider => provider;

        public int Priority => priority;

        public Task<bool> IsAvailableAsync(JsonElement providerSettings, CancellationToken cancellationToken = default)
            => Task.FromResult(true);
    }

    private static (DockerAutoDetect Probe, ISystemOperations System, IDockerClientFactory Factory) BuildProbe()
    {
        var factory = Substitute.For<IDockerClientFactory>();
        var client = Substitute.For<IDockerClient>();
        var system = Substitute.For<ISystemOperations>();
        client.System.Returns(system);
        factory.CreateClient(Arg.Any<string?>()).Returns(client);
        var probe = new DockerAutoDetect(factory, NullLogger<DockerAutoDetect>.Instance);
        return (probe, system, factory);
    }

    private static JsonElement Socket(string? socketUri)
        => JsonSerializer.SerializeToElement(new DockerProviderSettings { SocketUri = socketUri });

    [Fact]
    public async Task Probe_returns_available_when_the_daemon_answers_ping()
    {
        var (probe, system, factory) = BuildProbe();
        system.PingAsync(Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);

        var available = await probe.IsAvailableAsync(Socket("unix:///var/run/docker.sock"), CancellationToken.None);

        available.Should().BeTrue();
        factory.Received(1).CreateClient("unix:///var/run/docker.sock");
        await system.Received(1).PingAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Probe_returns_unavailable_when_the_socket_is_inaccessible_eacces()
    {
        var (probe, system, _) = BuildProbe();
        system.PingAsync(Arg.Any<CancellationToken>())
            .ThrowsAsync(new HttpRequestException("connect /var/run/docker.sock: permission denied"));

        var available = await probe.IsAvailableAsync(Socket(null), CancellationToken.None);

        available.Should().BeFalse();
    }

    [Fact]
    public async Task Probe_returns_unavailable_when_the_client_factory_rejects_a_malformed_socket_uri()
    {
        var factory = Substitute.For<IDockerClientFactory>();
        factory.CreateClient(Arg.Any<string?>()).Returns(_ => throw new FormatException("bad socket uri"));
        var probe = new DockerAutoDetect(factory, NullLogger<DockerAutoDetect>.Instance);

        var available = await probe.IsAvailableAsync(Socket("not a uri"), CancellationToken.None);

        available.Should().BeFalse();
    }

    [Fact]
    public async Task Probe_reads_an_absent_entry_as_the_platform_default_socket()
    {
        var (probe, system, factory) = BuildProbe();
        system.PingAsync(Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);

        var available = await probe.IsAvailableAsync(default, CancellationToken.None);

        available.Should().BeTrue();
        factory.Received(1).CreateClient(null);
    }

    [Fact]
    public async Task Probe_propagates_a_caller_requested_cancellation()
    {
        var (probe, system, _) = BuildProbe();
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        system.PingAsync(Arg.Any<CancellationToken>()).ThrowsAsync(new OperationCanceledException());

        var act = () => probe.IsAvailableAsync(Socket(null), cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task An_arm_whose_provider_is_not_configured_is_never_consulted()
    {
        env.IsInCluster.Returns(false);
        catalog.IsCompiledIn(DockerProvider.Id).Returns(true);
        var docker = new FakeDockerAutoDetect(available: true);
        Stored(new WorkloadOrchestratorSettings { ConfiguredProviders = [ManualProvider.Id] });

        var response = await CreateService(docker).GetAsync(CancellationToken.None);

        response.EffectiveProvider.Should().Be(ManualProvider.Id);
        docker.Calls.Should().Be(0);
    }

    [Fact]
    public async Task A_configured_arm_is_consulted_and_wins()
    {
        env.IsInCluster.Returns(false);
        catalog.IsCompiledIn(DockerProvider.Id).Returns(true);
        var docker = new FakeDockerAutoDetect(available: true);
        Stored(new WorkloadOrchestratorSettings { ConfiguredProviders = [DockerProvider.Id, ManualProvider.Id] });

        var response = await CreateService(docker).GetAsync(CancellationToken.None);

        response.EffectiveProvider.Should().Be(DockerProvider.Id);
        docker.Calls.Should().Be(1);
    }

    [Fact]
    public async Task Auto_detect_over_a_set_that_excludes_every_arm_falls_back_to_the_first_configured_catalog_provider()
    {
        env.IsInCluster.Returns(false);
        catalog.GetProviders().Returns(
        [
            new WorkloadProviderInfo { Provider = DockerProvider.Id, CompiledIn = true },
            new WorkloadProviderInfo { Provider = KubernetesProvider.Id, CompiledIn = true },
        ]);
        Stored(new WorkloadOrchestratorSettings { ConfiguredProviders = [KubernetesProvider.Id] }
            .WithProvider(KubernetesProvider.Id, new KubernetesProviderSettings()));
        env.IsInCluster.Returns(false);

        var response = await CreateService(docker: null).GetAsync(CancellationToken.None);

        response.EffectiveProvider.Should().Be(KubernetesProvider.Id);
    }
}
