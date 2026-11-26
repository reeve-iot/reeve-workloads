using FluentAssertions;
using Reeve.Workloads.Abstractions.Contracts;
using Reeve.Workloads.Abstractions.Contracts.Deployment;
using Reeve.Workloads.Abstractions.Domain;
using Reeve.Workloads.Domain;

namespace Reeve.Workloads.Abstractions.Tests;

public sealed class ConsentedProfileDiffTests
{
    private static WorkloadPrivilegeRequirements Base(
        WorkloadKind kind = WorkloadKind.SingleNode,
        bool hostNetwork = false, bool mdns = false, bool ipv6 = false,
        bool privileged = false, bool runAsRoot = false, bool hostDbus = false, bool tun = false,
        bool callsBackendRest = false,
        IEnumerable<string>? capabilities = null,
        IEnumerable<string>? devicePaths = null,
        IEnumerable<string>? dbusPaths = null,
        IEnumerable<string>? hostPaths = null,
        NodeLocalStateRequirement? nodeLocalState = null,
        string? patchDigest = null,
        IReadOnlyList<VolumeRequirement>? volumes = null)
        => new()
        {
            Kind = kind,
            HostNetwork = hostNetwork,
            Mdns = mdns,
            Ipv6Required = ipv6,
            Privileged = privileged,
            RunAsRoot = runAsRoot,
            HostDbus = hostDbus,
            TunDevice = tun,
            CallsBackendRest = callsBackendRest,
            Capabilities = new HashSet<string>(capabilities ?? Array.Empty<string>()),
            DevicePaths = new HashSet<string>(devicePaths ?? Array.Empty<string>()),
            DbusPaths = new HashSet<string>(dbusPaths ?? Array.Empty<string>()),
            HostPaths = new HashSet<string>(hostPaths ?? Array.Empty<string>()),
            NodeLocalState = nodeLocalState,
            UnsupportedPodPatchDigest = patchDigest,
            Volumes = volumes ?? Array.Empty<VolumeRequirement>(),
        };

    [Fact]
    public void Identical_profiles_are_Unchanged()
    {
        var p = Base(privileged: true, capabilities: new[] { "NET_ADMIN" });
        var diff = WorkloadPrivilegeRequirements.Compare(p, p);
        diff.Security.Should().Be(RequirementChangeKind.Unchanged);
        diff.EscalatedFields.Should().BeEmpty();
        diff.NarrowedFields.Should().BeEmpty();
        diff.InformationalVolumeChange.Should().BeFalse();
    }

    [Theory]
    [InlineData(nameof(WorkloadPrivilegeRequirements.Mdns))]
    [InlineData(nameof(WorkloadPrivilegeRequirements.Ipv6Required))]
    [InlineData(nameof(WorkloadPrivilegeRequirements.Privileged))]
    [InlineData(nameof(WorkloadPrivilegeRequirements.RunAsRoot))]
    [InlineData(nameof(WorkloadPrivilegeRequirements.HostDbus))]
    [InlineData(nameof(WorkloadPrivilegeRequirements.TunDevice))]
    [InlineData(nameof(WorkloadPrivilegeRequirements.CallsBackendRest))]
    public void Boolean_false_to_true_is_escalation(string field)
    {
        var oldP = Base();
        var newP = Base(
            hostNetwork: field == nameof(WorkloadPrivilegeRequirements.HostNetwork),
            mdns: field == nameof(WorkloadPrivilegeRequirements.Mdns),
            ipv6: field == nameof(WorkloadPrivilegeRequirements.Ipv6Required),
            privileged: field == nameof(WorkloadPrivilegeRequirements.Privileged),
            runAsRoot: field == nameof(WorkloadPrivilegeRequirements.RunAsRoot),
            hostDbus: field == nameof(WorkloadPrivilegeRequirements.HostDbus),
            tun: field == nameof(WorkloadPrivilegeRequirements.TunDevice),
            callsBackendRest: field == nameof(WorkloadPrivilegeRequirements.CallsBackendRest));

        var diff = WorkloadPrivilegeRequirements.Compare(oldP, newP);
        diff.Security.Should().Be(RequirementChangeKind.Escalation);
        diff.EscalatedFields.Should().ContainSingle().Which.Should().Contain(field);
        diff.NarrowedFields.Should().BeEmpty();
    }

