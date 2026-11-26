using System;
using System.Collections.Generic;
using System.Linq;
using Docker.DotNet.Models;
using FluentAssertions;
using Reeve.Workloads.Abstractions.Contracts;
using Reeve.Workloads.Abstractions.Contracts.Deployment;
using Reeve.Workloads.Abstractions.Domain.DesiredSpec;
using Reeve.Workloads.Providers.Docker;
using Xunit;

namespace Reeve.Workloads.Providers.Docker.Tests;

public sealed class DockerPrivilegeRenderTests
{
    private static readonly IDockerPrivilegeRenderer Renderer = new DockerPrivilegeRenderer();

    private static WorkloadDesiredSpec Spec(ResolvedPrivileges? privileges = null)
    {
        var instanceId = Guid.Parse("11111111-2222-3333-4444-555555555555");
        return new WorkloadDesiredSpec
        {
            GroupInstanceId = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee"),
            GroupId = "com.reeve.matter",
            WorkloadId = "matter",
            InstanceId = instanceId,
            Inst8 = "11111111",
            Provider = DockerProvider.Id,
            ResourceName = "reeve-wl-com-reeve-matter-matter-11111111",
            SecretName = "reeve-wl-com-reeve-matter-matter-11111111",
            Labels = new Dictionary<string, string>(),
            Image = "ghcr.io/reeve/matter@sha256:1111111111111111111111111111111111111111111111111111111111111111",
            Kind = WorkloadKind.SingleNode,
            Privileges = privileges,
            Health = new WorkloadHealth { Mode = WorkloadHealthMode.Container },
            SpecHash = "abc123def456",
        };
    }

    [Fact]
    public void Full_privilege_spec_renders_every_consented_field_and_nothing_extra()
    {
        var priv = new ResolvedPrivileges
        {
            Capabilities = new[] { "NET_ADMIN", "NET_RAW" },
            Privileged = true,
            RunAsRoot = true,
            TunDevice = true,
            DbusHostPath = "/var/run/dbus",
            DevicePaths = new[] { "/dev/ttyUSB0", "/dev/ttyACM0" },
            NodeLocalState = new ResolvedNodeLocalState
            {
                HostPath = "/var/lib/reeve/state/com.reeve.matter/matter",
                Criticality = VolumeCriticality.Critical,
            },
        };

        var r = Renderer.RenderPrivileges(Spec(priv));

        r.HostConfig.CapDrop.Should().BeEquivalentTo(new[] { "ALL" });
        r.HostConfig.CapAdd.Should().BeEquivalentTo(new[] { "NET_ADMIN", "NET_RAW" });

        r.HostConfig.Privileged.Should().BeTrue();
        r.User.Should().Be("0");

        r.HostConfig.Binds.Should().BeEquivalentTo(new[]
        {
            "/var/run/dbus:/var/run/dbus:ro",
            "/var/lib/reeve/state/com.reeve.matter/matter:/var/lib/reeve/state/com.reeve.matter/matter",
        });

        r.HostConfig.Devices.Should().HaveCount(3);
        r.HostConfig.Devices.Select(d => d.PathOnHost).Should()
            .BeEquivalentTo(new[] { "/dev/net/tun", "/dev/ttyUSB0", "/dev/ttyACM0" });
        r.HostConfig.Devices.Should().OnlyContain(d => d.PathInContainer == d.PathOnHost);
    }

    [Fact]
    public void Null_privilege_block_renders_minimal_capdrop_all_only()
    {
        var r = Renderer.RenderPrivileges(Spec(privileges: null));

        r.HostConfig.CapDrop.Should().BeEquivalentTo(new[] { "ALL" });
        r.HostConfig.CapAdd.Should().BeNullOrEmpty();
        r.HostConfig.Privileged.Should().BeFalse();
        r.User.Should().BeNull();
        r.HostConfig.Binds.Should().BeNullOrEmpty();
        r.HostConfig.Devices.Should().BeNullOrEmpty();
    }

    [Fact]
    public void Empty_privilege_block_renders_minimal_capdrop_all_only()
    {
        var r = Renderer.RenderPrivileges(Spec(new ResolvedPrivileges()));

        r.HostConfig.CapDrop.Should().BeEquivalentTo(new[] { "ALL" });
        r.HostConfig.CapAdd.Should().BeNullOrEmpty();
        r.HostConfig.Privileged.Should().BeFalse();
        r.User.Should().BeNull();
        r.HostConfig.Binds.Should().BeNullOrEmpty();
        r.HostConfig.Devices.Should().BeNullOrEmpty();
    }

