using Docker.DotNet.Models;
using FluentAssertions;
using Reeve.Workloads.Providers.Docker;
using Xunit;

namespace Reeve.Workloads.Providers.Docker.Tests;

public sealed class DockerPullProgressAggregatorTests
{
    private const long MiB = 1024L * 1024L;

    private static JSONMessage Downloading(string id, long currentMiB, long totalMiB) => new()
    {
        ID = id,
        Status = "Downloading",
        Progress = new JSONProgress { Current = currentMiB * MiB, Total = totalMiB * MiB },
    };

    private static JSONMessage Status(string id, string status) => new() { ID = id, Status = status };

    [Fact]
    public void No_observations_is_indeterminate()
    {
        var aggregator = new DockerPullProgressAggregator();

        var snapshot = aggregator.Snapshot();

        snapshot.Percent.Should().BeNull();
        snapshot.Message.Should().Contain("Pulling");
    }

    [Fact]
    public void Single_layer_download_reports_overall_percent_and_byte_line()
    {
        var aggregator = new DockerPullProgressAggregator();

        aggregator.Observe(Downloading("layerA", currentMiB: 12, totalMiB: 26));

        var snapshot = aggregator.Snapshot();

        snapshot.Percent.Should().Be(46);
        snapshot.Message.Should().Be("Pulling image… 46% (12/26 MB)");
    }

    [Fact]
    public void Percent_aggregates_bytes_across_layers()
    {
        var aggregator = new DockerPullProgressAggregator();

        aggregator.Observe(Downloading("layerA", currentMiB: 10, totalMiB: 20));
        aggregator.Observe(Downloading("layerB", currentMiB: 30, totalMiB: 30));

        aggregator.Snapshot().Percent.Should().Be(80);
        aggregator.Snapshot().Message.Should().Be("Pulling image… 80% (40/50 MB)");
    }

    [Fact]
    public void Download_complete_counts_the_layer_as_fully_downloaded()
    {
        var aggregator = new DockerPullProgressAggregator();

        aggregator.Observe(Downloading("layerA", currentMiB: 5, totalMiB: 20));
        aggregator.Observe(Status("layerA", "Download complete"));

        aggregator.Snapshot().Percent.Should().Be(100);
    }

    [Fact]
    public void Later_byte_frame_never_regresses_a_completed_layer()
    {
        var aggregator = new DockerPullProgressAggregator();

        aggregator.Observe(Downloading("layerA", currentMiB: 20, totalMiB: 20));
        aggregator.Observe(Status("layerA", "Pull complete"));
        aggregator.Observe(Downloading("layerA", currentMiB: 1, totalMiB: 20));

        aggregator.Snapshot().Percent.Should().Be(100);
    }

    [Fact]
    public void Layers_without_a_known_total_do_not_break_aggregation()
    {
        var aggregator = new DockerPullProgressAggregator();

        aggregator.Observe(Status("layerA", "Pulling fs layer"));
        aggregator.Observe(Status("layerB", "Already exists"));
        aggregator.Observe(Downloading("layerC", currentMiB: 5, totalMiB: 10));

        aggregator.Snapshot().Percent.Should().Be(50);
    }

    [Fact]
    public void Percent_never_exceeds_one_hundred()
    {
        var aggregator = new DockerPullProgressAggregator();

        aggregator.Observe(Downloading("layerA", currentMiB: 30, totalMiB: 20));

        aggregator.Snapshot().Percent.Should().Be(100);
    }
}
