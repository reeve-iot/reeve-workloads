using System.Text.RegularExpressions;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Reeve.Workloads.Abstractions.Domain.Entities;
using Reeve.Workloads.EntityFrameworkCore.Persistence;

namespace Reeve.Workloads.Composition.Tests.EntityFrameworkCore;

public sealed partial class WorkloadsColumnNamingTests
{
    [Theory]
    [InlineData("Id", "id")]
    [InlineData("ModuleInstanceId", "module_instance_id")]
    [InlineData("LastScanFailedNodes", "last_scan_failed_nodes")]
    [InlineData("AttachmentPinDocument", "attachment_pin_document")]
    [InlineData("ModuleIdUrl", "module_id_url")]
    [InlineData("HTTPProbe", "http_probe")]
    [InlineData("Sha256Digest", "sha256_digest")]
    [InlineData("V4DefaultRouteEgressesAttachment", "v4_default_route_egresses_attachment")]
    [InlineData("id", "id")]
    [InlineData("created_at_utc", "created_at_utc")]
    [InlineData("Created_AtUtc", "created_at_utc")]
    [InlineData("NodeIDs", "node_i_ds")]
    public void A_property_name_becomes_its_snake_case_column(string propertyName, string column)
        => WorkloadsColumnNaming.SnakeCase(propertyName).Should().Be(column);

    [Theory]
    [InlineData("GroupInstanceId", "module_instance_id")]
    [InlineData("GroupId", "module_id")]
    [InlineData("SourceGroupInstanceId", "source_module_instance_id")]
    public void A_group_property_keeps_the_column_it_had_as_a_module(string propertyName, string column)
        => WorkloadsColumnNaming.ColumnFor(propertyName).Should().Be(column);

    [Theory]
    [InlineData("WorkloadId", "workload_id")]
    [InlineData("GroupSlug", "group_slug")]
    [InlineData("ModuleId", "module_id")]
    public void Every_other_property_is_still_named_by_the_rule(string propertyName, string column)
        => WorkloadsColumnNaming.ColumnFor(propertyName).Should().Be(column);

    [Fact]
    public void The_rule_applied_to_a_model_keeps_a_group_property_on_its_old_column()
    {
        var modelBuilder = new ModelBuilder();
        modelBuilder.Entity<Grouped>(builder =>
        {
            builder.Property(entity => entity.GroupInstanceId);
            builder.Property(entity => entity.GroupId);
            builder.Property(entity => entity.WorkloadId);
        });

        WorkloadsColumnNaming.Apply(modelBuilder);

        var entity = modelBuilder.Model.FindEntityType(typeof(Grouped))!;
        string? Column(string property) => entity.FindProperty(property)![RelationalAnnotationNames.ColumnName] as string;
        Column(nameof(Grouped.GroupInstanceId)).Should().Be("module_instance_id");
        Column(nameof(Grouped.GroupId)).Should().Be("module_id");
        Column(nameof(Grouped.WorkloadId)).Should().Be("workload_id");
    }

    [Fact]
    public void A_column_name_already_pinned_is_refused_with_the_property_named()
    {
        var modelBuilder = new ModelBuilder();
        modelBuilder.Entity<Pinned>(builder => builder.Property(entity => entity.WorkloadId).HasColumnName("WorkloadId"));

        var apply = () => WorkloadsColumnNaming.Apply(modelBuilder);

        apply.Should().Throw<InvalidOperationException>().WithMessage("*'Pinned.WorkloadId'*'WorkloadId'*'workload_id'*");
    }

    [Fact]
    public void A_document_pinned_to_a_column_of_its_own_is_refused_with_the_property_named()
    {
        var modelBuilder = new ModelBuilder();
        modelBuilder.Entity<Pinned>(builder => builder.ComplexProperty(entity => entity.Document, document => document.ToJson("Doc")));

        var apply = () => WorkloadsColumnNaming.Apply(modelBuilder);

        apply.Should().Throw<InvalidOperationException>().WithMessage("*'Pinned.Document'*'Doc'*");
    }

