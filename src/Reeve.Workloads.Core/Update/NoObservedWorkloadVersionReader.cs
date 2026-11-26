namespace Reeve.Workloads.Core.Update;

internal sealed class NoObservedWorkloadVersionReader : IObservedWorkloadVersionReader
{
    public Task<string?> GetObservedAppVersionAsync(Guid instanceId, CancellationToken cancellationToken = default)
        => Task.FromResult<string?>(null);
}
