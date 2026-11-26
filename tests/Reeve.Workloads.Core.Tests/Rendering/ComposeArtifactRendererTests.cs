using Reeve.Workloads.Abstractions.Providers;
using Reeve.Workloads.Providers.Docker;
using System.Collections.Generic;
using FluentAssertions;
using static Reeve.Workloads.Core.Tests.Rendering.ArtifactSpecFactory;
using Reeve.Workloads.Abstractions.Contracts;
using Reeve.Workloads.Abstractions.Contracts.Deployment;
using Reeve.Workloads.Abstractions.Domain;
using Reeve.Workloads.Abstractions.Domain.DesiredSpec;
using Reeve.Workloads.Abstractions.Rendering;
using Reeve.Workloads.Domain;
using Reeve.Workloads.Providers.Docker.Rendering;
using Xunit;

namespace Reeve.Workloads.Core.Tests.Rendering;

public sealed class ComposeArtifactRendererTests
{
    private static readonly ComposeArtifactRenderer Renderer = new();

    private static WorkloadArtifactRenderOptions Reveal(string token = "rwt_secrettoken123") =>
        Revealing(token);

    private static WorkloadArtifactRenderOptions External() =>
        new();

    [Fact]
    public void Format_is_compose()
    {
        Renderer.Format.Should().Be(WorkloadArtifactFormatId.Parse("compose"));
        Renderer.IsSelfContained.Should().BeTrue();
        Renderer.Render(Spec(), External()).Format.Should().Be(WorkloadArtifactFormatId.Parse("compose"));
    }

    [Fact]
    public void Service_is_named_the_resource_name_and_carries_the_image_verbatim()
    {
        var content = Renderer.Render(Spec(), External()).PrimaryContent;

        content.Should().Contain($"  {ResourceName}:");
        content.Should().Contain($"    image: \"ghcr.io/reeve/matter@{Digest}\"");
    }

    [Fact]
    public void Restart_policy_is_unless_stopped()
    {
        Renderer.Render(Spec(), External()).PrimaryContent.Should().Contain("restart: unless-stopped");
    }

    [Fact]
    public void Environment_carries_literals_and_gateway_and_spec_hash()
    {
        var content = Renderer.Render(Spec(), External()).PrimaryContent;

        content.Should().Contain("\"LOG_LEVEL\": \"info\"");
        content.Should().Contain($"\"REEVE_GATEWAY_URL\": \"{GatewayUrl}\"");
        content.Should().Contain($"\"REEVE_SPEC_HASH\": \"{SpecHashValue}\"");
    }

    [Fact]
    public void Revealed_token_is_inlined_and_external_mode_is_a_placeholder()
    {
        Renderer.Render(Spec(), Reveal("rwt_abc")).PrimaryContent
            .Should().Contain("\"REEVE_WORKLOAD_TOKEN\": \"rwt_abc\"");
        Renderer.Render(Spec(), External()).PrimaryContent
            .Should().Contain("\"REEVE_WORKLOAD_TOKEN\": \"<set-this-secret>\"");
    }

    [Fact]
    public void A_chmod_600_note_is_present_for_the_inline_token()
    {
        Renderer.Render(Spec(), Reveal()).Notes.Should().Contain(n => n.Contains("chmod 600"));
    }

    [Fact]
    public void A_port_with_a_host_port_is_published()
    {
        var spec = Spec(network: new ResolvedNetwork
        {
            Ports = new[]
            {
                new ResolvedPort { Name = "api", Port = 8080, Protocol = "tcp", HostPort = 18080 },
            },
        });
        var content = Renderer.Render(spec, External()).PrimaryContent;

        content.Should().Contain("ports:");
        content.Should().Contain("\"18080:8080/tcp\"");
    }

    [Fact]
    public void A_port_without_a_host_port_is_not_published()
    {
        var spec = Spec(network: new ResolvedNetwork
        {
            Ports = new[]
            {
                new ResolvedPort { Name = "api", Port = 8080, Protocol = "tcp", HostPort = null },
            },
        });
        var content = Renderer.Render(spec, External()).PrimaryContent;

        content.Should().NotContain("8080");
    }

