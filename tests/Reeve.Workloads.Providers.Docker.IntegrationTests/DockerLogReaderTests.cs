using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Docker.DotNet;
using Docker.DotNet.Models;
using FluentAssertions;
using Reeve.Workloads.Abstractions.Contracts;
using Reeve.Workloads.Abstractions.Providers;
using Reeve.Workloads.Providers.Docker;

namespace Reeve.Workloads.Providers.Docker.IntegrationTests;

[Collection(DockerIntegrationCollection.Name)]
public sealed class DockerLogReaderTests
{
    private const string Image = "busybox:latest";
    private const string ImageRepo = "busybox";
    private const string ImageTag = "latest";

    private static readonly TimeSpan WaitBudget = TimeSpan.FromSeconds(20);

    private readonly DockerDaemonFixture _docker;

    public DockerLogReaderTests(DockerDaemonFixture docker) => _docker = docker;

    [SkippableFact]
    public async Task ReadAsync_current_container_returns_clean_demuxed_stdout_and_is_tail_limited()
    {
        _docker.EnsureAvailable();
        var client = _docker.Client!;
        Skip.IfNot(await EnsureImageAsync(client, CancellationToken.None), $"'{Image}' unavailable and not pullable on this runner.");

        var reader = new DockerLogReader();
        var reference = NewReference(out var instanceId);
        var createdIds = new List<string>();

        try
        {
            await CreateAndStartAsync(
                client,
                instanceId,
                specHash: "hash-only",
                cmd: new[] { "sh", "-c", "echo hello-A; echo hello-B; sleep 3600" },
                createdIds);

            var full = await WaitForLogsAsync(
                client,
                reader,
                reference,
                tailLines: 100,
                previousContainer: false,
                predicate: logs => LineTexts(logs).Contains("hello-A") && LineTexts(logs).Contains("hello-B"));

            var texts = LineTexts(full);
            texts.Should().Contain("hello-A").And.Contain("hello-B");
            full.Lines.Should().OnlyContain(l => !l.IsRestartMarker, "no previous container was requested");

            texts.Should().Contain(t => t == "hello-A", "the line is exactly the payload, header-free");
            full.Lines.Should().OnlyContain(
                l => l.AtUtc.HasValue && l.AtUtc.Value.Offset == TimeSpan.Zero,
                "the daemon is asked for timestamps and every line carries its UTC time with the prefix stripped");
            full.Lines.Should().OnlyContain(
                l => l.Text.All(ch => ch >= ' ' || ch == '\t'),
                "no docker stream-framing header bytes may leak into the log text");

            var tailed = await reader.ReadAsync(client, reference, tailLines: 1, previousContainer: false, CancellationToken.None);
            var tailedTexts = LineTexts(tailed);
            tailedTexts.Should().ContainSingle().Which.Should().Be("hello-B");
            tailedTexts.Should().NotContain("hello-A", "the tail cap of 1 drops the older line server-side");
        }
        finally
        {
            await RemoveAllAsync(client, createdIds);
        }
    }

    [SkippableFact]
    public async Task ReadAsync_with_previous_prepends_previous_tail_behind_restart_marker_current_last()
    {
        _docker.EnsureAvailable();
        var client = _docker.Client!;
        Skip.IfNot(await EnsureImageAsync(client, CancellationToken.None), $"'{Image}' unavailable and not pullable on this runner.");

        var reader = new DockerLogReader();
        var reference = NewReference(out var instanceId);
        var createdIds = new List<string>();

        try
        {
            var previousId = await CreateAndStartAsync(
                client,
                instanceId,
                specHash: "hash-prev",
                cmd: new[] { "sh", "-c", "echo prev-1; echo prev-2" },
                createdIds);
            await WaitForExitAsync(client, previousId);

            await CreateAndStartAsync(
                client,
                instanceId,
                specHash: "hash-curr",
                cmd: new[] { "sh", "-c", "echo curr-1; echo curr-2; sleep 3600" },
                createdIds);

            var logs = await WaitForLogsAsync(
                client,
                reader,
                reference,
                tailLines: 200,
                previousContainer: true,
                predicate: l => LineTexts(l).Contains("curr-2") && LineTexts(l).Contains("prev-2"));

            var texts = LineTexts(logs);
            texts.Should().Contain(new[] { "prev-1", "prev-2", "curr-1", "curr-2" });

            var markers = logs.Lines.Where(l => l.IsRestartMarker).ToList();
            markers.Should().ContainSingle("a single previous→current boundary is synthesized");
            markers[0].Text.Should().StartWith(DockerLogReader.RestartBoundaryMarkerPrefix).And.Contain("hash-prev");

            var markerIndex = logs.Lines.ToList().FindIndex(l => l.IsRestartMarker);
            IndexOf(logs, "prev-2").Should().BeLessThan(markerIndex, "previous output sits above the marker");
            markerIndex.Should().BeLessThan(IndexOf(logs, "curr-1"), "current output sits below the marker");
            IndexOf(logs, "curr-2").Should().Be(logs.Lines.Count - 1, "the current container's last line is last overall");
        }
        finally
        {
            await RemoveAllAsync(client, createdIds);
        }
    }

