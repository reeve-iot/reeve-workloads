using System;
using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using Reeve.Workloads.Abstractions.Providers;
using Xunit;

namespace Reeve.Workloads.Abstractions.Tests;

public sealed class WorkloadLogTailTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 21, 10, 15, 30, TimeSpan.Zero);

    private static WorkloadLogLine At(int second, string text) => WorkloadLogLine.Line(text, T0.AddSeconds(second));

    private static IReadOnlyList<WorkloadLogLine> Stream(params WorkloadLogLine[] lines) => lines;

    [Fact]
    public void A_docker_prefix_with_nine_fraction_digits_is_truncated_to_100_ns_not_rounded()
    {
        var line = WorkloadLogTail.ParseTimestampedLine("2026-11-21T10:15:30.999999999Z ready on :8080");

        line.Text.Should().Be("ready on :8080");
        line.AtUtc.Should().Be(T0.AddTicks(9_999_999));
        line.AtUtc!.Value.Offset.Should().Be(TimeSpan.Zero);
    }

    [Fact]
    public void A_kubernetes_prefix_with_trimmed_fraction_and_an_offset_is_read_as_utc()
    {
        var line = WorkloadLogTail.ParseTimestampedLine("2026-11-21T12:15:30.5+02:00 started");

        line.Text.Should().Be("started");
        line.AtUtc.Should().Be(T0.AddMilliseconds(500));
        line.AtUtc!.Value.Offset.Should().Be(TimeSpan.Zero);
    }

    [Fact]
    public void A_prefix_without_fraction_keeps_the_rest_of_the_text_verbatim()
    {
        var line = WorkloadLogTail.ParseTimestampedLine("2026-11-21T10:15:30Z  two  spaces ");

        line.AtUtc.Should().Be(T0);
        line.Text.Should().Be(" two  spaces ");
    }

    [Theory]
    [InlineData("2026-11-21T10:15:30.1Z ")]
    [InlineData("2026-11-21T10:15:30.1Z")]
    public void An_empty_output_line_has_a_timestamp_and_empty_text(string raw)
    {
        var line = WorkloadLogTail.ParseTimestampedLine(raw);

        line.Text.Should().BeEmpty();
        line.AtUtc.Should().Be(T0.AddMilliseconds(100));
    }

    [Theory]
    [InlineData("plain output without a timestamp")]
    [InlineData("")]
    [InlineData("2026-13-21T10:15:30Z impossible month")]
    [InlineData("2026-11-21T10:15:30.Z empty fraction")]
    [InlineData("2026-11-21T10:15:30.1234567890Z ten fraction digits")]
    [InlineData("2026-11-21T10:15:30 no offset")]
    [InlineData("2026-11-21T10:15:30Zglued text")]
    public void A_missing_or_malformed_prefix_keeps_the_whole_line_and_no_timestamp(string raw)
    {
        var line = WorkloadLogTail.ParseTimestampedLine(raw);

        line.Text.Should().Be(raw);
        line.AtUtc.Should().BeNull();
        line.IsRestartMarker.Should().BeFalse();
        line.IsGapMarker.Should().BeFalse();
    }

    [Fact]
    public void The_first_read_returns_the_newest_lines_and_says_when_older_ones_were_left_out()
    {
        var tail = Stream(At(1, "a"), At(2, "b"), At(3, "c"));

        WorkloadLogTail.LinesAfter(tail, after: null, maxLines: 3).Should().BeEquivalentTo(
            new WorkloadLogs { Lines = tail, Truncated = false });

        var capped = WorkloadLogTail.LinesAfter(tail, after: null, maxLines: 2);
        capped.Lines.Select(l => l.Text).Should().Equal("b", "c");
        capped.Truncated.Should().BeTrue();
        capped.Lines.Should().NotContain(l => l.IsGapMarker);
    }

    [Fact]
    public void A_resumed_read_returns_only_the_lines_after_the_last_one_shown()
    {
        var first = WorkloadLogTail.LinesAfter(Stream(At(1, "a"), At(2, "b")), after: null, maxLines: 2);

        var next = WorkloadLogTail.LinesAfter(Stream(At(2, "b"), At(3, "c")), first.Lines[^1], maxLines: 2);

        next.Lines.Select(l => l.Text).Should().Equal("c");
        next.Truncated.Should().BeFalse();
    }

    [Fact]
    public void Identical_lines_stay_apart_by_timestamp_so_a_repeated_line_neither_freezes_nor_repeats()
    {
        var shown = At(2, "ping");

        var next = WorkloadLogTail.LinesAfter(Stream(At(1, "ping"), shown, At(3, "ping")), shown, maxLines: 2);

        next.Lines.Should().Equal(At(3, "ping"));
    }

    [Fact]
    public void A_read_with_nothing_new_returns_no_lines_and_no_marker()
    {
        var shown = At(2, "b");

        var next = WorkloadLogTail.LinesAfter(Stream(At(1, "a"), shown), shown, maxLines: 1);

        next.Lines.Should().BeEmpty();
        next.Truncated.Should().BeFalse();
    }

    [Fact]
    public void An_empty_read_after_lines_were_shown_is_not_a_gap()
    {
        var next = WorkloadLogTail.LinesAfter([], At(2, "b"), maxLines: 5);

        next.Lines.Should().BeEmpty();
        next.Truncated.Should().BeFalse();
    }

    [Fact]
    public void A_burst_larger_than_the_tail_starts_with_a_gap_marker_and_keeps_the_newest_lines()
    {
        var next = WorkloadLogTail.LinesAfter(Stream(At(3, "c"), At(4, "d"), At(5, "e")), At(2, "b"), maxLines: 2);

        next.Truncated.Should().BeTrue();
        next.Lines[0].IsGapMarker.Should().BeTrue();
        next.Lines[0].Text.Should().Be(WorkloadLogTail.GapMarkerText);
        next.Lines[0].AtUtc.Should().BeNull();
        next.Lines.Skip(1).Select(l => l.Text).Should().Equal("d", "e");
    }

    [Fact]
    public void A_read_longer_than_asked_with_more_new_lines_than_the_tail_still_reports_the_gap()
    {
        var shown = At(1, "a");

        var next = WorkloadLogTail.LinesAfter(Stream(shown, At(2, "b"), At(3, "c"), At(4, "d")), shown, maxLines: 2);

        next.Lines.Select(l => (l.IsGapMarker, l.Text)).Should().Equal(
            (true, WorkloadLogTail.GapMarkerText), (false, "c"), (false, "d"));
    }

    [Fact]
    public void A_rotated_log_or_replaced_container_that_no_longer_holds_the_last_line_is_a_gap()
    {
        var next = WorkloadLogTail.LinesAfter(Stream(At(9, "fresh file")), At(2, "b"), maxLines: 200);

        next.Truncated.Should().BeTrue();
        next.Lines.Select(l => (l.IsGapMarker, l.Text)).Should().Equal(
            (true, WorkloadLogTail.GapMarkerText), (false, "fresh file"));
    }

    [Fact]
    public void After_a_gap_the_next_read_resumes_from_the_last_real_line_without_another_marker()
    {
        var gap = WorkloadLogTail.LinesAfter(Stream(At(9, "x")), At(2, "b"), maxLines: 1);

        var next = WorkloadLogTail.LinesAfter(Stream(At(9, "x"), At(10, "y")), gap.Lines[^1], maxLines: 1);

        next.Lines.Should().Equal(At(10, "y"));
    }

    [Fact]
    public void A_restart_marker_can_be_the_resume_point_and_is_not_shown_twice()
    {
        var marker = WorkloadLogLine.RestartMarker("--- previous container instance (restarts: 1) ---");
        var first = WorkloadLogTail.LinesAfter(Stream(At(1, "old"), marker), At(1, "old"), maxLines: 3);

        var next = WorkloadLogTail.LinesAfter(Stream(At(1, "old"), marker, At(5, "new")), first.Lines[^1], maxLines: 3);

        first.Lines.Should().Equal(marker);
        next.Lines.Should().Equal(At(5, "new"));
    }

    [Fact]
    public void A_gap_marker_is_refused_as_the_resume_point()
    {
        var act = () => WorkloadLogTail.LinesAfter(Stream(At(1, "a")), WorkloadLogLine.GapMarker("gap"), maxLines: 1);

        act.Should().Throw<ArgumentException>().WithParameterName("after");
    }

    [Fact]
    public void A_tail_cap_below_one_is_refused()
    {
        var act = () => WorkloadLogTail.LinesAfter(Stream(At(1, "a")), after: null, maxLines: 0);

        act.Should().Throw<ArgumentOutOfRangeException>().WithParameterName("maxLines");
    }
}
