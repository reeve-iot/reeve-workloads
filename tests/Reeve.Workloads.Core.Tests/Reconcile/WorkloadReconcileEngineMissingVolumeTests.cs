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

public sealed class WorkloadReconcileEngineMissingVolumeTests
{
    private const string GroupId = "acme.plugin";
    private const string WorkloadId = "web";
    private static readonly DateTimeOffset T0 = new(2026, 11, 4, 12, 0, 0, TimeSpan.Zero);

    private static readonly WorkloadStatus Present = new() { Phase = WorkloadProviderPhase.Running, ProviderReady = false };

    private static WorkloadStatus Missing(params string[] claims) => Present with { MissingVolumes = claims };

    [Fact]
    public async Task A_missing_volume_clears_the_applied_hash_and_the_next_pass_re_applies()
    {
        var provider = ProviderThatApplies();
        var engine = Engine(new FixedClock(T0), provider);
        var instance = Instance();

        StatusIs(provider, Present);
        await engine.ReconcileInstanceAsync(instance);
        instance.AppliedSpecHash.Should().NotBeNull("the first pass applied");

        StatusIs(provider, Missing(DeclaredClaim(provider)));
        await engine.ReconcileInstanceAsync(instance);
        instance.AppliedSpecHash.Should().BeNull("a deleted volume is drift the hash cannot see");
        await AppliesShouldBe(provider, 1);

        StatusIs(provider, Present);
        await engine.ReconcileInstanceAsync(instance);
        await AppliesShouldBe(provider, 2);
        instance.AppliedSpecHash.Should().NotBeNull();
    }

    [Fact]
    public async Task A_missing_claim_the_spec_does_not_declare_is_not_drift()
    {
        var provider = ProviderThatApplies();
        var engine = Engine(new FixedClock(T0), provider);
        var instance = Instance();

        StatusIs(provider, Present);
        await engine.ReconcileInstanceAsync(instance);
        var applied = instance.AppliedSpecHash;

        StatusIs(provider, Missing("claim-another-manager-mounted"));
        await engine.ReconcileInstanceAsync(instance);
        await engine.ReconcileInstanceAsync(instance);

        instance.AppliedSpecHash.Should().Be(applied, "an apply never creates that claim, so clearing would re-apply every pass");
        await AppliesShouldBe(provider, 1);
    }

    [Fact]
    public async Task A_pass_that_applied_keeps_the_hash_it_settled()
    {
        var provider = ProviderThatApplies();
        var engine = Engine(new FixedClock(T0), provider);
        var instance = Instance();
        StatusIs(provider, Present);
        await engine.ReconcileInstanceAsync(instance);
        var claim = DeclaredClaim(provider);
        instance.SetAppliedSpecHash(null, T0);

        StatusIs(provider, Missing(claim));
        await engine.ReconcileInstanceAsync(instance);

        await AppliesShouldBe(provider, 2);
        instance.AppliedSpecHash.Should().NotBeNull("the apply just created every claim the spec declares");
    }

    [Fact]
    public async Task An_observe_only_instance_keeps_its_applied_hash()
    {
        var provider = ProviderThatApplies();
        var engine = Engine(new FixedClock(T0), provider);
        var instance = Instance();

        StatusIs(provider, Present);
        await engine.ReconcileInstanceAsync(instance);
        var applied = instance.AppliedSpecHash;
        instance.SetObserveOnly(true, T0);

        StatusIs(provider, Missing(DeclaredClaim(provider)));
        await engine.ReconcileInstanceAsync(instance);

        instance.AppliedSpecHash.Should().Be(applied, "observe-only means the reconciler corrects no drift");
    }

    [Fact]
    public async Task A_suspended_instance_keeps_its_applied_hash()
    {
        var provider = ProviderThatApplies();
        var engine = Engine(new FixedClock(T0), provider);
        var instance = Instance();

        StatusIs(provider, Present);
        await engine.ReconcileInstanceAsync(instance);
        var applied = instance.AppliedSpecHash;
        instance.TransitionTo(WorkloadInstanceState.Suspended, WorkloadStateReason.OperatorPaused, "paused", T0);

        StatusIs(provider, Missing(DeclaredClaim(provider)));
        await engine.ReconcileInstanceAsync(instance);

        instance.AppliedSpecHash.Should().Be(applied, "a suspend is a hold, not a queue-then-apply");
    }

    [Fact]
    public async Task An_externally_managed_instance_keeps_its_applied_hash()
    {
        var provider = ProviderThatApplies();
        var engine = Engine(new FixedClock(T0), provider);
        var instance = Instance();

        StatusIs(provider, Present);
        await engine.ReconcileInstanceAsync(instance);
        var applied = instance.AppliedSpecHash;
        instance.TransitionTo(WorkloadInstanceState.Healthy, WorkloadStateReason.HealthObserved, null, T0);
        instance.TransitionTo(WorkloadInstanceState.Switching, WorkloadStateReason.HealthObserved, null, T0);
        instance.TransitionTo(WorkloadInstanceState.ExternallyManaged, WorkloadStateReason.ExternalHealthObserved, null, T0);

        StatusIs(provider, Missing(DeclaredClaim(provider)));
        await engine.ReconcileInstanceAsync(instance);

        instance.AppliedSpecHash.Should().Be(applied, "a released runtime is the operator's");
    }