    [SkippableFact]
    public async Task ReadAsync_unknown_instance_returns_empty_and_never_throws()
    {
        _docker.EnsureAvailable();
        var client = _docker.Client!;

        var reader = new DockerLogReader();
        var reference = NewReference(out _);

        var withPrevious = await reader.ReadAsync(client, reference, tailLines: 100, previousContainer: true, CancellationToken.None);
        var withoutPrevious = await reader.ReadAsync(client, reference, tailLines: 100, previousContainer: false, CancellationToken.None);

        withPrevious.Lines.Should().BeEmpty("no container carries this instance label");
        withPrevious.Truncated.Should().BeFalse();
        withoutPrevious.Lines.Should().BeEmpty();
    }

    [Fact]
    public async Task DemuxToTextAsync_caps_accumulated_bytes_when_maxBytes_is_set()
    {
        var payload = new string('x', 5000);
        using var stream = new MultiplexedStream(new MemoryStream(Encoding.ASCII.GetBytes(payload)), multiplexed: false);

        var text = await DockerLogReader.DemuxToTextAsync(stream, CancellationToken.None, maxBytes: 1000);

        text.Should().Be(new string('x', 1000));
    }

    [Fact]
    public async Task DemuxToTextAsync_without_a_cap_reads_the_whole_stream_unchanged()
    {
        var payload = new string('y', 5000);
        using var stream = new MultiplexedStream(new MemoryStream(Encoding.ASCII.GetBytes(payload)), multiplexed: false);

        var text = await DockerLogReader.DemuxToTextAsync(stream, CancellationToken.None);

        text.Should().Be(payload);
    }

    private WorkloadRef NewReference(out Guid instanceId)
    {
        instanceId = Guid.NewGuid();
        return new WorkloadRef
        {
            InstanceId = instanceId,
            GroupId = "reeve.test.logreader",
            WorkloadId = "logs",
            Inst8 = instanceId.ToString("N")[..8],
            Provider = DockerProvider.Id,
        };
    }

    private async Task<string> CreateAndStartAsync(
        IDockerClient client,
        Guid instanceId,
        string specHash,
        IList<string> cmd,
        List<string> createdIds)
    {
        var labels = new Dictionary<string, string>(_docker.BuildLabels(), StringComparer.Ordinal)
        {
            [DockerProviderConstants.InstanceLabelKey] = instanceId.ToString("D"),
            [DockerProviderConstants.SpecHashLabelKey] = specHash,
            [DockerProviderConstants.ManagedByLabelKey] = DockerProviderConstants.ManagedByLabelValue,
        };

        var created = await client.Containers.CreateContainerAsync(
            new CreateContainerParameters
            {
                Image = Image,
                Cmd = cmd,
                Labels = labels,
                Tty = false,
                Name = $"reeve-it-log-{_docker.RunId}-{Guid.NewGuid():N}",
            },
            CancellationToken.None).ConfigureAwait(false);

        createdIds.Add(created.ID);

        var started = await client.Containers.StartContainerAsync(created.ID, new ContainerStartParameters(), CancellationToken.None)
            .ConfigureAwait(false);
        started.Should().BeTrue("the test container should start");

        return created.ID;
    }

    private static async Task<WorkloadLogs> WaitForLogsAsync(
        IDockerClient client,
        DockerLogReader reader,
        WorkloadRef reference,
        int tailLines,
        bool previousContainer,
        Func<WorkloadLogs, bool> predicate)
    {
        var deadline = DateTime.UtcNow + WaitBudget;
        WorkloadLogs logs;
        do
        {
            logs = await reader.ReadAsync(client, reference, tailLines, previousContainer, CancellationToken.None)
                .ConfigureAwait(false);
        }
        while (!predicate(logs) && DateTime.UtcNow < deadline && await DelayAsync());

        return logs;
    }

    private async Task<bool> EnsureImageAsync(IDockerClient client, CancellationToken ct)
    {
        try
        {
            await client.Images.InspectImageAsync(Image, ct).ConfigureAwait(false);
            return true;
        }
        catch (DockerApiException)
        {
        }

        try
        {
            await client.Images.CreateImageAsync(
                new ImagesCreateParameters { FromImage = ImageRepo, Tag = ImageTag },
                authConfig: null,
                new Progress<JSONMessage>(),
                ct).ConfigureAwait(false);
            await client.Images.InspectImageAsync(Image, ct).ConfigureAwait(false);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static async Task WaitForExitAsync(IDockerClient client, string containerId)
    {
        var deadline = DateTime.UtcNow + WaitBudget;
        while (DateTime.UtcNow < deadline)
        {
            var inspect = await client.Containers.InspectContainerAsync(containerId, CancellationToken.None).ConfigureAwait(false);
            if (inspect.State is { Running: false })
            {
                return;
            }

            await Task.Delay(200).ConfigureAwait(false);
        }
    }

    private static async Task RemoveAllAsync(IDockerClient client, IEnumerable<string> ids)
    {
        foreach (var id in ids)
        {
            try
            {
                await client.Containers.RemoveContainerAsync(
                    id,
                    new ContainerRemoveParameters { Force = true, RemoveVolumes = true },
                    CancellationToken.None).ConfigureAwait(false);
            }
            catch (DockerApiException)
            {
            }
        }
    }

    private static IReadOnlyList<string> LineTexts(WorkloadLogs logs) => logs.Lines.Select(l => l.Text).ToList();

    private static int IndexOf(WorkloadLogs logs, string text) => logs.Lines.ToList().FindIndex(l => l.Text == text);

    private static async Task<bool> DelayAsync()
    {
        await Task.Delay(250).ConfigureAwait(false);
        return true;
    }
}
