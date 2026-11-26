using System.Threading;
using System.Threading.Tasks;
using k8s;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Reeve.Workloads.Core.Providers;
using Reeve.Workloads.Abstractions;

namespace Reeve.Workloads.Providers.Kubernetes;

internal sealed class KubernetesClientFactory : IKubernetesClientFactory, IDisposable
{
    private readonly IServiceScopeFactory scopeFactory;
    private readonly IConfiguration configuration;
    private readonly ILogger<KubernetesClientFactory> logger;
    private readonly Func<KubernetesConnectionSource, IKubernetes> clientBuilder;

    private readonly object gate = new();
    private (string Fingerprint, IKubernetes Client)? cache;

    private IKubernetes? previous;
    private bool disposed;

    public KubernetesClientFactory(
        IServiceScopeFactory scopeFactory,
        IConfiguration configuration,
        ILogger<KubernetesClientFactory> logger,
        IOptions<WorkloadProviderTimeoutOptions>? timeouts = null)
        : this(
            scopeFactory,
            configuration,
            logger,
            source => KubernetesClientBuilder.Build(
                source, (timeouts?.Value ?? new WorkloadProviderTimeoutOptions()).MutationTimeout))
    {
    }

    internal KubernetesClientFactory(
        IServiceScopeFactory scopeFactory,
        IConfiguration configuration,
        ILogger<KubernetesClientFactory> logger,
        Func<KubernetesConnectionSource, IKubernetes>? clientBuilder)
    {
        this.scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
        this.configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
        this.clientBuilder = clientBuilder ?? KubernetesClientBuilder.Build;
    }

    public async Task<IKubernetes> CreateClientAsync(CancellationToken cancellationToken = default)
    {
        var settings = await ReadKubernetesSettingsAsync(cancellationToken).ConfigureAwait(false);
        var source = KubernetesConnectionResolver.Resolve(configuration, settings);
        var fingerprint = source.Fingerprint;

        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (cache is { } hit && string.Equals(hit.Fingerprint, fingerprint, StringComparison.Ordinal))
            {
                return hit.Client;
            }
        }

        logger.LogInformation("Building Kubernetes workload client (connection mode {Mode}).", source.Mode);
        var built = clientBuilder(source);

        IKubernetes result;
        IKubernetes? surplus = null;
        IKubernetes? retiring = null;
        lock (gate)
        {
            if (disposed)
            {
                DisposeQuietly(built);
                throw new ObjectDisposedException(nameof(KubernetesClientFactory));
            }

            if (cache is { } hit && string.Equals(hit.Fingerprint, fingerprint, StringComparison.Ordinal))
            {
                result = hit.Client;
                surplus = built;
            }
            else
            {
                retiring = previous;
                previous = cache?.Client;
                cache = (fingerprint, built);
                result = built;
            }
        }

        DisposeQuietly(surplus);
        DisposeQuietly(retiring);
        return result;
    }

    private async Task<KubernetesProviderSettings> ReadKubernetesSettingsAsync(CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        var settingsService = scope.ServiceProvider.GetRequiredService<IWorkloadOrchestratorSecretReader>();
        var settings = await settingsService.GetDecryptedSettingsAsync(cancellationToken).ConfigureAwait(false);
        return KubernetesProviderSettings.From(settings);
    }

    public void Dispose()
    {
        IKubernetes? current;
        IKubernetes? retained;
        lock (gate)
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            current = cache?.Client;
            retained = previous;
            cache = null;
            previous = null;
        }

        DisposeQuietly(current);
        DisposeQuietly(retained);
    }

    private void DisposeQuietly(IKubernetes? client)
    {
        if (client is not IDisposable disposable)
        {
            return;
        }

        try
        {
            disposable.Dispose();
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Disposing a superseded Kubernetes client failed (ignored).");
        }
    }
}
