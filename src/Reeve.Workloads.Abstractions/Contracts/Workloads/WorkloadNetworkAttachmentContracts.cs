using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Reeve.Workloads.Abstractions.Contracts;

/// <summary>The closed, five-value <c>unavailableReason</c> vocabulary on the wire — every degraded enumeration
/// case must name its reason from this list; there is no sixth case.</summary>
public static class WorkloadNetworkAttachmentUnavailableReasons
{
    /// <summary>404 / CRD <c>NotFound</c> — Multus (or its CRD) is not installed.</summary>
    public const string MultusNotInstalled = "multus-not-installed";

    /// <summary>403 — the chart's read grant is missing, or <c>workloads.kubernetes.enabled=false</c>.</summary>
    public const string Forbidden = "forbidden";

    /// <summary>API server unreachable / transport fault / Docker daemon unreachable — Reeve could not ask the provider.</summary>
    public const string ProviderUnavailable = "provider-unavailable";

    /// <summary>200 with an empty list — Multus IS installed but no NAD lives in Reeve's workloads namespace.
    /// Exists so the namespace limit doesn't misreport as <see cref="MultusNotInstalled"/> and send the operator to the wrong runbook.</summary>
    public const string NoneInNamespace = "none-in-namespace";

    /// <summary>The manual provider — there is nothing to enumerate, so the picker degrades to validated free
    /// text with an explicit note that Reeve cannot verify the name out of band.</summary>
    public const string NotSupported = "not-supported";

    /// <summary>The closed vocabulary, in one place, so a consumer can assert membership rather than re-list it.</summary>
    public static IReadOnlyList<string> All { get; } =
    [
        MultusNotInstalled,
        Forbidden,
        ProviderUnavailable,
        NoneInNamespace,
        NotSupported,
    ];
}

/// <summary>Response of <c>GET /api/workloads/network-attachments</c> — populates the install wizard's picker.
/// An empty <see cref="Items"/> list is a valid, non-error result; every degraded case is 200 + empty list + a reason from <see cref="WorkloadNetworkAttachmentUnavailableReasons"/>.</summary>
public sealed record WorkloadNetworkAttachmentListResponse
{
    /// <summary>The effective workload provider the enumeration was dispatched to (explicit setting, else
    /// auto-detected) — never the raw nullable <c>settings.provider</c>, whose null means auto-detect.</summary>
    [JsonPropertyName("provider")]
    public required WorkloadProviderId Provider { get; init; }

    /// <summary>The attachment targets on offer; EMPTY whenever <see cref="UnavailableReason"/> is set.</summary>
    [JsonPropertyName("items")]
    public IReadOnlyList<WorkloadNetworkAttachmentDto> Items { get; init; } = [];

    /// <summary>Null on a successful enumeration (even an empty one). Otherwise a member of
    /// <see cref="WorkloadNetworkAttachmentUnavailableReasons"/> naming why, so the picker degrades to a shown reason rather than free text that can only fail.</summary>
    [JsonPropertyName("unavailableReason")]
    public string? UnavailableReason { get; init; }
}

/// <summary>One network offered to the operator's attachment picker — its name, and nothing read from the
/// network object behind it.</summary>
public sealed record WorkloadNetworkAttachmentDto
{
    /// <summary>The network's name — what an attachment refers to (the naming grammar excludes <c>/</c>).</summary>
    [JsonPropertyName("name")]
    public required string Name { get; init; }
}
