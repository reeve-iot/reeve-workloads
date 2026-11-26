using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Reeve.Workloads.Abstractions;
using Reeve.Workloads.Abstractions.Providers;
using Reeve.Workloads.Abstractions.Update;
using Reeve.Workloads.Core.Update;
using Xunit;

namespace Reeve.Workloads.Core.Tests;

public sealed class WorkloadReconcileNudgeTests
{
    private static readonly Guid Instance = Guid.Parse("7f2b1c64-0d2a-4d2e-9d1f-5c0e6a3b8f11");

    private readonly FakeWorkloadClusterBus bus = new();
    private readonly RecordingReconcileTrigger trigger = new();

    private WorkloadReconcileNudge Nudge() => new(bus, trigger);

    [Fact]
    public async Task An_instance_nudge_reaches_the_peers_and_this_replica()
    {
        await Nudge().NudgeInstanceAsync(Instance, "workload-parameter-edit");

        bus.Published.Should().ContainSingle()
            .Which.Should().Be((
                WorkloadClusterChannels.ReconcileRequest,
                (object?)new WorkloadReconcileRequestMessage(Instance, "workload-parameter-edit")));
        trigger.Requests.Should().Equal((Instance, "workload-parameter-edit"));
    }

    [Fact]
    public async Task A_fleet_nudge_reaches_the_peers_and_this_replica()
    {
        await Nudge().NudgeAllAsync("workload-materialize");

        bus.Published.Should().ContainSingle()
            .Which.Should().Be((
                WorkloadClusterChannels.ReconcileRequest,
                (object?)new WorkloadReconcileRequestMessage(null, "workload-materialize")));
        trigger.Requests.Should().Equal(((Guid?)null, "workload-materialize"));
    }

    [Fact]
    public async Task Over_the_shipped_single_replica_bus_the_local_half_is_the_whole_delivery()
    {
        var overTheRealDefault = new WorkloadReconcileNudge(new InProcessWorkloadClusterBus(), trigger);

        await overTheRealDefault.NudgeInstanceAsync(Instance, "workload-suspend");
        await overTheRealDefault.NudgeAllAsync("workload-uninstall");

        trigger.Requests.Should().Equal(
            (Instance, "workload-suspend"),
            ((Guid?)null, "workload-uninstall"));
    }

    [Fact]
    public void Neither_half_may_be_null()
    {
        var withoutBus = () => new WorkloadReconcileNudge(null!, trigger);
        var withoutTrigger = () => new WorkloadReconcileNudge(bus, null!);

        withoutBus.Should().Throw<ArgumentNullException>().Which.ParamName.Should().Be("clusterBus");
        withoutTrigger.Should().Throw<ArgumentNullException>().Which.ParamName.Should().Be("trigger");
    }

    [Theory]
    [InlineData(WorkloadReconcileReasons.Presence, WorkloadReconcileTrigger.Presence)]
    [InlineData(WorkloadReconcileReasons.Watch, WorkloadReconcileTrigger.Watch)]
    [InlineData(WorkloadReconcileReasons.WatchResync, WorkloadReconcileTrigger.Watch)]
    [InlineData("workload-materialize", WorkloadReconcileTrigger.LocalNudge)]
    [InlineData("workload-provider-switch", WorkloadReconcileTrigger.LocalNudge)]
    [InlineData("anything a host invents", WorkloadReconcileTrigger.LocalNudge)]
    public void A_reason_is_attributed_to_its_trigger(string reason, WorkloadReconcileTrigger expected)
        => WorkloadReconcilerUnit.AttributionFor(reason).Should().Be(expected);
}

public sealed class ReconcileNudgeConstructorSelectionTests
{
    private readonly FakeWorkloadClusterBus bus = new();
    private readonly RecordingReconcileTrigger trigger = new();

    private ServiceProvider Container(Action<IServiceCollection> register)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IWorkloadClusterBus>(bus);
        services.AddSingleton<IWorkloadReconcileTrigger>(trigger);
        services.AddSingleton<ILogger<WorkloadProviderSwitchService>>(NullLogger<WorkloadProviderSwitchService>.Instance);
        foreach (var port in new[]
                 {
                     typeof(IWorkloadInstanceRepository), typeof(IWorkloadProviderRegistry),
                     typeof(IActiveWorkloadIntentProvider), typeof(IWorkloadSwitchTakeoverRenderer),
                     typeof(IWorkloadManagedOwnershipReleaser), typeof(IWorkloadLockProvider),
                     typeof(IWorkloadAuditSink), typeof(IWorkloadClock),
                 })
        {
            services.AddSingleton(port, Substitute.For([port], []));
        }

        register(services);
        return services.BuildServiceProvider();
    }

    [Fact]
    public async Task The_update_nudge_the_container_builds_reaches_this_replica()
    {
        using var container = Container(services => services.AddSingleton<IWorkloadUpdateReconcileNudge, WorkloadUpdateReconcileNudge>());

        await container.GetRequiredService<IWorkloadUpdateReconcileNudge>().NudgeAsync(Guid.Empty, "update-staged");

        trigger.Requests.Should().ContainSingle()
            .Which.Reason.Should().StartWith("update-staged:");
    }

    [Fact]
    public async Task The_update_nudge_the_container_builds_wakes_this_replicas_walk()
    {
        var walk = new RecordingWalkTrigger();
        using var container = Container(services =>
        {
            services.AddSingleton<IWorkloadUpdateWalkTrigger>(walk);
            services.AddSingleton<IWorkloadUpdateReconcileNudge, WorkloadUpdateReconcileNudge>();
        });

        await container.GetRequiredService<IWorkloadUpdateReconcileNudge>().NudgeAsync(Guid.Empty, "update-staged");

        walk.Wakes.Should().Be(1);
        bus.Published.Should().ContainSingle("the peers still hear it");
        trigger.Requests.Should().ContainSingle("and so does this replica's reconciler");
    }

    [Fact]
    public async Task A_bus_that_throws_does_not_cost_the_walk_its_wake()
    {
        var walk = new RecordingWalkTrigger();
        var throwing = Substitute.For<IWorkloadClusterBus>();
        throwing.PublishAsync(Arg.Any<string>(), Arg.Any<WorkloadReconcileRequestMessage>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new InvalidOperationException("broker down")));
        var nudge = new WorkloadUpdateReconcileNudge(throwing, trigger, walk);

        await FluentActions.Awaiting(() => nudge.NudgeAsync(Guid.Empty, "update-staged"))
            .Should().ThrowAsync<InvalidOperationException>();

        walk.Wakes.Should().Be(1);
    }

    private sealed class RecordingWalkTrigger : IWorkloadUpdateWalkTrigger
    {
        public int Wakes { get; private set; }

        public void Wake() => Wakes++;
    }

    [Fact]
    public void The_switch_service_the_container_builds_takes_the_trigger()
    {
        using var container = Container(services => services.AddSingleton<WorkloadProviderSwitchService>());

        container.GetRequiredService<WorkloadProviderSwitchService>().Should().NotBeNull();

        typeof(WorkloadProviderSwitchService).GetConstructors()
            .OrderByDescending(c => c.GetParameters().Length).First()
            .GetParameters().Select(p => p.ParameterType)
            .Should().Contain(typeof(IWorkloadReconcileTrigger));
    }
}
