using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Reeve.Workloads.Abstractions;
using Reeve.Workloads.Abstractions.Network;
using Reeve.Workloads.Core.Network;
using Reeve.Workloads.EntityFrameworkCore;
using Reeve.Workloads.EntityFrameworkCore.Orphans;
using Reeve.Workloads.EntityFrameworkCore.Persistence;

namespace Reeve.Workloads.DependencyInjection;

/// <summary>The EF Core-backed stores behind the engine's repository ports, and the <c>WorkloadsDbContext</c> they
/// run on. The host supplies only the provider and the connection; where the tables live and which assembly holds
/// their migrations are the library's own facts, not a host's to restate.</summary>
public static class ReeveWorkloadsEntityFrameworkBuilderExtensions
{
    /// <summary>Registers the workloads DbContext, the migrator that brings its schema up to date, and every
    /// engine repository over it.</summary>
    /// <param name="builder">The builder returned by <c>AddReeveWorkloads</c>.</param>
    /// <param name="configure">The provider and the connection, and nothing else the library also decides:
    /// a migrations assembly or a history table named here is overwritten, because only one of the two
    /// answers can be right and it is not the host's.</param>
    /// <exception cref="InvalidOperationException">The collection already holds a <c>WorkloadsDbContext</c>
    /// registration — a host's own <c>AddDbContext</c> , or a second call to this method.</exception>
    public static ReeveWorkloadsBuilder AddEntityFrameworkStores(
        this ReeveWorkloadsBuilder builder,
        Action<DbContextOptionsBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(configure);
        RefuseAContextSomebodyElseAlreadyRegistered(builder.Services);

        var store = new WorkloadsStoreOptions();
        builder.Services.TryAddSingleton(store);

        var schema = WorkloadsSchemaRegistration.Probe(configure, store);

        builder.Services.AddDbContext<WorkloadsDbContext>(options =>
        {
            configure(options);
            schema.ApplyTo(options);
        });

        if (schema.IsRelational)
        {
            builder.AddDefault<IWorkloadsSchemaMigrator, EfWorkloadsSchemaMigrator>(ServiceLifetime.Scoped);
        }

        return builder
            .Add<IWorkloadInstanceRepository, WorkloadInstanceRepository>(ServiceLifetime.Scoped)
            .Add<IWorkloadUpdateIntentRepository, WorkloadUpdateIntentRepository>(ServiceLifetime.Scoped)
            .Add<IWorkloadGroupRepository, WorkloadGroupRepository>(ServiceLifetime.Scoped)
            .Add<IWorkloadInstanceIdReader, EfWorkloadInstanceIdReader>(ServiceLifetime.Scoped)
            .Add<IRetainedVolumeRepository, RetainedVolumeRepository>(ServiceLifetime.Scoped)
            .Add<IWorkloadResolvedParameterWriter, WorkloadResolvedParameterWriter>(ServiceLifetime.Scoped)
            .AddDefault<IWorkloadInstallationIdentity, EfWorkloadInstallationIdentity>(ServiceLifetime.Scoped);
    }

    private static void RefuseAContextSomebodyElseAlreadyRegistered(IServiceCollection services)
    {
        var existing = services.FirstOrDefault(descriptor =>
            descriptor.ServiceType == typeof(WorkloadsDbContext)
            || descriptor.ServiceType == typeof(DbContextOptions<WorkloadsDbContext>));

        if (existing is null)
        {
            return;
        }

        var found = existing.ServiceType == typeof(WorkloadsDbContext)
            ? nameof(WorkloadsDbContext)
            : "DbContextOptions<WorkloadsDbContext>";

        throw new InvalidOperationException(
            $"'{found}' is already registered, and AddEntityFrameworkStores registers the "
            + "workloads context itself. Delete the host's own "
            + "AddDbContext<WorkloadsDbContext>(...) call — including its MigrationsAssembly and "
            + "MigrationsHistoryTable lines and the WorkloadsStoreOptions it handed them — and pass the provider "
            + "and the connection to AddEntityFrameworkStores instead. (If this is a second call to "
            + "AddEntityFrameworkStores, delete that one: two calls compose two option actions, the last "
            + "connection string wins, and every repository is registered twice.)");
    }
}
