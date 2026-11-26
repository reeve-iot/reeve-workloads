using Reeve.Workloads.Abstractions.Domain.DesiredSpec;
using Reeve.Workloads.Abstractions.Domain.Entities;

namespace Reeve.Workloads.Abstractions;

/// <summary>A stored spec to replay and the journal direction and submitted values that produced it.</summary>
/// <remarks>Submitted parameters can contain plaintext secrets. Never log this record.</remarks>
public sealed record ActiveWorkloadIntentReplay(
    WorkloadDesiredSpec Spec,
    WorkloadUpdateDirection? Direction,
    IReadOnlyDictionary<string, string?> SubmittedParameters)
{
    /// <summary>The stored parameter snapshot used to render this replay target, when the intent carries one.</summary>
    public IReadOnlyDictionary<string, string?>? StoredParameters { get; init; }
    public IReadOnlyList<string>? PasswordParameterNames { get; init; }

    /// <summary>The module version <see cref="Spec"/> was rendered for: the update's target version while it walks
    /// forward, the version it started from while it walks back. Null when the intent does not say.</summary>
    public string? Version { get; init; }
}
