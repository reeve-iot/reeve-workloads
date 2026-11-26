namespace Reeve.Workloads.Abstractions.Update;

/// <summary>Wakes the update walk now, instead of when its interval next comes round.</summary>
/// <remarks>
/// <para>The walk goes over every open update in turn, then waits the engine's working-tick interval
/// (<c>Workloads:UpdateWalk:WorkingTickSeconds</c>, five seconds unless configured) before going over them again — or
/// less, if <see cref="Wake"/> is called. So a stage that answered <c>Working</c> is asked again once the rest of that
/// pass is done and the interval has passed, and sooner after a wake; a pass also waits for any drift reconcile of the
/// same group to finish before walking it. A host stage that waits on an event — the rolling plugin reload waits on
/// each replica's acknowledgement — calls this when the event lands, so the next replica is taken without waiting
/// out the interval.</para>
/// <para>It reaches the walk in THIS process, and only the leader runs one: on any other replica it does nothing,
/// and the leader's own interval still bounds the wait. A host whose event lands on a follower forwards it to the
/// leader the way it forwards anything else.</para>
/// </remarks>
public interface IWorkloadUpdateWalkTrigger
{
    /// <summary>Asks for a walk tick as soon as the current one, if any, has finished. Never blocks, never throws,
    /// and any number of calls before the tick starts come to one tick.</summary>
    void Wake();
}
