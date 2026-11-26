using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Reeve.Workloads.Abstractions;
using Reeve.Workloads.Abstractions.Contracts;
using Reeve.Workloads.Abstractions.Contracts.Deployment;
using Reeve.Workloads.Abstractions.Domain.DesiredSpec;
using Reeve.Workloads.Abstractions.Domain.Entities;
using Reeve.Workloads.Abstractions.Providers;
using Reeve.Workloads.Core;
using Reeve.Workloads.Domain;
using Reeve.Workloads.Domain.DesiredSpec;
using Reeve.Workloads.Providers.Manual;
using Reeve.Workloads.Testing;

namespace Reeve.Workloads.Core.Tests.Reconcile;

public sealed class WorkloadReconcileEngineIntentReadTests
{
    private const string GroupId = "acme.plugin";
    private const string WorkloadId = "web";

    [Fact]
    public async Task An_intent_that_appears_after_the_replay_read_does_not_become_the_desired_spec()
    {
        var instance = NewInstance();
        var target = Target(instance.Id, "intent-target-hash");
        var intentProvider = new AppearingIntentProvider(target);
        var provider = new InMemoryWorkloadProvider(ManualProvider.Descriptor);
        var engine = Engine(instance, provider, intentProvider, new DesiredSpecResolver(intentProvider));

        await engine.ReconcileInstanceAsync(instance);

        provider.LastAppliedSpec(instance.Id).Should().NotBeNull();
        provider.LastAppliedSpec(instance.Id)!.SpecHash.Should().NotBe(target.SpecHash);
        intentProvider.TargetReadCount.Should().Be(1);
    }

    [Fact]
    public async Task An_active_replay_is_the_desired_spec_and_the_resolver_is_not_asked()
    {
        var instance = NewInstance();
        var target = Target(instance.Id, "active-intent-hash");
        var replayProvider = new ActiveReplayProvider(target);
        var provider = new InMemoryWorkloadProvider(ManualProvider.Descriptor);
        var resolver = new ThrowingResolver();
        var engine = Engine(instance, provider, replayProvider, resolver);

        await engine.ReconcileInstanceAsync(instance);

        provider.LastAppliedSpec(instance.Id)!.SpecHash.Should().Be(target.SpecHash);
        resolver.WasCalled.Should().BeFalse();
    }

    [Fact]
    public async Task Replay_with_an_unrelated_submitted_parameter_deploys_decrypted_stored_password()
    {
        var instance = NewInstance();
        var codec = new WorkloadParameterSecretCodec(new Microsoft.AspNetCore.DataProtection.EphemeralDataProtectionProvider());
        var manifest = new WorkloadManifest
        {
            Id = WorkloadId, DisplayName = "Web",
            Image = new WorkloadImage { Ref = "registry.example/web", Digest = "sha256:" + new string('a', 64) },
            Kind = WorkloadKind.SingleNode,
            Health = new WorkloadHealth { Mode = new WorkloadHealthMode("adapter-session"), HeartbeatSeconds = 30 },
            Parameters = [new WorkloadParameter { Name = "dbPassword", Type = WorkloadParameterType.Password },
                new WorkloadParameter { Name = "logLevel", Type = WorkloadParameterType.Text }],
            Env = [new WorkloadEnv { Name = "DB_PASSWORD", From = "parameter:dbPassword" }],
        };
        var provider = new InMemoryWorkloadProvider(ManualProvider.Descriptor);
        var target = Target(instance.Id, "active-intent-hash") with
        {
            Env = [new ResolvedEnvVar { Name = "DB_PASSWORD", Kind = Reeve.Workloads.Abstractions.Domain.DesiredSpec.ResolvedEnvKind.SecretRef }],
        };
        var replay = new ActiveReplayProvider(target, new Dictionary<string, string?> { ["logLevel"] = "debug" });
        var stored = codec.Protect(manifest, new Dictionary<string, string?> { ["dbPassword"] = "clear-secret", ["logLevel"] = "info" });
        var engine = Engine(instance, provider, replay, new DesiredSpecResolver(replay), manifest, stored, codec);

        await engine.ReconcileInstanceAsync(instance);

        provider.LastAppliedSpec(instance.Id)!.SecretMaterial["DB_PASSWORD"].Should().Be("clear-secret");
    }

