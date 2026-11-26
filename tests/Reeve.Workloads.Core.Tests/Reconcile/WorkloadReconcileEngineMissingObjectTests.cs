using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Reeve.Workloads.Abstractions;
using Reeve.Workloads.Abstractions.Contracts;
using Reeve.Workloads.Abstractions.Contracts.Deployment;
using Reeve.Workloads.Abstractions.Domain;
using Reeve.Workloads.Abstractions.Domain.DesiredSpec;
using Reeve.Workloads.Abstractions.Domain.Entities;
using Reeve.Workloads.Abstractions.Providers;
using Reeve.Workloads.Core;
using Reeve.Workloads.Core.Providers;
using Reeve.Workloads.Core.Tests;
using Reeve.Workloads.Domain;
using Reeve.Workloads.Providers.Kubernetes;
using Reeve.Workloads.Providers.Manual;
using Reeve.Workloads.Tests.Shared;

namespace Reeve.Workloads.Core.Tests.Reconcile;

public sealed class WorkloadReconcileEngineMissingObjectTests
{
    private const string GroupId = "acme.plugin";
    private const string WorkloadId = "web";
    private static readonly DateTimeOffset T0 = new(2026, 11, 18, 12, 0, 0, TimeSpan.Zero);

    private static readonly WorkloadStatus Present = new() { Phase = WorkloadProviderPhase.Running, ProviderReady = false };
    private static readonly WorkloadStatus Ready = new() { Phase = WorkloadProviderPhase.Ready, ProviderReady = true };
    private static readonly WorkloadStatus Gone = WorkloadStatus.NotFound("No Kubernetes workload object matches the instance label selector.");
    private static readonly WorkloadStatus Unreachable = new() { Phase = WorkloadProviderPhase.Unknown, ProviderReady = false };

    [Fact]
    public async Task A_deleted_object_is_reported_failed_and_recreated_on_the_next_pass()
    {
        var world = new World();
        var instance = await world.DeployedInstanceAsync();

        world.StatusIs(Gone);
        var missing = await world.Engine().ReconcileInstanceAsync(instance);

        instance.State.Should().Be(WorkloadInstanceState.Failed);
        instance.StateMessage.Should().EndWith("Reeve recreates it.");
        instance.AppliedSpecHash.Should().BeNull("a deleted object is drift the spec hash cannot see");
        missing.RequeueAfter.Should().Be(TimeSpan.Zero, "the recreate should not wait for the next timer pass");
        await world.AppliesShouldBe(1);

        world.StatusIs(Present);
        var recreated = await world.Engine().ReconcileInstanceAsync(instance);

        recreated.Action.Should().Be(WorkloadReconcileAction.Applied);
        await world.AppliesShouldBe(2);
        instance.AppliedSpecHash.Should().NotBeNull();
        instance.State.Should().Be(WorkloadInstanceState.Deploying);
    }

    [Fact]
    public async Task A_failing_recreate_says_so_and_is_not_cleared_again()
    {
        var world = new World();
        var instance = await world.DeployedInstanceAsync();
        world.StatusIs(Gone);
        await world.Engine().ReconcileInstanceAsync(instance);

        world.ApplyFails(WorkloadApplyReason.Error);
        var retry = await world.Engine().ReconcileInstanceAsync(instance);

        await world.AppliesShouldBe(2);
        retry.RequeueAfter.Should().BeNull("a failed recreate retries on the ordinary cadence, not in a hot loop");
        instance.State.Should().Be(WorkloadInstanceState.Failed);
        instance.StateMessage.Should().EndWith("Reeve has not been able to apply it yet and keeps trying.",
            "the promise to recreate must not outlive a recreate that fails");
    }

    [Fact]
    public async Task Observe_only_switched_on_before_the_recreate_corrects_the_message()
    {
        var world = new World();
        var instance = await world.DeployedInstanceAsync();
        world.StatusIs(Gone);
        await world.Engine().ReconcileInstanceAsync(instance);

        instance.SetObserveOnly(true, T0);
        await world.Engine().ReconcileInstanceAsync(instance);

        await world.AppliesShouldBe(1);
        instance.StateMessage.Should().EndWith("Observe only is on, so Reeve does not recreate it.");
    }

