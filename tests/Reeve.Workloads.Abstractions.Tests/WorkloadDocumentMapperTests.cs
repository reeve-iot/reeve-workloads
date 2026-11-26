using System.Text.Json;
using FluentAssertions;
using Reeve.Workloads.Abstractions.Contracts;
using Reeve.Workloads.Abstractions.Contracts.Deployment;
using Reeve.Workloads.Abstractions.Domain;
using Reeve.Workloads.Abstractions.Domain.Entities;
using Reeve.Workloads.Abstractions.Domain.Entities.Documents;
using Reeve.Workloads.Abstractions.Domain.Network;
using Xunit;

namespace Reeve.Workloads.Abstractions.Tests;

public sealed class WorkloadDocumentMapperTests
{
    private static readonly DateTimeOffset At = new(2026, 7, 29, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public void A_string_map_is_stored_key_sorted_and_read_back_whole()
    {
        var map = new Dictionary<string, string?> { ["zeta"] = "1", ["alpha"] = null, ["Mid"] = "é" };

        var document = WorkloadDocumentMapper.StringMap(map);

        document.Entries.Select(entry => entry.Key).Should().Equal("Mid", "alpha", "zeta");
        WorkloadDocumentMapper.FromDocument(document).Should().BeEquivalentTo(map);
    }

    [Fact]
    public void A_retained_volume_snapshot_keeps_its_order_and_names_its_enums()
    {
        var volumes = RetainedVolumes();

        var document = WorkloadDocumentMapper.RetainedVolumes(volumes);

        document.Volumes.Select(volume => volume.VolumeId).Should().Equal("state", "cache");
        document.Volumes[0].VolumeNamingScheme.Should().Be("Docker");
        document.Volumes[0].Criticality.Should().Be("Recoverable");
        WorkloadDocumentMapper.FromDocument(document).Should().BeEquivalentTo(volumes, options => options.WithStrictOrdering());
    }

    private static List<RetainedVolumeSnapshotItem> RetainedVolumes() =>
    [
        new()
        {
            VolumeId = "state",
            ResourceName = "reeve-matter-1234abcd-state",
            VolumeNamingScheme = RetainedVolumeSchemeId.Docker,
            Size = "1Gi",
            Criticality = VolumeCriticality.Recoverable,
            Description = "Fabric state",
        },
        new()
        {
            VolumeId = "cache",
            ResourceName = "reeve-matter-1234abcd-cache",
            Size = "64Mi",
            Criticality = VolumeCriticality.Cache,
        },
    ];


    [Fact]
    public void A_stored_pin_document_with_a_configDigest_key_reads_back_its_mode_and_names()
    {
        const string stored = """{"SchemaVersion":1,"Unreadable":false,"Mode":"OperatorNetwork","AttachmentNames":["iot","cam"],"ConfigDigest":"sha256:written-by-18"}""";

        var document = JsonSerializer.Deserialize<WorkloadAttachmentPinDocument>(stored, new JsonSerializerOptions(JsonSerializerDefaults.General))!;
        var pin = WorkloadDocumentMapper.FromDocument(document);

        pin.Mode.Should().Be(WorkloadNetworkMode.OperatorNetwork);
        pin.AttachmentNames.Should().Equal("iot", "cam");
    }

    [Theory]
    [InlineData("SomethingNewer")]
    [InlineData("operatorNetwork")]
    [InlineData("0")]
    [InlineData("OperatorNetwork, OperatorNetwork")]
    public void A_pin_document_whose_mode_is_not_a_member_name_does_not_read(string mode)
    {
        var document = new WorkloadAttachmentPinDocument
        {
            Mode = mode,
            AttachmentNames = ["iot"],
        };

        var read = () => WorkloadDocumentMapper.FromDocument(document);

        read.Should().Throw<FormatException>().WithMessage($"*'{mode}'*");
    }

}
