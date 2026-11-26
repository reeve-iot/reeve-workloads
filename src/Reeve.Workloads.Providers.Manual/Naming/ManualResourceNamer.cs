using System;
using System.Collections.Generic;
using Reeve.Workloads.Abstractions.Contracts;
using Reeve.Workloads.Abstractions.Domain.Entities;
using Reeve.Workloads.Abstractions.Providers;
using Reeve.Workloads.Domain;

namespace Reeve.Workloads.Providers.Manual;

/// <summary>How a manual install names what the operator deploys: the default namer's object, secret and label
/// names, and the Docker scheme for volumes.</summary>
/// <remarks>
/// A manual install renders Compose and Kubernetes artifacts from one desired spec, and Reeve does not know which
/// one the operator ran. Its volumes therefore have one name in every artifact, the one recorded when they are
/// retained. That name is the Docker scheme: it is what a Compose deployment creates, and it is a valid
/// Kubernetes claim name.
/// </remarks>
internal sealed class ManualResourceNamer : IWorkloadResourceNamer
{
    /// <inheritdoc />
    public WorkloadProviderId Provider => ManualProvider.Id;

    /// <inheritdoc />
    public RetainedVolumeSchemeId VolumeScheme => RetainedVolumeSchemeId.Docker;

    /// <inheritdoc />
    public string ObjectName(WorkloadNamingSubject subject)
        => DefaultWorkloadResourceNamer.Instance.ObjectName(subject);

    /// <inheritdoc />
    public string SecretName(WorkloadNamingSubject subject)
        => DefaultWorkloadResourceNamer.Instance.SecretName(subject);

    /// <inheritdoc />
    public string VolumeName(WorkloadNamingSubject subject, string volumeId)
        => WorkloadResourceNaming.DockerSchemeVolumeName(subject, volumeId);

    /// <inheritdoc />
    public IReadOnlyDictionary<string, string> Labels(WorkloadLabelSubject subject)
        => DefaultWorkloadResourceNamer.Instance.Labels(subject);

    /// <inheritdoc />
    public IReadOnlyDictionary<string, string> InstanceSelectorLabels(Guid instanceId)
        => DefaultWorkloadResourceNamer.Instance.InstanceSelectorLabels(instanceId);
}
