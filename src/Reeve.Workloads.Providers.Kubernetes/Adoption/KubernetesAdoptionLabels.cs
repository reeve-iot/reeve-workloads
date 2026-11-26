namespace Reeve.Workloads.Providers.Kubernetes;

internal static class KubernetesAdoptionLabels
{
    public const string ManagedByLabel = "app.kubernetes.io/managed-by";

    public const string InstanceLabel = "app.kubernetes.io/instance";
}
