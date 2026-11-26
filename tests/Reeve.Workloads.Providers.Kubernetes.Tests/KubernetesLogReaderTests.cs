using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using k8s;
using Reeve.Workloads.Abstractions.Contracts;
using Reeve.Workloads.Abstractions.Providers;
using Reeve.Workloads.Providers.Kubernetes;
using Xunit;

namespace Reeve.Workloads.Providers.Kubernetes.Tests;

public sealed class KubernetesLogReaderTests
{
    private static readonly Guid InstanceId = Guid.Parse("11111111-2222-3333-4444-555555555555");

    private static WorkloadRef Ref() => new()
    {
        InstanceId = InstanceId,
        GroupId = "com.reeve.matter",
        WorkloadId = "matter",
        Inst8 = "11111111",
        Provider = KubernetesProvider.Id,
    };

    private static readonly IKubernetes NullClient = null!;

    [Fact]
    public async Task No_pods_matched_yields_empty_logs()
    {
        var source = new FakePodLogSource();
        var reader = new KubernetesLogReader(source);

        var logs = await reader.ReadAsync(NullClient, "reeve", Ref(), tailLines: 100, previousContainer: false, CancellationToken.None);

        logs.Lines.Should().BeEmpty();
        logs.Truncated.Should().BeFalse();
    }

    [Fact]
    public async Task Healthy_pod_without_previous_request_returns_only_current_lines()
    {
        var source = new FakePodLogSource
        {
            Pods = [new PodLogTarget { PodName = "pod-a", RestartCount = 0 }],
            CurrentLog = "line-1\nline-2\nline-3",
            PreviousLog = "SHOULD-NOT-BE-READ",
        };
        var reader = new KubernetesLogReader(source);

        var logs = await reader.ReadAsync(NullClient, "reeve", Ref(), tailLines: 100, previousContainer: false, CancellationToken.None);

        logs.Lines.Should().HaveCount(3);
        logs.Lines.Should().OnlyContain(l => !l.IsRestartMarker);
        logs.Lines.Select(l => l.Text).Should().ContainInOrder("line-1", "line-2", "line-3");
        logs.Truncated.Should().BeFalse();
        source.PreviousReadCount.Should().Be(0, "previous must not be fetched for a healthy pod when not requested");
    }

    [Fact]
    public async Task Restarted_pod_prepends_previous_lines_and_restart_marker_before_current()
    {
        var source = new FakePodLogSource
        {
            Pods = [new PodLogTarget { PodName = "pod-a", RestartCount = 3 }],
            PreviousLog = "prev-1\nprev-2",
            CurrentLog = "cur-1\ncur-2",
        };
        var reader = new KubernetesLogReader(source);

        var logs = await reader.ReadAsync(NullClient, "reeve", Ref(), tailLines: 100, previousContainer: false, CancellationToken.None);

        logs.Lines.Should().HaveCount(5);
        logs.Lines[0].Text.Should().Be("prev-1");
        logs.Lines[0].IsRestartMarker.Should().BeFalse();
        logs.Lines[1].Text.Should().Be("prev-2");

        var marker = logs.Lines[2];
        marker.IsRestartMarker.Should().BeTrue();
        marker.Text.Should().Contain("3", "the marker names the restart count");
        marker.Text.Should().Contain("previous container");

        logs.Lines[3].Text.Should().Be("cur-1");
        logs.Lines[3].IsRestartMarker.Should().BeFalse();
        logs.Lines[4].Text.Should().Be("cur-2");
        logs.Truncated.Should().BeFalse();
        source.PreviousReadCount.Should().Be(1, "restartCount>0 forces a previous-container fetch");
    }

