using System.Text.Json.Serialization;

namespace Reeve.Workloads.Abstractions.Contracts;

/// <summary><see cref="Acknowledged"/> is the fail-closed gate: the helper runs ONLY when it is true, and a
/// false or omitted acknowledgement returns the disclosure without touching docker.</summary>
public sealed record WorkloadDeviceDiscoveryRequest
{
    /// <summary>The operator's explicit acknowledgement that the helper reads the host
    /// <c>/dev/serial/by-id</c>. It does NOT run until this is true.</summary>
    [JsonPropertyName("acknowledged")]
    public bool Acknowledged { get; init; }
}

/// <summary><see cref="Ok"/> is false only on a hard fault; <see cref="Disclosure"/> is ALWAYS present, and
/// <see cref="Ran"/> is true only when the acknowledged helper enumerated to completion.</summary>
public sealed record WorkloadDeviceDiscoveryResponse
{
    [JsonPropertyName("ok")]
    public required bool Ok { get; init; }

    /// <summary>Echoes whether the run was authorized. False ⇒ fail-closed (the helper was NOT run).</summary>
    [JsonPropertyName("acknowledged")]
    public required bool Acknowledged { get; init; }

    /// <summary>True only when the acknowledged helper enumerated to completion (the inventory is authoritative).</summary>
    [JsonPropertyName("ran")]
    public required bool Ran { get; init; }

    /// <summary>The danger disclosure stating the helper reads the host <c>/dev</c>. Always present, so the
    /// wizard can render it before as well as after acknowledgement.</summary>
    [JsonPropertyName("disclosure")]
    public required WorkloadDisclosure Disclosure { get; init; }

    /// <summary>The discovered host serial devices (empty unless the helper ran and found any).</summary>
    [JsonPropertyName("devices")]
    public IReadOnlyList<DiscoveredSerialDevice> Devices { get; init; } = [];

    /// <summary>A human-readable note (why the helper did not run / a hard fault), or null on a clean run.</summary>
    [JsonPropertyName("message")]
    public string? Message { get; init; }

    /// <summary>A hard-fault result (e.g. the effective provider is not docker) — nothing was run.</summary>
    public static WorkloadDeviceDiscoveryResponse Blocked(WorkloadDisclosure disclosure, string message) => new()
    {
        Ok = false,
        Acknowledged = false,
        Ran = false,
        Disclosure = disclosure,
        Message = message,
    };
}

/// <summary>One discovered host serial device — the stable by-id path the wizard binds a workload to.</summary>
public sealed record DiscoveredSerialDevice
{
    /// <summary>The stable device path under <c>/dev/serial/by-id</c> (e.g. <c>/dev/serial/by-id/usb-FTDI_…</c>).</summary>
    [JsonPropertyName("path")]
    public required string Path { get; init; }
}

/// <summary>What a provider helper does to the host, stated before the operator acknowledges it.</summary>
public sealed record WorkloadDisclosure
{
    /// <summary>Stable machine code (e.g. <c>deviceDiscovery.hostDev</c>).</summary>
    [JsonPropertyName("code")]
    public required string Code { get; init; }

    /// <summary>The provider's own words for what the helper does.</summary>
    [JsonPropertyName("label")]
    public required string Label { get; init; }

    /// <summary>A <see cref="WorkloadDisclosureSeverity"/> token.</summary>
    [JsonPropertyName("severity")]
    public required string Severity { get; init; }
}

/// <summary>Severity tokens for a <see cref="WorkloadDisclosure"/>.</summary>
public static class WorkloadDisclosureSeverity
{
    public const string Danger = "danger";
    public const string Info = "info";
}
