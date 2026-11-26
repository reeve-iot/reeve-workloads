namespace Reeve.Workloads.EntityFrameworkCore.Persistence;

/// <summary>Where the workloads model is stored. Get-only, not configured: the library ships its own migrations, and a generated migration names its schema literally. A host
/// that set a different value would move its tables to one schema and point the model at another, and boot green.
/// </summary>
/// <remarks>Registered by <c>AddEntityFrameworkStores</c>. A host does not construct one: the library configures the context.</remarks>
public sealed class WorkloadsStoreOptions
{
    /// <summary>The database schema holding the workload tables. They were born in `integration`, under Reeve's
    /// own context; reaching this value is a `SET SCHEMA` run in a maintenance window.</summary>
    public string Schema => "workloads";

    /// <summary>This context's migrations history, applied to the context by the same registration that points it
    /// at the library's migrations assembly. Two contexts sharing one history table each read the other's rows as
    /// their own applied migrations, and then neither applies anything.</summary>
    public string MigrationsHistoryTable => "__WorkloadsMigrationsHistory";
}
