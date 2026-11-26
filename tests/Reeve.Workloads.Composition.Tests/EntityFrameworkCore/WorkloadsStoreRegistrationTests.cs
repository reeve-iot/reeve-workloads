using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Reeve.Workloads.Abstractions;
using Reeve.Workloads.DependencyInjection;
using Reeve.Workloads.EntityFrameworkCore.Persistence;
using Reeve.Workloads.EntityFrameworkCore.PostgreSql;

namespace Reeve.Workloads.Composition.Tests.EntityFrameworkCore;

public sealed class WorkloadsStoreRegistrationTests
{
    private const string UnopenedNpgsql = "Host=composition-tests.invalid;Database=workloads;Username=none;Password=none";

    private const string UnopenedSqlServer = "Server=composition-tests.invalid;Database=workloads;User Id=none;Password=none";

    private const string PostgreSqlPackage = "Reeve.Workloads.EntityFrameworkCore.PostgreSql";

    [Fact]
    public void A_provider_with_a_conventions_package_gets_that_package_as_its_migrations_assembly()
    {
        using var composed = Compose(options => options.UseNpgsql(UnopenedNpgsql));

        composed.RelationalOptions.MigrationsAssembly.Should().Be(PostgreSqlPackage);
    }

    [Theory]
    [InlineData("npgsql")]
    [InlineData("sqlserver")]
    public void The_history_table_is_the_librarys_two_values_on_every_relational_provider(string provider)
    {
        var store = new WorkloadsStoreOptions();
        using var composed = Compose(ProviderNamed(provider));

        composed.RelationalOptions.MigrationsHistoryTableName.Should().Be(store.MigrationsHistoryTable);
        composed.RelationalOptions.MigrationsHistoryTableSchema.Should().Be(store.Schema);
    }

    [Fact]
    public void A_provider_with_no_conventions_package_keeps_the_assembly_EF_would_have_used()
    {
        using var composed = Compose(options => options.UseSqlServer(UnopenedSqlServer));

        composed.Context.GetService<IMigrationsAssembly>().Assembly
            .Should().BeSameAs(typeof(WorkloadsDbContext).Assembly);
    }

    [Fact]
    public void The_conventions_package_is_the_assembly_EF_resolves_and_not_merely_a_string_on_the_options()
    {
        using var composed = Compose(options => options.UseNpgsql(UnopenedNpgsql));

        composed.Context.GetService<IMigrationsAssembly>().Assembly
            .Should().BeSameAs(typeof(WorkloadsPostgreSqlConventions).Assembly);
    }

    [Fact]
    public void The_library_overrides_a_migrations_assembly_the_host_set_itself()
    {
        using var composed = Compose(options => options.UseNpgsql(
            UnopenedNpgsql,
            npgsql => npgsql
                .MigrationsAssembly("Some.Host.Migrations")
                .MigrationsHistoryTable("__HostHistory", "public")));

        composed.RelationalOptions.MigrationsAssembly.Should().Be(PostgreSqlPackage);
        composed.RelationalOptions.MigrationsHistoryTableName.Should().Be(new WorkloadsStoreOptions().MigrationsHistoryTable);
        composed.RelationalOptions.MigrationsHistoryTableSchema.Should().Be(new WorkloadsStoreOptions().Schema);
    }

    [Fact]
    public void A_host_naming_its_own_assembly_for_a_provider_the_library_cannot_serve_keeps_it()
    {
        using var composed = Compose(options => options.UseSqlServer(
            UnopenedSqlServer,
            sql => sql.MigrationsAssembly("Some.Host.Migrations")));

        composed.RelationalOptions.MigrationsAssembly.Should().Be("Some.Host.Migrations");
    }

    [Fact]
    public void A_provider_that_is_not_relational_at_all_registers_without_complaint()
    {
        using var composed = Compose(options => options.UseInMemoryDatabase("registration"));

        composed.Context.Model.Should().NotBeNull();
    }

    [Fact]
    public void Two_providers_are_left_for_EF_to_report_rather_than_for_LINQ_to()
    {
        var services = Composed(options => options.UseNpgsql(UnopenedNpgsql).UseSqlServer(UnopenedSqlServer));

        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        var use = () => scope.ServiceProvider.GetRequiredService<WorkloadsDbContext>().Model;

        use.Should().Throw<InvalidOperationException>()
            .WithMessage("*Only a single database provider can be registered*")
            .And.Message.Should().Contain("Npgsql").And.Contain("SqlServer");
    }

    [Fact]
    public void The_store_options_come_from_the_library_now_that_the_host_no_longer_registers_them()
    {
        using var composed = Compose(options => options.UseNpgsql(UnopenedNpgsql));

        composed.Services.GetRequiredService<WorkloadsStoreOptions>().Schema.Should().Be("workloads");
    }

