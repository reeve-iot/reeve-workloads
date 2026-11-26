using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Docker.DotNet;
using Docker.DotNet.Models;
using FluentAssertions;
using NSubstitute;
using NSubstitute.Core;
using Reeve.Workloads.Abstractions.Contracts;
using Reeve.Workloads.Abstractions.Contracts.Deployment;
using Reeve.Workloads.Abstractions.Domain;
using Reeve.Workloads.Abstractions.Domain.DesiredSpec;
using Reeve.Workloads.Abstractions.Providers;
using Reeve.Workloads.Domain;
using Reeve.Workloads.Providers.Docker;

namespace Reeve.Workloads.Providers.Docker.IntegrationTests;

[Collection(DockerIntegrationCollection.Name)]
public sealed class DockerDaemonGateTests
{
    private readonly DockerDaemonFixture _docker;
    private readonly DockerDaemonGate _sut = new();

    public DockerDaemonGateTests(DockerDaemonFixture docker) => _docker = docker;

    [Theory]
    [InlineData("linux/arm/v7", "arm")]
    [InlineData("linux/amd64", "amd64")]
    [InlineData("linux/arm64", "arm64")]
    [InlineData("linux/arm64/v8", "arm64")]
    [InlineData("LINUX/AMD64", "amd64")]
    public void ExtractPlatformArch_takes_segment_1_not_the_variant(string platform, string expected)
    {
        DockerDaemonGate.ExtractPlatformArch(platform).Should().Be(expected);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("linux")]
    public void ExtractPlatformArch_returns_null_without_an_arch_segment(string? platform)
    {
        DockerDaemonGate.ExtractPlatformArch(platform).Should().BeNull();
    }

    [Theory]
    [InlineData("x86_64", "amd64")]
    [InlineData("aarch64", "arm64")]
    [InlineData("armv7l", "arm")]
    [InlineData("armv6l", "arm")]
    [InlineData("amd64", "amd64")]
    [InlineData("arm64", "arm64")]
    public void NormalizeArch_maps_uname_strings_to_goarch(string input, string expected)
    {
        DockerDaemonGate.NormalizeArch(input).Should().Be(expected);
    }

    [Fact]
    public async Task Unreachable_daemon_is_reported_as_a_blocking_check_never_thrown()
    {
        var client = FakeDaemon(
            version: _ => throw new HttpRequestException("connection refused"),
            info: _ => throw new HttpRequestException("connection refused"));

        var spec = Spec(platforms: new[] { "linux/amd64" });

        var checks = await _sut.EvaluateAsync(client, spec, CancellationToken.None);

        var reachable = checks.Should().ContainSingle(c => c.Id == DockerDaemonGate.ReachableCheckId).Which;
        reachable.Passed.Should().BeFalse();
        reachable.Severity.Should().Be(PreflightSeverity.Error, "an unreachable daemon blocks the apply (retryable)");
    }

    [Fact]
    public async Task HostNetwork_on_a_non_linux_daemon_is_a_blocking_error()
    {
        var client = FakeDaemon(
            version: new VersionResponse { Os = "windows", Arch = "amd64" },
            info: new SystemInfoResponse { OSType = "windows", Architecture = "x86_64" });

        var spec = Spec(network: new ResolvedNetwork { HostNetwork = true });

        var checks = await _sut.EvaluateAsync(client, spec, CancellationToken.None);

        checks.Should().ContainSingle(c => c.Id == DockerDaemonGate.HostNetworkNonLinuxCheckId)
            .Which.Severity.Should().Be(PreflightSeverity.Error);
    }

    [Fact]
    public async Task Arch_mismatch_when_no_platform_matches_the_daemon_goarch()
    {
        var client = FakeDaemon(
            version: new VersionResponse { Os = "linux", Arch = "amd64" },
            info: new SystemInfoResponse { OSType = "linux", Architecture = "x86_64" });

        var spec = Spec(platforms: new[] { "linux/arm64" });

        var checks = await _sut.EvaluateAsync(client, spec, CancellationToken.None);

        checks.Should().ContainSingle(c => c.Id == DockerDaemonGate.ArchMismatchCheckId)
            .Which.Severity.Should().Be(PreflightSeverity.Error);
        checks.Should().NotContain(c => c.Id == DockerDaemonGate.ArchMatchCheckId);
    }

    [Fact]
    public async Task Daemon_goarch_prefers_version_arch_over_the_systeminfo_uname()
    {
        var client = FakeDaemon(
            version: new VersionResponse { Os = "linux", Arch = "arm64" },
            info: new SystemInfoResponse { OSType = "linux", Architecture = "x86_64" });

        var spec = Spec(platforms: new[] { "linux/arm64" });

        var checks = await _sut.EvaluateAsync(client, spec, CancellationToken.None);

        checks.Should().Contain(c => c.Id == DockerDaemonGate.ArchMatchCheckId && c.Passed);
        checks.Should().NotContain(c => c.Id == DockerDaemonGate.ArchMismatchCheckId);
    }

