using System.Net;
using Docker.DotNet;
using Docker.DotNet.Models;
using FluentAssertions;
using Reeve.Workloads.Abstractions.Contracts;
using Reeve.Workloads.Abstractions.Contracts.Deployment;
using Reeve.Workloads.Abstractions.Domain;
using Reeve.Workloads.Abstractions.Domain.DesiredSpec;
using Reeve.Workloads.Abstractions.Providers;
using Reeve.Workloads.Domain;
using Reeve.Workloads.Providers.Docker;

namespace Reeve.Workloads.Providers.Docker.IntegrationTests;

[Collection(DockerIntegrationCollection.Name)]
public sealed class DockerVolumeTests : IAsyncLifetime
{
    private const string GroupId = "com.reeve.vol-test";
    private const string WorkloadId = "vt";

    private readonly DockerDaemonFixture _docker;
    private readonly DockerVolumeManager _sut = new();

    private readonly List<string> _volumesToCleanup = new();

    public DockerVolumeTests(DockerDaemonFixture docker) => _docker = docker;

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        if (_docker.Client is not { } client)
        {
            return;
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
    public async Task Ensure_precreates_named_volume_with_instance_label_and_is_idempotent()
    {
        _docker.EnsureAvailable();
        var client = _docker.Client!;

        var instanceId = Guid.NewGuid();
        var (spec, volumeName) = SingleVolumeSpec(instanceId, "data", existingClaim: false);
        _volumesToCleanup.Add(volumeName);

        var first = await _sut.EnsureVolumesAsync(client, spec, new DockerProviderSettings(), CancellationToken.None);
        first.Succeeded.Should().BeTrue(first.Message);

        var created = await client.Volumes.InspectAsync(volumeName, CancellationToken.None);
        created.Name.Should().Be(volumeName);
        created.Labels.Should().ContainKey(DockerProviderConstants.InstanceLabelKey)
            .WhoseValue.Should().Be(instanceId.ToString("D"));

        var second = await _sut.EnsureVolumesAsync(client, spec, new DockerProviderSettings(), CancellationToken.None);
        second.Succeeded.Should().BeTrue(second.Message);

        var reinspected = await client.Volumes.InspectAsync(volumeName, CancellationToken.None);
        reinspected.Mountpoint.Should().Be(created.Mountpoint);
        reinspected.CreatedAt.Should().Be(created.CreatedAt);
    }

    [SkippableFact]
    public async Task Ensure_adopts_existing_claim_without_recreating()
    {
        _docker.EnsureAvailable();
        var client = _docker.Client!;

        var instanceId = Guid.NewGuid();
        var (spec, volumeName) = SingleVolumeSpec(instanceId, "data", existingClaim: true);
        _volumesToCleanup.Add(volumeName);

        var pre = await client.Volumes.CreateAsync(
            new VolumesCreateParameters { Name = volumeName, Labels = new Dictionary<string, string>(_docker.BuildLabels()) },
            CancellationToken.None);

        var result = await _sut.EnsureVolumesAsync(client, spec, new DockerProviderSettings(), CancellationToken.None);
        result.Succeeded.Should().BeTrue(result.Message);

        var after = await client.Volumes.InspectAsync(volumeName, CancellationToken.None);
        after.Mountpoint.Should().Be(pre.Mountpoint, "adoption reuses the retained volume rather than recreating it");
        after.CreatedAt.Should().Be(pre.CreatedAt, "the adopted volume's data identity is preserved (not recreated)");
        (after.Labels ?? new Dictionary<string, string>())
            .Should().NotContainKey(DockerProviderConstants.InstanceLabelKey,
                "adoption keys on the deterministic name and must not relabel (recreate) the retained volume");
    }

    [SkippableFact]
    public async Task Ensure_fails_closed_when_existing_claim_volume_absent()
    {
        _docker.EnsureAvailable();
        var client = _docker.Client!;

        var instanceId = Guid.NewGuid();
        var (spec, volumeName) = SingleVolumeSpec(instanceId, "data", existingClaim: true);
        _volumesToCleanup.Add(volumeName);

        (await VolumeExistsAsync(client, volumeName))
            .Should().BeFalse("precondition: the adopted volume is absent for a fresh instance id");

        var result = await _sut.EnsureVolumesAsync(client, spec, new DockerProviderSettings(), CancellationToken.None);

        result.Succeeded.Should().BeFalse("an existing-claim volume that is absent must fail-closed, never be created");
        result.FailureKind.Should().Be(DockerFailureKind.Rejected);

        (await VolumeExistsAsync(client, volumeName))
            .Should().BeFalse("must-exist-else-fail must NEVER create a fresh empty volume");
    }

    [SkippableFact]
    public async Task Remove_deletes_this_instances_labelled_volumes()
    {
        _docker.EnsureAvailable();
        var client = _docker.Client!;

        var instanceId = Guid.NewGuid();
        var (spec, volumeName) = SingleVolumeSpec(instanceId, "data", existingClaim: false);
        _volumesToCleanup.Add(volumeName);

        (await _sut.EnsureVolumesAsync(client, spec, new DockerProviderSettings(), CancellationToken.None))
            .Succeeded.Should().BeTrue();
        (await VolumeExistsAsync(client, volumeName)).Should().BeTrue("Ensure materialized the volume");

        var removed = await _sut.RemoveVolumesAsync(client, WorkloadRef.FromDesiredSpec(spec), CancellationToken.None);
        removed.Succeeded.Should().BeTrue(removed.Message);

        (await VolumeExistsAsync(client, volumeName))
            .Should().BeFalse("teardown removes this instance's labelled named volume");
    }

    [SkippableFact]
    public async Task Remove_never_touches_another_instances_labelled_volume()
    {
        _docker.EnsureAvailable();
        var client = _docker.Client!;

        var ours = Guid.NewGuid();
        var (spec, ourVolume) = SingleVolumeSpec(ours, "data", existingClaim: false);
        _volumesToCleanup.Add(ourVolume);

        var other = Guid.NewGuid();
        var otherVolume = DockerResourceNamer.Instance.VolumeName(
                            new WorkloadNamingSubject
                            {
                                GroupId = GroupId,
                                WorkloadId = string.Empty,
                                Inst8 = Inst8(other),
                            },
                            "data");
        _volumesToCleanup.Add(otherVolume);
        await client.Volumes.CreateAsync(
            new VolumesCreateParameters
            {
                Name = otherVolume,
                Labels = new Dictionary<string, string>(_docker.BuildLabels())
                {
                    [DockerProviderConstants.InstanceLabelKey] = other.ToString("D"),
                },
            },
            CancellationToken.None);

        (await _sut.EnsureVolumesAsync(client, spec, new DockerProviderSettings(), CancellationToken.None))
            .Succeeded.Should().BeTrue();
        (await VolumeExistsAsync(client, ourVolume)).Should().BeTrue();

        var removed = await _sut.RemoveVolumesAsync(client, WorkloadRef.FromDesiredSpec(spec), CancellationToken.None);
        removed.Succeeded.Should().BeTrue(removed.Message);

        (await VolumeExistsAsync(client, ourVolume))
            .Should().BeFalse("our own labelled volume is removed");
        (await VolumeExistsAsync(client, otherVolume))
            .Should().BeTrue("a volume labelled with a DIFFERENT instance id must survive (cross-claim refusal)");
    }

    private const string BusyboxImage = "busybox:latest";
    private const int VolumeGroup = 4242;

    [SkippableFact]
    public async Task A_root_populated_volume_that_predates_the_volume_group_becomes_writable_to_a_non_root_user()
    {
        _docker.EnsureAvailable();
        var client = _docker.Client!;
        Skip.IfNot(await EnsureBusyboxAsync(client), $"'{BusyboxImage}' unavailable and not pullable on this runner.");

        var (plain, volumeName) = SingleVolumeSpec(Guid.NewGuid(), "data", existingClaim: false);
        plain = plain with { Image = BusyboxImage };
        _volumesToCleanup.Add(volumeName);
        (await _sut.EnsureVolumesAsync(client, plain, new DockerProviderSettings(), CancellationToken.None)).Succeeded.Should().BeTrue();

        (await RunOnceAsync(client, "0", volumeName, "mkdir -p /data/sub && echo x > /data/sub/f")).Should().Be(0);
        (await RunOnceAsync(client, $"1234:{VolumeGroup}", volumeName, "touch /data/new"))
            .Should().NotBe(0, "control: without the group a non-root user cannot write a root-owned volume");

        var grouped = plain with { Privileges = new ResolvedPrivileges { FsGroup = VolumeGroup } };
        var ensured = await _sut.EnsureVolumesAsync(client, grouped, new DockerProviderSettings(), CancellationToken.None);
        ensured.Succeeded.Should().BeTrue(ensured.Message);

        (await RunOnceAsync(
                client, $"1234:{VolumeGroup}", volumeName,
                "touch /data/new && echo y >> /data/sub/f && mkdir /data/sub/d && touch /data/sub/d/g"))
            .Should().Be(0, "the new group owns the volume root and everything in it");
    }

    [SkippableFact]
    public async Task A_new_volume_is_writable_to_a_non_root_user_from_its_first_start()
    {
        _docker.EnsureAvailable();
        var client = _docker.Client!;
        Skip.IfNot(await EnsureBusyboxAsync(client), $"'{BusyboxImage}' unavailable and not pullable on this runner.");

        var (plain, volumeName) = SingleVolumeSpec(Guid.NewGuid(), "data", existingClaim: false);
        var spec = plain with { Image = BusyboxImage, Privileges = new ResolvedPrivileges { FsGroup = VolumeGroup } };
        _volumesToCleanup.Add(volumeName);

        var ensured = await _sut.EnsureVolumesAsync(client, spec, new DockerProviderSettings(), CancellationToken.None);
        ensured.Succeeded.Should().BeTrue(ensured.Message);

        (await RunOnceAsync(client, $"1234:{VolumeGroup}", volumeName, "touch /data/new && mkdir /data/d && touch /data/d/e")).Should().Be(0);
    }

    [SkippableFact]
    public async Task A_helper_that_cannot_run_fails_the_volume_step_instead_of_starting_an_unwritable_workload()
    {
        _docker.EnsureAvailable();
        var client = _docker.Client!;
        Skip.IfNot(await EnsureBusyboxAsync(client), $"'{BusyboxImage}' unavailable and not pullable on this runner.");

        var (plain, volumeName) = SingleVolumeSpec(Guid.NewGuid(), "data", existingClaim: false);
        _volumesToCleanup.Add(volumeName);
        var spec = plain with
        {
            Image = "reeve.invalid/no-such-image:0",
            Privileges = new ResolvedPrivileges { FsGroup = VolumeGroup },
        };

        var ensured = await _sut.EnsureVolumesAsync(client, spec, new DockerProviderSettings(), CancellationToken.None);

        ensured.Succeeded.Should().BeFalse();
        ensured.FailureKind.Should().Be(DockerFailureKind.Rejected);
    }

    private async Task<int> RunOnceAsync(IDockerClient client, string user, string volumeName, string script)
    {
        var created = await client.Containers.CreateContainerAsync(
            new CreateContainerParameters
            {
                Image = BusyboxImage,
                User = user,
                Entrypoint = new[] { "sh", "-c", script },
                Labels = new Dictionary<string, string>(_docker.BuildLabels()),
                HostConfig = new HostConfig
                {
                    Mounts = new List<Mount> { new() { Type = "volume", Source = volumeName, Target = "/data" } },
                    NetworkMode = "none",
                },
            },
            CancellationToken.None);
        try
        {
            await client.Containers.StartContainerAsync(created.ID, new ContainerStartParameters(), CancellationToken.None);
            var exit = await client.Containers.WaitContainerAsync(created.ID, CancellationToken.None);
            return (int)exit.StatusCode;
        }
        finally
        {
            await client.Containers.RemoveContainerAsync(created.ID, new ContainerRemoveParameters { Force = true }, CancellationToken.None);
        }
    }

    private static async Task<bool> EnsureBusyboxAsync(IDockerClient client)
    {
        try
        {
            await client.Images.InspectImageAsync(BusyboxImage, CancellationToken.None);
            return true;
        }
        catch (DockerApiException)
        {
        }

        try
        {
            await client.Images.CreateImageAsync(
                new ImagesCreateParameters { FromImage = "busybox", Tag = "latest" },
                authConfig: null,
                new Progress<JSONMessage>(),
                CancellationToken.None);
            await client.Images.InspectImageAsync(BusyboxImage, CancellationToken.None);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string Inst8(Guid instanceId) => instanceId.ToString("N")[..8];

    private static async Task<bool> VolumeExistsAsync(IDockerClient client, string name)
    {
        try
        {
            await client.Volumes.InspectAsync(name, CancellationToken.None);
            return true;
        }
        catch (DockerApiException e) when (e.StatusCode == HttpStatusCode.NotFound)
        {
            return false;
        }
    }

    private static (WorkloadDesiredSpec Spec, string VolumeName) SingleVolumeSpec(
        Guid instanceId,
        string volumeId,
        bool existingClaim)
    {
        var inst8 = Inst8(instanceId);
        var volumeName = DockerResourceNamer.Instance.VolumeName(
                            new WorkloadNamingSubject
                            {
                                GroupId = GroupId,
                                WorkloadId = string.Empty,
                                Inst8 = inst8,
                            },
                            volumeId);
        var containerName = DockerResourceNamer.Instance.ObjectName(new WorkloadNamingSubject
                        {
                            GroupId = GroupId,
                            WorkloadId = WorkloadId,
                            Inst8 = inst8,
                        });

        var volume = new ResolvedVolume
        {
            Id = volumeId,
            MountPath = "/data",
            Size = "1Gi",
            Criticality = VolumeCriticality.Recoverable,
            ResourceName = volumeName,
            ExistingClaim = existingClaim,
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
            Image = "docker.io/library/busybox@sha256:0000000000000000000000000000000000000000000000000000000000000000",
            Kind = WorkloadKind.SingleNode,
            Health = new WorkloadHealth { Mode = WorkloadHealthMode.Container },
            Volumes = new[] { volume },
            SpecHash = "spechash-" + inst8,
        };

        return (spec, volumeName);
    }
}
