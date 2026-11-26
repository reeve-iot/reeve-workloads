using System.Reflection;
using FluentAssertions;
using Reeve.Workloads.Abstractions.Contracts;
using Reeve.Workloads.Abstractions.Domain.Entities;
using Reeve.Workloads.Abstractions.Domain.Entities.Documents;
using Reeve.Workloads.Abstractions.Domain.Network;
using Xunit;

namespace Reeve.Workloads.Abstractions.Tests;

public sealed class WorkloadAttachmentPinTests
{
    private static readonly DateTimeOffset At = new(2026, 9, 17, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Later = At.AddMinutes(5);

    private static WorkloadAttachmentPin Pin(params string[] names) => new()
    {
        Mode = WorkloadNetworkMode.OperatorNetwork,
        AttachmentNames = names,
    };

    private static WorkloadInstance Instance() => WorkloadInstance.Create(Guid.NewGuid(), Guid.NewGuid(), "zwave", "gateway", At);

    [Fact]
    public void A_row_that_was_never_pinned_reads_absent()
        => Instance().ReadAttachmentPin().Verdict.Should().Be(WorkloadDocumentVerdict.Absent);

    [Fact]
    public void A_pin_reads_back_with_its_names_in_declared_order()
    {
        var instance = Instance();

        instance.PinAttachment(Pin("iot", "cam"), Later);

        var read = instance.ReadAttachmentPin();
        read.Verdict.Should().Be(WorkloadDocumentVerdict.Value);
        read.Value!.Mode.Should().Be(WorkloadNetworkMode.OperatorNetwork);
        read.Value.AttachmentNames.Should().Equal("iot", "cam");
        instance.UpdatedAtUtc.Should().Be(Later);
    }

    [Fact]
    public void Pinning_null_clears_the_pin()
    {
        var instance = Instance();
        instance.PinAttachment(Pin("iot"), At);

        instance.PinAttachment(null, Later);

        instance.ReadAttachmentPin().Verdict.Should().Be(WorkloadDocumentVerdict.Absent);
        instance.AttachmentPinDocument.Should().BeNull();
    }

    [Fact]
    public void A_pin_is_not_part_of_the_desired_spec_so_pinning_leaves_the_hashes_alone()
    {
        var instance = Instance();
        instance.SetDesiredSpecHash("desired", At);
        instance.SetAppliedSpecHash("applied", At);

        instance.PinAttachment(Pin("iot"), Later);

        instance.DesiredSpecHash.Should().Be("desired");
        instance.AppliedSpecHash.Should().Be("applied", "pinning must not restart a workload");
    }

    [Fact]
    public void An_unreadable_pin_document_reads_unreadable_never_absent()
    {
        var instance = Instance();
        Set(instance, new WorkloadAttachmentPinDocument { Unreadable = true });

        instance.ReadAttachmentPin().Verdict.Should().Be(WorkloadDocumentVerdict.Unreadable);
    }

    [Fact]
    public void A_pin_document_naming_a_mode_that_does_not_exist_reads_unreadable()
    {
        var instance = Instance();
        Set(instance, new WorkloadAttachmentPinDocument
        {
            Mode = "SomethingElse",
            AttachmentNames = ["iot"],
        });

        instance.ReadAttachmentPin().Verdict.Should().Be(WorkloadDocumentVerdict.Unreadable);
    }

    public static TheoryData<string[]> NamelessPins => new()
    {
        Array.Empty<string>(),
        new[] { "iot", " " },
    };

    [Theory]
    [MemberData(nameof(NamelessPins))]
    public void A_pin_without_names_is_refused(string[] names)
        => FluentActions.Invoking(() => Instance().PinAttachment(
                new WorkloadAttachmentPin { Mode = WorkloadNetworkMode.OperatorNetwork, AttachmentNames = names },
                At))
            .Should().Throw<ArgumentException>();

    private static void Set(WorkloadInstance instance, WorkloadAttachmentPinDocument document)
        => typeof(WorkloadInstance)
            .GetProperty(nameof(WorkloadInstance.AttachmentPinDocument), BindingFlags.Instance | BindingFlags.Public)!
            .SetValue(instance, document);
}
