using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Docker.DotNet;
using Docker.DotNet.Models;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.Core;
using Reeve.Workloads.Abstractions;
using Reeve.Workloads.Abstractions.Contracts;
using Reeve.Workloads.Abstractions.Contracts.Deployment;
using Reeve.Workloads.Abstractions.Domain;
using Reeve.Workloads.Abstractions.Domain.DesiredSpec;
using Reeve.Workloads.Abstractions.Providers;
using Reeve.Workloads.Domain;
using Reeve.Workloads.Providers.Docker;
using Reeve.Workloads.Tests.Shared;

namespace Reeve.Workloads.Providers.Docker.IntegrationTests;

[Collection(DockerIntegrationCollection.Name)]
public sealed class DockerPreflightProbeTests
{
    private readonly DockerDaemonFixture _docker;

    public DockerPreflightProbeTests(DockerDaemonFixture docker) => _docker = docker;

    [Fact]
    public async Task Below_floor_api_version_is_a_blocking_error_and_surfaces_the_version()
    {
        var client = ClientReturningVersion(Version(apiVersion: "1.40", arch: "amd64", os: "linux"));
        var sut = new DockerPreflight(FactoryFor(client));

        var result = await sut.ProbeAsync(SettingsWith(), CancellationToken.None);

        var check = result.Checks.Should().ContainSingle(c => c.Id == DockerPreflight.ApiVersionCheckId).Which;
        check.Passed.Should().BeFalse();
        check.Severity.Should().Be(PreflightSeverity.Error, "an API version below the 1.41 floor blocks the deploy");
        result.DockerApiVersion.Should().Be("1.40", "the below-floor version is still surfaced to the operator");
        result.HasBlockingError.Should().BeTrue();
    }

    [Theory]
    [InlineData("1.41")]
    [InlineData("1.51")]
    [InlineData("2.0")]
    public async Task At_or_above_floor_api_version_passes(string apiVersion)
    {
        var client = ClientReturningVersion(Version(apiVersion: apiVersion, arch: "amd64", os: "linux"));
        var sut = new DockerPreflight(FactoryFor(client));

        var result = await sut.ProbeAsync(SettingsWith(), CancellationToken.None);

        result.Checks.Should().Contain(c => c.Id == DockerPreflight.ApiVersionCheckId && c.Passed);
        result.DockerApiVersion.Should().Be(apiVersion);
        result.Checks.Should().NotContain(c => c.Id == DockerPreflight.ApiVersionCheckId && c.Severity == PreflightSeverity.Error);
    }

    [Fact]
    public async Task No_socket_reachable_is_a_blocking_error_and_never_throws()
    {
        var client = ClientWhoseVersionThrows(new HttpRequestException("connection refused"));
        var sut = new DockerPreflight(FactoryFor(client));

        var result = await sut.ProbeAsync(SettingsWith(socketUri: "tcp://127.0.0.1:2375"), CancellationToken.None);

        result.SocketReachable.Should().BeFalse();
        result.HasBlockingError.Should().BeTrue("no reachable socket on the leader blocks the apply");
        result.Checks.Should().Contain(c =>
            c.Id == DockerPreflight.SocketReachableCheckId && !c.Passed && c.Severity == PreflightSeverity.Error);
    }

    [Fact]
    public async Task Absent_socket_is_classified_as_a_distinct_named_check()
    {
        var client = ClientWhoseVersionThrows(new HttpRequestException("Cannot connect: No such file or directory"));
        var sut = new DockerPreflight(FactoryFor(client));

        var result = await sut.ProbeAsync(SettingsWith(socketUri: "tcp://127.0.0.1:2375"), CancellationToken.None);

        result.Checks.Should().ContainSingle(c => c.Id == DockerPreflight.SocketAbsentCheckId)
            .Which.Severity.Should().Be(PreflightSeverity.Error);
        result.Checks.Should().NotContain(c => c.Id == DockerPreflight.SocketPermissionCheckId);
    }

