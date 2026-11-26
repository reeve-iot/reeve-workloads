using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Reeve.Workloads.Abstractions.Domain.Entities;

namespace Reeve.Workloads.EntityFrameworkCore.IntegrationTests;

[Collection(WorkloadsStoreCollection.Name)]
public sealed class WorkloadStateReasonColumnTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 26, 12, 0, 0, TimeSpan.Zero);

    private const string Schema = "workloads";

    private const string ReadReasonSql =
        "SELECT state_reason AS \"Value\" FROM \"" + Schema + "\".workload_instances WHERE id = {0}";

    private const string SeedReasonSql =
        "UPDATE \"" + Schema + "\".workload_instances SET state_reason = {1} WHERE id = {0}";

    private readonly PostgresFixture postgres;

    public WorkloadStateReasonColumnTests(PostgresFixture postgres) => this.postgres = postgres;

    [SkippableTheory]
    [InlineData("RunningVersionBehind", WorkloadStateReason.RunningVersionBehind)]
    [InlineData("HealthLost", WorkloadStateReason.HealthLost)]
    public async Task A_stored_name_reads_back_as_its_member(string stored, WorkloadStateReason expected)
    {
        var store = await postgres.CreateStoreAsync();
        var id = await SeedAsync(store);

        await using (var seeder = store.NewContext())
        {
            seeder.Model.GetDefaultSchema().Should().Be(Schema, "the raw SQL names the schema");
            (await seeder.Database.ExecuteSqlRawAsync(SeedReasonSql, id, stored)).Should().Be(1);
        }

        await using var reader = store.NewContext();
        (await reader.WorkloadInstances.SingleAsync(row => row.Id == id)).StateReason.Should().Be(expected);
    }

    [SkippableFact]
    public async Task Running_version_behind_is_written_as_its_own_name_and_found_by_a_filter()
    {
        var store = await postgres.CreateStoreAsync();
        var id = await SeedAsync(store);

        await using (var writer = store.NewContext())
        {
            var row = await writer.WorkloadInstances.SingleAsync(instance => instance.Id == id);
            row.TransitionTo(row.State, WorkloadStateReason.RunningVersionBehind, "the running version is behind", Now);
            await writer.SaveChangesAsync();
        }

        await using var reader = store.NewContext();
        (await reader.Database.SqlQueryRaw<string>(ReadReasonSql, id).SingleAsync()).Should().Be("RunningVersionBehind");
        (await reader.WorkloadInstances.CountAsync(row => row.StateReason == WorkloadStateReason.RunningVersionBehind))
            .Should().Be(1, "a filter on the member compares against the stored string");
    }

    private static async Task<Guid> SeedAsync(WorkloadsStore store)
    {
        var instance = WorkloadInstance.Create(Guid.NewGuid(), Guid.NewGuid(), "zwave", "zwave-js", Now);
        await using var writer = store.NewContext();
        writer.WorkloadInstances.Add(instance);
        await writer.SaveChangesAsync();
        return instance.Id;
    }
}
