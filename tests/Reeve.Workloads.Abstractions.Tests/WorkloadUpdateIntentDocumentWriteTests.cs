using System.Reflection;
using FluentAssertions;
using Reeve.Workloads.Abstractions.Contracts;
using Reeve.Workloads.Abstractions.Contracts.Deployment;
using Reeve.Workloads.Abstractions.Domain;
using Reeve.Workloads.Abstractions.Domain.Entities;
using Reeve.Workloads.Abstractions.Domain.Entities.Documents;
using Reeve.Workloads.Abstractions.Update;
using Xunit;
using static Reeve.Workloads.Abstractions.Tests.WorkloadUpdateDocumentFixtures;

namespace Reeve.Workloads.Abstractions.Tests;

public sealed class WorkloadUpdateIntentDocumentWriteTests
{
    private static readonly string[] IntentDocuments =
    [
        nameof(WorkloadUpdateIntent.TargetSpecsDocument),
        nameof(WorkloadUpdateIntent.PreviousSpecsDocument),
        nameof(WorkloadUpdateIntent.PreviousParametersDocument),
        nameof(WorkloadUpdateIntent.OperatorParametersDocument),
        nameof(WorkloadUpdateIntent.AdoptedResourceWorkloadIds),
        nameof(WorkloadUpdateIntent.FromDigestsDocument),
        nameof(WorkloadUpdateIntent.ToDigestsDocument),
        nameof(WorkloadUpdateIntent.EvidenceDocument),
    ];

    [Fact]
    public void Staging_writes_every_snapshot_as_the_document_its_reader_reads()
    {
        var intent = Staged();

        intent.ReadTargetSpecs().Value.Should().BeEquivalentTo(Specs());
        intent.ReadPreviousSpecs().Value.Should().ContainKey("matter").And.HaveCount(1);
        intent.ReadPreviousParameters()!["zigbee"]
            .Should().BeEquivalentTo(new Dictionary<string, string?> { ["port"] = "5540", ["token"] = null });
        intent.ReadOperatorParameters().Should().BeEquivalentTo(new Dictionary<string, string?> { ["port"] = "5541" });
        intent.GetAdoptedResourceWorkloadIds().Should().Equal("zigbee");
        intent.ReadFromDigests()["zigbee"].Should().Be("sha256:from");
        intent.ReadToDigests()["zigbee"].Should().Be("sha256:to");
        intent.EvidenceDocument.Should().BeNull("nothing has been captured yet");
    }

    [Fact]
    public void Staging_nothing_leaves_every_document_null()
    {
        var intent = New();

        intent.StageSnapshots(new WorkloadUpdateSnapshots(), At);

        foreach (var document in IntentDocuments)
        {
            Get(intent, document).Should().BeNull(document);
        }
    }

    [Fact]
    public void An_unstaged_snapshot_reads_as_absent_rather_than_unreadable()
    {
        var intent = New();

        intent.StageSnapshots(new WorkloadUpdateSnapshots(), At);

        intent.ReadTargetSpecs().Verdict.Should().Be(WorkloadDocumentVerdict.Absent);
        intent.ReadPreviousSpecs().Verdict.Should().Be(WorkloadDocumentVerdict.Absent);
    }

    [Fact]
    public void A_settled_intent_drops_the_values_nothing_reads_again()
    {
        var intent = Staged();

        intent.Fail("the gate failed", At);
        intent.MarkRollbackFailed("the old image is gone", At);

        intent.OperatorParametersDocument!.Entries.Should().BeEmpty();
        intent.AdoptedResourceWorkloadIds.Should().BeNull();
        intent.PreviousParametersDocument!.Workloads.Should().BeEmpty();
        intent.TargetSpecsDocument.Should().NotBeNull("only what nothing reads again is dropped");
    }

    [Fact]
    public void Captured_evidence_is_written_as_a_document_and_reads_back()
    {
        var intent = Staged();
        intent.Fail("the gate failed", At);

        intent.CaptureEvidence(
            new WorkloadUpdateEvidence("2.0.0", At, [new WorkloadEvidenceCapture("zigbee", ["boom"], [], null)]));

        intent.EvidenceDocument!.ToVersion.Should().Be("2.0.0");
        intent.ReadEvidence()!.Workloads.Should().ContainSingle()
            .Which.PreviousContainerLogTail.Should().Equal("boom");
    }