    [Theory]
    [InlineData(nameof(WorkloadPrivilegeRequirements.Privileged))]
    [InlineData(nameof(WorkloadPrivilegeRequirements.TunDevice))]
    [InlineData(nameof(WorkloadPrivilegeRequirements.CallsBackendRest))]
    public void Boolean_true_to_false_is_narrowing(string field)
    {
        var oldP = Base(
            hostNetwork: field == nameof(WorkloadPrivilegeRequirements.HostNetwork),
            privileged: field == nameof(WorkloadPrivilegeRequirements.Privileged),
            tun: field == nameof(WorkloadPrivilegeRequirements.TunDevice),
            callsBackendRest: field == nameof(WorkloadPrivilegeRequirements.CallsBackendRest));
        var newP = Base();

        var diff = WorkloadPrivilegeRequirements.Compare(oldP, newP);
        diff.Security.Should().Be(RequirementChangeKind.Narrowing);
        diff.NarrowedFields.Should().ContainSingle().Which.Should().Contain(field);
        diff.EscalatedFields.Should().BeEmpty();
    }

    private static WorkloadPrivilegeRequirements ModeProfile(WorkloadNetworkMode mode)
        => Base(hostNetwork: mode == WorkloadNetworkMode.HostNetwork) with { NetworkMode = mode };

    [Theory]
    [InlineData(WorkloadNetworkMode.Cluster, WorkloadNetworkMode.Cluster, RequirementChangeKind.Unchanged)]
    [InlineData(WorkloadNetworkMode.Cluster, WorkloadNetworkMode.HostNetwork, RequirementChangeKind.Escalation)]
    [InlineData(WorkloadNetworkMode.Cluster, WorkloadNetworkMode.OperatorNetwork, RequirementChangeKind.Escalation)]
    [InlineData(WorkloadNetworkMode.HostNetwork, WorkloadNetworkMode.Cluster, RequirementChangeKind.Narrowing)]
    [InlineData(WorkloadNetworkMode.HostNetwork, WorkloadNetworkMode.HostNetwork, RequirementChangeKind.Unchanged)]
    [InlineData(WorkloadNetworkMode.HostNetwork, WorkloadNetworkMode.OperatorNetwork, RequirementChangeKind.Escalation)]
    [InlineData(WorkloadNetworkMode.OperatorNetwork, WorkloadNetworkMode.Cluster, RequirementChangeKind.Narrowing)]
    [InlineData(WorkloadNetworkMode.OperatorNetwork, WorkloadNetworkMode.HostNetwork, RequirementChangeKind.Escalation)]
    [InlineData(WorkloadNetworkMode.OperatorNetwork, WorkloadNetworkMode.OperatorNetwork, RequirementChangeKind.Unchanged)]
    public void NetworkMode_change_classifies_across_the_full_3x3_matrix(
        WorkloadNetworkMode oldMode, WorkloadNetworkMode newMode, RequirementChangeKind expected)
    {
        var diff = WorkloadPrivilegeRequirements.Compare(ModeProfile(oldMode), ModeProfile(newMode));

        diff.Security.Should().Be(expected);

        switch (expected)
        {
            case RequirementChangeKind.Unchanged:
                diff.EscalatedFields.Should().BeEmpty();
                diff.NarrowedFields.Should().BeEmpty();
                break;
            case RequirementChangeKind.Escalation:
                diff.EscalatedFields.Should().ContainSingle()
                    .Which.Should().Contain(nameof(WorkloadPrivilegeRequirements.NetworkMode));
                diff.NarrowedFields.Should().BeEmpty();
                break;
            case RequirementChangeKind.Narrowing:
                diff.NarrowedFields.Should().ContainSingle()
                    .Which.Should().Contain(nameof(WorkloadPrivilegeRequirements.NetworkMode));
                diff.EscalatedFields.Should().BeEmpty();
                break;
        }

        diff.EscalatedFields.Concat(diff.NarrowedFields).Should()
            .NotContain(f => f.StartsWith($"{nameof(WorkloadPrivilegeRequirements.HostNetwork)}:", StringComparison.Ordinal));
    }