    [Fact]
    public async Task A_recreate_that_meets_an_outage_recovers_when_the_provider_returns()
    {
        var world = new World();
        var instance = await world.DeployedInstanceAsync();
        world.StatusIs(Gone);
        await world.Engine().ReconcileInstanceAsync(instance);

        world.ApplyFails(WorkloadApplyReason.ProviderUnreachable);
        world.StatusIs(Unreachable);
        await world.Engine().ReconcileInstanceAsync(instance);
        instance.State.Should().Be(WorkloadInstanceState.Unknown);

        world.ApplySucceeds();
        world.StatusIs(Gone);
        var back = await world.Engine().ReconcileInstanceAsync(instance);
        back.Action.Should().Be(WorkloadReconcileAction.Transitioned, "an Unknown instance still does not apply");
        back.Reason.Should().Be(WorkloadReconcileReason.HealthEvaluated, "the pass reports the move it made");
        back.PendingApply.Should().BeTrue();
        instance.State.Should().Be(WorkloadInstanceState.Failed, "a provider that answers again ends Unknown");

        world.StatusIs(Present);
        await world.Engine().ReconcileInstanceAsync(instance);

        await world.AppliesShouldBe(3);
        instance.AppliedSpecHash.Should().NotBeNull();
        instance.State.Should().Be(WorkloadInstanceState.Deploying);
    }

    [Fact]
    public async Task An_object_removed_again_before_it_became_healthy_is_not_recreated_again()
    {
        var world = new World();
        var instance = await world.DeployedInstanceAsync();
        world.StatusIs(Gone);
        await world.Engine().ReconcileInstanceAsync(instance);
        world.Clock.Advance(TimeSpan.FromSeconds(5));
        world.StatusIs(Present);
        await world.Engine().ReconcileInstanceAsync(instance);
        await world.AppliesShouldBe(2);

        world.Clock.Advance(TimeSpan.FromSeconds(5));
        world.StatusIs(Gone);
        var removedAgain = await world.Engine().ReconcileInstanceAsync(instance);
        await world.Engine().ReconcileInstanceAsync(instance);

        await world.AppliesShouldBe(2);
        instance.AppliedSpecHash.Should().NotBeNull("something keeps removing it, so Reeve stops fighting it");
        removedAgain.RequeueAfter.Should().BeNull();
        instance.State.Should().Be(WorkloadInstanceState.Failed, "a Failed workload raises the outage alert");
        instance.StateMessage.Should().EndWith("so Reeve does not recreate it again. Find what removes it, then redeploy it.");
    }

    [Fact]
    public async Task An_object_removed_after_the_recreate_became_healthy_is_recreated_again()
    {
        var world = new World();
        var instance = await world.DeployedInstanceAsync();
        world.StatusIs(Gone);
        await world.Engine().ReconcileInstanceAsync(instance);
        world.Clock.Advance(TimeSpan.FromSeconds(5));
        world.StatusIs(Present);
        await world.Engine().ReconcileInstanceAsync(instance);
        world.Clock.Advance(TimeSpan.FromSeconds(5));
        world.StatusIs(Ready);
        await world.Engine().ReconcileInstanceAsync(instance);
        instance.State.Should().Be(WorkloadInstanceState.Healthy);

        world.Clock.Advance(TimeSpan.FromSeconds(5));
        world.StatusIs(Gone);
        await world.Engine().ReconcileInstanceAsync(instance);

        instance.AppliedSpecHash.Should().BeNull();
    }

    [Fact]
    public async Task A_pass_that_applied_never_clears_the_hash_it_just_set()
    {
        var world = new World();
        var instance = await world.DeployedInstanceAsync();
        instance.SetAppliedSpecHash("an-older-spec", T0);

        world.StatusIs(Gone);
        var outcome = await world.Engine().ReconcileInstanceAsync(instance);

        await world.AppliesShouldBe(2);
        instance.AppliedSpecHash.Should().NotBeNull("clearing it in the pass that applied would re-apply every pass");
        outcome.RequeueAfter.Should().BeNull();
        instance.StateMessage.Should().Be(Gone.Message, "a pass that applied makes no recreate decision at all");
    }

    [Fact]
    public async Task An_intent_replay_owns_the_missing_object()
    {
        var world = new World();
        var instance = await world.DeployedInstanceAsync();
        var applied = instance.AppliedSpecHash;
        var spec = world.LastAppliedSpec();
        world.Intents.TryGetActiveReplayAsync(instance.GroupInstanceId, WorkloadId, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<ActiveWorkloadIntentReplay?>(new ActiveWorkloadIntentReplay(
                spec, WorkloadUpdateDirection.Forward, new Dictionary<string, string?>())));

        world.StatusIs(Gone);
        await world.Engine().ReconcileInstanceAsync(instance);

        instance.AppliedSpecHash.Should().Be(applied);
        await world.AppliesShouldBe(1);
    }

