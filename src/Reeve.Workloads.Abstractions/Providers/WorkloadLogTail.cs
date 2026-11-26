using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace Reeve.Workloads.Abstractions.Providers;

/// <summary>Turns repeated <see cref="IWorkloadProvider.GetRecentLogsAsync"/> reads into a live tail that neither repeats nor
/// silently drops lines, and parses the timestamp prefix Docker and Kubernetes write when asked for timestamps.</summary>
public static partial class WorkloadLogTail
{
    /// <summary>The text of the marker <see cref="LinesAfter"/> puts where lines may be missing.</summary>
    public const string GapMarkerText =
        "--- log lines may be missing here: the runtime no longer holds the last line shown (more output than the tail, a rotated log or a replaced container) ---";

    /// <summary>The lines of <paramref name="tail"/> that come after <paramref name="after"/>, at most <paramref name="maxLines"/>.
    /// <para><paramref name="tail"/> is one provider read, oldest first, asked for <paramref name="maxLines"/> + 1 lines;
    /// <paramref name="after"/> is the last line, other than a gap marker, of what the caller has shown so far, or null for
    /// the first read. The newest line equal to <paramref name="after"/> (text, timestamp and marker flags) marks where the
    /// caller stopped, so lines with identical text stay apart by their runtime timestamps.</para>
    /// <para>With <paramref name="after"/> null the result is the newest <paramref name="maxLines"/> lines, Truncated when
    /// older ones were left out. When <paramref name="tail"/> no longer contains <paramref name="after"/>, or more than
    /// <paramref name="maxLines"/> lines follow it, the result starts with a gap marker
    /// (<see cref="WorkloadLogLine.IsGapMarker"/>), holds the newest <paramref name="maxLines"/> lines and is Truncated. An
    /// empty <paramref name="tail"/> (no output yet, or a read that failed) yields no lines and no marker; the caller keeps
    /// its <paramref name="after"/>.</para>
    /// <para>Two lines with the same text and the same timestamp to 100 ns are one position: a read that lands between
    /// them hides the second one. A provider without line timestamps leaves identical lines apart only by position, so
    /// a tail of it can stall on a repeated line.</para></summary>
    public static WorkloadLogs LinesAfter(IReadOnlyList<WorkloadLogLine> tail, WorkloadLogLine? after, int maxLines)
    {
        ArgumentNullException.ThrowIfNull(tail);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxLines, 1);
        if (after is { IsGapMarker: true })
        {
            throw new ArgumentException("Pass the last line shown that is not a gap marker.", nameof(after));
        }

        if (after is null)
        {
            return tail.Count <= maxLines
                ? new WorkloadLogs { Lines = tail.ToList() }
                : new WorkloadLogs { Lines = tail.Skip(tail.Count - maxLines).ToList(), Truncated = true };
        }

        if (tail.Count == 0)
        {
            return WorkloadLogs.Empty();
        }

        var position = LastIndexOf(tail, after);
        var fresh = tail.Skip(position + 1).ToList();
        if (position >= 0 && fresh.Count <= maxLines)
        {
            return new WorkloadLogs { Lines = fresh };
        }

        var lines = new List<WorkloadLogLine>(Math.Min(fresh.Count, maxLines) + 1) { WorkloadLogLine.GapMarker(GapMarkerText) };
        lines.AddRange(fresh.Skip(Math.Max(0, fresh.Count - maxLines)));
        return new WorkloadLogs { Lines = lines, Truncated = true };
    }

    /// <summary>Splits one raw runtime line of the form <c>2026-11-21T10:15:30.123456789Z text</c> (an RFC 3339 timestamp, a
    /// space, then the text, as Docker and Kubernetes write it when asked for timestamps) into its text and its UTC time,
    /// with the fraction truncated to 100 ns. A line without such a prefix, or with an impossible date, keeps its whole
    /// text and a null <see cref="WorkloadLogLine.AtUtc"/>.</summary>
    public static WorkloadLogLine ParseTimestampedLine(string raw)
    {
        ArgumentNullException.ThrowIfNull(raw);

        var match = TimestampPrefix().Match(raw);
        if (!match.Success)
        {
            return WorkloadLogLine.Line(raw);
        }

        var offset = match.Groups["offset"].Value;
        if (!DateTimeOffset.TryParseExact(
                match.Groups["seconds"].Value + (offset is "Z" or "z" ? "+00:00" : offset),
                "yyyy-MM-dd'T'HH:mm:sszzz",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var seconds))
        {
            return WorkloadLogLine.Line(raw);
        }

        var fraction = match.Groups["fraction"].Value;
        var ticks = fraction.Length == 0
            ? 0
            : long.Parse(fraction.Length >= 7 ? fraction[..7] : fraction.PadRight(7, '0'), NumberStyles.None, CultureInfo.InvariantCulture);

        return WorkloadLogLine.Line(raw[match.Length..], seconds.AddTicks(ticks).ToUniversalTime());
    }

    private static int LastIndexOf(IReadOnlyList<WorkloadLogLine> tail, WorkloadLogLine line)
    {
        for (var i = tail.Count - 1; i >= 0; i--)
        {
            if (tail[i] == line)
            {
                return i;
            }
        }

        return -1;
    }

    [GeneratedRegex(
        @"\A(?<seconds>\d{4}-\d{2}-\d{2}[Tt]\d{2}:\d{2}:\d{2})(?:\.(?<fraction>\d{1,9}))?(?<offset>[Zz]|[+-]\d{2}:\d{2})(?: |\z)",
        RegexOptions.CultureInvariant)]
    private static partial Regex TimestampPrefix();
}
