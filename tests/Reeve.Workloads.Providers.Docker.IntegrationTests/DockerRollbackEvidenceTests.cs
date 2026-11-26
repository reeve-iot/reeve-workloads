using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Docker.DotNet;
using Docker.DotNet.Models;
using FluentAssertions;
using NSubstitute;
using Reeve.Workloads.Abstractions.Contracts;
using Reeve.Workloads.Abstractions.Contracts.Deployment;
using Reeve.Workloads.Abstractions.Domain;
using Reeve.Workloads.Abstractions.Domain.DesiredSpec;
using Reeve.Workloads.Abstractions.Providers;
using Reeve.Workloads.Domain;
using Reeve.Workloads.Providers.Docker;

namespace Reeve.Workloads.Providers.Docker.IntegrationTests;

[Collection(DockerIntegrationCollection.Name)]
public sealed class DockerRollbackEvidenceTests
{
    private const string Image = "busybox:latest";
    private const string ImageRepo = "busybox";
    private const string ImageTag = "latest";

    private const string GroupId = "reeve.test.rollback";
    private const string WorkloadId = "rb";

    private static readonly TimeSpan WaitBudget = TimeSpan.FromSeconds(20);

    private readonly DockerDaemonFixture _docker;

    public DockerRollbackEvidenceTests(DockerDaemonFixture docker) => _docker = docker;


    [SkippableFact]
    public async Task RollbackAsync_captures_failed_candidate_evidence_before_removing_it()
    {
        _docker.EnsureAvailable();
        var client = _docker.Client!;
        Skip.IfNot(await EnsureImageAsync(client), $"'{Image}' unavailable and not pullable on this runner.");

        var instanceId = Guid.NewGuid();
        var spec = BuildSpec(instanceId, "hash-candidate");
        var createdIds = new List<string>();
        var sut = new DockerRollbackCoordinator();

        try
        {
            var candidateId = await CreateAndStartAsync(
                client, instanceId, "hash-candidate",
                new[] { "sh", "-c", "echo candidate-line-A; echo candidate-line-B; exit 1" },
                createdIds);
            await WaitForExitAsync(client, candidateId);

            var candidate = await ToRefAsync(client, candidateId);

            var result = await sut.RollbackAsync(client, spec, candidate, previousContainer: null, CancellationToken.None);

            result.CapturedEvidence.Should().NotBeNull("the failed candidate's evidence is captured before teardown");
            var texts = result.CapturedEvidence!.Lines.Select(l => l.Text).ToList();
            texts.Should().Contain("candidate-line-A").And.Contain("candidate-line-B");

            (await ContainerExistsAsync(client, candidateId)).Should().BeFalse("the failed candidate is removed after capture");
        }
        finally
        {
            await RemoveAllAsync(client, createdIds);
        }
    }


    [SkippableFact]
    public async Task RollbackAsync_restores_a_stopped_previous_container_to_running()
    {
        _docker.EnsureAvailable();
        var client = _docker.Client!;
        Skip.IfNot(await EnsureImageAsync(client), $"'{Image}' unavailable and not pullable on this runner.");

        var instanceId = Guid.NewGuid();
        var spec = BuildSpec(instanceId, "hash-candidate");
        var createdIds = new List<string>();
        var sut = new DockerRollbackCoordinator();

        try
        {
            var previousId = await CreateAndStartAsync(
                client, instanceId, "hash-previous", new[] { "sleep", "3600" }, createdIds);
            (await client.Containers.StopContainerAsync(previousId, new ContainerStopParameters(), CancellationToken.None))
                .Should().BeTrue("the previous container starts running and is then stopped");
            (await IsRunningAsync(client, previousId)).Should().BeFalse("precondition: the previous is stopped");

            var candidateId = await CreateAndStartAsync(
                client, instanceId, "hash-candidate", new[] { "sh", "-c", "echo boom; exit 1" }, createdIds);
            await WaitForExitAsync(client, candidateId);

            var candidate = await ToRefAsync(client, candidateId);
            var previous = await ToRefAsync(client, previousId);

            var result = await sut.RollbackAsync(client, spec, candidate, previous, CancellationToken.None);

            result.PreviousRestored.Should().BeTrue("the previous container is brought back to running on rollback");
            (await IsRunningAsync(client, previousId)).Should().BeTrue("the previous container is running afterwards");
            (await ContainerExistsAsync(client, candidateId)).Should().BeFalse("the failed candidate is cleaned up");
        }
        finally
        {
            await RemoveAllAsync(client, createdIds);
        }
    }