    [Theory]
    [InlineData(WorkloadInstanceState.Unknown)]
    [InlineData(WorkloadInstanceState.Switching)]
    [InlineData(WorkloadInstanceState.AdoptionPending)]
    public async Task An_instance_outside_the_recreating_states_keeps_its_hash(WorkloadInstanceState state)
    {
        var world = new World();
        var instance = await world.DeployedInstanceAsync();
        var applied = instance.AppliedSpecHash;
        if (state == WorkloadInstanceState.AdoptionPending)
        {
            instance.TransitionTo(WorkloadInstanceState.PendingConfiguration, WorkloadStateReason.ConfigRequired, null, T0);
        }
        instance.TransitionTo(state, WorkloadStateReason.ConfigRequired, null, T0);

        world.StatusIs(Gone);
        var outcome = await world.Engine().ReconcileInstanceAsync(instance);

        instance.AppliedSpecHash.Should().Be(applied);
        outcome.RequeueAfter.Should().BeNull();
        await world.AppliesShouldBe(1);
    }

    [Fact]
    public async Task A_scan_per_node_workload_without_jobs_is_not_recreated()
    {
        var world = new World(kind: WorkloadKind.ScanPerNode);
        var instance = await world.DeployedInstanceAsync(Present);
        var applied = instance.AppliedSpecHash;

        world.StatusIs(Gone);
        await world.Engine().ReconcileInstanceAsync(instance);

        instance.AppliedSpecHash.Should().Be(applied, "a scan workload has no object between runs");
        instance.State.Should().NotBe(WorkloadInstanceState.Failed);
    }

    [Fact]
    public async Task Observe_only_reports_the_missing_object_and_does_not_recreate_it()
    {
        var world = new World();
        var instance = await world.DeployedInstanceAsync();
        var applied = instance.AppliedSpecHash;
        instance.SetObserveOnly(true, T0);

        world.StatusIs(Gone);
        var outcome = await world.Engine().ReconcileInstanceAsync(instance);

        instance.AppliedSpecHash.Should().Be(applied);
        instance.State.Should().Be(WorkloadInstanceState.Failed);
        instance.StateMessage.Should().EndWith("Observe only is on, so Reeve does not recreate it.");
        outcome.RequeueAfter.Should().BeNull();
        await world.AppliesShouldBe(1);
    }

    [Fact]
    public async Task Turning_observe_only_off_recreates_the_missing_object()
    {
        var world = new World();
        var instance = await world.DeployedInstanceAsync();
        instance.SetObserveOnly(true, T0);
        world.StatusIs(Gone);
        await world.Engine().ReconcileInstanceAsync(instance);

        instance.SetObserveOnly(false, T0);
        await world.Engine().ReconcileInstanceAsync(instance);
        await world.Engine().ReconcileInstanceAsync(instance);

        await world.AppliesShouldBe(2);
    }

    [Fact]
    public async Task Paused_reconciliation_reports_the_missing_object_and_does_not_recreate_it()
    {
        var world = new World();
        var instance = await world.DeployedInstanceAsync();
        var applied = instance.AppliedSpecHash;

        world.StatusIs(Gone);
        await world.Engine(reconciliationPaused: true).ReconcileInstanceAsync(instance);

        instance.AppliedSpecHash.Should().Be(applied);
        instance.StateMessage.Should().EndWith("Reconciliation is paused, so Reeve does not recreate it.");
        await world.AppliesShouldBe(1);
    }

    [Fact]
    public async Task A_suspended_instance_is_not_recreated()
    {
        var world = new World();
        var instance = await world.DeployedInstanceAsync();
        var applied = instance.AppliedSpecHash;
        instance.TransitionTo(WorkloadInstanceState.Suspended, WorkloadStateReason.OperatorPaused, "paused", T0);

        world.StatusIs(Gone);
        await world.Engine().ReconcileInstanceAsync(instance);

        instance.AppliedSpecHash.Should().Be(applied, "a pause is a hold");
        instance.State.Should().Be(WorkloadInstanceState.Suspended);
        await world.AppliesShouldBe(1);
    }

