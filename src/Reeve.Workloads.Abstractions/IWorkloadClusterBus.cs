namespace Reeve.Workloads.Abstractions;

/// <summary>Best-effort, fire-and-forget inter-replica hint bus. An unreachable broker degrades to a no-op:
/// correctness rests on each channel's DB-authoritative state, never on a hint arriving.</summary>
public interface IWorkloadClusterBus
{
    /// <summary>Fans a payload out to peer replicas on <paramref name="channel"/>. Never throws on a broker
    /// fault — an implementation logs and swallows.</summary>
    Task PublishAsync<T>(string channel, T payload, CancellationToken cancellationToken = default);

    /// <summary>Registers a handler for <paramref name="channel"/>, skipping this replica's own messages;
    /// <paramref name="onReconcile"/> fires on subscribe and reconnect to self-heal missed hints.</summary>
    IDisposable Subscribe<T>(
        string channel,
        Func<T, CancellationToken, Task> handler,
        Func<CancellationToken, Task>? onReconcile = null);
}

/// <summary>The workload channel names. These are WIRE NAMES, append-only: renaming one without a coordinated
/// rollout makes a mixed-version cluster drop every hint on it, silently.</summary>
public static class WorkloadClusterChannels
{
    /// <summary>Requests the leader's reconciler reconcile one instance now, or all when no id is carried.</summary>
    public const string ReconcileRequest = "workload_reconcile_request";

    /// <summary>The channels this library publishes or subscribes to. A host's own channels are the host's to
    /// declare and are not listed here.</summary>
    public static readonly IReadOnlyList<string> All = [ReconcileRequest];
}

/// <summary>The shipped default: one replica, so there are no peers to hint. Correct rather than degraded —
/// <see cref="IWorkloadClusterBus.Subscribe{T}"/>'s contract is that a bus skips this replica's OWN messages,
/// and in a single process every message is that.</summary>
/// <remarks>A host that runs several replicas MUST register a real bus. It does not lose correctness by
/// leaving this in place — each channel's state is DB-authoritative, which is the whole reason this port may
/// have a default at all — but every cross-replica hint is dropped, so reconcile waits for its timer instead
/// of a nudge. That decline is reported: the port appears in <c>FindHostPortsOnLibraryDefaults</c>, which a host can log at boot.
///
/// <para><c>onReconcile</c> is deliberately NOT fired. It exists to self-heal hints missed while
/// disconnected, and this bus misses none because it carries none; a subscriber's own boot pass is its
/// initial sync.</para></remarks>
public sealed class InProcessWorkloadClusterBus : IWorkloadClusterBus
{
    /// <inheritdoc />
    public Task PublishAsync<T>(string channel, T payload, CancellationToken cancellationToken = default)
        => Task.CompletedTask;

    /// <inheritdoc />
    public IDisposable Subscribe<T>(
        string channel,
        Func<T, CancellationToken, Task> handler,
        Func<CancellationToken, Task>? onReconcile = null)
    {
        ArgumentNullException.ThrowIfNull(handler);
        return NoSubscription.Instance;
    }

    private sealed class NoSubscription : IDisposable
    {
        public static readonly NoSubscription Instance = new();

        public void Dispose()
        {
        }
    }
}

