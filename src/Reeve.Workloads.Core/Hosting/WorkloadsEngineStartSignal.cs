namespace Reeve.Workloads.Core;

internal sealed class WorkloadsEngineStartSignal
{
    private int started;

    public bool TryMarkStarted() => Interlocked.Exchange(ref started, 1) == 0;

    public void Unmark() => Volatile.Write(ref started, 0);

    public bool WasStarted => Volatile.Read(ref started) == 1;
}