    [Fact]
    public async Task Explicit_previous_request_fetches_previous_and_marker_even_when_not_restarted()
    {
        var source = new FakePodLogSource
        {
            Pods = [new PodLogTarget { PodName = "pod-a", RestartCount = 0 }],
            PreviousLog = "prev-1",
            CurrentLog = "cur-1",
        };
        var reader = new KubernetesLogReader(source);

        var logs = await reader.ReadAsync(NullClient, "reeve", Ref(), tailLines: 100, previousContainer: true, CancellationToken.None);

        source.PreviousReadCount.Should().Be(1);
        logs.Lines.Should().Contain(l => l.IsRestartMarker);
        logs.Lines.Select(l => l.Text).Should().ContainInOrder("prev-1");
        var markerIndex = logs.Lines.ToList().FindIndex(l => l.IsRestartMarker);
        markerIndex.Should().Be(1, "the single previous line precedes the marker");
        logs.Lines[2].Text.Should().Be("cur-1");
    }

    [Fact]
    public async Task Tail_smaller_than_total_truncates_to_the_most_recent_lines()
    {
        var source = new FakePodLogSource
        {
            Pods = [new PodLogTarget { PodName = "pod-a", RestartCount = 0 }],
            CurrentLog = "l1\nl2\nl3\nl4\nl5",
        };
        var reader = new KubernetesLogReader(source);

        var logs = await reader.ReadAsync(NullClient, "reeve", Ref(), tailLines: 2, previousContainer: false, CancellationToken.None);

        logs.Truncated.Should().BeTrue();
        logs.Lines.Should().HaveCount(2);
        logs.Lines.Select(l => l.Text).Should().ContainInOrder("l4", "l5");
    }

