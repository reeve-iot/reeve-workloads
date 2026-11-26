using System.Text.Json.Serialization;

namespace Reeve.Workloads.Abstractions.Contracts;

/// <summary>How a workload's pod is attached to the network. Deliberately NOT a manifest vocabulary.
/// Ordinals are APPEND-ONLY; the wire spelling is the <c>[JsonStringEnumMemberName]</c>, never ToString().</summary>
[JsonConverter(typeof(JsonStringEnumConverter<WorkloadNetworkMode>))]
public enum WorkloadNetworkMode
{
    /// <summary>The default: the pod gets its address from the cluster pod network and reaches the LAN only
    /// through NAT / a Service.</summary>
    [JsonStringEnumMemberName("cluster")]
    Cluster = 0,

    /// <summary>The pod shares the node's network namespace, so LAN broadcast/multicast discovery works at
    /// the cost of node-wide port exposure.</summary>
    [JsonStringEnumMemberName("hostNetwork")]
    HostNetwork = 1,

    /// <summary>The pod receives an additional interface from an operator-defined attachment, giving it its
    /// own address on the chosen LAN/VLAN without taking over the node's namespace.</summary>
    [JsonStringEnumMemberName("operatorNetwork")]
    OperatorNetwork = 2,
}
