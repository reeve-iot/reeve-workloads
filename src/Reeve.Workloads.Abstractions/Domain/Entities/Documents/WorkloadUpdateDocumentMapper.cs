using System.Text.Json;
using Reeve.Workloads.Abstractions.Contracts;
using Reeve.Workloads.Abstractions.Contracts.Deployment;
using Reeve.Workloads.Abstractions.Domain.DesiredSpec;
using Reeve.Pipeline;
using Reeve.Workloads.Abstractions.Update;

namespace Reeve.Workloads.Abstractions.Domain.Entities.Documents;

internal static partial class WorkloadDocumentMapper
{
    public static WorkloadSpecMapDocument SpecMap(IReadOnlyDictionary<string, WorkloadDesiredSpec> specs) => new()
    {
        Workloads = specs
            .OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => Spec(pair.Key, pair.Value))
            .ToList(),
    };

    public static IReadOnlyDictionary<string, WorkloadDesiredSpec> FromDocument(WorkloadSpecMapDocument document)
    {
        var specs = new Dictionary<string, WorkloadDesiredSpec>(StringComparer.Ordinal);
        foreach (var workload in document.Workloads)
        {
            specs[workload.Key] = FromDocument(workload);
        }

        return specs;
    }

    private static WorkloadSpecDocument Spec(string key, WorkloadDesiredSpec spec) => new()
    {
        Key = key,
        ModuleInstanceId = spec.GroupInstanceId,
        ModuleId = spec.GroupId,
        WorkloadId = spec.WorkloadId,
        InstanceId = spec.InstanceId,
        Inst8 = spec.Inst8,
        Provider = spec.Provider.Value ?? string.Empty,
        ResourceName = spec.ResourceName,
        SecretName = spec.SecretName,
        Labels = Pairs(spec.Labels),
        Image = spec.Image,
        Platforms = [.. spec.Platforms],
        Kind = spec.Kind.ToString(),
        NodeSelector = Pairs(spec.NodeSelector),
        Resources = spec.Resources is { } resources
            ? new SpecResourcesDocument
            {
                RequestsCpu = resources.RequestsCpu,
                RequestsMemory = resources.RequestsMemory,
                LimitsCpu = resources.LimitsCpu,
                LimitsMemory = resources.LimitsMemory,
            }
            : null,
        Network = spec.Network is { } network
            ? new SpecNetworkDocument
            {
                HostNetwork = network.HostNetwork,
                Mode = network.Mode.ToString(),
                Attachments = [.. network.Attachments],
                Mdns = network.Mdns,
                Ipv6Required = network.Ipv6Required,
                Ports = network.Ports
                    .Select(port => new SpecPortDocument
                    {
                        Name = port.Name,
                        Port = port.Port,
                        Protocol = port.Protocol,
                        HostPort = port.HostPort,
                    })
                    .ToList(),
            }
            : null,
        Privileges = spec.Privileges is { } privileges
            ? new SpecPrivilegesDocument
            {
                Capabilities = [.. privileges.Capabilities],
                Privileged = privileges.Privileged,
                DevicePassthroughPrivileged = privileges.DevicePassthroughPrivileged ? true : null,
                RunAsRoot = privileges.RunAsRoot,
                FsGroup = privileges.FsGroup,
                TunDevice = privileges.TunDevice,
                CallsBackendRest = privileges.CallsBackendRest,
                DbusHostPath = privileges.DbusHostPath,
                DevicePaths = [.. privileges.DevicePaths],
                DeviceGroupIds = [.. privileges.DeviceGroupIds],
                JoinsDeviceGroups = privileges.JoinsDeviceGroups,
                HostPaths = privileges.HostPaths
                    .Select(path => new SpecHostPathDocument { HostPath = path.HostPath, MountPath = path.MountPath })
                    .ToList(),
                NodeLocalState = privileges.NodeLocalState is { } state
                    ? new SpecNodeLocalStateDocument { HostPath = state.HostPath, Criticality = state.Criticality.ToString() }
                    : null,
            }
            : null,
        Volumes = spec.Volumes
            .Select(volume => new SpecVolumeDocument
            {
                Id = volume.Id,
                MountPath = volume.MountPath,
                Size = volume.Size,
                Criticality = volume.Criticality.ToString(),
                AccessMode = volume.AccessMode,
                ResourceName = volume.ResourceName,
                ExistingClaim = volume.ExistingClaim,
                StorageClassName = volume.StorageClassName,
            })
            .ToList(),
        ConfigMapMounts = spec.ConfigMapMounts
            .Select(mount => new SpecConfigMapMountDocument { MountPath = mount.MountPath, ConfigMapName = mount.ConfigMapName })
            .ToList(),
        Health = new SpecHealthDocument
        {
            Mode = spec.Health.Mode.ToString(),
            HeartbeatSeconds = spec.Health.HeartbeatSeconds,
            Http = spec.Health.Http is { } http
                ? new SpecHttpProbeDocument { Port = http.Port, LivenessPath = http.LivenessPath, ReadinessPath = http.ReadinessPath }
                : null,
        },
        Observability = spec.Observability is { } observability
            ? new SpecObservabilityDocument
            {
                Metrics = observability.Metrics is { } metrics
                    ? new SpecMetricsDocument { Port = metrics.Port, Path = metrics.Path }
                    : null,
            }
            : null,
        Env = spec.Env
            .Select(env => new SpecEnvVarDocument
            {
                Name = env.Name,
                Kind = env.Kind.ToString(),
                Value = env.Value,
                SecretRef = env.SecretRef,
                CredentialId = env.CredentialId,
                ExcludedFromSpecHash = env.ExcludedFromSpecHash ? true : null,
            })
            .ToList(),
        ProvisionedCredentials = spec.ProvisionedCredentials
            .Select(credential => new SpecProvisionedCredentialDocument { CredentialId = credential.CredentialId, Name = credential.Name })
            .ToList(),
        HostRequirements = spec.HostRequirements
            .Select(requirement => new SpecHostRequirementDocument
            {
                Kind = requirement.Kind.ToString(),
                Name = requirement.Name,
                Description = requirement.Description,
            })
            .ToList(),
        UnsupportedPodPatchJson = spec.UnsupportedPodPatch?.PatchJson,
        RestartNonce = spec.RestartNonce,
        PendingScanRunId = spec.PendingScanRunId,
        ScanReadyNodeNames = spec.ScanReadyNodeNames?.ToList(),
        ScanIneligibleNodeNames = spec.ScanIneligibleNodeNames?.ToList(),
        UserNamespace = spec.UserNamespace ? true : null,
        UserNamespaceSkippedForHostNetwork = spec.UserNamespaceSkippedForHostNetwork ? true : null,
        UserNamespaceSkippedForDevicePassthrough = spec.UserNamespaceSkippedForDevicePassthrough ? true : null,
        SpecHash = spec.SpecHash,
    };

    private static WorkloadDesiredSpec FromDocument(WorkloadSpecDocument document) => new()
    {
        GroupInstanceId = document.ModuleInstanceId,
        GroupId = document.ModuleId,
        WorkloadId = document.WorkloadId,
        InstanceId = document.InstanceId,
        Inst8 = document.Inst8,
        Provider = WorkloadProviderId.Parse(document.Provider),
        ResourceName = document.ResourceName,
        SecretName = document.SecretName,
        Labels = Map(document.Labels),
        Image = document.Image,
        Platforms = [.. document.Platforms],
        Kind = Parse<WorkloadKind>(document.Kind),
        NodeSelector = Map(document.NodeSelector),
        Resources = document.Resources is { } resources
            ? new ResolvedResources
            {
                RequestsCpu = resources.RequestsCpu,
                RequestsMemory = resources.RequestsMemory,
                LimitsCpu = resources.LimitsCpu,
                LimitsMemory = resources.LimitsMemory,
            }
            : null,
        Network = document.Network is { } network
            ? new ResolvedNetwork
            {
                HostNetwork = network.HostNetwork,
                Mode = Parse<WorkloadNetworkMode>(network.Mode),
                Attachments = [.. network.Attachments],
                Mdns = network.Mdns,
                Ipv6Required = network.Ipv6Required,
                Ports = network.Ports
                    .Select(port => new ResolvedPort
                    {
                        Name = port.Name,
                        Port = port.Port,
                        Protocol = port.Protocol,
                        HostPort = port.HostPort,
                    })
                    .ToList(),
            }
            : null,
        Privileges = document.Privileges is { } privileges
            ? new ResolvedPrivileges
            {
                Capabilities = [.. privileges.Capabilities],
                Privileged = privileges.Privileged,
                DevicePassthroughPrivileged = privileges.DevicePassthroughPrivileged == true,
                RunAsRoot = privileges.RunAsRoot,
                FsGroup = privileges.FsGroup,
                TunDevice = privileges.TunDevice,
                CallsBackendRest = privileges.CallsBackendRest,
                DbusHostPath = privileges.DbusHostPath,
                DevicePaths = [.. privileges.DevicePaths],
                DeviceGroupIds = [.. privileges.DeviceGroupIds],
                JoinsDeviceGroups = privileges.JoinsDeviceGroups,
                HostPaths = privileges.HostPaths
                    .Select(path => new ResolvedHostPath { HostPath = path.HostPath, MountPath = path.MountPath })
                    .ToList(),
                NodeLocalState = privileges.NodeLocalState is { } state
                    ? new ResolvedNodeLocalState { HostPath = state.HostPath, Criticality = Parse<VolumeCriticality>(state.Criticality) }
                    : null,
            }
            : null,
        Volumes = document.Volumes
            .Select(volume => new ResolvedVolume
            {
                Id = volume.Id,
                MountPath = volume.MountPath,
                Size = volume.Size,
                Criticality = Parse<VolumeCriticality>(volume.Criticality),
                AccessMode = volume.AccessMode,
                ResourceName = volume.ResourceName,
                ExistingClaim = volume.ExistingClaim,
                StorageClassName = volume.StorageClassName,
            })
            .ToList(),
        ConfigMapMounts = document.ConfigMapMounts
            .Select(mount => new ResolvedConfigMapMount { MountPath = mount.MountPath, ConfigMapName = mount.ConfigMapName })
            .ToList(),
        Health = new WorkloadHealth
        {
            Mode = new WorkloadHealthMode(document.Health.Mode),
            HeartbeatSeconds = document.Health.HeartbeatSeconds,
            Http = document.Health.Http is { } http
                ? new WorkloadHttpProbe { Port = http.Port, LivenessPath = http.LivenessPath, ReadinessPath = http.ReadinessPath }
                : null,
        },
        Observability = document.Observability is { } observability
            ? new WorkloadObservability
            {
                Metrics = observability.Metrics is { } metrics
                    ? new WorkloadMetrics { Port = metrics.Port, Path = metrics.Path }
                    : null,
            }
            : null,
        Env = document.Env
            .Select(env => new ResolvedEnvVar
            {
                Name = env.Name,
                Kind = Parse<ResolvedEnvKind>(env.Kind),
                Value = env.Value,
                SecretRef = env.SecretRef,
                CredentialId = env.CredentialId,
                ExcludedFromSpecHash = env.ExcludedFromSpecHash == true,
            })
            .ToList(),
        ProvisionedCredentials = (document.ProvisionedCredentials ?? [])
            .Select(credential => new ProvisionedCredential(credential.CredentialId, credential.Name))
            .ToList(),
        HostRequirements = document.HostRequirements
            .Select(requirement => new HostRequirement
            {
                Kind = Parse<HostRequirementKind>(requirement.Kind),
                Name = requirement.Name,
                Description = requirement.Description,
            })
            .ToList(),
        UnsupportedPodPatch = document.UnsupportedPodPatchJson is { } patch ? new UnsupportedPodPatchSpec { PatchJson = patch } : null,
        RestartNonce = document.RestartNonce,
        PendingScanRunId = document.PendingScanRunId,
        ScanReadyNodeNames = document.ScanReadyNodeNames?.ToList(),
        ScanIneligibleNodeNames = document.ScanIneligibleNodeNames?.ToList(),
        UserNamespace = document.UserNamespace == true,
        UserNamespaceSkippedForHostNetwork = document.UserNamespaceSkippedForHostNetwork == true,
        UserNamespaceSkippedForDevicePassthrough = document.UserNamespaceSkippedForDevicePassthrough == true,
        SpecHash = document.SpecHash,
    };

    public static WorkloadUpdateEvidenceDocument Evidence(WorkloadUpdateEvidence evidence) => new()
    {
        ToVersion = evidence.ToVersion,
        CapturedAtUtc = evidence.CapturedAtUtc,
        Workloads = evidence.Workloads
            .Select(workload => new EvidenceWorkloadDocument
            {
                WorkloadId = workload.WorkloadId,
                PreviousContainerLogTail = [.. workload.PreviousContainerLogTail],
                RecentEvents = [.. workload.RecentEvents],
                Note = workload.Note,
            })
            .ToList(),
    };

    public static WorkloadUpdateEvidence FromDocument(WorkloadUpdateEvidenceDocument document) => new(
        document.ToVersion,
        document.CapturedAtUtc,
        document.Workloads
            .Select(workload => new WorkloadEvidenceCapture(
                workload.WorkloadId, [.. workload.PreviousContainerLogTail], [.. workload.RecentEvents], workload.Note))
            .ToList());

    public static GateClockStateDocument GateClock(WorkloadGateClockState clock) => new()
    {
        ClockStartedAtUtc = clock.ClockStartedAtUtc,
        PausedSinceUtc = clock.PausedSinceUtc,
        AccumulatedPause = clock.AccumulatedPause,
        ReconnectGraceUntilUtc = clock.ReconnectGraceUntilUtc,
        GraceConsumed = clock.GraceConsumed,
    };

    public static WorkloadGateClockState FromDocument(GateClockStateDocument document) => new()
    {
        ClockStartedAtUtc = document.ClockStartedAtUtc,
        PausedSinceUtc = document.PausedSinceUtc,
        AccumulatedPause = document.AccumulatedPause,
        ReconnectGraceUntilUtc = document.ReconnectGraceUntilUtc,
        GraceConsumed = document.GraceConsumed,
    };

    public static PipelineJournalDocument Journal(PipelineJournal journal) => new()
    {
        Direction = journal.Direction.ToString(),
        State = journal.State.ToString(),
        Entries = journal.Entries
            .Select(entry => new PipelineStageEntryDocument
            {
                StageId = entry.StageId,
                EnteredAtUtc = entry.EnteredAtUtc,
                Direction = entry.Direction.ToString(),
                State = entry.State.ToString(),
                Detail = entry.Detail,
            })
            .ToList(),
    };

    public static PipelineJournal FromDocument(PipelineJournalDocument document) => new(
        document.Entries
            .Select(entry => new StageEntry(
                entry.StageId,
                entry.EnteredAtUtc,
                Parse<PipelineDirection>(entry.Direction),
                Parse<StageEntryState>(entry.State),
                entry.Detail))
            .ToList(),
        Parse<PipelineDirection>(document.Direction),
        Parse<PipelineState>(document.State));

    private static List<WorkloadStringPairDocument> Pairs(IReadOnlyDictionary<string, string> map)
        => map
            .OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => new WorkloadStringPairDocument { Key = pair.Key, Value = pair.Value })
            .ToList();

    private static Dictionary<string, string> Map(List<WorkloadStringPairDocument> pairs)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in pairs)
        {
            map[pair.Key] = pair.Value ?? string.Empty;
        }

        return map;
    }

    private sealed class GateClockColumn
    {
        public DateTimeOffset? ClockStartedAtUtc { get; init; }

        public DateTimeOffset? PausedSinceUtc { get; init; }

        public TimeSpan AccumulatedPause { get; init; }

        public DateTimeOffset? ReconnectGraceUntilUtc { get; init; }

        public bool GraceConsumed { get; init; }
    }
}
