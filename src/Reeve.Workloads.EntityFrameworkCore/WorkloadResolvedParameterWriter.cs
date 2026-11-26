using Microsoft.EntityFrameworkCore;
using Reeve.Workloads.Abstractions;
using Reeve.Workloads.Abstractions.Domain.Entities;
using Reeve.Workloads.EntityFrameworkCore.Persistence;

namespace Reeve.Workloads.EntityFrameworkCore;

/// <summary>EF Core's side of <see cref="IWorkloadResolvedParameterWriter"/>: stages every instance's two parameter
/// columns and commits ONCE. Not via the repository, whose Update saves per call and notifies on status.</summary>
public sealed class WorkloadResolvedParameterWriter(WorkloadsDbContext dbContext)
    : IWorkloadResolvedParameterWriter
{
    public async Task<int> SetResolvedParametersAsync(
        Guid groupInstanceId,
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, string?>> byWorkloadId,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(byWorkloadId);
        if (byWorkloadId.Count == 0)
        {
            return 0;
        }

        var byTrimmedId = new Dictionary<string, IReadOnlyDictionary<string, string?>>(StringComparer.Ordinal);
        foreach (var (workloadId, resolved) in byWorkloadId)
        {
            byTrimmedId[(workloadId ?? string.Empty).Trim()] = resolved;
        }

        var wanted = byTrimmedId.Keys.ToHashSet(StringComparer.Ordinal);
        var instances = await dbContext.WorkloadInstances
            .Where(instance => instance.GroupInstanceId == groupInstanceId
                && wanted.Contains(instance.WorkloadId))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        foreach (var instance in instances)
        {
            if (!byTrimmedId.TryGetValue(instance.WorkloadId, out var resolved))
            {
                continue;
            }

            instance.SetResolvedParameters(resolved, nowUtc);

            var entry = dbContext.Entry(instance);
            if (dbContext.ShapesJsonDocuments)
            {
                entry.ComplexProperty(i => i.ParametersDocument).IsModified = true;
            }
            else
            {
                entry.Property(i => i.ParametersDocument).IsModified = true;
            }

            entry.Property(i => i.UpdatedAtUtc).IsModified = true;
        }

        return await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }
}
