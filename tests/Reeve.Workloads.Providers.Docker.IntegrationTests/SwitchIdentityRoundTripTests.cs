using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Docker.DotNet;
using Docker.DotNet.Models;
using FluentAssertions;
using Reeve.Workloads.Abstractions.Contracts;
using Reeve.Workloads.Abstractions.Contracts.Deployment;
using Reeve.Workloads.Abstractions.Domain;
using Reeve.Workloads.Abstractions.Domain.DesiredSpec;
using Reeve.Workloads.Abstractions.Domain.Entities;
using Reeve.Workloads.Abstractions.Providers;
using Reeve.Workloads.Domain;
using Reeve.Workloads.Providers.Docker;

namespace Reeve.Workloads.Providers.Docker.IntegrationTests;

[Collection(DockerIntegrationCollection.Name)]
public sealed class SwitchIdentityRoundTripTests : IAsyncLifetime
{
    private const string GroupId = "com.reeve.switch-rt";
    private const string WorkloadId = "srt";
    private const string VolumeId = "data";
    private const string MountPath = "/data";

    private const string BusyboxRepository = "busybox";
    private const string BusyboxTag = "1.36";
    private const string BusyboxImage = BusyboxRepository + ":" + BusyboxTag;

    private readonly DockerDaemonFixture _docker;
    private readonly DockerVolumeManager _volumeManager = new();
    private readonly DockerContainerManager _containerManager = new();

    private readonly List<string> _containersToCleanup = new();
    private readonly List<string> _volumesToCleanup = new();

    public SwitchIdentityRoundTripTests(DockerDaemonFixture docker) => _docker = docker;

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        if (_docker.Client is not { } client)
        {
            return;
        }

        foreach (var id in _containersToCleanup.Distinct())
        {
            try
            {
                await client.Containers.RemoveContainerAsync(
                    id, new ContainerRemoveParameters { Force = true }, CancellationToken.None);
            }
            catch (DockerApiException)
            {
            }
        }

