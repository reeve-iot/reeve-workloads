using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Reeve.EntityFrameworkCore.AdvisoryLocks;
using Reeve.Workloads.Abstractions;
using Reeve.Workloads.Core;
using Reeve.Workloads.EntityFrameworkCore.Persistence;

namespace Reeve.Workloads.EntityFrameworkCore.PostgreSql;

/// <summary>The engine's leadership, held as a PostgreSQL session-scoped advisory lock. One replica wins, every
/// other one is told so immediately, and the winner keeps it for exactly as long as its database session lives.
///
/// There is no lease clock, no fencing deadline and no row to renew, and that is the whole reason this is an
/// advisory lock rather than a leases table: the server releases the lock when the session ends, so the failure
/// this has to survive — a replica that dies without resigning — is handled by the database rather than by a
/// timeout this code would have to pick. What replaces the renewal is a probe on the holder's own connection that
/// asks the server whether THIS session still holds THIS key — a session that cannot answer, and a session that
/// answers but is not the holder, have both lost the leadership.
///
/// **Read this before running two replicas.** "The server releases the lock when the session ends" is doing a
/// great deal of work, and what ends the session is not always prompt. A killed PROCESS is: the kernel closes the
/// socket, the backend's read returns, the session ends in milliseconds. A lost NODE is not — no FIN is ever sent,
/// the backend sits in a blocking read, and the only thing that ever ends it is the server's TCP keepalive budget,
/// <c>tcp_keepalives_idle + tcp_keepalives_interval × tcp_keepalives_count</c>. On stock PostgreSQL that is
/// 7200 + 75×9 = 7875 seconds, measured on the 18 this library is tested against: for over two hours no replica
/// can take the key, and nothing is deployed, updated, reconciled or repaired. So the failover time on node loss
/// is NOT a property of this code — it is a server setting, and a deployment running more than one replica has to
/// set those three to the order of the failover it wants. This class logs the budget it actually found, once, at
/// its first acquisition, so the number is in the log rather than in somebody's assumptions.
///
/// Two settings that look like they help and do not. <c>client_connection_check_interval</c> only checks "while
/// running queries" (its own description), and a leader between probes is an idle session, so it never fires here.
/// <c>idle_session_timeout</c> is worse than useless: it would end the LEADER's session, and the only thing
/// keeping it from doing so is that <c>LeadershipProbeInterval</c> is shorter than it. That is not a free
/// assumption — the interval is <c>[Range]</c>-capped at ten minutes, so an <c>idle_session_timeout</c> under
/// that can be set high enough to reap a leader between two probes, and the leadership flaps on a server nobody
/// would call misconfigured.
///
/// Requires PostgreSQL 11 or newer, for <c>hashtextextended</c>. On PG 10 the first contest fails outright, which
/// the engine's runner reports at Error — noisy rather than silent, but it fails at the first contest, not at boot.
///
/// The lock statements are the advisory-lock package's (<c>Reeve.EntityFrameworkCore.AdvisoryLocks</c>), taken on
/// the context's own connection through its <c>DatabaseFacade</c> overloads. Not one Npgsql type is named here.
///
/// All of that rests on the context's connection being ONE pinned database session. Opening it explicitly gives
/// that on an ordinary pooled Npgsql connection. It does NOT behind a transaction-pooling proxy such as pgbouncer,
/// which spreads separate statements over separate server sessions — the acquire then takes the lock on a session
/// the probe never touches and the unlock never reaches, and two replicas both win. So the acquire asks the SERVER
/// whether the grant landed here, and refuses the leadership when it did not; see
/// <c>VerifyTheGrantIsOnThisSessionAsync</c> for what that catches and what it does not.</summary>
public sealed class PostgreSqlAdvisoryLockLeaderElection : IWorkloadLeaderElection
{
    private static readonly AdvisoryLockKey LeaderKey = AdvisoryLockKey.FromHashedText("ReeveWorkloadsEngine_Leader");

    private static readonly TimeSpan UnlockTimeout = TimeSpan.FromSeconds(5);

    private const int SessionChecks = 3;

