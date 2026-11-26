using System.Text.Json;
using FluentAssertions;
using Reeve.Workloads.Abstractions.Contracts;
using Reeve.Workloads.Abstractions.Contracts.Deployment;
using Reeve.Workloads.Abstractions.Domain;
using Reeve.Workloads.Abstractions.Domain.DesiredSpec;

using Reeve.Workloads.Abstractions.Providers;
using Reeve.Workloads.Domain;
using Reeve.Workloads.Domain.DesiredSpec;
using Reeve.Workloads.Tests.Shared;

namespace Reeve.Workloads.Abstractions.Tests;

public sealed class ConsentedProfileDerivationTests
{
    private static WorkloadManifest Workload(
        WorkloadKind kind,
        WorkloadNetwork? network = null,
        WorkloadPrivileges? privileges = null,
        IReadOnlyList<WorkloadVolume>? volumes = null,
        UnsupportedPodPatch? patch = null)
        => new()
        {
            Id = "w",
            DisplayName = "W",
            Image = new WorkloadImage { Ref = "reg/img", Digest = "sha256:" + new string('a', 64) },
            Kind = kind,
            Network = network,
            Privileges = privileges,
            Volumes = volumes ?? Array.Empty<WorkloadVolume>(),
            Health = new WorkloadHealth { Mode = WorkloadHealthMode.Container },
            UnsupportedPodPatch = patch,
        };

    [Fact]
    public void Matter_workload_resolves_the_serial_device_path_and_mdns()
    {
        var workload = Workload(
            WorkloadKind.SingleNode,
            network: new WorkloadNetwork { Mdns = true },
            privileges: new WorkloadPrivileges
            {
                Devices = new[] { new WorkloadDevice { Parameter = "serialPort" } },
            });

        var resolved = new Dictionary<string, string> { ["serialPort"] = "/dev/serial/by-id/usb-Thread_Radio" };
        var profile = WorkloadPrivilegeRequirements.FromWorkload(workload, resolved);

        profile.Kind.Should().Be(WorkloadKind.SingleNode);
        profile.Mdns.Should().BeTrue();
        profile.Privileged.Should().BeFalse();
        profile.DevicePaths.Should().BeEquivalentTo(new[] { "/dev/serial/by-id/usb-Thread_Radio" });
        profile.DbusPaths.Should().BeEmpty();
        profile.UnsupportedPodPatchDigest.Should().BeNull();
    }

    [Fact]
    public void Bluetooth_workload_resolves_dbus_path_capabilities_hostnetwork_and_a_critical_volume()
    {
        var workload = Workload(
            WorkloadKind.SingleNode,
            network: new WorkloadNetwork { HostNetwork = true },
            privileges: new WorkloadPrivileges
            {
                Capabilities = new[] { WorkloadCapability.NetAdmin, WorkloadCapability.NetRaw },
                HostDbus = true,
            },
            volumes: new[]
            {
                new WorkloadVolume
                {
                    Id = "bindkeys", MountPath = "/data", Size = "64Mi",
                    Criticality = VolumeCriticality.Critical, Description = "BLE bindkeys",
                },
            });

        var resolved = new Dictionary<string, string> { ["dbusHostPath"] = "/var/run/dbus" };
        var profile = WorkloadPrivilegeRequirements.FromWorkload(workload, resolved);

        profile.Kind.Should().Be(WorkloadKind.SingleNode);
        profile.HostNetwork.Should().BeTrue();
        profile.HostDbus.Should().BeTrue();
        profile.Capabilities.Should().BeEquivalentTo(new[] { "NET_ADMIN", "NET_RAW" });
        profile.DbusPaths.Should().BeEquivalentTo(new[] { "/var/run/dbus" });
        profile.Volumes.Should().ContainSingle();
        profile.Volumes[0].Id.Should().Be("bindkeys");
        profile.Volumes[0].Size.Should().Be("64Mi");
        profile.Volumes[0].Criticality.Should().Be(VolumeCriticality.Critical);
    }

