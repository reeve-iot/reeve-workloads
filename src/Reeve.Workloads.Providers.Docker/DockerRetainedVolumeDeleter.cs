using System;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Docker.DotNet;
using Microsoft.Extensions.Logging;
using Reeve.Workloads.Abstractions;
using Reeve.Workloads.Abstractions.Domain.Entities;
using Reeve.Workloads.Providers.Docker.Orphans;

namespace Reeve.Workloads.Providers.Docker;

internal sealed class DockerRetainedVolumeDeleter : IRetainedVolumeSchemeDeleter
{
    private readonly IDockerClientFactory clientFactory;
    private readonly IDockerSocketUriReader socketUriReader;
    private readonly ILogger<DockerRetainedVolumeDeleter> logger;

    public DockerRetainedVolumeDeleter(
        IDockerClientFactory clientFactory,
        IDockerSocketUriReader socketUriReader,
        ILogger<DockerRetainedVolumeDeleter> logger)
    {
        this.clientFactory = clientFactory ?? throw new ArgumentNullException(nameof(clientFactory));
        this.socketUriReader = socketUriReader ?? throw new ArgumentNullException(nameof(socketUriReader));
        this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public RetainedVolumeSchemeId NamingScheme => RetainedVolumeSchemeId.Docker;

    public async Task<bool> TryDeletePhysicalVolumeAsync(RetainedVolume retainedVolume, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(retainedVolume);

        if (retainedVolume.VolumeNamingScheme != RetainedVolumeSchemeId.Docker)
        {
            logger.LogWarning(
                "Purge of retained volume {ResourceName} refused: its naming scheme is {Scheme}, which the Docker " +
                "deleter cannot address — leaving it retained (fail-closed).",
                retainedVolume.ResourceName, retainedVolume.VolumeNamingScheme);
            return false;
        }

        if (retainedVolume.Provider != DockerProvider.Id)
        {
            logger.LogWarning(
                "Purge of retained volume {ResourceName} refused: it was retained by provider {Provider}, not Docker " +
                "— leaving it retained (fail-closed).",
                retainedVolume.ResourceName, retainedVolume.Provider);
            return false;
        }

        try
        {
            var socketUri = await socketUriReader.GetDockerSocketUriAsync(cancellationToken).ConfigureAwait(false);
            var client = clientFactory.CreateClient(socketUri);
            await client.Volumes.RemoveAsync(retainedVolume.ResourceName, force: false, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (DockerApiException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            return true;
        }
        catch (DockerApiException ex) when (ex.StatusCode == HttpStatusCode.Conflict)
        {
            logger.LogWarning(
                ex,
                "Purge of retained volume {ResourceName} refused: a container still uses the volume — leaving it retained (fail-closed).",
                retainedVolume.ResourceName);
            return false;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(
                ex, "Purge of retained volume {ResourceName} could not remove the Docker volume; leaving it retained for a retry.",
                retainedVolume.ResourceName);
            return false;
        }
    }
}