    [Fact]
    public async Task Reconcile_of_a_rebound_instance_keeps_the_literal_hash_and_does_not_recreate()
    {
        var now = new DateTimeOffset(2026, 10, 5, 12, 0, 0, TimeSpan.Zero);
        var instance = WorkloadInstance.Create(Guid.NewGuid(), Guid.NewGuid(), GroupId, WorkloadId, now);
        instance.SetProvider(ManualProvider.Id, now);
        instance.TransitionTo(WorkloadInstanceState.Deploying, WorkloadStateReason.Provisioning, null, now);
        instance.TransitionTo(WorkloadInstanceState.Healthy, WorkloadStateReason.HealthObserved, null, now);
        const string reference = "ref:vault:db#password";
        var manifest = new WorkloadManifest
        {
            Id = WorkloadId, DisplayName = "Web",
            Image = new WorkloadImage { Ref = "registry.example/web", Digest = "sha256:" + new string('a', 64) },
            Kind = WorkloadKind.SingleNode,
            Health = new WorkloadHealth { Mode = new WorkloadHealthMode("adapter-session"), HeartbeatSeconds = 30 },
            Parameters = [new WorkloadParameter { Name = "dbPassword", Type = WorkloadParameterType.Password }],
            Env = [new WorkloadEnv { Name = "DB_PASSWORD", From = "parameter:dbPassword" }],
        };
        var protection = new Microsoft.AspNetCore.DataProtection.EphemeralDataProtectionProvider();
        var guard = new UnpinnedReferenceRolloutGuard(protection);
        guard.PinHashToLiteral(instance, "dbPassword", reference, "same-secret", now);
        instance.SetResolvedParameters(new Dictionary<string, string?> { ["dbPassword"] = reference }, now);
        var inputs = new Dictionary<string, string?> { ["dbPassword"] = "same-secret" };
        var literalHash = WorkloadDesiredSpecFactory.ComputeWithParameterReferences(
            manifest,
            new WorkloadOrchestratorSettings { Provider = ManualProvider.Id },
            inputs,
            new Dictionary<string, string?>(),
            new WorkloadBootstrapContext { InstanceId = instance.Id, SecretName = "reeve-web-secret" },
            ManualProvider.Descriptor,
            DefaultWorkloadResourceNamer.Instance,
            instance.GroupInstanceId,
            instance.GroupId,
            instance.Id,
            "install-1").SpecHash;
        instance.SetAppliedSpecHash(literalHash, now);
        var provider = new InMemoryWorkloadProvider(ManualProvider.Descriptor);
        var deployed = WorkloadDesiredSpecFactory.ComputeWithParameterReferences(
            manifest,
            new WorkloadOrchestratorSettings { Provider = ManualProvider.Id },
            inputs,
            new Dictionary<string, string?>(),
            new WorkloadBootstrapContext { InstanceId = instance.Id, SecretName = "reeve-web-secret" },
            ManualProvider.Descriptor,
            DefaultWorkloadResourceNamer.Instance,
            instance.GroupInstanceId,
            instance.GroupId,
            instance.Id,
            "install-1");
        await provider.ApplyAsync(deployed, null);
        var resolver = Substitute.For<IWorkloadParameterReferenceResolver>();
        resolver.ResolveAsync(reference, Arg.Any<CancellationToken>())
            .Returns(new WorkloadReferenceResolution.Success("same-secret"));
        var engine = Engine(instance, provider, new NullReplayProvider(),
            new DesiredSpecResolver(new NullReplayProvider()), manifest,
            new Dictionary<string, string?> { ["dbPassword"] = reference },
            new WorkloadParameterSecretCodec(protection), resolver, rolloutGuard: guard, now: now);

        await engine.ReconcileInstanceAsync(instance);

        provider.ApplyCountFor(instance.Id).Should().Be(1);
        instance.AppliedSpecHash.Should().Be(literalHash);
    }

