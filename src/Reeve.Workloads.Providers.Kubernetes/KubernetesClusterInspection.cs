using System.Collections.Generic;

namespace Reeve.Workloads.Providers.Kubernetes;

internal enum KubernetesApiFailure
{
    None = 0,

    Unreachable,

    TlsCaFailure,

    AuthRejected,
}

internal sealed record KubernetesClusterInspection
{
    public required bool ApiReachable { get; init; }

    public string? ApiError { get; init; }

    public KubernetesApiFailure ApiFailure { get; init; } = KubernetesApiFailure.None;

    public IReadOnlyList<ClusterNodeInfo> Nodes { get; init; } = [];

    public bool NodesAvailable { get; init; } = true;

    public IReadOnlyDictionary<string, string> NamespacePsaLabels { get; init; } = new Dictionary<string, string>();

    public bool NamespacePsaAvailable { get; init; } = true;

    public IReadOnlyList<ClusterStorageClassInfo> StorageClasses { get; init; } = [];

    public bool StorageClassesAvailable { get; init; } = true;

    public bool NamespaceHasResourceQuotaRequiringRequests { get; init; }

    public bool ResourceQuotaAvailable { get; init; } = true;

    public Version? ServerVersion { get; init; }

    public static KubernetesClusterInspection Unreachable(
        string error, KubernetesApiFailure failure = KubernetesApiFailure.Unreachable) => new()
    {
        ApiReachable = false,
        ApiError = error,
        ApiFailure = failure,
        NodesAvailable = false,
        NamespacePsaAvailable = false,
        StorageClassesAvailable = false,
        ResourceQuotaAvailable = false,
    };

    public static KubernetesClusterInspection AuthRejected(string error) => new()
    {
        ApiReachable = true,
        ApiError = error,
        ApiFailure = KubernetesApiFailure.AuthRejected,
        NodesAvailable = false,
        NamespacePsaAvailable = false,
        StorageClassesAvailable = false,
        ResourceQuotaAvailable = false,
    };
}

internal sealed record ClusterNodeInfo
{
    public required string Name { get; init; }

    public IReadOnlyDictionary<string, string> Labels { get; init; } = new Dictionary<string, string>();

    public string? Arch { get; init; }

    public bool Schedulable { get; init; } = true;

    public bool? UserNamespacesSupported { get; init; }
}

internal sealed record ClusterStorageClassInfo
{
    public required string Name { get; init; }

    public string? Provisioner { get; init; }

    public string? VolumeBindingMode { get; init; }

    public bool AllowVolumeExpansion { get; init; }

    public bool IsDefault { get; init; }
}
