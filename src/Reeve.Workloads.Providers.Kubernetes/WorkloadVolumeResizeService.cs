using k8s.Models;
using Microsoft.Extensions.Logging;
using Reeve.Workloads.Abstractions;
using Reeve.Workloads.Abstractions.Contracts;
using Reeve.Workloads.Abstractions.Contracts.Deployment;
using Reeve.Workloads.Abstractions.Domain;
using Reeve.Workloads.Abstractions.Domain.DesiredSpec;
using Reeve.Workloads.Abstractions.Domain.Entities;
using Reeve.Workloads.Abstractions.Providers;
using Reeve.Workloads.Core;
using Reeve.Workloads.Domain.DesiredSpec;

namespace Reeve.Workloads.Providers.Kubernetes;

internal sealed class WorkloadVolumeResizeService : IWorkloadVolumeResizeService
{
    private const long MaxRequestedBytes = 64L * 1024 * 1024 * 1024 * 1024;

    private readonly IWorkloadInstanceRepository repository;
    private readonly IActiveWorkloadIntentProvider intentProvider;
    private readonly IWorkloadReconcileInputsProvider inputsProvider;
    private readonly IWorkloadReconcileNudge reconcileNudge;
    private readonly IWorkloadClock clock;
    private readonly ILogger<WorkloadVolumeResizeService> logger;
    private readonly IWorkloadProviderRegistry providerRegistry;

    public WorkloadVolumeResizeService(
        IWorkloadInstanceRepository repository,
        IActiveWorkloadIntentProvider intentProvider,
        IWorkloadReconcileInputsProvider inputsProvider,
        IWorkloadReconcileNudge reconcileNudge,
        IWorkloadClock clock,
        ILogger<WorkloadVolumeResizeService> logger,
        IWorkloadProviderRegistry providerRegistry)
    {
        this.repository = repository;
        this.intentProvider = intentProvider;
        this.inputsProvider = inputsProvider;
        this.reconcileNudge = reconcileNudge ?? throw new ArgumentNullException(nameof(reconcileNudge));
        this.clock = clock;
        this.logger = logger;
        this.providerRegistry = providerRegistry ?? throw new ArgumentNullException(nameof(providerRegistry));
    }

