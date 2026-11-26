using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace Reeve.Workloads.EntityFrameworkCore.Persistence;

internal sealed class WorkloadsSchemaMarkerExtension : IDbContextOptionsExtension
{
    private DbContextOptionsExtensionInfo? info;

    public DbContextOptionsExtensionInfo Info => info ??= new MarkerInfo(this);

    public void ApplyServices(IServiceCollection services)
    {
    }

    public void Validate(IDbContextOptions options)
    {
    }

    private sealed class MarkerInfo(WorkloadsSchemaMarkerExtension extension)
        : DbContextOptionsExtensionInfo(extension)
    {
        public override bool IsDatabaseProvider => false;

        public override string LogFragment => string.Empty;

        public override int GetServiceProviderHashCode() => 0;

        public override bool ShouldUseSameServiceProvider(DbContextOptionsExtensionInfo other)
            => other is MarkerInfo;

        public override void PopulateDebugInfo(IDictionary<string, string> debugInfo)
            => debugInfo["Workloads:SchemaOwnedByLibrary"] = "1";
    }
}
