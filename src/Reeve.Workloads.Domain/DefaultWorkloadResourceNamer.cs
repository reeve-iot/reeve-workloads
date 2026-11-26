using Reeve.Workloads.Abstractions.Contracts;
using Reeve.Workloads.Abstractions.Domain;
using Reeve.Workloads.Abstractions.Domain.Entities;
using Reeve.Workloads.Abstractions.Providers;

namespace Reeve.Workloads.Domain;

/// <summary>The namer every provider gets unless it ships one of its own: RFC 1123 object names under the
/// <c>reeve-wl-</c> prefix, one claim-shaped name per volume, and the <c>reeve-iot.dev/*</c> label set.</summary>
/// <remarks>
/// <para>
/// The names are persisted and address live objects, so the output is byte-stable.
/// </para>
/// <para>
/// It is the default rather than the Kubernetes arm's own namer because it is not only Kubernetes': a MANUAL
/// install names its objects and secrets with it (its volumes take the Docker scheme), and a third-party provider
/// that deploys anything claim-shaped wants it too. Making each of those ship a copy of the same scheme
/// would be three places for the same persisted name to drift apart, which is the one failure this whole seam
/// exists to prevent.
/// </para>
/// </remarks>
public sealed class DefaultWorkloadResourceNamer : IWorkloadResourceNamer
{
    /// <summary>The one instance. Stateless, and resolved on every spec computation.</summary>
    public static DefaultWorkloadResourceNamer Instance { get; } = new();

    /// <inheritdoc />
    /// <remarks>Empty on purpose: this namer belongs to no provider and answers for every provider that
    /// registers none.</remarks>
    public WorkloadProviderId Provider => default;

    /// <inheritdoc />
    public RetainedVolumeSchemeId VolumeScheme => RetainedVolumeSchemeId.Kubernetes;

    /// <inheritdoc />
    public string ObjectName(WorkloadNamingSubject subject)
        => WorkloadResourceNaming.ComposeObjectName(
            WorkloadResourceNaming.Sanitize(subject.GroupId),
            WorkloadResourceNaming.Sanitize(subject.WorkloadId),
            WorkloadResourceNaming.NormalizeInst8(subject.Inst8));

    /// <inheritdoc />
    /// <remarks>The same scheme as the object name, on purpose.</remarks>
    public string SecretName(WorkloadNamingSubject subject) => ObjectName(subject);

    /// <inheritdoc />
    public string VolumeName(WorkloadNamingSubject subject, string volumeId)
        => WorkloadResourceNaming.ComposeObjectName(
            WorkloadResourceNaming.Sanitize(subject.GroupId),
            WorkloadResourceNaming.Sanitize(subject.WorkloadId),
            WorkloadResourceNaming.Sanitize(volumeId),
            WorkloadResourceNaming.NormalizeInst8(subject.Inst8));

    /// <inheritdoc />
    public IReadOnlyDictionary<string, string> Labels(WorkloadLabelSubject subject)
    {
        var labels = new Dictionary<string, string>
        {
            [WorkloadResourceLabels.InstanceLabelKey] = subject.InstanceId.ToString("D"),
            [WorkloadResourceLabels.InstallationLabelKey] = WorkloadResourceNaming.Sanitize(subject.InstallationId),
            [WorkloadResourceLabels.SpecHashLabelKey] =
                WorkloadResourceNaming.TruncateForDns(WorkloadResourceNaming.Sanitize(subject.SpecHash)),
        };

        if (!string.IsNullOrWhiteSpace(subject.Criticality))
        {
            labels[WorkloadResourceLabels.CriticalityLabelKey] = WorkloadResourceNaming.Sanitize(subject.Criticality);
        }

        return labels;
    }

    /// <inheritdoc />
    public IReadOnlyDictionary<string, string> InstanceSelectorLabels(Guid instanceId)
        => new Dictionary<string, string> { [WorkloadResourceLabels.InstanceLabelKey] = instanceId.ToString("D") };
}
