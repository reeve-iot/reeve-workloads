using System.Data;
using System.Data.Common;
using System.Diagnostics;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
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
public sealed class PostgreSqlAdvisoryLockLeaderElectionTests
{
    private static readonly TimeSpan LostWithin = TimeSpan.FromSeconds(15);

    private static readonly TimeSpan FastProbe = TimeSpan.FromMilliseconds(150);

    private static readonly TimeSpan SlowProbe = TimeSpan.FromSeconds(5);

    private static readonly TimeSpan LostWithinSlowProbes = TimeSpan.FromSeconds(30);

    private static readonly TimeSpan NoProbe = TimeSpan.FromMinutes(5);

    private const string TerminateTheHolderSql =
        "SELECT pg_terminate_backend(l.pid)::int AS \"Value\" FROM pg_locks l "
        + "WHERE l.locktype = 'advisory' AND l.granted "
        + "AND l.database = (SELECT oid FROM pg_database WHERE datname = current_database())";

    private readonly PostgresFixture postgres;

    public PostgreSqlAdvisoryLockLeaderElectionTests(PostgresFixture postgres) => this.postgres = postgres;

    [SkippableFact]
    public async Task One_holder_wins_and_the_other_gets_null()
    {
        postgres.EnsureAvailable();
        var store = await postgres.CreateStoreAsync();

        await using var first = Replica(store, NoProbe);
        await using var second = Replica(store, NoProbe);

        var held = await Election(first).TryAcquireAsync(CancellationToken.None);
        var refused = await Election(second).TryAcquireAsync(CancellationToken.None);

        held.Should().NotBeNull("one replica has to be able to run the engine");
        refused.Should().BeNull(
            "a session try lock does not queue: the second replica is told at once that it is not the leader");
        held!.Lost.IsCancellationRequested.Should().BeFalse("nothing has taken the leadership away");

        await held.DisposeAsync();
    }

    [SkippableFact]
    public async Task The_second_replica_wins_after_the_first_resigns()
    {
        postgres.EnsureAvailable();
        var store = await postgres.CreateStoreAsync();

        await using var first = Replica(store, NoProbe);
        await using var second = Replica(store, NoProbe);

        var held = await Election(first).TryAcquireAsync(CancellationToken.None);
        held.Should().NotBeNull();
        (await Election(second).TryAcquireAsync(CancellationToken.None)).Should().BeNull(
            "the contest has to be lost before the handover is worth asserting");

        await held!.DisposeAsync();

        held.Lost.IsCancellationRequested.Should().BeTrue(
            "resigning is a leadership ending, and the runner reads Lost after it has let go");

        var taken = await Election(second).TryAcquireAsync(CancellationToken.None);
        taken.Should().NotBeNull("a resigned lock is released, so failover costs the next replica one attempt");

        await taken!.DisposeAsync();
    }

    [SkippableFact]
    public async Task Killing_the_holders_session_loses_the_leadership()
    {
        postgres.EnsureAvailable();
        var store = await postgres.CreateStoreAsync();

        await using var replica = Replica(store, FastProbe);
        var held = await Election(replica).TryAcquireAsync(CancellationToken.None);
        held.Should().NotBeNull();

        (await TerminateTheHolderAsync(store)).Should().Be(1, "exactly one session holds the engine's lock");

        (await WaitForCancellationAsync(held!.Lost, LostWithin)).Should().BeTrue(
            "the probe on the holder's own connection is what turns a dead session into a lost leadership");

        var resign = async () => await held.DisposeAsync();
        await resign.Should().NotThrowAsync(
            "the runner disposes a lost leadership in a finally, and a throw there costs the engine a pass");
    }

    [SkippableFact]
    public async Task A_failed_unlock_still_closes_the_connection()
    {
        postgres.EnsureAvailable();
        var store = await postgres.CreateStoreAsync();

        var contexts = new List<WorkloadsDbContext>();
        await using var replica = Replica(store, NoProbe, contexts);
        var held = await Election(replica).TryAcquireAsync(CancellationToken.None);
        held.Should().NotBeNull();

        contexts.Should().ContainSingle("the leadership opens exactly one scope and takes one context from it");
        DbConnection connection = contexts[0].Database.GetDbConnection();
        connection.State.Should().Be(ConnectionState.Open, "the leadership IS that open session");

        (await TerminateTheHolderAsync(store)).Should().Be(1);

        var resign = async () => await held!.DisposeAsync();
        await resign.Should().NotThrowAsync();

        ((NpgsqlConnection)connection).FullState.Should().Be(ConnectionState.Closed,
            "the server releases the lock with the session, so a failed unlock is not a leak — a connection "
            + "nobody closed is, and it is held for the life of the process");

        var useDisposedContext = () => contexts[0].Database.GetDbConnection();
        useDisposedContext.Should().Throw<ObjectDisposedException>(
            "the scope the singleton election opened is disposed with the leadership, not kept alive by it");

        await using var later = Replica(store, NoProbe);
        var retaken = await Election(later).TryAcquireAsync(CancellationToken.None);
        retaken.Should().NotBeNull("nothing is left holding the key after a resign that could not unlock");
        await retaken!.DisposeAsync();
    }

