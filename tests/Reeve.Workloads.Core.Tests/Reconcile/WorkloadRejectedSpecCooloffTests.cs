using Reeve.Workloads.Domain;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Reeve.Workloads.Abstractions;
using Reeve.Workloads.Abstractions.Contracts;
using Reeve.Workloads.Abstractions.Contracts.Deployment;
using Reeve.Workloads.Abstractions.Domain;
using Reeve.Workloads.Abstractions.Domain.DesiredSpec;
using Reeve.Workloads.Abstractions.Domain.Entities;
using Reeve.Workloads.Abstractions.Providers;
using Reeve.Workloads.Core;
using Reeve.Workloads.Core.Tests;
using Reeve.Workloads.Providers.Manual;
using Reeve.Workloads.Tests.Shared;

namespace Reeve.Workloads.Core.Tests.Reconcile;

public sealed class WorkloadRejectedSpecCooloffTests
{
    private const string GroupId = "acme.plugin";
    private const string WorkloadId = "web";
    private static readonly DateTimeOffset T0 = new(2026, 9, 10, 12, 0, 0, TimeSpan.Zero);

    private static readonly TimeSpan Cooloff = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task A_second_rejection_re_arms_the_cool_off_instead_of_leaving_it_expired_forever()
    {
        var clock = new MovableClock(T0);
        var provider = ProviderThatRejects();
        var engine = Engine(clock, provider);
        var instance = Instance();

        await engine.ReconcileInstanceAsync(instance);
        await provider.Received(1).ApplyAsync(
            Arg.Any<WorkloadDesiredSpec>(), Arg.Any<IProgress<WorkloadApplyProgress>?>(),
            Arg.Any<CancellationToken>(), Arg.Any<bool>());

        clock.Now = T0 + Cooloff + TimeSpan.FromSeconds(1);
        await engine.ReconcileInstanceAsync(instance);
        await provider.Received(2).ApplyAsync(
            Arg.Any<WorkloadDesiredSpec>(), Arg.Any<IProgress<WorkloadApplyProgress>?>(),
            Arg.Any<CancellationToken>(), Arg.Any<bool>());

        clock.Now = T0 + Cooloff + TimeSpan.FromSeconds(2);
        await engine.ReconcileInstanceAsync(instance);

        await provider.Received(2).ApplyAsync(
            Arg.Any<WorkloadDesiredSpec>(), Arg.Any<IProgress<WorkloadApplyProgress>?>(),
            Arg.Any<CancellationToken>(), Arg.Any<bool>());
    }

    [Fact]
    public async Task Every_rejection_moves_the_rejection_stamp_forward()
    {
        var clock = new MovableClock(T0);
        var engine = Engine(clock, ProviderThatRejects());
        var instance = Instance();

        await engine.ReconcileInstanceAsync(instance);
        instance.LastRejectedApplyAtUtc.Should().Be(T0);

        clock.Now = T0 + Cooloff + TimeSpan.FromSeconds(1);
        await engine.ReconcileInstanceAsync(instance);

        instance.LastRejectedApplyAtUtc.Should().Be(clock.Now,
            "the stamp is the only thing a repeat rejection can move — the transition into Failed is a no-op by then");
    }

    [Fact]
    public async Task A_repeat_rejection_leaves_the_transition_clock_alone()
    {
        var clock = new MovableClock(T0);
        var engine = Engine(clock, ProviderThatRejects());
        var instance = Instance();

        await engine.ReconcileInstanceAsync(instance);
        var firstTransition = instance.LastTransitionAtUtc;

        clock.Now = T0 + Cooloff + TimeSpan.FromSeconds(1);
        await engine.ReconcileInstanceAsync(instance);

        instance.LastTransitionAtUtc.Should().Be(firstTransition,
            "the repairs scanner anchors stuck-detection on this clock; a workload rejected for an hour must not "
            + "keep looking freshly transitioned, which is exactly why the cool-off cannot be timed from it");
    }

    [Fact]
    public async Task Inside_the_cool_off_the_provider_is_not_asked_again()
    {
        var clock = new MovableClock(T0);
        var provider = ProviderThatRejects();
        var engine = Engine(clock, provider);
        var instance = Instance();

        await engine.ReconcileInstanceAsync(instance);

        foreach (var offset in new[] { 1, 5, 29 })
        {
            clock.Now = T0 + TimeSpan.FromSeconds(offset);
            await engine.ReconcileInstanceAsync(instance);
        }

        await provider.Received(1).ApplyAsync(
            Arg.Any<WorkloadDesiredSpec>(), Arg.Any<IProgress<WorkloadApplyProgress>?>(),
            Arg.Any<CancellationToken>(), Arg.Any<bool>());
    }

