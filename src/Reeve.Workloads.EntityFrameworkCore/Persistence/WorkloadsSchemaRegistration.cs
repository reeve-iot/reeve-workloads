using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;

namespace Reeve.Workloads.EntityFrameworkCore.Persistence;

internal sealed class WorkloadsSchemaRegistration
{
    private readonly WorkloadsStoreOptions store;
    private readonly string? migrationsAssembly;

    private WorkloadsSchemaRegistration(WorkloadsStoreOptions store, string? migrationsAssembly, bool isRelational)
    {
        this.store = store;
        this.migrationsAssembly = migrationsAssembly;
        IsRelational = isRelational;
    }

    internal bool IsRelational { get; }

    internal static WorkloadsSchemaRegistration Probe(
        Action<DbContextOptionsBuilder> configure,
        WorkloadsStoreOptions store)
    {
        var probe = new DbContextOptionsBuilder<WorkloadsDbContext>();
        configure(probe);

        if (RelationalOptionsOf(probe.Options) is not { } relational)
        {
            return new WorkloadsSchemaRegistration(store, migrationsAssembly: null, isRelational: false);
        }

        var providerName = relational.GetType().Assembly.GetName().Name;
        return new WorkloadsSchemaRegistration(
            store, WorkloadsDatabaseConventions.AssemblyFor(providerName), isRelational: true);
    }

    internal void ApplyTo(DbContextOptionsBuilder options)
    {
        if (RelationalOptionsOf(options.Options) is not { } relational)
        {
            return;
        }

        var configured = relational
            .WithMigrationsHistoryTableName(store.MigrationsHistoryTable)
            .WithMigrationsHistoryTableSchema(store.Schema);

        if (migrationsAssembly is not null)
        {
            configured = configured.WithMigrationsAssembly(migrationsAssembly);
        }

        ((IDbContextOptionsBuilderInfrastructure)options).AddOrUpdateExtension(configured);

        ((IDbContextOptionsBuilderInfrastructure)options)
            .AddOrUpdateExtension(new WorkloadsSchemaMarkerExtension());
    }

    internal static void EnsureNothingOverrodeIt(IDbContextOptions options, WorkloadsStoreOptions store)
    {
        if (options.FindExtension<WorkloadsSchemaMarkerExtension>() is null)
        {
            return;
        }

        if (RelationalOptionsOf(options) is not { } relational)
        {
            return;
        }

        if (relational.MigrationsHistoryTableName != store.MigrationsHistoryTable
            || relational.MigrationsHistoryTableSchema != store.Schema)
        {
            throw Overridden(
                "migrations history table",
                $"{relational.MigrationsHistoryTableSchema ?? "(default)"}."
                + $"{relational.MigrationsHistoryTableName ?? "__EFMigrationsHistory"}",
                $"{store.Schema}.{store.MigrationsHistoryTable}");
        }

        var providerName = relational.GetType().Assembly.GetName().Name;
        if (WorkloadsDatabaseConventions.AssemblyFor(providerName) is not { } expected)
        {
            return;
        }

        var named = relational.MigrationsAssembly ?? relational.MigrationsAssemblyObject?.GetName().Name;
        if (!string.Equals(named, expected, StringComparison.Ordinal))
        {
            throw Overridden("migrations assembly", named ?? "EF's own default", expected);
        }
    }

    private static InvalidOperationException Overridden(string what, string found, string expected) =>
        new($"The workloads context resolved with a {what} of '{found}' rather than the library's '{expected}'. "
            + "Something re-registered WorkloadsDbContext after AddEntityFrameworkStores: AddDbContext does not "
            + "TryAdd its options action, so every registered action runs in call order and the last one wins. "
            + "Delete the host's own AddDbContext<WorkloadsDbContext>(...) call — including its MigrationsAssembly "
            + "and MigrationsHistoryTable lines — and pass the provider and the connection to "
            + "AddEntityFrameworkStores instead. Migrating on these options would apply the wrong chain and record "
            + "it as this library's, which boots green and diverges on the next release.");

    private static RelationalOptionsExtension? RelationalOptionsOf(IDbContextOptions options)
    {
        var relational = options.Extensions.OfType<RelationalOptionsExtension>().ToList();
        return relational.Count == 1 ? relational[0] : null;
    }
}
