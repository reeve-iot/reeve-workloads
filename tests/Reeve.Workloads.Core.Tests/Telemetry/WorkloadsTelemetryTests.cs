using System.Diagnostics;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Reeve.Workloads.Abstractions.Contracts;
using Reeve.Workloads.Abstractions.Contracts.Deployment;
using Reeve.Workloads.Abstractions.Domain.DesiredSpec;
using Reeve.Workloads.Abstractions.Providers;
using Reeve.Workloads.Core.Providers;
using Reeve.Workloads.Core.Telemetry;
using Xunit;
using Reeve.Workloads.Providers.Kubernetes;

namespace Reeve.Workloads.Core.Tests.Telemetry;

[Collection(WorkloadsTelemetryCollection.Name)]
public sealed class WorkloadsTelemetryTests
{
    private const string ProviderCall = "reeve.workloads.provider.call.duration";
    private const string QueueDepth = "reeve.workloads.queue.depth";
    private const string QueueSettling = "reeve.workloads.queue.settling";

    [Fact]
    public void The_meter_and_the_activity_source_share_one_name()
    {
        WorkloadsTelemetry.Name.Should().Be("Reeve.Workloads");
        WorkloadsTelemetry.Source.Name.Should().Be(WorkloadsTelemetry.Name);
    }

    [Fact]
    public async Task A_provider_call_is_timed_by_provider_operation_and_outcome()
    {
        var id = Id("metered-ok");
        using var capture = new MeterCapture();

        await Bounded(new AnsweringProvider(id)).GetStatusAsync(Reference());

        capture.Values(ProviderCall, ("provider", id.Value), ("operation", "GetStatusAsync"), ("outcome", "ok"))
            .Should().ContainSingle();
    }

    [Fact]
    public async Task Every_SPI_method_is_measured_not_just_apply()
    {
        var id = Id("metered-all");
        using var capture = new MeterCapture();
        var provider = Bounded(new AnsweringProvider(id));

        await provider.ApplyAsync(Spec(), progress: null);
        await provider.TeardownAsync(Reference(), retainVolumes: true);
        await provider.SuspendAsync(Reference());
        await provider.ResumeAsync(Reference());
        await provider.GetStatusAsync(Reference());
        await provider.PreflightAsync(Spec());
        await provider.GetRecentLogsAsync(Reference(), 10, previousContainer: false);
        await provider.GetAttachmentExistenceAsync(["macvlan0"]);

        var operations = new[]
        {
            "ApplyAsync", "TeardownAsync", "SuspendAsync", "ResumeAsync",
            "GetStatusAsync", "PreflightAsync", "GetRecentLogsAsync", "GetAttachmentExistenceAsync",
        };
        foreach (var operation in operations)
        {
            capture.Values(ProviderCall, ("provider", id.Value), ("operation", operation))
                .Should().ContainSingle($"{operation} goes through the same funnel as the rest");
        }
    }

    [Fact]
    public async Task A_timed_out_call_is_recorded_as_a_timeout_not_as_success()
    {
        var id = Id("metered-slow");
        using var capture = new MeterCapture();

        await Bounded(new HangingProvider(id)).GetStatusAsync(Reference());

        capture.Values(ProviderCall, ("provider", id.Value), ("outcome", "timeout")).Should().ContainSingle();
        capture.Values(ProviderCall, ("provider", id.Value), ("outcome", "ok")).Should().BeEmpty();
    }

    [Fact]
    public async Task A_throwing_provider_is_recorded_as_an_error_and_still_throws()
    {
        var id = Id("metered-bad");
        using var capture = new MeterCapture();
        var provider = Bounded(new ThrowingProvider(id));

        await FluentActions.Invoking(() => provider.GetStatusAsync(Reference()))
            .Should().ThrowAsync<InvalidOperationException>("measuring a call must not swallow it");

        capture.Values(ProviderCall, ("provider", id.Value), ("outcome", "error")).Should().ContainSingle();
    }

    [Fact]
    public async Task The_callers_own_cancellation_is_not_recorded_as_a_provider_timeout()
    {
        var id = Id("metered-cancel");
        using var capture = new MeterCapture();
        using var caller = new CancellationTokenSource();
        await caller.CancelAsync();

        await FluentActions.Invoking(() => Bounded(new HangingProvider(id)).GetStatusAsync(Reference(), caller.Token))
            .Should().ThrowAsync<OperationCanceledException>();

        capture.Values(ProviderCall, ("provider", id.Value), ("outcome", "cancelled")).Should().ContainSingle();
        capture.Values(ProviderCall, ("provider", id.Value), ("outcome", "timeout")).Should().BeEmpty();
    }

    [Fact]
    public async Task A_provider_call_opens_a_span_carrying_the_provider_and_the_outcome()
    {
        var id = Id("spanned");
        using var spans = new ActivityCapture();

        await Bounded(new AnsweringProvider(id)).GetStatusAsync(Reference());

        var span = spans.Stopped.Should().ContainSingle(a => a.GetTagItem("workload.provider") as string == id.Value).Subject;
        span.OperationName.Should().Be("workload.provider.GetStatusAsync");
        span.GetTagItem("workload.outcome").Should().Be("ok");
    }