    [Fact]
    public async Task Daemon_goarch_falls_back_to_the_normalized_uname_when_version_arch_is_absent()
    {
        var client = FakeDaemon(
            version: new VersionResponse { Os = "linux", Arch = "" },
            info: new SystemInfoResponse { OSType = "linux", Architecture = "aarch64" });

        var matching = await _sut.EvaluateAsync(client, Spec(platforms: new[] { "linux/arm64" }), CancellationToken.None);
        matching.Should().Contain(c => c.Id == DockerDaemonGate.ArchMatchCheckId && c.Passed);

        var mismatching = await _sut.EvaluateAsync(client, Spec(platforms: new[] { "linux/amd64" }), CancellationToken.None);
        mismatching.Should().Contain(c => c.Id == DockerDaemonGate.ArchMismatchCheckId && !c.Passed);
    }

    [Fact]
    public async Task Undeterminable_daemon_arch_yields_a_non_blocking_unverifiable_warning()
    {
        var client = FakeDaemon(
            version: new VersionResponse { Os = "linux", Arch = "" },
            info: new SystemInfoResponse { OSType = "linux", Architecture = "" });

        var spec = Spec(platforms: new[] { "linux/amd64" });

        var checks = await _sut.EvaluateAsync(client, spec, CancellationToken.None);

        var unverifiable = checks.Should().ContainSingle(c => c.Id == DockerDaemonGate.ArchUnverifiableCheckId).Which;
        unverifiable.Severity.Should().Be(PreflightSeverity.Warning, "an unverifiable arch must not block the apply");
        checks.Should().NotContain(c => c.Severity == PreflightSeverity.Error, "no error is raised when arch is merely unverifiable");
    }

    [Fact]
    public async Task Empty_platforms_produce_no_arch_check()
    {
        var client = FakeDaemon(
            version: new VersionResponse { Os = "linux", Arch = "amd64" },
            info: new SystemInfoResponse { OSType = "linux", Architecture = "x86_64" });

        var spec = Spec(platforms: Array.Empty<string>());

        var checks = await _sut.EvaluateAsync(client, spec, CancellationToken.None);

        checks.Should().NotContain(c => c.Id.StartsWith("docker-arch", StringComparison.Ordinal),
            "with no declared platforms there is no arch constraint to gate on");
    }

    [Theory]
    [InlineData("macvlan")]
    [InlineData("ipvlan")]
    [InlineData("bridge")]
    public async Task An_attachment_on_a_non_linux_daemon_raises_no_attachment_check_and_reads_no_network(string driver)
    {
        var client = FakeDaemon(
            version: new VersionResponse { Os = "windows", Arch = "amd64" },
            info: new SystemInfoResponse { OSType = "windows", Architecture = "x86_64" },
            networks: [Network("iot-attachment", driver)]);

        var spec = Spec(network: OperatorNetwork("iot-attachment"));

        var checks = await _sut.EvaluateAsync(client, spec, CancellationToken.None);

        checks.Should().NotContain(c => c.Id.StartsWith("docker-attachment-driver", StringComparison.Ordinal));
        checks.Should().NotContain(c => !c.Passed && c.Severity == PreflightSeverity.Error);
        await client.Networks.DidNotReceive().ListNetworksAsync(
            Arg.Any<NetworksListParameters>(), Arg.Any<CancellationToken>());
    }

    [SkippableFact]
    public async Task Platform_matching_the_real_daemon_arch_has_no_blocking_check()
    {
        _docker.EnsureAvailable();
        var client = _docker.Client!;

        var daemonArch = await DaemonGoArchAsync(client);
        var spec = Spec(platforms: new[] { $"linux/{daemonArch}" });

        var checks = await _sut.EvaluateAsync(client, spec, CancellationToken.None);

        checks.Should().NotContain(c => !c.Passed && c.Severity == PreflightSeverity.Error,
            "the image platform matches the daemon GOARCH, so nothing blocks the apply");
        checks.Should().Contain(c => c.Id == DockerDaemonGate.ArchMatchCheckId && c.Passed);
    }

    [SkippableFact]
    public async Task Foreign_platform_mismatches_the_real_daemon_with_a_blocking_error()
    {
        _docker.EnsureAvailable();
        var client = _docker.Client!;

        var daemonArch = await DaemonGoArchAsync(client);
        var foreignArch = string.Equals(daemonArch, "amd64", StringComparison.OrdinalIgnoreCase) ? "arm64" : "amd64";
        var spec = Spec(platforms: new[] { $"linux/{foreignArch}" });

        var checks = await _sut.EvaluateAsync(client, spec, CancellationToken.None);

        checks.Should().ContainSingle(c => c.Id == DockerDaemonGate.ArchMismatchCheckId)
            .Which.Severity.Should().Be(PreflightSeverity.Error);
    }