    private const string KeepaliveSettingsSql =
        "SELECT current_setting('tcp_keepalives_idle') || ' ' || current_setting('tcp_keepalives_interval') "
        + "|| ' ' || current_setting('tcp_keepalives_count') AS \"Value\"";

    private readonly IServiceScopeFactory scopes;

    private readonly WorkloadsEngineHostingOptions options;
    private readonly ILogger<PostgreSqlAdvisoryLockLeaderElection> logger;

    private int announcedFailoverBudget;

    private int explainedTheUnpinnedSession;

    public PostgreSqlAdvisoryLockLeaderElection(
        IServiceScopeFactory scopes,
        IOptions<WorkloadsEngineHostingOptions> options,
        ILogger<PostgreSqlAdvisoryLockLeaderElection> logger)
    {
        ArgumentNullException.ThrowIfNull(scopes);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        this.scopes = scopes;
        this.options = options.Value;
        this.logger = logger;
    }

    /// <summary>Contends once, without blocking: a session <c>try</c> lock answers false rather than queueing,
    /// so a replica that will never be leader costs one round trip per retry instead of a parked session.</summary>
    /// <param name="cancellationToken">The engine's shutdown token.</param>
    /// <returns>The leadership, or null when another session holds it.</returns>
    /// <exception cref="InvalidOperationException">The grant did not land on this session, so no leadership on
    /// this connection can mean anything. Null would say "someone else leads"; this says "nobody here can".</exception>
    public async Task<IWorkloadLeadership?> TryAcquireAsync(CancellationToken cancellationToken)
    {
        var scope = scopes.CreateAsyncScope();
        IWorkloadLeadership? leadership = null;
        WorkloadsDbContext? granted = null;
        try
        {
            var context = scope.ServiceProvider.GetRequiredService<WorkloadsDbContext>();

            await context.Database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

            var won = await AdvisorySessionLock.TryLockAsync(context.Database, LeaderKey, cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            if (!won)
            {
                return null;
            }

            granted = context;
            await VerifyTheGrantIsOnThisSessionAsync(context, cancellationToken).ConfigureAwait(false);
            await AnnounceTheFailoverBudgetOnceAsync(context, cancellationToken).ConfigureAwait(false);

            leadership = new AdvisoryLockLeadership(scope, context, options.LeadershipProbeInterval, logger);
            return leadership;
        }
        finally
        {
            if (leadership is null)
            {
                if (granted is not null)
                {
                    await ReleaseALockNothingHereCanHoldAsync(granted).ConfigureAwait(false);
                }

                await scope.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    private async Task VerifyTheGrantIsOnThisSessionAsync(
        WorkloadsDbContext context, CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < SessionChecks; attempt++)
        {
            var grantedHere = await AdvisorySessionLock.IsHeldAsync(context.Database, LeaderKey, cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            if (!grantedHere)
            {
                throw NotOurSession();
            }
        }

        return;

        InvalidOperationException NotOurSession()
            => new(Interlocked.Exchange(ref explainedTheUnpinnedSession, 1) == 0
                ? "The workloads engine was granted its PostgreSQL advisory lock, but a following statement ran "
                  + "on a different database session, so nothing on this connection holds it. A session-scoped "
                  + "advisory lock cannot survive that, and two replicas would both believe they lead. The cause "
                  + "is a transaction-pooling proxy such as pgbouncer in transaction or statement mode, which "
                  + "spreads statements across sessions. Give the workloads DbContext a connection with "
                  + "session-level pooling, or register an "
                  + "IWorkloadLeaderElection of your own. This explanation is logged in full once per process; "
                  + "later attempts say the same thing in one line."
                : "The workloads engine cannot hold a leadership on this connection: it is not pinned to one "
                  + "database session. See the first occurrence of this error for the full explanation.");
    }

    private async Task AnnounceTheFailoverBudgetOnceAsync(
        WorkloadsDbContext context, CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref announcedFailoverBudget) != 0)
        {
            return;
        }

        int idle, interval, count;
        try
        {
            var settings = await context.Database.SqlQueryRaw<string>(KeepaliveSettingsSql)
                .FirstAsync(cancellationToken).ConfigureAwait(false);

            var parts = settings.Split(' ');
            if (parts.Length != 3
                || !int.TryParse(parts[0], out idle)
                || !int.TryParse(parts[1], out interval)
                || !int.TryParse(parts[2], out count))
            {
                return;
            }
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "The workloads engine could not read this server's TCP keepalive settings.");
            return;
        }

        Interlocked.Exchange(ref announcedFailoverBudget, 1);

        if (idle <= 0 || interval <= 0 || count <= 0)
        {
            logger.LogInformation(
                "The workloads engine holds its leadership as a PostgreSQL session-scoped advisory lock, so a "
                + "replica that dies without resigning keeps it until the server ends its session. This server "
                + "reports no TCP keepalive budget for this connection (idle={Idle}, interval={Interval}, "
                + "count={Count}) — a Unix-domain socket always reads that way, and so does a platform that "
                + "cannot read the option back — which means the operating system's own keepalive policy decides "
                + "the failover time and nothing here can see it. A deployment running more than one replica "
                + "should connect over TCP and set tcp_keepalives_idle, tcp_keepalives_interval and "
                + "tcp_keepalives_count to the failover it wants.",
                idle, interval, count);
            return;
        }

        logger.LogInformation(
            "The workloads engine holds its leadership as a PostgreSQL session-scoped advisory lock. If the node "
            + "holding it is lost without closing its socket — power loss or a network partition, as opposed to a "
            + "killed process — no other replica can take the leadership until this server's TCP keepalive budget "
            + "expires, which on this connection is {FailoverSeconds}s (tcp_keepalives_idle {Idle} + "
            + "tcp_keepalives_interval {Interval} * tcp_keepalives_count {Count}). That is the failover time of "
            + "this deployment, and it is a server setting rather than a library one. Nothing is deployed, "
            + "updated, reconciled or repaired during it.",
            idle + (interval * count), idle, interval, count);
    }

    private async Task ReleaseALockNothingHereCanHoldAsync(WorkloadsDbContext context)
    {
        try
        {
            using var bounded = new CancellationTokenSource(UnlockTimeout);
            var released = await AdvisorySessionLock.UnlockAsync(context.Database, LeaderKey, cancellationToken: bounded.Token)
                .ConfigureAwait(false);

            if (!released)
            {
                logger.LogWarning(
                    "The workloads engine could not give back an advisory lock it was granted on a session it "
                    + "cannot reach again. The key stays held until that session ends, and no replica can lead "
                    + "before then.");
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "The workloads engine failed to give back an advisory lock it cannot hold.");
        }
    }

    internal sealed class AdvisoryLockLeadership : IWorkloadLeadership
    {
        private readonly AsyncServiceScope scope;
        private readonly WorkloadsDbContext context;
        private readonly ILogger logger;
        private readonly Func<CancellationToken, Task<bool>> askWhetherThisSessionStillHoldsIt;
        private readonly CancellationTokenSource lost = new();
        private readonly Task probe;
        private int disposed;

        public AdvisoryLockLeadership(
            AsyncServiceScope scope, WorkloadsDbContext context, TimeSpan probeInterval, ILogger logger)
            : this(
                scope,
                context,
                probeInterval,
                logger,
                token => AdvisorySessionLock.IsHeldAsync(context.Database, LeaderKey, cancellationToken: token))
        {
        }

        internal AdvisoryLockLeadership(
            AsyncServiceScope scope,
            WorkloadsDbContext context,
            TimeSpan probeInterval,
            ILogger logger,
            Func<CancellationToken, Task<bool>> askWhetherThisSessionStillHoldsIt)
        {
            this.scope = scope;
            this.context = context;
            this.logger = logger;
            this.askWhetherThisSessionStillHoldsIt = askWhetherThisSessionStillHoldsIt;

            Lost = lost.Token;

            probe = Task.Run(() => ProbeAsync(probeInterval, lost.Token), CancellationToken.None);
        }

        public CancellationToken Lost { get; }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0)
            {
                return;
            }

            try
            {
                await lost.CancelAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "A Lost registration threw while the workloads engine resigned; resigning anyway.");
            }