    [Fact]
    public void A_relational_provider_gets_a_migrator_so_a_host_that_composed_the_stores_can_boot()
    {
        using var composed = Compose(options => options.UseNpgsql(UnopenedNpgsql));

        composed.Scope.ServiceProvider.GetService<IWorkloadsSchemaMigrator>()
            .Should().NotBeNull().And.BeOfType<EfWorkloadsSchemaMigrator>();
    }

    [Fact]
    public void A_provider_with_no_schema_to_migrate_gets_no_migrator_at_all()
    {
        using var composed = Compose(options => options.UseInMemoryDatabase("no-migrator"));

        composed.Scope.ServiceProvider.GetService<IWorkloadsSchemaMigrator>().Should().BeNull();
    }

    [Fact]
    public void A_context_registered_before_the_call_is_refused_with_the_line_to_delete()
    {
        var services = new ServiceCollection();
        services.AddDbContext<WorkloadsDbContext>(options => options.UseNpgsql(UnopenedNpgsql));

        var compose = () => services
            .AddReeveWorkloads(EmptyConfiguration)
            .AddEntityFrameworkStores(options => options.UseNpgsql(UnopenedNpgsql));

        compose.Should().Throw<InvalidOperationException>()
            .WithMessage("*AddDbContext<WorkloadsDbContext>*")
            .And.Message.Should().Contain("MigrationsAssembly");
    }

    [Fact]
    public void Pre_built_options_alone_are_refused_even_with_no_context_registration_beside_them()
    {
        var services = new ServiceCollection();
        services.AddSingleton(new DbContextOptionsBuilder<WorkloadsDbContext>().UseNpgsql(UnopenedNpgsql).Options);

        var compose = () => services
            .AddReeveWorkloads(EmptyConfiguration)
            .AddEntityFrameworkStores(options => options.UseNpgsql(UnopenedNpgsql));

        compose.Should().Throw<InvalidOperationException>().WithMessage("*DbContextOptions*");
    }

    [Fact]
    public void Calling_it_twice_is_refused_the_same_way()
    {
        var builder = new ServiceCollection()
            .AddReeveWorkloads(EmptyConfiguration)
            .AddEntityFrameworkStores(options => options.UseNpgsql(UnopenedNpgsql));

        var again = () => builder.AddEntityFrameworkStores(options => options.UseNpgsql(UnopenedNpgsql));

        again.Should().Throw<InvalidOperationException>().WithMessage("*AddEntityFrameworkStores*");
    }

    [Fact]
    public void A_context_re_registered_AFTER_the_call_is_caught_WITHOUT_anything_migrating()
    {
        var services = Composed(options => options.UseNpgsql(UnopenedNpgsql));
        services.AddDbContext<WorkloadsDbContext>(options => options.UseNpgsql(
            UnopenedNpgsql, npgsql => npgsql.MigrationsAssembly("Some.Host.Api")));

        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        var use = () => scope.ServiceProvider.GetRequiredService<WorkloadsDbContext>().Model;

        use.Should().Throw<InvalidOperationException>()
            .WithMessage("*Some.Host.Api*")
            .And.Message.Should().Contain("AddDbContext<WorkloadsDbContext>");
    }

    [Fact]
    public async Task The_migrator_path_is_caught_by_the_same_check()
    {
        var services = Composed(options => options.UseNpgsql(UnopenedNpgsql));
        services.AddDbContext<WorkloadsDbContext>(options => options.UseNpgsql(
            UnopenedNpgsql, npgsql => npgsql.MigrationsAssembly("Some.Host.Api")));

        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        var migrator = scope.ServiceProvider.GetRequiredService<IWorkloadsSchemaMigrator>();

        var migrate = async () => await migrator.MigrateAsync();

        (await migrate.Should().ThrowAsync<InvalidOperationException>()).WithMessage("*Some.Host.Api*");
    }

    [Fact]
    public void The_check_still_fires_for_a_composition_that_is_not_the_first_of_its_shape()
    {
        using var good = Compose(options => options.UseNpgsql(UnopenedNpgsql));
        _ = good.Context.Model;

        var services = Composed(options => options.UseNpgsql(UnopenedNpgsql));
        services.AddDbContext<WorkloadsDbContext>(options => options.UseNpgsql(
            UnopenedNpgsql, npgsql => npgsql.MigrationsAssembly("Some.Host.Api")));
        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        var use = () => scope.ServiceProvider.GetRequiredService<WorkloadsDbContext>().Model;

        use.Should().Throw<InvalidOperationException>().WithMessage("*Some.Host.Api*");
    }

    [Fact]
    public void A_history_table_taken_back_afterwards_is_caught_too()
    {
        var services = Composed(options => options.UseNpgsql(UnopenedNpgsql));
        services.AddDbContext<WorkloadsDbContext>(options => options.UseNpgsql(
            UnopenedNpgsql, npgsql => npgsql.MigrationsHistoryTable("__HostHistory", "public")));

        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        var use = () => scope.ServiceProvider.GetRequiredService<WorkloadsDbContext>().Model;

        use.Should().Throw<InvalidOperationException>()
            .WithMessage("*public.__HostHistory*")
            .And.Message.Should().Contain("workloads.__WorkloadsMigrationsHistory");
    }

