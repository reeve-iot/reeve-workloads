using Reeve.Workloads.Abstractions.Contracts;
using Reeve.Workloads.Abstractions.Providers;

namespace Reeve.Workloads.Tests.Shared;

internal static class StandInProviders
{
    public static WorkloadProviderId KubernetesId { get; } = WorkloadProviderId.Parse("kubernetes");

    public static WorkloadProviderId DockerId { get; } = WorkloadProviderId.Parse("docker");

    public static WorkloadProviderId ManualId { get; } = WorkloadProviderId.Parse("manual");

    public static WorkloadProviderDescriptor Kubernetes { get; } = new()
    {
        Id = KubernetesId,
        DisplayName = "Kubernetes",
        LocationNoun = "cluster",
        ReadsStatusInBatches = true,
        SupportsUserNamespaces = true,
        DevicePassthroughRequiresPrivileged = true,
    };

    public static WorkloadProviderDescriptor Docker { get; } = new()
    {
        Id = DockerId,
        DisplayName = "Docker",
        LocationNoun = "docker host",
        RequiresRfc1123AttachmentNames = false,
        PublishesHostPorts = true,
        SupportsCpuRequests = false,
        SchedulesAcrossNodes = false,
        ReadsStatusInBatches = true,
    };

    public static WorkloadProviderDescriptor Manual { get; } = new()
    {
        Id = ManualId,
        DisplayName = "Manual",
        LocationNoun = "operator's host",
        Materializes = false,
        SchedulesAcrossNodes = false,
        DevicePassthroughRequiresPrivileged = true,
    };

    public static WorkloadProviderDescriptor For(WorkloadProviderId id)
        => id == KubernetesId ? Kubernetes
            : id == DockerId ? Docker
            : id == ManualId ? Manual
            : new WorkloadProviderDescriptor { Id = id };
}
