using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Reeve.Workloads.Abstractions.Domain.Entities;
using Reeve.Workloads.EntityFrameworkCore;
using Reeve.Workloads.EntityFrameworkCore.Persistence;

namespace Reeve.Workloads.Composition.Tests.EntityFrameworkCore;

public sealed class WorkloadInstanceConcurrencyTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 12, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid GroupInstanceId = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");

    [Fact]
    public void The_version_is_mapped_as_a_concurrency_token()
    {
        using var db = NewContext(Guid.NewGuid().ToString("n"));

        var version = db.Model.FindEntityType(typeof(WorkloadInstance))!.FindProperty(nameof(WorkloadInstance.Version))!;

        version.IsConcurrencyToken.Should().BeTrue(
            "the value assertions below would pass on a plain column while nothing actually guarded the row");
    }

    [Fact]
    public async Task A_write_advances_the_token()
    {
        var name = Guid.NewGuid().ToString("n");
        await using var db = await SeededAsync(name);
        var instance = await db.Set<WorkloadInstance>().FirstAsync();
        var before = instance.Version;

        instance.SetResolvedParameters(new Dictionary<string, string?> { ["serialPort"] = "/dev/ttyUSB0" }, Now);
        await db.SaveChangesAsync();

        instance.Version.Should().Be(before + 1,
            "a token that never moves is a column, not a guard — nobody would ever lose a race on it");
    }

    [Fact]
    public async Task A_save_that_changes_nothing_leaves_the_token_alone()
    {
        var name = Guid.NewGuid().ToString("n");
        await using var db = await SeededAsync(name);
        var instance = await db.Set<WorkloadInstance>().FirstAsync();
        var before = instance.Version;

        await db.SaveChangesAsync();

        instance.Version.Should().Be(before,
            "the no-op re-attach contract: an UpdateAsync that changed nothing must not invalidate everyone else's row");
    }

    [Fact]
    public async Task The_second_writer_of_the_same_row_loses()
    {
        var name = Guid.NewGuid().ToString("n");
        await using (var seed = await SeededAsync(name)) { }

        await using var leader = NewContext(name);
        await using var request = NewContext(name);
        var byLeader = await leader.Set<WorkloadInstance>().FirstAsync();
        var byRequest = await request.Set<WorkloadInstance>().FirstAsync();

        byLeader.SetResolvedParameters(new Dictionary<string, string?> { ["serialPort"] = "/dev/ttyLEADER" }, Now);
        await leader.SaveChangesAsync();

        byRequest.SetResolvedParameters(new Dictionary<string, string?> { ["serialPort"] = "/dev/ttyOPERATOR" }, Now);

        await FluentActions.Awaiting(() => request.SaveChangesAsync())
            .Should().ThrowAsync<DbUpdateConcurrencyException>(
                "the operator read the row before the leader wrote it, so their write is over a row that no longer exists as read");
    }

    [Fact]
    public async Task A_stale_leader_loses_to_the_one_that_took_over()
    {
        var name = Guid.NewGuid().ToString("n");
        await using (var seed = await SeededAsync(name)) { }

        await using var stale = NewContext(name);
        var heldByStale = await stale.Set<WorkloadInstance>().FirstAsync();

        await using (var successor = NewContext(name))
        {
            var row = await successor.Set<WorkloadInstance>().FirstAsync();
            row.SetResolvedParameters(new Dictionary<string, string?> { ["serialPort"] = "/dev/ttySUCCESSOR" }, Now);
            await successor.SaveChangesAsync();
        }

        heldByStale.SetResolvedParameters(new Dictionary<string, string?> { ["serialPort"] = "/dev/ttySTALE" }, Now);

        await FluentActions.Awaiting(() => stale.SaveChangesAsync())
            .Should().ThrowAsync<DbUpdateConcurrencyException>();
    }

    private static WorkloadsDbContext NewContext(string databaseName)
    {
        var builder = new DbContextOptionsBuilder<WorkloadsDbContext>();
        builder.UseInMemoryDatabase(databaseName);
        return new WorkloadsDbContext(builder.Options, new WorkloadsStoreOptions());
    }

    private static async Task<WorkloadsDbContext> SeededAsync(string databaseName)
    {
        var db = NewContext(databaseName);
        db.Set<WorkloadInstance>().Add(
            WorkloadInstance.Create(Guid.NewGuid(), GroupInstanceId, "zwave", "gateway", Now));
        await db.SaveChangesAsync();
        return db;
    }
}
