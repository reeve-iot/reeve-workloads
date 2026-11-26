using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using k8s;
using Microsoft.Extensions.Logging;
using Reeve.Workloads.Abstractions;
using Reeve.Workloads.Abstractions.Contracts;
using Reeve.Workloads.Abstractions.Contracts.Deployment;
using Reeve.Workloads.Abstractions.Domain.DesiredSpec;
using Reeve.Workloads.Abstractions.Domain.Entities;
using Reeve.Workloads.Abstractions.Providers;
using Reeve.Workloads.Core.Relocate;
using Reeve.Workloads.Domain.DesiredSpec;
using Reeve.Workloads.Providers.Kubernetes;

namespace Reeve.Workloads.Providers.Kubernetes.Relocate;

internal sealed class KubernetesWorkloadRelocateService : IWorkloadRelocateService
{
    private readonly IWorkloadInstanceRepository repository;
    private readonly IWorkloadReconcileInputsProvider inputsProvider;
    private readonly IKubernetesClusterInspector clusterInspector;
    private readonly IKubernetesPvcInspector pvcInspector;
    private readonly IKubernetesClientFactory clientFactory;
    private readonly IWorkloadReconcileNudge reconcileNudge;
    private readonly IWorkloadClock clock;
    private readonly KubernetesNamespaceResolver namespaceResolver;
    private readonly ILogger<KubernetesWorkloadRelocateService> logger;
    private readonly IWorkloadProviderRegistry providerRegistry;

    public KubernetesWorkloadRelocateService(
        IWorkloadInstanceRepository repository,
        IWorkloadReconcileInputsProvider inputsProvider,
        IKubernetesClusterInspector clusterInspector,
        IKubernetesPvcInspector pvcInspector,
        IKubernetesClientFactory clientFactory,
        IWorkloadReconcileNudge reconcileNudge,
        IWorkloadClock clock,
        KubernetesNamespaceResolver namespaceResolver,
        ILogger<KubernetesWorkloadRelocateService> logger,
        IWorkloadProviderRegistry providerRegistry)
    {
        this.repository = repository;
        this.inputsProvider = inputsProvider;
        this.clusterInspector = clusterInspector;
        this.pvcInspector = pvcInspector;
        this.clientFactory = clientFactory;
        this.reconcileNudge = reconcileNudge ?? throw new ArgumentNullException(nameof(reconcileNudge));
        this.clock = clock;
        this.namespaceResolver = namespaceResolver ?? throw new ArgumentNullException(nameof(namespaceResolver));
        this.logger = logger;
        this.providerRegistry = providerRegistry ?? throw new ArgumentNullException(nameof(providerRegistry));
    }

    public async Task<RelocatePlan?> PlanRelocateAsync(
        Guid instanceId, IReadOnlyDictionary<string, string> newNodeSelector, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(newNodeSelector);

        var instance = await repository.GetByIdAsync(instanceId, ct).ConfigureAwait(false);
        if (instance is null)
        {
            return null;
        }

        var (plan, refusal) = await BuildPlanAsync(instance, newNodeSelector, ct).ConfigureAwait(false);
        if (refusal is not null)
        {
            logger.LogWarning(
                "workloads.relocate: instance={InstanceId} no plan — the host refused its bootstrap: {Refusal}.",
                instance.Id, refusal);
        }

        return plan;
    }

