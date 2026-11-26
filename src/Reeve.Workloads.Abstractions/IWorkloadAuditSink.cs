namespace Reeve.Workloads.Abstractions;

/// <summary>The workloads side's audit seam. The host decides where an entry lands; what is worth recording — the
/// action names and metadata shapes — stays here, because that is a property of the operation, not of the log.</summary>
public interface IWorkloadAuditSink
{
    /// <summary>Records one entry. An implementation must not throw: auditing is bookkeeping beside an action that
    /// already happened, never a reason to fail it.</summary>
    Task WriteAsync(WorkloadAuditEntry entry, CancellationToken cancellationToken = default);
}

/// <summary>One audit record. <c>Metadata</c> is serialised by the host and carries the anonymous shapes the call
/// sites compose; secrets never go in it.</summary>
public sealed record WorkloadAuditEntry(
    Guid? ActorUserId,
    string Action,
    string EntityType,
    Guid? EntityId = null,
    object? Metadata = null);

/// <summary>The shipped no-op for a host with no audit log. Explicit rather than a nullable parameter, because a
/// nullable seam makes every call site decide again whether auditing is optional .</summary>
public sealed class NoWorkloadAuditSink : IWorkloadAuditSink
{
    /// <inheritdoc />
    public Task WriteAsync(WorkloadAuditEntry entry, CancellationToken cancellationToken = default)
        => Task.CompletedTask;
}
