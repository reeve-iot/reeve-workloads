using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Reeve.Workloads.Abstractions;
using Reeve.Workloads.Abstractions.Contracts;
using Reeve.Workloads.Abstractions.Contracts.Deployment;
using Reeve.Workloads.Abstractions.Domain;
using Reeve.Workloads.Abstractions.Domain.Entities;
using Reeve.Workloads.Abstractions.Domain.Network;

namespace Reeve.Workloads.EntityFrameworkCore.IntegrationTests;

[Collection(WorkloadsStoreCollection.Name)]
public sealed class WorkloadConcurrencyConflictEntityTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 1, 10, 0, 0, TimeSpan.Zero);

    private readonly PostgresFixture postgres;

    public WorkloadConcurrencyConflictEntityTests(PostgresFixture postgres) => this.postgres = postgres;

    private static WorkloadAttachmentPin Pin() => new()
    {
        Mode = WorkloadNetworkMode.OperatorNetwork,
        AttachmentNames = ["lan"],
    };

    [SkippableFact]
    public async Task A_conflict_on_another_pinned_row_is_named_and_the_pin_lands_once_that_row_is_re_read()
    {
        var store = await postgres.CreateStoreAsync();
        var group = Guid.NewGuid();
        var target = WorkloadInstance.Create(Guid.NewGuid(), group, "matter", "matter-server", Now);
        var other = WorkloadInstance.Create(Guid.NewGuid(), group, "matter", "otbr", Now);
        await using (var writer = store.NewContext())
        {
            writer.WorkloadInstances.AddRange(target, other);
            await writer.SaveChangesAsync();
        }

        await using var context = store.NewContext();
        var sut = new WorkloadInstanceRepository(context, NullWorkloadStatusRealtimeNotifier.Instance);
        var rows = (await sut.ListByPluginInstanceAsync(group)).ToDictionary(row => row.WorkloadId);
        rows["matter-server"].PinAttachment(Pin(), Now);
        rows["otbr"].PinAttachment(Pin(), Now);

        await using (var racer = store.NewContext())
        {
            var winner = new WorkloadInstanceRepository(racer, NullWorkloadStatusRealtimeNotifier.Instance);
            var current = (await winner.GetByIdAsync(other.Id))!;
            current.SetResolvedParameters(new Dictionary<string, string?> { ["port"] = "5541" }, Now.AddMinutes(1));
            await winner.UpdateAsync(current);
        }

        var lost = await FluentActions.Awaiting(() => sut.UpdateAsync(rows["matter-server"]))
            .Should().ThrowAsync<WorkloadConcurrencyConflictException>();
        lost.Which.EntityId.Should().Be(other.Id, "the row that lost is the one the save refused, not the one passed in");

        var fresh = (await sut.ReloadAsync(lost.Which.EntityId))!;
        fresh.PinAttachment(Pin(), Now);
        await sut.UpdateAsync(rows["matter-server"]);

        await using var check = store.NewContext();
        var stored = await check.WorkloadInstances.AsNoTracking().ToDictionaryAsync(row => row.WorkloadId);
        stored["matter-server"].AttachmentPinDocument.Should().NotBeNull();
        stored["otbr"].AttachmentPinDocument.Should().NotBeNull("the re-read row was re-pinned and flushed with the target");
        stored["otbr"].GetResolvedParameters()["port"].Should().Be("5541", "the winner's write survives the retry");
    }

    [SkippableFact]
    public async Task A_delete_that_lost_a_race_is_refused_and_succeeds_on_the_re_read_row()
    {
        var store = await postgres.CreateStoreAsync();
        var row = WorkloadInstance.Create(Guid.NewGuid(), Guid.NewGuid(), "matter", "matter-server", Now);
        await using (var writer = store.NewContext())
        {
            writer.WorkloadInstances.Add(row);
            await writer.SaveChangesAsync();
        }

        await using var context = store.NewContext();
        var sut = new WorkloadInstanceRepository(context, NullWorkloadStatusRealtimeNotifier.Instance);
        var stale = (await sut.GetByIdAsync(row.Id))!;

        await using (var racer = store.NewContext())
        {
            var winner = new WorkloadInstanceRepository(racer, NullWorkloadStatusRealtimeNotifier.Instance);
            var current = (await winner.GetByIdAsync(row.Id))!;
            current.TransitionTo(WorkloadInstanceState.Deploying, WorkloadStateReason.Provisioning, null, Now.AddMinutes(1));
            await winner.UpdateAsync(current);
        }

        var lost = await FluentActions.Awaiting(() => sut.DeleteAsync(stale))
            .Should().ThrowAsync<WorkloadConcurrencyConflictException>();
        lost.Which.EntityId.Should().Be(row.Id);

        var fresh = (await sut.ReloadAsync(row.Id))!;
        fresh.Should().NotBeSameAs(stale, "the copy whose delete lost is still tracked Deleted and carries the old token");
        fresh.State.Should().Be(WorkloadInstanceState.Deploying);
        await sut.DeleteAsync(fresh);

        await using var check = store.NewContext();
        (await check.WorkloadInstances.AsNoTracking().AnyAsync(stored => stored.Id == row.Id)).Should().BeFalse();
    }

    [SkippableFact]
    public async Task A_conflict_on_another_module_row_names_that_row()
    {
        var store = await postgres.CreateStoreAsync();
        var target = WorkloadGroup.Record("acme.thermostat", "marketplace", "1.0.0", Now);
        var other = WorkloadGroup.Record("acme.lights", "marketplace", "1.0.0", Now);
        await using (var writer = store.NewContext())
        {
            writer.WorkloadGroups.AddRange(target, other);
            await writer.SaveChangesAsync();
        }

        await using var context = store.NewContext();
        var sut = new WorkloadGroupRepository(context);
        var tracked = (await sut.GetAsync("acme.thermostat"))!;
        var stale = (await sut.GetAsync("acme.lights"))!;
        tracked.MoveTo("1.1.0", Now);
        stale.MoveTo("1.1.0", Now);

        await using (var racer = store.NewContext())
        {
            var winner = new WorkloadGroupRepository(racer);
            var current = (await winner.GetAsync("acme.lights"))!;
            current.MoveTo("1.2.0", Now.AddMinutes(1));
            await winner.UpdateAsync(current);
        }

        var lost = await FluentActions.Awaiting(() => sut.UpdateAsync(tracked))
            .Should().ThrowAsync<WorkloadConcurrencyConflictException>();
        lost.Which.EntityId.Should().Be(other.Id);
    }
}
