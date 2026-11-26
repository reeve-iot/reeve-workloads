using System;
using Reeve.Workloads.Abstractions.Contracts;

namespace Reeve.Workloads.Abstractions.Domain.Network;

/// <summary>The network configuration a host accepted for one workload: the mode and the attachments in DECLARED
/// order. The engine applies an operator-network spec only while the mode and names match and every attachment
/// exists.</summary>
/// <remarks>Facts, not a verdict: whether the configuration was acceptable is the host's decision, made before it
/// pins.</remarks>
public sealed record WorkloadAttachmentPin
{
    /// <summary>The resolved network mode the configuration was accepted for.</summary>
    public required WorkloadNetworkMode Mode { get; init; }

    /// <summary>The attachment names in declared order. Never sorted: attachments connect in this order.</summary>
    public required IReadOnlyList<string> AttachmentNames { get; init; }
}
