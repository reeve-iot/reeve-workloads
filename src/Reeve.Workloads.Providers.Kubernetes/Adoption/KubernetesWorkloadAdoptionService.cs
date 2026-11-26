using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using k8s;
using Microsoft.Extensions.Logging;
using Reeve.Workloads.Abstractions;
using Reeve.Workloads.Abstractions.Domain;
using Reeve.Workloads.Core.Adoption;
using Reeve.Workloads.Providers.Kubernetes;

namespace Reeve.Workloads.Providers.Kubernetes.Adoption;

internal sealed class KubernetesWorkloadAdoptionService : IWorkloadAdoptionService
{
    private const string FieldManager = KubernetesWorkloadProvider.FieldManager;

    private const string ForceAdoptAuditAction = "workloads.adoption.force-adopted";

    private const string ForceAdoptAuditEntityType = "workload-instance";

    private readonly IKubernetesClientFactory _clientFactory;
    private readonly IKubernetesObjectClient _objectClient;
    private readonly IWorkloadOrchestratorSettingsService _settingsService;
    private readonly IWorkloadInstallationIdentity _installationIdentity;
    private readonly ILogger<KubernetesWorkloadAdoptionService> _logger;
    private readonly IWorkloadAuditSink _auditSink;
    private readonly IWorkloadActorAccessor _actor;
    private readonly KubernetesNamespaceResolver _namespaceResolver;

    public KubernetesWorkloadAdoptionService(
        IKubernetesClientFactory clientFactory,
        IKubernetesObjectClient objectClient,
        IWorkloadOrchestratorSettingsService settingsService,
        IWorkloadInstallationIdentity installationIdentity,
        ILogger<KubernetesWorkloadAdoptionService> logger,
        IWorkloadAuditSink auditSink,
        IWorkloadActorAccessor actor,
        KubernetesNamespaceResolver namespaceResolver)
    {
        _clientFactory = clientFactory ?? throw new ArgumentNullException(nameof(clientFactory));
        _objectClient = objectClient ?? throw new ArgumentNullException(nameof(objectClient));
        _settingsService = settingsService ?? throw new ArgumentNullException(nameof(settingsService));
        _installationIdentity = installationIdentity ?? throw new ArgumentNullException(nameof(installationIdentity));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _auditSink = auditSink;
        _actor = actor;
        _namespaceResolver = namespaceResolver ?? throw new ArgumentNullException(nameof(namespaceResolver));
    }

    public async Task<IReadOnlyList<AdoptionCandidate>> DetectAsync(CancellationToken cancellationToken = default)
    {
        var ns = await ResolveNamespaceAsync(cancellationToken).ConfigureAwait(false);
        if (ns is null)
        {
            return Array.Empty<AdoptionCandidate>();
        }

        var thisInstallation = await _installationIdentity.GetInstallationIdAsync(cancellationToken).ConfigureAwait(false);

        IKubernetes client;
        try
        {
            client = await _clientFactory.CreateClientAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Adoption detection could not build the API client; offering no candidates.");
            return Array.Empty<AdoptionCandidate>();
        }

        var adoptable = await _objectClient.ListAdoptableAsync(client, ns, cancellationToken).ConfigureAwait(false);

        var groups = adoptable
            .Where(r => !string.IsNullOrEmpty(r.InstanceLabel))
            .GroupBy(r => r.InstanceLabel, StringComparer.Ordinal);

        var candidates = new List<AdoptionCandidate>();
        foreach (var group in groups)
        {
            var resources = group.ToList();
            if (!TryClassify(resources, thisInstallation, out var adoptionClass, out var installationLabel))
            {
                continue;
            }

            candidates.Add(new AdoptionCandidate
            {
                InstanceLabel = group.Key,
                Class = adoptionClass,
                InstallationLabel = installationLabel,
                Resources = resources,
            });
        }

        return candidates;
    }

    public Task<AdoptionEvidenceResult> VerifyEvidenceAsync(
        AdoptionCandidate candidate, AdoptionEvidence expected, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(expected);

        return Task.FromResult(VerifyEvidence(candidate, expected));
    }

