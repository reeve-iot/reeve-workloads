namespace Reeve.Workloads.Tests.Shared.CredentialFree;

public sealed class WorkloadStringPairDocument
{
    public string Key { get; init; } = string.Empty;

    public string? Value { get; init; }
}

public sealed class WorkloadSpecMapDocument
{
    public int SchemaVersion { get; init; } = 1;

    public string? SourceFingerprint { get; init; }

    public bool Unreadable { get; init; }

    public List<WorkloadSpecDocument> Workloads { get; init; } = [];
}

public sealed class WorkloadSpecDocument
{
    public string Key { get; init; } = string.Empty;

    public Guid ModuleInstanceId { get; init; }

    public string ModuleId { get; init; } = string.Empty;

    public string WorkloadId { get; init; } = string.Empty;

    public Guid InstanceId { get; init; }

    public string Inst8 { get; init; } = string.Empty;

    public string Provider { get; init; } = string.Empty;

    public string ResourceName { get; init; } = string.Empty;

    public string SecretName { get; init; } = string.Empty;

    public List<WorkloadStringPairDocument> Labels { get; init; } = [];

    public string Image { get; init; } = string.Empty;

    public List<string> Platforms { get; init; } = [];

    public string Kind { get; init; } = string.Empty;

    public List<WorkloadStringPairDocument> NodeSelector { get; init; } = [];

    public SpecResourcesDocument? Resources { get; init; }

    public SpecNetworkDocument? Network { get; init; }

    public SpecPrivilegesDocument? Privileges { get; init; }

    public List<SpecVolumeDocument> Volumes { get; init; } = [];

    public List<SpecConfigMapMountDocument> ConfigMapMounts { get; init; } = [];

    public SpecHealthDocument Health { get; init; } = new();

    public SpecObservabilityDocument? Observability { get; init; }

    public List<SpecEnvVarDocument> Env { get; init; } = [];

    public List<SpecHostRequirementDocument> HostRequirements { get; init; } = [];

    public string? UnsupportedPodPatchJson { get; init; }

    public long RestartNonce { get; init; }

    public string? PendingScanRunId { get; init; }

    public List<string>? ScanReadyNodeNames { get; init; }

    public List<string>? ScanIneligibleNodeNames { get; init; }

    public string SpecHash { get; init; } = string.Empty;
}

public sealed class SpecResourcesDocument
{
    public string? RequestsCpu { get; init; }

    public string? RequestsMemory { get; init; }

    public string? LimitsCpu { get; init; }

    public string? LimitsMemory { get; init; }
}

public sealed class SpecNetworkDocument
{
    public bool HostNetwork { get; init; }

    public string Mode { get; init; } = string.Empty;

    public List<string> Attachments { get; init; } = [];

    public bool Mdns { get; init; }

    public bool Ipv6Required { get; init; }

    public List<SpecPortDocument> Ports { get; init; } = [];
}

public sealed class SpecPortDocument
{
    public string Name { get; init; } = string.Empty;

    public int Port { get; init; }

    public string Protocol { get; init; } = string.Empty;

    public int? HostPort { get; init; }
}

public sealed class SpecPrivilegesDocument
{
    public List<string> Capabilities { get; init; } = [];

    public bool Privileged { get; init; }

    public bool RunAsRoot { get; init; }

    public bool TunDevice { get; init; }

    public bool CallsBackendRest { get; init; }

    public string? DbusHostPath { get; init; }

    public List<string> DevicePaths { get; init; } = [];

    public List<int> DeviceGroupIds { get; init; } = [];

    public bool JoinsDeviceGroups { get; init; }

    public List<SpecHostPathDocument> HostPaths { get; init; } = [];

    public SpecNodeLocalStateDocument? NodeLocalState { get; init; }
}

public sealed class SpecHostPathDocument
{
    public string HostPath { get; init; } = string.Empty;

    public string MountPath { get; init; } = string.Empty;
}

public sealed class SpecNodeLocalStateDocument
{
    public string HostPath { get; init; } = string.Empty;

    public string Criticality { get; init; } = string.Empty;
}

public sealed class SpecVolumeDocument
{
    public string Id { get; init; } = string.Empty;

    public string MountPath { get; init; } = string.Empty;

    public string Size { get; init; } = string.Empty;

    public string Criticality { get; init; } = string.Empty;

    public string AccessMode { get; init; } = string.Empty;

    public string ResourceName { get; init; } = string.Empty;

    public bool ExistingClaim { get; init; }

    public string? StorageClassName { get; init; }
}

public sealed class SpecConfigMapMountDocument
{
    public string MountPath { get; init; } = string.Empty;

    public string ConfigMapName { get; init; } = string.Empty;
}

public sealed class SpecHealthDocument
{
    public string Mode { get; init; } = string.Empty;

    public int HeartbeatSeconds { get; init; }

    public SpecHttpProbeDocument? Http { get; init; }
}

public sealed class SpecHttpProbeDocument
{
    public int Port { get; init; }

    public string? LivenessPath { get; init; }

    public string? ReadinessPath { get; init; }
}

public sealed class SpecObservabilityDocument
{
    public SpecMetricsDocument? Metrics { get; init; }
}

public sealed class SpecMetricsDocument
{
    public int Port { get; init; }

    public string Path { get; init; } = string.Empty;
}

public sealed class SpecEnvVarDocument
{
    public string Name { get; init; } = string.Empty;

    public string Kind { get; init; } = string.Empty;

    public string? Value { get; init; }

    public string? SecretRef { get; init; }
}

public sealed class SpecHostRequirementDocument
{
    public string Kind { get; init; } = string.Empty;

    public string? Name { get; init; }

    public string? Description { get; init; }
}