    [Fact]
    public async Task The_boundary_itself_still_holds_and_the_instant_past_it_does_not()
    {
        var clock = new MovableClock(T0);
        var provider = ProviderThatRejects();
        var engine = Engine(clock, provider);
        var instance = Instance();

        await engine.ReconcileInstanceAsync(instance);

        clock.Now = T0 + Cooloff;
        await engine.ReconcileInstanceAsync(instance);

        await provider.Received(2).ApplyAsync(
            Arg.Any<WorkloadDesiredSpec>(), Arg.Any<IProgress<WorkloadApplyProgress>?>(),
            Arg.Any<CancellationToken>(), Arg.Any<bool>());
    }

    [Fact]
    public async Task A_field_manager_conflict_arms_the_same_throttle()
    {
        var clock = new MovableClock(T0);
        var provider = ProviderThatAnswers(
            WorkloadApplyResult.Conflict("kubectl", "another manager owns spec.replicas"));
        var engine = Engine(clock, provider);
        var instance = Instance();

        await engine.ReconcileInstanceAsync(instance);

        instance.LastRejectedApplyAtUtc.Should().Be(T0,
            "a 409 from another field manager is as unresendable as an admission rejection — it takes the same branch");

        clock.Now = T0 + TimeSpan.FromSeconds(1);
        await engine.ReconcileInstanceAsync(instance);

        await provider.Received(1).ApplyAsync(
            Arg.Any<WorkloadDesiredSpec>(), Arg.Any<IProgress<WorkloadApplyProgress>?>(),
            Arg.Any<CancellationToken>(), Arg.Any<bool>());
    }

    [Fact]
    public async Task An_unreachable_provider_is_not_throttled_because_re_sending_can_genuinely_succeed()
    {
        var clock = new MovableClock(T0);
        var provider = ProviderThatAnswers(WorkloadApplyResult.Failed(
            WorkloadApplyReason.ProviderUnreachable, "docker socket lost"));
        var engine = Engine(clock, provider);
        var instance = Instance();

        await engine.ReconcileInstanceAsync(instance);
        instance.LastRejectedApplyAtUtc.Should().BeNull("nothing rejected the spec — the daemon was simply not there");

        clock.Now = T0 + TimeSpan.FromSeconds(1);
        await engine.ReconcileInstanceAsync(instance);

        await provider.Received(2).ApplyAsync(
            Arg.Any<WorkloadDesiredSpec>(), Arg.Any<IProgress<WorkloadApplyProgress>?>(),
            Arg.Any<CancellationToken>(), Arg.Any<bool>());
    }

    [Fact]
    public async Task A_row_that_predates_the_stamp_is_stamped_by_the_one_apply_it_is_allowed()
    {
        var clock = new MovableClock(T0);
        var provider = ProviderThatRejects();
        var engine = Engine(clock, provider);

        var instance = Instance(WorkloadInstanceState.Deploying);
        instance.TransitionTo(WorkloadInstanceState.Failed, WorkloadStateReason.SpecRejected, "rejected", T0);
        instance.LastRejectedApplyAtUtc.Should().BeNull();

        clock.Now = T0 + TimeSpan.FromSeconds(1);
        await engine.ReconcileInstanceAsync(instance);
        instance.LastRejectedApplyAtUtc.Should().Be(clock.Now, "that one apply is what stamps the row");

        clock.Now = T0 + TimeSpan.FromSeconds(2);
        await engine.ReconcileInstanceAsync(instance);

        await provider.Received(1).ApplyAsync(
            Arg.Any<WorkloadDesiredSpec>(), Arg.Any<IProgress<WorkloadApplyProgress>?>(),
            Arg.Any<CancellationToken>(), Arg.Any<bool>());
    }

    [Fact]
    public async Task A_rejected_first_apply_is_Failed_with_the_provider_message_and_throttled()
    {
        var clock = new MovableClock(T0);
        var provider = ProviderThatRejects();
        var engine = Engine(clock, provider);

        var instance = Instance(WorkloadInstanceState.PendingConfiguration);

        await engine.ReconcileInstanceAsync(instance);
        instance.State.Should().Be(WorkloadInstanceState.Failed);
        instance.StateReason.Should().Be(WorkloadStateReason.SpecRejected);
        instance.StateMessage.Should().Contain("port is already allocated");

        clock.Now = T0 + TimeSpan.FromSeconds(1);
        await engine.ReconcileInstanceAsync(instance);

        await provider.Received(1).ApplyAsync(
            Arg.Any<WorkloadDesiredSpec>(), Arg.Any<IProgress<WorkloadApplyProgress>?>(),
            Arg.Any<CancellationToken>(), Arg.Any<bool>());
    }