            var probeSettled = false;
            try
            {
                await probe.WaitAsync(UnlockTimeout).ConfigureAwait(false);
                probeSettled = true;
            }
            catch (TimeoutException)
            {
                _ = probe.ContinueWith(
                    static settled => _ = settled.Exception,
                    CancellationToken.None,
                    TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);

                logger.LogWarning(
                    "The workloads engine's leadership probe did not return within {Timeout}; resigning without "
                    + "unlocking. The lock stays held until the server ends or resets that session.",
                    UnlockTimeout);
            }
            catch (Exception ex)
            {
                probeSettled = true;
                logger.LogWarning(ex, "The workloads engine's leadership probe faulted; resigning anyway.");
            }

            try
            {
                if (!probeSettled)
                {
                    return;
                }

                using var bounded = new CancellationTokenSource(UnlockTimeout);
                var held = await AdvisorySessionLock.UnlockAsync(context.Database, LeaderKey, cancellationToken: bounded.Token)
                    .ConfigureAwait(false);

                if (!held)
                {
                    logger.LogDebug("The workloads engine's session no longer held its advisory lock when it resigned.");
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning(
                    ex,
                    "The workloads engine could not release its PostgreSQL advisory lock. It is released when the "
                    + "server ends the session; a pooled connection that is closed keeps its session until reset.");
            }
            finally
            {
                try
                {
                    try
                    {
                        await context.Database.CloseConnectionAsync().ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        logger.LogWarning(ex, "The workloads engine could not close the connection its leadership was held on.");
                    }

                    try
                    {
                        await scope.DisposeAsync().ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        logger.LogWarning(ex, "The workloads engine could not dispose the scope its leadership was held in.");
                    }
                }
                finally
                {
                    lost.Dispose();
                }
            }
        }

