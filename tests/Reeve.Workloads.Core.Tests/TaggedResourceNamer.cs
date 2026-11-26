using Reeve.Workloads.Abstractions.Contracts;
using Reeve.Workloads.Abstractions.Domain.Entities;
using Reeve.Workloads.Abstractions.Providers;
using Reeve.Workloads.Domain;

namespace Reeve.Workloads.Core.Tests;

internal sealed class TaggedResourceNamer(WorkloadProviderId provider, string tag) : IWorkloadResourceNamer
{
    public WorkloadProviderId Provider => provider;

    public RetainedVolumeSchemeId VolumeScheme => RetainedVolumeSchemeId.Parse(tag);

    public string ObjectName(WorkloadNamingSubject subject) => $"{tag}-obj-{subject.WorkloadId}-{subject.Inst8}";

    public string SecretName(WorkloadNamingSubject subject)
        => $"{tag}-secret-{subject.GroupId}-{subject.WorkloadId}-{subject.Inst8}";

    public string VolumeName(WorkloadNamingSubject subject, string volumeId)
        => $"{tag}-vol-{subject.WorkloadId}-{volumeId}-{subject.Inst8}";

    public IReadOnlyDictionary<string, string> Labels(WorkloadLabelSubject subject)
        => DefaultWorkloadResourceNamer.Instance.Labels(subject);

    public IReadOnlyDictionary<string, string> InstanceSelectorLabels(Guid instanceId)
        => DefaultWorkloadResourceNamer.Instance.InstanceSelectorLabels(instanceId);
}
