namespace Reeve.Workloads.Abstractions;

/// <summary>Who the current operation is attributed to. Deliberately narrower than the host's user accessor: the
/// workloads side stamps an actor onto audit entries and never reads claims.</summary>
public interface IWorkloadActorAccessor
{
    /// <summary>The acting user's id, or null outside a request (a reconcile pass, a background sweep).</summary>
    Guid? ActorUserId { get; }
}

/// <summary>The shipped no-op: everything is attributed to no actor, which is already what a background pass
/// records. Explicit rather than a nullable parameter, so no call site has to decide it again.</summary>
public sealed class NoWorkloadActorAccessor : IWorkloadActorAccessor
{
    /// <inheritdoc />
    public Guid? ActorUserId => null;
}
