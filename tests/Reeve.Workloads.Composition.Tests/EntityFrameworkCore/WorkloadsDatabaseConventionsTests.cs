using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Storage;
using Reeve.Workloads.EntityFrameworkCore.Persistence;
using Reeve.Workloads.EntityFrameworkCore.PostgreSql;

namespace Reeve.Workloads.Composition.Tests.EntityFrameworkCore;

public sealed class WorkloadsDatabaseConventionsTests
{
    private const string ActiveVolume = "\"reattached_at_utc\" IS NULL AND \"purged_at_utc\" IS NULL";

    [Fact]
    public void Postgres_keeps_the_filters_the_migrations_recorded()
    {
        IndexFilters(PostgresModel()).Should().BeEquivalentTo(new Dictionary<string, string>
        {
            ["ix_retained_volumes_source_workload_instance_id_volume_id"] = ActiveVolume,
            ["ux_retained_volumes_active_resource_name"] = ActiveVolume,
            ["ux_workload_update_intents_active_module_instance_id"] =
                "\"phase\" NOT IN ('Completed', 'RolledBack', 'RollbackFailed')",
        });
    }

    [Fact]
    public void Postgres_stores_every_json_document_as_jsonb_and_the_node_list_as_an_array()
    {
        var model = PostgresModel();

        DocumentStoreTypes(model).Should().OnlyContain(column => column.StoreType == "jsonb")
            .And.HaveCount(WorkloadsModelContract.JsonDocumentColumns.Count);
        ListStoreTypes(model).Should().OnlyContain(column => column.StoreType == "text[]")
            .And.Contain(column => column.Column == "last_scan_failed_nodes")
            .And.Contain(column => column.Column == "adopted_resource_workload_ids");
    }

    [Fact]
    public void The_json_documents_keep_the_column_names_the_migrations_will_record()
        => DocumentStoreTypes(PostgresModel()).Select(column => (column.Table, column.Column)).Should().BeEquivalentTo(
        [
            ("workload_instances", "parameters_document"),
            ("workload_instances", "retained_volumes_snapshot_document"),
            ("workload_instances", "attachment_pin_document"),
            ("workload_update_intents", "target_specs_document"),
            ("workload_update_intents", "previous_specs_document"),
            ("workload_update_intents", "previous_parameters_document"),
            ("workload_update_intents", "operator_parameters_document"),
            ("workload_update_intents", "previous_password_parameter_names_document"),
            ("workload_update_intents", "candidate_password_parameter_names_document"),
            ("workload_update_intents", "from_digests_document"),
            ("workload_update_intents", "to_digests_document"),
            ("workload_update_intents", "evidence_document"),
            ("workload_update_intents", "stage_journal_document"),
            ("workload_update_intent_steps", "gate_clock_state_document"),
        ]);

    [Fact]
    public void The_contract_lists_every_json_document_of_the_model()
    {
        var mapped = PostgresModel().GetEntityTypes()
            .SelectMany(entity => entity.GetComplexProperties()
                .Where(property => property.ComplexType.GetContainerColumnName() is not null)
                .Select(property => (entity.ClrType, property.Name)));

        mapped.Should().BeEquivalentTo(
            WorkloadsModelContract.JsonDocumentColumns.Select(column => (column.EntityType, column.PropertyName)));
    }

    [Fact]
    public void Every_property_of_a_json_document_is_mapped_inside_its_column()
    {
        var unmapped = PostgresModel().GetEntityTypes()
            .SelectMany(entity => entity.GetComplexProperties())
            .Where(property => property.ComplexType.GetContainerColumnName() is not null)
            .SelectMany(property => UnmappedMembers(property.ComplexType, property.Name))
            .ToList();

        unmapped.Should().BeEmpty();
    }

    private static IEnumerable<string> UnmappedMembers(IComplexType type, string path)
    {
        var mapped = type.GetProperties().Select(property => property.Name)
            .Concat(type.GetComplexProperties().Select(property => property.Name))
            .ToHashSet(StringComparer.Ordinal);
        var own = type.ClrType.GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)
            .Where(member => !mapped.Contains(member.Name))
            .Select(member => $"{path}.{member.Name}");

