using FluentAssertions;
using Reeve.Workloads.Abstractions;
using Reeve.Workloads.Abstractions.Contracts;
using Reeve.Workloads.Abstractions.Contracts.Deployment;
using Reeve.Workloads.Abstractions.Domain.DesiredSpec;
using Reeve.Workloads.Abstractions.Providers;
using Reeve.Workloads.Abstractions.Update;
using Reeve.Workloads.Core.HealthGate;
using Reeve.Workloads.Core.Tests;
using Reeve.Workloads.Core.Update;
using Reeve.Workloads.Testing;
using Xunit;
using Reeve.Workloads.Providers.Kubernetes;

namespace Reeve.Workloads.Core.Tests.Update;

public sealed class WorkloadUpdateHealthGateAdapterTests
{
    private static readonly DateTimeOffset ApplyAt = new(2026, 7, 6, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid InstanceId = Guid.NewGuid();

    private static readonly WorkloadRef Ref = new()
    {
        InstanceId = InstanceId,
        GroupId = "zwave",
        WorkloadId = "gateway",
        Inst8 = "abcdef12",
        Provider = KubernetesProvider.Id,
    };

    [Fact]
    public async Task Adapter_threads_prior_clock_into_the_gate_and_returns_the_next_state()
    {
        var registry = new SingleProviderRegistry(new ReadyProvider());
        var clock = new FixedClock(ApplyAt.AddSeconds(30));
        var adapter = new WorkloadUpdateHealthGateAdapter([new RecordingRolloutCertifier(RecordingRolloutCertifier.SessionMode)], registry, clock);

        var request = new WorkloadHealthGateTickRequest
        {
            Reference = Ref,
            InstanceId = InstanceId,
            ExpectedSpecHash = "sha256:applied",
            ApplyTimestampUtc = ApplyAt,
            ToVersion = "2.6.0",
            CurrentExtensions = ReeveTestExtensions.Pair(),
            CandidateExtensions = ReeveTestExtensions.Pair(),
            Timeout = TimeSpan.FromMinutes(5),
            Certification = WorkloadHealthGateCertification.HostEvidence,
            PriorClockState = new(),
        };

        var result = await adapter.EvaluateTickAsync(request);

        result.Verdict.Should().Be(WorkloadHealthGateVerdict.Pending);
        result.NextClockState.ClockStartedAtUtc.Should().Be(
            clock.UtcNow, "provider Ready + no candidate ⇒ clock started, threaded back out");
    }

    [Fact]
    public async Task Adapter_carries_the_requested_certification_into_the_gate()
    {
        var registry = new SingleProviderRegistry(new NotFoundProvider());
        var adapter = new WorkloadUpdateHealthGateAdapter([new RecordingRolloutCertifier(RecordingRolloutCertifier.SessionMode)], registry, new FixedClock(ApplyAt.AddSeconds(30)));

        WorkloadHealthGateTickRequest Request(WorkloadHealthGateCertification certification) => new()
        {
            Reference = Ref,
            InstanceId = InstanceId,
            ExpectedSpecHash = "sha256:applied",
            ApplyTimestampUtc = ApplyAt,
            ToVersion = "0.3.1",
            CurrentExtensions = ReeveTestExtensions.Pair(),
            CandidateExtensions = ReeveTestExtensions.Pair(),
            Timeout = TimeSpan.FromMinutes(5),
            Certification = certification,
            PriorClockState = new(),
        };

        (await adapter.EvaluateTickAsync(Request(WorkloadHealthGateCertification.HostEvidence)))
            .Verdict.Should().Be(WorkloadHealthGateVerdict.Pending);

        (await adapter.EvaluateTickAsync(Request(WorkloadHealthGateCertification.AppliedSpec)))
            .Verdict.Should().Be(WorkloadHealthGateVerdict.Passed);
    }

    [Fact]
    public async Task An_unknown_certification_throws_rather_than_choosing_a_gate()
    {
        var registry = new SingleProviderRegistry(new ReadyProvider());
        var adapter = new WorkloadUpdateHealthGateAdapter([], registry, new FixedClock(ApplyAt.AddSeconds(30)));

        var act = () => adapter.EvaluateTickAsync(new WorkloadHealthGateTickRequest
        {
            Reference = Ref,
            InstanceId = InstanceId,
            ExpectedSpecHash = "sha256:applied",
            ApplyTimestampUtc = ApplyAt,
            CurrentExtensions = WorkloadExtensions.None,
            CandidateExtensions = WorkloadExtensions.None,
            Timeout = TimeSpan.FromMinutes(5),
            Certification = (WorkloadHealthGateCertification)42,
            PriorClockState = new(),
        });

        await act.Should().ThrowAsync<ArgumentOutOfRangeException>();
    }

    [Fact]
    public async Task With_no_certifier_a_gate_that_requires_evidence_times_out_rather_than_pausing()
    {
        var registry = new SingleProviderRegistry(new ReadyProvider());
        var now = ApplyAt.AddMinutes(30);
        var adapter = new WorkloadUpdateHealthGateAdapter([], registry, new FixedClock(now));

        var result = await adapter.EvaluateTickAsync(new WorkloadHealthGateTickRequest
        {
            Reference = Ref,
            InstanceId = InstanceId,
            ExpectedSpecHash = "sha256:applied",
            ApplyTimestampUtc = ApplyAt,
            CurrentExtensions = WorkloadExtensions.None,
            CandidateExtensions = WorkloadExtensions.None,
            Timeout = TimeSpan.FromMinutes(5),
            Certification = WorkloadHealthGateCertification.HostEvidence,
            PriorClockState = new() { ClockStartedAtUtc = ApplyAt.AddSeconds(30) },
        });

        result.Verdict.Should().Be(WorkloadHealthGateVerdict.Failed);
        result.FailureReason.Should().Be(nameof(HealthGateFailureReason.Timeout));
    }

    private sealed class FixedClock(DateTimeOffset now) : IWorkloadClock
    {
        public DateTimeOffset UtcNow { get; } = now;
    }

    private sealed class ReadyProvider : IWorkloadProvider
    {
        public WorkloadProviderDescriptor Descriptor { get; } = new() { Id = KubernetesProvider.Id };
        public Task<WorkloadApplyResult> ApplyAsync(WorkloadDesiredSpec spec, IProgress<WorkloadApplyProgress>? progress, CancellationToken cancellationToken = default, bool forceConflicts = false) => throw new NotSupportedException();
        public Task TeardownAsync(WorkloadRef reference, bool retainVolumes, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task SuspendAsync(WorkloadRef reference, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task ResumeAsync(WorkloadRef reference, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<WorkloadStatus> GetStatusAsync(WorkloadRef reference, CancellationToken cancellationToken = default)
            => Task.FromResult(new WorkloadStatus { Phase = WorkloadProviderPhase.Ready, ProviderReady = true });
        public Task<WorkloadPreflightResult> PreflightAsync(WorkloadDesiredSpec spec, CancellationToken cancellationToken = default) => Task.FromResult(WorkloadPreflightResult.Ok());
        public Task<WorkloadLogs> GetRecentLogsAsync(WorkloadRef reference, int tailLines, bool previousContainer, CancellationToken cancellationToken = default) => Task.FromResult(WorkloadLogs.Empty());

        public Task<WorkloadAttachmentExistence> GetAttachmentExistenceAsync(
            IReadOnlyList<string> attachments, CancellationToken cancellationToken = default)
            => Task.FromResult(WorkloadAttachmentExistence.Read([]));
    }

    private sealed class NotFoundProvider : IWorkloadProvider
    {
        public WorkloadProviderDescriptor Descriptor { get; } = new() { Id = KubernetesProvider.Id };
        public Task<WorkloadApplyResult> ApplyAsync(WorkloadDesiredSpec spec, IProgress<WorkloadApplyProgress>? progress, CancellationToken cancellationToken = default, bool forceConflicts = false) => throw new NotSupportedException();
        public Task TeardownAsync(WorkloadRef reference, bool retainVolumes, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task SuspendAsync(WorkloadRef reference, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task ResumeAsync(WorkloadRef reference, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<WorkloadStatus> GetStatusAsync(WorkloadRef reference, CancellationToken cancellationToken = default)
            => Task.FromResult(WorkloadStatus.NotFound("No Kubernetes workload object matches the instance label selector."));
        public Task<WorkloadPreflightResult> PreflightAsync(WorkloadDesiredSpec spec, CancellationToken cancellationToken = default) => Task.FromResult(WorkloadPreflightResult.Ok());
        public Task<WorkloadLogs> GetRecentLogsAsync(WorkloadRef reference, int tailLines, bool previousContainer, CancellationToken cancellationToken = default) => Task.FromResult(WorkloadLogs.Empty());
        public Task<WorkloadAttachmentExistence> GetAttachmentExistenceAsync(
            IReadOnlyList<string> attachments, CancellationToken cancellationToken = default)
            => Task.FromResult(WorkloadAttachmentExistence.Read([]));
    }

    private sealed class SingleProviderRegistry(IWorkloadProvider provider) : IWorkloadProviderRegistry
    {
        public IWorkloadProvider? TryResolve(WorkloadProviderId p) => provider;
        public IWorkloadProvider Resolve(WorkloadProviderId p) => provider;
        public IReadOnlyCollection<WorkloadProviderId> RegisteredProviders => [provider.Provider];

        public IReadOnlyCollection<WorkloadProviderDescriptor> Descriptors
            => [.. RegisteredProviders.Select(Describe)];

        public WorkloadProviderDescriptor Describe(WorkloadProviderId kind)
            => TryResolve(kind)?.Descriptor ?? ShippedProviders.For(kind);
    }


}
