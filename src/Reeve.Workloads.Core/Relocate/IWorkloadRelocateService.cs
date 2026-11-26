using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Reeve.Workloads.Abstractions.Contracts.Deployment;

namespace Reeve.Workloads.Core.Relocate;

/// <summary>The relocate-workload flow: the operator retargets <c>nodeSelector</c>, <see cref="PlanRelocateAsync"/>
/// previews the move, and <see cref="ExecuteRelocateAsync"/> writes the new placement and marks the instance for
/// redeploy. Relocate never deletes or recreates a volume: a PVC is assumed to follow the pod, and one whose
/// PersistentVolume is provably bound to a node the new selector excludes is only reported, in
/// <see cref="RelocatePlan.NodeBoundVolumes"/>; one whose binding could not be read is reported in
/// <see cref="RelocatePlan.UnverifiedVolumes"/>. Moving that data is the operator's job.</summary>
public interface IWorkloadRelocateService
{
    /// <summary>Computes the relocate plan. Null when the instance is unknown, its workload is no longer declared,
    /// its effective provider is not Kubernetes, or the host refused its bootstrap (logged with the reason —
    /// <see cref="ExecuteRelocateAsync"/> refuses by name).</summary>
    Task<RelocatePlan?> PlanRelocateAsync(
        Guid instanceId,
        IReadOnlyDictionary<string, string> newNodeSelector,
        CancellationToken ct);

    /// <summary>Executes the relocate: re-plans, refuses a placement the plan cannot carry out, and otherwise writes
    /// the new selector and triggers a redeploy. Deletes nothing.</summary>
    Task<RelocateResult> ExecuteRelocateAsync(
        Guid instanceId,
        IReadOnlyDictionary<string, string> newNodeSelector,
        CancellationToken ct);
}

/// <summary>The plan for a proposed relocate. Pure data — the node-bound reasoning is computed by
/// <c>KubernetesRelocatePlanner</c>, the I/O by <c>KubernetesWorkloadRelocateService</c>.</summary>
public sealed record RelocatePlan
{
    public required Guid InstanceId { get; init; }

    /// <summary>The new node-selector label→value map the relocate would move the workload onto.</summary>
    public required IReadOnlyDictionary<string, string> NewNodeSelector { get; init; }

    /// <summary>A warning, not a delete set: volumes whose PersistentVolume is pinned by node affinity to a node the
    /// new selector does not admit. The relocate proceeds and leaves them in place, so the pod will not schedule
    /// until the operator moves or removes that data. A volume whose binding cannot be read is listed in
    /// <see cref="UnverifiedVolumes"/> instead.</summary>
    public required IReadOnlyList<NodeBoundVolume> NodeBoundVolumes { get; init; }

    /// <summary>A warning, not a delete set: volumes for which the node-bound check could not run, because the
    /// claim, its PersistentVolume or the bound node's labels could not be read (the PersistentVolume and node reads
    /// need the opt-in read-only ClusterRole) or the cluster was unreachable. The relocate proceeds; such a volume
    /// may be pinned to a node the new selector excludes, which leaves the pod Pending.</summary>
    public IReadOnlyList<UnverifiedVolume> UnverifiedVolumes { get; init; } = [];

    /// <summary>True when the requested placement can be persisted losslessly through the workload's declared
    /// nodeSelector parameters. False means applying it would drop selector entries — execute refuses (SelectorNotRepresentable), so the UI can warn first.</summary>
    public bool SelectorRepresentable { get; init; } = true;

    /// <summary>Adopted-claim volumes (a "Restore previous data" install's retained PVC) provably bound to a node the
    /// new selector does not admit. Non-empty refuses the relocate outright (AdoptedClaimNotRelocatable, nothing
    /// mutated); deliberately absent from <see cref="NodeBoundVolumes"/>.</summary>
    public IReadOnlyList<NodeBoundVolume> AdoptedVolumesBlockingRelocate { get; init; } = [];

    /// <summary>True when <see cref="AdoptedVolumesBlockingRelocate"/> is non-empty — the relocate is blocked.</summary>
    public bool HasStrandedAdoptedClaim => AdoptedVolumesBlockingRelocate.Count > 0;
}

/// <summary>A volume whose data lives on one node that a relocate would move the pod away from.</summary>
public sealed record NodeBoundVolume
{
    /// <summary>The manifest volume id (e.g. <c>state</c>).</summary>
    public required string VolumeId { get; init; }

    /// <summary>The volume's criticality, so the UI can weigh the warning.</summary>
    public required VolumeCriticality Criticality { get; init; }

    /// <summary>The author's volume description; empty when the author declared none.</summary>
    public required string Description { get; init; }

