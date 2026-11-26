using System.Diagnostics;
using FluentAssertions;
using Reeve.Workloads.Abstractions.Domain;
using Reeve.Workloads.Domain;

namespace Reeve.Workloads.Abstractions.Tests;

public sealed class WorkloadPathGlobTests
{
    [Fact]
    public void Literal_pattern_matches_only_the_exact_path()
    {
        WorkloadPathGlob.IsMatch("/dev/ttyUSB0", "/dev/ttyUSB0").Should().BeTrue();
        WorkloadPathGlob.IsMatch("/dev/ttyUSB0", "/dev/ttyUSB1").Should().BeFalse();
    }

    [Fact]
    public void Null_or_empty_path_never_matches()
    {
        WorkloadPathGlob.IsMatch("/dev/*", null).Should().BeFalse();
        WorkloadPathGlob.IsMatch("/dev/*", "").Should().BeFalse();
    }

    [Fact]
    public void Empty_pattern_never_matches()
    {
        WorkloadPathGlob.IsMatch("", "/dev/ttyUSB0").Should().BeFalse();
    }

    [Fact]
    public void Single_star_matches_within_a_segment()
    {
        WorkloadPathGlob.IsMatch("/dev/ttyUSB*", "/dev/ttyUSB0").Should().BeTrue();
        WorkloadPathGlob.IsMatch("/dev/ttyUSB*", "/dev/ttyUSB42").Should().BeTrue();
        WorkloadPathGlob.IsMatch("/dev/*", "/dev/ttyUSB0").Should().BeTrue();
    }

    [Fact]
    public void Single_star_matches_zero_characters()
    {
        WorkloadPathGlob.IsMatch("/dev/ttyUSB*", "/dev/ttyUSB").Should().BeTrue();
    }

    [Fact]
    public void Single_star_does_not_cross_a_slash()
    {
        WorkloadPathGlob.IsMatch("/dev/*", "/dev/bus/usb").Should().BeFalse();
    }

    [Fact]
    public void Single_star_in_the_middle_stays_intra_segment()
    {
        WorkloadPathGlob.IsMatch("/dev/tty*0", "/dev/ttyUSB0").Should().BeTrue();
        WorkloadPathGlob.IsMatch("/dev/tty*0", "/dev/tty/USB0").Should().BeFalse();
    }

    [Fact]
    public void Double_star_matches_within_a_single_segment()
    {
        WorkloadPathGlob.IsMatch("/run/**", "/run/dbus").Should().BeTrue();
    }

    [Fact]
    public void Double_star_crosses_segment_boundaries()
    {
        WorkloadPathGlob.IsMatch("/run/**", "/run/user/1000/bus").Should().BeTrue();
    }

    [Fact]
    public void Double_star_in_the_middle_spans_segments()
    {
        WorkloadPathGlob.IsMatch("/run/**/bus", "/run/user/1000/bus").Should().BeTrue();
        WorkloadPathGlob.IsMatch("/run/**/bus", "/run/a/bus").Should().BeTrue();
        WorkloadPathGlob.IsMatch("/run/**/bus", "/run/bus").Should().BeFalse();
    }

    [Fact]
    public void Question_mark_matches_exactly_one_non_separator_char()
    {
        WorkloadPathGlob.IsMatch("/dev/ttyUSB?", "/dev/ttyUSB0").Should().BeTrue();
        WorkloadPathGlob.IsMatch("/dev/ttyUSB?", "/dev/ttyUSB").Should().BeFalse();
        WorkloadPathGlob.IsMatch("/dev/ttyUSB?", "/dev/ttyUSB01").Should().BeFalse();
    }

    [Fact]
    public void Question_mark_does_not_match_a_separator()
    {
        WorkloadPathGlob.IsMatch("/dev?ttyUSB0", "/dev/ttyUSB0").Should().BeFalse();
    }

    [Fact]
    public void Matching_is_anchored_a_prefix_pattern_does_not_match_a_longer_path()
    {
        WorkloadPathGlob.IsMatch("/dev/tty", "/dev/ttyUSB0").Should().BeFalse();
    }

    [Fact]
    public void Trailing_single_star_matches_rest_of_final_segment_only()
    {
        WorkloadPathGlob.IsMatch("/dev/*", "/dev/").Should().BeTrue();
        WorkloadPathGlob.IsMatch("/dev/*", "/dev/a/b").Should().BeFalse();
    }

    [Fact]
    public void A_pathological_many_star_pattern_completes_quickly_when_it_does_NOT_match()
    {
        var pattern = string.Concat(Enumerable.Repeat("a*", 20)) + "b";
        var text = new string('a', 200);

        var sw = Stopwatch.StartNew();
        var matched = WorkloadPathGlob.IsMatch(pattern, text);
        sw.Stop();

        matched.Should().BeFalse("the pattern demands a trailing 'b' the text never provides");
        sw.Elapsed.Should().BeLessThan(TimeSpan.FromMilliseconds(500),
            "the matcher must be near-linear — a pathological many-star pattern must not backtrack exponentially");
    }

    [Fact]
    public void A_pathological_many_star_pattern_still_matches_correctly_and_quickly()
    {
        var pattern = string.Concat(Enumerable.Repeat("a*", 20)) + "z";
        var text = new string('a', 300) + "z";

        var sw = Stopwatch.StartNew();
        var matched = WorkloadPathGlob.IsMatch(pattern, text);
        sw.Stop();

        matched.Should().BeTrue();
        sw.Elapsed.Should().BeLessThan(TimeSpan.FromMilliseconds(500));
    }
}