    [Fact]
    public async Task Log_source_that_throws_on_list_yields_empty_never_throws()
    {
        var source = new ThrowingPodLogSource();
        var reader = new KubernetesLogReader(source);

        Func<Task> act = async () =>
        {
            var logs = await reader.ReadAsync(NullClient, "reeve", Ref(), tailLines: 100, previousContainer: false, CancellationToken.None);
            logs.Lines.Should().BeEmpty();
            logs.Truncated.Should().BeFalse();
        };

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task Multiple_matching_pods_reads_the_first_most_recent_pod()
    {
        var source = new FakePodLogSource
        {
            Pods =
            [
                new PodLogTarget { PodName = "pod-new", RestartCount = 0 },
                new PodLogTarget { PodName = "pod-old", RestartCount = 0 },
            ],
            CurrentLog = "from-new",
        };
        var reader = new KubernetesLogReader(source);

        var logs = await reader.ReadAsync(NullClient, "reeve", Ref(), tailLines: 100, previousContainer: false, CancellationToken.None);

        source.LastReadPodName.Should().Be("pod-new");
        logs.Lines.Should().ContainSingle().Which.Text.Should().Be("from-new");
    }

    [Fact]
    public void Assemble_splits_crlf_and_drops_only_the_trailing_newline()
    {
        var logs = KubernetesLogReader.Assemble(
            currentLog: "a\r\nb\r\n\r\nc\r\n",
            previousLog: null,
            restartCount: 0,
            includePrevious: false,
            tailLines: 100);

        logs.Lines.Select(l => l.Text).Should().Equal("a", "b", "", "c");
        logs.Truncated.Should().BeFalse();
    }

    [Fact]
    public void Assemble_reads_the_kubelet_timestamp_of_each_line_and_strips_it_from_the_text()
    {
        var logs = KubernetesLogReader.Assemble(
            currentLog: "2026-11-21T10:15:31.123456789Z cur-1\n",
            previousLog: "2026-11-21T10:15:30Z prev-1\n",
            restartCount: 1,
            includePrevious: true,
            tailLines: 100);

        logs.Lines.Select(l => (l.Text, l.AtUtc)).Should().Equal(
            ("prev-1", new DateTimeOffset(2026, 11, 21, 10, 15, 30, TimeSpan.Zero)),
            ("--- previous container instance (restarts: 1) ---", null),
            ("cur-1", new DateTimeOffset(2026, 11, 21, 10, 15, 31, TimeSpan.Zero).AddTicks(1_234_567)));
    }

    [Fact]
    public void Assemble_empty_current_and_no_previous_yields_no_lines()
    {
        var logs = KubernetesLogReader.Assemble(
            currentLog: string.Empty,
            previousLog: null,
            restartCount: 0,
            includePrevious: false,
            tailLines: 100);

        logs.Lines.Should().BeEmpty();
        logs.Truncated.Should().BeFalse();
    }

    [Fact]
    public async Task Failed_current_read_on_restarted_pod_yields_empty_then_next_good_read_continues_without_gap()
    {
        var source = new FakePodLogSource
        {
            Pods = [new PodLogTarget { PodName = "pod-a", RestartCount = 1 }],
            PreviousLog = "2026-01-01T00:00:01.0000000Z prev-1",
            CurrentLog = "2026-01-01T00:00:02.0000000Z cur-1\n2026-01-01T00:00:03.0000000Z cur-2",
            FailCurrentRead = true,
        };
        var reader = new KubernetesLogReader(source);

        var failed = await reader.ReadAsync(NullClient, "reeve", Ref(), tailLines: 100, previousContainer: false, CancellationToken.None);

        failed.Lines.Should().BeEmpty();

        source.FailCurrentRead = false;
        var good = await reader.ReadAsync(NullClient, "reeve", Ref(), tailLines: 100, previousContainer: false, CancellationToken.None);
        var shown = good.Lines.First(l => l.Text == "cur-1");

        var next = WorkloadLogTail.LinesAfter(good.Lines, shown, 100);

        next.Lines.Select(l => l.Text).Should().Equal("cur-2");
        next.Lines.Should().OnlyContain(l => !l.IsGapMarker);
    }

    private sealed class FakePodLogSource : IKubernetesPodLogSource
    {
        public IReadOnlyList<PodLogTarget> Pods { get; init; } = [];

        public string CurrentLog { get; init; } = string.Empty;

        public string PreviousLog { get; init; } = string.Empty;

        public int PreviousReadCount { get; private set; }

        public string? LastReadPodName { get; private set; }

        public bool FailCurrentRead { get; set; }

        public Task<IReadOnlyList<PodLogTarget>> ListPodsAsync(
            IKubernetes client, string @namespace, IReadOnlyDictionary<string, string> selectorLabels, CancellationToken ct)
            => Task.FromResult(Pods);

        public Task<string?> ReadPodLogAsync(
            IKubernetes client, string @namespace, string podName, int tailLines, bool previous, CancellationToken ct)
        {
            LastReadPodName = podName;
            if (previous)
            {
                PreviousReadCount++;
                return Task.FromResult<string?>(PreviousLog);
            }

            if (FailCurrentRead)
            {
                return Task.FromResult<string?>(null);
            }

            return Task.FromResult<string?>(CurrentLog);
        }

        public Task<IReadOnlyList<(DateTimeOffset AtUtc, string Reason, string Message, bool Warning)>> ListPodEventsAsync(
            IKubernetes client, string @namespace, string podName, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<(DateTimeOffset AtUtc, string Reason, string Message, bool Warning)>>([]);
    }

    private sealed class ThrowingPodLogSource : IKubernetesPodLogSource
    {
        public Task<IReadOnlyList<PodLogTarget>> ListPodsAsync(
            IKubernetes client, string @namespace, IReadOnlyDictionary<string, string> selectorLabels, CancellationToken ct)
            => throw new InvalidOperationException("simulated API-server failure");

        public Task<string?> ReadPodLogAsync(
            IKubernetes client, string @namespace, string podName, int tailLines, bool previous, CancellationToken ct)
            => throw new InvalidOperationException("simulated API-server failure");

        public Task<IReadOnlyList<(DateTimeOffset AtUtc, string Reason, string Message, bool Warning)>> ListPodEventsAsync(
            IKubernetes client, string @namespace, string podName, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<(DateTimeOffset AtUtc, string Reason, string Message, bool Warning)>>([]);
    }
}