        foreach (var name in _volumesToCleanup.Distinct())
        {
            try
            {
                await client.Volumes.RemoveAsync(name, force: true, CancellationToken.None);
            }
            catch (DockerApiException)
            {
            }
        }
    }


    [SkippableFact]
    public async Task Managed_to_manual_to_managed_roundtrip_preserves_instance_id_inst8_and_the_named_volume_and_its_data()
    {
        _docker.EnsureAvailable();
        var client = _docker.Client!;
        await EnsureBusyboxAsync(client);

        var now = DateTimeOffset.UtcNow;
        var instanceId = Guid.NewGuid();
        var pluginInstanceId = Guid.NewGuid();

        var instance = CreateHealthyManagedDockerInstance(instanceId, pluginInstanceId, now);
        instance.State.Should().Be(WorkloadInstanceState.Healthy);
        instance.Provider.Should().Be(DockerProvider.Id);

        var idBefore = instance.Id;
        var inst8Before = instance.Inst8;
        var volumeName = DockerResourceNamer.Instance.VolumeName(
                            new WorkloadNamingSubject
                            {
                                GroupId = GroupId,
                                WorkloadId = string.Empty,
                                Inst8 = inst8Before,
                            },
                            VolumeId);
        var containerName = DockerResourceNamer.Instance.ObjectName(new WorkloadNamingSubject
                        {
                            GroupId = GroupId,
                            WorkloadId = WorkloadId,
                            Inst8 = inst8Before,
                        });
        _volumesToCleanup.Add(volumeName);

        var marker = "persisted-" + Guid.NewGuid().ToString("N");

        var managedSpec = BuildSpec(instance, existingClaim: false);
        managedSpec.Volumes.Single().ResourceName.Should().Be(volumeName, "the resolved volume name is the deterministic inst8 name");

        var ensured = await _volumeManager.EnsureVolumesAsync(client, managedSpec, new DockerProviderSettings(), CancellationToken.None);
        ensured.Succeeded.Should().BeTrue(ensured.Message);

        var volumeAtDeploy = await client.Volumes.InspectAsync(volumeName, CancellationToken.None);
        volumeAtDeploy.Name.Should().Be(volumeName);
        volumeAtDeploy.Labels.Should().ContainKey(DockerProviderConstants.InstanceLabelKey)
            .WhoseValue.Should().Be(instanceId.ToString("D"), "the managed volume is stamped with THIS instance's identity label");

        const string managedToken = "rwt_switch_rt_managed_token_a1b2c3";
        var managedContainerId = await CreateManagedWorkloadContainerAsync(
            client,
            managedSpec,
            containerName,
            managedToken,
            cmd: new[] { "sh", "-c", $"echo '{marker}' > {MountPath}/marker.txt; echo WROTE_OK; sleep 3600" });

        (await WaitForLogSubstringAsync(client, managedContainerId, "WROTE_OK", TimeSpan.FromSeconds(30)))
            .Should().BeTrue("the managed container should have written the marker into the named volume");

        var managedRef = await _containerManager.FindContainerAsync(client, ReferenceFor(instance), CancellationToken.None);
        managedRef.Should().NotBeNull("the managed container carries this instance's identity label");
        managedRef!.Id.Should().Be(managedContainerId);
        var managedInspect = await client.Containers.InspectContainerAsync(managedContainerId, CancellationToken.None);
        managedInspect.Mounts.Should().Contain(m => m.Destination == MountPath && m.Name == volumeName);

        var takeoverVolumeName = DockerResourceNamer.Instance.VolumeName(
                            new WorkloadNamingSubject
                            {
                                GroupId = GroupId,
                                WorkloadId = string.Empty,
                                Inst8 = instance.Inst8,
                            },
                            VolumeId);
        var takeoverContainerName = DockerResourceNamer.Instance.ObjectName(new WorkloadNamingSubject
                        {
                            GroupId = GroupId,
                            WorkloadId = WorkloadId,
                            Inst8 = instance.Inst8,
                        });
        takeoverVolumeName.Should().Be(volumeName, "the takeover artifact references the byte-identical named volume");
        takeoverContainerName.Should().Be(containerName);
        var held = await _containerManager.ReadHeldEnvAsync(client, managedRef, ["REEVE_WORKLOAD_TOKEN"], CancellationToken.None);
        held.Should().Equal(new Dictionary<string, string> { ["REEVE_WORKLOAD_TOKEN"] = managedToken },
            "the takeover reuses the EXISTING injected credential (no rotation on switch)");

        instance.TransitionTo(WorkloadInstanceState.Switching, WorkloadStateReason.ExternalHealthObserved,
            "Managed→manual provider switch in progress.", DateTimeOffset.UtcNow);
        instance.CanMutate.Should().BeFalse("a Switching instance blocks every new mutation (single-field ownership machine)");
        instance.TransitionTo(WorkloadInstanceState.ExternallyManaged, WorkloadStateReason.ExternalHealthObserved,
            "Disowned to manual operator management — runtime/data retained.", DateTimeOffset.UtcNow);

        instance.State.Should().Be(WorkloadInstanceState.ExternallyManaged);
        instance.Provider.Should().Be(DockerProvider.Id, "the switch PRESERVES the original managed provider");
        instance.Id.Should().Be(idBefore, "the switch never mints a new instance id");
        instance.Inst8.Should().Be(inst8Before);

        var refAfterSwitch = await _containerManager.FindContainerAsync(client, ReferenceFor(instance), CancellationToken.None);
        refAfterSwitch.Should().NotBeNull("the switch did NOT delete the managed container (docker release is a no-op)");
        refAfterSwitch!.Id.Should().Be(managedContainerId, "the very same container keeps running across the switch");

        var volumeAfterSwitch = await client.Volumes.InspectAsync(volumeName, CancellationToken.None);
        volumeAfterSwitch.Mountpoint.Should().Be(volumeAtDeploy.Mountpoint, "the switch reused the SAME physical volume (not recreated)");
        volumeAfterSwitch.CreatedAt.Should().Be(volumeAtDeploy.CreatedAt, "the volume's data identity is preserved across the switch");

        (await ReadMarkerViaThrowawayAsync(client, volumeName, "postswitch"))
            .Should().Contain(marker, "the marker written before the switch survives the disown (runtime/data retained)");

        (await _containerManager.RemoveContainerAsync(client, managedContainerId, CancellationToken.None))
            .Succeeded.Should().BeTrue();
        _containersToCleanup.Remove(managedContainerId);

        var operatorContainerId = await CreateOperatorTakeoverContainerAsync(client, instance, containerName, volumeName, marker);

        (await WaitForLogSubstringAsync(client, operatorContainerId, marker, TimeSpan.FromSeconds(30)))
            .Should().BeTrue("the operator's container reads the SAME marker from the SAME named volume");

        WorkloadInstanceStateMachine.IsValidTransition(WorkloadInstanceState.ExternallyManaged, WorkloadInstanceState.Deploying)
            .Should().BeTrue();
        instance.TransitionTo(WorkloadInstanceState.Deploying, WorkloadStateReason.AdoptionAccepted,
            "Re-adopted from manual operator management back under Reeve (class-c force-adopt).", DateTimeOffset.UtcNow);
        instance.State.Should().Be(WorkloadInstanceState.Deploying);
        instance.Id.Should().Be(idBefore, "re-adoption re-uses the SAME row — never a new id");
        instance.Inst8.Should().Be(inst8Before);

        var readoptSpec = BuildSpec(instance, existingClaim: true);
        readoptSpec.Volumes.Single().ResourceName.Should().Be(volumeName, "the re-adopted volume name is byte-identical");

        var readopted = await _volumeManager.EnsureVolumesAsync(client, readoptSpec, new DockerProviderSettings(), CancellationToken.None);
        readopted.Succeeded.Should().BeTrue(
            "the must-exist-else-fail existing-claim adoption finds the SAME named volume (names never diverged), so it never hard-fails");

        var volumeAtReadopt = await client.Volumes.InspectAsync(volumeName, CancellationToken.None);
        volumeAtReadopt.Mountpoint.Should().Be(volumeAtDeploy.Mountpoint, "re-adoption reuses the ORIGINAL physical volume in place");
        volumeAtReadopt.CreatedAt.Should().Be(volumeAtDeploy.CreatedAt, "re-adoption never recreated the volume (data identity preserved)");

        (await _containerManager.RemoveContainerAsync(client, operatorContainerId, CancellationToken.None))
            .Succeeded.Should().BeTrue();
        _containersToCleanup.Remove(operatorContainerId);

        var readoptToken = "rwt_switch_rt_readopt_token_d4e5f6";
        var readoptContainerId = await CreateManagedWorkloadContainerAsync(
            client,
            readoptSpec,
            containerName,
            readoptToken,
            cmd: new[] { "sh", "-c", $"cat {MountPath}/marker.txt; sleep 3600" });

        var readoptInspect = await client.Containers.InspectContainerAsync(readoptContainerId, CancellationToken.None);
        readoptInspect.Mounts.Should().Contain(
            m => m.Destination == MountPath && m.Name == volumeName,
            "the re-adopted managed container re-attaches the SAME named volume by its byte-identical name");

        (await WaitForLogSubstringAsync(client, readoptContainerId, marker, TimeSpan.FromSeconds(30)))
            .Should().BeTrue("the marker written before the switch is STILL present after the full managed→manual→managed round-trip");

        instance.TransitionTo(WorkloadInstanceState.Healthy, WorkloadStateReason.ExternalHealthObserved,
            "Re-adopted and running under Reeve management again.", DateTimeOffset.UtcNow);

        instance.Id.Should().Be(idBefore, "InstanceId is byte-identical across the entire round-trip");
        instance.Inst8.Should().Be(inst8Before, "inst8 is byte-identical across the entire round-trip");
        DockerResourceNamer.Instance.VolumeName(
            new WorkloadNamingSubject
            {
                GroupId = GroupId,
                WorkloadId = string.Empty,
                Inst8 = instance.Inst8,
            },
            VolumeId)
            .Should().Be(volumeName, "the docker named-volume name is byte-identical after the round-trip");
        DockerResourceNamer.Instance.ObjectName(new WorkloadNamingSubject
        {
            GroupId = GroupId,
            WorkloadId = WorkloadId,
            Inst8 = instance.Inst8,
        })
            .Should().Be(containerName, "the docker container name is byte-identical after the round-trip");
        volumeAtReadopt.Mountpoint.Should().Be(volumeAtDeploy.Mountpoint, "the SAME physical named volume was used from first deploy to re-adoption");
    }

    [Fact]
    public void Switch_roundtrip_state_machine_preserves_instance_id_inst8_and_the_derived_docker_volume_name()
    {
        var now = DateTimeOffset.UtcNow;
        var instance = CreateHealthyManagedDockerInstance(Guid.NewGuid(), Guid.NewGuid(), now);

        var idBefore = instance.Id;
        var inst8Before = instance.Inst8;
        var volumeBefore = DockerResourceNamer.Instance.VolumeName(
                            new WorkloadNamingSubject
                            {
                                GroupId = GroupId,
                                WorkloadId = string.Empty,
                                Inst8 = instance.Inst8,
                            },
                            VolumeId);

        instance.TransitionTo(WorkloadInstanceState.Switching, WorkloadStateReason.ExternalHealthObserved, null, now);
        instance.TransitionTo(WorkloadInstanceState.ExternallyManaged, WorkloadStateReason.ExternalHealthObserved, null, now);
        instance.Provider.Should().Be(DockerProvider.Id, "the switch preserves the original managed provider");

        WorkloadInstanceStateMachine.IsValidTransition(WorkloadInstanceState.ExternallyManaged, WorkloadInstanceState.Switching)
            .Should().BeFalse("re-adoption never re-enters Switching (single-owner machine)");
        instance.TransitionTo(WorkloadInstanceState.Deploying, WorkloadStateReason.AdoptionAccepted, null, now);

        instance.Id.Should().Be(idBefore, "the round-trip never mints a new instance id");
        instance.Inst8.Should().Be(inst8Before);
        DockerResourceNamer.Instance.VolumeName(
            new WorkloadNamingSubject
            {
                GroupId = GroupId,
                WorkloadId = string.Empty,
                Inst8 = instance.Inst8,
            },
            VolumeId)
            .Should().Be(volumeBefore, "the derived docker named-volume name is byte-identical across the switch round-trip");
    }

    private static string Inst8(Guid instanceId) => instanceId.ToString("N")[..8];

    private static WorkloadInstance CreateHealthyManagedDockerInstance(Guid instanceId, Guid pluginInstanceId, DateTimeOffset now)
    {
        var instance = WorkloadInstance.Create(instanceId, pluginInstanceId, GroupId, WorkloadId, now);
        instance.TransitionTo(WorkloadInstanceState.Deploying, WorkloadStateReason.ConfigRequired, null, now);
        instance.SetProvider(DockerProvider.Id, now);
        instance.TransitionTo(WorkloadInstanceState.Healthy, WorkloadStateReason.ExternalHealthObserved, null, now);
        return instance;
    }

    private static WorkloadRef ReferenceFor(WorkloadInstance instance) => new()
    {
        InstanceId = instance.Id,
        GroupId = GroupId,
        WorkloadId = WorkloadId,
        Inst8 = instance.Inst8,
        Provider = DockerProvider.Id,
    };

    private static WorkloadDesiredSpec BuildSpec(WorkloadInstance instance, bool existingClaim)
    {
        var inst8 = instance.Inst8;
        var volumeName = DockerResourceNamer.Instance.VolumeName(
                            new WorkloadNamingSubject
                            {
                                GroupId = GroupId,
                                WorkloadId = string.Empty,
                                Inst8 = inst8,
                            },
                            VolumeId);
        var containerName = DockerResourceNamer.Instance.ObjectName(new WorkloadNamingSubject
                        {
                            GroupId = GroupId,
                            WorkloadId = WorkloadId,
                            Inst8 = inst8,
                        });

        var volume = new ResolvedVolume
        {
            Id = VolumeId,
            MountPath = MountPath,
            Size = "1Gi",
            Criticality = VolumeCriticality.Critical,
            AccessMode = "rwo",
            ResourceName = volumeName,
            ExistingClaim = existingClaim,
        };

        return new WorkloadDesiredSpec
        {
            GroupInstanceId = instance.GroupInstanceId,
            GroupId = GroupId,
            WorkloadId = WorkloadId,
            InstanceId = instance.Id,
            Inst8 = inst8,
            Provider = DockerProvider.Id,
            ResourceName = containerName,
            SecretName = containerName,
            Labels = new Dictionary<string, string>(),
            Image = BusyboxImage,
            Kind = WorkloadKind.SingleNode,
            Health = new WorkloadHealth { Mode = WorkloadHealthMode.Container },
            Volumes = new[] { volume },
            ProvisionedCredentials = [new ProvisionedCredential("workload-token", "REEVE_WORKLOAD_TOKEN")],
            SpecHash = "spechash-" + inst8,
        };
    }

    private async Task<string> CreateManagedWorkloadContainerAsync(
        IDockerClient client,
        WorkloadDesiredSpec spec,
        string containerName,
        string token,
        IList<string> cmd)
    {
        var ctx = new DockerContainerRenderContext
        {
            Spec = spec,
            ContainerName = containerName,
            Credentials = new Dictionary<string, string> { ["workload-token"] = token },
            SharedNetworkName = "bridge",
            Privilege = new DockerPrivilegeRenderer().RenderPrivileges(spec),
            Resource = new DockerResourceRenderer().RenderResources(spec),
            NetworkHostConfig = new DockerNetworkManager().RenderNetworking(spec, "bridge").HostConfig,
        };

        var parameters = _containerManager.BuildContainerParameters(ctx);
        parameters.Cmd = cmd;

        var created = await _containerManager.CreateAndStartAsync(client, parameters, [], CancellationToken.None);
        if (created.Container is not null)
        {
            _containersToCleanup.Add(created.Container.Id);
        }

        created.Succeeded.Should().BeTrue(created.Message);
        return created.Container!.Id;
    }

    private async Task<string> CreateOperatorTakeoverContainerAsync(
        IDockerClient client,
        WorkloadInstance instance,
        string containerName,
        string volumeName,
        string marker)
    {
        var parameters = new CreateContainerParameters
        {
            Image = BusyboxImage,
            Name = containerName,
            Cmd = new[] { "sh", "-c", $"cat {MountPath}/marker.txt; sleep 3600" },
            Labels = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [DockerProviderConstants.InstanceLabelKey] = instance.Id.ToString("D"),
                [DockerProviderConstants.ManagedByLabelKey] = "operator",
            },
            HostConfig = new HostConfig
            {
                NetworkMode = "bridge",
                Mounts = new List<Mount>
                {
                    new() { Type = "volume", Source = volumeName, Target = MountPath, ReadOnly = false },
                },
                RestartPolicy = new RestartPolicy { Name = RestartPolicyKind.No },
            },
        };

        var created = await client.Containers.CreateContainerAsync(parameters, CancellationToken.None);
        _containersToCleanup.Add(created.ID);
        await client.Containers.StartContainerAsync(created.ID, new ContainerStartParameters(), CancellationToken.None);
        _ = marker;
        return created.ID;
    }

    private async Task<string> ReadMarkerViaThrowawayAsync(IDockerClient client, string volumeName, string nameHint)
    {
        var parameters = new CreateContainerParameters
        {
            Image = BusyboxImage,
            Name = $"reeve-it-switch-rt-read-{nameHint}-{Guid.NewGuid():N}",
            Cmd = new[] { "sh", "-c", $"cat {MountPath}/marker.txt" },
            Labels = new Dictionary<string, string>(_docker.BuildLabels()),
            HostConfig = new HostConfig
            {
                Mounts = new List<Mount>
                {
                    new() { Type = "volume", Source = volumeName, Target = MountPath, ReadOnly = false },
                },
            },
        };

        var created = await client.Containers.CreateContainerAsync(parameters, CancellationToken.None);
        _containersToCleanup.Add(created.ID);
        await client.Containers.StartContainerAsync(created.ID, new ContainerStartParameters(), CancellationToken.None);

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        while (true)
        {
            var text = await ReadAllLogsAsync(client, created.ID, CancellationToken.None);
            if (!string.IsNullOrWhiteSpace(text) || DateTime.UtcNow >= deadline)
            {
                return text;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(200));
        }
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

    private static async Task<bool> WaitForLogSubstringAsync(
        IDockerClient client, string containerId, string expected, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (true)
        {
            var text = await ReadAllLogsAsync(client, containerId, CancellationToken.None);
            if (text.Contains(expected, StringComparison.Ordinal))
            {
                return true;
            }

            if (DateTime.UtcNow >= deadline)
            {
                return false;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(200));
        }
    }

    private static async Task<string> ReadAllLogsAsync(IDockerClient client, string containerId, CancellationToken cancellationToken)
    {
        var parameters = new ContainerLogsParameters
        {
            ShowStdout = true,
            ShowStderr = true,
            Follow = false,
            Timestamps = false,
            Tail = "all",
        };

        using var stream = await client.Containers
            .GetContainerLogsAsync(containerId, parameters, cancellationToken)
            .ConfigureAwait(false);

        using var merged = new MemoryStream();
        var buffer = new byte[8192];
        while (true)
        {
            var read = await stream.ReadOutputAsync(buffer, 0, buffer.Length, cancellationToken).ConfigureAwait(false);
            if (read.EOF)
            {
                break;
            }

            if (read.Count > 0)
            {
                merged.Write(buffer, 0, read.Count);
            }
        }

        return Encoding.UTF8.GetString(merged.ToArray());
    }
}