    [Fact]
    public async Task A_failed_first_apply_moves_to_Deploying_and_records_the_failure()
    {
        var clock = new MovableClock(T0);
        var engine = Engine(clock, ProviderThatAnswers(WorkloadApplyResult.Failed(
            WorkloadApplyReason.Error, "pull access denied for example.test/controller")));
        var instance = Instance(WorkloadInstanceState.PendingConfiguration);

        await engine.ReconcileInstanceAsync(instance);

        instance.State.Should().Be(WorkloadInstanceState.Deploying);
        instance.StateMessage.Should().Be("Apply failed: pull access denied for example.test/controller");
        instance.DeployStartedAtUtc.Should().Be(T0);
    }

    [Fact]
    public async Task A_first_apply_that_keeps_failing_escalates_to_Degraded_once_the_deploy_stalls()
    {
        var clock = new MovableClock(T0);
        var engine = Engine(clock, ProviderThatAnswers(WorkloadApplyResult.Failed(
            WorkloadApplyReason.Error, "pull access denied")), materializes: true);
        var instance = Instance(WorkloadInstanceState.PendingConfiguration);

        await engine.ReconcileInstanceAsync(instance);
        clock.Now = T0 + new WorkloadReconcilerOptions().DeployStalledAfter - TimeSpan.FromSeconds(1);
        await engine.ReconcileInstanceAsync(instance);
        instance.State.Should().Be(WorkloadInstanceState.Deploying, "the deploy clock runs from the first attempt");

        clock.Now = T0 + new WorkloadReconcilerOptions().DeployStalledAfter + TimeSpan.FromSeconds(1);
        await engine.ReconcileInstanceAsync(instance);

        instance.State.Should().Be(WorkloadInstanceState.Degraded);
        instance.StateMessage.Should().StartWith("Deploy stalled for over").And.Contain("pull access denied");
    }

    [Fact]
    public async Task An_accepted_apply_clears_the_stamp_so_a_later_apply_is_not_held_by_an_old_rejection()
    {
        var clock = new MovableClock(T0);
        var provider = ProviderThatRejects();
        var engine = Engine(clock, provider);
        var instance = Instance(WorkloadInstanceState.Deploying);

        await engine.ReconcileInstanceAsync(instance);
        instance.LastRejectedApplyAtUtc.Should().Be(T0);

        provider.ApplyAsync(
                Arg.Any<WorkloadDesiredSpec>(), Arg.Any<IProgress<WorkloadApplyProgress>?>(),
                Arg.Any<CancellationToken>(), Arg.Any<bool>())
            .Returns(call => Task.FromResult(
                WorkloadApplyResult.Applied(call.Arg<WorkloadDesiredSpec>().SpecHash)));

        clock.Now = T0 + Cooloff;
        await engine.ReconcileInstanceAsync(instance);

        instance.LastRejectedApplyAtUtc.Should().BeNull(
            "the throttle is about the LAST apply, not about any apply that ever failed — a stamp left behind "
            + "would go on holding applies long after the conflicting resource was gone");
    }

    [Fact]
    public void Marking_a_rejection_moves_the_stamp_and_the_row_clock_but_not_the_state()
    {
        var instance = Instance(WorkloadInstanceState.Deploying);

        instance.MarkSpecRejected(T0 + TimeSpan.FromMinutes(5));

        instance.LastRejectedApplyAtUtc.Should().Be(T0 + TimeSpan.FromMinutes(5));
        instance.UpdatedAtUtc.Should().Be(T0 + TimeSpan.FromMinutes(5));
        instance.State.Should().Be(WorkloadInstanceState.Deploying,
            "the stamp records a provider answer; the caller still owns the transition");
        instance.LastTransitionAtUtc.Should().Be(T0);
    }

    [Fact]
    public void A_self_transition_still_refuses_to_move_the_transition_clock()
    {
        var instance = Instance(WorkloadInstanceState.Deploying);
        instance.TransitionTo(WorkloadInstanceState.Failed, WorkloadStateReason.SpecRejected, "rejected", T0);

        instance.TransitionTo(
            WorkloadInstanceState.Failed, WorkloadStateReason.SpecRejected, "rejected again",
            T0 + TimeSpan.FromHours(1));

        instance.LastTransitionAtUtc.Should().Be(T0,
            "this is the behaviour the whole fix works around — if it ever changes, the fallback and the stamp "
            + "both need revisiting, and the repairs scanner's stuck clock changes meaning");
        instance.StateMessage.Should().Be("rejected again", "a self-transition does refresh reason and message");
    }

