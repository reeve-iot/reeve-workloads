using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Reeve.Workloads.Abstractions;
using Reeve.Workloads.Abstractions.Domain.Entities;
using Reeve.Workloads.EntityFrameworkCore;
using Reeve.Workloads.EntityFrameworkCore.Persistence;

namespace Reeve.Workloads.Composition.Tests.EntityFrameworkCore;

public sealed class WorkloadGroupRepositoryTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 17, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task A_recorded_module_reads_back()
    {
        var name = Guid.NewGuid().ToString("n");
        await using (var db = NewContext(name))
        {
            await new WorkloadGroupRepository(db).TryAddAsync(WorkloadGroup.Record("zigbee2mqtt", "repo-1", "1.2.0", Now));
        }

        await using var reader = NewContext(name);
        var read = await new WorkloadGroupRepository(reader).GetAsync("zigbee2mqtt");

        read.Should().NotBeNull();
        read!.SourceKey.Should().Be("repo-1");
        read.InstalledVersion.Should().Be("1.2.0");
        read.CreatedAtUtc.Should().Be(Now);
    }

    [Theory]
    [InlineData("zigbee2mqtt")]
    [InlineData("  ZIGBEE2MQTT  ")]
    public async Task A_module_is_found_trimmed_and_without_case(string spelled)
    {
        var name = Guid.NewGuid().ToString("n");
        await using var db = NewContext(name);
        var repository = new WorkloadGroupRepository(db);
        await repository.TryAddAsync(WorkloadGroup.Record("ZigBee2MQTT", "repo-1", "1.2.0", Now));
        db.ChangeTracker.Clear();

        (await repository.GetAsync(spelled)).Should().NotBeNull(
            "module ids are matched without case everywhere, and a miss here reads as a module with no version");
    }

    [Fact]
    public async Task A_second_row_for_the_same_module_is_not_added()
    {
        await using var db = NewContext(Guid.NewGuid().ToString("n"));
        var repository = new WorkloadGroupRepository(db);
        (await repository.TryAddAsync(WorkloadGroup.Record("zigbee2mqtt", "repo-1", "1.2.0", Now))).Should().BeTrue();

        (await repository.TryAddAsync(WorkloadGroup.Record("ZIGBEE2MQTT", "repo-2", "9.9.9", Now))).Should().BeFalse(
            "every replica adopts at boot, and the loser must learn it lost rather than write a second row");

        (await db.WorkloadGroups.CountAsync()).Should().Be(1);
        (await repository.GetAsync("zigbee2mqtt"))!.SourceKey.Should().Be("repo-1");
    }

    [Fact]
    public async Task Another_module_is_not_found()
    {
        await using var db = NewContext(Guid.NewGuid().ToString("n"));
        var repository = new WorkloadGroupRepository(db);
        await repository.TryAddAsync(WorkloadGroup.Record("zigbee2mqtt", "repo-1", "1.2.0", Now));

        (await repository.GetAsync("zigbee")).Should().BeNull();
        (await repository.GetAsync(" ")).Should().BeNull();
    }

    [Fact]
    public async Task A_moved_version_is_saved_and_advances_the_token()
    {
        var name = Guid.NewGuid().ToString("n");
        await using (var seed = NewContext(name))
        {
            await new WorkloadGroupRepository(seed).TryAddAsync(WorkloadGroup.Record("zigbee2mqtt", "repo-1", "1.2.0", Now));
        }

        await using (var writer = NewContext(name))
        {
            var repository = new WorkloadGroupRepository(writer);
            var row = (await repository.GetAsync("zigbee2mqtt"))!;
            var before = row.Version;
            row.MoveTo("1.3.0", Now.AddMinutes(1));
            await repository.UpdateAsync(row);
            row.Version.Should().Be(before + 1);
        }

        await using var reader = NewContext(name);
        (await new WorkloadGroupRepository(reader).GetAsync("zigbee2mqtt"))!.InstalledVersion.Should().Be("1.3.0");
    }

    [Fact]
    public async Task A_reinstalled_row_saves_its_source_and_version()
    {
        var name = Guid.NewGuid().ToString("n");
        await using (var seed = NewContext(name))
        {
            await new WorkloadGroupRepository(seed).TryAddAsync(WorkloadGroup.Record("zigbee2mqtt", "repo-1", "1.2.0", Now));
        }

        await using (var writer = NewContext(name))
        {
            var repository = new WorkloadGroupRepository(writer);
            var row = (await repository.GetAsync("zigbee2mqtt"))!;
            row.Reinstall("repo-2", "2.0.0", Now.AddMinutes(1));
            await repository.UpdateAsync(row);
        }

        await using var reader = NewContext(name);
        var read = (await new WorkloadGroupRepository(reader).GetAsync("zigbee2mqtt"))!;
        read.SourceKey.Should().Be("repo-2");
        read.InstalledVersion.Should().Be("2.0.0");
        read.UpdatedAtUtc.Should().Be(Now.AddMinutes(1));
    }

    [Fact]
    public async Task The_second_writer_of_the_same_row_loses()
    {
        var name = Guid.NewGuid().ToString("n");
        await using (var seed = NewContext(name))
        {
            await new WorkloadGroupRepository(seed).TryAddAsync(WorkloadGroup.Record("zigbee2mqtt", "repo-1", "1.2.0", Now));
        }

        await using var first = NewContext(name);
        await using var second = NewContext(name);
        var byFirst = (await new WorkloadGroupRepository(first).GetAsync("zigbee2mqtt"))!;
        var bySecond = (await new WorkloadGroupRepository(second).GetAsync("zigbee2mqtt"))!;

        byFirst.MoveTo("1.3.0", Now);
        await new WorkloadGroupRepository(first).UpdateAsync(byFirst);

        bySecond.MoveTo("1.4.0", Now);
        var act = () => new WorkloadGroupRepository(second).UpdateAsync(bySecond);

        (await act.Should().ThrowAsync<WorkloadConcurrencyConflictException>())
            .Which.EntityId.Should().Be(bySecond.Id);
    }

    [Fact]
    public async Task The_version_is_mapped_as_a_concurrency_token()
    {
        await using var db = NewContext(Guid.NewGuid().ToString("n"));

        db.Model.FindEntityType(typeof(WorkloadGroup))!.FindProperty(nameof(WorkloadGroup.Version))!
            .IsConcurrencyToken.Should().BeTrue();
    }

    private static WorkloadsDbContext NewContext(string databaseName)
    {
        var builder = new DbContextOptionsBuilder<WorkloadsDbContext>();
        builder.UseInMemoryDatabase(databaseName);
        return new WorkloadsDbContext(builder.Options, new WorkloadsStoreOptions());
    }
}