    public async Task<WorkloadVolumeResizeResult> RequestResizeAsync(
        Guid workloadInstanceId,
        string volumeId,
        string newSize,
        Guid? actorUserId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(volumeId);

        var instance = await repository.GetByIdAsync(workloadInstanceId, cancellationToken).ConfigureAwait(false);
        if (instance is null)
        {
            return WorkloadVolumeResizeResult.NotFound();
        }

        if (instance.IsOwnershipReleased(providerRegistry) && !instance.IsHeldByHost)
        {
            return WorkloadVolumeResizeResult.Rejected(
                $"Reeve released ownership of the workload instance ({instance.State}); its volumes cannot be resized. Re-adopt " +
                "the instance first.");
        }

        if (!instance.CanMutate)
        {
            return WorkloadVolumeResizeResult.Rejected(
                $"The workload instance is {instance.State}; its volumes cannot be resized.");
        }

        if (instance.IsHeldByHost)
        {
            return WorkloadVolumeResizeResult.Rejected(
                "The host holds the workload instance suspended; its volumes cannot be resized until the host resumes it.");
        }

        var answer = await inputsProvider.GetInputsAsync(instance, cancellationToken).ConfigureAwait(false);
        if (answer.Refusal is { } refusal)
        {
            return WorkloadVolumeResizeResult.HostRefused(refusal);
        }

        if (answer.Inputs is not { } inputs)
        {
            return WorkloadVolumeResizeResult.NotFound();
        }

        if (inputs.EffectiveProvider.Id != KubernetesProvider.Id)
        {
            return WorkloadVolumeResizeResult.NotApplicable(
                $"Volume resize is available for Kubernetes-provider workloads only (the effective provider is {inputs.EffectiveProvider.Label}).");
        }

        var declared = FindVolume(inputs.WorkloadManifest, volumeId);
        if (declared is null)
        {
            return WorkloadVolumeResizeResult.VolumeNotFound(
                $"The workload does not declare a volume with id '{volumeId}'.");
        }

        var activeIntentId = await intentProvider
            .GetActiveIntentIdAsync(instance.GroupInstanceId, cancellationToken)
            .ConfigureAwait(false);
        if (activeIntentId is { } intentId)
        {
            return WorkloadVolumeResizeResult.Conflict(intentId);
        }

        if (!TryParseStorageBytes(newSize, out var requestedBytes, out var normalizedNewSize))
        {
            return WorkloadVolumeResizeResult.Rejected(
                $"'{newSize}' is not a valid storage size. Use a Kubernetes quantity such as '10Gi' or '512Mi'.");
        }
        if (requestedBytes > MaxRequestedBytes)
        {
            return WorkloadVolumeResizeResult.Rejected(
                $"The requested size '{normalizedNewSize}' exceeds the maximum single-volume size (64Ti).");
        }

        var stored = instance.GetResolvedParameters();
        var overrideKey = $"{WorkloadDesiredSpecFactory.VolumeSizeOverrideParameterPrefix}{declared.Id}";
        var currentSize = stored.TryGetValue(overrideKey, out var currentOverride) && !string.IsNullOrWhiteSpace(currentOverride)
            ? currentOverride!.Trim()
            : declared.Size;

        var currentBytes = TryParseStorageBytes(currentSize, out var cb, out _) ? cb : 0L;

        if (requestedBytes < currentBytes)
        {
            return WorkloadVolumeResizeResult.Rejected(
                $"A volume can only be resized UP. The requested size '{normalizedNewSize}' is smaller than the current requested size '{currentSize}'.");
        }
        if (requestedBytes == currentBytes)
        {
            return WorkloadVolumeResizeResult.NoChange(currentSize);
        }

        var now = clock.UtcNow;
        var merged = new Dictionary<string, string?>(stored, StringComparer.Ordinal)
        {
            [overrideKey] = normalizedNewSize,
        };
        instance.SetResolvedParameters(merged, now);
        instance.SetAppliedSpecHash(null, now);

        instance.BeginOperatorRedeploy($"Applying operator volume resize ({declared.Id} → {normalizedNewSize}).", now);

        await repository.UpdateAsync(instance, cancellationToken).ConfigureAwait(false);
        await NudgeReconcileAsync(instance.Id, cancellationToken).ConfigureAwait(false);

        logger.LogInformation(
            "workloads.volume_resize: instance={InstanceId} volume={VolumeId} actor={ActorUserId} requested {From} → {To}; desired override recorded, reconcile nudged.",
            instance.Id, declared.Id, actorUserId, currentSize, normalizedNewSize);

        return WorkloadVolumeResizeResult.Success(normalizedNewSize);
    }

    private static WorkloadVolume? FindVolume(WorkloadManifest manifest, string volumeId)
    {
        foreach (var v in manifest.Volumes)
        {
            if (string.Equals(v.Id, volumeId, StringComparison.Ordinal))
            {
                return v;
            }
        }

        return null;
    }

    private static bool TryParseStorageBytes(string? size, out long bytes, out string normalized)
    {
        bytes = 0;
        normalized = string.Empty;
        if (string.IsNullOrWhiteSpace(size))
        {
            return false;
        }

        try
        {
            var quantity = new ResourceQuantity(size.Trim());
            var value = quantity.ToInt64();
            if (value <= 0)
            {
                return false;
            }

            bytes = value;
            normalized = quantity.ToString() ?? size.Trim();
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private Task NudgeReconcileAsync(Guid instanceId, CancellationToken cancellationToken)
        => reconcileNudge.NudgeInstanceAsync(instanceId, "workload-volume-resize", cancellationToken);
}