    public async Task<RelocateResult> ExecuteRelocateAsync(
        Guid instanceId, IReadOnlyDictionary<string, string> newNodeSelector, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(newNodeSelector);

        var instance = await repository.GetByIdAsync(instanceId, ct).ConfigureAwait(false);
        if (instance is null)
        {
            return RelocateResult.NotFound();
        }

        if (!instance.CanMutate)
        {
            return RelocateResult.NotMutable();
        }

        if (instance.IsOwnershipReleased(providerRegistry) && !instance.IsHeldByHost)
        {
            logger.LogWarning(
                "workloads.relocate: instance={InstanceId} refused — Reeve released ownership of the instance ({State}); a disowned/mid-switch runtime is operator-owned and must not be relocated.",
                instance.Id, instance.State);
            return RelocateResult.NotMutable();
        }

        if (instance.IsHeldByHost)
        {
            logger.LogInformation(
                "workloads.relocate: instance={InstanceId} refused — the host holds it suspended; only the host resumes it.",
                instance.Id);
            return RelocateResult.NotMutable();
        }

        var providerAnswer = await inputsProvider.GetInputsAsync(instance, ct).ConfigureAwait(false);
        if (providerAnswer.Refusal is { } providerRefusal)
        {
            return Refused(instance, providerRefusal);
        }

        if (providerAnswer.Inputs is { } providerInputs && providerInputs.EffectiveProvider.Id != KubernetesProvider.Id)
        {
            logger.LogWarning(
                "workloads.relocate: instance={InstanceId} effective provider is {Provider}, not Kubernetes — relocate is Kubernetes-only; refused, nothing mutated.",
                instance.Id, providerInputs.EffectiveProvider.Label);
            return RelocateResult.NotSupportedByProvider();
        }

        var (plan, planRefusal) = await BuildPlanAsync(instance, newNodeSelector, ct).ConfigureAwait(false);
        if (planRefusal is not null)
        {
            return Refused(instance, planRefusal);
        }

        if (plan is null)
        {
            return RelocateResult.NotFound();
        }

        if (!plan.SelectorRepresentable)
        {
            logger.LogWarning(
                "workloads.relocate: instance={InstanceId} declares nodeSelector params that cannot express the requested placement {Selector} — relocation aborted, nothing mutated.",
                instance.Id,
                string.Join(",", newNodeSelector.Select(kv => $"{kv.Key}={kv.Value}")));
            return RelocateResult.SelectorNotRepresentable(plan);
        }

        if (plan.HasStrandedAdoptedClaim)
        {
            logger.LogWarning(
                "workloads.relocate: instance={InstanceId} restores retained volume(s) [{Volumes}] bound to a node the requested placement {Selector} would move off — relocation refused (uninstall+reinstall to move it). Nothing mutated.",
                instance.Id,
                string.Join(",", plan.AdoptedVolumesBlockingRelocate.Select(v => v.VolumeId)),
                string.Join(",", newNodeSelector.Select(kv => $"{kv.Key}={kv.Value}")));
            return RelocateResult.AdoptedClaimNotRelocatable(plan);
        }

        var answer = await inputsProvider.GetInputsAsync(instance, ct).ConfigureAwait(false);
        if (answer.Refusal is { } refusal)
        {
            return Refused(instance, refusal);
        }

        if (answer.Inputs is not { } inputs)
        {
            return RelocateResult.NotFound();
        }

        var now = clock.UtcNow;

        var parameters = instance.GetResolvedParameters();
        var updatedParameters = WorkloadNodeSelectorParameters.ApplyNodeSelector(
            inputs.WorkloadManifest.Parameters ?? [],
            parameters,
            newNodeSelector);
        instance.SetResolvedParameters(updatedParameters, now);

        instance.SetAppliedSpecHash(null, now);
        instance.BeginOperatorRedeploy("Relocating to a new node by operator request.", now);

        await repository.UpdateAsync(instance, ct).ConfigureAwait(false);

        using var nudgeCts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        try
        {
            await reconcileNudge.NudgeInstanceAsync(instance.Id, "workload-relocate", nudgeCts.Token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex,
                "workloads.relocate: instance={InstanceId} reconcile nudge failed after placement commit.", instance.Id);
        }

        if (plan.NodeBoundVolumes.Count > 0)
        {
            logger.LogWarning(
                "workloads.relocate: instance={InstanceId} moved to selector={Selector} away from node-bound volume(s) [{Volumes}]; they were left in place and the pod will not schedule until the operator moves that data.",
                instance.Id,
                string.Join(",", newNodeSelector.Select(kv => $"{kv.Key}={kv.Value}")),
                string.Join(",", plan.NodeBoundVolumes.Select(v => $"{v.PvcName}@{v.BoundNode}")));
        }
        else if (plan.UnverifiedVolumes.Count > 0)
        {
            logger.LogWarning(
                "workloads.relocate: instance={InstanceId} moved to selector={Selector}; the node binding of volume(s) [{Volumes}] could not be read, so the pod may not schedule if their data is pinned to another node.",
                instance.Id,
                string.Join(",", newNodeSelector.Select(kv => $"{kv.Key}={kv.Value}")),
                string.Join(",", plan.UnverifiedVolumes.Select(v => v.PvcName)));
        }
        else
        {
            logger.LogInformation(
                "workloads.relocate: instance={InstanceId} moved to selector={Selector}.",
                instance.Id,
                string.Join(",", newNodeSelector.Select(kv => $"{kv.Key}={kv.Value}")));
        }

        return RelocateResult.Relocated(plan);
    }

    private RelocateResult Refused(WorkloadInstance instance, string refusal)
    {
        logger.LogWarning(
            "workloads.relocate: instance={InstanceId} refused — the host refused its bootstrap: {Refusal}. Nothing "
            + "mutated.",
            instance.Id, refusal);
        return RelocateResult.HostRefused(refusal);
    }

