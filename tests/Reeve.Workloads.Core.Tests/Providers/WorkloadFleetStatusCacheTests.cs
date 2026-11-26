using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Reeve.Workloads.Abstractions;
using Reeve.Workloads.Abstractions.Contracts;
using Reeve.Workloads.Abstractions.Contracts.Deployment;
using Reeve.Workloads.Abstractions.Domain.DesiredSpec;
using Reeve.Workloads.Abstractions.Domain.Entities;
using Reeve.Workloads.Abstractions.Providers;
using Reeve.Workloads.Core.Providers;
using Xunit;
using Reeve.Workloads.Providers.Docker;
using Reeve.Workloads.Providers.Kubernetes;

namespace Reeve.Workloads.Core.Tests.Providers;

public sealed class WorkloadFleetStatusCacheTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 16, 4, 0, 0, TimeSpan.Zero);

    [Fact]
    public void A_primed_status_is_handed_out_exactly_once()
    {
        var (cache, _) = NewCache();
        var reference = Reference();
        cache.Prime(new Dictionary<WorkloadRef, WorkloadStatus> { [reference] = Ready() });

        cache.TryConsume(reference, out var first).Should().BeTrue();
        first.Phase.Should().Be(WorkloadProviderPhase.Ready);

        cache.TryConsume(reference, out _).Should().BeFalse(
            "the second read of an instance this pass is the one after an apply — it must reach the provider");
    }

    [Fact]
    public void A_status_older_than_the_freshness_window_is_not_evidence()
    {
        var (cache, clock) = NewCache(freshnessSeconds: 15);
        var reference = Reference();
        cache.Prime(new Dictionary<WorkloadRef, WorkloadStatus> { [reference] = Ready() });

        clock.Now = Now + TimeSpan.FromSeconds(15);

        cache.TryConsume(reference, out _).Should().BeFalse(
            "a pass can sit on a settling instance for minutes; a status from before that is not about the runtime now");
        cache.Count.Should().Be(0, "stale for one instance is stale for every instance — they were read in one call");
    }

    [Fact]
    public void A_status_inside_the_freshness_window_is_still_evidence()
    {
        var (cache, clock) = NewCache(freshnessSeconds: 15);
        var reference = Reference();
        cache.Prime(new Dictionary<WorkloadRef, WorkloadStatus> { [reference] = Ready() });

        clock.Now = Now + TimeSpan.FromSeconds(14);

        cache.TryConsume(reference, out _).Should().BeTrue();
    }

    [Fact]
    public void Priming_replaces_what_the_previous_pass_left_behind()
    {
        var (cache, _) = NewCache();
        var stale = Reference();
        cache.Prime(new Dictionary<WorkloadRef, WorkloadStatus> { [stale] = Ready() });

        cache.Prime(new Dictionary<WorkloadRef, WorkloadStatus>());

        cache.TryConsume(stale, out _).Should().BeFalse(
            "an entry nothing consumed belongs to a pass that has ended");
    }

    [Fact]
    public async Task Only_providers_that_declare_batching_are_asked()
    {
        var batching = new RecordingProvider(KubernetesProvider.Id, readsInBatches: true);
        var oneAtATime = new RecordingProvider(DockerProvider.Id, readsInBatches: false);
        var (cache, _) = NewCache();

        await cache.PrimeAsync(
            new WorkloadProviderRegistry([batching, oneAtATime]),
            [Instance(KubernetesProvider.Id), Instance(KubernetesProvider.Id),
             Instance(DockerProvider.Id), Instance(DockerProvider.Id)],
            CancellationToken.None);

        batching.BatchCalls.Should().Be(1);
        oneAtATime.BatchCalls.Should().Be(0);
        cache.Count.Should().Be(2, "only the batching provider's instances were read");
    }

    [Fact]
    public async Task A_provider_with_a_single_instance_is_not_worth_a_fleet_read()
    {
        var provider = new RecordingProvider(KubernetesProvider.Id, readsInBatches: true);
        var (cache, _) = NewCache();

        await cache.PrimeAsync(
            new WorkloadProviderRegistry([provider]),
            [Instance(KubernetesProvider.Id)],
            CancellationToken.None);

        provider.BatchCalls.Should().Be(0,
            "a fleet read is one list query over the whole namespace — against one instance that is the wider call, not the cheaper one");
    }

    [Fact]
    public async Task An_instance_that_was_never_materialized_is_not_asked_about()
    {
        var provider = new RecordingProvider(KubernetesProvider.Id, readsInBatches: true);
        var (cache, _) = NewCache();
        var unbound = WorkloadInstanceRef.FromInstance(
            WorkloadInstance.Create(Guid.NewGuid(), Guid.NewGuid(), "acme.thread", "border-router", Now));

        await cache.PrimeAsync(
            new WorkloadProviderRegistry([provider]),
            [Instance(KubernetesProvider.Id), Instance(KubernetesProvider.Id), unbound],
            CancellationToken.None);

        provider.LastBatchSize.Should().Be(2, "an instance with no bound provider has no runtime object to read");
    }

    [Fact]
    public async Task A_provider_that_throws_costs_the_pass_a_round_trip_not_the_pass()
    {
        var provider = new ThrowingProvider();
        var (cache, _) = NewCache();

        var prime = async () => await cache.PrimeAsync(
            new WorkloadProviderRegistry([provider]),
            [Instance(KubernetesProvider.Id), Instance(KubernetesProvider.Id)],
            CancellationToken.None);

        await prime.Should().NotThrowAsync();
        cache.Count.Should().Be(0, "an empty cache is a pass that reads instance by instance — which is what it did before");
    }

    [Fact]
    public async Task The_callers_cancellation_is_not_swallowed()
    {
        var (cache, _) = NewCache();
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        var prime = async () => await cache.PrimeAsync(
            new WorkloadProviderRegistry([new ThrowingProvider(cancelling: true)]),
            [Instance(KubernetesProvider.Id), Instance(KubernetesProvider.Id)],
            cancelled.Token);

        await prime.Should().ThrowAsync<OperationCanceledException>(
            "a stopping unit must see its own cancellation, not a note that the provider faulted");
    }

    private static (WorkloadFleetStatusCache Cache, TestClock Clock) NewCache(int freshnessSeconds = 15)
    {
        var clock = new TestClock { Now = Now };
        var cache = new WorkloadFleetStatusCache(
            clock,
            Options.Create(new WorkloadReconcilerOptions { FleetStatusFreshnessSeconds = freshnessSeconds }),
            NullLogger<WorkloadFleetStatusCache>.Instance);
        return (cache, clock);
    }

    private static WorkloadInstanceRef Instance(WorkloadProviderId provider)
    {
        var instance = WorkloadInstance.Create(Guid.NewGuid(), Guid.NewGuid(), "acme.thread", "border-router", Now);
        instance.SetProvider(provider, Now);
        return WorkloadInstanceRef.FromInstance(instance);
    }

    private static WorkloadRef Reference() => new()
    {
        InstanceId = Guid.NewGuid(),
        GroupId = "acme.thread",
        WorkloadId = "border-router",
        Inst8 = "11111111",
        Provider = KubernetesProvider.Id,
    };

    private static WorkloadStatus Ready() => new() { Phase = WorkloadProviderPhase.Ready, ProviderReady = true };

    private sealed class TestClock : IWorkloadClock
    {
        public DateTimeOffset Now { get; set; }
        public DateTimeOffset UtcNow => Now;
    }

    private sealed class RecordingProvider(WorkloadProviderId id, bool readsInBatches) : StubProvider(id)
    {
        public override WorkloadProviderDescriptor Descriptor
            => ShippedProviders.For(Id) with { ReadsStatusInBatches = readsInBatches };

        public int BatchCalls { get; private set; }

        public int LastBatchSize { get; private set; }

        public override Task<IReadOnlyDictionary<WorkloadRef, WorkloadStatus>> GetStatusesAsync(
            IReadOnlyList<WorkloadRef> references, CancellationToken cancellationToken = default)
        {
            BatchCalls++;
            LastBatchSize = references.Count;

            var statuses = new Dictionary<WorkloadRef, WorkloadStatus>();
            foreach (var reference in references)
            {
                statuses[reference] = Ready();
            }

            return Task.FromResult<IReadOnlyDictionary<WorkloadRef, WorkloadStatus>>(statuses);
        }
    }

    private sealed class ThrowingProvider(bool cancelling = false) : StubProvider(KubernetesProvider.Id)
    {
        public override WorkloadProviderDescriptor Descriptor
            => ShippedProviders.For(Id) with { ReadsStatusInBatches = true };

        public override Task<IReadOnlyDictionary<WorkloadRef, WorkloadStatus>> GetStatusesAsync(
            IReadOnlyList<WorkloadRef> references, CancellationToken cancellationToken = default)
            => cancelling
                ? throw new OperationCanceledException(cancellationToken)
                : throw new InvalidOperationException("the API server hung up");
    }

    private abstract class StubProvider(WorkloadProviderId id) : IWorkloadProvider
    {
        protected WorkloadProviderId Id { get; } = id;

        public virtual WorkloadProviderDescriptor Descriptor => ShippedProviders.For(Id);

        public virtual Task<IReadOnlyDictionary<WorkloadRef, WorkloadStatus>> GetStatusesAsync(
            IReadOnlyList<WorkloadRef> references, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyDictionary<WorkloadRef, WorkloadStatus>>(
                new Dictionary<WorkloadRef, WorkloadStatus>());

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

        public Task<WorkloadStatus> GetStatusAsync(WorkloadRef reference, CancellationToken cancellationToken = default)
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
}
