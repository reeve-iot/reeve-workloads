namespace Reeve.Workloads.Abstractions;

/// <summary>Brings the workloads schema up to date. Supplied by whichever persistence package the host composed;
/// the engine knows only that someone can do it, never how, and never that it is EF.</summary>
public interface IWorkloadsSchemaMigrator
{
    Task MigrateAsync(CancellationToken cancellationToken = default);
}
