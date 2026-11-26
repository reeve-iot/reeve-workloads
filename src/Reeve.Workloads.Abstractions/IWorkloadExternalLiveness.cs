namespace Reeve.Workloads.Abstractions;

/// <summary>Whether anything is live for an instance, by the host's own evidence. A fault is false, never a throw.</summary>
public interface IWorkloadExternalLiveness
{
    ValueTask<bool> IsLiveAsync(Guid instanceId, CancellationToken cancellationToken = default);
}
