using System.ComponentModel.DataAnnotations;

namespace Reeve.Workloads.Core.Providers;

/// <summary>Deadlines for provider calls. Two numbers, not eight: a read is either quick or the provider is
/// unreachable, while an apply legitimately waits on image pulls and volume work.</summary>
/// <remarks>These exist because no provider call in the reconcile path had a deadline of its own — only the
/// pass token, which nothing ever cancelled. A hung socket held the reconciler until the leader changed.</remarks>
public sealed class WorkloadProviderTimeoutOptions
{
    public const string SectionName = "Workloads:ProviderTimeouts";

    /// <summary>The deadline for a read or a short mutation: status, preflight, logs, network digest,
    /// suspend, resume.</summary>
    [Range(1, 3600)]
    public int CallTimeoutSeconds { get; init; } = 60;

    /// <summary>The deadline for an apply or a teardown, which pull images and delete volumes.</summary>
    [Range(1, 7200)]
    public int MutationTimeoutSeconds { get; init; } = 300;

    /// <summary>Set false to hand the provider the caller's token unchanged. For a host that has its own
    /// deadline somewhere outside this library — not a way to opt out of having one at all.</summary>
    public bool Enabled { get; init; } = true;

    public TimeSpan CallTimeout => TimeSpan.FromSeconds(CallTimeoutSeconds);
    public TimeSpan MutationTimeout => TimeSpan.FromSeconds(MutationTimeoutSeconds);
}
