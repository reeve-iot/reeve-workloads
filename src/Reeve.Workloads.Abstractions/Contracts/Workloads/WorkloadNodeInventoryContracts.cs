using System.Text.Json.Serialization;

namespace Reeve.Workloads.Abstractions.Contracts;

/// <summary>The node-attributed device inventory the install wizard offers as pickable device paths.
/// <see cref="Ok"/> is always true here; an empty <see cref="Devices"/> list is a valid state.</summary>
public sealed record WorkloadNodeInventoryResponse
{
    [JsonPropertyName("ok")]
    public required bool Ok { get; init; }

    /// <summary>The discovered per-node serial devices (empty when none discovered / docker provider).</summary>
    [JsonPropertyName("devices")]
    public IReadOnlyList<WorkloadNodeDeviceDto> Devices { get; init; } = [];
}

/// <summary>One discovered node-attributed serial device — the stable by-id path the wizard binds a workload
/// to, plus the node it lives on and a human label for the dropdown.</summary>
public sealed record WorkloadNodeDeviceDto
{
    /// <summary>The Kubernetes node the device is attached to.</summary>
    [JsonPropertyName("nodeName")]
    public required string NodeName { get; init; }

    /// <summary>The stable device path under <c>/dev/serial/by-id</c>.</summary>
    [JsonPropertyName("devicePath")]
    public required string DevicePath { get; init; }

    /// <summary>Device class, e.g. <c>"usb-serial"</c>.</summary>
    [JsonPropertyName("deviceKind")]
    public required string DeviceKind { get; init; }

    /// <summary>Human-readable label for the wizard dropdown, or null when unknown.</summary>
    [JsonPropertyName("label")]
    public string? Label { get; init; }
}
