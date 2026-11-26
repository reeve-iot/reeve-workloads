using System.Text.Json;
using FluentAssertions;
using Reeve.Workloads.Abstractions.Contracts;
using Reeve.Workloads.Abstractions.Contracts.Deployment;
using Reeve.Workloads.Abstractions.Domain;
using Reeve.Workloads.Domain;

namespace Reeve.Workloads.Abstractions.Tests;

public sealed class ConsentedProfileSubsetTests
{
    private static readonly NodeLocalStateRequirement UseDefaultNls = new() { Criticality = VolumeCriticality.Critical };

    private static WorkloadPrivilegeRequirements Consented(
        WorkloadKind kind = WorkloadKind.SingleNode,
        bool hostNetwork = true, bool mdns = true, bool ipv6 = true,
        bool privileged = true, bool runAsRoot = true, bool hostDbus = true, bool tun = true,
        bool callsBackendRest = true,
        IEnumerable<string>? capabilities = null,
        IEnumerable<string>? devicePaths = null,
        IEnumerable<string>? dbusPaths = null,
        IEnumerable<string>? hostPaths = null,
        NodeLocalStateRequirement? nodeLocalState = null,
        bool noNodeLocalState = false,
        string? patchDigest = "a1b2c3")
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
            Capabilities = new HashSet<string>(capabilities ?? new[] { "NET_ADMIN", "NET_RAW", "SYS_ADMIN" }),
            DevicePaths = new HashSet<string>(devicePaths ?? new[] { "/dev/ttyUSB0", "/dev/serial/by-id/usb-Silicon_Labs" }),
            DbusPaths = new HashSet<string>(dbusPaths ?? new[] { "/run/dbus" }),
            HostPaths = new HashSet<string>(hostPaths ?? new[] { "/sys/bus/usb" }),
            NodeLocalState = noNodeLocalState ? null : (nodeLocalState ?? UseDefaultNls),
            UnsupportedPodPatchDigest = patchDigest,
            Volumes = Array.Empty<VolumeRequirement>(),
        };

    [Fact]
    public void A_profile_allows_itself()
    {
        var p = Consented();
        p.Covers(p, out var reasons).Should().BeTrue();
        reasons.Should().BeEmpty();
    }

    [Fact]
    public void An_all_false_empty_desired_is_a_subset_of_a_permissive_consent()
    {
        var consented = Consented(patchDigest: null);
        var desired = new WorkloadPrivilegeRequirements
        {
            Kind = WorkloadKind.SingleNode,
            Capabilities = new HashSet<string>(),
            DevicePaths = new HashSet<string>(),
            DbusPaths = new HashSet<string>(),
            UnsupportedPodPatchDigest = null,
            Volumes = Array.Empty<VolumeRequirement>(),
        };
        consented.Covers(desired, out var reasons).Should().BeTrue();
        reasons.Should().BeEmpty();
    }

    [Theory]
    [InlineData(nameof(WorkloadPrivilegeRequirements.HostNetwork))]
    [InlineData(nameof(WorkloadPrivilegeRequirements.Mdns))]
    [InlineData(nameof(WorkloadPrivilegeRequirements.Ipv6Required))]
    [InlineData(nameof(WorkloadPrivilegeRequirements.Privileged))]
    [InlineData(nameof(WorkloadPrivilegeRequirements.RunAsRoot))]
    [InlineData(nameof(WorkloadPrivilegeRequirements.HostDbus))]
    [InlineData(nameof(WorkloadPrivilegeRequirements.TunDevice))]
    [InlineData(nameof(WorkloadPrivilegeRequirements.CallsBackendRest))]
    public void Desired_true_boolean_against_consented_false_is_rejected_and_names_the_field(string field)
    {
        var consented = Consented(
            hostNetwork: field != nameof(WorkloadPrivilegeRequirements.HostNetwork),
            mdns: field != nameof(WorkloadPrivilegeRequirements.Mdns),
            ipv6: field != nameof(WorkloadPrivilegeRequirements.Ipv6Required),
            privileged: field != nameof(WorkloadPrivilegeRequirements.Privileged),
            runAsRoot: field != nameof(WorkloadPrivilegeRequirements.RunAsRoot),
            hostDbus: field != nameof(WorkloadPrivilegeRequirements.HostDbus),
            tun: field != nameof(WorkloadPrivilegeRequirements.TunDevice),
            callsBackendRest: field != nameof(WorkloadPrivilegeRequirements.CallsBackendRest));
        var desired = Consented();

        consented.Covers(desired, out var reasons).Should().BeFalse();
        reasons.Should().Contain(r => r.Contains(field, StringComparison.Ordinal));

        if (field == nameof(WorkloadPrivilegeRequirements.HostNetwork))
        {
            reasons.Should().HaveCount(2);
            reasons.Should().Contain(r => r.StartsWith($"{nameof(WorkloadPrivilegeRequirements.NetworkMode)}:", StringComparison.Ordinal));
        }
        else
        {
            reasons.Should().ContainSingle();
        }
    }

    private static WorkloadPrivilegeRequirements ModeProfile(WorkloadNetworkMode mode) => new()
    {
        Kind = WorkloadKind.SingleNode,
        HostNetwork = mode == WorkloadNetworkMode.HostNetwork,
        NetworkMode = mode,
        Capabilities = new HashSet<string>(),
        DevicePaths = new HashSet<string>(),
        DbusPaths = new HashSet<string>(),
        HostPaths = new HashSet<string>(),
        UnsupportedPodPatchDigest = null,
        Volumes = Array.Empty<VolumeRequirement>(),
    };

    [Theory]
    [InlineData(WorkloadNetworkMode.Cluster, WorkloadNetworkMode.Cluster, true)]
    [InlineData(WorkloadNetworkMode.HostNetwork, WorkloadNetworkMode.Cluster, true)]
    [InlineData(WorkloadNetworkMode.OperatorNetwork, WorkloadNetworkMode.Cluster, true)]
    [InlineData(WorkloadNetworkMode.Cluster, WorkloadNetworkMode.HostNetwork, false)]
    [InlineData(WorkloadNetworkMode.HostNetwork, WorkloadNetworkMode.HostNetwork, true)]
    [InlineData(WorkloadNetworkMode.OperatorNetwork, WorkloadNetworkMode.HostNetwork, false)]
    [InlineData(WorkloadNetworkMode.Cluster, WorkloadNetworkMode.OperatorNetwork, false)]
    [InlineData(WorkloadNetworkMode.HostNetwork, WorkloadNetworkMode.OperatorNetwork, false)]
    [InlineData(WorkloadNetworkMode.OperatorNetwork, WorkloadNetworkMode.OperatorNetwork, true)]
    public void NetworkMode_is_gated_by_the_partial_order_not_by_equality(
        WorkloadNetworkMode consentedMode, WorkloadNetworkMode desiredMode, bool expectedAllowed)
    {
        var consented = ModeProfile(consentedMode);
        var desired = ModeProfile(desiredMode);

        consented.Covers(desired, out var reasons).Should().Be(expectedAllowed);

        if (expectedAllowed)
        {
            reasons.Should().BeEmpty();
        }
        else
        {
            reasons.Should().Contain(
                r => r.StartsWith($"{nameof(WorkloadPrivilegeRequirements.NetworkMode)}:", StringComparison.Ordinal),
                "the violation is filed under NetworkMode in the file's PascalCase convention");
        }
    }

    [Fact]
    public void A_NetworkMode_violation_names_both_WIRE_spellings_never_the_enum_names()
    {
        var consented = ModeProfile(WorkloadNetworkMode.HostNetwork);
        var desired = ModeProfile(WorkloadNetworkMode.OperatorNetwork);

        consented.Covers(desired, out var reasons).Should().BeFalse();

        reasons.Should().ContainSingle().Which.Should()
            .Contain("NetworkMode").And.Contain("'operatorNetwork'").And.Contain("'hostNetwork'")
            .And.NotContain("OperatorNetwork").And.NotContain("HostNetwork:");
    }

    [Fact]
    public void Desired_false_boolean_against_consented_true_is_allowed()
    {
        var consented = Consented();
        var desired = Consented(hostNetwork: false, privileged: false, tun: false);
        consented.Covers(desired, out var reasons).Should().BeTrue();
        reasons.Should().BeEmpty();
    }

    [Fact]
    public void Capabilities_are_set_subset_a_desired_cap_outside_consent_is_rejected()
    {
        var consented = Consented(capabilities: new[] { "NET_ADMIN", "NET_RAW" });
        var desired = Consented(capabilities: new[] { "NET_ADMIN", "SYS_ADMIN" });

        consented.Covers(desired, out var reasons).Should().BeFalse();
        reasons.Should().ContainSingle()
            .Which.Should().Contain("Capabilities").And.Contain("SYS_ADMIN");
    }

    [Fact]
    public void Capabilities_a_desired_subset_is_allowed()
    {
        var consented = Consented(capabilities: new[] { "NET_ADMIN", "NET_RAW", "SYS_ADMIN" });
        var desired = Consented(capabilities: new[] { "NET_ADMIN" });
        consented.Covers(desired, out _).Should().BeTrue();
    }

    [Fact]
    public void Device_path_exact_match_is_allowed()
    {
        var consented = Consented(devicePaths: new[] { "/dev/ttyUSB0" });
        var desired = Consented(devicePaths: new[] { "/dev/ttyUSB0" });
        consented.Covers(desired, out _).Should().BeTrue();
    }

    [Fact]
    public void Device_path_matches_a_consented_glob()
    {
        var consented = Consented(devicePaths: new[] { "/dev/ttyUSB*" });
        var desired = Consented(devicePaths: new[] { "/dev/ttyUSB0" });
        consented.Covers(desired, out var reasons).Should().BeTrue();
        reasons.Should().BeEmpty();
    }

    [Fact]
    public void Device_path_not_covered_by_any_consented_path_or_glob_is_rejected()
    {
        var consented = Consented(devicePaths: new[] { "/dev/ttyUSB*" });
        var desired = Consented(devicePaths: new[] { "/dev/ttyACM0" });

        consented.Covers(desired, out var reasons).Should().BeFalse();
        reasons.Should().ContainSingle()
            .Which.Should().Contain("DevicePaths").And.Contain("/dev/ttyACM0");
    }

    [Fact]
    public void Host_path_exact_match_is_allowed()
    {
        var consented = Consented(hostPaths: new[] { "/sys/bus/usb" });
        var desired = Consented(hostPaths: new[] { "/sys/bus/usb" });
        consented.Covers(desired, out _).Should().BeTrue();
    }

    [Fact]
    public void Host_path_matches_a_consented_glob()
    {
        var consented = Consented(hostPaths: new[] { "/sys/bus/**" });
        var desired = Consented(hostPaths: new[] { "/sys/bus/usb" });
        consented.Covers(desired, out var reasons).Should().BeTrue();
        reasons.Should().BeEmpty();
    }

    [Fact]
    public void Host_path_not_covered_is_rejected()
    {
        var consented = Consented(hostPaths: new[] { "/sys/bus/usb" });
        var desired = Consented(hostPaths: new[] { "/sys/class/net" });

        consented.Covers(desired, out var reasons).Should().BeFalse();
        reasons.Should().ContainSingle()
            .Which.Should().Contain("HostPaths").And.Contain("/sys/class/net");
    }

    [Fact]
    public void Glob_star_does_not_cross_a_path_segment_boundary()
    {
        var consented = Consented(devicePaths: new[] { "/dev/*" });
        var desired = Consented(devicePaths: new[] { "/dev/bus/usb" });

        consented.Covers(desired, out var reasons).Should().BeFalse();
        reasons.Should().ContainSingle().Which.Should().Contain("/dev/bus/usb");
    }

    [Fact]
    public void A_wildcarded_desired_path_is_NOT_covered_by_a_different_consented_glob()
    {
        var consented = Consented(devicePaths: new[] { "/dev/x?" });
        var desired = Consented(devicePaths: new[] { "/dev/x*" });

        consented.Covers(desired, out var reasons).Should().BeFalse("a desired glob is wider than the consented glob and must not be covered by it");
        reasons.Should().ContainSingle().Which.Should().Contain("/dev/x*");
    }

    [Fact]
    public void A_wildcarded_desired_path_IS_covered_by_an_exactly_equal_consented_pattern()
    {
        var consented = Consented(devicePaths: new[] { "/dev/ttyUSB*" });
        var desired = Consented(devicePaths: new[] { "/dev/ttyUSB*" });

        consented.Covers(desired, out var reasons).Should().BeTrue();
        reasons.Should().BeEmpty();
    }

    [Fact]
    public void A_concrete_desired_path_still_matches_a_consented_glob_unchanged()
    {
        var consented = Consented(devicePaths: new[] { "/dev/ttyUSB*" });
        var desired = Consented(devicePaths: new[] { "/dev/ttyUSB0" });

        consented.Covers(desired, out var reasons).Should().BeTrue();
        reasons.Should().BeEmpty();
    }

    [Fact]
    public void Dbus_path_glob_match_is_allowed()
    {
        var consented = Consented(dbusPaths: new[] { "/run/**" });
        var desired = Consented(dbusPaths: new[] { "/run/dbus" });
        consented.Covers(desired, out var reasons).Should().BeTrue();
        reasons.Should().BeEmpty();
    }

    [Fact]
    public void Double_star_glob_crosses_segment_boundaries()
    {
        var consented = Consented(dbusPaths: new[] { "/run/**" });
        var desired = Consented(dbusPaths: new[] { "/run/user/1000/bus" });
        consented.Covers(desired, out _).Should().BeTrue();
    }

    [Fact]
    public void Kind_must_be_equal_a_different_kind_is_rejected()
    {
        var consented = Consented(kind: WorkloadKind.SingleNode);
        var desired = Consented(kind: WorkloadKind.Service);

        consented.Covers(desired, out var reasons).Should().BeFalse();
        reasons.Should().ContainSingle().Which.Should().Contain("Kind");
    }

    [Fact]
    public void Kind_scan_per_node_desired_against_consented_per_node_is_rejected_with_wire_strings()
    {
        var consented = Consented(kind: WorkloadKind.PerNode);
        var desired = Consented(kind: WorkloadKind.ScanPerNode);

        consented.Covers(desired, out var reasons).Should().BeFalse();
        reasons.Should().ContainSingle().Which.Should()
            .Contain("Kind").And.Contain("scan-per-node").And.Contain("per-node")
            .And.NotContain("ScanPerNode");
    }

    [Fact]
    public void Kind_scan_per_node_against_equal_scan_per_node_does_not_fail_the_kind_gate()
    {
        var p = Consented(kind: WorkloadKind.ScanPerNode);
        p.Covers(p, out var reasons).Should().BeTrue();
        reasons.Should().BeEmpty();
    }

    [Fact]
    public void Compare_per_node_to_scan_per_node_is_an_escalation_with_wire_strings()
    {
        var old = Consented(kind: WorkloadKind.PerNode);
        var @new = Consented(kind: WorkloadKind.ScanPerNode);

        var diff = WorkloadPrivilegeRequirements.Compare(old, @new);

        diff.Security.Should().Be(RequirementChangeKind.Escalation);
        diff.EscalatedFields.Should().Contain(f =>
            f.Contains("Kind") && f.Contains("per-node") && f.Contains("scan-per-node") && !f.Contains("ScanPerNode"));
    }

    [Fact]
    public void NodeLocalState_desired_present_against_consented_absent_is_rejected()
    {
        var consented = Consented(noNodeLocalState: true);
        var desired = Consented(nodeLocalState: new NodeLocalStateRequirement { Criticality = VolumeCriticality.Cache });

        consented.Covers(desired, out var reasons).Should().BeFalse();
        reasons.Should().ContainSingle().Which.Should().Contain("NodeLocalState");
    }

    [Fact]
    public void NodeLocalState_desired_absent_against_consented_present_is_allowed()
    {
        var consented = Consented(nodeLocalState: new NodeLocalStateRequirement { Criticality = VolumeCriticality.Critical });
        var desired = Consented(noNodeLocalState: true);
        consented.Covers(desired, out _).Should().BeTrue();
    }

    [Fact]
    public void NodeLocalState_desired_criticality_is_NOT_security_gated_a_stronger_criticality_is_allowed()
    {
        var consented = Consented(nodeLocalState: new NodeLocalStateRequirement { Criticality = VolumeCriticality.Recoverable });
        var desiredStronger = Consented(nodeLocalState: new NodeLocalStateRequirement { Criticality = VolumeCriticality.Critical });
        var desiredWeaker = Consented(nodeLocalState: new NodeLocalStateRequirement { Criticality = VolumeCriticality.Cache });
        var desiredEqual = Consented(nodeLocalState: new NodeLocalStateRequirement { Criticality = VolumeCriticality.Recoverable });

        consented.Covers(desiredStronger, out var stronger).Should().BeTrue("criticality is informational, not security-gated");
        stronger.Should().BeEmpty();
        consented.Covers(desiredWeaker, out _).Should().BeTrue();
        consented.Covers(desiredEqual, out _).Should().BeTrue();
    }

    [Fact]
    public void Patch_digest_equality_matching_digests_allowed()
    {
        var consented = Consented(patchDigest: "deadbeef");
        var desired = Consented(patchDigest: "deadbeef");
        consented.Covers(desired, out _).Should().BeTrue();
    }

    [Fact]
    public void Patch_digest_both_null_is_allowed()
    {
        var consented = Consented(patchDigest: null);
        var desired = Consented(patchDigest: null);
        consented.Covers(desired, out _).Should().BeTrue();
    }

    [Fact]
    public void Patch_digest_desired_value_against_consented_null_is_rejected()
    {
        var consented = Consented(patchDigest: null);
        var desired = Consented(patchDigest: "deadbeef");

        consented.Covers(desired, out var reasons).Should().BeFalse();
        reasons.Should().ContainSingle().Which.Should().Contain("Patch");
    }

    [Fact]
    public void Patch_digest_desired_null_against_consented_value_is_rejected()
    {
        var consented = Consented(patchDigest: "deadbeef");
        var desired = Consented(patchDigest: null);

        consented.Covers(desired, out var reasons).Should().BeFalse();
        reasons.Should().ContainSingle().Which.Should().Contain("Patch");
    }

    [Fact]
    public void Patch_digest_differing_values_are_rejected()
    {
        var consented = Consented(patchDigest: "aaaa");
        var desired = Consented(patchDigest: "bbbb");

        consented.Covers(desired, out var reasons).Should().BeFalse();
        reasons.Should().ContainSingle().Which.Should().Contain("Patch");
    }

    [Fact]
    public void Volumes_are_NOT_part_of_the_security_subset_gate_growth_and_additions_pass()
    {
        var consented = Consented();
        consented = consented with
        {
            Volumes = new[] { new VolumeRequirement { Id = "cache", Size = "64Mi", Criticality = VolumeCriticality.Cache } },
        };
        var desired = Consented() with
        {
            Volumes = new[]
            {
                new VolumeRequirement { Id = "cache", Size = "512Mi", Criticality = VolumeCriticality.Cache },
                new VolumeRequirement { Id = "extra", Size = "1Gi", Criticality = VolumeCriticality.Recoverable },
            },
        };

        consented.Covers(desired, out var reasons).Should().BeTrue();
        reasons.Should().BeEmpty();
    }

    [Fact]
    public void Multiple_violations_all_appear_in_the_reason_list()
    {
        var consented = Consented(privileged: false, capabilities: new[] { "NET_ADMIN" }, patchDigest: null);
        var desired = Consented(privileged: true, capabilities: new[] { "NET_ADMIN", "SYS_ADMIN" }, patchDigest: "x");

        consented.Covers(desired, out var reasons).Should().BeFalse();
        reasons.Should().HaveCountGreaterThanOrEqualTo(3);
        reasons.Should().Contain(r => r.Contains(nameof(WorkloadPrivilegeRequirements.Privileged)));
        reasons.Should().Contain(r => r.Contains("Capabilities"));
        reasons.Should().Contain(r => r.Contains("Patch"));
    }

    [Fact]
    public void IsSupersetOf_is_the_mirror_of_Allows()
    {
        var consented = Consented();
        var desired = Consented(privileged: false);
        consented.IsSupersetOf(desired, out _).Should().Be(consented.Covers(desired, out _));
    }
}

