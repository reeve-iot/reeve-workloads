using Reeve.Workloads.Abstractions.Contracts;
using Reeve.Workloads.Abstractions.Domain.Entities;

namespace Reeve.Workloads.Abstractions.Providers;

/// <summary>Everything a resource NAME is derived from: the owning plugin type, the manifest workload, and the
/// instance discriminator that keeps two installs of the same workload apart.</summary>
/// <remarks>A record rather than three string parameters because the three travel together through every namer
/// and every call site, and a transposed pair of adjacent strings names a resource that exists — just not the
/// one meant. The compiler cannot catch that; a named member can.</remarks>
public readonly record struct WorkloadNamingSubject
{
    /// <summary>The owning plugin type id — the first name segment.</summary>
    public required string GroupId { get; init; }

    /// <summary>The manifest workload id — the second name segment.</summary>
    public required string WorkloadId { get; init; }

    /// <summary>First 8 lowercase-hex chars of the instance id. Every name a namer produces embeds it.</summary>
    public required string Inst8 { get; init; }
}

/// <summary>Everything a LABEL set is derived from. Labels carry identity and provenance, so they take the full
/// instance id rather than the discriminator.</summary>
public readonly record struct WorkloadLabelSubject
{
    /// <summary>The canonical workload-instance id. Adoption, drift and teardown key on the label carrying it.</summary>
    public required Guid InstanceId { get; init; }

    /// <summary>The installation this instance belongs to — co-tenancy hygiene, not an adversarial boundary.</summary>
    public required string InstallationId { get; init; }

    /// <summary>The desired spec's hash, the drift/rollout discriminator.</summary>
    public required string SpecHash { get; init; }

    /// <summary>Set for an object carrying a backup-selectable volume; absent otherwise.</summary>
    public string? Criticality { get; init; }
}

/// <summary>How one provider names and labels what it materializes. Ships WITH the provider package when the
/// provider names things its own way; a provider that does not register one gets the library's default namer,
/// which is the RFC 1123 object scheme the Kubernetes and Manual providers use.</summary>
/// <remarks>
/// <para>
/// A provider the library has never heard of still gets stable, correct names, including the physical name of every volume it retains, which is the name a later restore has to find on disk.
/// </para>
/// <para>
/// THE NAMES ARE PERSISTED AND THEY ADDRESS LIVE OBJECTS. A namer that changes its output renames nothing: it
/// makes the engine look for resources under a name nothing created, and a retained volume under a name
/// nothing will ever mount. Byte-identical output is the whole contract, which is why the grammar the names
/// are built from — the sanitiser, the DNS truncation, the deterministic hash suffix — stays in one place in
/// <c>Reeve.Workloads.Domain</c> and is not reimplemented per provider.
/// </para>
/// </remarks>
public interface IWorkloadResourceNamer
{
    /// <summary>The provider this namer names for. The library's default namer leaves it
    /// <see cref="WorkloadProviderId.IsEmpty"/>: it belongs to no provider and answers for every provider that
    /// registers none.</summary>
    WorkloadProviderId Provider { get; }

    /// <summary>The scheme recorded on a volume this namer named, so a later restore can resolve the physical
    /// name the way the namer that created it would have.</summary>
    RetainedVolumeSchemeId VolumeScheme { get; }

    /// <summary>The base object name — a Kubernetes workload object, a docker container, whatever this provider
    /// materializes one of per instance.</summary>
    string ObjectName(WorkloadNamingSubject subject);

    /// <summary>The name of the object holding this instance's secrets. The same scheme as
    /// <see cref="ObjectName"/> for every provider that ships here; separate because it need not be.</summary>
    string SecretName(WorkloadNamingSubject subject);

    /// <summary>The physical name of one declared volume. This is the one name the shipped providers genuinely
    /// disagree about, and the one whose scheme is recorded when the volume is retained.</summary>
    string VolumeName(WorkloadNamingSubject subject, string volumeId);

    /// <summary>The label set stamped on what this provider materializes: identity, installation, spec hash,
    /// and criticality when the object carries a backup-selectable volume.</summary>
    IReadOnlyDictionary<string, string> Labels(WorkloadLabelSubject subject);

    /// <summary>The label selector that finds this instance's resources. Adoption, drift and teardown key on
    /// THIS — names are convenience, this selector is authority.</summary>
    IReadOnlyDictionary<string, string> InstanceSelectorLabels(Guid instanceId);
}

/// <summary>Resolves the namer for a provider. Implemented in <c>Reeve.Workloads.Core</c> over whatever namers
/// the referenced provider packages registered, falling back to the library's default.</summary>
public interface IWorkloadResourceNamerRegistry
{
    /// <summary>The namer for <paramref name="provider"/>, or the default namer when that provider registered
    /// none. Total: there is always a namer, because a workload with no name cannot be materialized at all.</summary>
    IWorkloadResourceNamer For(WorkloadProviderId provider);
}
