using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Reeve.Workloads.Abstractions;
using Reeve.Workloads.Abstractions.Contracts;
using Reeve.Workloads.Abstractions.Domain.Entities;
using Reeve.Workloads.Core;
using Reeve.Workloads.Providers.Kubernetes;
using Reeve.Workloads.Providers.Docker;
using Reeve.Workloads.Providers.Manual;

namespace Reeve.Workloads.Core.Tests;

public sealed class WorkloadProviderSwitchServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 10, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid InstanceId = Guid.Parse("3c9d1f70-5a2b-4e18-9c6d-8b7a4f2e1d05");
    private static readonly Guid GroupInstanceId = Guid.Parse("a1b2c3d4-e5f6-4708-9a0b-1c2d3e4f5061");
    private static readonly Guid ActorId = Guid.Parse("11111111-2222-4333-8444-555555555555");

    private readonly IWorkloadInstanceRepository repository = Substitute.For<IWorkloadInstanceRepository>();
    private readonly IActiveWorkloadIntentProvider intentProvider = Substitute.For<IActiveWorkloadIntentProvider>();
    private readonly IWorkloadSwitchTakeoverRenderer takeoverRenderer = Substitute.For<IWorkloadSwitchTakeoverRenderer>();
    private readonly IWorkloadManagedOwnershipReleaser ownershipReleaser = Substitute.For<IWorkloadManagedOwnershipReleaser>();
    private readonly IWorkloadLockProvider lockProvider = Substitute.For<IWorkloadLockProvider>();
    private readonly IWorkloadClusterBus clusterBus = Substitute.For<IWorkloadClusterBus>();
    private readonly IWorkloadAuditSink auditSink = Substitute.For<IWorkloadAuditSink>();
    private readonly IWorkloadClock clock = Substitute.For<IWorkloadClock>();

    private readonly List<WorkloadInstanceState> writtenStates = [];

    private readonly List<string> callOrder = [];

    public WorkloadProviderSwitchServiceTests()
    {
        clock.UtcNow.Returns(Now);
        lockProvider.TryAcquireAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IWorkloadLock?>(Substitute.For<IWorkloadLock>()));
        repository.When(r => r.UpdateAsync(Arg.Any<WorkloadInstance>(), Arg.Any<CancellationToken>()))
            .Do(call =>
            {
                call.Arg<CancellationToken>().ThrowIfCancellationRequested();
                writtenStates.Add(call.Arg<WorkloadInstance>().State);
            });
        takeoverRenderer.When(r => r.RenderTakeoverAsync(Arg.Any<WorkloadInstance>(), Arg.Any<CancellationToken>()))
            .Do(_ => callOrder.Add("render"));
        ownershipReleaser.When(r => r.ReleaseAsync(
                Arg.Any<WorkloadInstance>(), Arg.Any<WorkloadProviderId>(), Arg.Any<CancellationToken>()))
            .Do(_ => callOrder.Add("release"));
    }

    private WorkloadProviderSwitchService CreateService() => new(
        repository, ProviderRegistryFakes.FirstPartyRegistry(), intentProvider, takeoverRenderer, ownershipReleaser, lockProvider, clusterBus, auditSink, clock,
        NullLogger<WorkloadProviderSwitchService>.Instance);

    private static WorkloadInstance ManagedRunning(
        WorkloadInstanceState state = WorkloadInstanceState.Healthy,
        WorkloadProviderId? provider = null,
        bool unbound = false)
    {
        if (!unbound)
        {
            provider ??= DockerProvider.Id;
        }
        var instance = WorkloadInstance.Create(
            InstanceId, GroupInstanceId, "acme.zwave", "controller", Now - TimeSpan.FromDays(1));
        instance.TransitionTo(WorkloadInstanceState.PendingConfiguration, WorkloadStateReason.ConfigRequired, null, Now);
        instance.TransitionTo(WorkloadInstanceState.Deploying, WorkloadStateReason.Provisioning, null, Now);
        instance.TransitionTo(WorkloadInstanceState.Healthy, WorkloadStateReason.HealthObserved, null, Now);
        if (provider is { } bound)
        {
            instance.SetProvider(bound, Now);
        }

        if (state != WorkloadInstanceState.Healthy)
        {
            instance.TransitionTo(state, WorkloadStateReason.HealthLost, null, Now);
        }

        return instance;
    }

    private void Seed(WorkloadInstance? instance)
        => repository.GetByIdAsync(InstanceId, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(instance));

    private Task<WorkloadProviderSwitchResult> SwitchAsync(CancellationToken cancellationToken = default)
        => CreateService().SwitchToManualAsync(InstanceId, ActorId, cancellationToken);

    [Fact]
    public async Task A_replica_that_cannot_take_the_switch_lock_does_not_switch()
    {
        Seed(ManagedRunning());
        lockProvider.TryAcquireAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IWorkloadLock?>(null));

        var result = await SwitchAsync();

        result.Outcome.Should().Be(WorkloadProviderSwitchOutcome.NotLeader);
        writtenStates.Should().BeEmpty("a non-leader must leave the row exactly as it found it");
        await repository.DidNotReceiveWithAnyArgs().GetByIdAsync(default, default);
    }

    [Fact]
    public async Task The_switch_lock_is_keyed_on_the_instance_being_switched()
    {
        Seed(ManagedRunning());

        await SwitchAsync();

        await lockProvider.Received(1).TryAcquireAsync(
            "workload-provider-switch:" + InstanceId.ToString("N"), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task An_unknown_instance_is_not_found()
    {
        Seed(null);

        (await SwitchAsync()).Outcome.Should().Be(WorkloadProviderSwitchOutcome.NotFound);
    }

    [Fact]
    public async Task An_active_update_intent_blocks_the_switch_and_the_intent_id_is_echoed()
    {
        var intentId = Guid.Parse("9f8e7d6c-5b4a-4392-8180-7f6e5d4c3b2a");
        Seed(ManagedRunning());
        intentProvider.GetActiveIntentIdAsync(GroupInstanceId, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<Guid?>(intentId));

        var result = await SwitchAsync();

        result.Outcome.Should().Be(WorkloadProviderSwitchOutcome.Conflict);
        result.ActiveIntentId.Should().Be(intentId);
        writtenStates.Should().BeEmpty();
    }

    [Theory]
    [InlineData(WorkloadInstanceState.Switching)]
    [InlineData(WorkloadInstanceState.ExternallyManaged)]
    [InlineData(WorkloadInstanceState.Suspended)]
    [InlineData(WorkloadInstanceState.Failed)]
    [InlineData(WorkloadInstanceState.Unknown)]
    [InlineData(WorkloadInstanceState.Terminating)]
    public async Task Only_a_managed_running_instance_may_begin_a_switch(WorkloadInstanceState blocking)
    {
        Seed(ManagedRunning(blocking));

        var result = await SwitchAsync();

        result.Outcome.Should().Be(WorkloadProviderSwitchOutcome.NotSwitchable);
        result.BlockingState.Should().Be(blocking);
        writtenStates.Should().BeEmpty();
    }

    [Theory]
    [InlineData(WorkloadInstanceState.Healthy)]
    [InlineData(WorkloadInstanceState.Degraded)]
    public async Task A_healthy_or_degraded_instance_switches(WorkloadInstanceState state)
    {
        Seed(ManagedRunning(state));

        (await SwitchAsync()).Outcome.Should().Be(WorkloadProviderSwitchOutcome.Success);
    }

    [Fact]
    public async Task An_instance_with_no_bound_provider_has_no_ownership_to_release()
    {
        Seed(ManagedRunning(unbound: true));

        var result = await SwitchAsync();

        result.Outcome.Should().Be(WorkloadProviderSwitchOutcome.NotSwitchable);
        writtenStates.Should().BeEmpty();
    }

    [Fact]
    public async Task An_instance_already_on_the_manual_provider_is_not_switchable()
    {
        Seed(ManagedRunning(provider: ManualProvider.Id));

        (await SwitchAsync()).Outcome.Should().Be(WorkloadProviderSwitchOutcome.NotSwitchable);
    }

    [Fact]
    public async Task The_switch_commits_twice_and_renders_the_takeover_before_releasing_ownership()
    {
        var instance = ManagedRunning();
        Seed(instance);

        var result = await SwitchAsync();

        result.Outcome.Should().Be(WorkloadProviderSwitchOutcome.Success);
        writtenStates.Should().Equal(
            WorkloadInstanceState.Switching,
            WorkloadInstanceState.ExternallyManaged);
        callOrder.Should().Equal("render", "release");
        instance.State.Should().Be(WorkloadInstanceState.ExternallyManaged);
    }

    [Fact]
    public async Task The_original_provider_is_preserved_so_re_adoption_can_re_derive_its_volume_names()
    {
        var instance = ManagedRunning(provider: KubernetesProvider.Id);
        Seed(instance);

        await SwitchAsync();

        instance.Provider.Should().Be(KubernetesProvider.Id);
        await ownershipReleaser.Received(1).ReleaseAsync(
            instance, KubernetesProvider.Id, Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("render")]
    [InlineData("release")]
    public async Task An_in_band_failure_unwinds_to_the_state_and_reason_it_started_from(string failing)
    {
        var instance = ManagedRunning(WorkloadInstanceState.Degraded);
        var originalReason = instance.StateReason;
        Seed(instance);
        if (failing == "render")
        {
            takeoverRenderer.RenderTakeoverAsync(Arg.Any<WorkloadInstance>(), Arg.Any<CancellationToken>())
                .Returns<Task>(_ => throw new IOException("cannot write the takeover manifests"));
        }
        else
        {
            ownershipReleaser.ReleaseAsync(
                    Arg.Any<WorkloadInstance>(), Arg.Any<WorkloadProviderId>(), Arg.Any<CancellationToken>())
                .Returns<Task>(_ => throw new IOException("api server refused the label strip"));
        }

        var result = await SwitchAsync();

        result.Outcome.Should().Be(WorkloadProviderSwitchOutcome.Failed);
        result.Error.Should().NotBeNullOrWhiteSpace("the operator has to be told it was rolled back, not done");
        writtenStates.Should().Equal(
            WorkloadInstanceState.Switching,
            WorkloadInstanceState.Degraded);
        instance.State.Should().Be(WorkloadInstanceState.Degraded);
        instance.StateReason.Should().Be(originalReason);
    }

    [Fact]
    public async Task A_cancellation_mid_switch_unwinds_before_it_propagates()
    {
        var instance = ManagedRunning();
        Seed(instance);
        using var cancellation = new CancellationTokenSource();
        takeoverRenderer.RenderTakeoverAsync(Arg.Any<WorkloadInstance>(), Arg.Any<CancellationToken>())
            .Returns<Task>(_ =>
            {
                cancellation.Cancel();
                throw new OperationCanceledException(cancellation.Token);
            });

        var act = async () => await SwitchAsync(cancellationToken: cancellation.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        writtenStates.Should().Equal(
            WorkloadInstanceState.Switching,
            WorkloadInstanceState.Healthy);
        instance.State.Should().Be(WorkloadInstanceState.Healthy);
    }

    [Fact]
    public async Task The_terminal_commit_survives_a_cancellation_that_lands_after_ownership_is_released()
    {
        var instance = ManagedRunning();
        Seed(instance);
        using var cancellation = new CancellationTokenSource();
        ownershipReleaser.When(r => r.ReleaseAsync(
                Arg.Any<WorkloadInstance>(), Arg.Any<WorkloadProviderId>(), Arg.Any<CancellationToken>()))
            .Do(_ => cancellation.Cancel());

        var result = await SwitchAsync(cancellationToken: cancellation.Token);

        result.Outcome.Should().Be(WorkloadProviderSwitchOutcome.Success);
        instance.State.Should().Be(WorkloadInstanceState.ExternallyManaged);
        writtenStates.Should().Equal(
            WorkloadInstanceState.Switching,
            WorkloadInstanceState.ExternallyManaged);
    }

    [Fact]
    public async Task An_audit_sink_that_throws_does_not_fail_the_committed_switch()
    {
        Seed(ManagedRunning());
        auditSink.WriteAsync(Arg.Any<WorkloadAuditEntry>(), Arg.Any<CancellationToken>())
            .Returns<Task>(_ => throw new TimeoutException("audit store unreachable"));

        var result = await SwitchAsync();

        result.Outcome.Should().Be(WorkloadProviderSwitchOutcome.Success);
    }

    [Fact]
    public async Task A_cluster_bus_that_throws_does_not_fail_the_committed_switch()
    {
        var instance = ManagedRunning();
        Seed(instance);
        clusterBus.PublishAsync(
                Arg.Any<string>(), Arg.Any<WorkloadReconcileRequestMessage>(), Arg.Any<CancellationToken>())
            .Returns<Task>(_ => throw new TimeoutException("redis unreachable"));

        var result = await SwitchAsync();

        result.Outcome.Should().Be(WorkloadProviderSwitchOutcome.Success);
        instance.State.Should().Be(WorkloadInstanceState.ExternallyManaged);
    }

    [Fact]
    public async Task The_audit_entry_names_the_instance_the_actor_and_the_preserved_provider()
    {
        Seed(ManagedRunning(provider: KubernetesProvider.Id));

        await SwitchAsync();

        await auditSink.Received(1).WriteAsync(
            Arg.Is<WorkloadAuditEntry>(e =>
                e.ActorUserId == ActorId
                && e.EntityId == InstanceId
                && e.Action == "workloads.provider.switched-to-manual"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task The_reconcile_nudge_names_the_switched_instance()
    {
        Seed(ManagedRunning());

        await SwitchAsync();

        await clusterBus.Received(1).PublishAsync(
            WorkloadClusterChannels.ReconcileRequest,
            Arg.Is<WorkloadReconcileRequestMessage>(m => m.InstanceId == InstanceId),
            Arg.Any<CancellationToken>());
    }

    private WorkloadInstance MarkStoredRowForTheHostsHoldDuring(string step)
    {
        var stored = ManagedRunning();
        stored.TransitionTo(WorkloadInstanceState.Switching, WorkloadStateReason.HealthObserved, null, Now);
        var marked = false;
        void Mark() => marked = true;
        if (step == "render")
        {
            takeoverRenderer.When(r => r.RenderTakeoverAsync(Arg.Any<WorkloadInstance>(), Arg.Any<CancellationToken>()))
                .Do(_ => Mark());
        }
        else
        {
            ownershipReleaser.When(r => r.ReleaseAsync(
                    Arg.Any<WorkloadInstance>(), Arg.Any<WorkloadProviderId>(), Arg.Any<CancellationToken>()))
                .Do(_ =>
                {
                    Mark();
                    throw new InvalidOperationException("release failed (test)");
                });
        }

        repository.When(r => r.UpdateAsync(Arg.Any<WorkloadInstance>(), Arg.Any<CancellationToken>()))
            .Do(call =>
            {
                if (marked && !ReferenceEquals(call.Arg<WorkloadInstance>(), stored))
                {
                    throw new WorkloadConcurrencyConflictException("written by the host's suspend (test)")
                    {
                        EntityId = InstanceId,
                    };
                }
            });
        repository.ReloadAsync(InstanceId, Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                stored.TransitionTo(WorkloadInstanceState.Switching, WorkloadStateReason.SuspendedByHost, "withdrawn", Now);
                return Task.FromResult<WorkloadInstance?>(stored);
            });
        return stored;
    }

    [Fact]
    public async Task A_switch_the_host_withdrew_mid_way_completes_into_the_hosts_hold()
    {
        Seed(ManagedRunning());
        var stored = MarkStoredRowForTheHostsHoldDuring("render");

        var result = await SwitchAsync();

        result.Outcome.Should().Be(WorkloadProviderSwitchOutcome.Success);
        stored.State.Should().Be(WorkloadInstanceState.Suspended);
        stored.StateReason.Should().Be(WorkloadStateReason.SuspendedByHostExternallyManaged,
            "the operator owns the runtime now, and the host's hold still stands");
        writtenStates.Last().Should().Be(WorkloadInstanceState.Suspended);
    }

    [Fact]
    public async Task A_switch_the_host_withdrew_mid_way_that_unwinds_lands_in_the_managed_hold()
    {
        Seed(ManagedRunning());
        var stored = MarkStoredRowForTheHostsHoldDuring("release");

        var result = await SwitchAsync();

        result.Outcome.Should().Be(WorkloadProviderSwitchOutcome.Failed);
        stored.State.Should().Be(WorkloadInstanceState.Suspended);
        stored.StateReason.Should().Be(WorkloadStateReason.SuspendedByHost, "the runtime stayed Reeve's, so it is held as one");
    }

    [Fact]
    public async Task A_switch_whose_row_an_uninstall_moved_to_terminating_mid_way_refuses_to_commit_and_leaves_it_terminating()
    {
        Seed(ManagedRunning());
        var stored = ManagedRunning();
        stored.TransitionTo(WorkloadInstanceState.Terminating, WorkloadStateReason.Uninstalling, "uninstalled", Now);
        repository.When(r => r.UpdateAsync(Arg.Any<WorkloadInstance>(), Arg.Any<CancellationToken>()))
            .Do(call =>
            {
                if (!ReferenceEquals(call.Arg<WorkloadInstance>(), stored))
                {
                    throw new WorkloadConcurrencyConflictException("written by the uninstall (test)")
                    {
                        EntityId = InstanceId,
                    };
                }
            });
        repository.ReloadAsync(InstanceId, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<WorkloadInstance?>(stored));

        var act = () => SwitchAsync();

        await act.Should().ThrowAsync<WorkloadConcurrencyConflictException>();
        stored.State.Should().Be(WorkloadInstanceState.Terminating);
        stored.StateReason.Should().Be(WorkloadStateReason.Uninstalling);
        await repository.DidNotReceive().UpdateAsync(stored, Arg.Any<CancellationToken>());
    }
}
