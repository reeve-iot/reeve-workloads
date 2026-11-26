using Reeve.Workloads.Abstractions.Contracts;
using Reeve.Workloads.Abstractions.Domain;
using Reeve.Workloads.Abstractions.Domain.Entities;
using Reeve.Workloads.Abstractions.Providers;

namespace Acme.Workloads.Nomad;

public sealed class NomadResourceNamer : IWorkloadResourceNamer
{
    public static RetainedVolumeSchemeId VolumeSchemeId { get; } = RetainedVolumeSchemeId.Parse("acme.nomad");

    public WorkloadProviderId Provider => NomadProvider.Id;

    public RetainedVolumeSchemeId VolumeScheme => VolumeSchemeId;

    public string ObjectName(WorkloadNamingSubject subject)
        => $"nomad-{Slug(subject.GroupId)}-{Slug(subject.WorkloadId)}-{Slug(subject.Inst8)}";

    public string SecretName(WorkloadNamingSubject subject) => $"nomad/{ObjectName(subject)}";

    public string VolumeName(WorkloadNamingSubject subject, string volumeId)
        => $"nomad-{Slug(subject.GroupId)}-{Slug(volumeId)}-{Slug(subject.Inst8)}";

    public IReadOnlyDictionary<string, string> Labels(WorkloadLabelSubject subject) => new Dictionary<string, string>
    {
        [WorkloadResourceLabels.InstanceLabelKey] = subject.InstanceId.ToString("D"),
        [WorkloadResourceLabels.InstallationLabelKey] = subject.InstallationId,
        [WorkloadResourceLabels.SpecHashLabelKey] = subject.SpecHash,
    };

    public IReadOnlyDictionary<string, string> InstanceSelectorLabels(Guid instanceId)
        => new Dictionary<string, string> { [WorkloadResourceLabels.InstanceLabelKey] = instanceId.ToString("D") };

    private static string Slug(string value)
        => new(value.ToLowerInvariant().Select(c => char.IsAsciiLetterOrDigit(c) ? c : '-').ToArray());
}
