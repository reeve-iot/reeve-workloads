namespace Reeve.Workloads.Abstractions;

/// <summary>Someone else wrote the row between the read and the write, so this write was refused rather than
/// applied over theirs. Expected, not exceptional: the caller re-reads and tries again.</summary>
/// <remarks>The library's own type on purpose. A store-specific concurrency exception would make every caller
/// in Core reference that store, which is exactly the seam the agnostic packages exist to keep.</remarks>
public sealed class WorkloadConcurrencyConflictException : Exception
{
    public WorkloadConcurrencyConflictException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }

    /// <summary>The row that lost, when the caller needs to name it. A store that saves every pending row at once
    /// names the row whose write was refused, which need not be the one the caller passed; when the refusal is on
    /// an entity of another type, the store has no such row to name and this is the row the caller passed.</summary>
    public Guid EntityId { get; init; }
}