    [Fact]
    public void RunAsRoot_true_pins_user_to_root_uid_zero()
    {
        var r = Renderer.RenderPrivileges(Spec(new ResolvedPrivileges { RunAsRoot = true }));

        r.User.Should().Be("0");
    }

    [Fact]
    public void RunAsRoot_false_leaves_user_null_image_default()
    {
        var r = Renderer.RenderPrivileges(Spec(new ResolvedPrivileges { RunAsRoot = false }));

        r.User.Should().BeNull();
    }

    [Fact]
    public void Dbus_host_path_becomes_a_read_only_bind_and_nothing_else()
    {
        var r = Renderer.RenderPrivileges(Spec(new ResolvedPrivileges { DbusHostPath = "/var/run/dbus" }));

        r.HostConfig.Binds.Should().ContainSingle()
            .Which.Should().Be("/var/run/dbus:/var/run/dbus:ro");
        r.HostConfig.Devices.Should().BeNullOrEmpty();
    }

    [Fact]
    public void Tun_device_becomes_the_dev_net_tun_device_with_rwm()
    {
        var r = Renderer.RenderPrivileges(Spec(new ResolvedPrivileges { TunDevice = true }));

        var device = r.HostConfig.Devices.Should().ContainSingle().Subject;
        device.PathOnHost.Should().Be("/dev/net/tun");
        device.PathInContainer.Should().Be("/dev/net/tun");
        device.CgroupPermissions.Should().Be("rwm");
        r.HostConfig.Binds.Should().BeNullOrEmpty();
    }

    [Fact]
    public void Device_paths_each_become_a_passthrough_device_with_rwm()
    {
        var r = Renderer.RenderPrivileges(Spec(new ResolvedPrivileges
        {
            DevicePaths = new[] { "/dev/ttyUSB0", "/dev/ttyACM0" },
        }));

        r.HostConfig.Devices.Should().HaveCount(2);
        r.HostConfig.Devices.Select(d => d.PathOnHost).Should()
            .BeEquivalentTo(new[] { "/dev/ttyUSB0", "/dev/ttyACM0" });
        r.HostConfig.Devices.Should().OnlyContain(d =>
            d.PathInContainer == d.PathOnHost && d.CgroupPermissions == "rwm");
        r.HostConfig.Binds.Should().BeNullOrEmpty();
    }

    [Fact]
    public void Host_path_becomes_a_read_only_bind_at_its_container_mount_path()
    {
        var r = Renderer.RenderPrivileges(Spec(new ResolvedPrivileges
        {
            HostPaths = new[] { new ResolvedHostPath { HostPath = "/sys/bus/usb", MountPath = "/host/sys/bus/usb" } },
        }));

        r.HostConfig.Binds.Should().ContainSingle()
            .Which.Should().Be("/sys/bus/usb:/host/sys/bus/usb:ro");
        r.HostConfig.Devices.Should().BeNullOrEmpty();
    }

    [Fact]
    public void Node_local_state_becomes_a_read_write_bind_of_the_curated_host_path()
    {
        var hostPath = "/var/lib/reeve/state/com.reeve.matter/matter";
        var r = Renderer.RenderPrivileges(Spec(new ResolvedPrivileges
        {
            NodeLocalState = new ResolvedNodeLocalState
            {
                HostPath = hostPath,
                Criticality = VolumeCriticality.Critical,
            },
        }));

        r.HostConfig.Binds.Should().ContainSingle()
            .Which.Should().Be($"{hostPath}:{hostPath}");
        r.HostConfig.Devices.Should().BeNullOrEmpty();
    }

    [Fact]
    public void GroupAdd_carries_each_resolved_device_group_id_as_a_string()
    {
        var r = Renderer.RenderPrivileges(Spec(new ResolvedPrivileges
        {
            DevicePaths = new[] { "/dev/dri/renderD128" },
            DeviceGroupIds = new[] { 993, 104 },
        }));

        r.HostConfig.GroupAdd.Should().Equal("104", "993");
    }

    [Fact]
    public void GroupAdd_joins_the_volume_group_to_the_device_groups_once_and_sorted()
    {
        var r = Renderer.RenderPrivileges(Spec(new ResolvedPrivileges { FsGroup = 1000, DeviceGroupIds = new[] { 993, 104 } }));
        r.HostConfig.GroupAdd.Should().Equal("104", "993", "1000");

        Renderer.RenderPrivileges(Spec(new ResolvedPrivileges { FsGroup = 104, DeviceGroupIds = new[] { 104 } }))
            .HostConfig.GroupAdd.Should().Equal("104");
    }