    [Fact]
    public void An_assembly_named_by_REFERENCE_afterwards_cannot_take_the_migrations_back()
    {
        var services = Composed(options => options.UseNpgsql(UnopenedNpgsql));
        services.AddDbContext<WorkloadsDbContext>(options => options.UseNpgsql(
            UnopenedNpgsql, npgsql => npgsql.MigrationsAssembly(typeof(WorkloadsStoreRegistrationTests).Assembly)));

        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<WorkloadsDbContext>();

        context.Model.Should().NotBeNull();
        context.GetService<IMigrationsAssembly>().Assembly
            .Should().BeSameAs(typeof(WorkloadsPostgreSqlConventions).Assembly);
    }

    [Fact]
    public void Options_the_library_configured_pass_the_check_unaltered()
    {
        using var composed = Compose(options => options.UseNpgsql(UnopenedNpgsql));

        var use = () => composed.Context.Model;

        use.Should().NotThrow();
    }

    [Fact]
    public void The_lookup_is_silent_about_a_provider_no_package_serves()
    {
        WorkloadsDatabaseConventions.AssemblyFor("Microsoft.EntityFrameworkCore.InMemory").Should().BeNull();
        WorkloadsDatabaseConventions.AssemblyFor(null).Should().BeNull();
        WorkloadsDatabaseConventions.AssemblyFor("npgsql.entityframeworkcore.postgresql").Should().BeNull();
    }

    [Fact]
    public void Two_packages_in_one_assembly_serving_one_provider_still_name_one_migrations_assembly()
    {
        var registered = new[]
        {
            new WorkloadsDatabaseConventionsAttribute("Some.Provider", typeof(ZLastConventions)),
            new WorkloadsDatabaseConventionsAttribute("Some.Provider", typeof(ALastButOneConventions)),
        };

        WorkloadsDatabaseConventions.AssemblyFor("Some.Provider", registered)
            .Should().Be(typeof(ZLastConventions).Assembly.GetName().Name);
    }

    [Fact]
    public void Two_assemblies_serving_one_provider_refuse_to_pick_one()
    {
        var registered = new[]
        {
            new WorkloadsDatabaseConventionsAttribute("Some.Provider", typeof(ZLastConventions)),
            new WorkloadsDatabaseConventionsAttribute("Some.Provider", typeof(string)),
        };

        var act = () => WorkloadsDatabaseConventions.AssemblyFor("Some.Provider", registered);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*Some.Provider*")
            .And.Message.Should().Contain(typeof(string).Assembly.GetName().Name)
            .And.Contain(typeof(ZLastConventions).Assembly.GetName().Name);
    }

    [Fact]
    public void The_two_packages_refusal_happens_once_at_registration_rather_than_once_per_scope()
    {
        var scans = 0;
        using var composed = Compose(options =>
        {
            scans++;
            options.UseNpgsql(UnopenedNpgsql);
        });

        _ = composed.Scope.ServiceProvider.GetRequiredService<WorkloadsDbContext>();
        using var second = composed.Services.CreateScope();
        _ = second.ServiceProvider.GetRequiredService<WorkloadsDbContext>();

        scans.Should().Be(3);
    }

    public sealed class ALastButOneConventions : IWorkloadsDatabaseConventions
    {
        public void Apply(ModelBuilder modelBuilder)
        {
        }
    }

    public sealed class ZLastConventions : IWorkloadsDatabaseConventions
    {
        public void Apply(ModelBuilder modelBuilder)
        {
        }
    }

    private static readonly IConfiguration EmptyConfiguration = new ConfigurationBuilder().Build();

    private static Action<DbContextOptionsBuilder> ProviderNamed(string provider) => provider switch
    {
        "npgsql" => options => options.UseNpgsql(UnopenedNpgsql),
        "sqlserver" => options => options.UseSqlServer(UnopenedSqlServer),
        _ => throw new ArgumentOutOfRangeException(nameof(provider), provider, "no such provider in this suite"),
    };

    private static IServiceCollection Composed(Action<DbContextOptionsBuilder> configure)
        => new ServiceCollection()
            .AddReeveWorkloads(EmptyConfiguration)
            .AddEntityFrameworkStores(configure)
            .Services;

    private static Composition Compose(Action<DbContextOptionsBuilder> configure)
        => new(Composed(configure).BuildServiceProvider());

    private sealed class Composition(ServiceProvider services) : IDisposable
    {
        public ServiceProvider Services { get; } = services;

        public IServiceScope Scope { get; } = services.CreateScope();

        public WorkloadsDbContext Context => Scope.ServiceProvider.GetRequiredService<WorkloadsDbContext>();

        public RelationalOptionsExtension RelationalOptions =>
            Services.GetRequiredService<DbContextOptions<WorkloadsDbContext>>()
                .Extensions.OfType<RelationalOptionsExtension>().Single();

        public void Dispose()
        {
            Scope.Dispose();
            Services.Dispose();
        }
    }
}
