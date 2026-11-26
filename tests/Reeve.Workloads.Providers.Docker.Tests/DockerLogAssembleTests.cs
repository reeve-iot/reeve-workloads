using System;
using System.Linq;
using FluentAssertions;
using Reeve.Workloads.Providers.Docker;
using Xunit;

namespace Reeve.Workloads.Providers.Docker.Tests;

public sealed class DockerLogAssembleTests
{
    [Fact]
    public void Timestamped_daemon_lines_carry_their_time_and_the_restart_marker_has_none()
    {
        var logs = DockerLogReader.Assemble(
            currentLog: "2026-11-21T10:15:31.000000100Z cur-1\n",
            previousLog: "2026-11-21T10:15:30.500000000Z prev-1\nunstamped\n",
            previousSpecHash: "abcdef0123456789",
            includePrevious: true,
            tailLines: 10);

        logs.Lines.Select(l => l.Text).Should().Equal(
            "prev-1", "unstamped", "--- previous container instance (spec-hash: abcdef012345) ---", "cur-1");
        logs.Lines.Select(l => l.AtUtc).Should().Equal(
            new DateTimeOffset(2026, 11, 21, 10, 15, 30, 500, TimeSpan.Zero),
            null,
            null,
            new DateTimeOffset(2026, 11, 21, 10, 15, 31, TimeSpan.Zero).AddTicks(1));
        logs.Lines[2].IsRestartMarker.Should().BeTrue();
    }
}
