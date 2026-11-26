namespace Reeve.Workloads.EntityFrameworkCore.IntegrationTests;

[CollectionDefinition(Name)]
public sealed class WorkloadsStoreCollection : ICollectionFixture<PostgresFixture>
{
    public const string Name = "workloads-postgres";
}
