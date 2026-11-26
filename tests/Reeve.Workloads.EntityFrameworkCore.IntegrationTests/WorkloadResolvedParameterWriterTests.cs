using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Reeve.Workloads.Abstractions.Domain.Entities;
using Reeve.Workloads.EntityFrameworkCore;
using Reeve.Workloads.EntityFrameworkCore.Persistence;

namespace Reeve.Workloads.EntityFrameworkCore.IntegrationTests;

[Collection(WorkloadsStoreCollection.Name)]
public sealed class WorkloadResolvedParameterWriterTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 5, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid GroupInstanceId = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");
    private const string GroupId = "zwave";

    private readonly PostgresFixture postgres;

    public WorkloadResolvedParameterWriterTests(PostgresFixture postgres) => this.postgres = postgres;

    [SkippableFact]
    public async Task A_workload_id_with_surrounding_whitespace_still_writes_its_row()
    {
        await using var db = await SeededAsync("gateway");

        var written = await Writer(db).SetResolvedParametersAsync(
            GroupInstanceId,
            Map(("  gateway  ", "serialPort", "/dev/ttyUSB0")),
            Now);

        written.Should().BeGreaterThan(0);
        (await Reload(db, "gateway")).GetResolvedParameters()["serialPort"].Should().Be("/dev/ttyUSB0");
    }

    [SkippableFact]
    public async Task An_absent_workload_is_left_alone_rather_than_faulting()
    {
        await using var db = await SeededAsync("gateway");

        var act = async () => await Writer(db).SetResolvedParametersAsync(
            GroupInstanceId,
            Map(("nosuchworkload", "serialPort", "/dev/ttyUSB0")),
            Now);

        await act.Should().NotThrowAsync();
        (await Reload(db, "gateway")).GetResolvedParameters().Should().NotContainKey("serialPort");
    }

    [SkippableFact]
    public async Task An_empty_map_writes_nothing_and_reports_zero()
    {
        await using var db = await SeededAsync("gateway");

        var written = await Writer(db).SetResolvedParametersAsync(
            GroupInstanceId,
            new Dictionary<string, IReadOnlyDictionary<string, string?>>(StringComparer.Ordinal),
            Now);

        written.Should().Be(0);
    }

    [SkippableFact]
    public async Task The_typed_parameters_document_is_written_with_the_column()
    {
        var store = await postgres.CreateStoreAsync();
        await using (var seed = store.NewContext())
        {
            seed.WorkloadInstances.Add(WorkloadInstance.Create(Guid.NewGuid(), GroupInstanceId, GroupId, "gateway", Now));
            await seed.SaveChangesAsync();
        }

        await using (var db = store.NewContext())
        {
            await Writer(db).SetResolvedParametersAsync(
                GroupInstanceId, Map(("gateway", "serialPort", "/dev/ttyUSB0")), Now);
        }

        await using var reader = store.NewContext();
        var loaded = await Reload(reader, "gateway");

        loaded.ParametersDocument.Should().NotBeNull();
        loaded.ParametersDocument!.Entries.Should().ContainSingle()
            .Which.Should().BeEquivalentTo(new { Key = "serialPort", Value = "/dev/ttyUSB0" });
    }

    private static WorkloadResolvedParameterWriter Writer(WorkloadsDbContext db) => new(db);

    private async Task<WorkloadsDbContext> SeededAsync(string workloadId)
    {
        var store = await postgres.CreateStoreAsync();
        var db = store.NewContext();
        db.Set<WorkloadInstance>().Add(
            WorkloadInstance.Create(Guid.NewGuid(), GroupInstanceId, GroupId, workloadId, Now));
        await db.SaveChangesAsync();
        return db;
    }

    private static Task<WorkloadInstance> Reload(WorkloadsDbContext db, string workloadId)
        => db.Set<WorkloadInstance>().FirstAsync(i => i.WorkloadId == workloadId);

    private static Dictionary<string, IReadOnlyDictionary<string, string?>> Map(
        params (string WorkloadId, string Name, string? Value)[] entries)
    {
        var map = new Dictionary<string, IReadOnlyDictionary<string, string?>>(StringComparer.Ordinal);
        foreach (var (workloadId, name, value) in entries)
        {
            map[workloadId] = new Dictionary<string, string?>(StringComparer.Ordinal) { [name] = value };
        }

        return map;
    }
}
