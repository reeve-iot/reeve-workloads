using FluentAssertions;
using Reeve.Workloads.Abstractions.Contracts;
using Reeve.Workloads.Abstractions.Contracts.Deployment;
using Reeve.Workloads.Abstractions.Domain;
using Reeve.Workloads.Abstractions.Domain.Entities;
using Reeve.Workloads.Abstractions.Domain.Entities.Documents;
using Reeve.Workloads.Abstractions.Domain.Network;
using Xunit;

namespace Reeve.Workloads.Abstractions.Tests;

public sealed class WorkloadJsonDocumentWriteTests
{
    private static readonly DateTimeOffset Created = new(2026, 7, 29, 10, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Later = Created.AddMinutes(5);

    [Fact]
    public void Resolved_parameters_are_written_as_a_document_and_read_back_alike()
    {
        var instance = Instance();

        instance.SetResolvedParameters(new Dictionary<string, string?> { ["port"] = "5540", ["token"] = null }, Later);

        instance.ParametersDocument.Should().NotBeNull();
        WorkloadDocumentMapper.FromDocument(instance.ParametersDocument!)
            .Should().BeEquivalentTo(instance.GetResolvedParameters());
    }

    [Fact]
    public void Clearing_the_parameters_clears_the_document()
    {
        var instance = Instance();
        instance.SetResolvedParameters(new Dictionary<string, string?> { ["port"] = "5540" }, Created);

        instance.SetResolvedParameters(new Dictionary<string, string?>(), Later);

        instance.ParametersDocument.Should().BeNull();
        instance.GetResolvedParameters().Should().BeEmpty();
    }

    [Fact]
    public void A_scan_run_records_its_failed_nodes_and_a_new_run_clears_them()
    {
        var instance = Instance();

        instance.CompleteScanRun([" node-b ", "", "node-a"], Later);

        instance.LastScanFailedNodes.Should().Equal(instance.GetLastScanFailedNodes());
        instance.LastScanFailedNodes.Should().Equal("node-b", "node-a");

        instance.BeginScanRun("run-2", Later);

        instance.LastScanFailedNodes.Should().BeNull();
    }

    [Fact]
    public void A_clean_scan_run_records_nothing()
    {
        var instance = Instance();

        instance.CompleteScanRun([], Later);

        instance.LastScanFailedNodes.Should().BeNull();
    }

    [Fact]
    public void A_retained_volume_snapshot_is_written_and_an_empty_one_clears_it()
    {
        var instance = Instance();

        instance.SnapshotRetainedVolumes([Volume("state")], Later);

        WorkloadDocumentMapper.FromDocument(instance.RetainedVolumesSnapshotDocument!)
            .Should().BeEquivalentTo(instance.GetRetainedVolumesSnapshot());

        instance.SnapshotRetainedVolumes([], Later);

        instance.RetainedVolumesSnapshotDocument.Should().BeNull();
    }

    private static WorkloadInstance Instance()
        => WorkloadInstance.Create(Guid.NewGuid(), Guid.NewGuid(), "matter", "matter-server", Created);

    private static RetainedVolumeSnapshotItem Volume(string id) => new()
    {
        VolumeId = id,
        ResourceName = $"reeve-matter-1234abcd-{id}",
        Size = "1Gi",
        Criticality = VolumeCriticality.Recoverable,
    };

}
