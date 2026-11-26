using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Configuration;

namespace Reeve.Workloads.Providers.Kubernetes;

internal enum KubernetesConnectionKind
{
    InCluster,

    Kubeconfig,

    RemoteKubeconfig,
}

internal readonly record struct KubernetesConnectionSource
{
    private KubernetesConnectionSource(
        KubernetesConnectionKind mode, string? kubeconfigPath, string? kubeconfigContent, string? context)
    {
        Mode = mode;
        KubeconfigPath = kubeconfigPath;
        KubeconfigContent = kubeconfigContent;
        Context = context;
    }

    public KubernetesConnectionKind Mode { get; }

    public string? KubeconfigPath { get; }

    public string? KubeconfigContent { get; }

    public string? Context { get; }

    public static KubernetesConnectionSource InCluster() =>
        new(KubernetesConnectionKind.InCluster, kubeconfigPath: null, kubeconfigContent: null, context: null);

    public static KubernetesConnectionSource KubeconfigFile(string path) =>
        new(KubernetesConnectionKind.Kubeconfig, kubeconfigPath: path, kubeconfigContent: null, context: null);

    public static KubernetesConnectionSource Remote(string content, string? context) =>
        new(
            KubernetesConnectionKind.RemoteKubeconfig,
            kubeconfigPath: null,
            kubeconfigContent: content,
            context: string.IsNullOrWhiteSpace(context) ? null : context);

    public string Fingerprint => Mode switch
    {
        KubernetesConnectionKind.Kubeconfig => $"kubeconfigfile:{KubeconfigPath}",
        KubernetesConnectionKind.RemoteKubeconfig => $"remote:{Sha256Hex(KubeconfigContent ?? string.Empty)}:{Context}",
        _ => "incluster",
    };

    private static string Sha256Hex(string content) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content))).ToLowerInvariant();
}

internal static class KubernetesConnectionResolver
{
    public const string AllowKubeconfigKey = "Workloads:Kubernetes:AllowKubeconfig";

    public const string KubeconfigPathKey = "Workloads:Kubernetes:KubeconfigPath";

    public static KubernetesConnectionSource Resolve(IConfiguration configuration, KubernetesProviderSettings settings)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(settings);

        if (settings.ConnectionMode == KubernetesConnectionMode.RemoteKubeconfig)
        {
            return KubernetesConnectionSource.Remote(settings.Kubeconfig ?? string.Empty, settings.KubeconfigContext);
        }

        var allowKubeconfig = bool.TryParse(configuration[AllowKubeconfigKey], out var allowed) && allowed;
        var kubeconfigPath = configuration[KubeconfigPathKey];
        if (allowKubeconfig && !string.IsNullOrWhiteSpace(kubeconfigPath))
        {
            return KubernetesConnectionSource.KubeconfigFile(kubeconfigPath);
        }

        return KubernetesConnectionSource.InCluster();
    }
}