    [Fact]
    public void HostPaths_workload_resolves_the_host_directory_into_the_hostPaths_set()
    {
        var workload = Workload(
            WorkloadKind.PerNode,
            privileges: new WorkloadPrivileges
            {
                HostPaths = new[] { new WorkloadHostPath { Parameter = "usbSys", MountPath = "/host/sys/bus/usb" } },
            });

        var resolved = new Dictionary<string, string> { ["usbSys"] = "/sys/bus/usb" };
        var profile = WorkloadPrivilegeRequirements.FromWorkload(workload, resolved);

        profile.HostPaths.Should().BeEquivalentTo(new[] { "/sys/bus/usb" });
        profile.DevicePaths.Should().BeEmpty();
    }

    [Fact]
    public void An_unresolved_hostPaths_parameter_is_skipped_never_injects_an_empty_path()
    {
        var workload = Workload(
            WorkloadKind.PerNode,
            privileges: new WorkloadPrivileges
            {
                HostPaths = new[] { new WorkloadHostPath { Parameter = "usbSys", MountPath = "/host/x" } },
            });

        var profile = WorkloadPrivilegeRequirements.FromWorkload(workload, new Dictionary<string, string>());
        profile.HostPaths.Should().BeEmpty();
    }

    [Fact]
    public void HostDbus_true_but_no_resolved_dbus_path_falls_back_to_the_spec_default()
    {
        var workload = Workload(
            WorkloadKind.SingleNode,
            privileges: new WorkloadPrivileges { HostDbus = true });

        var profile = WorkloadPrivilegeRequirements.FromWorkload(workload, new Dictionary<string, string>());
        profile.HostDbus.Should().BeTrue();
        profile.DbusPaths.Should().BeEquivalentTo(new[] { "/var/run/dbus" });
    }

    [Fact]
    public void Zwave_workload_resolves_runAsRoot_serial_device_and_recoverable_volume()
    {
        var workload = Workload(
            WorkloadKind.SingleNode,
            privileges: new WorkloadPrivileges
            {
                RunAsRoot = true,
                Devices = new[] { new WorkloadDevice { Parameter = "serialPort" } },
            },
            volumes: new[]
            {
                new WorkloadVolume
                {
                    Id = "cache", MountPath = "/cache", Size = "512Mi",
                    Criticality = VolumeCriticality.Recoverable,
                },
            });

        var resolved = new Dictionary<string, string> { ["serialPort"] = "/dev/ttyACM0" };
        var profile = WorkloadPrivilegeRequirements.FromWorkload(workload, resolved);

        profile.RunAsRoot.Should().BeTrue();
        profile.DevicePaths.Should().BeEquivalentTo(new[] { "/dev/ttyACM0" });
        profile.Volumes.Should().ContainSingle().Which.Criticality.Should().Be(VolumeCriticality.Recoverable);
    }

    [Fact]
    public void FromWorkload_derives_callsBackendRest_true_when_the_privilege_is_declared()
    {
        var workload = Workload(
            WorkloadKind.SingleNode,
            privileges: new WorkloadPrivileges { CallsBackendRest = true });

        var profile = WorkloadPrivilegeRequirements.FromWorkload(workload, new Dictionary<string, string>());

        profile.CallsBackendRest.Should().BeTrue("callsBackendRest is a consent-bearing privilege derived from the manifest");
    }

    [Fact]
    public void FromWorkload_callsBackendRest_defaults_false_when_absent()
    {
        var workload = Workload(WorkloadKind.SingleNode, privileges: new WorkloadPrivileges());

        var profile = WorkloadPrivilegeRequirements.FromWorkload(workload, new Dictionary<string, string>());

        profile.CallsBackendRest.Should().BeFalse("an opt-in privilege is false unless declared");
    }

    [Fact]
    public void A_plain_http_health_service_workload_has_no_privileges_or_paths()
    {
        var workload = Workload(WorkloadKind.Service, network: new WorkloadNetwork { HostNetwork = true, Ipv6Required = true });

        var profile = WorkloadPrivilegeRequirements.FromWorkload(workload, new Dictionary<string, string>());

        profile.Kind.Should().Be(WorkloadKind.Service);
        profile.HostNetwork.Should().BeTrue();
        profile.Ipv6Required.Should().BeTrue();
        profile.Privileged.Should().BeFalse();
        profile.RunAsRoot.Should().BeFalse();
        profile.DevicePaths.Should().BeEmpty();
        profile.DbusPaths.Should().BeEmpty();
        profile.NodeLocalState.Should().BeNull();
    }

