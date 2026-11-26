using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
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

namespace Reeve.Workloads.Providers.Docker.IntegrationTests;

[Collection(DockerIntegrationCollection.Name)]
public sealed class DockerSuspendTeardownTests : IAsyncLifetime
{
    private const string GroupId = "com.reeve.suspend-test";
    private const string WorkloadId = "st";

    private const string BusyboxRepository = "busybox";
    private const string BusyboxTag = "1.36";
    private const string BusyboxImage = BusyboxRepository + ":" + BusyboxTag;

    private readonly DockerDaemonFixture _docker;

    private readonly DockerClientFactory _factory = new(NullLogger<DockerClientFactory>.Instance);

    private readonly List<string> _containersToCleanup = new();
    private readonly List<string> _volumesToCleanup = new();
    private readonly List<string> _networksToCleanup = new();

    public DockerSuspendTeardownTests(DockerDaemonFixture docker) => _docker = docker;

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        try
        {
            var client = _factory.CreateClient(null);

            foreach (var id in _containersToCleanup.Distinct())
            {
                await SafeAsync(() => client.Containers.RemoveContainerAsync(
                    id, new ContainerRemoveParameters { Force = true }, CancellationToken.None));
            }

            foreach (var name in _volumesToCleanup.Distinct())
            {
                await SafeAsync(() => client.Volumes.RemoveAsync(name, force: true, CancellationToken.None));
            }

            foreach (var id in _networksToCleanup.Distinct())
            {
                await SafeAsync(() => client.Networks.DeleteNetworkAsync(id, CancellationToken.None));
            }
        }
        catch (Exception)
        {
        }
        finally
        {
            _factory.Dispose();
        }
    }

    [SkippableFact]
    public async Task Suspend_stops_without_removing_then_resume_restarts_and_both_are_idempotent()
    {
        var client = await ArrangeDaemonAsync();

        var instanceId = Guid.NewGuid();
        var reference = ReferenceFor(instanceId);
        var provider = BuildProvider();

        var (spec, containerName, volumeName) = BuildSpec(instanceId);
        await EnsureVolumeAsync(client, spec, volumeName);
        var containerId = await CreateRunningContainerAsync(client, spec, containerName);

        (await IsRunningAsync(client, containerId)).Should().BeTrue("precondition: the workload container is running");

        await provider.SuspendAsync(reference, CancellationToken.None);
        (await IsRunningAsync(client, containerId)).Should().BeFalse("suspend stops the container");
        (await ContainerExistsAsync(client, containerId)).Should().BeTrue("suspend must NOT remove the container");
        (await VolumeExistsAsync(client, volumeName)).Should().BeTrue("suspend must NOT remove the named volume");

        await provider.SuspendAsync(reference, CancellationToken.None);
        (await IsRunningAsync(client, containerId)).Should().BeFalse("a second suspend is an idempotent no-op");

        await provider.ResumeAsync(reference, CancellationToken.None);
        (await IsRunningAsync(client, containerId)).Should().BeTrue("resume starts the suspended container again");

        await provider.ResumeAsync(reference, CancellationToken.None);
        (await IsRunningAsync(client, containerId)).Should().BeTrue("a second resume is an idempotent no-op");
    }

    [SkippableFact]
    public async Task Provider_container_has_unless_stopped_policy_and_a_suspended_container_stays_stopped()
    {
        var client = await ArrangeDaemonAsync();

        var instanceId = Guid.NewGuid();
        var reference = ReferenceFor(instanceId);
        var provider = BuildProvider();

        var (spec, containerName, _) = BuildSpec(instanceId);
        var containerId = await CreateRunningContainerAsync(client, spec, containerName);

        var inspected = await client.Containers.InspectContainerAsync(containerId, CancellationToken.None);
        inspected.HostConfig.Should().NotBeNull();
        inspected.HostConfig!.RestartPolicy.Should().NotBeNull();
        inspected.HostConfig.RestartPolicy!.Name.Should().Be(
            RestartPolicyKind.UnlessStopped, "the provider builds workloads with an unless-stopped restart policy");

        await provider.SuspendAsync(reference, CancellationToken.None);
        (await IsRunningAsync(client, containerId)).Should().BeFalse("suspend stopped the container");

        await Task.Delay(TimeSpan.FromMilliseconds(1200));
        (await IsRunningAsync(client, containerId)).Should().BeFalse(
            "unless-stopped honours an explicit stop — a suspended container stays down");
    }

    [SkippableFact]
    public async Task Teardown_retaining_volumes_removes_the_container_but_keeps_the_named_volume()
    {
        var client = await ArrangeDaemonAsync();

        var instanceId = Guid.NewGuid();
        var reference = ReferenceFor(instanceId);
        var provider = BuildProvider();

        var (spec, containerName, volumeName) = BuildSpec(instanceId);
        await EnsureVolumeAsync(client, spec, volumeName);
        var containerId = await CreateRunningContainerAsync(client, spec, containerName);

        await provider.TeardownAsync(reference, retainVolumes: true, CancellationToken.None);

        (await ContainerExistsAsync(client, containerId)).Should().BeFalse("teardown removes this instance's container");
        (await FindByInstanceLabelAsync(client, reference)).Should().BeNull(
            "the removed container is no longer addressable by the instance label");
        (await VolumeExistsAsync(client, volumeName)).Should().BeTrue(
            "retainVolumes=true keeps the named volume (data survives teardown)");
    }

    [SkippableFact]
    public async Task Teardown_not_retaining_volumes_removes_our_volume_but_never_another_instances()
    {
        var client = await ArrangeDaemonAsync();

        var instanceId = Guid.NewGuid();
        var reference = ReferenceFor(instanceId);
        var provider = BuildProvider();

        var (spec, containerName, ourVolume) = BuildSpec(instanceId);
        await EnsureVolumeAsync(client, spec, ourVolume);
        var containerId = await CreateRunningContainerAsync(client, spec, containerName);

        var otherId = Guid.NewGuid();
        var otherVolume = DockerResourceNamer.Instance.VolumeName(
                            new WorkloadNamingSubject
                            {
                                GroupId = GroupId,
                                WorkloadId = string.Empty,
                                Inst8 = Inst8(otherId),
                            },
                            "data");
        _volumesToCleanup.Add(otherVolume);
        await client.Volumes.CreateAsync(
            new VolumesCreateParameters
            {
                Name = otherVolume,
                Labels = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    [DockerProviderConstants.InstanceLabelKey] = otherId.ToString("D"),
                },
            },
            CancellationToken.None);

        await provider.TeardownAsync(reference, retainVolumes: false, CancellationToken.None);

        (await ContainerExistsAsync(client, containerId)).Should().BeFalse("teardown removes this instance's container");
        (await VolumeExistsAsync(client, ourVolume)).Should().BeFalse(
            "retainVolumes=false removes this instance's named volume");
        (await VolumeExistsAsync(client, otherVolume)).Should().BeTrue(
            "a volume labelled with a DIFFERENT instance id must survive teardown (cross-claim refusal)");
    }

    [SkippableFact]
    public async Task Teardown_leaves_the_shared_network_in_place()
    {
        var client = await ArrangeDaemonAsync();

        var instanceId = Guid.NewGuid();
        var reference = ReferenceFor(instanceId);
        var provider = BuildProvider();

        var networkName = $"reeve-it-shared-{Inst8(instanceId)}-{Guid.NewGuid():N}"[..40];
        var network = await client.Networks.CreateNetworkAsync(
            new NetworksCreateParameters
            {
                Name = networkName,
                Driver = "bridge",
                Labels = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    [DockerProviderConstants.ManagedByLabelKey] = DockerProviderConstants.ManagedByLabelValue,
                },
            },
            CancellationToken.None);
        _networksToCleanup.Add(network.ID);

        var (spec, containerName, _) = BuildSpec(instanceId);
        var containerId = await CreateRunningContainerAsync(client, spec, containerName, networkName);

        await provider.TeardownAsync(reference, retainVolumes: false, CancellationToken.None);

        (await ContainerExistsAsync(client, containerId)).Should().BeFalse("teardown removes the instance's container");
        (await NetworkExistsAsync(client, network.ID)).Should().BeTrue(
            "teardown NEVER removes the shared network — it is shared across every workload");
    }

    private DockerWorkloadProvider BuildProvider() => new(
        clientFactory: _factory,
        imagePuller: Substitute.For<IDockerImagePuller>(),
        networkManager: Substitute.For<IDockerNetworkManager>(),
        volumeManager: new DockerVolumeManager(),
        containerManager: new DockerContainerManager(),
        privilegeRenderer: Substitute.For<IDockerPrivilegeRenderer>(),
        resourceRenderer: Substitute.For<IDockerResourceRenderer>(),
        deploymentValidator: Substitute.For<IDockerDeploymentValidator>(),
        daemonGate: Substitute.For<IDockerDaemonGate>(),
        journaledRecreate: Substitute.For<IDockerJournaledRecreate>(),
        logReader: Substitute.For<IDockerLogReader>(),
        rollbackCoordinator: Substitute.For<IDockerRollbackCoordinator>(),
        statusMapper: Substitute.For<IDockerStatusMapper>(),
        scopeFactory: DockerProviderScope.For(new FakeSettingsService(), Substitute.For<IWorkloadCredentialPort>()),
        logger: NullLogger<DockerWorkloadProvider>.Instance);

    private async Task<IDockerClient> ArrangeDaemonAsync()
    {
        _docker.EnsureAvailable();
        var client = _factory.CreateClient(null);
        Skip.IfNot(
            await IsReachableAsync(client),
            "the provider's DockerClientFactory client (platform-default socket) is not reachable on this runner.");
        await EnsureBusyboxAsync(client);
        return client;
    }

    private static async Task<bool> IsReachableAsync(IDockerClient client)
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            await client.System.GetVersionAsync(cts.Token);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private async Task<string> CreateRunningContainerAsync(
        IDockerClient client, WorkloadDesiredSpec spec, string containerName, string networkName = "bridge")
    {
        var ctx = BuildContext(spec, containerName, "rwt_suspend_teardown_token_a1b2", networkName);
        var parameters = new DockerContainerManager().BuildContainerParameters(ctx);
        parameters.Cmd = new[] { "sleep", "3600" };

        var created = await new DockerContainerManager()
            .CreateAndStartAsync(client, parameters, [], CancellationToken.None);
        if (created.Container is not null)
        {
            _containersToCleanup.Add(created.Container.Id);
        }

        created.Succeeded.Should().BeTrue(created.Message);
        return created.Container!.Id;
    }

    private async Task EnsureVolumeAsync(IDockerClient client, WorkloadDesiredSpec spec, string volumeName)
    {
        _volumesToCleanup.Add(volumeName);
        var result = await new DockerVolumeManager()
            .EnsureVolumesAsync(client, spec, new DockerProviderSettings(), CancellationToken.None);
        result.Succeeded.Should().BeTrue(result.Message);
        (await VolumeExistsAsync(client, volumeName)).Should().BeTrue("Ensure materialized the named volume");
    }

    private static DockerContainerRenderContext BuildContext(
        WorkloadDesiredSpec spec, string containerName, string token, string networkName)
        => new()
        {
            Spec = spec,
            ContainerName = containerName,
            Credentials = new Dictionary<string, string> { ["workload-token"] = token },
            SharedNetworkName = networkName,
            Privilege = new DockerPrivilegeRenderer().RenderPrivileges(spec),
            Resource = new DockerResourceRenderer().RenderResources(spec),
            NetworkHostConfig = new DockerNetworkManager().RenderNetworking(spec, networkName).HostConfig,
        };

    private static async Task<bool> IsRunningAsync(IDockerClient client, string containerId)
    {
        try
        {
            var inspected = await client.Containers.InspectContainerAsync(containerId, CancellationToken.None);
            return inspected.State?.Running ?? false;
        }
        catch (DockerApiException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            return false;
        }
    }

    private static async Task<bool> ContainerExistsAsync(IDockerClient client, string containerId)
    {
        try
        {
            await client.Containers.InspectContainerAsync(containerId, CancellationToken.None);
            return true;
        }
        catch (DockerApiException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            return false;
        }
    }

    private static async Task<bool> VolumeExistsAsync(IDockerClient client, string name)
    {
        try
        {
            await client.Volumes.InspectAsync(name, CancellationToken.None);
            return true;
        }
        catch (DockerApiException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            return false;
        }
    }

    private static async Task<bool> NetworkExistsAsync(IDockerClient client, string id)
    {
        try
        {
            await client.Networks.InspectNetworkAsync(id, CancellationToken.None);
            return true;
        }
        catch (DockerApiException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            return false;
        }
    }

    private static async Task<DockerContainerRef?> FindByInstanceLabelAsync(IDockerClient client, WorkloadRef reference)
        => await new DockerContainerManager().FindContainerAsync(client, reference, CancellationToken.None);

    private static string Inst8(Guid instanceId) => instanceId.ToString("N")[..8];

    private static WorkloadRef ReferenceFor(Guid instanceId) => new()
    {
        InstanceId = instanceId,
        GroupId = GroupId,
        WorkloadId = WorkloadId,
        Inst8 = Inst8(instanceId),
        Provider = DockerProvider.Id,
    };

    private static (WorkloadDesiredSpec Spec, string ContainerName, string VolumeName) BuildSpec(Guid instanceId)
    {
        var inst8 = Inst8(instanceId);
        var containerName = DockerResourceNamer.Instance.ObjectName(new WorkloadNamingSubject
                        {
                            GroupId = GroupId,
                            WorkloadId = WorkloadId,
                            Inst8 = inst8,
                        });
        var volumeName = DockerResourceNamer.Instance.VolumeName(
                            new WorkloadNamingSubject
                            {
                                GroupId = GroupId,
                                WorkloadId = string.Empty,
                                Inst8 = inst8,
                            },
                            "data");

        var volume = new ResolvedVolume
        {
            Id = "data",
            MountPath = "/data",
            Size = "1Gi",
            Criticality = VolumeCriticality.Recoverable,
            ResourceName = volumeName,
            ExistingClaim = false,
        };

        var spec = new WorkloadDesiredSpec
        {
            GroupInstanceId = Guid.NewGuid(),
            GroupId = GroupId,
            WorkloadId = WorkloadId,
            InstanceId = instanceId,
            Inst8 = inst8,
            Provider = DockerProvider.Id,
            ResourceName = containerName,
            SecretName = containerName,
            Labels = new Dictionary<string, string>(),
            Image = BusyboxImage,
            Kind = WorkloadKind.SingleNode,
            Health = new WorkloadHealth { Mode = WorkloadHealthMode.Container },
            Volumes = new[] { volume },
            SpecHash = "spechash-" + inst8,
        };

        return (spec, containerName, volumeName);
    }

    private static async Task EnsureBusyboxAsync(IDockerClient client)
    {
        try
        {
            await client.Images.InspectImageAsync(BusyboxImage, CancellationToken.None);
            return;
        }
        catch (DockerApiException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
        }

        await client.Images.CreateImageAsync(
            new ImagesCreateParameters { FromImage = BusyboxRepository, Tag = BusyboxTag },
            authConfig: null,
            progress: new Progress<JSONMessage>(),
            CancellationToken.None);
    }

    private static async Task SafeAsync(Func<Task> action)
    {
        try
        {
            await action().ConfigureAwait(false);
        }
        catch (Exception)
        {
        }
    }

    private sealed class FakeSettingsService : IWorkloadOrchestratorSettingsService, IWorkloadOrchestratorSecretReader
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
}