        return own.Concat(type.GetComplexProperties()
            .SelectMany(property => UnmappedMembers(property.ComplexType, $"{path}.{property.Name}")));
    }

    [Fact]
    public void Every_entity_can_be_read_on_the_provider_this_library_ships_conventions_for()
    {
        using var context = PostgresContext();

        foreach (var entity in context.Model.GetEntityTypes().Where(entity => entity.GetTableName() is not null))
        {
            var read = () => QueryOf(context, entity.ClrType);

            read.Should().NotThrow($"{entity.ClrType.Name} must materialize on Npgsql");
        }
    }

    [Fact]
    public void The_agnostic_model_names_no_filter()
        => IndexFilters(AgnosticModel()).Should().BeEmpty();

    [Fact]
    public void The_contract_names_every_unique_index_it_filters()
    {
        var model = AgnosticModel();

        foreach (var index in WorkloadsModelContract.ActiveRowIndexes)
        {
            model.FindEntityType(index.EntityType)!.GetIndexes()
                .Should().ContainSingle(candidate => candidate.GetDatabaseName() == index.DatabaseName && candidate.IsUnique);
        }
    }

    [Fact]
    public void Each_package_is_found_for_its_own_provider_only()
    {
        WorkloadsDatabaseConventions.For(WorkloadsPostgreSqlConventions.ProviderName)
            .Should().ContainSingle().Which.Should().BeOfType<WorkloadsPostgreSqlConventions>();
        WorkloadsDatabaseConventions.For("Microsoft.EntityFrameworkCore.SqlServer").Should().BeEmpty();
        WorkloadsDatabaseConventions.For("Microsoft.EntityFrameworkCore.InMemory").Should().BeEmpty();
        WorkloadsDatabaseConventions.For(null).Should().BeEmpty();
    }

    [Fact]
    public void A_registration_naming_something_else_fails_with_the_type_named()
    {
        var registered = new[] { new WorkloadsDatabaseConventionsAttribute("Some.Provider", typeof(string)) };

        var act = () => WorkloadsDatabaseConventions.For("Some.Provider", registered);

        act.Should().Throw<InvalidOperationException>().WithMessage("*System.String*");
    }

    [Fact]
    public void Two_packages_for_one_provider_apply_in_the_same_order_every_time()
    {
        var registered = new[]
        {
            new WorkloadsDatabaseConventionsAttribute("Some.Provider", typeof(ZLastConventions)),
            new WorkloadsDatabaseConventionsAttribute("Some.Provider", typeof(ALastButOneConventions)),
        };

        WorkloadsDatabaseConventions.For("Some.Provider", registered)
            .Select(conventions => conventions.GetType())
            .Should().Equal(typeof(ALastButOneConventions), typeof(ZLastConventions));
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

    private static IModel PostgresModel() => DesignTimeModel(options => options.UseNpgsql("Host=unopened;Database=unopened"));

    private static IModel AgnosticModel() => DesignTimeModel(options => options.UseInMemoryDatabase("unopened"));

    private static IModel DesignTimeModel(Action<DbContextOptionsBuilder<WorkloadsDbContext>> useProvider)
    {
        var builder = new DbContextOptionsBuilder<WorkloadsDbContext>();
        useProvider(builder);
        using var context = new WorkloadsDbContext(builder.Options, new WorkloadsStoreOptions());
        return context.GetService<IDesignTimeModel>().Model;
    }

    private static WorkloadsDbContext PostgresContext()
    {
        var builder = new DbContextOptionsBuilder<WorkloadsDbContext>();
        builder.UseNpgsql("Host=unopened;Database=unopened");
        return new WorkloadsDbContext(builder.Options, new WorkloadsStoreOptions());
    }

    private static string QueryOf(DbContext context, Type entityType)
        => ((IQueryable)typeof(DbContext).GetMethod(nameof(DbContext.Set), Type.EmptyTypes)!
            .MakeGenericMethod(entityType)
            .Invoke(context, null)!)
            .ToQueryString();

    private static List<(string Table, string Column, string StoreType)> DocumentStoreTypes(IModel model) =>
        model.GetEntityTypes()
            .SelectMany(entity => entity.GetComplexProperties()
                .Select(property => property.ComplexType.GetContainerColumnName())
                .OfType<string>()
                .Select(column => (Table: entity.GetTableName()!, Column: column)))
            .Select(column => (column.Table, column.Column, StoreType(model, column.Table, column.Column)))
            .ToList();

    private static string StoreType(IModel model, string table, string column) =>
        model.GetRelationalModel().Tables.Single(candidate => candidate.Name == table).FindColumn(column)!.StoreType;

    private static List<(string Column, string StoreType)> ListStoreTypes(IModel model) =>
        model.GetEntityTypes()
            .SelectMany(entity => entity.GetProperties()
                .Where(property => property.IsPrimitiveCollection)
                .Select(property => (Column: property.GetColumnName(), StoreType: property.GetColumnType())))
            .ToList();

    private static Dictionary<string, string> IndexFilters(IModel model) =>
        model.GetEntityTypes()
            .SelectMany(entity => entity.GetIndexes())
            .Where(index => index.GetFilter() is not null)
            .ToDictionary(index => index.GetDatabaseName()!, index => index.GetFilter()!);
}