    [Fact]
    public async Task An_externally_managed_instance_is_not_recreated()
    {
        var world = new World();
        var instance = await world.DeployedInstanceAsync();
        var applied = instance.AppliedSpecHash;
        instance.TransitionTo(WorkloadInstanceState.Healthy, WorkloadStateReason.HealthObserved, null, T0);
        instance.TransitionTo(WorkloadInstanceState.Switching, WorkloadStateReason.HealthObserved, null, T0);
        instance.TransitionTo(WorkloadInstanceState.ExternallyManaged, WorkloadStateReason.ExternalHealthObserved, null, T0);

        world.StatusIs(Gone);
        await world.Engine().ReconcileInstanceAsync(instance);

        instance.AppliedSpecHash.Should().Be(applied, "a released runtime is the operator's");
        await world.AppliesShouldBe(1);
    }

    [Fact]
    public async Task An_unreachable_provider_is_not_read_as_a_missing_object()
    {
        var world = new World();
        var instance = await world.DeployedInstanceAsync();
        var applied = instance.AppliedSpecHash;

        world.StatusIs(Unreachable);
        await world.Engine().ReconcileInstanceAsync(instance);

        instance.AppliedSpecHash.Should().Be(applied);
        instance.State.Should().Be(WorkloadInstanceState.Unknown);
        await world.AppliesShouldBe(1);
    }

    [Fact]
    public async Task A_status_read_that_throws_is_not_read_as_a_missing_object()
    {
        var world = new World();
        var instance = await world.DeployedInstanceAsync();
        var applied = instance.AppliedSpecHash;

        world.StatusThrows();
        await world.Engine().ReconcileInstanceAsync(instance);

        instance.AppliedSpecHash.Should().Be(applied);
        instance.State.Should().Be(WorkloadInstanceState.Unknown);
    }

    [Fact]
    public async Task An_update_in_flight_owns_the_missing_object()
    {
        var world = new World();
        var instance = await world.DeployedInstanceAsync();
        var applied = instance.AppliedSpecHash;
        world.Intents.GetActiveIntentIdAsync(instance.GroupInstanceId, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<Guid?>(Guid.NewGuid()));

        world.StatusIs(Gone);
        await world.Engine().ReconcileInstanceAsync(instance);
        await world.Engine().ReconcileInstanceAsync(instance);

        instance.AppliedSpecHash.Should().Be(applied, "an update may remove the object on purpose, for example while rolling back");
        await world.AppliesShouldBe(1);
    }

    [Fact]
    public async Task An_unreadable_update_intent_does_not_recreate()
    {
        var world = new World();
        var instance = await world.DeployedInstanceAsync();
        var applied = instance.AppliedSpecHash;
        world.Intents.GetActiveIntentIdAsync(instance.GroupInstanceId, Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("database unreachable"));

        world.StatusIs(Gone);
        await world.Engine().ReconcileInstanceAsync(instance);

        instance.AppliedSpecHash.Should().Be(applied);
    }

    [Fact]
    public async Task A_manual_workload_is_never_recreated()
    {
        var world = new World(ManualProvider.Descriptor);
        var instance = await world.DeployedInstanceAsync(Present);
        var applied = instance.AppliedSpecHash;

        world.StatusIs(Gone);
        await world.Engine().ReconcileInstanceAsync(instance);

        instance.AppliedSpecHash.Should().Be(applied, "Reeve does not run a manual workload, so it has nothing to recreate");
        await world.AppliesShouldBe(1);
    }

    [Fact]
    public async Task A_primed_not_found_is_confirmed_by_a_direct_read()
    {
        var world = new World();
        var instance = await world.DeployedInstanceAsync();
        var applied = instance.AppliedSpecHash;
        var fleet = new WorkloadFleetStatusCache(
            new FixedClock(T0),
            Options.Create(new WorkloadReconcilerOptions()),
            NullLogger<WorkloadFleetStatusCache>.Instance);
        fleet.Prime(new Dictionary<WorkloadRef, WorkloadStatus> { [WorkloadRef.FromInstance(instance)] = Gone });

        world.StatusIs(Present);
        await world.Engine(fleetStatus: fleet).ReconcileInstanceAsync(instance);

        instance.AppliedSpecHash.Should().Be(applied, "the object came back after the fleet read");
        instance.State.Should().NotBe(WorkloadInstanceState.Failed);
    }

