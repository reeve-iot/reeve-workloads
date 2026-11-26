using System.Text.Json;
using Microsoft.Extensions.Logging;
using Reeve.Workloads.Abstractions.Contracts;
using Reeve.Workloads.Abstractions.Providers;

namespace Reeve.Workloads.Providers.Docker;

internal sealed class DockerAutoDetect : IWorkloadProviderAutoDetect
{
    private readonly IDockerClientFactory clientFactory;
    private readonly ILogger<DockerAutoDetect> logger;

    public DockerAutoDetect(IDockerClientFactory clientFactory, ILogger<DockerAutoDetect> logger)
    {
        ArgumentNullException.ThrowIfNull(clientFactory);
        ArgumentNullException.ThrowIfNull(logger);
        this.clientFactory = clientFactory;
        this.logger = logger;
    }

    public WorkloadProviderId Provider => DockerProvider.Id;

    public int Priority => IWorkloadProviderAutoDetect.LocalRuntimePriority;

    public async Task<bool> IsAvailableAsync(JsonElement providerSettings, CancellationToken cancellationToken = default)
    {
        var socketUri = DockerProviderSettings.From(providerSettings).SocketUri;
        try
        {
            var client = clientFactory.CreateClient(socketUri);
            await client.System.PingAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogDebug(
                ex,
                "Docker auto-detect found no usable daemon at {SocketUri}; the chain moves on.",
                socketUri ?? "the platform default socket");
            return false;
        }
    }
}
