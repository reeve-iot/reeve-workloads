using Microsoft.EntityFrameworkCore;
using Reeve.Workloads.Abstractions;

namespace Reeve.Workloads.EntityFrameworkCore.Persistence;

internal sealed class EfWorkloadsSchemaMigrator(WorkloadsDbContext dbContext) : IWorkloadsSchemaMigrator
{
    public Task MigrateAsync(CancellationToken cancellationToken = default)
        => dbContext.Database.MigrateAsync(cancellationToken);
}
