using System.Reflection;
using FluentAssertions;
using Reeve.Workloads.Abstractions.Contracts.Deployment;
using Reeve.Workloads.Abstractions.Domain.Entities;
using Reeve.Workloads.Abstractions.Domain.Entities.Documents;

namespace Reeve.Workloads.Abstractions.Tests.Documents;

public sealed class WorkloadInstanceDocumentReadTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 12, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void The_document_is_what_the_parameter_read_answers_from()
    {
        var instance = WorkloadInstance.Create(Guid.NewGuid(), Guid.NewGuid(), "zwave", "gateway", Now);
        Set(instance, nameof(WorkloadInstance.ParametersDocument), new WorkloadStringMapDocument
        {
            Entries = [new WorkloadStringPairDocument { Key = "serialPort", Value = "/dev/FROM-DOCUMENT" }],
        });

        instance.GetResolvedParameters()["serialPort"].Should().Be("/dev/FROM-DOCUMENT");
    }

    [Fact]
    public void An_unreadable_parameter_document_degrades_to_no_parameters()
    {
        var instance = WorkloadInstance.Create(Guid.NewGuid(), Guid.NewGuid(), "zwave", "gateway", Now);
        Set(instance, nameof(WorkloadInstance.ParametersDocument), new WorkloadStringMapDocument { Unreadable = true });

        instance.GetResolvedParameters().Should().BeEmpty();
    }

    [Fact]
    public void A_row_that_never_resolved_a_parameter_reads_as_none()
        => WorkloadInstance.Create(Guid.NewGuid(), Guid.NewGuid(), "zwave", "gateway", Now)
            .GetResolvedParameters().Should().BeEmpty();

    [Fact]
    public void The_document_is_what_the_volume_snapshot_read_answers_from()
    {
        var instance = SeededVolumes();

        instance.GetRetainedVolumesSnapshot().Single().ResourceName.Should().Be("pvc-state");
    }

    [Fact]
    public void A_volume_document_naming_an_enum_member_that_no_longer_exists_degrades_to_no_volumes()
    {
        var instance = SeededVolumes();
        Set(instance, nameof(WorkloadInstance.RetainedVolumesSnapshotDocument), new RetainedVolumesSnapshotDocument
        {
            Volumes = [Doc("FROM-DOCUMENT", "kubernetes", criticality: "NoSuchCriticalityAnyMore")],
        });

        var read = () => instance.GetRetainedVolumesSnapshot();

        read.Should().NotThrow();
        read().Should().BeEmpty();
    }

    [Fact]
    public void A_naming_scheme_this_build_has_never_heard_of_reads_back_rather_than_degrading()
    {
        var instance = SeededVolumes();
        Set(instance, nameof(WorkloadInstance.RetainedVolumesSnapshotDocument), new RetainedVolumesSnapshotDocument
        {
            Volumes = [Doc("nomad-vol-state", "nomad")],
        });

        instance.GetRetainedVolumesSnapshot().Single().VolumeNamingScheme
            .Should().Be(RetainedVolumeSchemeId.Parse("nomad"));
    }

    [Fact]
    public void A_scheme_that_is_not_an_id_at_all_still_degrades()
    {
        var instance = SeededVolumes();
        Set(instance, nameof(WorkloadInstance.RetainedVolumesSnapshotDocument), new RetainedVolumesSnapshotDocument
        {
            Volumes = [Doc("FROM-DOCUMENT", "not a scheme id")],
        });

        instance.GetRetainedVolumesSnapshot().Should().BeEmpty();
    }

    private static RetainedVolumeSnapshotDocument Doc(
        string resourceName, string scheme, string? criticality = null) => new()
    {
        VolumeId = "state",
        ResourceName = resourceName,
        VolumeNamingScheme = scheme,
        Size = "512Mi",
        Criticality = criticality ?? nameof(VolumeCriticality.Critical),
    };

    private static WorkloadInstance SeededVolumes()
    {
        var instance = WorkloadInstance.Create(Guid.NewGuid(), Guid.NewGuid(), "zwave", "gateway", Now);
        instance.SnapshotRetainedVolumes(
            [
                new RetainedVolumeSnapshotItem
                {
                    VolumeId = "state",
                    ResourceName = "pvc-state",
                    Size = "512Mi",
                    Criticality = VolumeCriticality.Critical,
                },
            ],
            Now);
        return instance;
    }

    private static void Set(object entity, string property, object? value)
        => entity.GetType().GetProperty(property, BindingFlags.Instance | BindingFlags.Public)!.SetValue(entity, value);
}