    [Fact]
    public async Task A_throwing_call_marks_its_span_as_an_error()
    {
        var id = Id("spanned-bad");
        using var spans = new ActivityCapture();

        await FluentActions.Invoking(() => Bounded(new ThrowingProvider(id)).GetStatusAsync(Reference()))
            .Should().ThrowAsync<InvalidOperationException>();

        var span = spans.Stopped.Should().ContainSingle(a => a.GetTagItem("workload.provider") as string == id.Value).Subject;
        span.Status.Should().Be(ActivityStatusCode.Error);
    }

    [Fact]
    public void The_queue_gauges_report_what_the_source_says()
    {
        using var capture = new MeterCapture();
        WorkloadsMetrics.ObserveQueue(() => (7, 3));

        capture.CollectGauges();

        capture.Values(QueueDepth).Should().ContainSingle().Which.Should().Be(7);
        capture.Values(QueueSettling).Should().ContainSingle().Which.Should().Be(3);
    }

    [Fact]
    public void The_queue_gauges_report_nothing_until_a_reconciler_owns_a_queue()
    {
        using var capture = new MeterCapture();
        WorkloadsMetrics.ObserveQueue(null!);

        capture.CollectGauges();

        capture.Values(QueueDepth).Should().BeEmpty();
        capture.Values(QueueSettling).Should().BeEmpty();
    }

    private static WorkloadProviderId Id(string suffix) => WorkloadProviderId.Parse(suffix);

    private static IWorkloadProvider Bounded(IWorkloadProvider inner)
        => new BoundedWorkloadProvider(
            inner,
            new WorkloadProviderTimeoutOptions { CallTimeoutSeconds = 1, MutationTimeoutSeconds = 1 },
            NullLogger.Instance);

    private static WorkloadRef Reference() => new()
    {
        InstanceId = Guid.NewGuid(),
        GroupId = "zwave",
        WorkloadId = "gateway",
        Inst8 = "11111111",
        Provider = KubernetesProvider.Id,
    };

    private static WorkloadDesiredSpec Spec() => new()
    {
        GroupInstanceId = Guid.NewGuid(),
        GroupId = "zwave",
        WorkloadId = "gateway",
        InstanceId = Guid.NewGuid(),
        Inst8 = "11111111",
        Provider = KubernetesProvider.Id,
        ResourceName = "reeve-gateway",
        SecretName = "reeve-gateway-secret",
        Labels = new Dictionary<string, string>(),
        Image = "registry.example.com/acme/zwave@sha256:deadbeef",
        Kind = WorkloadKind.Service,
        Health = new WorkloadHealth { Mode = WorkloadHealthMode.Container },
        SpecHash = "hash-gateway",
    };

    private abstract class FakeProvider(WorkloadProviderId id) : IWorkloadProvider
    {
        public WorkloadProviderDescriptor Descriptor { get; } = ShippedProviders.For(id);

        public abstract Task<WorkloadStatus> GetStatusAsync(WorkloadRef reference, CancellationToken cancellationToken = default);

        public virtual Task<WorkloadApplyResult> ApplyAsync(
            WorkloadDesiredSpec spec, IProgress<WorkloadApplyProgress>? progress,
            CancellationToken cancellationToken = default, bool forceConflicts = false)
            => Task.FromResult(WorkloadApplyResult.Applied("sha256:abc"));

        public virtual Task TeardownAsync(WorkloadRef reference, bool retainVolumes, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public virtual Task SuspendAsync(WorkloadRef reference, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public virtual Task ResumeAsync(WorkloadRef reference, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public virtual Task<WorkloadPreflightResult> PreflightAsync(WorkloadDesiredSpec spec, CancellationToken cancellationToken = default)
            => Task.FromResult(WorkloadPreflightResult.Ok());

        public virtual Task<WorkloadLogs> GetRecentLogsAsync(
            WorkloadRef reference, int tailLines, bool previousContainer, CancellationToken cancellationToken = default)
            => Task.FromResult(WorkloadLogs.Empty());

        public virtual Task<WorkloadAttachmentExistence> GetAttachmentExistenceAsync(
            IReadOnlyList<string> attachments, CancellationToken cancellationToken = default)
            => Task.FromResult(WorkloadAttachmentExistence.Read([]));
    }

    private sealed class AnsweringProvider(WorkloadProviderId id) : FakeProvider(id)
    {
        public override Task<WorkloadStatus> GetStatusAsync(WorkloadRef reference, CancellationToken cancellationToken = default)
            => Task.FromResult(new WorkloadStatus { Phase = WorkloadProviderPhase.Ready, ProviderReady = true });
    }

    private sealed class ThrowingProvider(WorkloadProviderId id) : FakeProvider(id)
    {
        public override Task<WorkloadStatus> GetStatusAsync(WorkloadRef reference, CancellationToken cancellationToken = default)
            => Task.FromException<WorkloadStatus>(new InvalidOperationException("the socket is gone"));
    }

    private sealed class HangingProvider(WorkloadProviderId id) : FakeProvider(id)
    {
        public override Task<WorkloadStatus> GetStatusAsync(WorkloadRef reference, CancellationToken cancellationToken = default)
            => Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken)
                .ContinueWith(_ => default(WorkloadStatus)!, cancellationToken, TaskContinuationOptions.None, TaskScheduler.Default);
    }
}