    public async Task<AdoptionResult> AdoptAsync(
        AdoptionCandidate candidate, AdoptionEvidence expected, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(expected);

        var evidence = VerifyEvidence(candidate, expected);
        if (!evidence.Verified)
        {
            return new AdoptionResult
            {
                Outcome = AdoptionOutcome.EvidenceNotVerified,
                Failures = evidence.Failures,
                Message = "Ownership evidence did not verify — adoption refused.",
            };
        }

        var ns = await ResolveNamespaceAsync(cancellationToken).ConfigureAwait(false);
        if (ns is null)
        {
            return new AdoptionResult
            {
                Outcome = AdoptionOutcome.ProviderUnreachable,
                Message = "Workloads namespace is not configured.",
            };
        }

        var thisInstallation = await _installationIdentity.GetInstallationIdAsync(cancellationToken).ConfigureAwait(false);

        IKubernetes client;
        try
        {
            client = await _clientFactory.CreateClientAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Adoption of instance {Instance} could not build the API client.", candidate.InstanceLabel);
            return new AdoptionResult
            {
                Outcome = AdoptionOutcome.ProviderUnreachable,
                Message = "The Kubernetes API client could not be built; retry.",
            };
        }

        var adoptedPvcs = new List<string>();
        var adoptedObjects = new List<string>();

        try
        {
            foreach (var pvc in candidate.Resources.Where(r => r.IsPersistentVolumeClaim))
            {
                var (labelsToSet, labelsToRemove, annotationsToRemove) =
                    BuildSurgery(pvc, candidate.InstanceLabel, thisInstallation);

                await _objectClient.PatchResourceMetadataAsync(
                    client, ns, KubernetesObjectKind.PersistentVolumeClaim, pvc.Name,
                    labelsToSet, labelsToRemove, annotationsToRemove, cancellationToken).ConfigureAwait(false);

                adoptedPvcs.Add(pvc.Name);
            }

            foreach (var obj in candidate.Resources.Where(r => !r.IsPersistentVolumeClaim))
            {
                var liveBody = await _objectClient.ReadObjectManifestAsync(
                    client, ns, obj.Kind.ToKubernetesKind(), obj.Name, cancellationToken).ConfigureAwait(false);

                if (liveBody is null)
                {
                    return new AdoptionResult
                    {
                        Outcome = AdoptionOutcome.ProviderUnreachable,
                        AdoptedPvcNames = adoptedPvcs,
                        AdoptedObjectNames = adoptedObjects,
                        Message = $"Could not read the live body of {obj.Kind} {obj.Name} to take ownership; retry.",
                    };
                }

                var manifestJson = BuildOwnershipManifest(liveBody, candidate.InstanceLabel, thisInstallation);

                var outcome = await _objectClient.ServerSideApplyAsync(
                    client, ns, obj.Kind.ToKubernetesKind(), obj.Name, manifestJson, FieldManager, force: true, cancellationToken)
                    .ConfigureAwait(false);

                if (outcome.Unreachable)
                {
                    return new AdoptionResult
                    {
                        Outcome = AdoptionOutcome.ProviderUnreachable,
                        AdoptedPvcNames = adoptedPvcs,
                        AdoptedObjectNames = adoptedObjects,
                        Message = outcome.Error ?? "The API server was unreachable during adoption.",
                    };
                }

                if (!outcome.Succeeded)
                {
                    return new AdoptionResult
                    {
                        Outcome = AdoptionOutcome.Error,
                        AdoptedPvcNames = adoptedPvcs,
                        AdoptedObjectNames = adoptedObjects,
                        Message = outcome.Error ?? $"Force-adopting {obj.Kind} {obj.Name} failed.",
                    };
                }

                adoptedObjects.Add(obj.Name);
            }

            var adopted = new AdoptionResult
            {
                Outcome = AdoptionOutcome.Adopted,
                AdoptedPvcNames = adoptedPvcs,
                AdoptedObjectNames = adoptedObjects,
            };

            await WriteForceAdoptAuditAsync(candidate, adopted).ConfigureAwait(false);

            return adopted;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Adoption of instance {Instance} failed unexpectedly.", candidate.InstanceLabel);
            return new AdoptionResult
            {
                Outcome = AdoptionOutcome.Error,
                AdoptedPvcNames = adoptedPvcs,
                AdoptedObjectNames = adoptedObjects,
                Message = ex.Message,
            };
        }
    }

    private async Task WriteForceAdoptAuditAsync(AdoptionCandidate candidate, AdoptionResult result)
    {
        var entityId = Guid.TryParse(candidate.InstanceLabel, out var instanceId) ? instanceId : (Guid?)null;

        try
        {
            await _auditSink.WriteAsync(
                new WorkloadAuditEntry(
                    ActorUserId: _actor.ActorUserId,
                    Action: ForceAdoptAuditAction,
                    EntityType: ForceAdoptAuditEntityType,
                    EntityId: entityId,
                    Metadata: new
                    {
                        instanceLabel = candidate.InstanceLabel,
                        adoptionClass = candidate.Class.ToString(),
                        installationLabel = candidate.InstallationLabel,
                        adoptedObjectNames = result.AdoptedObjectNames,
                        adoptedPvcNames = result.AdoptedPvcNames,
                    }),
                CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Force-adopt of instance {Instance} succeeded but the audit entry could not be written.",
                candidate.InstanceLabel);
        }
    }