    [Fact]
    public void A_NetworkMode_change_emits_the_files_PascalCase_formatted_sentence_never_a_bare_token()
    {
        var diff = WorkloadPrivilegeRequirements.Compare(
            ModeProfile(WorkloadNetworkMode.HostNetwork), ModeProfile(WorkloadNetworkMode.OperatorNetwork));

        diff.EscalatedFields.Should().ContainSingle()
            .Which.Should().Be("NetworkMode: 'hostNetwork' → 'operatorNetwork'");
    }

    [Fact]
    public void HostNetwork_false_to_true_is_reported_ONLY_as_a_NetworkMode_escalation()
    {
        var diff = WorkloadPrivilegeRequirements.Compare(Base(), Base(hostNetwork: true));

        diff.Security.Should().Be(RequirementChangeKind.Escalation);
        diff.EscalatedFields.Should().ContainSingle().Which.Should().Be("NetworkMode: 'cluster' → 'hostNetwork'");
        diff.NarrowedFields.Should().BeEmpty();
        diff.EscalatedFields.Should().NotContain(f => f.Contains(nameof(WorkloadPrivilegeRequirements.HostNetwork), StringComparison.Ordinal));
    }

    [Fact]
    public void HostNetwork_true_to_false_is_reported_ONLY_as_a_NetworkMode_narrowing()
    {
        var diff = WorkloadPrivilegeRequirements.Compare(Base(hostNetwork: true), Base());

        diff.Security.Should().Be(RequirementChangeKind.Narrowing);
        diff.NarrowedFields.Should().ContainSingle().Which.Should().Be("NetworkMode: 'hostNetwork' → 'cluster'");
        diff.EscalatedFields.Should().BeEmpty();
        diff.NarrowedFields.Should().NotContain(f => f.Contains(nameof(WorkloadPrivilegeRequirements.HostNetwork), StringComparison.Ordinal));
    }

    [Fact]
    public void The_lateral_hostNetwork_to_operatorNetwork_files_NOTHING_under_narrowed()
    {
        var diff = WorkloadPrivilegeRequirements.Compare(
            ModeProfile(WorkloadNetworkMode.HostNetwork), ModeProfile(WorkloadNetworkMode.OperatorNetwork));

        diff.Security.Should().Be(RequirementChangeKind.Escalation);
        diff.NarrowedFields.Should().BeEmpty("the mode subsumes the companion boolean — nothing narrowed here");
    }

    [Fact]
    public void Capability_superset_is_escalation()
    {
        var oldP = Base(capabilities: new[] { "NET_ADMIN" });
        var newP = Base(capabilities: new[] { "NET_ADMIN", "SYS_ADMIN" });
        var diff = WorkloadPrivilegeRequirements.Compare(oldP, newP);
        diff.Security.Should().Be(RequirementChangeKind.Escalation);
        diff.EscalatedFields.Should().ContainSingle().Which.Should().Contain("Capabilities").And.Contain("SYS_ADMIN");
    }

    [Fact]
    public void Capability_subset_is_narrowing()
    {
        var oldP = Base(capabilities: new[] { "NET_ADMIN", "SYS_ADMIN" });
        var newP = Base(capabilities: new[] { "NET_ADMIN" });
        var diff = WorkloadPrivilegeRequirements.Compare(oldP, newP);
        diff.Security.Should().Be(RequirementChangeKind.Narrowing);
        diff.NarrowedFields.Should().ContainSingle().Which.Should().Contain("Capabilities");
    }