        private async Task ProbeAsync(TimeSpan interval, CancellationToken stopping)
        {
            while (!stopping.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(interval, stopping).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }

                try
                {
                    var stillOurs = await askWhetherThisSessionStillHoldsIt(CancellationToken.None)
                        .ConfigureAwait(false);

                    if (!stillOurs)
                    {
                        logger.LogWarning(
                            "The session the workloads engine holds its advisory lock on no longer holds it, "
                            + "though it still answers. The leadership is lost and the engine's units are being "
                            + "stopped. A connection whose session was reset, or one that was never pinned to a "
                            + "single session, both look like this.");
                        await CancelLostAsync().ConfigureAwait(false);
                        return;
                    }
                }
                catch (Exception first)
                {
                    try
                    {
                        await Task.Delay(RetryPause(), stopping).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        return;
                    }

                    bool? confirmed = null;
                    try
                    {
                        confirmed = await askWhetherThisSessionStillHoldsIt(CancellationToken.None)
                            .ConfigureAwait(false);
                    }
                    catch (Exception second)
                    {
                        logger.LogWarning(
                            second,
                            "The session holding the workloads engine's advisory lock stopped answering, twice. "
                            + "The leadership is lost and the engine's units are being stopped.");
                    }

                    if (confirmed == true)
                    {
                        logger.LogInformation(
                            first,
                            "A workloads engine leadership probe failed, but the session still holds the lock, so "
                            + "the leadership is kept. A statement timeout or a cancelled query looks like this.");
                        continue;
                    }

                    if (confirmed == false)
                    {
                        logger.LogWarning(
                            first,
                            "A workloads engine leadership probe failed, and the session that answered the retry "
                            + "no longer holds the lock. The leadership is lost and the engine's units are being "
                            + "stopped.");
                    }

                    await CancelLostAsync().ConfigureAwait(false);
                    return;
                }
            }
        }

        private static TimeSpan RetryPause()
            => TimeSpan.FromMilliseconds(Random.Shared.Next(100, 301));

        private async Task CancelLostAsync()
        {
            try
            {
                await lost.CancelAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "A Lost registration threw when the workloads engine reported its leadership lost.");
            }
        }
    }
}
