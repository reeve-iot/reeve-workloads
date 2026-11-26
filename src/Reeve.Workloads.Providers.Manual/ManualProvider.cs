using Reeve.Workloads.Abstractions.Contracts;
using Reeve.Workloads.Abstractions.Providers;

namespace Reeve.Workloads.Providers.Manual;

/// <summary>The identity and declared traits of the provider this package ships.</summary>
/// <remarks>Declared here, not in Abstractions: the vocabulary names no provider, so a third-party package declares itself exactly the way this one does.</remarks>
public static class ManualProvider
{
    /// <summary>No orchestration at all: the operator applies the artifacts the library renders.</summary>
    public static WorkloadProviderId Id { get; } = WorkloadProviderId.Parse("manual");

    /// <summary>Materializes NOTHING — the operator applies what the renderers emit, so nothing exists at runtime
    /// for Reeve to enumerate or fan out: a scan-per-node run has no per-node Job to create and no Ready-node set to
    /// read. Still strict about attachment names, and runs a device-binding workload privileged, because one manual
    /// install renders Kubernetes, Compose and Helm artifacts alike.</summary>
    public static WorkloadProviderDescriptor Descriptor { get; } = new()
    {
        Id = Id,
        DisplayName = "Manual",
        LocationNoun = "operator's host",
        Materializes = false,
        SchedulesAcrossNodes = false,
        DevicePassthroughRequiresPrivileged = true,
    };
}
