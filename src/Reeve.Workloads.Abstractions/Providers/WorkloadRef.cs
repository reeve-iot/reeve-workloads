using Reeve.Workloads.Abstractions.Contracts;
using Reeve.Workloads.Abstractions.Domain;
using Reeve.Workloads.Abstractions.Domain.DesiredSpec;
using Reeve.Workloads.Abstractions.Domain.Entities;

namespace Reeve.Workloads.Abstractions.Providers;

/// <summary>Lightweight, provider-agnostic handle to a materialized workload; carries only what a provider
/// needs to address it — never the full desired spec. Key on <see cref="InstanceSelectorLabels"/>, never reconstruct a name.</summary>
public readonly record struct WorkloadRef
{
    /// <summary>The canonical workload-instance id — the id the host's seams are keyed on and the label value keyed on for teardown/drift.</summary>
    public required Guid InstanceId { get; init; }

    /// <summary>The owning plugin type id (a resource-name segment).</summary>
    public required string GroupId { get; init; }

    /// <summary>The manifest workload id (a resource-name segment).</summary>
    public required string WorkloadId { get; init; }

    /// <summary>First 8 lowercase-hex chars of <see cref="InstanceId"/> — the resource-name discriminator.</summary>
    public required string Inst8 { get; init; }

    /// <summary>The provider this instance is materialized on.</summary>
    public required WorkloadProviderId Provider { get; init; }

    /// <summary>The canonical label-based lookup for this instance's resources: <c>{ reeve.io/workload-instance
    /// = InstanceId }</c>. This is the only reliable identity key — providers must key on this, never on names.</summary>
    public IReadOnlyDictionary<string, string> InstanceSelectorLabels
        => new Dictionary<string, string> { [WorkloadResourceLabels.InstanceLabelKey] = InstanceId.ToString("D") };

    /// <summary>Derives a ref from a persisted <see cref="WorkloadInstance"/>. Requires the instance to have a bound provider.</summary>
    public static WorkloadRef FromInstance(WorkloadInstance instance)
    {
        ArgumentNullException.ThrowIfNull(instance);
        if (instance.Provider is not { } provider)
        {
            throw new InvalidOperationException(
                $"Workload instance {instance.Id} has no bound provider yet — it cannot be addressed as a WorkloadRef " +
                "until it has deployed (Provider is set on first deploy).");
        }

        return new WorkloadRef
        {
            InstanceId = instance.Id,
            GroupId = instance.GroupId,
            WorkloadId = instance.WorkloadId,
            Inst8 = instance.Inst8,
            Provider = provider,
        };
    }

    /// <summary>Derives a ref from the repository's projection of an instance, for callers — the fleet status
    /// read — that never load the entity. Same requirement as <see cref="FromInstance"/>: a bound provider.</summary>
    public static WorkloadRef FromInstanceRef(WorkloadInstanceRef reference)
    {
        if (reference.Provider is not { } provider)
        {
            throw new InvalidOperationException(
                $"Workload instance {reference.Id} has no bound provider yet — it cannot be addressed as a WorkloadRef " +
                "until it has deployed (Provider is set on first deploy).");
        }

        return new WorkloadRef
        {
            InstanceId = reference.Id,
            GroupId = reference.GroupId,
            WorkloadId = reference.WorkloadId,
            Inst8 = reference.Inst8,
            Provider = provider,
        };
    }

    /// <summary>Derives a ref from a resolved <see cref="WorkloadDesiredSpec"/> (both carry the same identity fields).</summary>
    public static WorkloadRef FromDesiredSpec(WorkloadDesiredSpec spec)
    {
        ArgumentNullException.ThrowIfNull(spec);
        return new WorkloadRef
        {
            InstanceId = spec.InstanceId,
            GroupId = spec.GroupId,
            WorkloadId = spec.WorkloadId,
            Inst8 = spec.Inst8,
            Provider = spec.Provider,
        };
    }
}
