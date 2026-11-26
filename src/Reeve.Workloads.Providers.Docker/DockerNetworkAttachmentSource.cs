using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Reeve.Workloads.Abstractions.Contracts;
using Reeve.Workloads.Abstractions.Network;

namespace Reeve.Workloads.Providers.Docker;

internal sealed class DockerNetworkAttachmentSource(
    IDockerClientFactory clientFactory,
    IDockerNetworkManager networkManager) : INetworkAttachmentSource
{
    private readonly IDockerClientFactory clientFactory = clientFactory;
    private readonly IDockerNetworkManager networkManager = networkManager;

    public WorkloadProviderId Provider => DockerProvider.Id;

    public async Task<WorkloadNetworkAttachmentListResponse> ListAsync(
        WorkloadOrchestratorSettings settings,
        CancellationToken cancellationToken)
    {
        try
        {
            var docker = DockerProviderSettings.From(settings);
            var client = clientFactory.CreateClient(docker.SocketUri);
            var result = await networkManager
                .ListAttachableNetworksAsync(client, docker, cancellationToken)
                .ConfigureAwait(false);

            if (!result.Succeeded)
            {
                return Unavailable(WorkloadNetworkAttachmentUnavailableReasons.ProviderUnavailable);
            }

            return new WorkloadNetworkAttachmentListResponse
            {
                Provider = DockerProvider.Id,
                Items = result.Networks.Select(ToDto).ToList(),
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return Unavailable(WorkloadNetworkAttachmentUnavailableReasons.ProviderUnavailable);
        }
    }

    private static WorkloadNetworkAttachmentDto ToDto(DockerAttachableNetwork network) => new()
    {
        Name = network.Name,
    };

    private static WorkloadNetworkAttachmentListResponse Unavailable(string reason) => new()
    {
        Provider = DockerProvider.Id,
        Items = Array.Empty<WorkloadNetworkAttachmentDto>(),
        UnavailableReason = reason,
    };

    public string DescribeMissing(string name)
        => $"No network named '{name}' is present on the docker daemon.";
}
