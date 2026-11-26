using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Reeve.Workloads.Abstractions.Contracts;
using Reeve.Workloads.Abstractions.Contracts.Deployment;
using Reeve.Workloads.Abstractions.Domain.DesiredSpec;
using Reeve.Workloads.Abstractions.Domain.Entities;
using Reeve.Workloads.Abstractions.Providers;
using Reeve.Workloads.Core.Providers;
using Reeve.Workloads.Core.Tests.Telemetry;
using Xunit;
using Reeve.Workloads.Providers.Kubernetes;

namespace Reeve.Workloads.Core.Tests;

[Collection(WorkloadsTelemetryCollection.Name)]
public sealed class BoundedWorkloadProviderTests
{
    [Fact]
    public async Task A_hung_status_read_answers_Unknown_rather_than_hanging()
    {
        var provider = Bounded(new HangingProvider());

        var status = await provider.GetStatusAsync(Reference());

        status.Phase.Should().Be(WorkloadProviderPhase.Unknown,
            "the SPI says a provider that cannot be reached reports Unknown, and a socket that never answers is unreachable");
        status.ProviderReady.Should().BeFalse();
    }

    [Fact]
    public async Task A_hung_apply_is_never_reported_as_applied()
    {
        var provider = Bounded(new HangingProvider());

        var result = await provider.ApplyAsync(Spec(), progress: null);

        result.Succeeded.Should().BeFalse();
        result.Reason.Should().Be(WorkloadApplyReason.ProviderUnreachable);
    }

    [Fact]
    public async Task A_hung_preflight_blocks_rather_than_passing()
    {
        var provider = Bounded(new HangingProvider());

        var result = await provider.PreflightAsync(Spec());

        result.HasBlockingError.Should().BeTrue(
            "an all-clear from a provider that never answered would let a deploy through on no evidence at all");
    }

    [Fact]
    public async Task A_hung_existence_read_times_out_as_unreadable()
    {
        var provider = Bounded(new HangingProvider());

        var existence = await provider.GetAttachmentExistenceAsync(["macvlan0"]);

        existence.Status.Should().Be(WorkloadAttachmentExistenceStatus.Unreadable);
        existence.MissingAttachments.Should().BeEmpty();
    }

    [Fact]
    public async Task A_hung_idempotent_mutation_returns_instead_of_throwing()
    {
        var provider = Bounded(new HangingProvider());

        await FluentActions.Awaiting(() => provider.SuspendAsync(Reference())).Should().NotThrowAsync(
            "suspend is documented idempotent and unreachable-tolerant; the next pass retries it");
    }

    [Fact]
    public async Task A_hung_teardown_fails_so_the_instance_is_kept_for_a_retry()
    {
        var provider = Bounded(new HangingProvider());

        await FluentActions.Awaiting(() => provider.TeardownAsync(Reference(), retainVolumes: true))
            .Should().ThrowAsync<TimeoutException>(
                "a teardown that never finished must not read as done, or the caller deletes the instance of a live runtime");
    }

