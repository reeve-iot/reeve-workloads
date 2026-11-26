using Acme.Workloads.Nomad;
using Microsoft.Extensions.DependencyInjection;
using Reeve.Workloads.Abstractions.Providers;

namespace Reeve.Workloads.DependencyInjection;

public static class NomadBuilderExtensions
{
    public static ReeveWorkloadsBuilder AddNomadProvider(this ReeveWorkloadsBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.AddSelf<NomadWorkloadProvider>(ServiceLifetime.Singleton);
        builder.AddEnumerable<IWorkloadProviderSettingsCodec, NomadProviderSettingsCodec>(ServiceLifetime.Singleton);
        builder.AddEnumerable<IWorkloadProviderAutoDetect, NomadAutoDetect>(ServiceLifetime.Singleton);
        builder.AddEnumerable<IWorkloadResourceNamer, NomadResourceNamer>(ServiceLifetime.Singleton);
        return builder.Forward<IWorkloadProvider, NomadWorkloadProvider>(ServiceLifetime.Singleton);
    }
}
