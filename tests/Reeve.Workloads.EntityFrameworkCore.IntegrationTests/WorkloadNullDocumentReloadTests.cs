using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Reeve.Workloads.Abstractions;
using Reeve.Workloads.Abstractions.Contracts;
using Reeve.Workloads.Abstractions.Contracts.Deployment;
using Reeve.Workloads.Abstractions.Domain;
using Reeve.Workloads.Abstractions.Domain.Entities;
using Reeve.Workloads.Abstractions.Domain.Network;
using Reeve.Workloads.EntityFrameworkCore.Persistence;

namespace Reeve.Workloads.EntityFrameworkCore.IntegrationTests;

[Collection(WorkloadsStoreCollection.Name)]
public sealed class WorkloadNullDocumentReloadTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 1, 10, 0, 0, TimeSpan.Zero);

    private readonly PostgresFixture postgres;

    public WorkloadNullDocumentReloadTests(PostgresFixture postgres) => this.postgres = postgres;

    private static WorkloadInstanceRepository Repository(WorkloadsDbContext context)
        => new(context, NullWorkloadStatusRealtimeNotifier.Instance);

    [SkippableFact]
    public async Task A_reload_answers_a_concurrent_write_to_an_instance_with_no_retained_volume_snapshot()
    {
        var store = await postgres.CreateStoreAsync();
        var instance = WorkloadInstance.Create(Guid.NewGuid(), Guid.NewGuid(), "matter", "matter-server", Now);
        instance.SetResolvedParameters(new Dictionary<string, string?> { ["port"] = "5540" }, Now);
        await using (var writer = store.NewContext())
        {
            writer.WorkloadInstances.Add(instance);
            await writer.SaveChangesAsync();
        }

        await using var context = store.NewContext();
        var sut = Repository(context);
        var stale = (await sut.GetByIdAsync(instance.Id))!;
        stale.RetainedVolumesSnapshotDocument.Should().BeNull();
        stale.AttachmentPinDocument.Should().BeNull();

        await using (var racer = store.NewContext())
        {
            var other = Repository(racer);
            var current = (await other.GetByIdAsync(instance.Id))!;
            current.SetResolvedParameters(new Dictionary<string, string?> { ["port"] = "5541" }, Now.AddMinutes(1));
            await other.UpdateAsync(current);
        }

        var fresh = await sut.ReloadAsync(instance.Id);

        fresh.Should().NotBeNull();
        fresh!.GetResolvedParameters()["port"].Should().Be("5541");
        fresh.Version.Should().BeGreaterThan(stale.Version, "the retry must carry the token that won, not the one that lost");
        fresh.RetainedVolumesSnapshotDocument.Should().BeNull();
        fresh.AttachmentPinDocument.Should().BeNull();
        (await sut.GetByIdAsync(instance.Id)).Should().BeSameAs(fresh, "the unit of work tracks the fresh row");
    }

    [SkippableFact]
    public async Task A_reload_of_an_instance_with_no_documents_at_all_answers_it()
    {
        var store = await postgres.CreateStoreAsync();
        var instance = WorkloadInstance.Create(Guid.NewGuid(), Guid.NewGuid(), "matter", "matter-server", Now);
        await using (var writer = store.NewContext())
        {
            writer.WorkloadInstances.Add(instance);
            await writer.SaveChangesAsync();
        }

        await using var context = store.NewContext();
        var sut = Repository(context);
        await sut.GetByIdAsync(instance.Id);

        var fresh = await sut.ReloadAsync(instance.Id);

        fresh.Should().NotBeNull();
        fresh!.ParametersDocument.Should().BeNull();
        fresh.RetainedVolumesSnapshotDocument.Should().BeNull();
        fresh.AttachmentPinDocument.Should().BeNull();
    }

    [SkippableFact]
    public async Task A_reload_of_a_row_that_is_gone_answers_null()
    {
        var store = await postgres.CreateStoreAsync();
        var instance = WorkloadInstance.Create(Guid.NewGuid(), Guid.NewGuid(), "matter", "matter-server", Now);
        await using (var writer = store.NewContext())
        {
            writer.WorkloadInstances.Add(instance);
            await writer.SaveChangesAsync();
        }

        await using var context = store.NewContext();
        var sut = Repository(context);
        await sut.GetByIdAsync(instance.Id);
        await using (var racer = store.NewContext())
        {
            await racer.WorkloadInstances.Where(row => row.Id == instance.Id).ExecuteDeleteAsync();
        }

        (await sut.ReloadAsync(instance.Id)).Should().BeNull();
    }

    [SkippableFact]
    public async Task An_instance_whose_documents_are_cleared_is_saved_queried_and_reloaded()
    {
        var store = await postgres.CreateStoreAsync();
        var instance = WorkloadInstance.Create(Guid.NewGuid(), Guid.NewGuid(), "matter", "matter-server", Now);
        instance.SetResolvedParameters(new Dictionary<string, string?> { ["port"] = "5540" }, Now);
        instance.PinAttachment(
            new WorkloadAttachmentPin
            {
                Mode = WorkloadNetworkMode.OperatorNetwork,
                AttachmentNames = ["lan"],
            },
            Now);
        instance.SnapshotRetainedVolumes(
        [
            new RetainedVolumeSnapshotItem
            {
                VolumeId = "state",
                ResourceName = "reeve-matter-1234abcd-state",
                VolumeNamingScheme = RetainedVolumeSchemeId.Docker,
                Size = "1Gi",
                Criticality = VolumeCriticality.Recoverable,
                Description = "Fabric state",
            },
        ], Now);
        await using (var writer = store.NewContext())
        {
            writer.WorkloadInstances.Add(instance);
            await writer.SaveChangesAsync();
        }

        await using (var clearing = store.NewContext())
        {
            var loaded = await clearing.WorkloadInstances.SingleAsync(row => row.Id == instance.Id);
            loaded.SetResolvedParameters(null, Now);
            loaded.PinAttachment(null, Now);
            loaded.SnapshotRetainedVolumes(null, Now);
            await clearing.SaveChangesAsync();
        }

        await using var context = store.NewContext();
        var sut = Repository(context);
        await sut.GetByIdAsync(instance.Id);

        var fresh = await sut.ReloadAsync(instance.Id);

        fresh!.ParametersDocument.Should().BeNull();
        fresh.AttachmentPinDocument.Should().BeNull();
        fresh.RetainedVolumesSnapshotDocument.Should().BeNull();
    }

    [SkippableFact]
    public async Task A_pin_write_that_loses_a_race_is_retried_through_reload_on_a_row_with_no_snapshot()
    {
        var store = await postgres.CreateStoreAsync();
        var instance = WorkloadInstance.Create(Guid.NewGuid(), Guid.NewGuid(), "matter", "matter-server", Now);
        instance.SetResolvedParameters(new Dictionary<string, string?> { ["port"] = "5540" }, Now);
        await using (var writer = store.NewContext())
        {
            writer.WorkloadInstances.Add(instance);
            await writer.SaveChangesAsync();
        }

        await using var context = store.NewContext();
        var sut = Repository(context);
        var row = (await sut.GetByIdAsync(instance.Id))!;

        await using (var racer = store.NewContext())
        {
            var other = Repository(racer);
            var current = (await other.GetByIdAsync(instance.Id))!;
            current.SetResolvedParameters(new Dictionary<string, string?> { ["port"] = "5541" }, Now.AddMinutes(1));
            await other.UpdateAsync(current);
        }

        var pin = new WorkloadAttachmentPin
        {
            Mode = WorkloadNetworkMode.OperatorNetwork,
            AttachmentNames = ["lan"],
        };
        row.PinAttachment(pin, Now);
        var lost = () => sut.UpdateAsync(row);
        await lost.Should().ThrowAsync<WorkloadConcurrencyConflictException>();

        var fresh = (await sut.ReloadAsync(instance.Id))!;
        fresh.PinAttachment(pin, Now);
        await sut.UpdateAsync(fresh);
        await context.SaveChangesAsync();

        await using var check = store.NewContext();
        var stored = await check.WorkloadInstances.AsNoTracking().SingleAsync(r => r.Id == instance.Id);
        stored.GetResolvedParameters()["port"].Should().Be("5541");
        stored.AttachmentPinDocument.Should().NotBeNull();
        stored.RetainedVolumesSnapshotDocument.Should().BeNull();
    }
}