    [Fact]
    public void NodeLocalState_is_carried_through_with_its_criticality()
    {
        var workload = Workload(
            WorkloadKind.PerNode,
            privileges: new WorkloadPrivileges
            {
                NodeLocalState = new NodeLocalState { Subdir = "bindkeys", Criticality = VolumeCriticality.Critical },
            });

        var profile = WorkloadPrivilegeRequirements.FromWorkload(workload, new Dictionary<string, string>());
        profile.NodeLocalState.Should().NotBeNull();
        profile.NodeLocalState!.Criticality.Should().Be(VolumeCriticality.Critical);
    }

    [Fact]
    public void Multiple_device_parameters_all_resolve_into_the_path_set()
    {
        var workload = Workload(
            WorkloadKind.SingleNode,
            privileges: new WorkloadPrivileges
            {
                Devices = new[]
                {
                    new WorkloadDevice { Parameter = "portA" },
                    new WorkloadDevice { Parameter = "portB" },
                },
            });

        var resolved = new Dictionary<string, string>
        {
            ["portA"] = "/dev/ttyUSB0",
            ["portB"] = "/dev/ttyUSB1",
        };
        var profile = WorkloadPrivilegeRequirements.FromWorkload(workload, resolved);
        profile.DevicePaths.Should().BeEquivalentTo(new[] { "/dev/ttyUSB0", "/dev/ttyUSB1" });
    }

    [Fact]
    public void An_unresolved_device_parameter_is_skipped_rather_than_recording_a_blank_path()
    {
        var workload = Workload(
            WorkloadKind.SingleNode,
            privileges: new WorkloadPrivileges
            {
                Devices = new[] { new WorkloadDevice { Parameter = "missing" } },
            });

        var profile = WorkloadPrivilegeRequirements.FromWorkload(workload, new Dictionary<string, string>());
        profile.DevicePaths.Should().BeEmpty();
    }

    [Fact]
    public void Consent_itemizes_the_device_at_every_bound_index()
    {
        var workload = Workload(
            WorkloadKind.SingleNode,
            privileges: new WorkloadPrivileges
            {
                Devices = new[] { new WorkloadDevice { Parameter = "acc", MaxCount = 3 } },
            });

        var resolved = new Dictionary<string, string>
        {
            ["acc[0]"] = "/dev/ttyUSB0",
            ["acc[1]"] = "/dev/ttyUSB1",
            ["acc[2]"] = "/dev/ttyUSB2",
        };
        var profile = WorkloadPrivilegeRequirements.FromWorkload(workload, resolved);

        profile.DevicePaths.Should().BeEquivalentTo(new[] { "/dev/ttyUSB0", "/dev/ttyUSB1", "/dev/ttyUSB2" });
    }

    [Fact]
    public void A_declared_key_value_on_a_multidevice_slot_does_not_enter_consent()
    {
        var workload = Workload(
            WorkloadKind.SingleNode,
            privileges: new WorkloadPrivileges
            {
                Devices = new[] { new WorkloadDevice { Parameter = "acc", MaxCount = 2 } },
            });

        var resolved = new Dictionary<string, string> { ["acc"] = "/dev/ttyUSB9" };
        var profile = WorkloadPrivilegeRequirements.FromWorkload(workload, resolved);

        profile.DevicePaths.Should().BeEmpty();
    }

    [Fact]
    public void Unsupported_pod_patch_digest_is_the_normalized_hash_of_the_body()
    {
        using var doc = JsonDocument.Parse("""[{"op":"add","path":"/spec/hostPID","value":true}]""");
        var body = doc.RootElement.Clone();
        var patch = new UnsupportedPodPatch { RawJson = body.GetRawText(), Body = body };

        var workload = Workload(WorkloadKind.Service, patch: patch);
        var profile = WorkloadPrivilegeRequirements.FromWorkload(workload, new Dictionary<string, string>());

        profile.UnsupportedPodPatchDigest.Should().Be(WorkloadPatchDigest.Normalize(patch));
        profile.UnsupportedPodPatchDigest.Should().MatchRegex("^[0-9a-f]{64}$");
    }