    [Fact]
    public async Task A_status_read_right_after_an_apply_ignores_the_status_primed_before_it()
    {
        var world = new World();
        var instance = await world.DeployedInstanceAsync();
        world.Clock.Advance(TimeSpan.FromSeconds(5));
        instance.TransitionTo(WorkloadInstanceState.Deploying, WorkloadStateReason.Redeploying, "redeploy", world.Clock.UtcNow);
        instance.SetAppliedSpecHash(null, world.Clock.UtcNow);
        var lastHealthy = instance.LastHealthyAtUtc;
        var fleet = new WorkloadFleetStatusCache(
            new FixedClock(T0),
            Options.Create(new WorkloadReconcilerOptions()),
            NullLogger<WorkloadFleetStatusCache>.Instance);
        fleet.Prime(new Dictionary<WorkloadRef, WorkloadStatus> { [WorkloadRef.FromInstance(instance)] = Ready });

        world.StatusIs(Present);
        await world.Engine(fleetStatus: fleet).ReconcileInstanceAsync(instance);

        await world.AppliesShouldBe(2);
        instance.State.Should().Be(WorkloadInstanceState.Deploying, "the new spec was never observed ready");
        instance.LastHealthyAtUtc.Should().Be(lastHealthy);
        fleet.TryConsume(WorkloadRef.FromInstance(instance), out _).Should().BeFalse("the stale entry is discarded");
    }

    private sealed class World(WorkloadProviderDescriptor? descriptor = null, WorkloadKind kind = WorkloadKind.Service)
    {
        private readonly WorkloadProviderDescriptor descriptor = descriptor ?? KubernetesProvider.Descriptor;

        public SettableClock Clock { get; } = new(T0);

        public IWorkloadProvider Provider { get; } = Substitute.For<IWorkloadProvider>();

        public IActiveWorkloadIntentProvider Intents { get; } = Substitute.For<IActiveWorkloadIntentProvider>();

        public async Task<WorkloadInstance> DeployedInstanceAsync(WorkloadStatus? settled = null)
        {
            Provider.Provider.Returns(descriptor.Id);
            ApplySucceeds();
            Intents.GetActiveIntentIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult<Guid?>(null));

            var instance = WorkloadInstance.Create(Guid.NewGuid(), Guid.NewGuid(), GroupId, WorkloadId, T0);
            instance.TransitionTo(
                WorkloadInstanceState.PendingConfiguration, WorkloadStateReason.ConfigRequired, "consented", T0);
            instance.TransitionTo(WorkloadInstanceState.Deploying, WorkloadStateReason.Provisioning, "deploying", T0);

            StatusIs(Present);
            await Engine().ReconcileInstanceAsync(instance);
            instance.AppliedSpecHash.Should().NotBeNull("the first pass applied");
            Clock.Advance(TimeSpan.FromSeconds(5));
            StatusIs(settled ?? Ready);
            await Engine().ReconcileInstanceAsync(instance);
            if (settled is null)
            {
                instance.State.Should().Be(WorkloadInstanceState.Healthy, "the workload served before it was removed");
            }

            Clock.Advance(TimeSpan.FromSeconds(5));
            return instance;
        }

        public void ApplySucceeds()
            => Provider.ApplyAsync(
                    Arg.Any<WorkloadDesiredSpec>(), Arg.Any<IProgress<WorkloadApplyProgress>?>(),
                    Arg.Any<CancellationToken>(), Arg.Any<bool>())
                .Returns(call => Task.FromResult(WorkloadApplyResult.Applied(call.Arg<WorkloadDesiredSpec>().SpecHash)));

        public WorkloadDesiredSpec LastAppliedSpec()
            => Provider.ReceivedCalls()
                .Select(call => call.GetArguments().OfType<WorkloadDesiredSpec>().FirstOrDefault())
                .Last(spec => spec is not null)!;

        public void StatusIs(WorkloadStatus status)
            => Provider.GetStatusAsync(Arg.Any<WorkloadRef>(), Arg.Any<CancellationToken>()).Returns(Task.FromResult(status));

        public void StatusThrows()
            => Provider.GetStatusAsync(Arg.Any<WorkloadRef>(), Arg.Any<CancellationToken>())
                .ThrowsAsync(new HttpRequestException("connection refused"));