    [Fact]
    public void M3_host_network_with_a_host_port_does_not_emit_a_ports_block()
    {
        var spec = Spec(network: new ResolvedNetwork
        {
            HostNetwork = true,
            Ports = new[]
            {
                new ResolvedPort { Name = "api", Port = 8080, Protocol = "tcp", HostPort = 18080 },
            },
        });
        var result = Renderer.Render(spec, External());

        result.PrimaryContent.Should().Contain("network_mode: host");
        result.PrimaryContent.Should().NotContain("ports:");
        result.PrimaryContent.Should().NotContain("18080:8080");
        result.Notes.Should().Contain(n =>
            n.Contains("host networking") && (n.Contains("port") || n.Contains("published")),
            "the operator must be TOLD the published ports are implicit under host networking, not silently dropped");
    }

    [Fact]
    public void M3_non_host_network_with_a_host_port_still_publishes_the_port_block()
    {
        var spec = Spec(network: new ResolvedNetwork
        {
            HostNetwork = false,
            Ports = new[]
            {
                new ResolvedPort { Name = "api", Port = 8080, Protocol = "tcp", HostPort = 18080 },
            },
        });
        var result = Renderer.Render(spec, External());

        result.PrimaryContent.Should().Contain("ports:");
        result.PrimaryContent.Should().Contain("\"18080:8080/tcp\"");
        result.Notes.Should().NotContain(n => n.Contains("host networking") && n.Contains("published"));
    }

    [Fact]
    public void Privileged_and_caps_and_tun_and_devices_map_through()
    {
        var spec = Spec(privileges: new ResolvedPrivileges
        {
            Privileged = true,
            Capabilities = new[] { "NET_ADMIN", "NET_RAW" },
            TunDevice = true,
            DevicePaths = new[] { "/dev/ttyUSB0" },
        });
        var content = Renderer.Render(spec, External()).PrimaryContent;

        content.Should().Contain("privileged: true");
        content.Should().Contain("cap_add:");
        content.Should().Contain("- NET_ADMIN");
        content.Should().Contain("- NET_RAW");
        content.Should().Contain("devices:");
        content.Should().Contain("- \"/dev/ttyUSB0:/dev/ttyUSB0\"");
        content.Should().Contain("- \"/dev/net/tun:/dev/net/tun\"");
    }

    [Fact]
    public void A_privilege_needed_only_for_kubernetes_device_passthrough_stays_out_of_compose()
    {
        var spec = Spec(privileges: new ResolvedPrivileges
        {
            DevicePassthroughPrivileged = true,
            DevicePaths = new[] { "/dev/ttyUSB0" },
        });
        var content = Renderer.Render(spec, External()).PrimaryContent;

        content.Should().NotContain("privileged:");
        content.Should().Contain("devices:");
        content.Should().Contain("- \"/dev/ttyUSB0:/dev/ttyUSB0\"");
    }

    [Fact]
    public void A_dollar_in_a_value_is_doubled_so_compose_does_not_interpolate_it()
    {
        var spec = Spec(env:
        [
            new ResolvedEnvVar { Name = "DB_URL", Kind = ResolvedEnvKind.Literal, Value = "postgres://u:pa$w0rd@db" },
            new ResolvedEnvVar { Name = "TOKEN", Kind = ResolvedEnvKind.Literal, Value = "${POSTGRES_PASSWORD}" },
        ]);

        var content = Renderer.Render(spec, External()).PrimaryContent;

        content.Should().Contain("\"DB_URL\": \"postgres://u:pa$$w0rd@db\"");
        content.Should().Contain("\"TOKEN\": \"$${POSTGRES_PASSWORD}\"");
    }

    [Fact]
    public void Run_as_root_renders_user_zero_like_the_managed_container()
    {
        Renderer.Render(Spec(privileges: new ResolvedPrivileges { RunAsRoot = true }), External())
            .PrimaryContent.Should().Contain("    user: \"0\"");
        Renderer.Render(Spec(privileges: new ResolvedPrivileges { RunAsRoot = false }), External())
            .PrimaryContent.Should().NotContain("user:");
    }

    [Fact]
    public void A_read_only_volume_mounts_read_only_like_the_managed_container()
    {
        var vol = new ResolvedVolume
        {
            Id = "data",
            MountPath = "/data",
            Size = "64Mi",
            AccessMode = "rox",
            Criticality = VolumeCriticality.Critical,
            ResourceName = "reeve-wl-com-reeve-matter-matter-data-11111111",
        };

        Renderer.Render(Spec(volumes: new[] { vol }), External()).PrimaryContent.Should().Contain(":/data:ro\"");
    }

    [Fact]
    public void Dbus_is_a_read_only_bind_mount()
    {
        var spec = Spec(privileges: new ResolvedPrivileges { DbusHostPath = "/var/run/dbus" });
        var content = Renderer.Render(spec, External()).PrimaryContent;

        content.Should().Contain("- \"/var/run/dbus:/var/run/dbus:ro\"");
    }