    [Fact]
    public void A_derived_profile_allows_itself_end_to_end()
    {
        var workload = Workload(
            WorkloadKind.SingleNode,
            network: new WorkloadNetwork { HostNetwork = true },
            privileges: new WorkloadPrivileges
            {
                Capabilities = new[] { WorkloadCapability.NetAdmin },
                HostDbus = true,
                Devices = new[] { new WorkloadDevice { Parameter = "serialPort" } },
            });
        var resolved = new Dictionary<string, string>
        {
            ["serialPort"] = "/dev/ttyACM0",
            ["dbusHostPath"] = "/run/dbus",
        };
        var profile = WorkloadPrivilegeRequirements.FromWorkload(workload, resolved);
        profile.Covers(profile, out var reasons).Should().BeTrue();
        reasons.Should().BeEmpty();
    }

    [Fact]
    public void FromWorkload_propagates_the_scan_per_node_kind()
    {
        var workload = Workload(WorkloadKind.ScanPerNode);

        var profile = WorkloadPrivilegeRequirements.FromWorkload(workload, new Dictionary<string, string>());

        profile.Kind.Should().Be(WorkloadKind.ScanPerNode);
    }

    private static WorkloadDesiredSpec DesiredSpec(ResolvedPrivileges? privileges = null, ResolvedNetwork? network = null)
        => new()
        {
            GroupInstanceId = Guid.NewGuid(),
            GroupId = "p",
            WorkloadId = "w",
            InstanceId = Guid.NewGuid(),
            Inst8 = "abcdef01",
            Provider = StandInProviders.KubernetesId,
            ResourceName = "res",
            SecretName = "sec",
            Labels = new Dictionary<string, string>(),
            Image = "reg/img@sha256:" + new string('a', 64),
            Kind = WorkloadKind.SingleNode,
            Network = network,
            Privileges = privileges,
            Health = new WorkloadHealth { Mode = new WorkloadHealthMode("adapter-session"), HeartbeatSeconds = 30 },
            SpecHash = "hash",
        };

    private const string NetworkModeParameter = "network.mode";
    private const string ReservedAttachmentsParameter = "network.attachments";

    private static WorkloadManifest LegacyHostNetworkWorkload()
        => Workload(WorkloadKind.SingleNode, network: new WorkloadNetwork { HostNetwork = true });

    [Theory]
    [InlineData("cluster", WorkloadNetworkMode.Cluster)]
    [InlineData("hostNetwork", WorkloadNetworkMode.HostNetwork)]
    [InlineData("operatorNetwork", WorkloadNetworkMode.OperatorNetwork)]
    public void FromWorkload_reads_the_consented_mode_off_the_RESOLVED_map_and_keeps_the_boolean_coherent(
        string rawMode, WorkloadNetworkMode expected)
    {
        var profile = WorkloadPrivilegeRequirements.FromWorkload(
            LegacyHostNetworkWorkload(),
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [NetworkModeParameter] = rawMode,
                [ReservedAttachmentsParameter] = "iot-macvlan-100",
            });