    private static IWorkloadProvider ProviderThatRejects()
        => ProviderThatAnswers(WorkloadApplyResult.Failed(
            WorkloadApplyReason.Rejected, "Bind for 0.0.0.0:5179 failed: port is already allocated"));

    private static IWorkloadProvider ProviderThatAnswers(WorkloadApplyResult applyResult)
    {
        var provider = Substitute.For<IWorkloadProvider>();
        provider.Provider.Returns(ManualProvider.Id);
        provider.ApplyAsync(
                Arg.Any<WorkloadDesiredSpec>(), Arg.Any<IProgress<WorkloadApplyProgress>?>(),
                Arg.Any<CancellationToken>(), Arg.Any<bool>())
            .Returns(Task.FromResult(applyResult));
        provider.GetStatusAsync(Arg.Any<WorkloadRef>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new WorkloadStatus
            {
                Phase = WorkloadProviderPhase.NotFound,
                ProviderReady = false,
            }));
        return provider;
    }

    private static WorkloadReconcileEngine Engine(IWorkloadClock clock, IWorkloadProvider provider, bool materializes = false)
    {
        var registry = Substitute.For<IWorkloadProviderRegistry>();
        registry.Describe(Arg.Any<WorkloadProviderId>())
            .Returns(call => materializes
                ? ShippedProviders.For(call.Arg<WorkloadProviderId>()) with { Materializes = true }
                : ShippedProviders.For(call.Arg<WorkloadProviderId>()));
        registry.TryResolve(Arg.Any<WorkloadProviderId>()).Returns(provider);

        var repository = Substitute.For<IWorkloadInstanceRepository>();
        var health = Substitute.For<IWorkloadHealthContributor>();
        health.ObserveAsync(Arg.Any<WorkloadHealthObservation>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new WorkloadHealthContribution()));

        return new WorkloadReconcileEngine(
            Inputs(),
            Resolver(),
            registry,
            repository,
            new Reeve.Workloads.Core.Health.WorkloadHealthContributorDispatch([health]),
            clock,
            NullLogger<WorkloadReconcileEngine>.Instance,
            Substitute.For<IRetainedVolumeService>(),
            Substitute.For<IActiveWorkloadIntentProvider>(), new Reeve.Workloads.Core.NoWorkloadParameterReferenceResolver(), new Reeve.Workloads.Core.WorkloadParameterSecretCodec(new Microsoft.AspNetCore.DataProtection.EphemeralDataProtectionProvider()), null,
            new Reeve.Workloads.Core.UnpinnedReferenceRolloutGuard(new Microsoft.AspNetCore.DataProtection.EphemeralDataProtectionProvider()), new Reeve.Workloads.Abstractions.NoCredentialPort());
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

    private static IWorkloadReconcileInputsProvider Inputs()
    {
        var port = Substitute.For<IWorkloadReconcileInputsProvider>();
        port.GetInputsAsync(Arg.Any<WorkloadInstance>(), Arg.Any<CancellationToken>())
            .Returns(ReconcileInputsAnswers.Of(new WorkloadReconcileInputs
            {
                Namer = DefaultWorkloadResourceNamer.Instance,
                WorkloadManifest = Manifest(),
                ResolvedParameters = new Dictionary<string, string?>(StringComparer.Ordinal),
                Settings = new WorkloadOrchestratorSettings { Provider = ManualProvider.Id },
                Bootstrap = new WorkloadBootstrapContext { InstanceId = Guid.NewGuid(), SecretName = "reeve-wl-secret" },
                EffectiveProvider = ManualProvider.Descriptor,
                ProductImages = Images(),
            }));
        return port;
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

    private static WorkloadManifest Manifest() => new()
    {
        Id = WorkloadId,
        DisplayName = WorkloadId,
        Image = new WorkloadImage { Component = "controller" },
        Kind = WorkloadKind.Service,
        Health = new WorkloadHealth { Mode = WorkloadHealthMode.Container },
    };

    private static WorkloadInstance Instance(
        WorkloadInstanceState from = WorkloadInstanceState.Deploying)
    {
        var instance = WorkloadInstance.Create(Guid.NewGuid(), Guid.NewGuid(), GroupId, WorkloadId, T0);
        instance.TransitionTo(
            WorkloadInstanceState.PendingConfiguration, WorkloadStateReason.ConfigRequired, "consented", T0);
        if (from != WorkloadInstanceState.PendingConfiguration)
        {
            instance.TransitionTo(from, WorkloadStateReason.Provisioning, "deploying", T0);
        }

        return instance;
    }

    private sealed class MovableClock(DateTimeOffset now) : IWorkloadClock
    {
        public DateTimeOffset Now { get; set; } = now;

        public DateTimeOffset UtcNow => Now;
    }
}