    [Fact]
    public void Capability_disjoint_change_is_both_gain_and_loss_hence_escalation()
    {
        var oldP = Base(capabilities: new[] { "NET_ADMIN" });
        var newP = Base(capabilities: new[] { "NET_RAW" });
        var diff = WorkloadPrivilegeRequirements.Compare(oldP, newP);
        diff.Security.Should().Be(RequirementChangeKind.Escalation);
        diff.EscalatedFields.Should().Contain(f => f.Contains("Capabilities"));
    }

    [Fact]
    public void Device_path_addition_is_escalation()
    {
        var oldP = Base(devicePaths: new[] { "/dev/ttyUSB0" });
        var newP = Base(devicePaths: new[] { "/dev/ttyUSB0", "/dev/ttyUSB1" });
        var diff = WorkloadPrivilegeRequirements.Compare(oldP, newP);
        diff.Security.Should().Be(RequirementChangeKind.Escalation);
        diff.EscalatedFields.Should().Contain(f => f.Contains("DevicePaths"));
    }

    [Fact]
    public void Device_path_removal_is_narrowing()
    {
        var oldP = Base(devicePaths: new[] { "/dev/ttyUSB0", "/dev/ttyUSB1" });
        var newP = Base(devicePaths: new[] { "/dev/ttyUSB0" });
        var diff = WorkloadPrivilegeRequirements.Compare(oldP, newP);
        diff.Security.Should().Be(RequirementChangeKind.Narrowing);
        diff.NarrowedFields.Should().Contain(f => f.Contains("DevicePaths"));
    }

    [Fact]
    public void Host_path_addition_is_escalation_requiring_reconsent()
    {
        var oldP = Base(hostPaths: new[] { "/sys/bus/usb" });
        var newP = Base(hostPaths: new[] { "/sys/bus/usb", "/sys/class/net" });
        var diff = WorkloadPrivilegeRequirements.Compare(oldP, newP);
        diff.Security.Should().Be(RequirementChangeKind.Escalation);
        diff.EscalatedFields.Should().Contain(f => f.Contains("HostPaths"));
    }

    [Fact]
    public void Host_path_removal_is_narrowing()
    {
        var oldP = Base(hostPaths: new[] { "/sys/bus/usb", "/sys/class/net" });
        var newP = Base(hostPaths: new[] { "/sys/bus/usb" });
        var diff = WorkloadPrivilegeRequirements.Compare(oldP, newP);
        diff.Security.Should().Be(RequirementChangeKind.Narrowing);
        diff.NarrowedFields.Should().Contain(f => f.Contains("HostPaths"));
    }

    [Fact]
    public void Dbus_path_addition_is_escalation()
    {
        var oldP = Base(dbusPaths: new[] { "/run/dbus" });
        var newP = Base(dbusPaths: new[] { "/run/dbus", "/var/run/dbus" });
        var diff = WorkloadPrivilegeRequirements.Compare(oldP, newP);
        diff.Security.Should().Be(RequirementChangeKind.Escalation);
        diff.EscalatedFields.Should().Contain(f => f.Contains("DbusPaths"));
    }

    [Fact]
    public void Kind_change_is_escalation_structural()
    {
        var oldP = Base(kind: WorkloadKind.SingleNode);
        var newP = Base(kind: WorkloadKind.PerNode);
        var diff = WorkloadPrivilegeRequirements.Compare(oldP, newP);
        diff.Security.Should().Be(RequirementChangeKind.Escalation);
        diff.EscalatedFields.Should().Contain(f => f.Contains("Kind"));
    }

    [Fact]
    public void NodeLocalState_absent_to_present_is_escalation()
    {
        var oldP = Base(nodeLocalState: null);
        var newP = Base(nodeLocalState: new NodeLocalStateRequirement { Criticality = VolumeCriticality.Cache });
        var diff = WorkloadPrivilegeRequirements.Compare(oldP, newP);
        diff.Security.Should().Be(RequirementChangeKind.Escalation);
        diff.EscalatedFields.Should().Contain(f => f.Contains("NodeLocalState"));
    }

