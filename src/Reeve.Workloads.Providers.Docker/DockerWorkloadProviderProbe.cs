using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Reeve.Workloads.Abstractions;
using Reeve.Workloads.Abstractions.Contracts;
using Reeve.Workloads.Abstractions.Providers;

namespace Reeve.Workloads.Providers.Docker;

internal sealed class DockerWorkloadProviderProbe : IWorkloadProviderProbe
{
    private readonly DockerPreflight _preflight;
    private readonly ILogger<DockerWorkloadProviderProbe> _logger;

    public DockerWorkloadProviderProbe(DockerPreflight preflight, ILogger<DockerWorkloadProviderProbe> logger)
    {
        _preflight = preflight ?? throw new ArgumentNullException(nameof(preflight));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public WorkloadProviderId Provider => DockerProvider.Id;

    public async Task<WorkloadProviderProbeResult> ProbeAsync(
        WorkloadOrchestratorSettings settings, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);

        DockerPreflightResult preflight;
        try
        {
            preflight = await _preflight.ProbeAsync(settings, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Docker probe faulted unexpectedly; reporting the daemon not reachable.");
            return new WorkloadProviderProbeResult
            {
                Provider = DockerProvider.Id,
                ProbeAvailable = true,
                ApiServerReachable = false,
                Message = ex.Message,
            };
        }

        var blockingErrors = preflight.Checks.Count(c => !c.Passed && c.Severity == PreflightSeverity.Error);

        return new WorkloadProviderProbeResult
        {
            Provider = DockerProvider.Id,
            ProbeAvailable = true,
            ApiServerReachable = preflight.SocketReachable,
            DockerApiVersion = preflight.DockerApiVersion,
            RegistryPullAuthResult = preflight.RegistryPullAuthResult,
            Warnings = ProjectWarnings(preflight.Checks),
            Message = Summarize(preflight, blockingErrors),
        };
    }

    private static IReadOnlyList<string> ProjectWarnings(IReadOnlyList<PreflightCheck> checks)
        => checks.Where(c => !c.Passed).Select(c => $"{c.Id}: {c.Message}").ToList();

    private static string Summarize(DockerPreflightResult preflight, int blockingErrors)
    {
        var reach = preflight.SocketReachable
            ? $"Docker daemon reachable (API {preflight.DockerApiVersion ?? "unknown"}, arch {preflight.DaemonArchitecture ?? "unknown"})"
            : "Docker daemon unreachable";

        return blockingErrors == 0
            ? $"{reach}; no blocking preflight errors."
            : $"{reach}; {blockingErrors} blocking preflight error(s).";
    }
}
