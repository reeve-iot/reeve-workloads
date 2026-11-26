using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using Reeve.EntityFrameworkCore.AdvisoryLocks;
using Reeve.Workloads.Abstractions;
using Reeve.Workloads.Core;
using Reeve.Workloads.EntityFrameworkCore.Persistence;
using Reeve.Workloads.EntityFrameworkCore.PostgreSql;

namespace Reeve.Workloads.EntityFrameworkCore.IntegrationTests.Hosting;

[Collection(WorkloadsStoreCollection.Name)]
public sealed class LeaderKeyEquivalenceTests
{
    private const string OldTry = "SELECT pg_try_advisory_lock(hashtextextended('ReeveWorkloadsEngine_Leader', 0))";

    private static readonly AdvisoryLockKey Key = AdvisoryLockKey.FromHashedText("ReeveWorkloadsEngine_Leader");

    private readonly PostgresFixture postgres;

    public LeaderKeyEquivalenceTests(PostgresFixture postgres) => this.postgres = postgres;

    [SkippableFact]
    public async Task A_holder_of_the_old_statement_is_the_package_key_and_refuses_the_package()
    {
        postgres.EnsureAvailable();
        var store = await postgres.CreateStoreAsync();

        await using var oldHolder = await OpenAsync(store);
        await using var contender = await OpenAsync(store);

        (await TryOldAsync(oldHolder)).Should().BeTrue("nothing holds the key yet");

        (await AdvisorySessionLock.IsHeldAsync(oldHolder, Key)).Should().BeTrue(
            "the package reads the very lock the old statement took, so the two keys are one value");
        (await AdvisorySessionLock.TryLockAsync(contender, Key)).Should().BeFalse(
            "the package contends with a replica that still runs the old statement");
    }

    [SkippableFact]
    public async Task A_holder_of_the_package_key_refuses_the_old_statement()
    {
        postgres.EnsureAvailable();
        var store = await postgres.CreateStoreAsync();

        await using var packageHolder = await OpenAsync(store);
        await using var contender = await OpenAsync(store);

        (await AdvisorySessionLock.TryLockAsync(packageHolder, Key)).Should().BeTrue();

        (await TryOldAsync(contender)).Should().BeFalse(
            "a replica that still runs the old statement is refused by one running the package");
    }

    [SkippableFact]
    public async Task The_election_is_refused_while_an_old_statement_holds_the_key_and_refuses_one_while_it_leads()
    {
        postgres.EnsureAvailable();
        var store = await postgres.CreateStoreAsync();

        await using var oldHolder = await OpenAsync(store);
        (await TryOldAsync(oldHolder)).Should().BeTrue();

        await using var replica = Replica(store);
        (await Election(replica).TryAcquireAsync(CancellationToken.None)).Should().BeNull(
            "the election's key is the old key: a holder of the old statement leads");

        var oldPid = oldHolder.ProcessID;
        await oldHolder.CloseAsync();
        await WaitUntilSessionHoldsNoAdvisoryLockAsync(store, oldPid);
        await using var retry = Replica(store);
        var leadership = await Election(retry).TryAcquireAsync(CancellationToken.None);
        leadership.Should().NotBeNull("the old holder is gone, so the key is free");

        try
        {
            await using var late = await OpenAsync(store);
            (await TryOldAsync(late)).Should().BeFalse("the election now holds the key the old statement names");
        }
        finally
        {
            await leadership!.DisposeAsync();
        }
    }

    private static async Task WaitUntilSessionHoldsNoAdvisoryLockAsync(WorkloadsStore store, int backendPid)
    {
        await using var observer = await OpenAsync(store);
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        while (await HoldsAdvisoryLockAsync(observer, backendPid))
        {
            DateTime.UtcNow.Should().BeBefore(deadline, "the closed session's backend must release the key");
            await Task.Delay(TimeSpan.FromMilliseconds(25));
        }
    }

    private static async Task<bool> HoldsAdvisoryLockAsync(NpgsqlConnection observer, int backendPid)
    {
        await using var command = new NpgsqlCommand(
            "SELECT EXISTS (SELECT 1 FROM pg_locks WHERE locktype = 'advisory' AND pid = @pid)", observer);
        command.Parameters.AddWithValue("pid", backendPid);
        return await command.ExecuteScalarAsync() is true;
    }

    private static async Task<bool> TryOldAsync(NpgsqlConnection connection)
    {
        await using var command = new NpgsqlCommand(OldTry, connection);
        return await command.ExecuteScalarAsync() is true;
    }

    private static async Task<NpgsqlConnection> OpenAsync(WorkloadsStore store)
    {
        var connection = new NpgsqlConnection(
            new NpgsqlConnectionStringBuilder(store.ConnectionString) { Pooling = false }.ConnectionString);
        await connection.OpenAsync();
        return connection;
    }

    private static ServiceProvider Replica(WorkloadsStore store)
    {
        var services = new ServiceCollection();
        services.AddScoped(_ => store.NewContext());
        services.AddSingleton<IOptions<WorkloadsEngineHostingOptions>>(
            Options.Create(new WorkloadsEngineHostingOptions { LeadershipProbeInterval = TimeSpan.FromMinutes(5) }));
        services.AddSingleton<Microsoft.Extensions.Logging.ILogger<PostgreSqlAdvisoryLockLeaderElection>>(
            NullLogger<PostgreSqlAdvisoryLockLeaderElection>.Instance);
        services.AddSingleton<PostgreSqlAdvisoryLockLeaderElection>();
        return services.BuildServiceProvider();
    }

    private static IWorkloadLeaderElection Election(IServiceProvider replica)
        => replica.GetRequiredService<PostgreSqlAdvisoryLockLeaderElection>();
}