    [Fact]
    public async Task Permission_denied_socket_is_classified_as_a_distinct_named_check()
    {
        var client = ClientWhoseVersionThrows(new HttpRequestException("connect: Permission denied"));
        var sut = new DockerPreflight(FactoryFor(client));

        var result = await sut.ProbeAsync(SettingsWith(socketUri: "tcp://127.0.0.1:2375"), CancellationToken.None);

        result.Checks.Should().ContainSingle(c => c.Id == DockerPreflight.SocketPermissionCheckId)
            .Which.Severity.Should().Be(PreflightSeverity.Error);
        result.Checks.Should().NotContain(c => c.Id == DockerPreflight.SocketAbsentCheckId);
    }

    [Fact]
    public async Task A_malformed_socket_uri_is_reported_as_a_socket_error_never_thrown()
    {
        var factory = new DockerClientFactory(NullLogger<DockerClientFactory>.Instance);
        var sut = new DockerPreflight(factory);

        var act = async () => await sut.ProbeAsync(SettingsWith(socketUri: "not-a-valid-uri"), CancellationToken.None);

        var result = await act.Should().NotThrowAsync();
        result.Which.HasBlockingError.Should().BeTrue();
        result.Which.SocketReachable.Should().BeFalse();
    }

    [Fact]
    public void NodeSelector_present_yields_an_info_check_never_an_error()
    {
        var spec = SpecWithNodeSelector(new Dictionary<string, string> { ["kubernetes.io/hostname"] = "edge-1" });

        var check = DockerPreflight.NodeSelectorInfoCheck(spec);

        check.Should().NotBeNull("a declared nodeSelector must never be silently dropped");
        check!.Severity.Should().Be(PreflightSeverity.Info, "docker is a single host — a selector is informational, never blocking");
        check.Passed.Should().BeTrue();
        check.Message.Should().Contain("edge-1");
    }

    [Fact]
    public void NodeSelector_absent_yields_no_check()
    {
        var spec = SpecWithNodeSelector(new Dictionary<string, string>());

        DockerPreflight.NodeSelectorInfoCheck(spec).Should().BeNull("an empty selector has nothing to surface");
    }