        public void ApplyFails(WorkloadApplyReason reason)
            => Provider.ApplyAsync(
                    Arg.Any<WorkloadDesiredSpec>(), Arg.Any<IProgress<WorkloadApplyProgress>?>(),
                    Arg.Any<CancellationToken>(), Arg.Any<bool>())
                .Returns(Task.FromResult(WorkloadApplyResult.Failed(reason, "the apply did not land")));

        public Task AppliesShouldBe(int count)
            => Provider.Received(count).ApplyAsync(
                Arg.Any<WorkloadDesiredSpec>(), Arg.Any<IProgress<WorkloadApplyProgress>?>(),
                Arg.Any<CancellationToken>(), Arg.Any<bool>());

        public WorkloadReconcileEngine Engine(bool reconciliationPaused = false, WorkloadFleetStatusCache? fleetStatus = null)
        {
            var registry = Substitute.For<IWorkloadProviderRegistry>();
            registry.Describe(Arg.Any<WorkloadProviderId>())
                .Returns(call => ShippedProviders.For(call.Arg<WorkloadProviderId>()));
            registry.TryResolve(Arg.Any<WorkloadProviderId>()).Returns(Provider);

            var health = Substitute.For<IWorkloadHealthContributor>();
            health.ObserveAsync(Arg.Any<WorkloadHealthObservation>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(new WorkloadHealthContribution()));

            return new WorkloadReconcileEngine(
                Inputs(reconciliationPaused),
                Resolver(),
                registry,
                Substitute.For<IWorkloadInstanceRepository>(),
                new Reeve.Workloads.Core.Health.WorkloadHealthContributorDispatch([health]),
                Clock,
                NullLogger<WorkloadReconcileEngine>.Instance,
                Substitute.For<IRetainedVolumeService>(),
                Intents,
                new NoWorkloadParameterReferenceResolver(),
                new WorkloadParameterSecretCodec(new Microsoft.AspNetCore.DataProtection.EphemeralDataProtectionProvider()),
                fleetStatus,
                new UnpinnedReferenceRolloutGuard(new Microsoft.AspNetCore.DataProtection.EphemeralDataProtectionProvider()),
                new NoCredentialPort());
        }

        private IWorkloadReconcileInputsProvider Inputs(bool reconciliationPaused)
        {
            var port = Substitute.For<IWorkloadReconcileInputsProvider>();
            port.GetInputsAsync(Arg.Any<WorkloadInstance>(), Arg.Any<CancellationToken>())
                .Returns(ReconcileInputsAnswers.Of(new WorkloadReconcileInputs
                {
                    Namer = DefaultWorkloadResourceNamer.Instance,
                    WorkloadManifest = Manifest(kind),
                    ResolvedParameters = new Dictionary<string, string?>(StringComparer.Ordinal),
                    Settings = new WorkloadOrchestratorSettings { Provider = descriptor.Id, ReconciliationPaused = reconciliationPaused },
                    Bootstrap = new WorkloadBootstrapContext { InstanceId = Guid.NewGuid(), SecretName = "reeve-wl-secret" },
                    EffectiveProvider = descriptor,
                    ProductImages = Images(),
                }));
            return port;
        }
    }

    private static IDesiredSpecResolver Resolver()
    {
        var resolver = Substitute.For<IDesiredSpecResolver>();
        resolver.ResolveAsync(
                Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<Func<WorkloadDesiredSpec>>(),
                Arg.Any<CancellationToken>())
            .Returns(call => Task.FromResult(call.Arg<Func<WorkloadDesiredSpec>>()()));
        return resolver;
    }

    private static ProductImageOptions Images()
    {
        var options = new ProductImageOptions();
        options.Components["controller"] = new ProductImageEntry
        {
            Ref = "example.test/controller",
            Digest = "sha256:" + new string('1', 64),
        };
        return options;
    }

    private static WorkloadManifest Manifest(WorkloadKind kind) => new()
    {
        Id = WorkloadId,
        DisplayName = WorkloadId,
        Image = new WorkloadImage { Component = "controller" },
        Kind = kind,
        Health = new WorkloadHealth { Mode = WorkloadHealthMode.Container },
    };

    private sealed class FixedClock(DateTimeOffset now) : IWorkloadClock
    {
        public DateTimeOffset UtcNow => now;
    }

    private sealed class SettableClock(DateTimeOffset start) : IWorkloadClock
    {
        public DateTimeOffset UtcNow { get; private set; } = start;

        public void Advance(TimeSpan by) => UtcNow += by;
    }
}
