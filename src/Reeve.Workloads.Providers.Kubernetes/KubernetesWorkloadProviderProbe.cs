using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Reeve.Workloads.Abstractions;
using Reeve.Workloads.Abstractions.Contracts;

namespace Reeve.Workloads.Providers.Kubernetes;

internal sealed class KubernetesWorkloadProviderProbe : IWorkloadProviderProbe
{
    private readonly IKubernetesClientFactory _clientFactory;
    private readonly IKubernetesClusterInspector _clusterInspector;
    private readonly KubernetesNamespaceResolver _namespaceResolver;
    private readonly ILogger<KubernetesWorkloadProviderProbe> _logger;

    public KubernetesWorkloadProviderProbe(
        IKubernetesClientFactory clientFactory,
        IKubernetesClusterInspector clusterInspector,
        KubernetesNamespaceResolver namespaceResolver,
        ILogger<KubernetesWorkloadProviderProbe> logger)
    {
        _clientFactory = clientFactory ?? throw new ArgumentNullException(nameof(clientFactory));
        _clusterInspector = clusterInspector ?? throw new ArgumentNullException(nameof(clusterInspector));
        _namespaceResolver = namespaceResolver ?? throw new ArgumentNullException(nameof(namespaceResolver));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public WorkloadProviderId Provider => KubernetesProvider.Id;

    public async Task<WorkloadProviderProbeResult> ProbeAsync(
        WorkloadOrchestratorSettings settings, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var kubernetes = KubernetesProviderSettings.From(settings);

        var ns = _namespaceResolver.Resolve(kubernetes);

        if (kubernetes.ConnectionMode == KubernetesConnectionMode.RemoteKubeconfig)
        {
            var validation = KubeconfigValidator.Validate(kubernetes.Kubeconfig, kubernetes.KubeconfigContext);
            if (!validation.IsValid)
            {
                return NotReachable(validation.Message ?? "the kubeconfig is invalid.");
            }
        }

        KubernetesClusterInspection inspection;
        try
        {
            var client = await _clientFactory.CreateClientAsync(cancellationToken).ConfigureAwait(false);

            if (ns is null)
            {
                return NamespaceNotConfigured();
            }

            inspection = await _clusterInspector.InspectAsync(client, ns, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return NotReachable(DescribeClientBuildFailure(kubernetes.ConnectionMode, ex));
        }

        return new WorkloadProviderProbeResult
        {
            Provider = KubernetesProvider.Id,
            ProbeAvailable = true,
            ApiServerReachable = inspection.ApiReachable,
            ConnectionFailed = inspection.ApiFailure != KubernetesApiFailure.None || !inspection.ApiReachable,
            CanListNodes = inspection.NodesAvailable,
            StorageClasses = inspection.StorageClassesAvailable
                ? inspection.StorageClasses.Select(s => s.Name).ToList()
                : null,
            PsaEnforcementLabels = inspection.NamespacePsaAvailable ? inspection.NamespacePsaLabels : null,
            Message = DescribeConnection(inspection),
        };
    }

    private static WorkloadProviderProbeResult NamespaceNotConfigured() => new()
    {
        Provider = KubernetesProvider.Id,
        ProbeAvailable = true,
        Message = "the workloads namespace is not configured, so the cluster was not probed.",
        Warnings = ["workloads-namespace: not configured"],
    };

    private static WorkloadProviderProbeResult NotReachable(string message) => new()
    {
        Provider = KubernetesProvider.Id,
        ProbeAvailable = true,
        ApiServerReachable = false,
        ConnectionFailed = true,
        Message = message,
    };

    private string DescribeClientBuildFailure(KubernetesConnectionMode mode, Exception ex)
    {
        if (mode == KubernetesConnectionMode.RemoteKubeconfig)
        {
            _logger.LogDebug("Kubernetes probe could not establish a client from the remote kubeconfig; reporting not reachable.");
            return "the kubeconfig is invalid — a Kubernetes client could not be established from it.";
        }

        _logger.LogDebug(ex, "Kubernetes probe could not build an in-cluster client; reporting not reachable.");
        return ex.Message;
    }

    private static string DescribeConnection(KubernetesClusterInspection inspection) => inspection.ApiFailure switch
    {
        KubernetesApiFailure.TlsCaFailure =>
            "could not establish TLS with the remote API server — its certificate could not be validated against the "
            + $"kubeconfig's certificate-authority (TLS/CA failure): {inspection.ApiError}",
        KubernetesApiFailure.AuthRejected =>
            "the remote API server rejected the kubeconfig credentials (authentication failed — HTTP 401).",
        KubernetesApiFailure.Unreachable =>
            $"the remote Kubernetes API server is unreachable (connection refused / DNS / timeout): {inspection.ApiError}",
        _ => inspection.ApiReachable
            ? "Connected to the Kubernetes API."
            : inspection.ApiError ?? "the Kubernetes API server is not reachable.",
    };
}
