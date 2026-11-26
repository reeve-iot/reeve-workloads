using Microsoft.Extensions.DependencyInjection;
using Reeve.Workloads.Abstractions;
using Reeve.Workloads.EntityFrameworkCore.PostgreSql;

namespace Reeve.Workloads.DependencyInjection;

/// <summary>What this package contributes to the container. The model conventions are NOT here and never will
/// be: they are found through an assembly attribute when the context reports the Npgsql provider, so a host that
/// references the package gets them without writing a line. The leadership cannot be found that way — it is a
/// registration, and registration happens long before any context has a provider to report — so this one method
/// is the exception to "referencing the package is the whole of the wiring".</summary>
public static class ReeveWorkloadsPostgreSqlBuilderExtensions
{
    /// <summary>Elects the engine's single leader with a PostgreSQL advisory lock on the workloads database,
    /// replacing the single-replica default that only ever wins. A host running more than one replica needs
    /// this; a host running exactly one loses nothing by calling it.
    ///
    /// <para>Two deployment constraints come with it, both outside this library's control. The workloads
    /// <c>DbContext</c> must be on a connection PINNED to one database session — no transaction-pooling proxy
    /// such as pgbouncer — because a session-scoped advisory lock cannot survive
    /// statements being spread over sessions. The election refuses to lead on such a connection rather than
    /// leading twice, so this shows up as a refusal, not as silent corruption.</para>
    ///
    /// <para>And failover after a LOST NODE takes as long as the server's TCP keepalive budget,
    /// <c>tcp_keepalives_idle + tcp_keepalives_interval * tcp_keepalives_count</c>, because that is when the
    /// server ends the dead session and releases its lock. Out of the box that is 7875 seconds, during which no
    /// replica leads and nothing is deployed, updated, reconciled or repaired. A killed process is detected in
    /// milliseconds; a powered-off node or a partition is not. Set those three to the order of the failover you
    /// want. The election logs the budget it actually found, once, at its first acquisition.</para>
    ///
    /// <para>PostgreSQL 11 or newer, for <c>hashtextextended</c>.</para></summary>
    /// <param name="builder">The builder returned by <c>AddReeveWorkloads</c>.</param>
    /// <returns>The same builder.</returns>
    /// <remarks><c>Add</c>, not <c>AddDefault</c>, and that is the whole mechanism: Core registers
    /// <c>SingleReplicaLeaderElection</c> with <c>AddDefault</c>, which is a <c>TryAdd</c>. This wins from BOTH
    /// sides — called after Core, because the container resolves the last descriptor for a service type; called
    /// before Core, because a TryAdd over an existing descriptor does nothing. A host therefore cannot lose the
    /// real election by ordering its extension calls the other way round.
    /// <para>Singleton, matching the port and matching what Core registers: the election holds no state between
    /// attempts and the runner that drives it is itself a singleton. The leadership it hands back owns a scope of
    /// its own for as long as it is held — see the type's own remarks for why a singleton opens one.</para>
    /// <para>One consequence of <c>Add</c> that nothing else writes down. <c>ReeveWorkloadsBuilder</c> records
    /// only <c>AddDefault</c> registrations, so from this call on
    /// <c>FindHostPortsOnLibraryDefaults()</c> stops naming <c>IWorkloadLeaderElection</c> — the same answer it
    /// gives for a port the HOST supplied, although no host wrote a line of it. That list means "decisions the
    /// library made on this composition's behalf", and by that reading the answer is right: calling this method
    /// is the composition's own decision, so it is not standing on the single-replica default. It would
    /// be the wrong answer only under the reading "ports the host wired itself", which is not what the list is
    /// for.</para></remarks>
    public static ReeveWorkloadsBuilder AddPostgreSqlLeaderElection(this ReeveWorkloadsBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        return builder.Add<IWorkloadLeaderElection, PostgreSqlAdvisoryLockLeaderElection>(ServiceLifetime.Singleton);
    }
}