    [Fact]
    public void A_document_mapped_without_a_name_is_named_by_the_rule()
    {
        var modelBuilder = new ModelBuilder();
        modelBuilder.Entity<Pinned>(builder => builder.ComplexProperty(entity => entity.Document, document => document.ToJson()));

        WorkloadsColumnNaming.Apply(modelBuilder);

        modelBuilder.Model.FindEntityType(typeof(Pinned))!.FindComplexProperty(nameof(Pinned.Document))!
            .ComplexType.GetContainerColumnName().Should().Be("document");
    }

    [Fact]
    public void Every_scalar_column_is_its_property_name_in_snake_case()
    {
        var columns = PostgresModel().GetEntityTypes()
            .Where(entity => entity.GetTableName() is not null)
            .SelectMany(entity => entity.GetProperties())
            .Select(property => (Property: $"{property.DeclaringType.ClrType.Name}.{property.Name}", Column: property.GetColumnName()))
            .ToList();

        columns.Should().NotBeEmpty();
        columns.Should().OnlyContain(
            column => column.Column == WorkloadsColumnNaming.ColumnFor(column.Property.Split('.', 2)[1]),
            "a column is named from its property and nothing else");
        columns.Should().Contain(("WorkloadInstance.GroupInstanceId", "module_instance_id"))
            .And.Contain(("RetainedVolume.GroupId", "module_id"))
            .And.Contain(("RetainedVolume.SourceGroupInstanceId", "source_module_instance_id"));
    }

    [Fact]
    public void Every_column_of_every_table_is_snake_case()
    {
        var columns = PostgresModel().GetRelationalModel().Tables
            .SelectMany(table => table.Columns.Select(column => $"{table.Name}.{column.Name}"))
            .ToList();

        columns.Should().HaveCountGreaterThan(80);
        columns.Should().OnlyContain(column => SnakeCaseIdentifier().IsMatch(column.Split('.', 2)[1]));
    }

    [Fact]
    public void Every_document_column_is_its_property_name_in_snake_case()
        => PostgresModel().GetEntityTypes()
            .SelectMany(entity => entity.GetComplexProperties())
            .Where(property => property.ComplexType.GetContainerColumnName() is not null)
            .Select(property => (property.Name, Column: property.ComplexType.GetContainerColumnName()))
            .Should().HaveCount(WorkloadsModelContract.JsonDocumentColumns.Count)
            .And.OnlyContain(document => document.Column == WorkloadsColumnNaming.SnakeCase(document.Name));

    [Fact]
    public void No_property_inside_a_document_is_renamed()
    {
        var model = PostgresModel();
        var renamed = model.GetEntityTypes()
            .SelectMany(entity => entity.GetComplexProperties())
            .Where(property => property.ComplexType.GetContainerColumnName() is not null)
            .SelectMany(property => JsonNames(property.ComplexType))
            .ToList();

        renamed.Should().NotBeEmpty();
        renamed.Should().OnlyContain(name => name.Json == name.Property, "a JSON key keeps the name it was stored under");

        var spec = model.FindEntityType(typeof(WorkloadUpdateIntent))!
            .FindComplexProperty(nameof(WorkloadUpdateIntent.TargetSpecsDocument))!.ComplexType
            .FindComplexProperty("Workloads")!.ComplexType;
        spec.FindProperty("ModuleInstanceId")!.GetJsonPropertyName().Should().Be("ModuleInstanceId");
        spec.FindProperty("Inst8")!.GetJsonPropertyName().Should().Be("Inst8");
        model.FindEntityType(typeof(WorkloadInstance))!
            .FindComplexProperty(nameof(WorkloadInstance.AttachmentPinDocument))!.ComplexType
            .FindProperty("Mode")!.GetJsonPropertyName().Should().Be("Mode");
    }

    [Fact]
    public void Both_lists_are_snake_case()
        => PostgresModel().GetEntityTypes()
            .SelectMany(entity => entity.GetProperties())
            .Where(property => property.IsPrimitiveCollection)
            .Select(property => property.GetColumnName())
            .Should().BeEquivalentTo("last_scan_failed_nodes", "adopted_resource_workload_ids");