    private static bool TryClassify(
        IReadOnlyList<AdoptableResource> resources,
        string thisInstallation,
        out AdoptionClass adoptionClass,
        out string? installationLabel)
    {
        adoptionClass = default;
        installationLabel = resources.Select(r => r.InstallationLabel).FirstOrDefault(v => v is not null);

        if (resources.Any(r => r.IsOperatorManaged))
        {
            adoptionClass = AdoptionClass.ManualManaged;
            return true;
        }

        var isOwnInstallation = installationLabel is not null
            && string.Equals(installationLabel, thisInstallation, StringComparison.Ordinal);
        if (isOwnInstallation)
        {
            return false;
        }

        adoptionClass = AdoptionClass.ForeignInstallation;
        return true;
    }

    private static AdoptionEvidenceResult VerifyEvidence(AdoptionCandidate candidate, AdoptionEvidence expected)
    {
        var failures = new List<string>();

        var pinsInstanceLabel =
            expected.ExpectedLabels.TryGetValue(WorkloadResourceLabels.InstanceLabelKey, out var pinnedInstance)
            && string.Equals(pinnedInstance, candidate.InstanceLabel, StringComparison.Ordinal);
        var hasCorroboration =
            !string.IsNullOrEmpty(expected.ExpectedImage) || expected.ExpectedPvcNames.Count > 0;

        if (!pinsInstanceLabel || !hasCorroboration)
        {
            failures.Add(
                "adoption requires ownership evidence: the instance label plus the expected image ref and/or PVC name(s).");
        }

        foreach (var (key, value) in expected.ExpectedLabels)
        {
            foreach (var resource in candidate.Resources)
            {
                if (!resource.Labels.TryGetValue(key, out var actual) || !string.Equals(actual, value, StringComparison.Ordinal))
                {
                    failures.Add($"{resource.Kind} '{resource.Name}': label '{key}' expected '{value}' but was '{actual ?? "<absent>"}'.");
                }
            }
        }

        if (!string.IsNullOrEmpty(expected.ExpectedImage))
        {
            var workloadObjects = candidate.Resources
                .Where(r => r.Kind is AdoptableResourceKind.Deployment or AdoptableResourceKind.DaemonSet)
                .ToList();

            if (workloadObjects.Count == 0)
            {
                failures.Add($"expected image '{expected.ExpectedImage}' but the candidate has no Deployment/DaemonSet to run it.");
            }
            else if (!workloadObjects.Any(r => string.Equals(r.Image, expected.ExpectedImage, StringComparison.Ordinal)))
            {
                var observed = string.Join(", ", workloadObjects.Select(r => r.Image ?? "<none>"));
                failures.Add($"expected image '{expected.ExpectedImage}' but observed '{observed}'.");
            }
        }

        foreach (var pvcName in expected.ExpectedPvcNames)
        {
            if (!candidate.Resources.Any(r => r.IsPersistentVolumeClaim && string.Equals(r.Name, pvcName, StringComparison.Ordinal)))
            {
                failures.Add($"expected PVC '{pvcName}' bound to the candidate but it was not found.");
            }
        }

        return failures.Count == 0 ? AdoptionEvidenceResult.Ok() : AdoptionEvidenceResult.Fail(failures);
    }

