using FluentAssertions;
using Reeve.Pipeline;
using Reeve.Workloads.Abstractions.Domain.Entities.Documents;
using Xunit;

namespace Reeve.Workloads.Abstractions.Tests;

public sealed class PipelineJournalDocumentTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 22, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public void A_fresh_journal_round_trips()
    {
        var journal = PipelineJournal.New();

        var read = WorkloadDocumentMapper.FromDocument(WorkloadDocumentMapper.Journal(journal));

        read.Should().BeEquivalentTo(journal);
    }

    [Fact]
    public void A_forward_walk_round_trips_with_its_entries_in_order()
    {
        var journal = new PipelineJournal(
            [
                new StageEntry("stage.params", T0, PipelineDirection.Forward, StageEntryState.Done, null),
                new StageEntry("stage.sidecar", T0.AddSeconds(5), PipelineDirection.Forward, StageEntryState.Done, null),
                new StageEntry("stage.health-gate", T0.AddSeconds(9), PipelineDirection.Forward, StageEntryState.Working, "2 of 3 healthy"),
            ],
            PipelineDirection.Forward,
            PipelineState.Running);

        var read = WorkloadDocumentMapper.FromDocument(WorkloadDocumentMapper.Journal(journal));

        read.Should().BeEquivalentTo(journal, options => options.WithStrictOrdering());
    }

    [Fact]
    public void A_walk_caught_mid_compensation_round_trips()
    {
        var journal = new PipelineJournal(
            [
                new StageEntry("stage.params", T0, PipelineDirection.Forward, StageEntryState.Done, null),
                new StageEntry("stage.sidecar", T0.AddSeconds(5), PipelineDirection.Forward, StageEntryState.Failed, "provider refused"),
                new StageEntry("stage.sidecar", T0.AddSeconds(6), PipelineDirection.Compensating, StageEntryState.Working, "re-applying 2 of 3"),
            ],
            PipelineDirection.Compensating,
            PipelineState.Running);

        var read = WorkloadDocumentMapper.FromDocument(WorkloadDocumentMapper.Journal(journal));

        read.Should().BeEquivalentTo(journal, options => options.WithStrictOrdering());
        read.Direction.Should().Be(PipelineDirection.Compensating);
        read.IsTerminal.Should().BeFalse();
    }

    [Theory]
    [InlineData(PipelineState.Completed)]
    [InlineData(PipelineState.Compensated)]
    [InlineData(PipelineState.Failed)]
    public void A_terminal_journal_round_trips_as_terminal(PipelineState state)
    {
        var journal = new PipelineJournal(
            [new StageEntry("stage.params", T0, PipelineDirection.Forward, StageEntryState.Done, null)],
            PipelineDirection.Forward,
            state);

        var read = WorkloadDocumentMapper.FromDocument(WorkloadDocumentMapper.Journal(journal));

        read.State.Should().Be(state);
        read.IsTerminal.Should().BeTrue();
    }

    [Fact]
    public void A_stage_id_this_build_does_not_register_survives_the_round_trip()
    {
        var journal = new PipelineJournal(
            [new StageEntry("stage.host.plugin-roll", T0, PipelineDirection.Forward, StageEntryState.Working, "waiting for replica-2")],
            PipelineDirection.Forward,
            PipelineState.Running);

        var read = WorkloadDocumentMapper.FromDocument(WorkloadDocumentMapper.Journal(journal));

        read.Entries.Single().StageId.Should().Be("stage.host.plugin-roll");
        read.Entries.Single().Detail.Should().Be("waiting for replica-2");
    }

    [Fact]
    public void An_unreadable_journal_is_a_decision_and_never_an_empty_walk()
    {
        var unreadable = new PipelineJournalDocument { Unreadable = true };

        var reading = WorkloadDocumentRead.Decide(unreadable, WorkloadDocumentMapper.FromDocument);

        reading.Verdict.Should().Be(WorkloadDocumentVerdict.Unreadable);
        reading.Value.Should().BeNull();
    }

    [Fact]
    public void A_journal_whose_enum_name_this_build_cannot_parse_is_unreadable_and_not_absent()
    {
        var written = new PipelineJournalDocument
        {
            Direction = "SidewaysInSomeFutureRelease",
            State = nameof(PipelineState.Running),
        };

        var reading = WorkloadDocumentRead.Decide(written, WorkloadDocumentMapper.FromDocument);

        reading.Verdict.Should().Be(WorkloadDocumentVerdict.Unreadable,
            "something IS recorded here and cannot be read, which is not the same as nothing having been staged");
    }

    [Fact]
    public void No_document_at_all_is_absent()
    {
        var reading = WorkloadDocumentRead.Decide<PipelineJournalDocument, PipelineJournal>(
            null, WorkloadDocumentMapper.FromDocument);

        reading.Verdict.Should().Be(WorkloadDocumentVerdict.Absent);
    }
}