    [Fact]
    public async Task Undecryptable_stored_password_holds_configuration_on_engine_path()
    {
        var instance = NewInstance();
        var manifest = new WorkloadManifest
        {
            Id = WorkloadId, DisplayName = "Web",
            Image = new WorkloadImage { Ref = "registry.example/web", Digest = "sha256:" + new string('a', 64) },
            Kind = WorkloadKind.SingleNode,
            Health = new WorkloadHealth { Mode = new WorkloadHealthMode("adapter-session"), HeartbeatSeconds = 30 },
            Parameters = [new WorkloadParameter { Name = "dbPassword", Type = WorkloadParameterType.Password }],
        };
        var wrongCodec = new WorkloadParameterSecretCodec(new Microsoft.AspNetCore.DataProtection.EphemeralDataProtectionProvider());
        var wrongStored = wrongCodec.Protect(manifest, new Dictionary<string, string?> { ["dbPassword"] = "secret" });
        var provider = new InMemoryWorkloadProvider(ManualProvider.Descriptor);
        var engine = Engine(instance, provider, new NullReplayProvider(), new DesiredSpecResolver(new NullReplayProvider()),
            manifest, wrongStored,
            new WorkloadParameterSecretCodec(new Microsoft.AspNetCore.DataProtection.EphemeralDataProtectionProvider()));

        var result = await engine.ReconcileInstanceAsync(instance);

        result.Reason.Should().Be(Reeve.Workloads.Abstractions.WorkloadReconcileReason.ConfigurationRequired);
        instance.State.Should().Be(WorkloadInstanceState.PendingConfiguration);
        provider.IsTracked(instance.Id).Should().BeFalse();
    }

