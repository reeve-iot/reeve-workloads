using Microsoft.EntityFrameworkCore;
using Reeve.Workloads.EntityFrameworkCore.Persistence;
using Reeve.Workloads.EntityFrameworkCore.PostgreSql;

[assembly: WorkloadsDatabaseConventions(WorkloadsPostgreSqlConventions.ProviderName, typeof(WorkloadsPostgreSqlConventions))]

namespace Reeve.Workloads.EntityFrameworkCore.PostgreSql;

/// <summary>The workloads model on PostgreSQL. It must come out byte-identical to what the host's migrations
/// recorded before the model went provider-agnostic, or the next migration rewrites live columns and indexes.</summary>
public sealed class WorkloadsPostgreSqlConventions : IWorkloadsDatabaseConventions
{
    /// <summary>What Npgsql reports as <c>Database.ProviderName</c>.</summary>
    public const string ProviderName = "Npgsql.EntityFrameworkCore.PostgreSQL";

    public void Apply(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        foreach (var index in WorkloadsModelContract.ActiveRowIndexes)
        {
            index.FindIn(modelBuilder).SetFilter(Filter(modelBuilder, index));
        }
    }

    private static string Filter(ModelBuilder modelBuilder, WorkloadsActiveRowIndex index)
    {
        var conditions = index.NullProperties
            .Select(property => $"{Quote(index.ColumnOf(modelBuilder, property))} IS NULL")
            .ToList();

        if (index.ExcludedProperty is { } excluded && index.ExcludedValues is { Count: > 0 } values)
        {
            var list = string.Join(", ", values.Select(value => $"'{value.Replace("'", "''", StringComparison.Ordinal)}'"));
            conditions.Add($"{Quote(index.ColumnOf(modelBuilder, excluded))} NOT IN ({list})");
        }

        return string.Join(" AND ", conditions);
    }

    private static string Quote(string identifier) => $"\"{identifier.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";
}
