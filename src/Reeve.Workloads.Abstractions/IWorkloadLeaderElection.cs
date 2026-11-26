namespace Reeve.Workloads.Abstractions;

/// <summary>Contends for the engine's single leadership. One leadership covers every engine unit: they are all
/// leader-only, so spreading them buys load distribution, not availability.</summary>
public interface IWorkloadLeaderElection
{
    /// <summary>Try to become leader. Null when someone else holds it; the caller waits and tries again.</summary>
    Task<IWorkloadLeadership?> TryAcquireAsync(CancellationToken cancellationToken);
}

/// <summary>Leadership, for as long as it is held. Disposing resigns it.</summary>
public interface IWorkloadLeadership : IAsyncDisposable
{
    /// <summary>Cancelled the moment leadership is lost. The engine's units stop on it.
    ///
    /// It must stay readable, and read cancelled, after the leadership has been resigned. An implementation
    /// backed by a <see cref="CancellationTokenSource"/> it disposes in <see cref="IAsyncDisposable.DisposeAsync"/>
    /// therefore captures the token BEFORE disposing the source: a property that returned <c>source.Token</c>
    /// would throw <see cref="ObjectDisposedException"/> at a caller that is holding a reference to a leadership
    /// it has already let go, which is a legitimate thing for a caller to be doing.</summary>
    CancellationToken Lost { get; }
}