    [Fact]
    public void Host_path_is_a_read_only_bind_mount_at_its_container_mount_path()
    {
        var spec = Spec(privileges: new ResolvedPrivileges
        {
            HostPaths = new[] { new ResolvedHostPath { HostPath = "/sys/bus/usb", MountPath = "/host/sys/bus/usb" } },
        });
        var content = Renderer.Render(spec, External()).PrimaryContent;

        content.Should().Contain("- \"/sys/bus/usb:/host/sys/bus/usb:ro\"");
    }

    [Fact]
    public void I2_host_network_emits_network_mode_host()
    {
        var spec = Spec(network: new ResolvedNetwork { HostNetwork = true });
        var content = Renderer.Render(spec, External()).PrimaryContent;

        content.Should().Contain("network_mode: host");
    }

    [Fact]
    public void I2_no_host_network_does_not_emit_network_mode()
    {
        var spec = Spec(network: new ResolvedNetwork { HostNetwork = false });
        var content = Renderer.Render(spec, External()).PrimaryContent;

        content.Should().NotContain("network_mode");
    }

    [Fact]
    public void I2_mdns_and_ipv6_without_host_network_add_a_note()
    {
        var spec = Spec(network: new ResolvedNetwork { HostNetwork = false, Mdns = true, Ipv6Required = true });
        var result = Renderer.Render(spec, External());

        result.Notes.Should().Contain(n =>
            (n.Contains("mDNS") || n.Contains("mdns")) && (n.Contains("IPv6") || n.Contains("ipv6")));
    }

    [Fact]
    public void I2_host_network_carries_mdns_and_ipv6_transparently_without_an_extra_note()
    {
        var spec = Spec(network: new ResolvedNetwork { HostNetwork = true, Mdns = true, Ipv6Required = true });
        var result = Renderer.Render(spec, External());

        result.PrimaryContent.Should().Contain("network_mode: host");
        result.Notes.Should().NotContain(n => n.Contains("macvlan"));
    }

    [Fact]
    public void R30_an_attachment_renders_a_top_level_networks_block_external_true_plus_the_retained_shared_bridge()
    {
        var content = Renderer.Render(Spec(network: OperatorNetwork("iot-vlan100")), External()).PrimaryContent;

        content.Should().Contain("    networks:\n      - reeve-workloads\n      - iot-vlan100\n");

        content.Should().Contain("networks:\n  reeve-workloads:\n    name: \"reeve-workloads\"\n");
        content.Should().Contain("  iot-vlan100:\n    external: true\n    name: \"iot-vlan100\"");

        content.Should().NotContain("network_mode");
    }

    [Fact]
    public void R30_attachments_render_in_declared_order_and_are_never_sorted()
    {
        var content = Renderer.Render(Spec(network: OperatorNetwork("zone-b-net", "alpha-net")), External())
            .PrimaryContent;

        content.IndexOf("- zone-b-net", System.StringComparison.Ordinal)
            .Should().BeLessThan(content.IndexOf("- alpha-net", System.StringComparison.Ordinal),
                "the declared order is part of the SpecHash and must never be sorted");
    }

    [Fact]
    public void R30_the_operator_note_is_the_driver_unknown_variant_and_names_no_driver()
    {
        var result = Renderer.Render(Spec(network: OperatorNetwork("iot-vlan100")), External());

        var note = result.Notes.Should().ContainSingle(n => n.Contains(DriverUnknownCopy)).Subject;
        note.Should().Contain("iot-vlan100");
        note.Should().Contain("Reeve cannot verify an out-of-band network");
        note.Should().Contain("you own their configuration");
        note.Should().NotContainEquivalentOf("driver").And.NotContainEquivalentOf("verdict");

        foreach (var token in DriverTokens)
        {
            note.Should().NotContain(token, "guessing a driver would be a false disclosure");
        }
    }

    [Fact]
    public void R30_a_manual_artifact_never_carries_the_isolation_or_lateral_reach_copy_even_for_driver_named_networks()
    {
        var everything = Everything(
            Renderer.Render(Spec(network: OperatorNetwork("iot-macvlan", "lab-bridge")), External()));

        everything.Should().NotContain(IsolationCopy);
        everything.Should().NotContain(LateralReachCopy);
        everything.Should().Contain(DriverUnknownCopy);
    }

