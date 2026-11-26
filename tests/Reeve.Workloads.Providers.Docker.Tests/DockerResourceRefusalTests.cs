using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Docker.DotNet;
using Docker.DotNet.Models;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Reeve.Workloads.Abstractions;
using Reeve.Workloads.Abstractions.Contracts;
using Reeve.Workloads.Abstractions.Contracts.Deployment;
using Reeve.Workloads.Abstractions.Domain;
using Reeve.Workloads.Abstractions.Domain.DesiredSpec;
using Reeve.Workloads.Abstractions.Providers;
using Reeve.Workloads.Domain;
using Reeve.Workloads.Providers.Docker;
using Xunit;

namespace Reeve.Workloads.Providers.Docker.Tests;

public sealed class DockerResourceRefusalTests
{
    private static readonly Guid InstanceId = Guid.Parse("11111111-2222-3333-4444-555555555555");
    private const string TargetSpecHash = "abc123def456";

    [Fact]
    public async Task A_workload_with_an_unrenderable_limit_is_REJECTED_not_reported_as_an_unreachable_daemon()
    {
        var provider = BuildProvider();

        var result = await provider.ApplyAsync(Spec(new ResolvedResources { LimitsMemory = "64 MB" }), progress: null);

        result.Succeeded.Should().BeFalse();
        result.Reason.Should().Be(WorkloadApplyReason.Rejected);
        result.Message.Should().Contain("64 MB");
    }

    [Fact]
    public async Task A_renderable_limit_does_not_trip_the_refusal_path()
    {
        var provider = BuildProvider();

        var result = await provider.ApplyAsync(Spec(new ResolvedResources { LimitsMemory = "512Mi" }), progress: null);

        result.Reason.Should().NotBe(WorkloadApplyReason.Rejected);
    }

    private static WorkloadDesiredSpec Spec(ResolvedResources resources) => new()
    {
        GroupInstanceId = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee"),
        GroupId = "com.reeve.matter",
        WorkloadId = "matter",
        InstanceId = InstanceId,
        Inst8 = "11111111",
        Provider = DockerProvider.Id,
        ResourceName = "wl-matter",
        SecretName = "wl-matter-secret",
        Labels = DefaultWorkloadResourceNamer.Instance.Labels(new WorkloadLabelSubject
                 {
                     InstanceId = InstanceId,
                     InstallationId = "install-1",
                     SpecHash = TargetSpecHash,
                 }),
        Image = "ghcr.io/reeve/matter@sha256:1111111111111111111111111111111111111111111111111111111111111111",
        Kind = WorkloadKind.SingleNode,
        Resources = resources,
        Health = new WorkloadHealth { Mode = WorkloadHealthMode.Container },
        SpecHash = TargetSpecHash,
    };

    private static DockerWorkloadProvider BuildProvider()
    {
        var settingsService = Substitute.For<IWorkloadOrchestratorSecretReader>();
        settingsService.GetDecryptedSettingsAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new WorkloadOrchestratorSettings()));

        var credentials = Substitute.For<IWorkloadCredentialPort>();
        credentials.ResolveOrMintAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<string?>("rwt_test"));

        var serviceProvider = Substitute.For<IServiceProvider>();
        serviceProvider.GetService(typeof(IWorkloadOrchestratorSecretReader)).Returns(settingsService);
        serviceProvider.GetService(typeof(IWorkloadCredentialPort)).Returns(credentials);
        var scope = Substitute.For<IServiceScope>();
        scope.ServiceProvider.Returns(serviceProvider);
        var scopeFactory = Substitute.For<IServiceScopeFactory>();
        scopeFactory.CreateScope().Returns(scope);

        var clientFactory = Substitute.For<IDockerClientFactory>();
        clientFactory.CreateClient(Arg.Any<string>()).Returns(Substitute.For<IDockerClient>());

        var validator = Substitute.For<IDockerDeploymentValidator>();
        validator.Validate(Arg.Any<WorkloadDesiredSpec>()).Returns(Array.Empty<PreflightCheck>());

        var gate = Substitute.For<IDockerDaemonGate>();
        gate.EvaluateAsync(Arg.Any<IDockerClient>(), Arg.Any<WorkloadDesiredSpec>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<PreflightCheck>>(Array.Empty<PreflightCheck>()));

        var puller = Substitute.For<IDockerImagePuller>();
        puller.PullAsync(
                Arg.Any<IDockerClient>(), Arg.Any<string>(), Arg.Any<DockerProviderSettings>(),
                Arg.Any<IProgress<DockerPullProgress>>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(DockerOperationResult.Ok()));

        var networks = Substitute.For<IDockerNetworkManager>();
        networks.EnsureSharedNetworkAsync(
                Arg.Any<IDockerClient>(), Arg.Any<DockerProviderSettings>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(DockerNetworkEnsureResult.Ok("reeve-workloads")));
        networks.RenderNetworking(Arg.Any<WorkloadDesiredSpec>(), Arg.Any<string>())
            .Returns(call => new DockerNetworkManager()
                .RenderNetworking(call.Arg<WorkloadDesiredSpec>(), call.ArgAt<string>(1)));

        var volumes = Substitute.For<IDockerVolumeManager>();
        volumes.EnsureVolumesAsync(
                Arg.Any<IDockerClient>(), Arg.Any<WorkloadDesiredSpec>(), Arg.Any<DockerProviderSettings>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(DockerOperationResult.Ok()));

        var privilege = Substitute.For<IDockerPrivilegeRenderer>();
        privilege.RenderPrivileges(Arg.Any<WorkloadDesiredSpec>())
            .Returns(new DockerPrivilegeRendering { HostConfig = new HostConfig() });

        return new DockerWorkloadProvider(
            clientFactory,
            puller,
            networks,
            volumes,
            Substitute.For<IDockerContainerManager>(),
            privilege,
            new DockerResourceRenderer(),
            validator,
            gate,
            Substitute.For<IDockerJournaledRecreate>(),
            Substitute.For<IDockerLogReader>(),
            Substitute.For<IDockerRollbackCoordinator>(),
            Substitute.For<IDockerStatusMapper>(),
            scopeFactory,
            NullLogger<DockerWorkloadProvider>.Instance);
    }
}