        profile.NetworkMode.Should().Be(expected);
        profile.HostNetwork.Should().Be(expected == WorkloadNetworkMode.HostNetwork);
    }

    [Theory]
    [InlineData(true, WorkloadNetworkMode.HostNetwork)]
    [InlineData(false, WorkloadNetworkMode.Cluster)]
    public void FromWorkload_with_no_resolved_mode_falls_back_to_the_authors_declaration(
        bool declaredHostNetwork, WorkloadNetworkMode expected)
    {
        var workload = Workload(WorkloadKind.SingleNode, network: new WorkloadNetwork { HostNetwork = declaredHostNetwork });

        var profile = WorkloadPrivilegeRequirements.FromWorkload(workload, new Dictionary<string, string>());

        profile.NetworkMode.Should().Be(expected);
    }

    [Fact]
    public void FromWorkload_falls_back_to_the_declaration_when_the_resolved_mode_is_out_of_vocabulary()
    {
        var profile = WorkloadPrivilegeRequirements.FromWorkload(
            LegacyHostNetworkWorkload(),
            new Dictionary<string, string>(StringComparer.Ordinal) { [NetworkModeParameter] = "OPERATORNETWORK" });

        profile.NetworkMode.Should().Be(WorkloadNetworkMode.HostNetwork, "an unrecognised value falls back to the declaration");
    }

    [Theory]
    [InlineData("cluster", WorkloadNetworkMode.Cluster)]
    [InlineData("hostNetwork", WorkloadNetworkMode.HostNetwork)]
    [InlineData("operatorNetwork", WorkloadNetworkMode.OperatorNetwork)]
    public void The_mode_derived_from_a_manifest_plus_params_and_from_the_resolved_spec_AGREE(
        string rawMode, WorkloadNetworkMode expected)
    {
        var workload = LegacyHostNetworkWorkload();
        var resolved = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            [NetworkModeParameter] = rawMode,
            [ReservedAttachmentsParameter] = "iot-macvlan-100",
        };

        var fromWorkload = WorkloadPrivilegeRequirements.FromWorkload(
            workload,
            resolved.Where(kv => kv.Value is not null)
                .ToDictionary(kv => kv.Key, kv => kv.Value!, StringComparer.Ordinal));

        var spec = WorkloadDesiredSpecFactory.Compute(
            workload,
            FactorySettings(),
            resolved,
            FactoryBootstrap(),
            StandInProviders.Kubernetes,
            DefaultWorkloadResourceNamer.Instance,
            Guid.NewGuid(),
            "com.reeve.legacy",
            Guid.NewGuid(),
            "install-1");
        var fromSpec = WorkloadPrivilegeRequirements.FromDesiredSpec(spec);

        fromWorkload.NetworkMode.Should().Be(expected);
        fromSpec.NetworkMode.Should().Be(fromWorkload.NetworkMode, "both derivations gate the SAME install");
        fromSpec.HostNetwork.Should().Be(fromWorkload.HostNetwork);
    }

    [Fact]
    public void A_name_only_change_with_an_unchanged_mode_derives_an_IDENTICAL_profile_and_is_not_an_escalation()
    {
        var workload = LegacyHostNetworkWorkload();

        WorkloadPrivilegeRequirements Derive(string names) => WorkloadPrivilegeRequirements.FromWorkload(
            workload,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [NetworkModeParameter] = "operatorNetwork",
                [ReservedAttachmentsParameter] = names,
            });

        var before = Derive("iot-macvlan-a");
        var after = Derive("iot-macvlan-b,cams");

        after.Should().BeEquivalentTo(before, "attachment names are deliberately absent from the profile");

        var diff = WorkloadPrivilegeRequirements.Compare(before, after);
        diff.Security.Should().Be(RequirementChangeKind.Unchanged);
        diff.EscalatedFields.Should().BeEmpty();
        diff.NarrowedFields.Should().BeEmpty();
    }

    [Fact]
    public void FromDesiredSpec_reads_the_resolved_mode_and_carries_NO_attachment_names()
    {
        var spec = DesiredSpec(network: new ResolvedNetwork
        {
            Mode = WorkloadNetworkMode.OperatorNetwork,
            Attachments = new[] { "iot-macvlan-100", "cams" },
        });

        var profile = WorkloadPrivilegeRequirements.FromDesiredSpec(spec);
        var renamed = WorkloadPrivilegeRequirements.FromDesiredSpec(DesiredSpec(network: new ResolvedNetwork
        {
            Mode = WorkloadNetworkMode.OperatorNetwork,
            Attachments = new[] { "lab-bridge" },
        }));

        profile.NetworkMode.Should().Be(WorkloadNetworkMode.OperatorNetwork);
        profile.HostNetwork.Should().BeFalse();
        profile.Fingerprint().Should().Be(renamed.Fingerprint(), "the attachment names are not part of the requirements");
    }

    private static WorkloadOrchestratorSettings FactorySettings()
        => new() { Mirrors = Array.Empty<RegistryMirror>() };

    private static WorkloadBootstrapContext FactoryBootstrap()
        => new()
        {
            InstanceId = Guid.Parse("11111111-2222-3333-4444-555555555555"),
            SecretName = "reeve-wl-token-secret",
            Values = new Dictionary<string, BootstrapValue>(StringComparer.Ordinal)
            {
                ["gatewayUrl"] = BootstrapValue.Literal(
                    "wss://reeve.local/ws/workloads/11111111-2222-3333-4444-555555555555"),
                ["backendBaseUrl"] = BootstrapValue.Literal("https://reeve.local"),
                ["workloadToken"] = BootstrapValue.SecretRef("reeve-wl-token-secret"),
                ["specHash"] = BootstrapValue.SpecHash,
            },
        };

    [Fact]
    public void FromDesiredSpec_propagates_the_scan_per_node_kind()
    {
        var spec = DesiredSpec() with { Kind = WorkloadKind.ScanPerNode };

        var profile = WorkloadPrivilegeRequirements.FromDesiredSpec(spec);

        profile.Kind.Should().Be(WorkloadKind.ScanPerNode);
    }

    [Fact]
    public void FromDesiredSpec_reads_runAsRoot_independently_of_privileged()
    {
        var spec = DesiredSpec(new ResolvedPrivileges { RunAsRoot = true, Privileged = false });

        var profile = WorkloadPrivilegeRequirements.FromDesiredSpec(spec);

        profile.RunAsRoot.Should().BeTrue("RunAsRoot must reflect the REAL resolved value, not be derived from Privileged.");
        profile.Privileged.Should().BeFalse();
    }

    [Fact]
    public void FromDesiredSpec_runAsRoot_false_stays_false_even_when_privileged_true()
    {
        var spec = DesiredSpec(new ResolvedPrivileges { RunAsRoot = false, Privileged = true });

        var profile = WorkloadPrivilegeRequirements.FromDesiredSpec(spec);

        profile.RunAsRoot.Should().BeFalse("RunAsRoot is independent — a privileged spec with runAsRoot:false stays false.");
        profile.Privileged.Should().BeTrue();
    }

    private static WorkloadPrivilegeRequirements GroupGrant(bool joins, IReadOnlyList<int>? groupIds = null)
        => WorkloadPrivilegeRequirements.FromDesiredSpec(DesiredSpec(new ResolvedPrivileges
        {
            DevicePaths = new[] { "/dev/dri/renderD128" },
            JoinsDeviceGroups = joins,
            DeviceGroupIds = groupIds ?? Array.Empty<int>(),
        }));

    [Fact]
    public void Turning_the_device_group_grant_on_is_not_allowed_by_the_old_consent()
    {
        var consented = GroupGrant(false);
        var desired = GroupGrant(true);

        consented.Covers(desired, out var violations).Should().BeFalse();
        violations.Should().ContainSingle()
            .Which.Should().Contain(nameof(WorkloadPrivilegeRequirements.JoinsDeviceGroups));
    }

    [Fact]
    public void An_already_consented_device_group_grant_allows_the_same_desired_grant()
    {
        GroupGrant(true).Covers(GroupGrant(true), out var violations).Should().BeTrue();
        violations.Should().BeEmpty();
    }

    [Fact]
    public void Narrowing_the_device_group_grant_off_is_allowed()
    {
        GroupGrant(true).Covers(GroupGrant(false), out var violations).Should().BeTrue();
        violations.Should().BeEmpty();
    }

    [Fact]
    public void Both_derivations_agree_for_an_auto_slot_whose_group_id_is_unknown()
    {
        var workload = Workload(
            WorkloadKind.SingleNode,
            privileges: new WorkloadPrivileges
            {
                Devices = new[] { new WorkloadDevice { Parameter = "acc", Group = WorkloadDeviceGroup.Auto } },
            });

        var fromWorkload = WorkloadPrivilegeRequirements.FromWorkload(
            workload,
            new Dictionary<string, string> { ["acc"] = "/dev/dri/renderD128" });
        var fromSpec = GroupGrant(true);

        fromWorkload.JoinsDeviceGroups.Should().BeTrue();
        fromSpec.JoinsDeviceGroups.Should().BeTrue("the resolved spec carries the FACT, not the GID count");
        fromSpec.JoinsDeviceGroups.Should().Be(fromWorkload.JoinsDeviceGroups, "both derivations gate the SAME install");
    }

    [Fact]
    public void FromWorkload_records_no_grant_for_a_group_none_slot()
    {
        var workload = Workload(
            WorkloadKind.SingleNode,
            privileges: new WorkloadPrivileges
            {
                Devices = new[] { new WorkloadDevice { Parameter = "acc", Group = WorkloadDeviceGroup.None } },
            });

        var profile = WorkloadPrivilegeRequirements.FromWorkload(
            workload,
            new Dictionary<string, string> { ["acc"] = "/dev/dri/renderD128" });

        profile.JoinsDeviceGroups.Should().BeFalse();
        profile.DevicePaths.Should().BeEquivalentTo(new[] { "/dev/dri/renderD128" });
    }

    [Fact]
    public void FromWorkload_records_no_grant_when_an_auto_slot_bound_nothing()
    {
        var workload = Workload(
            WorkloadKind.SingleNode,
            privileges: new WorkloadPrivileges
            {
                Devices = new[] { new WorkloadDevice { Parameter = "acc", Group = WorkloadDeviceGroup.Auto } },
            });

        var profile = WorkloadPrivilegeRequirements.FromWorkload(workload, new Dictionary<string, string> { ["acc"] = "   " });

        profile.JoinsDeviceGroups.Should().BeFalse();
        profile.DevicePaths.Should().BeEmpty();
    }

    [Fact]
    public void A_renumbered_group_id_does_not_change_the_consent_record()
    {
        var debian = GroupGrant(true, new[] { 104 });
        var ubuntu = GroupGrant(true, new[] { 993 });

        debian.Covers(ubuntu, out var violations).Should().BeTrue();
        violations.Should().BeEmpty();
    }

    [Fact]
    public void Turning_the_device_group_grant_on_is_reported_as_an_escalation()
    {
        var diff = WorkloadPrivilegeRequirements.Compare(GroupGrant(false), GroupGrant(true));

        diff.Security.Should().Be(RequirementChangeKind.Escalation);
        diff.EscalatedFields.Should().ContainSingle()
            .Which.Should().Contain(nameof(WorkloadPrivilegeRequirements.JoinsDeviceGroups));
        diff.NarrowedFields.Should().NotContain(f => f.Contains(nameof(WorkloadPrivilegeRequirements.JoinsDeviceGroups)));
    }

    [Fact]
    public void FromDesiredSpec_maps_the_other_security_axis_fields_from_the_resolved_spec()
    {
        var spec = DesiredSpec(
            privileges: new ResolvedPrivileges
            {
                Privileged = true,
                RunAsRoot = true,
                TunDevice = true,
                CallsBackendRest = true,
                Capabilities = new[] { "NET_ADMIN", "SYS_ADMIN" },
                DevicePaths = new[] { "/dev/ttyUSB0" },
                DbusHostPath = "/run/dbus",
                NodeLocalState = new ResolvedNodeLocalState { HostPath = "/var/lib/reeve/p/w", Criticality = VolumeCriticality.Critical },
            },
            network: new ResolvedNetwork { HostNetwork = true, Mdns = true, Ipv6Required = true });

        var profile = WorkloadPrivilegeRequirements.FromDesiredSpec(spec);

        profile.HostNetwork.Should().BeTrue();
        profile.Mdns.Should().BeTrue();
        profile.Ipv6Required.Should().BeTrue();
        profile.Privileged.Should().BeTrue();
        profile.RunAsRoot.Should().BeTrue();
        profile.TunDevice.Should().BeTrue();
        profile.CallsBackendRest.Should().BeTrue("FromDesiredSpec must read the REAL resolved callsBackendRest so the applied/target spec is gated (A5)");
        profile.HostDbus.Should().BeTrue("HostDbus is expressed as the presence of a resolved DbusHostPath.");
        profile.Capabilities.Should().BeEquivalentTo(new[] { "NET_ADMIN", "SYS_ADMIN" });
        profile.DevicePaths.Should().BeEquivalentTo(new[] { "/dev/ttyUSB0" });
        profile.DbusPaths.Should().BeEquivalentTo(new[] { "/run/dbus" });
        profile.NodeLocalState.Should().NotBeNull();
        profile.NodeLocalState!.Criticality.Should().Be(VolumeCriticality.Critical);
    }
}
