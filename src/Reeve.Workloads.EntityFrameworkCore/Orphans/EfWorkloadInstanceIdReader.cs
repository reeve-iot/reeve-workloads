using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Reeve.Workloads.Abstractions;
using Reeve.Workloads.EntityFrameworkCore.Persistence;

namespace Reeve.Workloads.EntityFrameworkCore.Orphans;

/// <summary>The persisted workload-instance ids. Reads ONLY the key column — pulling entities would load every
/// instance's spec on a surface an operator can refresh freely.</summary>
public sealed class EfWorkloadInstanceIdReader(WorkloadsDbContext db) : IWorkloadInstanceIdReader
{
    /// <inheritdoc />
    public async Task<IReadOnlySet<Guid>> GetKnownInstanceIdsAsync(CancellationToken cancellationToken = default)
    {
        var ids = await db.WorkloadInstances
            .AsNoTracking()
            .Select(instance => instance.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return new HashSet<Guid>(ids);
    }
}
