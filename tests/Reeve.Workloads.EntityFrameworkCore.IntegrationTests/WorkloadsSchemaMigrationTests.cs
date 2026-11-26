using System.Globalization;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Reeve.Workloads.DependencyInjection;
using Reeve.Workloads.Abstractions;
using Reeve.Workloads.Abstractions.Contracts;
using Reeve.Workloads.Abstractions.Contracts.Deployment;
using Reeve.Workloads.Abstractions.Domain.Entities;
using Reeve.Workloads.EntityFrameworkCore;
using Reeve.Workloads.EntityFrameworkCore.Persistence;
using Reeve.Workloads.EntityFrameworkCore.PostgreSql;

namespace Reeve.Workloads.EntityFrameworkCore.IntegrationTests;

[Collection(WorkloadsStoreCollection.Name)]
public sealed class WorkloadsSchemaMigrationTests(PostgresFixture postgres)
{
    private static readonly string SurvivingTables =
        "SELECT table_name FROM information_schema.tables WHERE table_schema = 'workloads'";

    private static readonly string[] ExpectedTables =
    [
        "retained_volumes",
        "workload_installation",
        "workload_instances",
        "workload_modules",
        "workload_update_intent_steps",
        "workload_update_intents",
    ];

    [SkippableFact]
    public async Task The_migration_builds_the_whole_schema_on_an_empty_database()
    {
        await using var migrated = await MigrateAsync();

        var tables = await migrated.QueryAsync<string>(
            "SELECT table_name FROM information_schema.tables "
            + "WHERE table_schema = 'workloads' AND table_name <> '__WorkloadsMigrationsHistory' "
            + "ORDER BY table_name");

        tables.Should().Equal(ExpectedTables);
    }

    [SkippableFact]
    public async Task A_downgrade_to_zero_empties_the_schema()
    {
        await using var migrated = await MigrateAsync();

        await migrated.Context.GetService<IMigrator>().MigrateAsync("0");

        (await migrated.QueryAsync<string>(SurvivingTables)).Should().NotContain(ExpectedTables);
    }

    [SkippableFact]
    public async Task The_model_has_nothing_pending_once_the_migration_has_run()
    {
        await using var migrated = await MigrateAsync();

        migrated.Context.GetService<IMigrator>().HasPendingModelChanges().Should().BeFalse(
            "the migration is generated from this model and nothing may have moved since");
    }

    [SkippableFact]
    public async Task The_history_lands_in_the_librarys_own_table_and_not_EFs_default()
    {
        await using var migrated = await MigrateAsync();

        var applied = await migrated.QueryAsync<string>(
            "SELECT \"MigrationId\" FROM workloads.\"__WorkloadsMigrationsHistory\" ORDER BY \"MigrationId\"");

        var declared = migrated.Context.GetService<IMigrationsAssembly>().Migrations.Keys
            .OrderBy(id => id, StringComparer.Ordinal);
        applied.Should().Equal(declared, "every migration this assembly declares, and nothing else, belongs here");

        applied.First().Should().Be("20260918041436_InitialWorkloadsSchema");
        (await migrated.QueryAsync<string>(
                "SELECT to_regclass('public.\"__EFMigrationsHistory\"')::text WHERE to_regclass('public.\"__EFMigrationsHistory\"') IS NOT NULL"))
            .Should().BeEmpty("nothing may have been written to EF's default history table");
    }

    [SkippableFact]
    public async Task Two_modules_differing_only_in_case_cannot_both_be_stored()
    {
        await using var migrated = await MigrateAsync();

        await migrated.ExecuteAsync(Insert("acme.Thermostat"));
        var second = async () => await migrated.ExecuteAsync(Insert("ACME.THERMOSTAT"));

        await second.Should().ThrowAsync<PostgresException>()
            .Where(ex => ex.SqlState == PostgresErrorCodes.UniqueViolation);
    }

    [SkippableFact]
    public async Task The_idempotent_script_builds_an_empty_database_and_a_second_run_changes_nothing()
    {
        string script;
        List<string> migrations;
        await using (var designTime = new WorkloadsDesignTimeDbContextFactory().CreateDbContext([]))
        {
            script = designTime.GetService<IMigrator>().GenerateScript(options: MigrationsSqlGenerationOptions.Idempotent);
            migrations = designTime.Database.GetMigrations().ToList();
        }

        var connectionString = await postgres.CreateEmptyDatabaseAsync();
        const string applied = "SELECT \"MigrationId\" FROM workloads.\"__WorkloadsMigrationsHistory\" ORDER BY 1";

        await RunScriptAsync(connectionString, script);
        (await QueryAsync<string>(connectionString, applied)).Should().Equal(migrations,
            "a script that died part-way records fewer migrations than the assembly holds");

        var before = await SchemaShapeAsync(connectionString);
        await RunScriptAsync(connectionString, script);
        (await QueryAsync<string>(connectionString, applied)).Should().Equal(migrations);
        (await SchemaShapeAsync(connectionString)).Should().NotBeEmpty().And.Equal(before);
    }