    [Fact]
    public void A_recorded_stage_journal_is_written_as_a_document_and_reads_back()
    {
        var intent = Staged();
        var journal = new Reeve.Pipeline.PipelineJournal(
            [
                new Reeve.Pipeline.StageEntry(
                    "stage.params", At, Reeve.Pipeline.PipelineDirection.Forward, Reeve.Pipeline.StageEntryState.Done, null),
                new Reeve.Pipeline.StageEntry(
                    "stage.sidecar", At.AddSeconds(5), Reeve.Pipeline.PipelineDirection.Forward,
                    Reeve.Pipeline.StageEntryState.Failed, "apply of 'zigbee' failed"),
                new Reeve.Pipeline.StageEntry(
                    "stage.sidecar", At.AddSeconds(9), Reeve.Pipeline.PipelineDirection.Compensating,
                    Reeve.Pipeline.StageEntryState.Working, "restoring 'zigbee'"),
            ],
            Reeve.Pipeline.PipelineDirection.Compensating,
            Reeve.Pipeline.PipelineState.Running);

        intent.RecordStageJournal(journal);

        intent.StageJournalDocument.Should().NotBeNull();
        var read = intent.ReadStageJournal();
        read.Verdict.Should().Be(WorkloadDocumentVerdict.Value);
        read.Value.Should().BeEquivalentTo(journal, options => options.WithStrictOrdering());
    }

    [Fact]
    public void An_intent_the_walk_never_ticked_reads_its_journal_as_absent()
    {
        var intent = Staged();

        intent.StageJournalDocument.Should().BeNull();
        intent.ReadStageJournal().Verdict.Should().Be(WorkloadDocumentVerdict.Absent);
    }

    [Fact]
    public void Recording_a_journal_moves_the_concurrency_token()
    {
        var intent = Staged();
        var before = intent.Version;

        intent.RecordStageJournal(Reeve.Pipeline.PipelineJournal.New());

        intent.Version.Should().Be(before + 1);
    }

    [Fact]
    public void Capturing_no_evidence_clears_the_document()
    {
        var intent = Staged();
        intent.Fail("the gate failed", At);
        intent.CaptureEvidence(new WorkloadUpdateEvidence("2.0.0", At, []));

        intent.CaptureEvidence(null);

        intent.EvidenceDocument.Should().BeNull();
    }

    [Fact]
    public void The_previous_parameters_document_holds_one_parameter_map_per_workload()
    {
        var previous = new Dictionary<string, IReadOnlyDictionary<string, string?>>(StringComparer.Ordinal)
        {
            ["zigbee"] = new Dictionary<string, string?> { ["port"] = "5540", ["token"] = null },
            ["matter"] = new Dictionary<string, string?> { ["port"] = "5541" },
        };

        var intent = New();
        intent.StageSnapshots(new WorkloadUpdateSnapshots { PreviousParameters = previous }, At);

        intent.PreviousParametersDocument!.Unreadable.Should().BeFalse();
        intent.ReadPreviousParameters().Should().BeEquivalentTo(previous);
    }

    [Fact]
    public void A_gate_clock_is_written_as_a_document_and_a_null_one_clears_it()
    {
        var step = Step();

        step.SetGateClockState(
            new WorkloadGateClockState { ClockStartedAtUtc = At, AccumulatedPause = TimeSpan.FromSeconds(10) }, At);

        step.GateClockStateDocument!.ClockStartedAtUtc.Should().Be(At);
        step.ReadPriorClockState().AccumulatedPause.Should().Be(TimeSpan.FromSeconds(10));

        step.SetGateClockState(null, At);

        step.GateClockStateDocument.Should().BeNull();
    }

    [Fact]
    public void A_step_with_no_clock_reads_the_pristine_state()
        => Step().ReadPriorClockState().Should().Be(new WorkloadGateClockState());

    private static WorkloadUpdateIntent New()
        => WorkloadUpdateIntent.Create(Guid.NewGuid(), Guid.NewGuid(), "1.0.0", "2.0.0", At);

    private static WorkloadUpdateIntentStep Step()
        => WorkloadUpdateIntentStep.Create(Guid.NewGuid(), Guid.NewGuid(), "zigbee", At);

    private static WorkloadUpdateIntent Staged()
    {
        var previous = Specs();
        previous.Remove("zigbee");

        var intent = New();
        intent.StageSnapshots(
            new WorkloadUpdateSnapshots
            {
                TargetSpecs = Specs(),
                PreviousSpecs = previous,
                PreviousParameters = new Dictionary<string, IReadOnlyDictionary<string, string?>>(StringComparer.Ordinal)
                {
                    ["zigbee"] = new Dictionary<string, string?> { ["port"] = "5540", ["token"] = null },
                },
                FromDigests = new Dictionary<string, string?>(StringComparer.Ordinal) { ["zigbee"] = "sha256:from" },
                ToDigests = new Dictionary<string, string?>(StringComparer.Ordinal) { ["zigbee"] = "sha256:to" },
                OperatorParameters = new Dictionary<string, string?>(StringComparer.Ordinal) { ["port"] = "5541" },
                AdoptedResourceWorkloadIds = ["zigbee"],
            },
            At);
        return intent;
    }

    private static object? Get(object entity, string property)
        => entity.GetType().GetProperty(property, BindingFlags.Instance | BindingFlags.Public)!.GetValue(entity);
}
