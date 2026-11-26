using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Docker.DotNet;
using Docker.DotNet.Models;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
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
[Trait("lane", "smoke")]
public sealed class DockerSmokeTests : IAsyncLifetime
{
    private const string GroupId = "com.reeve.smoke-test";
    private const string WorkloadId = "smk";

    // Idles under CapDrop=[ALL], which every container the provider creates gets; stock nginx needs CHOWN/SETUID at start.
    private const string BlockingImage =
        "registry.k8s.io/pause@sha256:ee6521f290b2168b6e0935a181d4cff9be1ac3f505666ef0e3c98fae8199917a";

    private const string GatewayUrlEnvName = "REEVE_GATEWAY_URL";
    private const string GatewayUrl = "http://reeve-gateway.smoke.test:8080/workloads/connect";
    private const string TestToken = "rwt_smoke_test_injected_token_9f2a7c";

    private readonly DockerDaemonFixture _docker;

    private readonly DockerClientFactory _factory = new(NullLogger<DockerClientFactory>.Instance);

    private readonly Guid _instanceId = Guid.NewGuid();
    private readonly string _networkName = "reeve-smoke-" + Guid.NewGuid().ToString("N")[..8];

    public DockerSmokeTests(DockerDaemonFixture docker) => _docker = docker;

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        await BestEffortCleanupAsync().ConfigureAwait(false);
        _factory.Dispose();
    }

    [SkippableFact]
    public async Task Apply_lands_a_running_container_with_gateway_env_spec_hash_label_on_the_shared_network()
    {
        var client = await ArrangeDaemonAsync();

        var provider = BuildProvider();
        var spec = BuildSpec();
        var reference = WorkloadRef.FromDesiredSpec(spec);

        try
        {
            var apply = await provider.ApplyAsync(spec, null, CancellationToken.None);

            Skip.If(
                !apply.Succeeded && apply.Reason == WorkloadApplyReason.ProviderUnreachable,
                $"docker daemon / public registry unreachable on this runner: {apply.Message}");

            apply.Succeeded.Should().BeTrue(apply.Message);
            apply.AppliedSpecHash.Should().Be(spec.SpecHash, "a successful apply echoes the applied spec hash");

            var status = await WaitForProviderReadyAsync(provider, reference);
            status.Phase.Should().Be(WorkloadProviderPhase.Ready, status.Message);
            status.ProviderReady.Should().BeTrue("a running container with no failing healthcheck is provider-ready");

            var found = await new DockerContainerManager().FindContainerAsync(client, reference, CancellationToken.None);
            found.Should().NotBeNull("Apply materialized a container carrying this instance's identity label");
            found!.IsRunning.Should().BeTrue("the applied container is running");

            var inspected = await client.Containers.InspectContainerAsync(found.Id, CancellationToken.None);
            inspected.Config.Should().NotBeNull();
            inspected.NetworkSettings.Should().NotBeNull();

            inspected.Config!.Env.Should().Contain(
                $"REEVE_WORKLOAD_TOKEN={TestToken}",
                "the provider must inject the per-instance gateway token env onto the container");

            inspected.Config!.Env.Should().Contain(
                $"{GatewayUrlEnvName}={GatewayUrl}",
                "the provider must render the REEVE_GATEWAY_URL bootstrap env onto the container");

            inspected.Config!.Labels.Should().ContainKey(DockerProviderConstants.SpecHashLabelKey)
                .WhoseValue.Should().Be(spec.SpecHash, "the spec-hash label discriminates the applied revision");

            inspected.NetworkSettings!.Networks.Should().ContainKey(
                _networkName, "the applied container is attached to the shared reeve workloads network");

            await provider.TeardownAsync(reference, retainVolumes: false, CancellationToken.None);

            (await new DockerContainerManager().FindContainerAsync(client, reference, CancellationToken.None))
                .Should().BeNull("teardown removed the instance's container — it is no longer addressable by label");
        }
        finally
        {
            await BestEffortCleanupAsync();
        }
    }

    private DockerWorkloadProvider BuildProvider()
    {
        var containerManager = new DockerContainerManager();
        return new DockerWorkloadProvider(
            clientFactory: _factory,
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
            scopeFactory: DockerProviderScope.For(new FakeSettingsService(_networkName), new FakeCredentialPort(TestToken)),
            logger: NullLogger<DockerWorkloadProvider>.Instance);
    }

    private async Task<IDockerClient> ArrangeDaemonAsync()
    {
        _docker.EnsureAvailable();
        var client = _factory.CreateClient(null);
        Skip.IfNot(
            await IsReachableAsync(client),
            "the provider's DockerClientFactory client (platform-default socket) is not reachable on this runner.");
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

    private static async Task<WorkloadStatus> WaitForProviderReadyAsync(DockerWorkloadProvider provider, WorkloadRef reference)
    {
        WorkloadStatus status = await provider.GetStatusAsync(reference, CancellationToken.None);
        for (var attempt = 0; attempt < 20 && !status.ProviderReady; attempt++)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(250));
            status = await provider.GetStatusAsync(reference, CancellationToken.None);
        }

        return status;
    }

    private WorkloadDesiredSpec BuildSpec()
    {
        var inst8 = _instanceId.ToString("N")[..8];
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
            InstanceId = _instanceId,
            Inst8 = inst8,
            Provider = DockerProvider.Id,
            ResourceName = containerName,
            SecretName = containerName,
            Labels = new Dictionary<string, string>(),
            Image = BlockingImage,
            Kind = WorkloadKind.SingleNode,
            Health = new WorkloadHealth { Mode = WorkloadHealthMode.Container },
            Env = new[]
            {
                new ResolvedEnvVar { Name = GatewayUrlEnvName, Kind = ResolvedEnvKind.Literal, Value = GatewayUrl },
                new ResolvedEnvVar { Name = "REEVE_SPEC_HASH", Kind = ResolvedEnvKind.SpecHash, Value = string.Empty },
            },
            ProvisionedCredentials = [new ProvisionedCredential("workload-token", "REEVE_WORKLOAD_TOKEN")],
            SpecHash = "smoke-spechash-" + inst8,
        };
    }

    private async Task BestEffortCleanupAsync()
    {
        try
        {
            var client = _factory.CreateClient(null);

            var listed = await client.Containers.ListContainersAsync(
                new ContainersListParameters
                {
                    All = true,
                    Filters = new Dictionary<string, IDictionary<string, bool>>(StringComparer.Ordinal)
                    {
                        ["label"] = new Dictionary<string, bool>(StringComparer.Ordinal)
                        {
                            [$"{DockerProviderConstants.InstanceLabelKey}={_instanceId:D}"] = true,
                        },
                    },
                },
                CancellationToken.None).ConfigureAwait(false);

            foreach (var container in listed)
            {
                await SafeAsync(() => client.Containers.RemoveContainerAsync(
                    container.ID, new ContainerRemoveParameters { Force = true }, CancellationToken.None));
            }

            await SafeAsync(() => client.Networks.DeleteNetworkAsync(_networkName, CancellationToken.None));
        }
        catch (Exception)
        {
        }
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
        private readonly string _networkName;

        public FakeSettingsService(string networkName) => _networkName = networkName;

        public Task<WorkloadOrchestratorSettingsResponse> GetAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(new WorkloadOrchestratorSettingsResponse
            {
                Settings = new WorkloadOrchestratorSettings
                {
                    Provider = DockerProvider.Id,
                }
                    .WithProvider(DockerProvider.Id, new DockerProviderSettings { SocketUri = null, NetworkName = _networkName }),
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

    private sealed class FakeCredentialPort(string token) : IWorkloadCredentialPort
    {
        public Task<string?> ResolveOrMintAsync(
            Guid instanceId, string credentialId, string? existingMaterial, CancellationToken cancellationToken = default)
            => Task.FromResult<string?>(existingMaterial ?? token);

        public Task RevokeAllAsync(
            Guid instanceId, Guid? actorUserId = null, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }

}