    [Fact]
    public async Task The_callers_own_cancellation_is_not_dressed_up_as_a_timeout()
    {
        var provider = Bounded(new HangingProvider(), timeout: TimeSpan.FromMinutes(5));
        using var stopping = new CancellationTokenSource();

        var call = provider.GetStatusAsync(Reference(), stopping.Token);
        await stopping.CancelAsync();

        await FluentActions.Awaiting(() => call).Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task A_provider_that_answers_in_time_is_left_alone()
    {
        var inner = new AnsweringProvider();
        var provider = Bounded(inner, timeout: TimeSpan.FromMinutes(5));

        var status = await provider.GetStatusAsync(Reference());

        status.Phase.Should().Be(WorkloadProviderPhase.Ready, "the decorator bounds a call, it does not change one");
    }

    [Fact]
    public async Task Disabling_the_deadline_hands_the_callers_token_through_untouched()
    {
        var provider = new BoundedWorkloadProvider(
            new AnsweringProvider(),
            new WorkloadProviderTimeoutOptions { Enabled = false },
            NullLogger.Instance);

        var status = await provider.GetStatusAsync(Reference());

        status.Phase.Should().Be(WorkloadProviderPhase.Ready);
    }

    [Fact]
    public async Task The_registry_hands_back_a_provider_that_is_already_bounded()
    {
        var registry = new WorkloadProviderRegistry(
            [new HangingProvider()],
            Options.Create(new WorkloadProviderTimeoutOptions { CallTimeoutSeconds = 1, MutationTimeoutSeconds = 1 }),
            NullLoggerFactory.Instance);

        var status = await registry.Resolve(KubernetesProvider.Id).GetStatusAsync(Reference());

        status.Phase.Should().Be(WorkloadProviderPhase.Unknown,
            "nothing else wraps a provider, so an unbounded one out of the registry is an unbounded one everywhere");
    }

    [Fact]
    public async Task A_hung_fleet_read_answers_Unknown_for_every_instance()
    {
        var provider = Bounded(new HangingProvider());
        var references = new[] { Reference(), Reference(), Reference() };

        var statuses = await provider.GetStatusesAsync(references);

        statuses.Should().HaveCount(3);
        statuses.Values.Should().OnlyContain(s => s.Phase == WorkloadProviderPhase.Unknown);
    }

    [Fact]
    public async Task The_decorator_forwards_a_batch_instead_of_looping_it()
    {
        var inner = new CountingProvider();
        var provider = Bounded(inner, timeout: TimeSpan.FromMinutes(5));

        await provider.GetStatusesAsync([Reference(), Reference()]);

        inner.BatchCalls.Should().Be(1);
        inner.SingleCalls.Should().Be(0);
    }

    private static IWorkloadProvider Bounded(IWorkloadProvider inner, TimeSpan? timeout = null)
        => new BoundedWorkloadProvider(
            inner,
            new WorkloadProviderTimeoutOptions
            {
                CallTimeoutSeconds = 1,
                MutationTimeoutSeconds = 1,
            },
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

    private sealed class HangingProvider : IWorkloadProvider
    {
        public WorkloadProviderDescriptor Descriptor => ShippedProviders.For(KubernetesProvider.Id);

        public Task<WorkloadApplyResult> ApplyAsync(
            WorkloadDesiredSpec spec, IProgress<WorkloadApplyProgress>? progress,
            CancellationToken cancellationToken = default, bool forceConflicts = false)
            => Hang<WorkloadApplyResult>(cancellationToken);

        public Task TeardownAsync(WorkloadRef reference, bool retainVolumes, CancellationToken cancellationToken = default)
            => Hang<bool>(cancellationToken);

        public Task SuspendAsync(WorkloadRef reference, CancellationToken cancellationToken = default)
            => Hang<bool>(cancellationToken);

        public Task ResumeAsync(WorkloadRef reference, CancellationToken cancellationToken = default)
            => Hang<bool>(cancellationToken);

        public Task<WorkloadStatus> GetStatusAsync(WorkloadRef reference, CancellationToken cancellationToken = default)
            => Hang<WorkloadStatus>(cancellationToken);

        public Task<WorkloadPreflightResult> PreflightAsync(WorkloadDesiredSpec spec, CancellationToken cancellationToken = default)
            => Hang<WorkloadPreflightResult>(cancellationToken);

        public Task<WorkloadLogs> GetRecentLogsAsync(
            WorkloadRef reference, int tailLines, bool previousContainer, CancellationToken cancellationToken = default)
            => Hang<WorkloadLogs>(cancellationToken);

        public Task<WorkloadAttachmentExistence> GetAttachmentExistenceAsync(
            IReadOnlyList<string> attachments, CancellationToken cancellationToken = default)
            => Hang<WorkloadAttachmentExistence>(cancellationToken);

        private static Task<T> Hang<T>(CancellationToken cancellationToken)
            => Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ContinueWith(
                _ => default(T)!, cancellationToken, TaskContinuationOptions.None, TaskScheduler.Default);
    }

    private sealed class CountingProvider : IWorkloadProvider
    {
        public int SingleCalls { get; private set; }

        public int BatchCalls { get; private set; }

        public WorkloadProviderDescriptor Descriptor
            => ShippedProviders.For(KubernetesProvider.Id) with { ReadsStatusInBatches = true };

        public Task<WorkloadStatus> GetStatusAsync(WorkloadRef reference, CancellationToken cancellationToken = default)
        {
            SingleCalls++;
            return Task.FromResult(new WorkloadStatus { Phase = WorkloadProviderPhase.Ready, ProviderReady = true });
        }

        public Task<IReadOnlyDictionary<WorkloadRef, WorkloadStatus>> GetStatusesAsync(
            IReadOnlyList<WorkloadRef> references, CancellationToken cancellationToken = default)
        {
            BatchCalls++;

            var statuses = new Dictionary<WorkloadRef, WorkloadStatus>();
            foreach (var reference in references)
            {
                statuses[reference] = new WorkloadStatus { Phase = WorkloadProviderPhase.Ready, ProviderReady = true };
            }

            return Task.FromResult<IReadOnlyDictionary<WorkloadRef, WorkloadStatus>>(statuses);
        }

        public Task<WorkloadApplyResult> ApplyAsync(
            WorkloadDesiredSpec spec, IProgress<WorkloadApplyProgress>? progress,
            CancellationToken cancellationToken = default, bool forceConflicts = false)
            => throw new NotSupportedException();

        public Task TeardownAsync(WorkloadRef reference, bool retainVolumes, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task SuspendAsync(WorkloadRef reference, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task ResumeAsync(WorkloadRef reference, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<WorkloadPreflightResult> PreflightAsync(WorkloadDesiredSpec spec, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<WorkloadLogs> GetRecentLogsAsync(
            WorkloadRef reference, int tailLines, bool previousContainer, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<WorkloadAttachmentExistence> GetAttachmentExistenceAsync(
            IReadOnlyList<string> attachments, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }

    private sealed class AnsweringProvider : IWorkloadProvider
    {
        public WorkloadProviderDescriptor Descriptor => ShippedProviders.For(KubernetesProvider.Id);

        public Task<WorkloadApplyResult> ApplyAsync(
            WorkloadDesiredSpec spec, IProgress<WorkloadApplyProgress>? progress,
            CancellationToken cancellationToken = default, bool forceConflicts = false)
            => Task.FromResult(WorkloadApplyResult.Applied("sha256:abc"));

        public Task TeardownAsync(WorkloadRef reference, bool retainVolumes, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task SuspendAsync(WorkloadRef reference, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task ResumeAsync(WorkloadRef reference, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<WorkloadStatus> GetStatusAsync(WorkloadRef reference, CancellationToken cancellationToken = default)
            => Task.FromResult(new WorkloadStatus { Phase = WorkloadProviderPhase.Ready, ProviderReady = true });

        public Task<WorkloadPreflightResult> PreflightAsync(WorkloadDesiredSpec spec, CancellationToken cancellationToken = default)
            => Task.FromResult(WorkloadPreflightResult.Ok());

        public Task<WorkloadLogs> GetRecentLogsAsync(
            WorkloadRef reference, int tailLines, bool previousContainer, CancellationToken cancellationToken = default)
            => Task.FromResult(WorkloadLogs.Empty());

        public Task<WorkloadAttachmentExistence> GetAttachmentExistenceAsync(
            IReadOnlyList<string> attachments, CancellationToken cancellationToken = default)
            => Task.FromResult(WorkloadAttachmentExistence.Read([]));
    }
}
