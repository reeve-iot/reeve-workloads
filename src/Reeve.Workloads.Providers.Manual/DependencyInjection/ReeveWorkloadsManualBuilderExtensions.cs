using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Reeve.Workloads.Abstractions;
using Reeve.Workloads.Abstractions.Providers;
using Reeve.Workloads.Providers.Manual;

namespace Reeve.Workloads.DependencyInjection;

/// <summary>The manual provider: it deploys nothing and observes whether a workload is live by the host's own
/// evidence, which is why it needs the host's <see cref="IWorkloadExternalLiveness"/> and nothing else. The port has
/// no default: <c>ValidateHostPorts</c> reports it missing for a host that registers this provider without it.</summary>
public static class ReeveWorkloadsManualBuilderExtensions
{
    /// <summary>Registers the manual provider and its resource namer. It registers no capability probe and no host address contributor:
    /// there is no runtime to probe, and no address the library could derive for one it does not run.</summary>
    /// <param name="builder">The builder returned by <c>AddReeveWorkloads</c>.</param>
    public static ReeveWorkloadsBuilder AddManualProvider(this ReeveWorkloadsBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        return builder
            .AddEnumerable<IWorkloadProvider, ManualWorkloadProvider>(ServiceLifetime.Singleton)
            .AddEnumerable<IWorkloadProviderSettingsCodec, ManualProviderSettingsCodec>(ServiceLifetime.Singleton)
            .AddEnumerable<IWorkloadProviderAutoDetect, ManualAutoDetect>(ServiceLifetime.Singleton)
            .AddEnumerable<IWorkloadResourceNamer, ManualResourceNamer>(ServiceLifetime.Singleton);
    }
}