    [Fact]
    public void NodeLocalState_present_to_absent_is_narrowing()
    {
        var oldP = Base(nodeLocalState: new NodeLocalStateRequirement { Criticality = VolumeCriticality.Critical });
        var newP = Base(nodeLocalState: null);
        var diff = WorkloadPrivilegeRequirements.Compare(oldP, newP);
        diff.Security.Should().Be(RequirementChangeKind.Narrowing);
        diff.NarrowedFields.Should().Contain(f => f.Contains("NodeLocalState"));
    }

    [Fact]
    public void NodeLocalState_criticality_increase_is_informational_not_escalation()
    {
        var oldP = Base(nodeLocalState: new NodeLocalStateRequirement { Criticality = VolumeCriticality.Cache });
        var newP = Base(nodeLocalState: new NodeLocalStateRequirement { Criticality = VolumeCriticality.Critical });
        var diff = WorkloadPrivilegeRequirements.Compare(oldP, newP);

        diff.Security.Should().Be(RequirementChangeKind.Unchanged, "presence is unchanged; only the informational criticality moved");
        diff.EscalatedFields.Should().NotContain(f => f.Contains("NodeLocalState"));
        diff.NarrowedFields.Should().NotContain(f => f.Contains("NodeLocalState"));
        diff.InformationalVolumeChange.Should().BeTrue();
        diff.VolumeNotes.Should().Contain(n => n.Contains("nodeLocalState") || n.Contains("NodeLocalState"));
    }

    [Fact]
    public void NodeLocalState_criticality_decrease_is_informational_not_narrowing()
    {
        var oldP = Base(nodeLocalState: new NodeLocalStateRequirement { Criticality = VolumeCriticality.Critical });
        var newP = Base(nodeLocalState: new NodeLocalStateRequirement { Criticality = VolumeCriticality.Recoverable });
        var diff = WorkloadPrivilegeRequirements.Compare(oldP, newP);

        diff.Security.Should().Be(RequirementChangeKind.Unchanged);
        diff.NarrowedFields.Should().NotContain(f => f.Contains("NodeLocalState"));
        diff.EscalatedFields.Should().NotContain(f => f.Contains("NodeLocalState"));
        diff.InformationalVolumeChange.Should().BeTrue();
        diff.VolumeNotes.Should().Contain(n => n.Contains("nodeLocalState") || n.Contains("NodeLocalState"));
    }

    [Fact]
    public void Patch_digest_null_to_value_is_escalation()
    {
        var oldP = Base(patchDigest: null);
        var newP = Base(patchDigest: "deadbeef");
        var diff = WorkloadPrivilegeRequirements.Compare(oldP, newP);
        diff.Security.Should().Be(RequirementChangeKind.Escalation);
        diff.EscalatedFields.Should().Contain(f => f.Contains("Patch"));
    }

    [Fact]
    public void Patch_digest_value_to_null_is_escalation_any_change_re_consents()
    {
        var oldP = Base(patchDigest: "deadbeef");
        var newP = Base(patchDigest: null);
        var diff = WorkloadPrivilegeRequirements.Compare(oldP, newP);
        diff.Security.Should().Be(RequirementChangeKind.Escalation);
        diff.EscalatedFields.Should().Contain(f => f.Contains("Patch"));
    }

    [Fact]
    public void Patch_digest_body_swap_value_to_different_value_is_escalation()
    {
        var oldP = Base(patchDigest: "aaaa");
        var newP = Base(patchDigest: "bbbb");
        var diff = WorkloadPrivilegeRequirements.Compare(oldP, newP);
        diff.Security.Should().Be(RequirementChangeKind.Escalation);
        diff.EscalatedFields.Should().Contain(f => f.Contains("Patch"));
    }

    [Fact]
    public void A_mix_of_narrow_and_escalate_is_escalation_any_increase_dominates()
    {
        var oldP = Base(privileged: true, capabilities: new[] { "NET_ADMIN" });
        var newP = Base(privileged: false, hostNetwork: true, capabilities: new[] { "NET_ADMIN" });
        var diff = WorkloadPrivilegeRequirements.Compare(oldP, newP);
        diff.Security.Should().Be(RequirementChangeKind.Escalation);
        diff.EscalatedFields.Should().Contain(f => f.Contains(nameof(WorkloadPrivilegeRequirements.NetworkMode)));
        diff.NarrowedFields.Should().Contain(f => f.Contains(nameof(WorkloadPrivilegeRequirements.Privileged)));
    }

