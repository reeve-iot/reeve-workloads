using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using k8s;
using k8s.Models;
using Microsoft.Extensions.Logging;
using Reeve.Workloads.Abstractions;
using Reeve.Workloads.Abstractions.Contracts;
using Reeve.Workloads.Abstractions.Domain.DesiredSpec;
using Reeve.Workloads.Abstractions.Domain.Entities;
using Reeve.Workloads.Domain.DesiredSpec;
using Reeve.Workloads.Providers.Kubernetes.Relocate;

namespace Reeve.Workloads.Providers.Kubernetes;

internal sealed class KubernetesWorkloadVolumeStatusReader : IWorkloadVolumeStatusReader
{
    private readonly IWorkloadInstanceRepository repository;
    private readonly IWorkloadReconcileInputsProvider inputsProvider;
    private readonly IKubernetesPvcInspector pvcInspector;
    private readonly IKubernetesClientFactory clientFactory;
    private readonly KubernetesNamespaceResolver namespaceResolver;
    private readonly ILogger<KubernetesWorkloadVolumeStatusReader> logger;

    public KubernetesWorkloadVolumeStatusReader(
        IWorkloadInstanceRepository repository,
        IWorkloadReconcileInputsProvider inputsProvider,
        IKubernetesPvcInspector pvcInspector,
        IKubernetesClientFactory clientFactory,
        KubernetesNamespaceResolver namespaceResolver,
        ILogger<KubernetesWorkloadVolumeStatusReader> logger)
    {
        this.repository = repository;
        this.inputsProvider = inputsProvider;
        this.pvcInspector = pvcInspector;
        this.clientFactory = clientFactory;
        this.namespaceResolver = namespaceResolver;
        this.logger = logger;
    }

    public async Task<WorkloadVolumeStatusResult?> ReadVolumeStatusAsync(Guid instanceId, CancellationToken cancellationToken = default)
    {
        var instance = await repository.GetByIdAsync(instanceId, cancellationToken).ConfigureAwait(false);
        if (instance is null)
        {
            return null;
        }

        var answer = await inputsProvider.GetInputsAsync(instance, cancellationToken).ConfigureAwait(false);
        if (answer.Refusal is { } refusal)
        {
            return WorkloadVolumeStatusResult.NotApplicable(refusal);
        }

        if (answer.Inputs is not { } inputs)
        {
            return WorkloadVolumeStatusResult.NotApplicable("The workload is no longer declared by its plugin.");
        }

        if (inputs.EffectiveProvider.Id != KubernetesProvider.Id)
        {
            return WorkloadVolumeStatusResult.NotApplicable(
                $"Volume live-status is available for Kubernetes-provider workloads only (the effective provider is {inputs.EffectiveProvider.Label}).");
        }

        var spec = ComputeDesiredSpec(instance, inputs);
        if (spec.Volumes.Count == 0)
        {
            return WorkloadVolumeStatusResult.Applicable([]);
        }

        var ns = namespaceResolver.Resolve(inputs.Settings);

        IKubernetes? client = null;
        try
        {
            client = ns is null ? null : await clientFactory.CreateClientAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex,
                "workloads.volume-status: instance={InstanceId} could not build the API client; reporting every volume's resize state as Unknown (honest degrade, never a false no-expansion).",
                instance.Id);
            client = null;
        }

        var volumes = new List<WorkloadVolumeStatus>(spec.Volumes.Count);
        foreach (var vol in spec.Volumes)
        {
            var live = client is null
                ? null
                : await pvcInspector.ReadPvcLiveStatusAsync(client, ns!, vol.ResourceName, cancellationToken).ConfigureAwait(false);

            volumes.Add(MapVolume(vol, live));
        }

        return WorkloadVolumeStatusResult.Applicable(volumes);
    }

    private static WorkloadVolumeStatus MapVolume(ResolvedVolume vol, PvcLiveStatus? live)
    {
        var desired = vol.Size;
        var appliedRequested = live?.RequestedSize;
        var liveState = live?.ResizeState ?? WorkloadVolumeResizeState.Unknown;

        var unapplied = liveState != WorkloadVolumeResizeState.Unknown
            && appliedRequested is not null
            && IsStrictlyLarger(desired, appliedRequested);

        return new()
        {
            VolumeId = vol.Id,
            MountPath = vol.MountPath,
            RequestedSize = unapplied ? desired : (appliedRequested ?? vol.Size),
            ActualCapacity = live?.ActualCapacity,
            StorageClassName = live?.StorageClassName ?? vol.StorageClassName,
            ResizeState = unapplied ? WorkloadVolumeResizeState.ResizeRequested : liveState,
            ResizeMessage = unapplied
                ? (live?.ResizeMessage
                   ?? "The requested size has not been applied to the volume yet — it is pending, or the StorageClass may not allow expansion (allowVolumeExpansion=false).")
                : live?.ResizeMessage,
        };
    }

    private static bool IsStrictlyLarger(string? desired, string? applied)
        => TryParseStorageBytes(desired, out var d) && TryParseStorageBytes(applied, out var a) && d > a;

    private static bool TryParseStorageBytes(string? size, out long bytes)
    {
        bytes = 0;
        if (string.IsNullOrWhiteSpace(size))
        {
            return false;
        }

        try
        {
            var value = new ResourceQuantity(size.Trim()).ToInt64();
            if (value <= 0)
            {
                return false;
            }

            bytes = value;
            return true;
        }
        catch (Exception)
        {
            return false;
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
