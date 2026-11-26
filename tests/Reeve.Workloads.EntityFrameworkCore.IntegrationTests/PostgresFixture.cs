using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;
using Reeve.Workloads.EntityFrameworkCore.Persistence;
using Testcontainers.PostgreSql;

namespace Reeve.Workloads.EntityFrameworkCore.IntegrationTests;

public sealed class PostgresFixture : IAsyncLifetime
{
    private const string Image = "postgres:18-alpine";

    private static readonly TimeSpan StartTimeout = TimeSpan.FromMinutes(3);

    private PostgreSqlContainer? container;

    public bool IsAvailable { get; private set; }

    public string SkipReason { get; private set; } = string.Empty;

    public async Task InitializeAsync()
    {
        try
        {
            container = new PostgreSqlBuilder(Image).Build();
            using var cts = new CancellationTokenSource(StartTimeout);
            await container.StartAsync(cts.Token).ConfigureAwait(false);
            IsAvailable = true;
        }
        catch (Exception ex)
        {
            IsAvailable = false;
            SkipReason = $"No PostgreSQL container ({ex.GetType().Name}: {ex.Message}). "
                + "This suite needs a reachable docker daemon.";
            container = null;
        }
    }

    public async Task DisposeAsync()
    {
        if (container is { } running)
        {
            await running.DisposeAsync().ConfigureAwait(false);
            container = null;
        }
    }

    public void EnsureAvailable() => Skip.IfNot(IsAvailable, SkipReason);

    public async Task<WorkloadsStore> CreateStoreAsync(CancellationToken cancellationToken = default)
    {
        var connectionString = await CreateEmptyDatabaseAsync(cancellationToken).ConfigureAwait(false);

        var store = new WorkloadsStore(connectionString);
        await using (var context = store.NewContext())
        {
            await context.Database.EnsureCreatedAsync(cancellationToken).ConfigureAwait(false);
        }

        return store;
    }

    public async Task<string> CreateEmptyDatabaseAsync(CancellationToken cancellationToken = default)
    {
        EnsureAvailable();

        NpgsqlConnection.ClearAllPools();

        var databaseName = "wl_" + Guid.NewGuid().ToString("N")[..12];
        var baseConnectionString = container!.GetConnectionString();

        await using (var admin = new NpgsqlConnection(baseConnectionString))
        {
            await admin.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var create = new NpgsqlCommand($"CREATE DATABASE \"{databaseName}\"", admin);
            await create.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        return new NpgsqlConnectionStringBuilder(baseConnectionString)
        {
            Database = databaseName,
        }.ConnectionString;
    }
}

public sealed class WorkloadsStore
{
    private readonly string connectionString;

    internal WorkloadsStore(string connectionString) => this.connectionString = connectionString;

    public string ConnectionString => connectionString;

    public WorkloadsDbContext NewContext() => new(
        new DbContextOptionsBuilder<WorkloadsDbContext>().UseNpgsql(connectionString).Options,
        new WorkloadsStoreOptions());

    public WorkloadsDbContext NewContext(params IInterceptor[] interceptors) => new(
        new DbContextOptionsBuilder<WorkloadsDbContext>()
            .UseNpgsql(connectionString)
            .AddInterceptors(interceptors)
            .Options,
        new WorkloadsStoreOptions());

    public async Task<bool> WaitForABlockedWriterAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        await using var observer = new NpgsqlConnection(connectionString);
        await observer.OpenAsync(cancellationToken).ConfigureAwait(false);

        while (DateTimeOffset.UtcNow < deadline)
        {
            await using (var query = new NpgsqlCommand(
                "SELECT count(*) FROM pg_stat_activity "
                + "WHERE datname = current_database() AND wait_event_type = 'Lock'",
                observer))
            {
                if (Convert.ToInt64(await query.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false)) > 0)
                {
                    return true;
                }
            }

            await Task.Delay(TimeSpan.FromMilliseconds(25), cancellationToken).ConfigureAwait(false);
        }

        return false;
    }
}