    [Fact]
    public void A_set_with_a_concurrent_add_and_remove_reports_both_the_addition_and_the_removal()
    {
        var oldP = Base(capabilities: new[] { "NET_ADMIN", "NET_RAW" });
        var newP = Base(capabilities: new[] { "NET_ADMIN", "SYS_ADMIN" });

        var diff = WorkloadPrivilegeRequirements.Compare(oldP, newP);

        diff.Security.Should().Be(RequirementChangeKind.Escalation);
        diff.EscalatedFields.Should().Contain(f => f.Contains("Capabilities") && f.Contains("SYS_ADMIN"));
        diff.NarrowedFields.Should().Contain(f => f.Contains("Capabilities") && f.Contains("NET_RAW"));
    }

    [Fact]
    public void A_path_set_with_concurrent_add_and_remove_reports_both_fields()
    {
        var oldP = Base(devicePaths: new[] { "/dev/ttyUSB0", "/dev/ttyUSB1" });
        var newP = Base(devicePaths: new[] { "/dev/ttyUSB0", "/dev/ttyACM0" });

        var diff = WorkloadPrivilegeRequirements.Compare(oldP, newP);

        diff.Security.Should().Be(RequirementChangeKind.Escalation);
        diff.EscalatedFields.Should().Contain(f => f.Contains("DevicePaths") && f.Contains("/dev/ttyACM0"));
        diff.NarrowedFields.Should().Contain(f => f.Contains("DevicePaths") && f.Contains("/dev/ttyUSB1"));
    }

    [Fact]
    public void Volume_size_growth_is_an_informational_note_not_escalation()
    {
        var oldP = Base(volumes: new[] { new VolumeRequirement { Id = "cache", Size = "64Mi", Criticality = VolumeCriticality.Cache } });
        var newP = Base(volumes: new[] { new VolumeRequirement { Id = "cache", Size = "512Mi", Criticality = VolumeCriticality.Cache } });

        var diff = WorkloadPrivilegeRequirements.Compare(oldP, newP);
        diff.Security.Should().Be(RequirementChangeKind.Unchanged, "no security-axis field changed");
        diff.EscalatedFields.Should().BeEmpty();
        diff.InformationalVolumeChange.Should().BeTrue();
        diff.VolumeNotes.Should().ContainSingle().Which.Should().Contain("cache");
    }

    [Fact]
    public void Added_volume_is_an_informational_note_not_escalation()
    {
        var oldP = Base(volumes: new[] { new VolumeRequirement { Id = "cache", Size = "64Mi", Criticality = VolumeCriticality.Cache } });
        var newP = Base(volumes: new[]
        {
            new VolumeRequirement { Id = "cache", Size = "64Mi", Criticality = VolumeCriticality.Cache },
            new VolumeRequirement { Id = "data", Size = "1Gi", Criticality = VolumeCriticality.Critical },
        });

        var diff = WorkloadPrivilegeRequirements.Compare(oldP, newP);
        diff.Security.Should().Be(RequirementChangeKind.Unchanged);
        diff.EscalatedFields.Should().BeEmpty();
        diff.InformationalVolumeChange.Should().BeTrue();
        diff.VolumeNotes.Should().Contain(n => n.Contains("data"));
    }

    [Fact]
    public void Removed_or_shrunk_volume_is_also_informational_not_narrowing()
    {
        var oldP = Base(volumes: new[]
        {
            new VolumeRequirement { Id = "cache", Size = "512Mi", Criticality = VolumeCriticality.Cache },
            new VolumeRequirement { Id = "data", Size = "1Gi", Criticality = VolumeCriticality.Critical },
        });
        var newP = Base(volumes: new[] { new VolumeRequirement { Id = "cache", Size = "64Mi", Criticality = VolumeCriticality.Cache } });

        var diff = WorkloadPrivilegeRequirements.Compare(oldP, newP);
        diff.Security.Should().Be(RequirementChangeKind.Unchanged, "volume changes never touch the security axis");
        diff.NarrowedFields.Should().BeEmpty();
        diff.InformationalVolumeChange.Should().BeTrue();
    }