    [Fact]
    public async Task A_paused_reconciler_keeps_the_applied_hash()
    {
        var provider = ProviderThatApplies();
        var instance = Instance();
        StatusIs(provider, Present);
        await Engine(new FixedClock(T0), provider).ReconcileInstanceAsync(instance);
        var applied = instance.AppliedSpecHash;

        StatusIs(provider, Missing(DeclaredClaim(provider)));
        await Engine(new FixedClock(T0), provider, reconciliationPaused: true).ReconcileInstanceAsync(instance);

        instance.AppliedSpecHash.Should().Be(applied, "a paused reconciler corrects no drift");
    }

    [Fact]
    public async Task An_unreported_volume_list_changes_nothing()
    {
        var provider = ProviderThatApplies();
        var engine = Engine(new FixedClock(T0), provider);
        var instance = Instance();

        StatusIs(provider, Present);
        await engine.ReconcileInstanceAsync(instance);
        await engine.ReconcileInstanceAsync(instance);

        await AppliesShouldBe(provider, 1);
    }

    private static string DeclaredClaim(IWorkloadProvider provider)
        => provider.ReceivedCalls()
            .Select(call => call.GetArguments().OfType<WorkloadDesiredSpec>().FirstOrDefault())
            .First(spec => spec is not null)!
            .Volumes.Should().ContainSingle().Subject.ResourceName;

    private static void StatusIs(IWorkloadProvider provider, WorkloadStatus status)
        => provider.GetStatusAsync(Arg.Any<WorkloadRef>(), Arg.Any<CancellationToken>()).Returns(Task.FromResult(status));

    private static Task AppliesShouldBe(IWorkloadProvider provider, int count)
        => provider.Received(count).ApplyAsync(
            Arg.Any<WorkloadDesiredSpec>(), Arg.Any<IProgress<WorkloadApplyProgress>?>(),
            Arg.Any<CancellationToken>(), Arg.Any<bool>());

    private static IWorkloadProvider ProviderThatApplies()
    {
        var provider = Substitute.For<IWorkloadProvider>();
        provider.Provider.Returns(ManualProvider.Id);
        provider.ApplyAsync(
                Arg.Any<WorkloadDesiredSpec>(), Arg.Any<IProgress<WorkloadApplyProgress>?>(),
                Arg.Any<CancellationToken>(), Arg.Any<bool>())
            .Returns(call => Task.FromResult(WorkloadApplyResult.Applied(call.Arg<WorkloadDesiredSpec>().SpecHash)));
        return provider;
    }

    private static WorkloadReconcileEngine Engine(IWorkloadClock clock, IWorkloadProvider provider, bool reconciliationPaused = false)
    {
        var registry = Substitute.For<IWorkloadProviderRegistry>();
        registry.Describe(Arg.Any<WorkloadProviderId>())
            .Returns(call => ShippedProviders.For(call.Arg<WorkloadProviderId>()));
        registry.TryResolve(Arg.Any<WorkloadProviderId>()).Returns(provider);

        var repository = Substitute.For<IWorkloadInstanceRepository>();
        var health = Substitute.For<IWorkloadHealthContributor>();
        health.ObserveAsync(Arg.Any<WorkloadHealthObservation>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new WorkloadHealthContribution()));

        return new WorkloadReconcileEngine(
            Inputs(reconciliationPaused),
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

    private static IWorkloadReconcileInputsProvider Inputs(bool reconciliationPaused)
    {
        var port = Substitute.For<IWorkloadReconcileInputsProvider>();
        port.GetInputsAsync(Arg.Any<WorkloadInstance>(), Arg.Any<CancellationToken>())
            .Returns(ReconcileInputsAnswers.Of(new WorkloadReconcileInputs
            {
                Namer = DefaultWorkloadResourceNamer.Instance,
                WorkloadManifest = Manifest(),
                ResolvedParameters = new Dictionary<string, string?>(StringComparer.Ordinal),
                Settings = new WorkloadOrchestratorSettings { Provider = ManualProvider.Id, ReconciliationPaused = reconciliationPaused },
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
        Volumes = [new WorkloadVolume { Id = "data", MountPath = "/data", Size = "64Mi", Criticality = VolumeCriticality.Cache }],
    };

    private static WorkloadInstance Instance()
    {
        var instance = WorkloadInstance.Create(Guid.NewGuid(), Guid.NewGuid(), GroupId, WorkloadId, T0);
        instance.TransitionTo(
            WorkloadInstanceState.PendingConfiguration, WorkloadStateReason.ConfigRequired, "consented", T0);
        instance.TransitionTo(WorkloadInstanceState.Deploying, WorkloadStateReason.Provisioning, "deploying", T0);
        return instance;
    }

    private sealed class FixedClock(DateTimeOffset now) : IWorkloadClock
    {
        public DateTimeOffset UtcNow => now;
    }
}