    [Fact]
    public void R30_the_host_network_arm_is_kept_and_emits_no_networks_block_and_no_operator_note()
    {
        var result = Renderer.Render(
            Spec(network: new ResolvedNetwork { HostNetwork = true, Mode = WorkloadNetworkMode.HostNetwork }),
            External());

        result.PrimaryContent.Should().Contain("network_mode: host");
        result.PrimaryContent.Should().NotContain("networks:");
        result.Notes.Should().NotContain(n => n.Contains(DriverUnknownCopy));
    }

    [Fact]
    public void R30_an_incoherent_host_network_plus_attachments_spec_still_emits_no_networks_block()
    {
        var result = Renderer.Render(
            Spec(network: new ResolvedNetwork
            {
                HostNetwork = true,
                Mode = WorkloadNetworkMode.HostNetwork,
                Attachments = new[] { "iot-vlan100" },
            }),
            External());

        result.PrimaryContent.Should().Contain("network_mode: host");
        result.PrimaryContent.Should().NotContain("networks:");
        result.PrimaryContent.Should().NotContain("iot-vlan100");
        result.Notes.Should().NotContain(n => n.Contains(DriverUnknownCopy));
    }

    [Fact]
    public void R30_a_cluster_mode_workload_renders_no_networks_block_and_no_operator_note()
    {
        var result = Renderer.Render(Spec(network: new ResolvedNetwork { HostNetwork = false }), External());

        result.PrimaryContent.Should().NotContain("networks:");
        result.PrimaryContent.Should().NotContain("external: true");
        result.Notes.Should().NotContain(n => n.Contains(DriverUnknownCopy));
    }

    [Fact]
    public void I3_node_local_state_adds_a_read_write_host_path_bind_mount()
    {
        var spec = Spec(privileges: new ResolvedPrivileges
        {
            NodeLocalState = new ResolvedNodeLocalState
            {
                HostPath = "/var/lib/reeve/ble/state",
                Criticality = VolumeCriticality.Critical,
            },
        });
        var content = Renderer.Render(spec, External()).PrimaryContent;

        content.Should().Contain("volumes:");
        content.Should().Contain("- \"/var/lib/reeve/ble/state:/var/lib/reeve/ble/state\"");
    }

    [Fact]
    public void Resources_are_surfaced_at_least_as_a_note()
    {
        var spec = Spec(resources: new ResolvedResources
        {
            RequestsCpu = "100m",
            RequestsMemory = "256Mi",
            LimitsCpu = "500m",
            LimitsMemory = "512Mi",
        });
        var result = Renderer.Render(spec, External());

        result.Notes.Should().Contain(n => n.Contains("resource") || n.Contains("mem_limit") || n.Contains("cpus"));
    }

    [Fact]
    public void A_volume_uses_the_m10_docker_named_volume_scheme()
    {
        var vol = new ResolvedVolume
        {
            Id = "data",
            MountPath = "/data",
            Size = "64Mi",
            Criticality = VolumeCriticality.Critical,
            ResourceName = "reeve-wl-com-reeve-matter-matter-data-11111111",
        };
        var expectedVolumeName = DockerResourceNamer.Instance.VolumeName(
                            new WorkloadNamingSubject
                            {
                                GroupId = "com.reeve.matter",
                                WorkloadId = string.Empty,
                                Inst8 = Inst8,
                            },
                            "data");

        var content = Renderer.Render(Spec(volumes: new[] { vol }), External()).PrimaryContent;

        content.Should().Contain($"- \"{expectedVolumeName}:/data\"");
        content.Should().Contain($"  {expectedVolumeName}:");
        content.Should().Contain($"name: \"{expectedVolumeName}\"");
    }

    [Fact]
    public void A_restore_adopted_volume_is_referenced_by_its_physical_external_name()
    {
        const string adoptedVolume = "reeve-com-reeve-matter-data-99999999";
        var vol = new ResolvedVolume
        {
            Id = "data",
            MountPath = "/data",
            Size = "64Mi",
            Criticality = VolumeCriticality.Critical,
            ResourceName = adoptedVolume,
            ExistingClaim = true,
        };

        var content = Renderer.Render(Spec(volumes: new[] { vol }), External()).PrimaryContent;

        content.Should().Contain($"- \"{adoptedVolume}:/data\"",
            "the compose file mounts the retained volume by its physical name");
        content.Should().Contain($"  {adoptedVolume}:");
        content.Should().Contain("external: true",
            "an adopted retained volume is external so docker mounts it in place, never (re)creating empty data");
        content.Should().Contain($"name: \"{adoptedVolume}\"",
            "the external volume is bound to the exact retained physical name");
    }