    private static async Task<List<string>> SchemaShapeAsync(string connectionString)
    {
        var columns = await QueryAsync<string>(
            connectionString,
            "SELECT 'column ' || table_name || '.' || column_name || ' ' || data_type || ' ' || is_nullable || ' ' "
            + "|| coalesce(column_default, '') || ' #' || ordinal_position FROM information_schema.columns "
            + "WHERE table_schema = 'workloads' ORDER BY 1");
        var constraints = await QueryAsync<string>(
            connectionString,
            "SELECT 'constraint ' || t.relname || ' ' || c.conname || ' ' || c.contype::text || ' ' "
            + "|| pg_catalog.pg_get_constraintdef(c.oid) FROM pg_catalog.pg_constraint c "
            + "JOIN pg_catalog.pg_class t ON t.oid = c.conrelid JOIN pg_catalog.pg_namespace n ON n.oid = t.relnamespace "
            + "WHERE n.nspname = 'workloads' ORDER BY 1");
        var indexes = await QueryAsync<string>(
            connectionString,
            "SELECT 'index ' || indexdef FROM pg_catalog.pg_indexes WHERE schemaname = 'workloads' ORDER BY 1");
        return [.. columns, .. constraints, .. indexes];
    }

    private static async Task RunScriptAsync(string connectionString, string script)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(script, connection) { CommandTimeout = 300 };
        await command.ExecuteNonQueryAsync();
    }

    [SkippableFact]
    public async Task Every_not_null_constraint_is_named_as_the_model_would_name_it()
    {
        var notNullConstraints =
            "SELECT t.relname || '.' || a.attname || ' ' || c.conname AS \"Value\" FROM pg_constraint c "
            + "JOIN pg_class t ON t.oid = c.conrelid JOIN pg_namespace n ON n.oid = t.relnamespace "
            + "JOIN pg_attribute a ON a.attrelid = t.oid AND a.attnum = c.conkey[1] "
            + "WHERE c.contype = 'n' AND n.nspname = 'workloads' AND t.relname <> '__WorkloadsMigrationsHistory' "
            + "ORDER BY 1";
        await using var migrated = await MigrateAsync();
        var store = await postgres.CreateStoreAsync();
        await using var created = store.NewContext();

        var fromTheMigration = await migrated.QueryAsync<string>(notNullConstraints);
        var fromTheModel = await created.Database.SqlQueryRaw<string>(notNullConstraints).ToListAsync();

        fromTheMigration.Should().Contain("workload_instances.module_instance_id workload_instances_module_instance_id_not_null")
            .And.Equal(fromTheModel);
    }

    private static string Insert(string groupId) =>
        "INSERT INTO workloads.workload_modules "
        + "(id, module_id, source_key, installed_version, created_at_utc, updated_at_utc, version) VALUES "
        + $"(gen_random_uuid(), '{groupId}', 'marketplace', '1.0.0', now(), now(), 0)";

    private async Task<MigratedDatabase> MigrateAsync()
    {
        var connectionString = await postgres.CreateEmptyDatabaseAsync();
        var services = new ServiceCollection()
            .AddReeveWorkloads(new ConfigurationBuilder().Build())
            .AddEntityFrameworkStores(options => options.UseNpgsql(connectionString))
            .Services
            .BuildServiceProvider();

        var migrated = new MigratedDatabase(services, services.CreateAsyncScope(), connectionString);
        try
        {
            await migrated.Scope.ServiceProvider.GetRequiredService<IWorkloadsSchemaMigrator>().MigrateAsync();
        }
        catch
        {
            await migrated.DisposeAsync();
            throw;
        }

        return migrated;
    }

    private static async Task<List<T>> QueryAsync<T>(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync();

        var rows = new List<T>();
        while (await reader.ReadAsync())
        {
            rows.Add(reader.GetFieldValue<T>(0));
        }

        return rows;
    }

    private sealed class MigratedDatabase(
        ServiceProvider services,
        AsyncServiceScope scope,
        string connectionString) : IAsyncDisposable
    {
        public AsyncServiceScope Scope => scope;

        public WorkloadsDbContext Context => scope.ServiceProvider.GetRequiredService<WorkloadsDbContext>();

        public Task<List<T>> QueryAsync<T>(string sql) => WorkloadsSchemaMigrationTests.QueryAsync<T>(connectionString, sql);

        public async Task ExecuteAsync(string sql)
        {
            await using var connection = new NpgsqlConnection(connectionString);
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand(sql, connection);
            await command.ExecuteNonQueryAsync();
        }

        public async ValueTask DisposeAsync()
        {
            await scope.DisposeAsync();
            await services.DisposeAsync();
        }
    }
}
