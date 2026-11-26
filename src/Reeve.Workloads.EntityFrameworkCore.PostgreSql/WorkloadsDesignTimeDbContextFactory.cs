using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Reeve.Workloads.EntityFrameworkCore.Persistence;

namespace Reeve.Workloads.EntityFrameworkCore.PostgreSql;

/// <summary>Hands <c>dotnet ef</c> a context it can build with no host. It exists for the TOOL and is not a
/// composition seam: a host never calls it, and a host that did would get a context pointed at a database that
/// does not exist. The wiring a host gets is <c>AddEntityFrameworkStores(configure)</c>, which is the only
/// place the two facts below are decided for a running deployment.</summary>
/// <remarks>Scaffolding a migration reads the MODEL, not the database, so the connection string is never
/// dialled — and names a host that cannot resolve on purpose, so a tool that did try to connect fails loudly
/// instead of quietly finding whatever PostgreSQL the machine happens to be running.</remarks>
public sealed class WorkloadsDesignTimeDbContextFactory : IDesignTimeDbContextFactory<WorkloadsDbContext>
{
    private const string NeverDialled =
        "Host=workloads-design-time.invalid;Database=workloads;Username=none;Password=none";

    public WorkloadsDbContext CreateDbContext(string[] args)
    {
        var store = new WorkloadsStoreOptions();
        var options = new DbContextOptionsBuilder<WorkloadsDbContext>()
            .UseNpgsql(NeverDialled, npgsql => npgsql
                .MigrationsAssembly(typeof(WorkloadsDesignTimeDbContextFactory).Assembly.GetName().Name)
                .MigrationsHistoryTable(store.MigrationsHistoryTable, store.Schema))
            .Options;

        return new WorkloadsDbContext(options, store);
    }
}
