using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Reeve.Workloads.EntityFrameworkCore.Persistence;

internal static class WorkloadDocumentStringColumns
{
    internal static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.General)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private static readonly MethodInfo MapOne = typeof(WorkloadDocumentStringColumns)
        .GetMethod(nameof(Map), BindingFlags.NonPublic | BindingFlags.Static)!;

    public static void MapAll(ModelBuilder modelBuilder)
    {
        foreach (var column in WorkloadsModelContract.JsonDocumentColumns)
        {
            var declared = column.EntityType.GetProperty(column.PropertyName)
                ?? throw new InvalidOperationException(
                    $"'{column.EntityType.Name}' has no property '{column.PropertyName}' to map as a document.");
            MapOne.MakeGenericMethod(Nullable.GetUnderlyingType(declared.PropertyType) ?? declared.PropertyType)
                .Invoke(null, [modelBuilder, column.EntityType, column.PropertyName]);
        }
    }

    private static void Map<TDocument>(ModelBuilder modelBuilder, Type entityType, string propertyName)
        where TDocument : class
    {
        var converter = new ValueConverter<TDocument?, string?>(
            document => document == null ? null : JsonSerializer.Serialize(document, Options),
            column => column == null ? null : JsonSerializer.Deserialize<TDocument>(column, Options));

        var comparer = new ValueComparer<TDocument?>(
            (left, right) => (left == null ? null : JsonSerializer.Serialize(left, Options))
                == (right == null ? null : JsonSerializer.Serialize(right, Options)),
            document => document == null
                ? 0
                : JsonSerializer.Serialize(document, Options).GetHashCode(StringComparison.Ordinal),
            document => document == null
                ? null
                : JsonSerializer.Deserialize<TDocument>(JsonSerializer.Serialize(document, Options), Options));

        modelBuilder.Entity(entityType)
            .Property(typeof(TDocument), propertyName)
            .HasConversion(converter)
            .Metadata.SetValueComparer(comparer);
    }
}
