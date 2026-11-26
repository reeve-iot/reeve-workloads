namespace Reeve.Workloads.Core.Update;

/// <summary>Thrown when an update plan cannot be built because the host refused a side's bootstrap. An
/// <see cref="InvalidOperationException"/>, so a caller that treats every "nothing to plan against" alike still
/// does; one that must tell the host's answer apart from a missing instance or manifest catches this type.</summary>
public sealed class WorkloadHostRefusedException : InvalidOperationException
{
    /// <summary>A refusal carrying the host's reason, which is also the message.</summary>
    public WorkloadHostRefusedException(string reason)
        : base(reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        Reason = reason;
    }

    /// <summary>The host's reason, verbatim.</summary>
    public string Reason { get; }
}