    private async Task<(RelocatePlan? Plan, string? Refusal)> BuildPlanAsync(
        WorkloadInstance instance, IReadOnlyDictionary<string, string> newNodeSelector, CancellationToken ct)
    {
        var answer = await inputsProvider.GetInputsAsync(instance, ct).ConfigureAwait(false);
        if (answer.Refusal is { } refusal)
        {
            return (null, refusal);
        }

        if (answer.Inputs is not { } inputs)
        {
            return (null, null);
        }

        return (await PlanFromAsync(instance, inputs, newNodeSelector, ct).ConfigureAwait(false), null);
    }

    private async Task<RelocatePlan?> PlanFromAsync(
        WorkloadInstance instance,
        WorkloadReconcileInputs inputs,
        IReadOnlyDictionary<string, string> newNodeSelector,
        CancellationToken ct)
    {
        if (inputs.EffectiveProvider.Id != KubernetesProvider.Id)
        {
            logger.LogWarning(
                "workloads.relocate: instance={InstanceId} effective provider is {Provider}, not Kubernetes — relocate is a Kubernetes-only flow; no plan built, nothing mutated.",
                instance.Id, inputs.EffectiveProvider.Label);
            return null;
        }

        var selectorRepresentable = WorkloadNodeSelectorParameters.CanRepresent(
            inputs.WorkloadManifest.Parameters ?? [], newNodeSelector);

        var spec = ComputeDesiredSpec(instance, inputs);
        var facts = new List<RelocateVolumeFact>(spec.Volumes.Count);
        var ns = namespaceResolver.Resolve(inputs.Settings);
        var client = spec.Volumes.Count == 0 || ns is null ? null : await TryCreateClientAsync(instance, ct).ConfigureAwait(false);
        var descriptions = inputs.WorkloadManifest.Volumes
            .GroupBy(v => v.Id, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First().Description, StringComparer.Ordinal);
        if (client is null)
        {
            facts.AddRange(spec.Volumes.Select(vol => new RelocateVolumeFact
            {
                VolumeId = vol.Id,
                Criticality = vol.Criticality,
                Description = descriptions.TryGetValue(vol.Id, out var d) ? d : null,
                PvcName = vol.ResourceName,
                IsAdoptedClaim = vol.ExistingClaim,
                BindingUnknown = true,
            }));
        }
        else
        {
            var inspection = await clusterInspector.InspectAsync(client, ns!, ct).ConfigureAwait(false);
            var nodeLabels = inspection.Nodes
                .GroupBy(n => n.Labels.TryGetValue(KubernetesPvcInspector.HostnameLabel, out var host) ? host : n.Name, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.First().Labels, StringComparer.Ordinal);

            foreach (var vol in spec.Volumes)
            {
                var binding = await pvcInspector.ReadPvcBindingAsync(client, ns!, vol.ResourceName, ct).ConfigureAwait(false);
                var boundNode = binding?.BoundNode;
                facts.Add(new RelocateVolumeFact
                {
                    VolumeId = vol.Id,
                    Criticality = vol.Criticality,
                    Description = descriptions.TryGetValue(vol.Id, out var d) ? d : null,
                    PvcName = vol.ResourceName,
                    BoundNode = boundNode,
                    BoundNodeLabels = boundNode is not null && nodeLabels.TryGetValue(boundNode, out var labels) ? labels : null,
                    IsAdoptedClaim = vol.ExistingClaim,
                    BindingUnknown = binding?.BindingUnreadable == true,
                });
            }
        }

        return KubernetesRelocatePlanner.Plan(instance.Id, newNodeSelector, facts)
            with { SelectorRepresentable = selectorRepresentable };
    }

    private async Task<IKubernetes?> TryCreateClientAsync(WorkloadInstance instance, CancellationToken ct)
    {
        try
        {
            return await clientFactory.CreateClientAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex,
                "workloads.relocate: instance={InstanceId} could not build the API client; the plan reports every volume as unverified.",
                instance.Id);
            return null;
        }
    }

    internal static WorkloadDesiredSpec ComputeDesiredSpec(WorkloadInstance instance, WorkloadReconcileInputs inputs)
        => WorkloadDesiredSpecFactory.ComputeWithParameterReferences(
            inputs.WorkloadManifest,
            inputs.Settings,
            inputs.ResolvedParameters,
            instance.ParameterReferencesForSpecHash(inputs.ParameterReferencesForHash),
            inputs.Bootstrap,
            inputs.EffectiveProvider,
            inputs.Namer,
            instance.GroupInstanceId,
            instance.GroupId,
            instance.Id,
            inputs.InstallationId,
            inputs.ProductImages);
}