    private static (IReadOnlyDictionary<string, string> LabelsToSet, IReadOnlyList<string> LabelsToRemove, IReadOnlyList<string> AnnotationsToRemove)
        BuildSurgery(AdoptableResource pvc, string instanceLabel, string thisInstallation)
    {
        var labelsToRemove = new List<string>();
        var annotationsToRemove = new List<string>();

        if (pvc.Labels.TryGetValue(KubernetesAdoptionLabels.ManagedByLabel, out var managedBy)
            && string.Equals(managedBy, WorkloadAdoptionLabels.HelmManagedByValue, StringComparison.Ordinal))
        {
            labelsToRemove.Add(KubernetesAdoptionLabels.ManagedByLabel);
        }

        if (pvc.Labels.ContainsKey(KubernetesAdoptionLabels.InstanceLabel))
        {
            labelsToRemove.Add(KubernetesAdoptionLabels.InstanceLabel);
        }

        foreach (var key in pvc.Labels.Keys)
        {
            if (key.StartsWith(WorkloadAdoptionLabels.ArgoAnnotationPrefix, StringComparison.Ordinal))
            {
                labelsToRemove.Add(key);
            }
        }

        foreach (var key in pvc.Annotations.Keys)
        {
            if (string.Equals(key, WorkloadAdoptionLabels.HelmReleaseNameAnnotation, StringComparison.Ordinal)
                || string.Equals(key, WorkloadAdoptionLabels.HelmReleaseNamespaceAnnotation, StringComparison.Ordinal)
                || string.Equals(key, WorkloadAdoptionLabels.HelmResourcePolicyAnnotation, StringComparison.Ordinal)
                || key.StartsWith(WorkloadAdoptionLabels.ArgoAnnotationPrefix, StringComparison.Ordinal))
            {
                annotationsToRemove.Add(key);
            }
        }

        var labelsToSet = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [WorkloadResourceLabels.InstanceLabelKey] = instanceLabel,
            [WorkloadResourceLabels.InstallationLabelKey] = thisInstallation,
        };

        if (pvc.Labels.TryGetValue(WorkloadResourceLabels.CriticalityLabelKey, out var criticality)
            && !string.IsNullOrEmpty(criticality))
        {
            labelsToSet[WorkloadResourceLabels.CriticalityLabelKey] = criticality;
        }

        return (labelsToSet, labelsToRemove, annotationsToRemove);
    }

    private static string BuildOwnershipManifest(string liveBodyJson, string instanceLabel, string thisInstallation)
    {
        using var doc = System.Text.Json.JsonDocument.Parse(liveBodyJson);
        var root = doc.RootElement;

        var buffer = new System.IO.MemoryStream();
        using (var writer = new System.Text.Json.Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();

            foreach (var property in root.EnumerateObject())
            {
                if (property.NameEquals("metadata")
                    && property.Value.ValueKind == System.Text.Json.JsonValueKind.Object)
                {
                    writer.WritePropertyName(property.Name);
                    WriteMetadataWithReeveLabels(writer, property.Value, instanceLabel, thisInstallation);
                }
                else
                {
                    property.WriteTo(writer);
                }
            }

            writer.WriteEndObject();
        }

        return System.Text.Encoding.UTF8.GetString(buffer.ToArray());
    }

    private static void WriteMetadataWithReeveLabels(
        System.Text.Json.Utf8JsonWriter writer,
        System.Text.Json.JsonElement metadata,
        string instanceLabel,
        string thisInstallation)
    {
        writer.WriteStartObject();

        var wroteLabels = false;
        foreach (var property in metadata.EnumerateObject())
        {
            if (property.NameEquals("labels")
                && property.Value.ValueKind == System.Text.Json.JsonValueKind.Object)
            {
                writer.WritePropertyName(property.Name);
                WriteLabelsWithReeveIdentity(writer, property.Value, instanceLabel, thisInstallation);
                wroteLabels = true;
            }
            else
            {
                property.WriteTo(writer);
            }
        }

        if (!wroteLabels)
        {
            writer.WritePropertyName("labels");
            WriteLabelsWithReeveIdentity(writer, existing: null, instanceLabel, thisInstallation);
        }

        writer.WriteEndObject();
    }

    private static void WriteLabelsWithReeveIdentity(
        System.Text.Json.Utf8JsonWriter writer,
        System.Text.Json.JsonElement? existing,
        string instanceLabel,
        string thisInstallation)
    {
        writer.WriteStartObject();

        if (existing is { ValueKind: System.Text.Json.JsonValueKind.Object } labels)
        {
            foreach (var label in labels.EnumerateObject())
            {
                if (label.NameEquals(WorkloadResourceLabels.InstanceLabelKey)
                    || label.NameEquals(WorkloadResourceLabels.InstallationLabelKey))
                {
                    continue;
                }

                label.WriteTo(writer);
            }
        }

        writer.WriteString(WorkloadResourceLabels.InstanceLabelKey, instanceLabel);
        writer.WriteString(WorkloadResourceLabels.InstallationLabelKey, thisInstallation);
        writer.WriteEndObject();
    }

    private async Task<string?> ResolveNamespaceAsync(CancellationToken ct)
    {
        var response = await _settingsService.GetAsync(ct).ConfigureAwait(false);
        return _namespaceResolver.Resolve(response.Settings);
    }
}