    [SkippableFact]
    public async Task A_lock_released_behind_the_holders_back_loses_the_leadership()
    {
        postgres.EnsureAvailable();
        var store = await postgres.CreateStoreAsync();

        var contexts = new List<WorkloadsDbContext>();
        await using var replica = Replica(store, SlowProbe, contexts);
        var held = await Election(replica).TryAcquireAsync(CancellationToken.None);
        held.Should().NotBeNull();

        await contexts[0].Database.ExecuteSqlRawAsync("SELECT pg_advisory_unlock_all()");

        (await WaitForCancellationAsync(held!.Lost, LostWithinSlowProbes)).Should().BeTrue(
            "a probe that only asked SELECT 1 would call this session healthy for ever, while the key it no "
            + "longer holds was granted to another replica leading beside it");

        await using var next = Replica(store, NoProbe);
        var taken = await Election(next).TryAcquireAsync(CancellationToken.None);
        taken.Should().NotBeNull("the key really was free — this is a lost leadership, not a false alarm");

        await taken!.DisposeAsync();
        await held.DisposeAsync();
    }

    [SkippableFact]
    public async Task An_acquire_abandoned_after_the_lock_was_granted_gives_the_lock_back()
    {
        postgres.EnsureAvailable();
        var store = await postgres.CreateStoreAsync();
        await using var replica = Replica(store, NoProbe);

        using var shutdown = new CancellationTokenSource();
        var granted = false;
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == AdvisoryLockTelemetry.ActivitySourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStopped = activity =>
            {
                if (activity.GetTagItem("advisory_lock.outcome") as string == "acquired")
                {
                    granted = true;
                    shutdown.Cancel();
                }
            },
        };
        ActivitySource.AddActivityListener(listener);

        var attempt = () => Election(replica).TryAcquireAsync(shutdown.Token);
        await attempt.Should().ThrowAsync<OperationCanceledException>(
            "an abandoned acquire reports the cancellation instead of a leadership");

        granted.Should().BeTrue("the case is only about a lock that was granted before the acquire was abandoned");

        await using var contender = new NpgsqlConnection(
            new NpgsqlConnectionStringBuilder(store.ConnectionString) { Pooling = false }.ConnectionString);
        await contender.OpenAsync();
        (await AdvisorySessionLock.TryLockAsync(contender, AdvisoryLockKey.FromHashedText("ReeveWorkloadsEngine_Leader")))
            .Should().BeTrue("the abandoned acquire unlocked before its session went back to the pool, so another replica can lead");
    }

    [SkippableFact]
    public async Task An_unrelated_advisory_lock_does_not_stand_in_for_the_engines_own()
    {
        postgres.EnsureAvailable();
        var store = await postgres.CreateStoreAsync();

        var contexts = new List<WorkloadsDbContext>();
        await using var replica = Replica(store, SlowProbe, contexts);
        var held = await Election(replica).TryAcquireAsync(CancellationToken.None);
        held.Should().NotBeNull();

        await contexts[0].Database.ExecuteSqlRawAsync("SELECT pg_advisory_lock(hashtextextended('something-else', 0))");
        await contexts[0].Database.ExecuteSqlRawAsync(
            "SELECT pg_advisory_unlock(hashtextextended('ReeveWorkloadsEngine_Leader', 0))");

        (await WaitForCancellationAsync(held!.Lost, LostWithinSlowProbes)).Should().BeTrue(
            "the probe counts THIS key, not 'any advisory lock on this session' — counting any would see the "
            + "unrelated one still held and report a leadership this replica has already lost");

        await using var next = Replica(store, NoProbe);
        var taken = await Election(next).TryAcquireAsync(CancellationToken.None);
        taken.Should().NotBeNull("the engine's key really was released; only the unrelated one is still held");

        await taken!.DisposeAsync();
        await held.DisposeAsync();
    }

    private static ServiceProvider Replica(
        WorkloadsStore store,
        TimeSpan probeInterval,
        ICollection<WorkloadsDbContext>? contexts = null)
    {
        var services = new ServiceCollection();

        services.AddScoped(_ =>
        {
            var context = store.NewContext();
            contexts?.Add(context);
            return context;
        });

        services.AddSingleton<IOptions<WorkloadsEngineHostingOptions>>(
            Options.Create(new WorkloadsEngineHostingOptions { LeadershipProbeInterval = probeInterval }));
        services.AddSingleton<ILogger<PostgreSqlAdvisoryLockLeaderElection>>(
            NullLogger<PostgreSqlAdvisoryLockLeaderElection>.Instance);
        services.AddSingleton<PostgreSqlAdvisoryLockLeaderElection>();

        return services.BuildServiceProvider();
    }

    private static IWorkloadLeaderElection Election(IServiceProvider replica)
        => replica.GetRequiredService<PostgreSqlAdvisoryLockLeaderElection>();

    private static async Task<int> TerminateTheHolderAsync(WorkloadsStore store)
    {
        await using var other = store.NewContext();
        var terminated = await other.Database.SqlQueryRaw<int>(TerminateTheHolderSql).ToListAsync();
        return terminated.Count(killed => killed == 1);
    }

    private static async Task<bool> WaitForCancellationAsync(CancellationToken token, TimeSpan timeout)
    {
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = token.Register(
            static state => ((TaskCompletionSource)state!).TrySetResult(), cancelled);

        return await Task.WhenAny(cancelled.Task, Task.Delay(timeout)).ConfigureAwait(false) == cancelled.Task;
    }
}