    [Fact]
    public void Identical_volumes_produce_no_informational_note()
    {
        var vols = new[] { new VolumeRequirement { Id = "cache", Size = "64Mi", Criticality = VolumeCriticality.Cache } };
        var oldP = Base(volumes: vols);
        var newP = Base(volumes: vols);
        var diff = WorkloadPrivilegeRequirements.Compare(oldP, newP);
        diff.InformationalVolumeChange.Should().BeFalse();
        diff.VolumeNotes.Should().BeEmpty();
    }

    [Fact]
    public void Escalation_and_volume_growth_together_report_both_axes_independently()
    {
        var oldP = Base(privileged: false, volumes: new[] { new VolumeRequirement { Id = "c", Size = "64Mi", Criticality = VolumeCriticality.Cache } });
        var newP = Base(privileged: true, volumes: new[] { new VolumeRequirement { Id = "c", Size = "128Mi", Criticality = VolumeCriticality.Cache } });

        var diff = WorkloadPrivilegeRequirements.Compare(oldP, newP);
        diff.Security.Should().Be(RequirementChangeKind.Escalation, "privileged went true");
        diff.EscalatedFields.Should().Contain(f => f.Contains(nameof(WorkloadPrivilegeRequirements.Privileged)));
        diff.InformationalVolumeChange.Should().BeTrue("cache grew — reported on its own axis, not as escalation");
    }

    [Fact]
    public void Duplicate_volume_ids_on_either_side_do_not_throw_and_the_diff_is_deterministic()
    {
        var oldP = Base(volumes: new[]
        {
            new VolumeRequirement { Id = "dup", Size = "10Mi", Criticality = VolumeCriticality.Cache },
            new VolumeRequirement { Id = "dup", Size = "20Mi", Criticality = VolumeCriticality.Cache },
        });
        var newP = Base(volumes: new[]
        {
            new VolumeRequirement { Id = "dup", Size = "30Mi", Criticality = VolumeCriticality.Cache },
            new VolumeRequirement { Id = "dup", Size = "40Mi", Criticality = VolumeCriticality.Cache },
        });

        WorkloadPrivilegeRequirementsDiff diff = null!;
        var act = () => diff = WorkloadPrivilegeRequirements.Compare(oldP, newP);
        act.Should().NotThrow("a duplicate volume id must not crash the diff");

        var again = WorkloadPrivilegeRequirements.Compare(oldP, newP);
        again.VolumeNotes.Should().BeEquivalentTo(diff.VolumeNotes, o => o.WithStrictOrdering());

        diff.Security.Should().Be(RequirementChangeKind.Unchanged);
        diff.InformationalVolumeChange.Should().BeTrue();
        diff.VolumeNotes.Should().Contain(n => n.Contains("dup") && n.Contains("20Mi") && n.Contains("40Mi"));
    }

    [Fact]
    public void Duplicate_volume_id_only_on_the_old_side_does_not_throw()
    {
        var oldP = Base(volumes: new[]
        {
            new VolumeRequirement { Id = "cache", Size = "10Mi", Criticality = VolumeCriticality.Cache },
            new VolumeRequirement { Id = "cache", Size = "99Mi", Criticality = VolumeCriticality.Cache },
        });
        var newP = Base(volumes: new[] { new VolumeRequirement { Id = "cache", Size = "99Mi", Criticality = VolumeCriticality.Cache } });

        var act = () => WorkloadPrivilegeRequirements.Compare(oldP, newP);
        act.Should().NotThrow();
        WorkloadPrivilegeRequirements.Compare(oldP, newP).VolumeNotes.Should().NotContain(n => n.Contains("size"));
    }
}
