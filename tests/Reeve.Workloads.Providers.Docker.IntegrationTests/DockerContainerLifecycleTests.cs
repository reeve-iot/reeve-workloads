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
using Reeve.Workloads.Abstractions.Providers;
using Reeve.Workloads.Domain;
using Reeve.Workloads.Providers.Docker;

namespace Reeve.Workloads.Providers.Docker.IntegrationTests;

[Collection(DockerIntegrationCollection.Name)]
public sealed class DockerContainerLifecycleTests : IAsyncLifetime
{
    private const string GroupId = "com.reeve.ctr-test";
    private const string WorkloadId = "ct";

    private const string TokenEnv = "REEVE_WORKLOAD_TOKEN";
    private const string TokenId = "workload-token";

    private const string BusyboxRepository = "busybox";
    private const string BusyboxTag = "1.36";
    private const string BusyboxImage = BusyboxRepository + ":" + BusyboxTag;

    private readonly DockerDaemonFixture _docker;
    private readonly DockerContainerManager _sut = new();

    private readonly List<string> _containersToCleanup = new();

    public DockerContainerLifecycleTests(DockerDaemonFixture docker) => _docker = docker;

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
    }

    [Fact]
    public void BuildContainerParameters_collapses_to_one_container_and_merges_all_fragments()
    {
        var instanceId = Guid.NewGuid();
        const string token = "rwt_pure_build_token_value_abc123";

        var privileges = new ResolvedPrivileges
        {
            Capabilities = new[] { "NET_ADMIN" },
            Privileged = false,
            RunAsRoot = true,
        };
        var resources = new ResolvedResources
        {
            LimitsMemory = "64Mi",
            RequestsMemory = "32Mi",
            LimitsCpu = "500m",
        };
        var network = new ResolvedNetwork
        {
            HostNetwork = false,
            Ports = new[]
            {
                new ResolvedPort { Name = "api", Port = 8080, Protocol = "tcp", HostPort = 18080 },
            },
        };
        var observability = new WorkloadObservability
        {
            Metrics = new WorkloadMetrics { Port = 9090, Path = "/metrics" },
        };

        var (baseSpec, containerName) = BuildSpec(
            instanceId, privileges: privileges, resources: resources, network: network, observability: observability);

        var env = new[]
        {
            new ResolvedEnvVar { Name = "FOO", Kind = ResolvedEnvKind.Literal, Value = "bar" },
            new ResolvedEnvVar { Name = "REEVE_SPEC_HASH", Kind = ResolvedEnvKind.SpecHash, Value = baseSpec.SpecHash },
            new ResolvedEnvVar
            {
                Name = TokenEnv,
                Kind = ResolvedEnvKind.SecretRef,
                SecretRef = TokenEnv,
                CredentialId = TokenId,
            },
        };
        var spec = baseSpec with { Env = env };

        const string networkName = DockerProviderConstants.DefaultNetworkName;
        var ctx = BuildContext(spec, containerName, token, networkName);

        var parameters = _sut.BuildContainerParameters(ctx);

        parameters.Image.Should().Be(spec.Image);
        parameters.Name.Should().Be(containerName);

        parameters.Labels.Should().ContainKey(DockerProviderConstants.InstanceLabelKey)
            .WhoseValue.Should().Be(instanceId.ToString("D"));
        parameters.Labels.Should().ContainKey(DockerProviderConstants.SpecHashLabelKey)
            .WhoseValue.Should().Be(spec.SpecHash);
        parameters.Labels.Should().ContainKey(DockerProviderConstants.MetricsScrapeLabelKey)
            .WhoseValue.Should().Be("true");
        parameters.Labels.Should().ContainKey(DockerProviderConstants.MetricsPortLabelKey)
            .WhoseValue.Should().Be("9090");
        parameters.Labels.Should().ContainKey("prometheus.io/scrape").WhoseValue.Should().Be("true");

        parameters.Env.Should().Contain("FOO=bar");
        parameters.Env.Should().Contain($"REEVE_SPEC_HASH={spec.SpecHash}");
        parameters.Env.Should().Contain($"{TokenEnv}={token}");
        parameters.Env!.Count(e => e.StartsWith(TokenEnv + "=", StringComparison.Ordinal))
            .Should().Be(1, "the credential env must not be duplicated");

        parameters.User.Should().Be("0");

        var hostConfig = parameters.HostConfig;
        hostConfig.Should().NotBeNull();

        hostConfig!.RestartPolicy.Should().NotBeNull();
        hostConfig.RestartPolicy!.Name.Should().Be(RestartPolicyKind.UnlessStopped);

        hostConfig.CapDrop.Should().Contain("ALL");
        hostConfig.CapAdd.Should().Contain("NET_ADMIN");
        hostConfig.Privileged.Should().BeFalse();

        hostConfig.Memory.Should().Be(64L * 1024 * 1024);
        hostConfig.MemoryReservation.Should().Be(32L * 1024 * 1024);
        hostConfig.NanoCPUs.Should().Be(500_000_000L);

        hostConfig.NetworkMode.Should().Be(networkName);
        hostConfig.PortBindings.Should().ContainKey("8080/tcp");
        hostConfig.PortBindings!["8080/tcp"].Single().HostPort.Should().Be("18080");

        parameters.ExposedPorts.Should().ContainKey("8080/tcp");
    }

    [Fact]
    public void BuildContainerParameters_injects_a_provisioned_credential_even_when_the_spec_declares_no_secret_env()
    {
        var instanceId = Guid.NewGuid();
        const string token = "rwt_no_declared_secret_env_xyz789";

        var (spec, containerName) = BuildSpec(instanceId);
        var ctx = BuildContext(spec, containerName, token, DockerProviderConstants.DefaultNetworkName);

        var parameters = _sut.BuildContainerParameters(ctx);

        parameters.Env.Should().Contain($"{TokenEnv}={token}");
        parameters.HostConfig!.RestartPolicy!.Name.Should().Be(RestartPolicyKind.UnlessStopped);
    }

    [Fact]
    public void BuildContainerParameters_mounts_each_declared_volume_by_resource_name_and_honours_read_only()
    {
        var instanceId = Guid.NewGuid();
        var (baseSpec, containerName) = BuildSpec(instanceId);

        var volumes = new[]
        {
            new ResolvedVolume
            {
                Id = "data",
                MountPath = "/var/lib/reeve/data",
                Size = "1Gi",
                Criticality = VolumeCriticality.Critical,
                AccessMode = "rwo",
                ResourceName = "reeve-vol-data-" + Inst8(instanceId),
            },
            new ResolvedVolume
            {
                Id = "config",
                MountPath = "/etc/reeve/config",
                Size = "16Mi",
                Criticality = VolumeCriticality.Recoverable,
                AccessMode = "rox",
                ResourceName = "reeve-vol-config-" + Inst8(instanceId),
            },
        };
        var spec = baseSpec with { Volumes = volumes };
        var ctx = BuildContext(spec, containerName, "rwt_volmount_token", DockerProviderConstants.DefaultNetworkName);

        var parameters = _sut.BuildContainerParameters(ctx);

        var mounts = parameters.HostConfig!.Mounts;
        mounts.Should().NotBeNull().And.HaveCount(2, "every declared spec.Volume must be mounted");

        var data = mounts!.Single(m => m.Target == "/var/lib/reeve/data");
        data.Type.Should().Be("volume");
        data.Source.Should().Be("reeve-vol-data-" + Inst8(instanceId), "the mount binds the named volume by ResourceName");
        data.ReadOnly.Should().BeFalse("a default rwo volume mounts read-write");

        var config = mounts.Single(m => m.Target == "/etc/reeve/config");
        config.Type.Should().Be("volume");
        config.Source.Should().Be("reeve-vol-config-" + Inst8(instanceId));
        config.ReadOnly.Should().BeTrue("an 'ro*' access mode mounts read-only");
    }

    [Fact]
    public void BuildContainerParameters_emits_no_mounts_when_the_workload_declares_no_volumes()
    {
        var instanceId = Guid.NewGuid();
        var (spec, containerName) = BuildSpec(instanceId);
        spec.Volumes.Should().BeEmpty("this spec declares no volumes");

        var ctx = BuildContext(spec, containerName, "rwt_novol_token", DockerProviderConstants.DefaultNetworkName);

        var parameters = _sut.BuildContainerParameters(ctx);

        parameters.HostConfig!.Mounts.Should().BeNullOrEmpty("an empty Volumes set must not fabricate mounts");
    }

    [SkippableFact]
    public async Task CreateStart_find_read_token_stop_remove_full_lifecycle()
    {
        _docker.EnsureAvailable();
        var client = _docker.Client!;
        await EnsureBusyboxAsync(client);

        var instanceId = Guid.NewGuid();
        const string token = "rwt_lifecycle_injected_token_5f3a";
        var reference = ReferenceFor(instanceId);

        var (spec, containerName) = BuildSpec(instanceId);
        var ctx = BuildContext(spec, containerName, token, "bridge");

        var parameters = _sut.BuildContainerParameters(ctx);
        parameters.Cmd = new[] { "sleep", "3600" };

        var created = await _sut.CreateAndStartAsync(client, parameters, [], CancellationToken.None);
        created.Container.Should().NotBeNull();
        if (created.Container is not null)
        {
            _containersToCleanup.Add(created.Container.Id);
        }

        created.Succeeded.Should().BeTrue(created.Message);
        created.Container!.IsRunning.Should().BeTrue();
        created.Container.Name.Should().Be(containerName);

        var found = await _sut.FindContainerAsync(client, reference, CancellationToken.None);
        found.Should().NotBeNull("the container carries this instance's label");
        found!.Id.Should().Be(created.Container.Id);
        found.IsRunning.Should().BeTrue();
        found.SpecHash.Should().Be(spec.SpecHash, "the spec-hash label discriminates current-vs-previous");

        var held = await _sut.ReadHeldEnvAsync(client, found, [TokenEnv], CancellationToken.None);
        held.Should().Equal(new Dictionary<string, string> { [TokenEnv] = token });

        var stopped = await _sut.StopContainerAsync(client, found.Id, CancellationToken.None);
        stopped.Succeeded.Should().BeTrue(stopped.Message);

        var afterStop = await _sut.FindContainerAsync(client, reference, CancellationToken.None);
        afterStop.Should().NotBeNull("a stopped container is still addressable by label");
        afterStop!.IsRunning.Should().BeFalse();

        var removed = await _sut.RemoveContainerAsync(client, found.Id, CancellationToken.None);
        removed.Succeeded.Should().BeTrue(removed.Message);

        var afterRemove = await _sut.FindContainerAsync(client, reference, CancellationToken.None);
        afterRemove.Should().BeNull("the removed container is no longer addressable");
    }

    [SkippableFact]
    public async Task Volume_mount_persists_workload_data_across_a_recreate()
    {
        _docker.EnsureAvailable();
        var client = _docker.Client!;
        await EnsureBusyboxAsync(client);

        var instanceId = Guid.NewGuid();
        const string mountPath = "/data";
        var marker = "persisted-" + Guid.NewGuid().ToString("N");

        var volumeName = await _docker.CreateLabeledVolumeAsync("fixf");

        var volumes = new[]
        {
            new ResolvedVolume
            {
                Id = "data",
                MountPath = mountPath,
                Size = "1Gi",
                Criticality = VolumeCriticality.Critical,
                AccessMode = "rwo",
                ResourceName = volumeName,
            },
        };

        var (baseSpec, containerName1) = BuildSpec(instanceId);
        var spec1 = baseSpec with { Volumes = volumes };
        var ctx1 = BuildContext(spec1, containerName1, "rwt_fixf_write_token", "bridge");
        var parameters1 = _sut.BuildContainerParameters(ctx1);
        parameters1.Cmd = new[] { "sh", "-c", $"echo '{marker}' > {mountPath}/marker.txt; echo WROTE_OK; sleep 3600" };

        var created1 = await _sut.CreateAndStartAsync(client, parameters1, [], CancellationToken.None);
        if (created1.Container is not null)
        {
            _containersToCleanup.Add(created1.Container.Id);
        }

        created1.Succeeded.Should().BeTrue(created1.Message);
        var id1 = created1.Container!.Id;

        var inspected1 = await client.Containers.InspectContainerAsync(id1, CancellationToken.None);
        inspected1.Mounts.Should().Contain(
            m => m.Destination == mountPath && m.Name == volumeName,
            "the workload's named volume must actually be mounted (not ephemeral storage)");

        (await WaitForLogSubstringAsync(client, id1, "WROTE_OK", TimeSpan.FromSeconds(30)))
            .Should().BeTrue("container #1 should have written the marker into the volume");

        (await _sut.RemoveContainerAsync(client, id1, CancellationToken.None)).Succeeded.Should().BeTrue();

        var container2Name = containerName1 + "-recreate";
        var spec2 = baseSpec with { Volumes = volumes };
        var ctx2 = BuildContext(spec2, container2Name, "rwt_fixf_read_token", "bridge");
        var parameters2 = _sut.BuildContainerParameters(ctx2);
        parameters2.Name = container2Name;
        parameters2.Cmd = new[] { "sh", "-c", $"cat {mountPath}/marker.txt; sleep 3600" };

        var created2 = await _sut.CreateAndStartAsync(client, parameters2, [], CancellationToken.None);
        if (created2.Container is not null)
        {
            _containersToCleanup.Add(created2.Container.Id);
        }

        created2.Succeeded.Should().BeTrue(created2.Message);
        var id2 = created2.Container!.Id;

        var inspected2 = await client.Containers.InspectContainerAsync(id2, CancellationToken.None);
        inspected2.Mounts.Should().Contain(
            m => m.Destination == mountPath && m.Name == volumeName,
            "the recreated container must re-attach the same named volume");

        (await WaitForLogSubstringAsync(client, id2, marker, TimeSpan.FromSeconds(30)))
            .Should().BeTrue("the marker written before the recreate must persist on the re-mounted volume");
    }

    [SkippableFact]
    public async Task Remove_is_idempotent_when_the_container_is_already_gone()
    {
        _docker.EnsureAvailable();
        var client = _docker.Client!;
        await EnsureBusyboxAsync(client);

        var instanceId = Guid.NewGuid();
        var (spec, containerName) = BuildSpec(instanceId);
        var ctx = BuildContext(spec, containerName, "rwt_idem_token", "bridge");
        var parameters = _sut.BuildContainerParameters(ctx);
        parameters.Cmd = new[] { "sleep", "3600" };

        var created = await _sut.CreateAndStartAsync(client, parameters, [], CancellationToken.None);
        created.Succeeded.Should().BeTrue(created.Message);
        _containersToCleanup.Add(created.Container!.Id);

        (await _sut.RemoveContainerAsync(client, created.Container.Id, CancellationToken.None))
            .Succeeded.Should().BeTrue();

        (await _sut.RemoveContainerAsync(client, created.Container.Id, CancellationToken.None))
            .Succeeded.Should().BeTrue("remove is idempotent — an already-gone container is the desired state");
    }

    [SkippableFact]
    public async Task CreateAndStart_returns_the_created_ref_when_start_fails()
    {
        _docker.EnsureAvailable();
        var client = _docker.Client!;
        await EnsureBusyboxAsync(client);

        var instanceId = Guid.NewGuid();
        var (spec, containerName) = BuildSpec(instanceId);
        var ctx = BuildContext(spec, containerName, "rwt_startfail_token", "bridge");

        var parameters = _sut.BuildContainerParameters(ctx);
        parameters.Cmd = new[] { "/nonexistent-binary-zzz-9c1e" };

        var result = await _sut.CreateAndStartAsync(client, parameters, [], CancellationToken.None);
        if (result.Container is not null)
        {
            _containersToCleanup.Add(result.Container.Id);
        }

        result.Succeeded.Should().BeFalse("the container was created but could not start");
        result.Container.Should().NotBeNull(
            "the created candidate ref must be returned so the shell/rollback can capture + clean it up");
        result.FailureKind.Should().NotBe(DockerFailureKind.None);

        var found = await _sut.FindContainerAsync(client, ReferenceFor(instanceId), CancellationToken.None);
        found.Should().NotBeNull();
        found!.IsRunning.Should().BeFalse();
    }

    private static string Inst8(Guid instanceId) => instanceId.ToString("N")[..8];

    private static WorkloadRef ReferenceFor(Guid instanceId) => new()
    {
        InstanceId = instanceId,
        GroupId = GroupId,
        WorkloadId = WorkloadId,
        Inst8 = Inst8(instanceId),
        Provider = DockerProvider.Id,
    };

    private static (WorkloadDesiredSpec Spec, string ContainerName) BuildSpec(
        Guid instanceId,
        ResolvedPrivileges? privileges = null,
        ResolvedResources? resources = null,
        ResolvedNetwork? network = null,
        WorkloadObservability? observability = null)
    {
        var inst8 = Inst8(instanceId);
        var containerName = DockerResourceNamer.Instance.ObjectName(new WorkloadNamingSubject
                        {
                            GroupId = GroupId,
                            WorkloadId = WorkloadId,
                            Inst8 = inst8,
                        });

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
            Privileges = privileges,
            Resources = resources,
            Network = network,
            Observability = observability,
            ProvisionedCredentials = [new ProvisionedCredential(TokenId, TokenEnv)],
            SpecHash = "spechash-" + inst8,
        };

        return (spec, containerName);
    }

    private static DockerContainerRenderContext BuildContext(
        WorkloadDesiredSpec spec, string containerName, string token, string networkName)
        => new()
        {
            Spec = spec,
            ContainerName = containerName,
            Credentials = new Dictionary<string, string> { [TokenId] = token },
            SharedNetworkName = networkName,
            Privilege = new DockerPrivilegeRenderer().RenderPrivileges(spec),
            Resource = new DockerResourceRenderer().RenderResources(spec),
            NetworkHostConfig = new DockerNetworkManager().RenderNetworking(spec, networkName).HostConfig,
        };

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