    [SkippableFact]
    public async Task RollbackAsync_retains_the_shared_network_and_the_previous_named_volume()
    {
        _docker.EnsureAvailable();
        var client = _docker.Client!;
        Skip.IfNot(await EnsureImageAsync(client), $"'{Image}' unavailable and not pullable on this runner.");

        var instanceId = Guid.NewGuid();
        var spec = BuildSpec(instanceId, "hash-candidate");
        var createdIds = new List<string>();
        var sut = new DockerRollbackCoordinator();

        var networkName = await CreateHarnessNetworkNameAsync();
        var volumeName = await _docker.CreateLabeledVolumeAsync();

        try
        {
            var previousId = await CreateAndStartAsync(
                client, instanceId, "hash-previous", new[] { "sleep", "3600" }, createdIds,
                networkName, volumeBind: $"{volumeName}:/data");
            (await client.Containers.StopContainerAsync(previousId, new ContainerStopParameters(), CancellationToken.None))
                .Should().BeTrue();

            var candidateId = await CreateAndStartAsync(
                client, instanceId, "hash-candidate", new[] { "sh", "-c", "echo nope; exit 1" }, createdIds,
                networkName, volumeBind: $"{volumeName}:/data");
            await WaitForExitAsync(client, candidateId);

            var candidate = await ToRefAsync(client, candidateId);
            var previous = await ToRefAsync(client, previousId);

            var result = await sut.RollbackAsync(client, spec, candidate, previous, CancellationToken.None);

            (await ContainerExistsAsync(client, candidateId)).Should().BeFalse();
            result.PreviousRestored.Should().BeTrue();
            (await IsRunningAsync(client, previousId)).Should().BeTrue();

            var networks = await _docker.ListLabeledNetworksAsync();
            networks.Any(n => string.Equals(n.Name, networkName, StringComparison.Ordinal))
                .Should().BeTrue("the shared network survives the rollback");

            var volumes = await _docker.ListLabeledVolumesAsync();
            volumes.Any(v => string.Equals(v.Name, volumeName, StringComparison.Ordinal))
                .Should().BeTrue("the retained named volume survives the rollback");
        }
        finally
        {
            await RemoveAllAsync(client, createdIds);
        }
    }


    [SkippableFact]
    public async Task RollbackAsync_tolerates_an_unreachable_daemon_without_throwing()
    {
        var instanceId = Guid.NewGuid();
        var spec = BuildSpec(instanceId, "hash-candidate");
        var sut = new DockerRollbackCoordinator();

        var client = Substitute.For<IDockerClient>();
        client.Containers.Returns(_ => throw new TimeoutException("simulated unreachable docker daemon"));

        var candidate = new DockerContainerRef { Id = "candidate-id", Name = "reeve-rb-candidate", SpecHash = "hash-candidate", IsRunning = false };
        var previous = new DockerContainerRef { Id = "previous-id", Name = "reeve-rb-previous", SpecHash = "hash-previous", IsRunning = false };

        var act = async () => await sut.RollbackAsync(client, spec, candidate, previous, CancellationToken.None);

        var result = await act.Should().NotThrowAsync("an unreachable daemon degrades to a best-effort result, never a throw");
        result.Which.PreviousRestored.Should().BeFalse("nothing could be restored against a dead daemon");
        result.Which.CapturedEvidence.Should().BeNull("no evidence is readable from a dead daemon");
    }


