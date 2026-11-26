using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using NSubstitute;
using Reeve.Workloads.Abstractions;
using Reeve.Workloads.Abstractions.Contracts.Deployment;
using Reeve.Workloads.Abstractions.Domain.Entities;
using Reeve.Workloads.Abstractions.Providers;
using Reeve.Workloads.Core;
using Reeve.Workloads.Core.Health;
using Reeve.Workloads.Domain;
using Reeve.Workloads.Providers.Manual;
using Reeve.Workloads.Testing;

namespace Reeve.Workloads.Core.Tests;

public sealed class WorkloadReferenceRolloutServiceTests
{
    [Fact]
    public async Task ApproveCurrent_recovers_a_lost_key_ring_and_approves_the_pending_candidate()
    {
        const string candidateReference = "ref:vault:new";
        var now = new DateTimeOffset(2026, 10, 4, 0, 0, 0, TimeSpan.Zero);
        var secrets = new WorkloadParameterSecretCodec(new EphemeralDataProtectionProvider());
        var instance = WorkloadInstance.Create(Guid.NewGuid(), Guid.NewGuid(), "test.plugin", "gateway", now);
        var manifest = new WorkloadManifest
        {
            Id = "gateway", DisplayName = "Gateway",
            Image = new WorkloadImage { Ref = "registry/image", Digest = "sha256:abc" },
            Kind = WorkloadKind.Service, Health = new WorkloadHealth { Mode = WorkloadHealthMode.Container },
            Parameters = [new WorkloadParameter { Name = "networkKey", Type = WorkloadParameterType.Password }],
        };
        var inputs = Substitute.For<IWorkloadReconcileInputsProvider>();
        inputs.GetInputsAsync(instance, Arg.Any<CancellationToken>()).Returns(WorkloadReconcileInputsResult.Declared(new WorkloadReconcileInputs
        {
            WorkloadManifest = manifest,
            ResolvedParameters = secrets.Protect(manifest, new Dictionary<string, string?> { ["networkKey"] = "old-literal" }),
            Settings = new(), Bootstrap = new() { InstanceId = instance.Id, SecretName = "gateway" },
            EffectiveProvider = ManualProvider.Descriptor, Namer = DefaultWorkloadResourceNamer.Instance,
        }));
        var target = new Reeve.Workloads.Abstractions.Domain.DesiredSpec.WorkloadDesiredSpec
        {
            GroupInstanceId = instance.GroupInstanceId, GroupId = instance.GroupId, WorkloadId = instance.WorkloadId,
            InstanceId = instance.Id, Inst8 = WorkloadInstance.ComputeInst8(instance.Id), Provider = ManualProvider.Id,
            ResourceName = "gateway", SecretName = "gateway-secret", Labels = new Dictionary<string, string>(),
            Image = "registry/image@sha256:abc", Kind = WorkloadKind.Service,
            Health = new Reeve.Workloads.Abstractions.Contracts.Deployment.WorkloadHealth { Mode = WorkloadHealthMode.Container },
            SpecHash = "candidate-hash",
        };
        var intentProvider = Substitute.For<IActiveWorkloadIntentProvider>();
        intentProvider.TryGetActiveReplayAsync(instance.GroupInstanceId, instance.WorkloadId, Arg.Any<CancellationToken>())
            .Returns(new ActiveWorkloadIntentReplay(target, WorkloadUpdateDirection.Forward,
                new Dictionary<string, string?> { ["networkKey"] = candidateReference })
            {
                PasswordParameterNames = ["networkKey"],
            });
        var resolver = Substitute.For<IWorkloadParameterReferenceResolver>();
        resolver.ResolveAsync(candidateReference, Arg.Any<CancellationToken>())
            .Returns(new WorkloadReferenceResolution.Success("candidate-secret"));
        var repository = Substitute.For<IWorkloadInstanceRepository>();
        repository.GetByIdAsync(instance.Id, Arg.Any<CancellationToken>()).Returns(instance);
        repository.UpdateAsync(instance, Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        var originalRingGuard = new UnpinnedReferenceRolloutGuard(new EphemeralDataProtectionProvider());
        originalRingGuard.RecordApplied(instance,
            new Dictionary<string, string?> { ["networkKey"] = "ref:vault:old" },
            new Dictionary<string, string?> { ["networkKey"] = "old-secret" }, ["networkKey"], now);
        var guard = new UnpinnedReferenceRolloutGuard(new EphemeralDataProtectionProvider());
        var nudge = Substitute.For<IWorkloadReconcileNudge>();
        var provider = new InMemoryWorkloadProvider(ManualProvider.Descriptor);
        instance.SetProvider(ManualProvider.Id, now);
        await provider.ApplyAsync(target with { SpecHash = "old-applied-hash" }, null);
        instance.SetAppliedSpecHash("old-applied-hash", now);
        var registry = Substitute.For<IWorkloadProviderRegistry>();
        registry.Describe(ManualProvider.Id).Returns(ManualProvider.Descriptor);
        registry.TryResolve(ManualProvider.Id).Returns(provider);
        var engine = new WorkloadReconcileEngine(inputs, new DesiredSpecResolver(intentProvider),
            registry, repository, new WorkloadHealthContributorDispatch([]), new FixedClock(now),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<WorkloadReconcileEngine>.Instance,
            Substitute.For<IRetainedVolumeService>(), intentProvider, resolver,
            secrets, null, guard, new Reeve.Workloads.Abstractions.NoCredentialPort());
        var service = new WorkloadReferenceRolloutService(repository, inputs, intentProvider, resolver,
            secrets, new FixedClock(now), nudge, guard);

        var initial = await engine.ReconcileInstanceAsync(instance);
        initial.Reason.Should().Be(Reeve.Workloads.Abstractions.WorkloadReconcileReason.ConfigurationRequired);
        initial.Message.Should().Be("Password reference rollout state is unavailable (unreadable-key); restore the Data Protection key ring and approve or pin the reference before applying.");
        instance.State.Should().Be(WorkloadInstanceState.PendingConfiguration);
        provider.ApplyCountFor(instance.Id).Should().Be(1, "the lost key must hold before candidate apply");

        var pending = await service.GetPendingAsync(instance.Id);
        pending.Should().ContainSingle(item => item.ParameterName == "networkKey"
            && item.Reference == candidateReference
            && item.State == WorkloadReferenceRolloutState.ApprovalStateUnavailable);
        pending.Select(item => item.Reference).Should().NotContain("candidate-secret");
        pending.Select(item => item.ParameterName).Should().NotContain("candidate-secret");
        resolver.ResolveAsync(candidateReference, Arg.Any<CancellationToken>())
            .Returns(new WorkloadReferenceResolution.Failure(WorkloadReferenceFailureKind.Unavailable, "offline"));
        var failed = await service.GetPendingAsync(instance.Id);
        failed.Should().ContainSingle(item => item.ParameterName == "networkKey"
            && item.Reference == candidateReference && item.State == WorkloadReferenceRolloutState.ResolutionFailed);
        resolver.ResolveAsync(candidateReference, Arg.Any<CancellationToken>())
            .Returns(new WorkloadReferenceResolution.Success("candidate-secret"));

        await service.ApproveCurrentAsync(instance.Id);

        (await service.GetPendingAsync(instance.Id)).Should().BeEmpty();
        resolver.ResolveAsync(candidateReference, Arg.Any<CancellationToken>())
            .Returns(new WorkloadReferenceResolution.Success("candidate-secret", IsVersionPinned: true));
        (await service.GetPendingAsync(instance.Id)).Should().BeEmpty("a successfully resolved pinned reference is outside the rollout gate");

        guard.Check(instance, new Dictionary<string, string?> { ["networkKey"] = candidateReference },
            new Dictionary<string, string?> { ["networkKey"] = "candidate-secret" }, ["networkKey"])
            .ChangedParameter.Should().BeNull();

        var result = await engine.ReconcileInstanceAsync(instance);

        result.Action.Should().Be(Reeve.Workloads.Abstractions.WorkloadReconcileAction.Applied);
        provider.IsTracked(instance.Id).Should().BeTrue();

        intentProvider.TryGetActiveReplayAsync(instance.GroupInstanceId, instance.WorkloadId, Arg.Any<CancellationToken>())
            .Returns(new ActiveWorkloadIntentReplay(target, WorkloadUpdateDirection.RollingBack,
                new Dictionary<string, string?>())
            {
                StoredParameters = secrets.Protect(manifest, new Dictionary<string, string?> { ["networkKey"] = "stored-value" }),
                PasswordParameterNames = ["networkKey"],
            });
        (await service.GetPendingAsync(instance.Id)).Should().BeEmpty();
        await service.ApproveCurrentAsync(instance.Id);
    }

    private sealed class FixedClock(DateTimeOffset now) : IWorkloadClock
    {
        public DateTimeOffset UtcNow { get; } = now;
    }
}
