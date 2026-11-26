using FluentAssertions;
using NSubstitute;
using Reeve.Workloads.Abstractions;
using Reeve.Workloads.Abstractions.Contracts;
using Reeve.Workloads.Abstractions.Providers;
using Reeve.Workloads.Core;
using Reeve.Workloads.Providers.Docker;
using Reeve.Workloads.Providers.Kubernetes;
using Reeve.Workloads.Providers.Manual;
using Xunit;

namespace Reeve.Workloads.Core.Tests;

public sealed class WorkloadOrchestratorSettingsServiceTests
{
    private readonly SwitchableSettingsSource source = new();

    private WorkloadOrchestratorSettingsService CreateService(bool inCluster = false, IEnumerable<IWorkloadProviderProbe>? probes = null)
        => new(
            source,
            SettingsKit.ShippedRules(),
            SettingsKit.ShippedCatalog(),
            probes ?? Array.Empty<IWorkloadProviderProbe>(),
            [new KubernetesAutoDetect(new FakeInClusterEnvironment(inCluster)), new ManualAutoDetect()]);

    [Fact]
    public async Task GetAsync_serves_what_the_source_holds_with_secrets_masked()
    {
        source.Document = SettingsKit.Docker("registry-secret") with { AllowUnpinnedImages = true };

        var response = await CreateService().GetAsync();

        response.Settings.AllowUnpinnedImages.Should().BeTrue();
        DockerProviderSettings.From(response.Settings).RegistryCredentials[0].Token.Should().Be(WorkloadSecretMask.Sentinel);
        response.EffectiveProvider.Should().Be(DockerProvider.Id);
        response.AutoDetected.Should().BeFalse();
    }

    [Fact]
    public async Task GetDecryptedSettingsAsync_returns_the_secrets_as_the_source_holds_them()
    {
        source.Document = SettingsKit.Docker("registry-secret");

        var document = await CreateService().GetDecryptedSettingsAsync();

        DockerProviderSettings.From(document).RegistryCredentials[0].Token.Should().Be("registry-secret");
    }

    [Fact]
    public async Task A_changed_source_document_is_seen_by_the_next_read()
    {
        var service = CreateService();
        source.Document = SettingsKit.Docker();
        (await service.GetAsync()).EffectiveProvider.Should().Be(DockerProvider.Id);

        source.Document = SettingsKit.Kubernetes();

        (await service.GetAsync()).EffectiveProvider.Should().Be(KubernetesProvider.Id);
    }

    [Fact]
    public async Task Autodetect_prefers_kubernetes_when_in_cluster()
    {
        var response = await CreateService(inCluster: true).GetAsync();

        response.AutoDetected.Should().BeTrue();
        response.EffectiveProvider.Should().Be(KubernetesProvider.Id);
    }

    [Fact]
    public async Task Autodetect_falls_back_to_manual_when_not_in_cluster()
    {
        var response = await CreateService(inCluster: false).GetAsync();

        response.AutoDetected.Should().BeTrue();
        response.EffectiveProvider.Should().Be(ManualProvider.Id);
    }

    [Fact]
    public async Task Explicit_provider_overrides_autodetect()
    {
        source.Document = new WorkloadOrchestratorSettings { Provider = ManualProvider.Id };

        var response = await CreateService(inCluster: true).GetAsync();

        response.AutoDetected.Should().BeFalse();
        response.EffectiveProvider.Should().Be(ManualProvider.Id);
    }

    [Fact]
    public async Task Duplicate_probe_provider_registration_does_not_throw_at_construction()
    {
        var first = Substitute.For<IWorkloadProviderProbe>();
        first.Provider.Returns(KubernetesProvider.Id);
        var second = Substitute.For<IWorkloadProviderProbe>();
        second.Provider.Returns(KubernetesProvider.Id);

        var act = () => CreateService(probes: [first, second]).GetAsync();

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task Test_returns_probe_unavailable_when_no_probe_registered()
    {
        source.Document = new WorkloadOrchestratorSettings { Provider = KubernetesProvider.Id };

        var result = await CreateService().TestAsync(null);

        result.ProbeAvailable.Should().BeFalse();
        result.Provider.Should().Be(KubernetesProvider.Id);
        result.Message.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task Test_invokes_registered_probe_for_requested_provider()
    {
        var probe = Substitute.For<IWorkloadProviderProbe>();
        probe.Provider.Returns(KubernetesProvider.Id);
        probe.ProbeAsync(Arg.Any<WorkloadOrchestratorSettings>(), Arg.Any<CancellationToken>())
            .Returns(new WorkloadProviderProbeResult { Provider = KubernetesProvider.Id, ProbeAvailable = true, CanListNodes = true });
        source.Document = new WorkloadOrchestratorSettings { Provider = ManualProvider.Id };

        var result = await CreateService(probes: [probe]).TestAsync(KubernetesProvider.Id);

        result.CanListNodes.Should().BeTrue();
        await probe.Received(1).ProbeAsync(Arg.Any<WorkloadOrchestratorSettings>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Test_passes_the_plaintext_secret_to_the_probe()
    {
        WorkloadOrchestratorSettings? seen = null;
        var probe = Substitute.For<IWorkloadProviderProbe>();
        probe.Provider.Returns(DockerProvider.Id);
        probe.ProbeAsync(Arg.Do<WorkloadOrchestratorSettings>(s => seen = s), Arg.Any<CancellationToken>())
            .Returns(new WorkloadProviderProbeResult { Provider = DockerProvider.Id, ProbeAvailable = true });
        source.Document = SettingsKit.Docker("docker-registry-secret");

        await CreateService(probes: [probe]).TestAsync(DockerProvider.Id);

        DockerProviderSettings.From(seen!).RegistryCredentials[0].Token.Should().Be("docker-registry-secret");
    }

    [Fact]
    public void Catalog_reports_all_three_providers_compiled_in()
    {
        var catalog = SettingsKit.ShippedCatalog();

        catalog.IsCompiledIn(KubernetesProvider.Id).Should().BeTrue();
        catalog.IsCompiledIn(ManualProvider.Id).Should().BeTrue();
        catalog.IsCompiledIn(DockerProvider.Id).Should().BeTrue();
    }

    private sealed class FakeInClusterEnvironment(bool value) : IInClusterEnvironment
    {
        public bool IsInCluster { get; } = value;

        public string? Namespace => null;
    }
}
