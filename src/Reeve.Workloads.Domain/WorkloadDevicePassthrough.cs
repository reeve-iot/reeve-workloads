using Reeve.Workloads.Abstractions.Providers;

namespace Reeve.Workloads.Domain;

internal static class WorkloadDevicePassthrough
{
    public static bool NeedsPrivileged(WorkloadProviderDescriptor provider, int boundDevicePaths)
        => provider.DevicePassthroughRequiresPrivileged && boundDevicePaths > 0;
}