    [Fact]
    public void Golden_compose_external_secret_mode()
    {
        var spec = Spec(
            network: new ResolvedNetwork
            {
                Ports = new[]
                {
                    new ResolvedPort { Name = "api", Port = 8080, Protocol = "tcp", HostPort = 18080 },
                },
            },
            privileges: new ResolvedPrivileges
            {
                Capabilities = new[] { "NET_ADMIN" },
                TunDevice = true,
                DevicePaths = new[] { "/dev/ttyUSB0" },
                DbusHostPath = "/var/run/dbus",
            },
            volumes: new[]
            {
                new ResolvedVolume
                {
                    Id = "data", MountPath = "/data", Size = "64Mi",
                    Criticality = VolumeCriticality.Critical,
                    ResourceName = "reeve-wl-com-reeve-matter-matter-data-11111111",
                },
            });

        var content = Renderer.Render(spec, External()).PrimaryContent;

        content.Should().Be(ArtifactGoldens.ComposeExternalSecret);
    }

    [Fact]
    public void Golden_compose_reveals_the_token_and_env_stays_out_of_kubernetes()
    {
        var content = Renderer.Render(Spec(), Reveal("rwt_goldentoken")).PrimaryContent;

        content.Should().Be(ArtifactGoldens.ComposeRevealedToken);
        content.Should().Contain("rwt_goldentoken");
    }

    [Fact]
    public void Golden_compose_host_network_with_host_port_omits_the_ports_block()
    {
        var spec = Spec(network: new ResolvedNetwork
        {
            HostNetwork = true,
            Ports = new[]
            {
                new ResolvedPort { Name = "api", Port = 8080, Protocol = "tcp", HostPort = 18080 },
            },
        });

        var content = Renderer.Render(spec, External()).PrimaryContent;

        content.Should().Be(ArtifactGoldens.ComposeHostNetworkWithHostPortOmitsPorts);
    }

    [Fact]
    public void Golden_compose_host_network_and_node_local_state_field_complete()
    {
        var spec = Spec(
            network: new ResolvedNetwork { HostNetwork = true, Mdns = true, Ipv6Required = true },
            privileges: new ResolvedPrivileges
            {
                NodeLocalState = new ResolvedNodeLocalState
                {
                    HostPath = "/var/lib/reeve/ble/state",
                    Criticality = VolumeCriticality.Critical,
                },
                DbusHostPath = "/var/run/dbus",
            },
            volumes: new[]
            {
                new ResolvedVolume
                {
                    Id = "data", MountPath = "/data", Size = "64Mi",
                    Criticality = VolumeCriticality.Critical,
                    ResourceName = "reeve-wl-com-reeve-matter-matter-data-11111111",
                },
            });

        var content = Renderer.Render(spec, External()).PrimaryContent;

        content.Should().Be(ArtifactGoldens.ComposeHostNetworkNodeLocalState);
    }

    [Fact]
    public void Compose_emits_group_add_for_each_resolved_device_group()
    {
        var spec = Spec(privileges: new ResolvedPrivileges
        {
            DevicePaths = new[] { "/dev/dri/renderD128" },
            DeviceGroupIds = new[] { 993, 104 },
        });

        var content = Renderer.Render(spec, External()).PrimaryContent;

        content.Should().Contain("    group_add:\n      - \"104\"\n      - \"993\"");
    }

    [Fact]
    public void Compose_adds_the_volume_group_and_tells_the_operator_the_volumes_are_theirs_to_hand_over()
    {
        var spec = Spec(privileges: new ResolvedPrivileges { FsGroup = 1000, DeviceGroupIds = new[] { 104 } }) with
        {
            Volumes = new[]
            {
                new ResolvedVolume
                {
                    Id = "data", MountPath = "/data", Size = "64Mi",
                    Criticality = VolumeCriticality.Cache, ResourceName = "reeve-wl-pvc-data",
                },
            },
        };

        var result = Renderer.Render(spec, External());

        result.PrimaryContent.Should().Contain("    group_add:\n      - \"104\"\n      - \"1000\"");
        result.Notes.Should().Contain(n => n.Contains("1000") && n.Contains("setgid"));
    }

    [Fact]
    public void Compose_emits_no_group_add_without_a_device_group()
    {
        var spec = Spec(privileges: new ResolvedPrivileges { DevicePaths = new[] { "/dev/ttyUSB0" } });

        Renderer.Render(spec, External()).PrimaryContent.Should().NotContain("group_add");
    }
}
