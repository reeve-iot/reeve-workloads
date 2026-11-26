using System.Collections.Concurrent;

namespace Reeve.Workloads.Abstractions;

/// <summary>A held cross-replica lock. Disposing releases it; an implementation must not throw on release.</summary>
public interface IWorkloadLock : IAsyncDisposable
{
    /// <summary>Whether this lock is still held. A long critical section re-checks rather than assuming.</summary>
    Task<bool> IsValidAsync(CancellationToken cancellationToken = default);
}

/// <summary>Cross-replica mutual exclusion for operations that must run on one replica at a time.</summary>
public interface IWorkloadLockProvider
{
    /// <summary>Takes <paramref name="lockKey"/>, or returns null when another replica holds it. Never blocks:
    /// the caller decides whether losing the race is a retry or a refusal.</summary>
    Task<IWorkloadLock?> TryAcquireAsync(string lockKey, CancellationToken cancellationToken = default);
}

/// <summary>The shipped default: mutual exclusion WITHIN ONE PROCESS. Correct for a single-replica host and
/// wrong for any other, which is not the same bargain the cluster bus offers and is worth saying plainly.</summary>
/// <remarks>The bus may be defaulted because its port promises correctness never rests on a hint arriving.
/// This port promises the opposite — it exists for "operations that must run on one replica at a time" — so on
/// several replicas this default lets every replica win its own take and enter a section meant for one. It is
/// still shipped, because a single-replica host is the common deployment and the alternative is a port every
/// such host must write by hand; and because the decline is REPORTED rather than silent: the port appears in
/// <c>FindHostPortsOnLibraryDefaults</c>, which a host can log at boot, one warning per port.
///
/// <para>A multi-replica host registers a real provider. There is no way for this type to detect that it is
/// in one — replica count is host knowledge — so the boot log is the signal.</para></remarks>
public sealed class InProcessWorkloadLockProvider : IWorkloadLockProvider
{
    private readonly ConcurrentDictionary<string, object> held = new(StringComparer.Ordinal);

    /// <inheritdoc />
    public Task<IWorkloadLock?> TryAcquireAsync(string lockKey, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(lockKey);

        var token = new object();
        return Task.FromResult<IWorkloadLock?>(
            held.TryAdd(lockKey, token) ? new HeldLock(held, lockKey, token) : null);
    }

    private sealed class HeldLock : IWorkloadLock
    {
        private readonly ConcurrentDictionary<string, object> held;
        private readonly string lockKey;
        private readonly object token;

        public HeldLock(ConcurrentDictionary<string, object> held, string lockKey, object token)
        {
            this.held = held;
            this.lockKey = lockKey;
            this.token = token;
        }

        public Task<bool> IsValidAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(held.TryGetValue(lockKey, out var current) && ReferenceEquals(current, token));

        public ValueTask DisposeAsync()
        {
            held.TryRemove(new KeyValuePair<string, object>(lockKey, token));
            return ValueTask.CompletedTask;
        }
    }
}

