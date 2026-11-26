using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using NSubstitute;
using Reeve.Workloads.Abstractions;
using Reeve.Workloads.Abstractions.Contracts.Deployment;
using Reeve.Workloads.Abstractions.Domain.Entities;
using Reeve.Workloads.Abstractions.Providers;
using Reeve.Workloads.Core;
using Reeve.Workloads.Domain;
using Reeve.Workloads.Providers.Manual;

namespace Reeve.Workloads.Core.Tests;

public sealed class CandidateOnlyPasswordApprovalTests
{
    [Fact]
    public async Task Approval_releases_a_hold_on_a_password_parameter_only_the_candidate_declares()
    {
        const string adminRef = "ref:vault:admin";
        var now = new DateTimeOffset(2026, 10, 5, 0, 0, 0, TimeSpan.Zero);
        var secrets = new WorkloadParameterSecretCodec(new EphemeralDataProtectionProvider());
        var instance = WorkloadInstance.Create(Guid.NewGuid(), Guid.NewGuid(), "test.plugin", "gateway", now);
        instance.SetAppliedSpecHash("installed-hash", now);
        var installed = new WorkloadManifest
        {
            Id = "gateway", DisplayName = "Gateway",
            Image = new WorkloadImage { Ref = "registry/image", Digest = "sha256:abc" },
            Kind = WorkloadKind.Service, Health = new WorkloadHealth { Mode = WorkloadHealthMode.Container },
            Parameters = [new WorkloadParameter { Name = "networkKey", Type = WorkloadParameterType.Password }],
        };
        var inputs = Substitute.For<IWorkloadReconcileInputsProvider>();
        inputs.GetInputsAsync(instance, Arg.Any<CancellationToken>()).Returns(WorkloadReconcileInputsResult.Declared(new WorkloadReconcileInputs
        {
            WorkloadManifest = installed,
            ResolvedParameters = secrets.Protect(installed, new Dictionary<string, string?> { ["networkKey"] = "literal" }),
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
                new Dictionary<string, string?> { ["adminKey"] = adminRef })
            {
                PasswordParameterNames = ["networkKey", "adminKey"],
            });
        var resolver = Substitute.For<IWorkloadParameterReferenceResolver>();
        resolver.ResolveAsync(adminRef, Arg.Any<CancellationToken>())
            .Returns(new WorkloadReferenceResolution.Success("admin-secret"));
        var repository = Substitute.For<IWorkloadInstanceRepository>();
        repository.GetByIdAsync(instance.Id, Arg.Any<CancellationToken>()).Returns(instance);
        repository.UpdateAsync(instance, Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        var guard = new UnpinnedReferenceRolloutGuard(new EphemeralDataProtectionProvider());
        guard.RecordApplied(instance, new Dictionary<string, string?> { ["networkKey"] = "ref:vault:old" },
            new Dictionary<string, string?> { ["networkKey"] = "old" }, ["networkKey"], now);
        guard.RecordApplied(instance, new Dictionary<string, string?>(), new Dictionary<string, string?>(), ["networkKey"], now);
        instance.SetAppliedSpecHash("installed-hash", now);
        var service = new WorkloadReferenceRolloutService(repository, inputs, intentProvider, resolver,
            secrets, new FixedClock(now),
            Substitute.For<IWorkloadReconcileNudge>(), guard);

        var sidecarRefs = new Dictionary<string, string?> { ["adminKey"] = adminRef };
        var sidecarValues = new Dictionary<string, string?> { ["networkKey"] = "literal", ["adminKey"] = "admin-secret" };
        string[] candidateNames = ["networkKey", "adminKey"];
        guard.Check(instance, sidecarRefs, sidecarValues, candidateNames).ChangedParameter.Should().Be("adminKey");

        await service.ApproveCurrentAsync(instance.Id);

        guard.Check(instance, sidecarRefs, sidecarValues, candidateNames).ChangedParameter
            .Should().BeNull("approval of the pending candidate must release the sidecar's check");
    }

    private sealed class FixedClock(DateTimeOffset now) : IWorkloadClock
    {
        public DateTimeOffset UtcNow { get; } = now;
    }
}
