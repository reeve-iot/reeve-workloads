using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Reeve.Workloads.Abstractions.Domain.Entities;
using Reeve.Workloads.EntityFrameworkCore.Persistence;

namespace Reeve.Workloads.Composition.Tests.EntityFrameworkCore;

public sealed class WorkloadsAgnosticStoreTests
{
    public static TheoryData<string> EntitiesCarryingDocuments()
    {
        var data = new TheoryData<string>();
        foreach (var name in WorkloadsModelContract.JsonDocumentColumns
            .Select(column => column.EntityType.AssemblyQualifiedName!)
            .Distinct(StringComparer.Ordinal))
        {
            data.Add(name);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(EntitiesCarryingDocuments))]
    public void An_entity_carrying_documents_can_be_read_back_from_a_store_without_json(string entityTypeName)
    {
        using var context = AgnosticContext();

        var act = () => Query(context, Type.GetType(entityTypeName)!).Cast<object>().ToList();

        act.Should().NotThrow();
    }

    [Fact]
    public void An_instance_can_be_queried_by_its_state_reason_on_a_store_without_json()
    {
        using var context = AgnosticContext();

        var act = () => context.Set<WorkloadInstance>()
            .Where(instance => instance.StateReason == WorkloadStateReason.RunningVersionBehind)
            .ToList();

        act.Should().NotThrow();
    }

    [Fact]
    public void The_state_reason_column_maps_to_a_string_column()
    {
        using var context = AgnosticContext();

        var property = context.Model.FindEntityType(typeof(WorkloadInstance))!
            .FindProperty(nameof(WorkloadInstance.StateReason))!;

        property.GetProviderClrType().Should().Be(typeof(string));
        property.GetMaxLength().Should().Be(64);
    }

    private static IQueryable Query(DbContext context, Type entityType)
        => (IQueryable)typeof(DbContext).GetMethod(nameof(DbContext.Set), Type.EmptyTypes)!
            .MakeGenericMethod(entityType)
            .Invoke(context, null)!;

    private static WorkloadsDbContext AgnosticContext()
    {
        var builder = new DbContextOptionsBuilder<WorkloadsDbContext>();
        builder.UseInMemoryDatabase(Guid.NewGuid().ToString("n"));
        return new WorkloadsDbContext(builder.Options, new WorkloadsStoreOptions());
    }
}