    private static WorkloadDesiredSpec BuildSpec(Guid instanceId, string specHash)
    {
        var inst8 = instanceId.ToString("N")[..8];
        var containerName = DockerResourceNamer.Instance.ObjectName(new WorkloadNamingSubject
                        {
                            GroupId = GroupId,
                            WorkloadId = WorkloadId,
                            Inst8 = inst8,
                        });

        return new WorkloadDesiredSpec
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
            Image = Image,
            Kind = WorkloadKind.SingleNode,
            Health = new WorkloadHealth { Mode = WorkloadHealthMode.Container },
            SpecHash = specHash,
        };
    }

    private async Task<string> CreateAndStartAsync(
        IDockerClient client,
        Guid instanceId,
        string specHash,
        IList<string> cmd,
        List<string> createdIds,
        string? networkName = null,
        string? volumeBind = null)
    {
        var labels = new Dictionary<string, string>(_docker.BuildLabels(), StringComparer.Ordinal)
        {
            [DockerProviderConstants.InstanceLabelKey] = instanceId.ToString("D"),
            [DockerProviderConstants.SpecHashLabelKey] = specHash,
            [DockerProviderConstants.ManagedByLabelKey] = DockerProviderConstants.ManagedByLabelValue,
        };

        var hostConfig = new HostConfig();
        if (networkName is not null)
        {
            hostConfig.NetworkMode = networkName;
        }

        if (volumeBind is not null)
        {
            hostConfig.Binds = new List<string> { volumeBind };
        }

        var created = await client.Containers.CreateContainerAsync(
            new CreateContainerParameters
            {
                Image = Image,
                Cmd = cmd,
                Labels = labels,
                Tty = false,
                Name = $"reeve-it-rb-{_docker.RunId}-{Guid.NewGuid():N}",
                HostConfig = hostConfig,
            },
            CancellationToken.None).ConfigureAwait(false);

        createdIds.Add(created.ID);

        await client.Containers.StartContainerAsync(created.ID, new ContainerStartParameters(), CancellationToken.None)
            .ConfigureAwait(false);

        return created.ID;
    }

    private async Task<string> CreateHarnessNetworkNameAsync()
    {
        var id = await _docker.CreateLabeledNetworkAsync();
        var networks = await _docker.ListLabeledNetworksAsync();
        var match = networks.FirstOrDefault(n => string.Equals(n.ID, id, StringComparison.Ordinal));
        match.Should().NotBeNull("the harness network was just created");
        return match!.Name;
    }

    private static async Task<DockerContainerRef> ToRefAsync(IDockerClient client, string containerId)
    {
        var inspected = await client.Containers.InspectContainerAsync(containerId, CancellationToken.None).ConfigureAwait(false);
        var name = inspected.Name?.TrimStart('/') ?? containerId;
        var specHash = inspected.Config?.Labels is { } labels
            && labels.TryGetValue(DockerProviderConstants.SpecHashLabelKey, out var h)
                ? h
                : null;
        return new DockerContainerRef
        {
            Id = inspected.ID,
            Name = name,
            SpecHash = specHash,
            IsRunning = inspected.State?.Running == true,
        };
    }

    private async Task<bool> EnsureImageAsync(IDockerClient client)
    {
        try
        {
            await client.Images.InspectImageAsync(Image, CancellationToken.None).ConfigureAwait(false);
            return true;
        }
        catch (DockerApiException)
        {
        }

        try
        {
            await client.Images.CreateImageAsync(
                new ImagesCreateParameters { FromImage = ImageRepo, Tag = ImageTag },
                authConfig: null,
                new Progress<JSONMessage>(),
                CancellationToken.None).ConfigureAwait(false);
            await client.Images.InspectImageAsync(Image, CancellationToken.None).ConfigureAwait(false);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static async Task WaitForExitAsync(IDockerClient client, string containerId)
    {
        var deadline = DateTime.UtcNow + WaitBudget;
        while (DateTime.UtcNow < deadline)
        {
            var inspect = await client.Containers.InspectContainerAsync(containerId, CancellationToken.None).ConfigureAwait(false);
            if (inspect.State is { Running: false })
            {
                return;
            }

            await Task.Delay(200).ConfigureAwait(false);
        }
    }

    private static async Task<bool> ContainerExistsAsync(IDockerClient client, string id)
    {
        try
        {
            await client.Containers.InspectContainerAsync(id, CancellationToken.None).ConfigureAwait(false);
            return true;
        }
        catch (DockerApiException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            return false;
        }
    }

    private static async Task<bool> IsRunningAsync(IDockerClient client, string id)
    {
        var inspected = await client.Containers.InspectContainerAsync(id, CancellationToken.None).ConfigureAwait(false);
        return inspected.State?.Running == true;
    }

    private static async Task RemoveAllAsync(IDockerClient client, IEnumerable<string> ids)
    {
        foreach (var id in ids)
        {
            try
            {
                await client.Containers.RemoveContainerAsync(
                    id,
                    new ContainerRemoveParameters { Force = true, RemoveVolumes = true },
                    CancellationToken.None).ConfigureAwait(false);
            }
            catch (DockerApiException)
            {
            }
        }
    }
}
