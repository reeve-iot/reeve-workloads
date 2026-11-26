using FluentAssertions;
using Reeve.Workloads.Abstractions.Domain.Entities;
using Reeve.Workloads.Abstractions.Domain.Entities.Documents;

namespace Reeve.Workloads.Abstractions.Tests.Documents;

public sealed class WorkloadDocumentReadTests
{
    [Fact]
    public void A_document_that_maps_is_the_one_read()
    {
        var document = WorkloadDocumentMapper.StringMap(
            new Dictionary<string, string?>(StringComparer.Ordinal) { ["serialPort"] = "/dev/ttyUSB0" });

        var read = WorkloadDocumentRead.Read(document, WorkloadDocumentMapper.FromDocument, Empty);

        read.Should().ContainKey("serialPort").WhoseValue.Should().Be("/dev/ttyUSB0");
    }

    [Fact]
    public void No_document_reads_as_the_degraded_value()
    {
        var read = WorkloadDocumentRead.Read<WorkloadStringMapDocument, IReadOnlyDictionary<string, string?>>(
            null, WorkloadDocumentMapper.FromDocument, Empty);

        read.Should().BeEmpty();
    }

    [Fact]
    public void An_unreadable_document_carries_no_values_so_it_reads_as_the_degraded_value()
    {
        var document = new WorkloadStringMapDocument { Unreadable = true };

        var read = WorkloadDocumentRead.Read(document, WorkloadDocumentMapper.FromDocument, Empty);

        read.Should().BeEmpty("an unreadable document is empty, and mapping it would invent values");
    }

    [Fact]
    public void A_document_whose_inverse_throws_degrades_rather_than_faulting()
    {
        var document = Renamed();

        IReadOnlyList<RetainedVolumeSnapshotItem> read = null!;
        var act = () => read = WorkloadDocumentRead.Read<RetainedVolumesSnapshotDocument, IReadOnlyList<RetainedVolumeSnapshotItem>>(
            document, WorkloadDocumentMapper.FromDocument, Array.Empty<RetainedVolumeSnapshotItem>);

        act.Should().NotThrow();
        read.Should().BeEmpty();
    }

    [Fact]
    public void Decide_answers_from_the_document_when_it_maps()
    {
        var document = WorkloadDocumentMapper.StringMap(
            new Dictionary<string, string?>(StringComparer.Ordinal) { ["serialPort"] = "/dev/ttyUSB0" });

        var read = WorkloadDocumentRead.Decide(document, WorkloadDocumentMapper.FromDocument);

        read.Verdict.Should().Be(WorkloadDocumentVerdict.Value);
        read.Value.Should().ContainKey("serialPort").WhoseValue.Should().Be("/dev/ttyUSB0");
    }

    [Fact]
    public void Decide_reports_no_document_as_absent()
    {
        var read = WorkloadDocumentRead.Decide<WorkloadStringMapDocument, IReadOnlyDictionary<string, string?>>(
            null, WorkloadDocumentMapper.FromDocument);

        read.Verdict.Should().Be(WorkloadDocumentVerdict.Absent);
        read.Value.Should().BeNull("nothing was read");
    }

    [Fact]
    public void Decide_reports_an_unreadable_document_as_such()
    {
        var document = new WorkloadStringMapDocument { Unreadable = true };

        var read = WorkloadDocumentRead.Decide(document, WorkloadDocumentMapper.FromDocument);

        read.Verdict.Should().Be(WorkloadDocumentVerdict.Unreadable);
    }

    [Fact]
    public void Decide_reports_a_faulting_inverse_as_unreadable_rather_than_absent()
    {
        var act = () => WorkloadDocumentRead.Decide(Renamed(), WorkloadDocumentMapper.FromDocument);

        act.Should().NotThrow().Which.Verdict.Should().Be(WorkloadDocumentVerdict.Unreadable);
    }

    private static RetainedVolumesSnapshotDocument Renamed() => new()
    {
        Volumes =
        [
            new RetainedVolumeSnapshotDocument
            {
                VolumeId = "v1",
                ResourceName = "r1",
                VolumeNamingScheme = "kubernetes",
                Criticality = "NoSuchCriticality",
            },
        ],
    };

    private static IReadOnlyDictionary<string, string?> Empty()
        => new Dictionary<string, string?>(StringComparer.Ordinal);
}