    [SkippableFact]
    public async Task Linux_amd64_platform_has_no_mismatch_on_an_amd64_daemon()
    {
        _docker.EnsureAvailable();
        var client = _docker.Client!;

        var daemonArch = await DaemonGoArchAsync(client);
        Skip.IfNot(string.Equals(daemonArch, "amd64", StringComparison.OrdinalIgnoreCase),
            $"this literal-acceptance test targets an amd64 daemon; this daemon is '{daemonArch}'.");

        var checks = await _sut.EvaluateAsync(client, Spec(platforms: new[] { "linux/amd64" }), CancellationToken.None);

        checks.Should().NotContain(c => c.Id == DockerDaemonGate.ArchMismatchCheckId);
        checks.Should().NotContain(c => !c.Passed && c.Severity == PreflightSeverity.Error);
    }

    [SkippableFact]
    public async Task Linux_arm64_platform_mismatches_an_amd64_daemon()
    {
        _docker.EnsureAvailable();
        var client = _docker.Client!;

        var daemonArch = await DaemonGoArchAsync(client);
        Skip.IfNot(string.Equals(daemonArch, "amd64", StringComparison.OrdinalIgnoreCase),
            $"this literal-acceptance test targets an amd64 daemon; this daemon is '{daemonArch}'.");

        var checks = await _sut.EvaluateAsync(client, Spec(platforms: new[] { "linux/arm64" }), CancellationToken.None);

        checks.Should().ContainSingle(c => c.Id == DockerDaemonGate.ArchMismatchCheckId)
            .Which.Severity.Should().Be(PreflightSeverity.Error);
    }

    [SkippableFact]
    public async Task Empty_platforms_produce_no_arch_check_against_the_real_daemon()
    {
        _docker.EnsureAvailable();
        var client = _docker.Client!;

        var checks = await _sut.EvaluateAsync(client, Spec(platforms: Array.Empty<string>()), CancellationToken.None);

        checks.Should().NotContain(c => c.Id.StartsWith("docker-arch", StringComparison.Ordinal));
    }

    [SkippableFact]
    public async Task HostNetwork_on_the_linux_dev_daemon_raises_no_non_linux_error()
    {
        _docker.EnsureAvailable();
        var client = _docker.Client!;

        var spec = Spec(network: new ResolvedNetwork { HostNetwork = true });

        var checks = await _sut.EvaluateAsync(client, spec, CancellationToken.None);

        checks.Should().NotContain(c => c.Id == DockerDaemonGate.HostNetworkNonLinuxCheckId,
            "the dev daemon is Linux, where host networking is supported");
        checks.Should().Contain(c => c.Id == DockerDaemonGate.HostNetworkOsCheckId && c.Passed);
    }

    private static async Task<string> DaemonGoArchAsync(IDockerClient client)
    {
        var version = await client.System.GetVersionAsync(CancellationToken.None);
        var info = await client.System.GetSystemInfoAsync(CancellationToken.None);
        return DockerDaemonGate.ResolveDaemonGoArch(version, info)
            ?? throw new InvalidOperationException("the local docker daemon reported no architecture");
    }

    private static IDockerClient FakeDaemon(VersionResponse version, SystemInfoResponse info)
    {
        var client = Substitute.For<IDockerClient>();
        var system = Substitute.For<ISystemOperations>();
        client.System.Returns(system);
        system.GetVersionAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult(version));
        system.GetSystemInfoAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult(info));
        return client;
    }

    private static IDockerClient FakeDaemon(
        VersionResponse version,
        SystemInfoResponse info,
        IReadOnlyList<NetworkResponse>? networks)
    {
        var client = FakeDaemon(version, info);
        var operations = Substitute.For<INetworkOperations>();
        client.Networks.Returns(operations);

        if (networks is null)
        {
            operations.ListNetworksAsync(Arg.Any<NetworksListParameters>(), Arg.Any<CancellationToken>())
                .Returns<Task<IList<NetworkResponse>>>(_ => throw new HttpRequestException("connection reset"));
        }
        else
        {
            operations.ListNetworksAsync(Arg.Any<NetworksListParameters>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult<IList<NetworkResponse>>(networks.ToList()));
        }

        return client;
    }

    private static NetworkResponse Network(string name, string driver) =>
        new() { Name = name, Driver = driver, Scope = "local" };

    private static ResolvedNetwork OperatorNetwork(params string[] attachments) =>
        new() { Mode = WorkloadNetworkMode.OperatorNetwork, Attachments = attachments };

    private static IDockerClient FakeDaemon(
        Func<CallInfo, Task<VersionResponse>> version,
        Func<CallInfo, Task<SystemInfoResponse>> info)
    {
        var client = Substitute.For<IDockerClient>();
        var system = Substitute.For<ISystemOperations>();
        client.System.Returns(system);
        system.GetVersionAsync(Arg.Any<CancellationToken>()).Returns(version);
        system.GetSystemInfoAsync(Arg.Any<CancellationToken>()).Returns(info);
        return client;
    }

    private static WorkloadDesiredSpec Spec(
        IReadOnlyList<string>? platforms = null,
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
            Platforms = platforms ?? Array.Empty<string>(),
            Kind = WorkloadKind.SingleNode,
            Network = network,
            Health = new WorkloadHealth { Mode = WorkloadHealthMode.Container },
            SpecHash = specHash,
        };
    }
}