    [Fact]
    public async Task Same_reference_value_rotation_holds_even_when_desired_hash_is_stable_and_still_observes_health()
    {
        var now = new DateTimeOffset(2026, 9, 9, 12, 0, 0, TimeSpan.Zero);
        var instance = WorkloadInstance.Create(Guid.NewGuid(), Guid.NewGuid(), GroupId, WorkloadId, now);
        instance.SetProvider(ManualProvider.Id, now);
        instance.TransitionTo(WorkloadInstanceState.Deploying, WorkloadStateReason.Provisioning, null, now);
        instance.TransitionTo(WorkloadInstanceState.Healthy, WorkloadStateReason.HealthObserved, null, now);
        const string reference = "ref:vault:network";
        var references = new Dictionary<string, string?> { ["dbPassword"] = reference };
        var manifest = new WorkloadManifest
        {
            Id = WorkloadId, DisplayName = "Web",
            Image = new WorkloadImage { Ref = "registry.example/web", Digest = "sha256:" + new string('a', 64) },
            Kind = WorkloadKind.SingleNode,
            Health = new WorkloadHealth { Mode = new WorkloadHealthMode("adapter-session"), HeartbeatSeconds = 30 },
            Parameters = [new WorkloadParameter { Name = "dbPassword", Type = WorkloadParameterType.Password }],
        };
        var target = Target(instance.Id, "stable-applied-hash");
        var replay = new ActiveReplayProvider(target);
        var provider = new InMemoryWorkloadProvider(ManualProvider.Descriptor);
        await provider.ApplyAsync(target, null);
        instance.SetAppliedSpecHash(target.SpecHash, now);
        provider.ApplyCountFor(instance.Id).Should().Be(1);
        var guard = new UnpinnedReferenceRolloutGuard(new Microsoft.AspNetCore.DataProtection.EphemeralDataProtectionProvider());
        guard.RecordApplied(instance, references, new Dictionary<string, string?> { ["dbPassword"] = "old-secret" }, ["dbPassword"], now);
        var resolver = Substitute.For<IWorkloadParameterReferenceResolver>();
        resolver.ResolveAsync(reference, Arg.Any<CancellationToken>())
            .Returns(new WorkloadReferenceResolution.Success("rotated-secret"));
        var inputsRepository = Substitute.For<IWorkloadInstanceRepository>();
        inputsRepository.UpdateAsync(instance, Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        var health = Substitute.For<IWorkloadHealthContributor>();
        health.ObservedModes.Returns(new HashSet<WorkloadHealthMode> { new("adapter-session") });
        health.ObserveAsync(Arg.Any<WorkloadHealthObservation>(), Arg.Any<CancellationToken>())
            .Returns(new WorkloadHealthContribution());
        var engine = Engine(instance, provider, replay, new DesiredSpecResolver(replay), manifest,
            references, referenceResolver: resolver, instanceRepository: inputsRepository,
            healthContributor: health, rolloutGuard: guard, now: now);

        var result = await engine.ReconcileInstanceAsync(instance);

        result.Reason.Should().Be(Reeve.Workloads.Abstractions.WorkloadReconcileReason.ConfigurationRequired);
        result.ResultingState.Should().Be(WorkloadInstanceState.PendingConfiguration);
        instance.StateReason.Should().Be(WorkloadStateReason.ConfigRequired);
        provider.ApplyCountFor(instance.Id).Should().Be(1, "the stable hash must not permit the rotated secret to be applied");
        await health.Received(1).ObserveAsync(Arg.Any<WorkloadHealthObservation>(), Arg.Any<CancellationToken>());
        await inputsRepository.Received().UpdateAsync(instance, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_password_only_the_candidate_declares_is_held_on_an_unpinned_reference_during_forward_replay()
    {
        var now = new DateTimeOffset(2026, 9, 9, 12, 0, 0, TimeSpan.Zero);
        var instance = WorkloadInstance.Create(Guid.NewGuid(), Guid.NewGuid(), GroupId, WorkloadId, now);
        instance.SetProvider(ManualProvider.Id, now);
        instance.TransitionTo(WorkloadInstanceState.Deploying, WorkloadStateReason.Provisioning, null, now);
        instance.TransitionTo(WorkloadInstanceState.Healthy, WorkloadStateReason.HealthObserved, null, now);
        instance.SetAppliedSpecHash("installed-hash", now);
        const string reference = "ref:vault:admin";
        var installed = new WorkloadManifest
        {
            Id = WorkloadId, DisplayName = "Web",
            Image = new WorkloadImage { Ref = "registry.example/web", Digest = "sha256:" + new string('a', 64) },
            Kind = WorkloadKind.SingleNode,
            Health = new WorkloadHealth { Mode = new WorkloadHealthMode("adapter-session"), HeartbeatSeconds = 30 },
            Parameters = [new WorkloadParameter { Name = "dbPassword", Type = WorkloadParameterType.Password }],
        };
        var replay = new ActiveReplayProvider(Target(instance.Id, "candidate-hash"),
            new Dictionary<string, string?> { ["adminKey"] = reference }, ["dbPassword", "adminKey"]);
        var resolver = Substitute.For<IWorkloadParameterReferenceResolver>();
        resolver.ResolveAsync(reference, Arg.Any<CancellationToken>())
            .Returns(new WorkloadReferenceResolution.Success("admin-secret"));
        var repository = Substitute.For<IWorkloadInstanceRepository>();
        repository.UpdateAsync(instance, Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        var provider = new InMemoryWorkloadProvider(ManualProvider.Descriptor);
        var engine = Engine(instance, provider, replay, new DesiredSpecResolver(replay), installed,
            new Dictionary<string, string?> { ["dbPassword"] = "literal" },
            referenceResolver: resolver, instanceRepository: repository, now: now);

        var result = await engine.ReconcileInstanceAsync(instance);

        result.Reason.Should().Be(Reeve.Workloads.Abstractions.WorkloadReconcileReason.ConfigurationRequired);
        provider.ApplyCountFor(instance.Id).Should().Be(0, "the candidate's unpinned reference was never approved");
    }

    [Fact]
    public async Task Generic_inputs_failure_propagates_without_changing_instance_state()
    {
        var instance = NewInstance();
        var original = instance.State;
        var inputs = Substitute.For<IWorkloadReconcileInputsProvider>();
        inputs.GetInputsAsync(instance, Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromException<WorkloadReconcileInputsResult>(new InvalidOperationException("manifest store offline")));
        var engine = new WorkloadReconcileEngine(inputs, Substitute.For<IDesiredSpecResolver>(),
            Substitute.For<IWorkloadProviderRegistry>(), Substitute.For<IWorkloadInstanceRepository>(),
            new Reeve.Workloads.Core.Health.WorkloadHealthContributorDispatch([]), new FixedClock(),
            NullLogger<WorkloadReconcileEngine>.Instance, Substitute.For<IRetainedVolumeService>(),
            new NullReplayProvider(), new NoWorkloadParameterReferenceResolver(),
            new WorkloadParameterSecretCodec(new Microsoft.AspNetCore.DataProtection.EphemeralDataProtectionProvider()),
            null, new UnpinnedReferenceRolloutGuard(new Microsoft.AspNetCore.DataProtection.EphemeralDataProtectionProvider()), new Reeve.Workloads.Abstractions.NoCredentialPort());

        await FluentActions.Awaiting(() => engine.ReconcileInstanceAsync(instance))
            .Should().ThrowAsync<InvalidOperationException>().WithMessage("manifest store offline");
        instance.State.Should().Be(original);
    }

    private static WorkloadReconcileEngine Engine(
        WorkloadInstance instance,
        InMemoryWorkloadProvider workloadProvider,
        IActiveWorkloadIntentProvider intents,
        IDesiredSpecResolver resolver,
        WorkloadManifest? manifest = null,
        IReadOnlyDictionary<string, string?>? values = null,
        WorkloadParameterSecretCodec? codec = null,
        IWorkloadParameterReferenceResolver? referenceResolver = null,
        IWorkloadInstanceRepository? instanceRepository = null,
        IWorkloadHealthContributor? healthContributor = null,
        UnpinnedReferenceRolloutGuard? rolloutGuard = null,
        DateTimeOffset? now = null)
    {
        var inputs = Substitute.For<IWorkloadReconcileInputsProvider>();
        inputs.GetInputsAsync(instance, Arg.Any<CancellationToken>()).Returns(
            WorkloadReconcileInputsResult.Declared(new WorkloadReconcileInputs
            {
                Namer = DefaultWorkloadResourceNamer.Instance,
                WorkloadManifest = manifest ?? new WorkloadManifest
                {
                    Id = WorkloadId,
                    DisplayName = "Web",
                    Image = new WorkloadImage { Ref = "registry.example/web", Digest = "sha256:" + new string('a', 64) },
                    Kind = WorkloadKind.SingleNode,
                    Health = new WorkloadHealth { Mode = new WorkloadHealthMode("adapter-session"), HeartbeatSeconds = 30 },
                },
                ResolvedParameters = values ?? new Dictionary<string, string?>(),
                Settings = new WorkloadOrchestratorSettings { Provider = ManualProvider.Id },
                Bootstrap = new WorkloadBootstrapContext { InstanceId = instance.Id, SecretName = "reeve-web-secret" },
                EffectiveProvider = ManualProvider.Descriptor,
                InstallationId = "install-1",
            }));

        var registry = Substitute.For<IWorkloadProviderRegistry>();
        registry.Describe(Arg.Any<WorkloadProviderId>()).Returns(ManualProvider.Descriptor);
        registry.TryResolve(ManualProvider.Id).Returns(workloadProvider);
        var health = healthContributor ?? Substitute.For<IWorkloadHealthContributor>();
        health.ObserveAsync(Arg.Any<WorkloadHealthObservation>(), Arg.Any<CancellationToken>())
            .Returns(new WorkloadHealthContribution());
        return new WorkloadReconcileEngine(
            inputs,
            resolver,
            registry,
            instanceRepository ?? Substitute.For<IWorkloadInstanceRepository>(),
            new Reeve.Workloads.Core.Health.WorkloadHealthContributorDispatch([health]),
            new FixedClock(now ?? new DateTimeOffset(2026, 9, 9, 12, 0, 0, TimeSpan.Zero)),
            NullLogger<WorkloadReconcileEngine>.Instance,
            Substitute.For<IRetainedVolumeService>(),
            intents, referenceResolver ?? new Reeve.Workloads.Core.NoWorkloadParameterReferenceResolver(), codec ?? new Reeve.Workloads.Core.WorkloadParameterSecretCodec(new Microsoft.AspNetCore.DataProtection.EphemeralDataProtectionProvider()), null,
            rolloutGuard ?? new UnpinnedReferenceRolloutGuard(new Microsoft.AspNetCore.DataProtection.EphemeralDataProtectionProvider()), new Reeve.Workloads.Abstractions.NoCredentialPort());
    }

    private static WorkloadInstance NewInstance()
        => WorkloadInstance.Create(Guid.NewGuid(), Guid.NewGuid(), GroupId, WorkloadId, DateTimeOffset.UtcNow, false);

    private static WorkloadDesiredSpec Target(Guid instanceId, string hash)
    {
        var inst8 = WorkloadInstance.ComputeInst8(instanceId);
        return new WorkloadDesiredSpec
        {
            GroupInstanceId = Guid.NewGuid(),
            GroupId = GroupId,
            WorkloadId = WorkloadId,
            InstanceId = instanceId,
            Inst8 = inst8,
            Provider = ManualProvider.Id,
            ResourceName = "web-" + inst8,
            SecretName = "web-secret-" + inst8,
            Labels = new Dictionary<string, string>(),
            Image = "registry.example/web@sha256:" + new string('a', 64),
            Kind = WorkloadKind.SingleNode,
            Health = new WorkloadHealth { Mode = new WorkloadHealthMode("adapter-session"), HeartbeatSeconds = 30 },
            SpecHash = hash,
        };
    }

    private sealed class AppearingIntentProvider(WorkloadDesiredSpec target) : IActiveWorkloadIntentProvider
    {
        public int TargetReadCount { get; private set; }

        public Task<WorkloadDesiredSpec?> TryGetActiveTargetSpecAsync(Guid pluginInstanceId, string workloadId, CancellationToken cancellationToken)
        {
            TargetReadCount++;
            return Task.FromResult<WorkloadDesiredSpec?>(target);
        }

        public Task<ActiveWorkloadIntentReplay?> TryGetActiveReplayAsync(Guid pluginInstanceId, string workloadId, CancellationToken cancellationToken)
            => Task.FromResult<ActiveWorkloadIntentReplay?>(null);
    }

    private sealed class ActiveReplayProvider(
        WorkloadDesiredSpec target,
        IReadOnlyDictionary<string, string?>? submitted = null,
        IReadOnlyList<string>? passwordNames = null) : IActiveWorkloadIntentProvider
    {
        public Task<WorkloadDesiredSpec?> TryGetActiveTargetSpecAsync(Guid pluginInstanceId, string workloadId, CancellationToken cancellationToken)
            => Task.FromResult<WorkloadDesiredSpec?>(target);

        public Task<ActiveWorkloadIntentReplay?> TryGetActiveReplayAsync(Guid pluginInstanceId, string workloadId, CancellationToken cancellationToken)
            => Task.FromResult<ActiveWorkloadIntentReplay?>(new ActiveWorkloadIntentReplay(
                target, WorkloadUpdateDirection.Forward, submitted ?? new Dictionary<string, string?>())
            {
                PasswordParameterNames = passwordNames,
            });
    }

    private sealed class NullReplayProvider : IActiveWorkloadIntentProvider
    {
        public Task<WorkloadDesiredSpec?> TryGetActiveTargetSpecAsync(Guid pluginInstanceId, string workloadId, CancellationToken cancellationToken)
            => Task.FromResult<WorkloadDesiredSpec?>(null);
        public Task<ActiveWorkloadIntentReplay?> TryGetActiveReplayAsync(Guid pluginInstanceId, string workloadId, CancellationToken cancellationToken)
            => Task.FromResult<ActiveWorkloadIntentReplay?>(null);
    }

    private sealed class ThrowingResolver : IDesiredSpecResolver
    {
        public bool WasCalled { get; private set; }

        public Task<WorkloadDesiredSpec> ResolveAsync(Guid pluginInstanceId, string workloadId, Func<WorkloadDesiredSpec> computeFresh, CancellationToken cancellationToken)
        {
            WasCalled = true;
            throw new InvalidOperationException("The active replay should bypass desired-spec resolution.");
        }
    }

    private sealed class FixedClock(DateTimeOffset? now = null) : IWorkloadClock
    {
        public DateTimeOffset UtcNow { get; } = now ?? new DateTimeOffset(2026, 9, 9, 12, 0, 0, TimeSpan.Zero);
    }
}
