using FluentAssertions;
using Reeve.Workloads.Abstractions.Contracts;
using Reeve.Workloads.Abstractions.Contracts.Deployment;
using Reeve.Workloads.Abstractions.Domain;
using Reeve.Workloads.Domain;

namespace Reeve.Workloads.Abstractions.Tests;

public sealed class WorkloadPrivilegeRequirementsFingerprintTests
{
    private static WorkloadPrivilegeRequirements Requirements(params string[] capabilities) => new()
    {
        Kind = WorkloadKind.SingleNode,
        Mdns = true,
        Capabilities = new HashSet<string>(capabilities),
        DevicePaths = new HashSet<string> { "/dev/ttyUSB0", "/dev/ttyUSB1" },
        NodeLocalState = new NodeLocalStateRequirement { Criticality = VolumeCriticality.Recoverable },
        ImageDigest = "sha256:" + new string('a', 64),
    };

    [Fact]
    public void Equal_requirements_have_equal_fingerprints()
        => Requirements("NET_ADMIN").Fingerprint().Should().Be(Requirements("NET_ADMIN").Fingerprint());

    [Fact]
    public void It_is_lowercase_sha256_hex()
        => Requirements().Fingerprint().Should().MatchRegex("^[0-9a-f]{64}$");

    [Fact]
    public void An_added_capability_moves_it()
        => Requirements("NET_ADMIN", "SYS_ADMIN").Fingerprint().Should().NotBe(Requirements("NET_ADMIN").Fingerprint());

    [Fact]
    public void A_changed_node_local_state_criticality_moves_it()
        => (Requirements() with { NodeLocalState = new NodeLocalStateRequirement { Criticality = VolumeCriticality.Critical } })
            .Fingerprint().Should().NotBe(Requirements().Fingerprint());

    [Fact]
    public void A_changed_network_mode_moves_it()
        => (Requirements() with { NetworkMode = WorkloadNetworkMode.OperatorNetwork })
            .Fingerprint().Should().NotBe(Requirements().Fingerprint());

    [Fact]
    public void Every_security_flag_moves_it()
    {
        var baseline = Requirements().Fingerprint();
        WorkloadPrivilegeRequirements[] flipped =
        [
            Requirements() with { HostNetwork = true },
            Requirements() with { Mdns = false },
            Requirements() with { Ipv6Required = true },
            Requirements() with { Privileged = true },
            Requirements() with { RunAsRoot = true },
            Requirements() with { HostDbus = true },
            Requirements() with { TunDevice = true },
            Requirements() with { JoinsDeviceGroups = true },
            Requirements() with { CallsBackendRest = true },
            Requirements() with { Kind = WorkloadKind.Service },
            Requirements() with { DbusPaths = new HashSet<string> { "/run/dbus" } },
            Requirements() with { HostPaths = new HashSet<string> { "/sys/bus/usb" } },
            Requirements() with { UnsupportedPodPatchDigest = "sha256:" + new string('b', 64) },
        ];

        flipped.Select(r => r.Fingerprint()).Should().OnlyHaveUniqueItems().And.NotContain(baseline);
    }

    [Fact]
    public void The_image_digest_and_volumes_do_not_move_it()
    {
        var moved = Requirements() with
        {
            ImageDigest = "sha256:" + new string('c', 64),
            Volumes = [new VolumeRequirement { Id = "data", Size = "1Gi", Criticality = VolumeCriticality.Critical }],
        };

        moved.Fingerprint().Should().Be(Requirements().Fingerprint());
    }

    [Fact]
    public void Set_order_does_not_move_it()
    {
        var forwards = Requirements() with { DevicePaths = new HashSet<string> { "/dev/a", "/dev/b", "/dev/c" } };
        var backwards = Requirements() with { DevicePaths = new HashSet<string> { "/dev/c", "/dev/b", "/dev/a" } };

        forwards.Fingerprint().Should().Be(backwards.Fingerprint());
    }

    [Fact]
    public void A_value_moving_between_sets_moves_it()
    {
        var asDevice = Requirements() with { DevicePaths = new HashSet<string> { "/run/x" }, DbusPaths = new HashSet<string>() };
        var asDbus = Requirements() with { DevicePaths = new HashSet<string>(), DbusPaths = new HashSet<string> { "/run/x" } };

        asDevice.Fingerprint().Should().NotBe(asDbus.Fingerprint());
    }

    [Fact]
    public void A_module_fingerprint_is_independent_of_workload_order_and_moves_with_any_workload()
    {
        var one = WorkloadPrivilegeRequirementsPolicy.Fingerprint(new Dictionary<string, WorkloadPrivilegeRequirements>
        {
            ["db"] = Requirements(),
            ["api"] = Requirements("NET_ADMIN"),
        });
        var reordered = WorkloadPrivilegeRequirementsPolicy.Fingerprint(new Dictionary<string, WorkloadPrivilegeRequirements>
        {
            ["api"] = Requirements("NET_ADMIN"),
            ["db"] = Requirements(),
        });
        var swapped = WorkloadPrivilegeRequirementsPolicy.Fingerprint(new Dictionary<string, WorkloadPrivilegeRequirements>
        {
            ["db"] = Requirements("NET_ADMIN"),
            ["api"] = Requirements(),
        });

        one.Should().Be(reordered);
        one.Should().NotBe(swapped, "the same requirements on a different workload are a different deployment");
    }

    [Fact]
    public void A_renamed_workload_moves_the_module_fingerprint()
    {
        var before = WorkloadPrivilegeRequirementsPolicy.Fingerprint(
            new Dictionary<string, WorkloadPrivilegeRequirements> { ["db"] = Requirements() });
        var renamed = WorkloadPrivilegeRequirementsPolicy.Fingerprint(
            new Dictionary<string, WorkloadPrivilegeRequirements> { ["cache"] = Requirements() });

        before.Should().NotBe(renamed);
    }
}
