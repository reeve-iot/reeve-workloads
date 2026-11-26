using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Docker.DotNet;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Reeve.Workloads.Abstractions;
using Reeve.Workloads.Abstractions.Contracts;
using Reeve.Workloads.Abstractions.Providers;
using Reeve.Workloads.Providers.Docker;
using Xunit;

namespace Reeve.Workloads.Providers.Docker.Tests;

public sealed class DockerTeardownTests
{
    private static readonly Guid InstanceId = Guid.Parse("11111111-2222-3333-4444-555555555555");

    private readonly IDockerContainerManager containers = Substitute.For<IDockerContainerManager>();
    private readonly IDockerVolumeManager volumes = Substitute.For<IDockerVolumeManager>();

    public DockerTeardownTests()
    {
        containers.FindContainersAsync(Arg.Any<IDockerClient>(), Arg.Any<WorkloadRef>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<DockerContainerRef>>([new DockerContainerRef { Id = "c1", Name = "wl-matter" }]));
        containers.StopContainerAsync(Arg.Any<IDockerClient>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(DockerOperationResult.Ok()));
        containers.RemoveContainerAsync(Arg.Any<IDockerClient>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(DockerOperationResult.Ok()));
        volumes.RemoveVolumesAsync(Arg.Any<IDockerClient>(), Arg.Any<WorkloadRef>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(DockerOperationResult.Ok()));
    }

    [Fact]
    public async Task A_teardown_that_removed_everything_returns()
    {
        await FluentActions.Awaiting(() => BuildProvider().TeardownAsync(Reference(), retainVolumes: false))
            .Should().NotThrowAsync();
    }

    [Fact]
    public async Task An_unreachable_daemon_fails_the_teardown()
    {
        containers.FindContainersAsync(Arg.Any<IDockerClient>(), Arg.Any<WorkloadRef>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new HttpRequestException("connection refused"));

        await FluentActions.Awaiting(() => BuildProvider().TeardownAsync(Reference(), retainVolumes: true))
            .Should().ThrowAsync<HttpRequestException>();
    }

    [Fact]
    public async Task Every_container_carrying_the_instance_label_is_removed()
    {
        containers.FindContainersAsync(Arg.Any<IDockerClient>(), Arg.Any<WorkloadRef>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<DockerContainerRef>>(
            [
                new DockerContainerRef { Id = "c1", Name = "wl-matter" },
                new DockerContainerRef { Id = "c2", Name = "wl-matter-prev-abc" },
            ]));

        await BuildProvider().TeardownAsync(Reference(), retainVolumes: true);

        await containers.Received(1).RemoveContainerAsync(Arg.Any<IDockerClient>(), "c1", Arg.Any<CancellationToken>());
        await containers.Received(1).RemoveContainerAsync(Arg.Any<IDockerClient>(), "c2", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_container_the_daemon_refused_to_remove_fails_the_teardown()
    {
        containers.RemoveContainerAsync(Arg.Any<IDockerClient>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(DockerOperationResult.Fail(DockerFailureKind.Error, "removal in progress")));

        await FluentActions.Awaiting(() => BuildProvider().TeardownAsync(Reference(), retainVolumes: true))
            .Should().ThrowAsync<InvalidOperationException>().WithMessage("*removal in progress*");
    }

    [Fact]
    public async Task A_volume_the_daemon_refused_to_remove_fails_a_purging_teardown()
    {
        volumes.RemoveVolumesAsync(Arg.Any<IDockerClient>(), Arg.Any<WorkloadRef>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(DockerOperationResult.Fail(DockerFailureKind.Error, "volume is in use")));

        await FluentActions.Awaiting(() => BuildProvider().TeardownAsync(Reference(), retainVolumes: false))
            .Should().ThrowAsync<InvalidOperationException>().WithMessage("*volume is in use*");
    }

    private static WorkloadRef Reference() => new()
    {
        InstanceId = InstanceId,
        GroupId = "com.reeve.matter",
        WorkloadId = "matter",
        Inst8 = "11111111",
        Provider = DockerProvider.Id,
    };

    private DockerWorkloadProvider BuildProvider()
    {
        var settingsService = Substitute.For<IWorkloadOrchestratorSecretReader>();
        settingsService.GetDecryptedSettingsAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new WorkloadOrchestratorSettings()));

        var serviceProvider = Substitute.For<IServiceProvider>();
        serviceProvider.GetService(typeof(IWorkloadOrchestratorSecretReader)).Returns(settingsService);
        var scope = Substitute.For<IServiceScope>();
        scope.ServiceProvider.Returns(serviceProvider);
        var scopeFactory = Substitute.For<IServiceScopeFactory>();
        scopeFactory.CreateScope().Returns(scope);

        var clientFactory = Substitute.For<IDockerClientFactory>();
        clientFactory.CreateClient(Arg.Any<string>()).Returns(Substitute.For<IDockerClient>());

        return new DockerWorkloadProvider(
            clientFactory,
            Substitute.For<IDockerImagePuller>(),
            Substitute.For<IDockerNetworkManager>(),
            volumes,
            containers,
            Substitute.For<IDockerPrivilegeRenderer>(),
            new DockerResourceRenderer(),
            Substitute.For<IDockerDeploymentValidator>(),
            Substitute.For<IDockerDaemonGate>(),
            Substitute.For<IDockerJournaledRecreate>(),
            Substitute.For<IDockerLogReader>(),
            Substitute.For<IDockerRollbackCoordinator>(),
            Substitute.For<IDockerStatusMapper>(),
            scopeFactory,
            NullLogger<DockerWorkloadProvider>.Instance);
    }
}
