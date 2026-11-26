using System;

namespace Reeve.Workloads.Abstractions;

/// <summary>Marks a mutable per-process coordination collection as INTENTIONALLY single-node: each replica keeps
/// its own copy. Purely declarative — state that must be shared needs Redis and <see cref="ClusterSafeAttribute"/>.</summary>
[AttributeUsage(
    AttributeTargets.Field | AttributeTargets.Property,
    AllowMultiple = false,
    Inherited = false)]
public sealed class SingleNodeStateAttribute(string justification) : Attribute
{
    /// <summary>Why keeping this coordination state per-process (un-propagated) is correct — each replica
    /// keeping its own copy is acceptable/expected here.</summary>
    public string Justification { get; } = justification;
}

/// <summary>Counterpart to <see cref="SingleNodeStateAttribute"/>: the map is only a local materialization of
/// state propagated across replicas. Purely declarative — the propagation must already exist in the code.</summary>
[AttributeUsage(
    AttributeTargets.Field | AttributeTargets.Property,
    AllowMultiple = false,
    Inherited = false)]
public sealed class ClusterSafeAttribute(string justification) : Attribute
{
    /// <summary>How this coordination state stays consistent across replicas (the Redis key / IClusterBus
    /// topic that propagates it). It documents why the state is multi-replica-safe.</summary>
    public string Justification { get; } = justification;
}
