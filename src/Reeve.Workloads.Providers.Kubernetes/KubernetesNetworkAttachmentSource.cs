using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Reeve.Workloads.Abstractions.Contracts;
using Reeve.Workloads.Abstractions.Network;

namespace Reeve.Workloads.Providers.Kubernetes;

internal sealed class KubernetesNetworkAttachmentSource(
    IKubernetesClientFactory clientFactory,
    IKubernetesObjectClient objectClient,
    KubernetesNamespaceResolver namespaceResolver) : INetworkAttachmentSource
{
    private readonly IKubernetesClientFactory clientFactory = clientFactory;
    private readonly IKubernetesObjectClient objectClient = objectClient;

    public WorkloadProviderId Provider => KubernetesProvider.Id;

    public async Task<WorkloadNetworkAttachmentListResponse> ListAsync(
        WorkloadOrchestratorSettings settings,
        CancellationToken cancellationToken)
    {
        var @namespace = namespaceResolver.Resolve(settings);
        if (@namespace is null)
        {
            return Unavailable(WorkloadNetworkAttachmentUnavailableReasons.ProviderUnavailable);
        }

        try
        {
            var client = await clientFactory.CreateClientAsync(cancellationToken).ConfigureAwait(false);
            var result = await objectClient
                .ListNetworkAttachmentDefinitionsAsync(client, @namespace, cancellationToken)
                .ConfigureAwait(false);

            if (result.UnavailableReason is { } reason)
            {
                return Unavailable(reason);
            }

            return new WorkloadNetworkAttachmentListResponse
            {
                Provider = KubernetesProvider.Id,
                Items = result.Items.Select(ToDto).ToList(),
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

    private static WorkloadNetworkAttachmentDto ToDto(KubernetesNetworkAttachment attachment) => new()
    {
        Name = attachment.Name,
    };

    private static WorkloadNetworkAttachmentListResponse Unavailable(string reason) => new()
    {
        Provider = KubernetesProvider.Id,
        Items = Array.Empty<WorkloadNetworkAttachmentDto>(),
        UnavailableReason = reason,
    };

    public string DescribeMissing(string name)
        => $"No NetworkAttachmentDefinition named '{name}' is listed in reeve's workloads namespace.";
}