    [Fact]
    public async Task Probe_with_a_nodeSelector_surfaces_the_node_selector_info_check_through_the_wired_path()
    {
        var client = ClientReturningVersion(Version(apiVersion: "1.51", arch: "amd64", os: "linux"));
        var sut = new DockerPreflight(FactoryFor(client));
        var spec = Spec(nodeSelector: new Dictionary<string, string> { ["reeve.io/ble"] = "true" });

        var result = await sut.ProbeAsync(SettingsWith(), spec, CancellationToken.None);

        var check = result.Checks.Should().ContainSingle(c => c.Id == DockerPreflight.NodeSelectorCheckId).Which;
        check.Severity.Should().Be(PreflightSeverity.Info, "a docker-honourable nodeSelector is surfaced, never blocking");
        check.Passed.Should().BeTrue();
        result.HasBlockingError.Should().BeFalse("an INFO disposition never blocks a reachable modern daemon");
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task Probe_with_mdns_or_ipv6_and_no_hostNetwork_surfaces_the_mdns_ipv6_info_check(bool mdns, bool ipv6)
    {
        var client = ClientReturningVersion(Version(apiVersion: "1.51", arch: "amd64", os: "linux"));
        var sut = new DockerPreflight(FactoryFor(client));
        var spec = Spec(network: new ResolvedNetwork { Mdns = mdns, Ipv6Required = ipv6, HostNetwork = false });

        var result = await sut.ProbeAsync(SettingsWith(), spec, CancellationToken.None);

        var check = result.Checks.Should().ContainSingle(c => c.Id == DockerPreflight.MdnsIpv6CheckId).Which;
        check.Severity.Should().Be(PreflightSeverity.Info, "mdns/ipv6 not honoured on a single docker host is surfaced, never blocking");
        check.Passed.Should().BeTrue();
        result.HasBlockingError.Should().BeFalse();
    }

    [Fact]
    public async Task Probe_with_mdns_but_hostNetwork_does_not_surface_the_mdns_ipv6_info_check()
    {
        var client = ClientReturningVersion(Version(apiVersion: "1.51", arch: "amd64", os: "linux"));
        var sut = new DockerPreflight(FactoryFor(client));
        var spec = Spec(network: new ResolvedNetwork { Mdns = true, Ipv6Required = true, HostNetwork = true });

        var result = await sut.ProbeAsync(SettingsWith(), spec, CancellationToken.None);

        result.Checks.Should().NotContain(c => c.Id == DockerPreflight.MdnsIpv6CheckId,
            "hostNetwork carries mDNS/IPv6, so the disposition does not apply");
    }

    [Fact]
    public async Task Probe_with_no_selector_and_no_mdns_surfaces_neither_info_check()
    {
        var client = ClientReturningVersion(Version(apiVersion: "1.51", arch: "amd64", os: "linux"));
        var sut = new DockerPreflight(FactoryFor(client));
        var spec = Spec(nodeSelector: new Dictionary<string, string>(), network: new ResolvedNetwork());

        var result = await sut.ProbeAsync(SettingsWith(), spec, CancellationToken.None);

        result.Checks.Should().NotContain(c => c.Id == DockerPreflight.NodeSelectorCheckId);
        result.Checks.Should().NotContain(c => c.Id == DockerPreflight.MdnsIpv6CheckId);
    }

    [Fact]
    public async Task Provider_preflight_surfaces_both_info_dispositions_on_the_live_path()
    {
        var client = ClientReturningVersion(Version(apiVersion: "1.51", arch: "amd64", os: "linux"));
        var provider = BuildProvider(client);
        var spec = Spec(
            nodeSelector: new Dictionary<string, string> { ["reeve.io/ble"] = "true" },
            network: new ResolvedNetwork { Mdns = true, HostNetwork = false });

        var result = await provider.PreflightAsync(spec, CancellationToken.None);

        result.Checks.Should().Contain(
            c => c.Id == DockerPreflight.NodeSelectorCheckId && c.Severity == PreflightSeverity.Info && c.Passed,
            "PreflightAsync must surface the nodeSelector INFO disposition");
        result.Checks.Should().Contain(
            c => c.Id == DockerPreflight.MdnsIpv6CheckId && c.Severity == PreflightSeverity.Info && c.Passed,
            "PreflightAsync must surface the mdns/ipv6 INFO disposition");
    }

    [Fact]
    public async Task Provider_preflight_omits_the_info_dispositions_when_neither_applies()
    {
        var client = ClientReturningVersion(Version(apiVersion: "1.51", arch: "amd64", os: "linux"));
        var provider = BuildProvider(client);
        var spec = Spec(nodeSelector: new Dictionary<string, string>(), network: new ResolvedNetwork { HostNetwork = true, Mdns = true });

        var result = await provider.PreflightAsync(spec, CancellationToken.None);

        result.Checks.Should().NotContain(c => c.Id == DockerPreflight.NodeSelectorCheckId);
        result.Checks.Should().NotContain(c => c.Id == DockerPreflight.MdnsIpv6CheckId);
    }

    [Fact]
    public async Task Configured_registry_credentials_each_get_a_credential_free_check()
    {
        var client = ClientReturningVersion(Version(apiVersion: "1.51", arch: "amd64", os: "linux"));
        var creds = new[]
        {
            new RegistryCredential { HostPattern = "registry.example.com:5000", Username = "reeve", Token = "SECRET-TOKEN-VALUE", AuthType = RegistryAuthType.Basic },
            new RegistryCredential { HostPattern = "localhost:5000", Username = "dev", Token = "ANOTHER-SECRET", AuthType = RegistryAuthType.Basic },
        };
        var sut = new DockerPreflight(FactoryFor(client));

        var result = await sut.ProbeAsync(SettingsWith(creds: creds), CancellationToken.None);

        var registryChecks = result.Checks.Where(c => c.Id.StartsWith(DockerPreflight.RegistryAuthCheckId, StringComparison.Ordinal)).ToList();
        registryChecks.Should().HaveCount(2);
        registryChecks.Should().Contain(c => c.Message.Contains("registry.example.com:5000"));
        registryChecks.Should().Contain(c => c.Message.Contains("localhost:5000") && c.Severity == PreflightSeverity.Warning && !c.Passed);
        result.Checks.Should().NotContain(c => c.Message.Contains("SECRET-TOKEN-VALUE") || c.Message.Contains("ANOTHER-SECRET"));
        result.RegistryPullAuthResult.Should().NotBeNullOrEmpty();
        result.RegistryPullAuthResult.Should().NotContain("SECRET-TOKEN-VALUE");
    }

    [Fact]
    public async Task Probe_adapter_reports_available_and_maps_a_reachable_daemon()
    {
        var client = ClientReturningVersion(Version(apiVersion: "1.51", arch: "amd64", os: "linux"));
        var probe = new DockerWorkloadProviderProbe(new DockerPreflight(FactoryFor(client)), NullLogger<DockerWorkloadProviderProbe>.Instance);

        probe.Provider.Should().Be(DockerProvider.Id);

        var result = await probe.ProbeAsync(SettingsWith(), CancellationToken.None);

        result.Provider.Should().Be(DockerProvider.Id);
        result.ProbeAvailable.Should().BeTrue("a docker probe IS compiled into this build");
        result.ApiServerReachable.Should().BeTrue("the daemon answered /version");
        result.DockerApiVersion.Should().Be("1.51");
    }

    [Fact]
    public async Task Probe_adapter_reports_an_unreachable_daemon_without_throwing()
    {
        var client = ClientWhoseVersionThrows(new HttpRequestException("connection refused"));
        var probe = new DockerWorkloadProviderProbe(new DockerPreflight(FactoryFor(client)), NullLogger<DockerWorkloadProviderProbe>.Instance);

        var result = await probe.ProbeAsync(SettingsWith(socketUri: "tcp://127.0.0.1:2375"), CancellationToken.None);

        result.ProbeAvailable.Should().BeTrue();
        result.ApiServerReachable.Should().BeFalse("a lost socket is reported, never thrown (SPI contract)");
    }

    [SkippableFact]
    public async Task Real_daemon_apiVersion_passes_arch_present_and_socket_reachable()
    {
        _docker.EnsureAvailable();
        var sut = new DockerPreflight(FactoryFor(_docker.Client!));

        var result = await sut.ProbeAsync(SettingsWith(), CancellationToken.None);

        result.Checks.Should().Contain(c => c.Id == DockerPreflight.ApiVersionCheckId && c.Passed,
            "the dev daemon reports an API version at or above the 1.41 floor");
        result.DockerApiVersion.Should().NotBeNullOrEmpty();
        result.Checks.Should().Contain(c => c.Id == DockerPreflight.ArchCheckId,
            "the daemon reports its GOARCH");
        result.DaemonArchitecture.Should().NotBeNullOrEmpty();
        result.Checks.Should().Contain(c => c.Id == DockerPreflight.SocketReachableCheckId && c.Passed);
        result.SocketReachable.Should().BeTrue();
        result.HasBlockingError.Should().BeFalse("a reachable modern daemon has nothing blocking");
    }

    private static WorkloadOrchestratorSettings SettingsWith(
        string? socketUri = null,
        IReadOnlyList<RegistryCredential>? creds = null)
        => new WorkloadOrchestratorSettings
        {
            Provider = DockerProvider.Id,
        }
            .WithProvider(DockerProvider.Id, new DockerProviderSettings
            {
                SocketUri = socketUri,
                RegistryCredentials = creds ?? Array.Empty<RegistryCredential>(),
            });

    private static VersionResponse Version(string apiVersion, string arch, string os) =>
        new() { APIVersion = apiVersion, Arch = arch, Os = os, Version = "test" };

    private static IDockerClientFactory FactoryFor(IDockerClient client)
    {
        var factory = Substitute.For<IDockerClientFactory>();
        factory.CreateClient(Arg.Any<string?>()).Returns(client);
        return factory;
    }

    private static IDockerClient ClientReturningVersion(VersionResponse version)
    {
        var client = Substitute.For<IDockerClient>();
        var system = Substitute.For<ISystemOperations>();
        client.System.Returns(system);
        system.GetVersionAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult(version));
        return client;
    }

