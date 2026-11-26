using Reeve.Workloads.Abstractions.Contracts;
using Reeve.Workloads.Abstractions.Domain.Entities;
using Reeve.Workloads.Abstractions.Providers;
using Reeve.Workloads.Domain;

namespace Reeve.Workloads.Providers.Docker;

/// <summary>How the docker arm names what it creates. It disagrees with the default namer about exactly one
/// name — the volume — and about the label key that identifies an instance.</summary>
/// <remarks>
/// <para>
/// A docker named volume is <c>reeve-{pluginTypeId}-{volumeId}-{inst8}</c>: no <c>-wl-</c> segment and no
/// workload id, which is not a style choice but the name volumes on operators' machines already carry. The
/// scheme is recorded on every volume this arm retains, so a restore years later resolves the physical name
/// the way the namer that created it would have. The scheme itself is
/// <see cref="WorkloadResourceNaming.DockerSchemeVolumeName"/>, which the manual provider's namer shares.
/// </para>
/// <para>
/// The container NAME is deliberately the default scheme, byte for byte: because containers on real hosts carry those names.
/// </para>
/// </remarks>
public sealed class DockerResourceNamer : IWorkloadResourceNamer
{
    /// <summary>The one instance. Stateless, and the renderers reach for it directly rather than taking a
    /// registry to ask for the namer of the provider they already are.</summary>
    public static DockerResourceNamer Instance { get; } = new();

    /// <summary>Docker instance-identity label key. Docker forbids <c>/</c> before the domain, so the k8s
    /// <c>reeve.io/workload-instance</c> spelling is not available and the reverse-DNS one is used instead.</summary>
    public const string InstanceLabelKey = "io.reeve.workload-instance";

    /// <inheritdoc />
    public WorkloadProviderId Provider => DockerProvider.Id;

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
    /// <remarks>The DEFAULT set, <c>reeve.io/*</c> and all — which looks wrong for a docker arm and is not. These
    /// labels land on <c>WorkloadDesiredSpec.Labels</c>, and this arm does not stamp that set on its containers:
    /// it builds its own <c>io.reeve.*</c> set in <c>DockerProviderConstants.BuildResourceLabels</c> and reads
    /// the installation id back OUT of the spec's set. Answering anything else here would silently drop the
    /// installation label from every container this arm creates.</remarks>
    public IReadOnlyDictionary<string, string> Labels(WorkloadLabelSubject subject)
        => DefaultWorkloadResourceNamer.Instance.Labels(subject);

    /// <inheritdoc />
    public IReadOnlyDictionary<string, string> InstanceSelectorLabels(Guid instanceId)
        => new Dictionary<string, string> { [InstanceLabelKey] = instanceId.ToString("D") };
}