public sealed class ConsentedProfileImageDigestTests
{
    private const string DigestA = "sha256:a1" + "00000000000000000000000000000000000000000000000000000000000";
    private const string DigestB = "sha256:b2" + "00000000000000000000000000000000000000000000000000000000000";

    private static WorkloadPrivilegeRequirements Privileged(string? digest) => new()
    {
        Kind = WorkloadKind.SingleNode,
        RunAsRoot = true,
        Capabilities = new HashSet<string> { "NET_ADMIN" },
        ImageDigest = digest,
    };

    private static WorkloadPrivilegeRequirements NonPrivileged(string? digest) => new()
    {
        Kind = WorkloadKind.Service,
        ImageDigest = digest,
    };

    [Fact]
    public void A_PRIVILEGED_workloads_digest_bump_is_NOT_an_escalation()
    {
        var diff = WorkloadPrivilegeRequirements.Compare(Privileged(DigestA), Privileged(DigestB));

        diff.Security.Should().Be(RequirementChangeKind.Unchanged,
            "a moved image digest is no longer consent-bearing, whatever the privilege.");
        diff.EscalatedFields.Should().NotContain(f => f.StartsWith("Image:", StringComparison.Ordinal));
    }

    [Fact]
    public void A_privileged_digest_bump_is_still_DISCLOSED_as_an_informational_note()
    {
        var diff = WorkloadPrivilegeRequirements.Compare(Privileged(DigestA), Privileged(DigestB));

        diff.VolumeNotes.Should().Contain(n => n.StartsWith("Image:", StringComparison.Ordinal));
    }