    /// <summary>The provider PVC name the data physically lives under.</summary>
    public required string PvcName { get; init; }

    /// <summary>The node the PersistentVolume's node affinity pins the data to (its <c>kubernetes.io/hostname</c> value).</summary>
    public required string BoundNode { get; init; }
}

/// <summary>A volume whose binding to a node a relocate could not verify.</summary>
public sealed record UnverifiedVolume
{
    /// <summary>The manifest volume id (e.g. <c>state</c>).</summary>
    public required string VolumeId { get; init; }

    /// <summary>The volume's criticality, so the UI can weigh the warning.</summary>
    public required VolumeCriticality Criticality { get; init; }

    /// <summary>The author's volume description; empty when the author declared none.</summary>
    public required string Description { get; init; }

    /// <summary>The provider PVC name the data physically lives under.</summary>
    public required string PvcName { get; init; }
}

/// <summary>The outcome kind of an execute-relocate attempt.</summary>
public enum RelocateOutcome
{
    /// <summary>The placement was applied and redeploy triggered.</summary>
    Relocated = 0,

    /// <summary>No instance exists for the supplied id.</summary>
    NotFound = 1,

    /// <summary>The instance is in a non-mutable state (Unknown/Terminating) — the relocate is rejected.</summary>
    NotMutable = 3,

    /// <summary>The workload cannot express the requested placement (too few/zero nodeSelector parameters) —
    /// refused, nothing mutated: the pod would redeploy onto the old placement.</summary>
    SelectorNotRepresentable = 4,

    /// <summary>The relocate would move the pod away from the node an adopted (retained, must-exist) claim is
    /// provably bound to. Refused, nothing mutated; the operator must uninstall+reinstall.</summary>
    AdoptedClaimNotRelocatable = 5,

    /// <summary>The instance's effective provider is not the one the registered relocate service moves. Refused,
    /// nothing mutated.</summary>
    
    RelocateNotSupportedByProvider = 6,

    /// <summary>The host refused the workload's bootstrap, so no plan could be rendered. Refused before any write,
    /// nothing mutated; <see cref="RelocateResult.Error"/> carries the host's reason.</summary>
    HostRefused = 7,
}

/// <summary>The typed result of <see cref="IWorkloadRelocateService.ExecuteRelocateAsync"/> — a closed record
/// so a host can map each outcome to one response.</summary>
public sealed record RelocateResult
{
    public required RelocateOutcome Outcome { get; init; }

    /// <summary>The plan the execute acted on (always set for Relocated). Its
    /// <see cref="RelocatePlan.NodeBoundVolumes"/> name the volumes left behind on their node.</summary>
    public RelocatePlan? Plan { get; init; }

    /// <summary>True when the nodeSelector parameter was rewritten + the instance marked for redeploy (Relocated only).</summary>
    public bool RedeployTriggered { get; init; }

    /// <summary>The host's reason, set only for <see cref="RelocateOutcome.HostRefused"/>.</summary>
    public string? Error { get; init; }

    public static RelocateResult Relocated(RelocatePlan plan) => new()
    {
        Outcome = RelocateOutcome.Relocated,
        Plan = plan,
        RedeployTriggered = true,
    };

    public static RelocateResult NotFound() => new() { Outcome = RelocateOutcome.NotFound };

    public static RelocateResult NotMutable() => new() { Outcome = RelocateOutcome.NotMutable };

    /// <summary>The workload cannot express the requested placement — refused, nothing mutated. Carries the plan
    /// (SelectorRepresentable=false) so the UI can explain why.</summary>
    public static RelocateResult SelectorNotRepresentable(RelocatePlan plan) => new()
    {
        Outcome = RelocateOutcome.SelectorNotRepresentable,
        Plan = plan,
    };

    /// <summary>The relocate would strand a retained (adopted) claim — refused, nothing mutated. Carries the
    /// plan with AdoptedVolumesBlockingRelocate populated so the UI can name the blocking volumes.</summary>
    public static RelocateResult AdoptedClaimNotRelocatable(RelocatePlan plan) => new()
    {
        Outcome = RelocateOutcome.AdoptedClaimNotRelocatable,
        Plan = plan,
    };

    /// <summary>The instance's effective provider is not one this relocate service moves — refused, nothing
    /// mutated. No plan is built (another provider's inspection seams don't apply), so <see cref="Plan"/> is null.</summary>
    public static RelocateResult NotSupportedByProvider() => new() { Outcome = RelocateOutcome.RelocateNotSupportedByProvider };

    /// <summary>The host refused the workload's bootstrap, carrying its reason. Nothing mutated.</summary>
    public static RelocateResult HostRefused(string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        return new() { Outcome = RelocateOutcome.HostRefused, Error = reason };
    }
}