    [Fact]
    public void A_volume_group_alone_adds_the_group_and_leaves_the_user_to_the_image()
    {
        var r = Renderer.RenderPrivileges(Spec(new ResolvedPrivileges { FsGroup = 1000 }));

        r.HostConfig.GroupAdd.Should().Equal("1000");
        r.User.Should().BeNull("the volume group is a group, not an identity; the image's USER still decides who runs");
        r.HostConfig.CapDrop.Should().Equal("ALL");
    }

    [Fact]
    public void GroupAdd_is_null_when_no_device_group_was_resolved()
    {
        var r = Renderer.RenderPrivileges(Spec(new ResolvedPrivileges
        {
            DevicePaths = new[] { "/dev/ttyUSB0" },
        }));

        r.HostConfig.GroupAdd.Should().BeNull();
        r.HostConfig.Devices.Should().ContainSingle().Which.PathOnHost.Should().Be("/dev/ttyUSB0");
    }

    [Fact]
    public void GroupAdd_is_null_for_a_workload_with_no_privilege_block()
    {
        var r = Renderer.RenderPrivileges(Spec(privileges: null));

        r.HostConfig.GroupAdd.Should().BeNull();
    }

    [Fact]
    public void CapAdd_set_equals_the_consented_capabilities_no_more_no_less()
    {
        var caps = new[] { "SYS_ADMIN", "NET_RAW", "NET_ADMIN" };
        var r = Renderer.RenderPrivileges(Spec(new ResolvedPrivileges { Capabilities = caps }));

        r.HostConfig.CapAdd.Should().BeEquivalentTo(caps);
    }

    [Fact]
    public void Capabilities_only_emits_no_binds_and_no_devices_no_over_privilege()
    {
        var r = Renderer.RenderPrivileges(Spec(new ResolvedPrivileges
        {
            Capabilities = new[] { "NET_ADMIN" },
        }));

        r.HostConfig.CapAdd.Should().BeEquivalentTo(new[] { "NET_ADMIN" });
        r.HostConfig.Privileged.Should().BeFalse();
        r.User.Should().BeNull();
        r.HostConfig.Binds.Should().BeNullOrEmpty();
        r.HostConfig.Devices.Should().BeNullOrEmpty();
    }

    [Fact]
    public void Privileged_flag_mirrors_the_consented_value()
    {
        Renderer.RenderPrivileges(Spec(new ResolvedPrivileges { Privileged = true }))
            .HostConfig.Privileged.Should().BeTrue();
        Renderer.RenderPrivileges(Spec(new ResolvedPrivileges { Privileged = false }))
            .HostConfig.Privileged.Should().BeFalse();
    }

    [Fact]
    public void Same_spec_renders_an_equivalent_fragment_deterministic()
    {
        var priv = new ResolvedPrivileges
        {
            Capabilities = new[] { "NET_ADMIN" },
            DbusHostPath = "/var/run/dbus",
            TunDevice = true,
            DevicePaths = new[] { "/dev/ttyUSB0" },
            NodeLocalState = new ResolvedNodeLocalState
            {
                HostPath = "/var/lib/reeve/state/x",
                Criticality = VolumeCriticality.Recoverable,
            },
        };

        var a = Renderer.RenderPrivileges(Spec(priv));
        var b = Renderer.RenderPrivileges(Spec(priv));

        a.Should().BeEquivalentTo(b);
    }

    [Fact]
    public void The_create_body_carries_every_privilege_field_the_renderer_set()
    {
        var spec = Spec(new ResolvedPrivileges
        {
            Capabilities = new[] { "NET_ADMIN" },
            Privileged = true,
            RunAsRoot = true,
            DbusHostPath = "/var/run/dbus",
            DevicePaths = new[] { "/dev/ttyUSB0" },
            DeviceGroupIds = new[] { 993 },
            FsGroup = 1000,
        });
        var privilege = Renderer.RenderPrivileges(spec);

        var body = new DockerContainerManager().BuildContainerParameters(new DockerContainerRenderContext
        {
            Spec = spec,
            ContainerName = "c",
            SharedNetworkName = "reeve",
            Privilege = privilege,
            Resource = new DockerResourceRenderer().RenderResources(spec),
            NetworkHostConfig = new HostConfig(),
        });

        body.HostConfig.GroupAdd.Should().Equal("993", "1000");
        body.HostConfig.CapDrop.Should().Equal(privilege.HostConfig.CapDrop);
        body.HostConfig.CapAdd.Should().Equal(privilege.HostConfig.CapAdd);
        body.HostConfig.Privileged.Should().BeTrue();
        body.HostConfig.Binds.Should().Equal(privilege.HostConfig.Binds);
        body.HostConfig.Devices.Should().BeEquivalentTo(privilege.HostConfig.Devices);
        body.User.Should().Be("0");
    }
}
