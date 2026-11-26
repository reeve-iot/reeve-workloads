namespace Reeve.Workloads.Providers.Kubernetes;

internal static class KubernetesEnvValue
{
    internal static string? Escape(string? value) => value?.Replace("$", "$$", StringComparison.Ordinal);
}