    private static IDockerClient ClientWhoseVersionThrows(Exception ex)
    {
        var client = Substitute.For<IDockerClient>();
        var system = Substitute.For<ISystemOperations>();
        client.System.Returns(system);
        Func<CallInfo, Task<VersionResponse>> thrower = _ => throw ex;
        system.GetVersionAsync(Arg.Any<CancellationToken>()).Returns(thrower);
        return client;
    }

    private static WorkloadDesiredSpec SpecWithNodeSelector(IReadOnlyDictionary<string, string> nodeSelector)
        => Spec(nodeSelector: nodeSelector);

    private static WorkloadDesiredSpec Spec(
        IReadOnlyDictionary<string, string>? nodeSelector = null,
        ResolvedNetwork? network = null)
    {
        const string specHash = "abc123def456";
        var instanceId = Guid.Parse("11111111-2222-3333-4444-555555555555");
        var labels = DefaultWorkloadResourceNamer.Instance.Labels(new WorkloadLabelSubject
                     {
                         InstanceId = instanceId,
                         InstallationId = "install-1",
                         SpecHash = specHash,
                     });

        return new WorkloadDesiredSpec
        {
            GroupInstanceId = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee"),
            GroupId = "com.reeve.matter",
            WorkloadId = "matter",
            InstanceId = instanceId,
            Inst8 = "11111111",
            Provider = DockerProvider.Id,
            ResourceName = "reeve-wl-com-reeve-matter-matter-11111111",
            SecretName = "reeve-wl-com-reeve-matter-matter-11111111",
            Labels = labels,
            Image = "ghcr.io/reeve/matter@sha256:1111111111111111111111111111111111111111111111111111111111111111",
            Platforms = Array.Empty<string>(),
            Kind = WorkloadKind.SingleNode,
            NodeSelector = nodeSelector ?? new Dictionary<string, string>(),
            Network = network,
            Health = new WorkloadHealth { Mode = WorkloadHealthMode.Container },
            SpecHash = specHash,
        };
    }

