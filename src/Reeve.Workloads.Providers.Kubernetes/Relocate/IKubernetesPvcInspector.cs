using System.Threading;
using System.Threading.Tasks;
using k8s;
using Reeve.Workloads.Abstractions.Contracts;

namespace Reeve.Workloads.Providers.Kubernetes.Relocate;

internal interface IKubernetesPvcInspector
{
    Task<PvcBinding?> ReadPvcBindingAsync(IKubernetes client, string ns, string pvcName, CancellationToken ct);

    Task<PvcLiveStatus?> ReadPvcLiveStatusAsync(IKubernetes client, string ns, string pvcName, CancellationToken ct);

    Task<bool> DeletePvcAsync(IKubernetes client, string ns, string pvcName, CancellationToken ct);
}

internal sealed record PvcBinding
{
    public required string Name { get; init; }

    public required bool Bound { get; init; }

    public string? BoundNode { get; init; }

    public required string Phase { get; init; }

    public bool BindingUnreadable { get; init; }
}

internal sealed record PvcLiveStatus
{
    public required string Name { get; init; }

    public required WorkloadVolumeResizeState ResizeState { get; init; }

    public string? Phase { get; init; }

    public string? RequestedSize { get; init; }

    public string? ActualCapacity { get; init; }

    public string? StorageClassName { get; init; }

    public string? ResizeMessage { get; init; }
}