    [Fact]
    public void A_legacy_NULL_digest_is_no_longer_consent_bearing()
    {
        var diff = WorkloadPrivilegeRequirements.Compare(Privileged(null), Privileged(DigestB));

        diff.Security.Should().Be(RequirementChangeKind.Unchanged,
            "an unknown prior digest must not manufacture an escalation now that the gate is gone.");
    }

    [Fact]
    public void An_UNCHANGED_digest_produces_no_image_note_at_all()
    {
        var diff = WorkloadPrivilegeRequirements.Compare(Privileged(DigestA), Privileged(DigestA));

        diff.Security.Should().Be(RequirementChangeKind.Unchanged);
        diff.VolumeNotes.Should().NotContain(n => n.StartsWith("Image:", StringComparison.Ordinal));
        diff.EscalatedFields.Should().NotContain(f => f.StartsWith("Image:", StringComparison.Ordinal));
    }

    [Fact]
    public void Compare_reports_a_NON_privileged_digest_bump_as_an_informational_note()
    {
        var diff = WorkloadPrivilegeRequirements.Compare(NonPrivileged(DigestA), NonPrivileged(DigestB));

        diff.Security.Should().Be(RequirementChangeKind.Unchanged,
            "an ordinary image update must not park the auto-update loop.");
        diff.VolumeNotes.Should().Contain(n => n.StartsWith("Image:", StringComparison.Ordinal),
            "…but the operator should still be able to SEE that the image moved.");
    }

    [Fact]
    public void Allows_deliberately_IGNORES_the_image_digest()
    {
        Privileged(DigestA).Covers(Privileged(DigestB), out var violations)
            .Should().BeTrue("the runtime ⊆ gate must not deadlock on an image change.");
        violations.Should().BeEmpty();
    }
}
