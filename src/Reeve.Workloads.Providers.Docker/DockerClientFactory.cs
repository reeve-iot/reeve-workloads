using System;
using System.Runtime.InteropServices;
using Docker.DotNet;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Reeve.Workloads.Core.Providers;

namespace Reeve.Workloads.Providers.Docker;

internal sealed class DockerClientFactory : IDockerClientFactory, IDisposable
{
    private readonly ILogger<DockerClientFactory> _logger;
    private readonly WorkloadProviderTimeoutOptions _timeouts;
    private readonly object _gate = new();

    private Uri? _cachedEndpoint;
    private IDockerClient? _cachedClient;
    private bool _disposed;

    public DockerClientFactory(ILogger<DockerClientFactory> logger, IOptions<WorkloadProviderTimeoutOptions>? timeouts = null)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _timeouts = timeouts?.Value ?? new WorkloadProviderTimeoutOptions();
    }

    public IDockerClient CreateClient(string? socketUri = null)
    {
        var endpoint = ResolveEndpoint(socketUri);

        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            if (_cachedClient is not null && _cachedEndpoint == endpoint)
            {
                return _cachedClient;
            }

            var client = new DockerClientBuilder()
                .WithEndpoint(endpoint)
                .WithTimeout(_timeouts.MutationTimeout)
                .Build();

            (_cachedClient as IDisposable)?.Dispose();
            _cachedClient = client;
            _cachedEndpoint = endpoint;

            _logger.LogInformation("Docker workload client built for endpoint {Endpoint}.", endpoint);
            return _cachedClient;
        }
    }

    public static Uri ResolveEndpoint(string? socketUri)
    {
        if (!string.IsNullOrWhiteSpace(socketUri))
        {
            if (!Uri.TryCreate(socketUri.Trim(), UriKind.Absolute, out var custom))
            {
                throw new FormatException(
                    $"Docker socketUri '{socketUri}' is not a valid absolute URI (expected e.g. unix:///var/run/docker.sock or tcp://host:2375).");
            }

            return custom;
        }

        return PlatformDefaultEndpoint();
    }

    public static Uri PlatformDefaultEndpoint()
        => new(RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? DockerProviderConstants.WindowsDefaultSocketUri
            : DockerProviderConstants.UnixDefaultSocketUri);

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            (_cachedClient as IDisposable)?.Dispose();
            _cachedClient = null;
            _cachedEndpoint = null;
        }
    }
}
