using Reeve.Workloads.Abstractions.Contracts;
using Reeve.Workloads.Abstractions.Providers;

namespace Reeve.Workloads.Providers.Docker;

/// <summary>The identity and declared traits of the provider this package ships.</summary>
/// <remarks>Declared here, not in Abstractions: the vocabulary names no provider, so a third-party package declares itself exactly the way this one does.</remarks>
public static class DockerProvider
{
    /// <summary>Docker socket orchestration.</summary>
    public static WorkloadProviderId Id { get; } = WorkloadProviderId.Parse("docker");

    /// <summary>Docker publishes host ports, needs no RFC 1123 attachment names, has container CPU LIMITS but no
    /// requests, and is one host. How it names volumes is its namer's business, not a trait.</summary>
    public static WorkloadProviderDescriptor Descriptor { get; } = new()
    {
        Id = Id,
        DisplayName = "Docker",
        LocationNoun = "docker host",
        RequiresRfc1123AttachmentNames = false,
        PublishesHostPorts = true,
        SupportsCpuRequests = false,
        SchedulesAcrossNodes = false,
        ReadsStatusInBatches = true,
    };
}