    private static DockerWorkloadProvider BuildProvider(IDockerClient client)
    {
        var containerManager = new DockerContainerManager();
        return new DockerWorkloadProvider(
            clientFactory: FactoryFor(client),
            imagePuller: new DockerImagePuller(),
            networkManager: new DockerNetworkManager(),
            volumeManager: new DockerVolumeManager(),
            containerManager: containerManager,
            privilegeRenderer: new DockerPrivilegeRenderer(),
            resourceRenderer: new DockerResourceRenderer(),
            deploymentValidator: new DockerDeploymentValidator(),
            daemonGate: new DockerDaemonGate(),
            journaledRecreate: new DockerJournaledRecreate(containerManager),
            logReader: new DockerLogReader(),
            rollbackCoordinator: new DockerRollbackCoordinator(),
            statusMapper: new DockerStatusMapper(),
            scopeFactory: DockerProviderScope.For(new FakePreflightSettingsService(), new ThrowingCredentialPort()),
            logger: NullLogger<DockerWorkloadProvider>.Instance);
    }

    private sealed class FakePreflightSettingsService : IWorkloadOrchestratorSettingsService, IWorkloadOrchestratorSecretReader
    {
        public Task<WorkloadOrchestratorSettingsResponse> GetAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(new WorkloadOrchestratorSettingsResponse
            {
                Settings = new WorkloadOrchestratorSettings
                {
                    Provider = DockerProvider.Id,
                }
                    .WithProvider(DockerProvider.Id, new DockerProviderSettings { SocketUri = null }),
                EffectiveProvider = DockerProvider.Id,
                AutoDetected = false,
                Providers = Array.Empty<WorkloadProviderInfo>(),
            });

        public async Task<WorkloadOrchestratorSettings> GetDecryptedSettingsAsync(
            CancellationToken cancellationToken = default)
            => (await GetAsync(cancellationToken)).Settings;


        public Task<WorkloadProviderProbeResult> TestAsync(
            WorkloadProviderId? requestedProvider, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }

    private sealed class ThrowingCredentialPort : IWorkloadCredentialPort
    {
        public Task<string?> ResolveOrMintAsync(
            Guid instanceId, string credentialId, string? existingMaterial, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task RevokeAllAsync(
            Guid instanceId, Guid? actorUserId = null, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }
}