    [Fact]
    public void Every_active_row_filter_names_only_columns_its_table_has()
    {
        var model = PostgresModel();

        foreach (var index in WorkloadsModelContract.ActiveRowIndexes)
        {
            var entity = model.FindEntityType(index.EntityType)!;
            var table = model.GetRelationalModel().FindTable(entity.GetTableName()!, entity.GetSchema())!;
            var filter = entity.GetIndexes().Single(candidate => candidate.GetDatabaseName() == index.DatabaseName).GetFilter();

            var quoted = QuotedIdentifier().Matches(filter!).Select(match => match.Groups[1].Value).ToList();

            quoted.Should().NotBeEmpty();
            quoted.Should().OnlyContain(
                column => table.FindColumn(column) != null,
                $"{index.DatabaseName}'s filter must name columns {table.Name} has");
        }
    }

    [Fact]
    public void Every_index_is_named_for_the_columns_it_covers()
        => PostgresModel().GetEntityTypes()
            .SelectMany(entity => entity.GetIndexes())
            .ToDictionary(
                index => index.GetDatabaseName()!,
                index => string.Join(",", index.Properties.Cast<IProperty>().Select(property => property.GetColumnName())))
            .Should().BeEquivalentTo(new Dictionary<string, string>
            {
                ["ix_workload_instances_module_instance_id_workload_id"] = "module_instance_id,workload_id",
                ["ix_workload_instances_state"] = "state",
                ["ix_retained_volumes_claimed_by_workload_instance_id"] = "claimed_by_workload_instance_id",
                ["ix_retained_volumes_source_workload_instance_id_volume_id"] = "source_workload_instance_id,volume_id",
                ["ix_retained_volumes_module_id_workload_id"] = "module_id,workload_id",
                ["ix_retained_volumes_retained_at_utc"] = "retained_at_utc",
                ["ux_retained_volumes_active_resource_name"] = "resource_name",
                ["ux_workload_update_intents_active_module_instance_id"] = "module_instance_id",
                ["ix_workload_update_intents_phase"] = "phase",
                ["ux_workload_update_intents_cohort_id_cohort_ordinal"] = "cohort_id,cohort_ordinal",
                ["ux_workload_update_intent_steps_intent_id_workload_id"] = "intent_id,workload_id",
            });

    private sealed class Pinned
    {
        public Guid Id { get; set; }

        public string WorkloadId { get; set; } = string.Empty;

        public PinnedDocument? Document { get; set; }
    }

    private sealed class Grouped
    {
        public Guid Id { get; set; }

        public Guid GroupInstanceId { get; set; }

        public string GroupId { get; set; } = string.Empty;

        public string WorkloadId { get; set; } = string.Empty;
    }

    private sealed class PinnedDocument
    {
        public string Value { get; set; } = string.Empty;
    }

    private static IEnumerable<(string Property, string? Json)> JsonNames(IComplexType type)
    {
        foreach (var property in type.GetProperties())
        {
            yield return (property.Name, property.GetJsonPropertyName());
        }

        foreach (var complex in type.GetComplexProperties())
        {
            yield return (complex.Name, complex.GetJsonPropertyName());
            foreach (var nested in JsonNames(complex.ComplexType))
            {
                yield return nested;
            }
        }
    }

    private static IModel PostgresModel()
    {
        var builder = new DbContextOptionsBuilder<WorkloadsDbContext>();
        builder.UseNpgsql("Host=unopened;Database=unopened");
        using var context = new WorkloadsDbContext(builder.Options, new WorkloadsStoreOptions());
        return context.GetService<IDesignTimeModel>().Model;
    }

    [GeneratedRegex("^[a-z][a-z0-9]*(_[a-z0-9]+)*$")]
    private static partial Regex SnakeCaseIdentifier();

    [GeneratedRegex("\"([^\"]+)\"")]
    private static partial Regex QuotedIdentifier();
}
