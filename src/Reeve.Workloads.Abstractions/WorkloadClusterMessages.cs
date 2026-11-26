using System.Text.Json.Serialization;

namespace Reeve.Workloads.Abstractions;

/// <summary>Bus request asking the leader's reconciler to run now; a null instance id means all instances.
/// Hint-grade — a dropped request is subsumed by the periodic drift pass.</summary>
public sealed record WorkloadReconcileRequestMessage(
    [property: JsonPropertyName("instanceId")] Guid? InstanceId,
    [property: JsonPropertyName("reason")] string Reason);
