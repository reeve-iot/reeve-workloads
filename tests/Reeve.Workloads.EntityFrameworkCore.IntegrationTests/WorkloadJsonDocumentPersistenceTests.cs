using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Reeve.Workloads.Abstractions.Contracts;
using Reeve.Workloads.Abstractions.Contracts.Deployment;
using Reeve.Workloads.Abstractions.Domain;
using Reeve.Workloads.Abstractions.Domain.Entities;
using Reeve.Workloads.Abstractions.Domain.Network;

namespace Reeve.Workloads.EntityFrameworkCore.IntegrationTests;

[Collection(WorkloadsStoreCollection.Name)]
public sealed class WorkloadJsonDocumentPersistenceTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 29, 10, 0, 0, TimeSpan.Zero);

    private readonly PostgresFixture postgres;

    public WorkloadJsonDocumentPersistenceTests(PostgresFixture postgres) => this.postgres = postgres;

    [SkippableFact]
    public async Task An_instance_reads_back_every_document_it_wrote()
    {
        var store = await postgres.CreateStoreAsync();
        var instance = WorkloadInstance.Create(Guid.NewGuid(), Guid.NewGuid(), "matter", "matter-server", Now);
        instance.SetResolvedParameters(new Dictionary<string, string?> { ["port"] = "5540", ["token"] = null }, Now);
        instance.CompleteScanRun(["node-b", "node-a"], Now);
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

        await using var reader = store.NewContext();
        var loaded = await reader.WorkloadInstances.SingleAsync(row => row.Id == instance.Id);

        loaded.ParametersDocument.Should().BeEquivalentTo(instance.ParametersDocument);
        loaded.LastScanFailedNodes.Should().Equal("node-b", "node-a");
        loaded.RetainedVolumesSnapshotDocument.Should().BeEquivalentTo(instance.RetainedVolumesSnapshotDocument);
    }

    [SkippableFact]
    public async Task An_instance_with_nothing_recorded_reads_back_no_documents()
    {
        var store = await postgres.CreateStoreAsync();
        var instance = WorkloadInstance.Create(Guid.NewGuid(), Guid.NewGuid(), "matter", "matter-server", Now);

        await using (var writer = store.NewContext())
        {
            writer.WorkloadInstances.Add(instance);
            await writer.SaveChangesAsync();
        }

        await using var reader = store.NewContext();
        var loaded = await reader.WorkloadInstances.SingleAsync(row => row.Id == instance.Id);

        loaded.ParametersDocument.Should().BeNull();
        loaded.LastScanFailedNodes.Should().BeNull();
        loaded.RetainedVolumesSnapshotDocument.Should().BeNull();
    }

    [SkippableFact]
    public async Task Clearing_a_document_is_written_as_a_null()
    {
        var store = await postgres.CreateStoreAsync();
        var instance = WorkloadInstance.Create(Guid.NewGuid(), Guid.NewGuid(), "matter", "matter-server", Now);
        instance.SetResolvedParameters(new Dictionary<string, string?> { ["port"] = "5540" }, Now);

        await using (var writer = store.NewContext())
        {
            writer.WorkloadInstances.Add(instance);
            await writer.SaveChangesAsync();
            instance.SetResolvedParameters(null, Now.AddMinutes(1));
            await writer.SaveChangesAsync();
        }

        await using var reader = store.NewContext();
        var loaded = await reader.WorkloadInstances.SingleAsync(row => row.Id == instance.Id);

        loaded.ParametersDocument.Should().BeNull();
        loaded.GetResolvedParameters().Should().BeEmpty();
    }
}
