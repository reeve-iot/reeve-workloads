using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;

namespace Reeve.Workloads.EntityFrameworkCore.Persistence;

internal static class WorkloadsColumnNaming
{
    private static readonly IReadOnlyDictionary<string, string> NamedBeforeTheGroup = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["GroupInstanceId"] = "module_instance_id",
        ["GroupId"] = "module_id",
        ["SourceGroupInstanceId"] = "source_module_instance_id",
    };

    internal static void Apply(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        foreach (var entity in modelBuilder.Model.GetEntityTypes())
        {
            foreach (var property in entity.GetDeclaredProperties())
            {
                if (property.FindAnnotation(RelationalAnnotationNames.ColumnName)?.Value is string pinned)
                {
                    throw Pinned(entity.ClrType.Name, property.Name, pinned);
                }

                property.SetColumnName(ColumnFor(property.Name));
            }

            foreach (var document in entity.GetDeclaredComplexProperties())
            {
                if (document.ComplexType.GetContainerColumnName() is not { } container)
                {
                    continue;
                }

                if (!string.Equals(container, document.Name, StringComparison.Ordinal))
                {
                    throw Pinned(entity.ClrType.Name, document.Name, container);
                }

                document.ComplexType.SetContainerColumnName(SnakeCase(document.Name));
            }
        }
    }

    internal static string ColumnFor(string propertyName)
        => NamedBeforeTheGroup.TryGetValue(propertyName, out var column) ? column : SnakeCase(propertyName);

    private static InvalidOperationException Pinned(string entity, string property, string column) => new(
        $"'{entity}.{property}' is mapped to the column '{column}', but every workloads column is named from its "
        + "property by rule. Remove the explicit column name; the rule names it "
        + $"'{ColumnFor(property)}'.");

    internal static string SnakeCase(string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);

        var snake = new StringBuilder(name.Length + 8);
        for (var i = 0; i < name.Length; i++)
        {
            var current = name[i];
            if (char.IsUpper(current) && i > 0)
            {
                var previous = name[i - 1];
                var next = i + 1 < name.Length ? name[i + 1] : '\0';
                if (char.IsLower(previous) || char.IsDigit(previous) || (char.IsUpper(previous) && char.IsLower(next)))
                {
                    snake.Append('_');
                }
            }

            snake.Append(char.ToLowerInvariant(current));
        }

        return snake.ToString();
    }
}
