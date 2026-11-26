using Reeve.Workloads.Abstractions;
using Reeve.Workloads.Abstractions.Domain.Entities;

namespace Reeve.Workloads.Core.Tests.Groups;

internal sealed class InMemoryWorkloadGroupRepository : IWorkloadGroupRepository
{
    private readonly List<WorkloadGroup> rows = [];

    public List<WorkloadGroup> Saves { get; } = [];

    public IReadOnlyList<WorkloadGroup> Rows => rows;

    public WorkloadGroup? LoseNextInsertTo { get; set; }

    public WorkloadGroup Seed(string groupId, string sourceKey, string? installedVersion)
    {
        var row = WorkloadGroup.Record(groupId, sourceKey, installedVersion, DateTimeOffset.UnixEpoch);
        rows.Add(row);
        return row;
    }

    public Task<WorkloadGroup?> GetAsync(string groupId, CancellationToken cancellationToken = default)
        => Task.FromResult(rows.Find(row => row.IsGroup(groupId)));

    public Task<bool> TryAddAsync(WorkloadGroup module, CancellationToken cancellationToken = default)
    {
        if (LoseNextInsertTo is { } winner)
        {
            LoseNextInsertTo = null;
            rows.Add(winner);
            return Task.FromResult(false);
        }

        if (rows.Exists(row => row.IsGroup(module.GroupId)))
        {
            return Task.FromResult(false);
        }

        rows.Add(module);
        Saves.Add(module);
        return Task.FromResult(true);
    }

    public Task UpdateAsync(WorkloadGroup module, CancellationToken cancellationToken = default)
    {
        Saves.Add(module);
        return Task.CompletedTask;
    }
}
