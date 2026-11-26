using FluentAssertions;
using Reeve.Workloads.Abstractions.Contracts;
using Reeve.Workloads.Abstractions.Contracts.Deployment;
using Reeve.Workloads.Abstractions.Domain;
using Reeve.Workloads.Abstractions.Domain.DesiredSpec;
using Reeve.Workloads.Abstractions.Domain.Entities;
using Reeve.Workloads.Domain;
using Reeve.Workloads.Domain.DesiredSpec;
using Reeve.Workloads.Tests.Shared;

namespace Reeve.Workloads.Abstractions.Tests;

public sealed class VolumeGroupTests
{
    private static readonly Guid InstanceId = Guid.Parse("11111111-2222-3333-4444-555555555555");
    private const string Digest = "sha256:" + "1111111111111111111111111111111111111111111111111111111111111111";

    private static WorkloadManifest Workload(int? fsGroup) => new()
    {
        Id = "matter",
        DisplayName = "Matter",
        Image = new WorkloadImage { Ref = "ghcr.io/reeve/matter", Digest = Digest, Platforms = ["linux/amd64"] },
        Kind = WorkloadKind.SingleNode,
        Privileges = new WorkloadPrivileges { FsGroup = fsGroup },
        Volumes = [new WorkloadVolume { Id = "ota", MountPath = "/data/ota", Size = "64Mi", Criticality = VolumeCriticality.Cache }],
        Health = new WorkloadHealth { Mode = WorkloadHealthMode.Container },
    };

    private static WorkloadDesiredSpec Spec(WorkloadManifest workload)
    {
        var provider = StandInProviders.Kubernetes;
        var values = new Dictionary<string, string?>();
        return WorkloadDesiredSpecFactory.Compute(
            workload,
            new WorkloadOrchestratorSettings { Mirrors = [] },
            WorkloadParameterResolver.Resolve(workload, values, provider),
            new WorkloadBootstrapContext
            {
                InstanceId = InstanceId,
                Values = new Dictionary<string, BootstrapValue> { ["specHash"] = BootstrapValue.SpecHash },
                SecretName = "secret",
                HostEnv = [],
                ProvisionedCredentials = [],
            },
            provider,
            DefaultWorkloadResourceNamer.Instance,
            Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee"),
            "com.reeve.matter",
            InstanceId,
            "install-1");
    }

    private static WorkloadPrivilegeRequirements Requirements(int? fsGroup = null)
        => new() { Kind = WorkloadKind.SingleNode, FsGroup = fsGroup };

    [Fact]
    public void The_declared_group_reaches_the_spec_and_the_spec_hash_moves_with_it()
    {
        var none = Spec(Workload(null));
        var group = Spec(Workload(1000));
        var other = Spec(Workload(1001));

        none.Privileges!.FsGroup.Should().BeNull();
        group.Privileges!.FsGroup.Should().Be(1000);
        new[] { none.SpecHash, group.SpecHash, other.SpecHash }.Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public void A_declared_group_is_covered_only_by_the_same_group()
    {
        Requirements(1000).Covers(Requirements(1000), out _).Should().BeTrue();
        Requirements(1000).Covers(Requirements(null), out _).Should().BeTrue("asking for less than was accepted is covered");

        Requirements(null).Covers(Requirements(1000), out var none).Should().BeFalse();
        none.Should().ContainSingle().Which.Should().Contain("FsGroup").And.Contain("1000").And.Contain("(none)");

        Requirements(1000).Covers(Requirements(999), out var other).Should().BeFalse();
        other.Should().ContainSingle().Which.Should().Contain("999");
    }

    [Fact]
    public void Gaining_or_moving_the_group_is_an_escalation_and_dropping_it_a_narrowing()
    {
        var gained = WorkloadPrivilegeRequirements.Compare(Requirements(null), Requirements(1000));
        gained.Security.Should().Be(RequirementChangeKind.Escalation);
        gained.EscalatedFields.Should().ContainSingle().Which.Should().Contain("FsGroup");

        WorkloadPrivilegeRequirements.Compare(Requirements(1000), Requirements(999))
            .Security.Should().Be(RequirementChangeKind.Escalation);

        WorkloadPrivilegeRequirements.Compare(Requirements(1000), Requirements(null))
            .Security.Should().Be(RequirementChangeKind.Narrowing);

        WorkloadPrivilegeRequirements.Compare(Requirements(1000), Requirements(1000))
            .Security.Should().Be(RequirementChangeKind.Unchanged);
    }

    [Fact]
    public void The_fingerprint_moves_with_the_group_and_returns_when_it_is_dropped()
    {
        var baseline = Requirements().Fingerprint();

        Requirements(1000).Fingerprint().Should().NotBe(baseline).And.NotBe(Requirements(1001).Fingerprint());
        Requirements(null).Fingerprint().Should().Be(baseline);
    }

    [Fact]
    public void A_profile_with_no_volume_group_keeps_the_fingerprint_it_was_accepted_under()
    {
        var profile = new WorkloadPrivilegeRequirements
        {
            Kind = WorkloadKind.SingleNode,
            Mdns = true,
            Capabilities = new HashSet<string> { "NET_ADMIN" },
            DevicePaths = new HashSet<string> { "/dev/ttyUSB0" },
        };

        profile.Fingerprint().Should().Be("dae097487a9676ae6b00fa0896ff2b1877a2cc28dfbc6e0470c16b7838735d23");
    }

    [Fact]
    public void The_manifest_and_the_resolved_spec_derive_the_same_requirement()
    {
        var workload = Workload(1000);

        WorkloadPrivilegeRequirements.FromWorkload(workload, new Dictionary<string, string>()).FsGroup.Should().Be(1000);
        WorkloadPrivilegeRequirements.FromDesiredSpec(Spec(workload)).FsGroup.Should().Be(1000);
        WorkloadPrivilegeRequirements.FromDesiredSpec(Spec(Workload(null))).FsGroup.Should().BeNull();
    }
}
