using System.Threading;
using System.Threading.Tasks;
using Reeve.Workloads.Abstractions;

namespace Reeve.Workloads.Providers.Docker.Orphans;

internal sealed class SettingsDockerSocketUriReader(IWorkloadOrchestratorSecretReader settings) : IDockerSocketUriReader
{
    public async Task<string?> GetDockerSocketUriAsync(CancellationToken cancellationToken = default)
    {
        var resolved = await settings.GetDecryptedSettingsAsync(cancellationToken).ConfigureAwait(false);
        return DockerProviderSettings.From(resolved).SocketUri;
    }
}
