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

public sealed class WorkloadParameterResolverTests
{
    private static WorkloadManifest Workload(
        IReadOnlyList<WorkloadParameter>? parameters = null,
        WorkloadPrivileges? privileges = null,
        WorkloadNetwork? network = null,
        IReadOnlyList<WorkloadVolume>? volumes = null)
        => new()
        {
            Id = "w",
            DisplayName = "W",
            Image = new WorkloadImage { Ref = "reg/img", Digest = "sha256:" + new string('a', 64) },
            Kind = WorkloadKind.SingleNode,
            Privileges = privileges,
            Network = network,
            Volumes = volumes ?? Array.Empty<WorkloadVolume>(),
            Parameters = parameters ?? Array.Empty<WorkloadParameter>(),
            Health = new WorkloadHealth { Mode = WorkloadHealthMode.Container },
        };

    private static Dictionary<string, string?> Values(params (string k, string? v)[] pairs)
        => pairs.ToDictionary(p => p.k, p => p.v, StringComparer.Ordinal);

    [Theory]
    [InlineData(null)]
    [InlineData("hostNetwork")]
    [InlineData("cluster")]
    public void User_namespace_with_any_resolved_network_mode_resolves_for_the_factory_to_judge(string? mode)
    {
        var wl = Workload(network: new WorkloadNetwork { HostNetwork = true });
        var values = Values(("security.userNamespace", "true"));
        if (mode is not null)
            values["network.mode"] = mode;
        var resolved = WorkloadParameterResolver.Resolve(wl, values, StandInProviders.Kubernetes);

        resolved["security.userNamespace"].Should().Be("true");
    }

    [Fact]
    public void User_namespace_is_synthesized_only_for_a_provider_with_the_trait()
    {
        WorkloadParameterResolver.Resolve(Workload(), Values(), StandInProviders.Kubernetes)
            .Should().ContainKey("security.userNamespace").WhoseValue.Should().BeNull();
        WorkloadParameterResolver.Resolve(Workload(), Values(), StandInProviders.Docker)
            .Should().NotContainKey("security.userNamespace");
        WorkloadParameterResolver.Resolve(Workload(), Values(), StandInProviders.Manual)
            .Should().NotContainKey("security.userNamespace");
    }

    [Fact]
    public void User_namespace_redeclaration_on_Docker_is_not_judged_as_a_reserved_boolean()
    {
        var workload = Workload(parameters: [new WorkloadParameter
            { Name = "security.userNamespace", Type = WorkloadParameterType.Boolean, Hint = "author hint" }]);
        WorkloadParameterResolver.Resolve(workload, Values(("security.userNamespace", "yes")), StandInProviders.Docker)
            ["security.userNamespace"].Should().Be("yes");
    }

    [Fact]
    public void User_namespace_accepts_boolean_values_and_rejects_other_values_on_kubernetes()
    {
        foreach (var value in new[] { "true", "TRUE", " true ", "false" })
            WorkloadParameterResolver.Resolve(Workload(), Values(("security.userNamespace", value)), StandInProviders.Kubernetes)
                ["security.userNamespace"].Should().Be(value);

        var act = () => WorkloadParameterResolver.Resolve(
            Workload(), Values(("security.userNamespace", "yes")), StandInProviders.Kubernetes);
        act.Should().Throw<WorkloadParameterResolutionException>().Which.ParameterName.Should().Be("security.userNamespace");
    }

    [Fact]
    public void WithoutUnsupportedUserNamespace_drops_values_only_when_provider_cannot_render_and_name_is_reserved()
    {
        var submitted = Values(("security.userNamespace", "true"), ("other", "x"));
        WorkloadParameterResolver.WithoutUnsupportedUserNamespace(submitted, Workload(), StandInProviders.Docker)
            .Should().NotContainKey("security.userNamespace").And.ContainKey("other");
        WorkloadParameterResolver.WithoutUnsupportedUserNamespace(submitted, Workload(), StandInProviders.Kubernetes)
            .Should().BeSameAs(submitted);

        var declared = Workload(parameters: [new WorkloadParameter
            { Name = "security.userNamespace", Type = WorkloadParameterType.Boolean, Hint = "h" }]);
        WorkloadParameterResolver.WithoutUnsupportedUserNamespace(submitted, declared, StandInProviders.Docker)
            .Should().BeSameAs(submitted);
    }

    [Fact]
    public void Declared_parameter_uses_its_default_when_operator_supplies_nothing()
    {
        var wl = Workload(parameters: new[]
        {
            new WorkloadParameter { Name = "adapterName", Type = WorkloadParameterType.Text, Default = "hci0" },
        });

        var resolved = WorkloadParameterResolver.Resolve(wl, Values(), StandInProviders.Kubernetes);

        resolved["adapterName"].Should().Be("hci0");
    }

    [Fact]
    public void Operator_value_overrides_the_declared_default()
    {
        var wl = Workload(parameters: new[]
        {
            new WorkloadParameter { Name = "adapterName", Type = WorkloadParameterType.Text, Default = "hci0" },
        });

        var resolved = WorkloadParameterResolver.Resolve(wl, Values(("adapterName", "hci1")), StandInProviders.Kubernetes);

        resolved["adapterName"].Should().Be("hci1");
    }

    [Fact]
    public void Blank_operator_value_falls_back_to_the_declared_default()
    {
        var wl = Workload(parameters: new[]
        {
            new WorkloadParameter { Name = "minRssiDbm", Type = WorkloadParameterType.Integer, Default = "-100" },
            new WorkloadParameter { Name = "offlineTimeoutMinutes", Type = WorkloadParameterType.Integer, Default = "10" },
        });

        var resolved = WorkloadParameterResolver.Resolve(
            wl, Values(("minRssiDbm", ""), ("offlineTimeoutMinutes", "   ")), StandInProviders.Kubernetes);

        resolved["minRssiDbm"].Should().Be("-100");
        resolved["offlineTimeoutMinutes"].Should().Be("10");
    }

    [Fact]
    public void Declared_optional_parameter_without_default_or_value_resolves_null()
    {
        var wl = Workload(parameters: new[]
        {
            new WorkloadParameter { Name = "minRssiDbm", Type = WorkloadParameterType.Integer },
        });

        var resolved = WorkloadParameterResolver.Resolve(wl, Values(), StandInProviders.Kubernetes);

        resolved.Should().ContainKey("minRssiDbm");
        resolved["minRssiDbm"].Should().BeNull();
    }

    [Fact]
    public void DbusHostPath_is_synthesized_with_the_spec_default_when_hostDbus_and_not_declared()
    {
        var wl = Workload(privileges: new WorkloadPrivileges { HostDbus = true });

        var resolved = WorkloadParameterResolver.Resolve(wl, Values(), StandInProviders.Kubernetes);

        resolved[WorkloadPrivilegeRequirements.DbusHostPathParameter].Should().Be(WorkloadPrivilegeRequirements.DefaultDbusHostPath);
    }

    [Fact]
    public void DbusHostPath_is_not_synthesized_when_hostDbus_is_off()
    {
        var wl = Workload(privileges: new WorkloadPrivileges { HostDbus = false });

        var resolved = WorkloadParameterResolver.Resolve(wl, Values(), StandInProviders.Kubernetes);

        resolved.Should().NotContainKey(WorkloadPrivilegeRequirements.DbusHostPathParameter);
    }

    [Fact]
    public void Author_declared_dbusHostPath_default_overrides_the_synthesized_default()
    {
        var wl = Workload(
            privileges: new WorkloadPrivileges { HostDbus = true },
            parameters: new[]
            {
                new WorkloadParameter { Name = "dbusHostPath", Type = WorkloadParameterType.Text, Default = "/run/dbus" },
            });

        var resolved = WorkloadParameterResolver.Resolve(wl, Values(), StandInProviders.Kubernetes);

        resolved["dbusHostPath"].Should().Be("/run/dbus");
    }

    [Fact]
    public void Resources_override_group_is_always_available_and_operator_settable()
    {
        var wl = Workload();

        var resolved = WorkloadParameterResolver.Resolve(
            wl, Values(("resources.limits.memory", "512Mi")), StandInProviders.Kubernetes);

        resolved.Should().ContainKey("resources.requests.cpu");
        resolved.Should().ContainKey("resources.requests.memory");
        resolved.Should().ContainKey("resources.limits.cpu");
        resolved["resources.limits.memory"].Should().Be("512Mi");
    }

    [Fact]
    public void Port_hostPort_is_synthesized_only_for_docker()
    {
        var wl = Workload(network: new WorkloadNetwork
        {
            Ports = new[] { new WorkloadPort { Name = "api", Port = 8080, Protocol = "tcp" } },
        });

        var k8s = WorkloadParameterResolver.Resolve(wl, Values(), StandInProviders.Kubernetes);
        var docker = WorkloadParameterResolver.Resolve(wl, Values(), StandInProviders.Docker);

        k8s.Should().NotContainKey("port.api.hostPort");
        docker.Should().ContainKey("port.api.hostPort");
    }

    [Fact]
    public void ExistingClaim_is_synthesized_per_declared_volume()
    {
        var wl = Workload(volumes: new[]
        {
            new WorkloadVolume { Id = "bindkeys", MountPath = "/data", Size = "64Mi", Criticality = VolumeCriticality.Critical },
        });

        var resolved = WorkloadParameterResolver.Resolve(
            wl, Values(("existingClaim.bindkeys", "reeve-wl-x-bindkeys-abc")), StandInProviders.Kubernetes);

        resolved["existingClaim.bindkeys"].Should().Be("reeve-wl-x-bindkeys-abc");
    }

    [Fact]
    public void VolumeSize_override_is_re_synthesized_per_declared_volume_and_survives_re_resolution()
    {
        var wl = Workload(volumes: new[]
        {
            new WorkloadVolume { Id = "state", MountPath = "/data", Size = "512Mi", Criticality = VolumeCriticality.Critical },
        });

        var priorResolved = Values(
            ($"{WorkloadDesiredSpecFactory.VolumeSizeOverrideParameterPrefix}state", "8Gi"));

        var resolved = WorkloadParameterResolver.Resolve(wl, priorResolved, StandInProviders.Kubernetes);

        resolved.Should().ContainKey($"{WorkloadDesiredSpecFactory.VolumeSizeOverrideParameterPrefix}state");
        resolved[$"{WorkloadDesiredSpecFactory.VolumeSizeOverrideParameterPrefix}state"]
            .Should().Be("8Gi", "the reserved resize override must survive an update re-resolution, not be stripped");
    }

    [Fact]
    public void VolumeSize_is_only_synthesized_for_DECLARED_volume_ids_never_arbitrary_keys()
    {
        var wl = Workload(volumes: new[]
        {
            new WorkloadVolume { Id = "state", MountPath = "/data", Size = "512Mi", Criticality = VolumeCriticality.Critical },
        });

        var resolved = WorkloadParameterResolver.Resolve(
            wl,
            Values(($"{WorkloadDesiredSpecFactory.VolumeSizeOverrideParameterPrefix}ghost", "9Gi")),
            StandInProviders.Kubernetes);

        resolved.Should().NotContainKey($"{WorkloadDesiredSpecFactory.VolumeSizeOverrideParameterPrefix}ghost");
        resolved.Should().ContainKey($"{WorkloadDesiredSpecFactory.VolumeSizeOverrideParameterPrefix}state");
    }

    [Fact]
    public void A_device_path_outside_dev_is_rejected()
    {
        var wl = Workload(
            privileges: new WorkloadPrivileges { Devices = new[] { new WorkloadDevice { Parameter = "serialPort" } } },
            parameters: new[] { new WorkloadParameter { Name = "serialPort", Type = WorkloadParameterType.DevicePath } });

        var act = () => WorkloadParameterResolver.Resolve(
            wl, Values(("serialPort", "/etc/passwd")), StandInProviders.Kubernetes);

        act.Should().Throw<WorkloadParameterResolutionException>()
            .Which.ParameterName.Should().Be("serialPort");
    }

    [Fact]
    public void A_device_path_under_dev_is_accepted()
    {
        var wl = Workload(
            privileges: new WorkloadPrivileges { Devices = new[] { new WorkloadDevice { Parameter = "serialPort" } } },
            parameters: new[] { new WorkloadParameter { Name = "serialPort", Type = WorkloadParameterType.DevicePath } });

        var resolved = WorkloadParameterResolver.Resolve(
            wl, Values(("serialPort", "/dev/ttyUSB0")), StandInProviders.Kubernetes);

        resolved["serialPort"].Should().Be("/dev/ttyUSB0");
    }

    [Theory]
    [InlineData("/dev/serial")]
    [InlineData("/dev/serial/by-id")]
    [InlineData("/sys/bus/usb")]
    public void A_hostPath_value_under_the_allowlist_or_dev_serial_is_accepted(string value)
    {
        var wl = Workload(
            privileges: new WorkloadPrivileges { HostPaths = new[] { new WorkloadHostPath { Parameter = "serialDir", MountPath = "/dev/serial" } } },
            parameters: new[] { new WorkloadParameter { Name = "serialDir", Type = WorkloadParameterType.HostPath } });

        var resolved = WorkloadParameterResolver.Resolve(
            wl, Values(("serialDir", value)), StandInProviders.Kubernetes);

        resolved["serialDir"].Should().Be(value);
    }

    [Theory]
    [InlineData("/dev")]
    [InlineData("/dev/mem")]
    [InlineData("/dev/sda")]
    [InlineData("/dev/serialx")]
    [InlineData("/dev/serial/../mem")]
    [InlineData("/etc/passwd")]
    [InlineData("/var/run/docker.sock")]
    [InlineData("/run/containerd/containerd.sock")]
    [InlineData("/run/k3s/containerd")]
    [InlineData("/proc/1/root")]
    [InlineData("/proc/self/cwd")]
    public void A_hostPath_value_outside_the_allowlist_and_not_dev_serial_is_rejected(string value)
    {
        var wl = Workload(
            privileges: new WorkloadPrivileges { HostPaths = new[] { new WorkloadHostPath { Parameter = "serialDir", MountPath = "/dev/serial" } } },
            parameters: new[] { new WorkloadParameter { Name = "serialDir", Type = WorkloadParameterType.HostPath } });

        var act = () => WorkloadParameterResolver.Resolve(
            wl, Values(("serialDir", value)), StandInProviders.Kubernetes);

        act.Should().Throw<WorkloadParameterResolutionException>()
            .Which.ParameterName.Should().Be("serialDir");
    }

    [Theory]
    [InlineData("/dev/dri")]
    [InlineData("/dev/dri/by-path")]
    public void Dri_tree_is_an_allowed_host_inspect_mount(string value)
    {
        var wl = Workload(
            privileges: new WorkloadPrivileges { HostPaths = new[] { new WorkloadHostPath { Parameter = "renderDir", MountPath = "/dev/dri" } } },
            parameters: new[] { new WorkloadParameter { Name = "renderDir", Type = WorkloadParameterType.HostPath } });

        var resolved = WorkloadParameterResolver.Resolve(
            wl, Values(("renderDir", value)), StandInProviders.Kubernetes);

        resolved["renderDir"].Should().Be(value);
    }

    [Theory]
    [InlineData("/dev")]
    [InlineData("/dev/mem")]
    [InlineData("/dev/drilling")]
    [InlineData("/dev/dri/../mem")]
    public void No_other_dev_path_is_an_allowed_host_inspect_mount(string value)
    {
        var wl = Workload(
            privileges: new WorkloadPrivileges { HostPaths = new[] { new WorkloadHostPath { Parameter = "renderDir", MountPath = "/dev/dri" } } },
            parameters: new[] { new WorkloadParameter { Name = "renderDir", Type = WorkloadParameterType.HostPath } });

        var act = () => WorkloadParameterResolver.Resolve(
            wl, Values(("renderDir", value)), StandInProviders.Kubernetes);

        act.Should().Throw<WorkloadParameterResolutionException>()
            .Which.ParameterName.Should().Be("renderDir");
    }

    [Theory]
    [InlineData("/etc/dbus")]
    [InlineData("/run/docker.sock")]
    [InlineData("/var/run/containerd")]
    public void A_dbus_path_outside_run_is_rejected(string value)
    {
        var wl = Workload(privileges: new WorkloadPrivileges { HostDbus = true });

        var act = () => WorkloadParameterResolver.Resolve(
            wl, Values(("dbusHostPath", value)), StandInProviders.Kubernetes);

        act.Should().Throw<WorkloadParameterResolutionException>()
            .Which.ParameterName.Should().Be("dbusHostPath");
    }

    [Fact]
    public void A_dbus_path_using_traversal_is_rejected()
    {
        var wl = Workload(privileges: new WorkloadPrivileges { HostDbus = true });

        var act = () => WorkloadParameterResolver.Resolve(
            wl, Values(("dbusHostPath", "/run/../etc")), StandInProviders.Kubernetes);

        act.Should().Throw<WorkloadParameterResolutionException>();
    }

    [Fact]
    public void The_default_dbus_path_satisfies_the_var_run_prefix()
    {
        var wl = Workload(privileges: new WorkloadPrivileges { HostDbus = true });

        var act = () => WorkloadParameterResolver.Resolve(wl, Values(), StandInProviders.Kubernetes);

        act.Should().NotThrow();
    }

    [Fact]
    public void MaxCount_greater_than_one_synthesizes_indexed_keys()
    {
        var wl = Workload(
            parameters: new[] { new WorkloadParameter { Name = "acc", Type = WorkloadParameterType.DevicePath } },
            privileges: new WorkloadPrivileges
            {
                Devices = new[] { new WorkloadDevice { Parameter = "acc", MaxCount = 3 } },
            });

        var resolved = WorkloadParameterResolver.Resolve(
            wl, Values(("acc[0]", "/dev/dri/renderD128"), ("acc[1]", "/dev/dri/renderD129")), StandInProviders.Kubernetes);

        resolved["acc[0]"].Should().Be("/dev/dri/renderD128");
        resolved["acc[1]"].Should().Be("/dev/dri/renderD129");
        resolved["acc[2]"].Should().BeNull();
    }

    [Fact]
    public void MaxCount_of_one_keeps_the_unindexed_key()
    {
        var wl = Workload(
            parameters: new[] { new WorkloadParameter { Name = "stick", Type = WorkloadParameterType.DevicePath } },
            privileges: new WorkloadPrivileges
            {
                Devices = new[] { new WorkloadDevice { Parameter = "stick" } },
            });

        var resolved = WorkloadParameterResolver.Resolve(
            wl, Values(("stick", "/dev/serial/by-id/usb-zwave")), StandInProviders.Kubernetes);

        resolved["stick"].Should().Be("/dev/serial/by-id/usb-zwave");
        resolved.Keys.Should().NotContain(k => k.Contains('['));
    }

    [Fact]
    public void MaxCount_declared_default_lands_only_on_index_zero()
    {
        var wl = Workload(
            parameters: new[]
            {
                new WorkloadParameter { Name = "acc", Type = WorkloadParameterType.DevicePath, Default = "/dev/dri/renderD128" },
            },
            privileges: new WorkloadPrivileges
            {
                Devices = new[] { new WorkloadDevice { Parameter = "acc", MaxCount = 4 } },
            });

        var resolved = WorkloadParameterResolver.Resolve(wl, Values(), StandInProviders.Kubernetes);

        resolved["acc[0]"].Should().Be("/dev/dri/renderD128");
        resolved["acc[1]"].Should().BeNull();
        resolved["acc[2]"].Should().BeNull();
        resolved["acc[3]"].Should().BeNull();
    }

    [Fact]
    public void MaxCount_greater_than_one_keeps_the_declared_key_because_it_is_the_operator_input_accept_list()
    {
        var wl = Workload(
            parameters: new[]
            {
                new WorkloadParameter { Name = "acc", Type = WorkloadParameterType.DevicePath, Default = "/dev/dri/renderD128" },
            },
            privileges: new WorkloadPrivileges
            {
                Devices = new[] { new WorkloadDevice { Parameter = "acc", MaxCount = 4 } },
            });

        var resolved = WorkloadParameterResolver.Resolve(wl, Values(), StandInProviders.Kubernetes);

        resolved.Should().ContainKey("acc");
        resolved["acc"].Should().Be("/dev/dri/renderD128");
    }

    [Fact]
    public void MaxCount_greater_than_one_keeps_an_operator_supplied_declared_key_alongside_the_indexed_keys()
    {
        var wl = Workload(
            parameters: new[] { new WorkloadParameter { Name = "acc", Type = WorkloadParameterType.DevicePath } },
            privileges: new WorkloadPrivileges
            {
                Devices = new[] { new WorkloadDevice { Parameter = "acc", MaxCount = 3 } },
            });

        var resolved = WorkloadParameterResolver.Resolve(
            wl, Values(("acc", "/dev/dri/renderD127"), ("acc[0]", "/dev/dri/renderD128")), StandInProviders.Kubernetes);

        resolved["acc"].Should().Be("/dev/dri/renderD127");
        resolved["acc[0]"].Should().Be("/dev/dri/renderD128");
    }

    [Fact]
    public void MaxCount_does_not_synthesize_keys_beyond_the_declared_bound()
    {
        var wl = Workload(
            parameters: new[] { new WorkloadParameter { Name = "acc", Type = WorkloadParameterType.DevicePath } },
            privileges: new WorkloadPrivileges
            {
                Devices = new[] { new WorkloadDevice { Parameter = "acc", MaxCount = 3 } },
            });

        var resolved = WorkloadParameterResolver.Resolve(wl, Values(), StandInProviders.Kubernetes);

        resolved.Keys.Should().BeEquivalentTo(new[] { "acc", "acc[0]", "acc[1]", "acc[2]",
            "resources.requests.cpu", "resources.requests.memory", "resources.limits.cpu", "resources.limits.memory",
            "nodeSelector", "security.userNamespace" });
    }

    [Fact]
    public void A_path_outside_dev_is_rejected_at_a_NON_zero_index()
    {
        var wl = Workload(
            parameters: new[] { new WorkloadParameter { Name = "acc", Type = WorkloadParameterType.DevicePath } },
            privileges: new WorkloadPrivileges
            {
                Devices = new[] { new WorkloadDevice { Parameter = "acc", MaxCount = 3 } },
            });

        var act = () => WorkloadParameterResolver.Resolve(
            wl, Values(("acc[0]", "/dev/dri/renderD128"), ("acc[2]", "/etc/shadow")), StandInProviders.Kubernetes);

        act.Should().Throw<WorkloadParameterResolutionException>()
            .Which.Should().Match<WorkloadParameterResolutionException>(
                e => e.ParameterName == "acc[2]" && e.Message.Contains("must be under /dev/**"));
    }

    [Fact]
    public void A_path_outside_dev_is_rejected_at_index_zero()
    {
        var wl = Workload(
            parameters: new[] { new WorkloadParameter { Name = "acc", Type = WorkloadParameterType.DevicePath } },
            privileges: new WorkloadPrivileges
            {
                Devices = new[] { new WorkloadDevice { Parameter = "acc", MaxCount = 3 } },
            });

        var act = () => WorkloadParameterResolver.Resolve(
            wl,
            Values(("acc[0]", "/etc/shadow"), ("acc[1]", "/dev/dri/renderD129"), ("acc[2]", "/dev/dri/renderD130")),
            StandInProviders.Kubernetes);

        act.Should().Throw<WorkloadParameterResolutionException>()
            .Which.Should().Match<WorkloadParameterResolutionException>(
                e => e.ParameterName == "acc[0]" && e.Message.Contains("must be under /dev/**"));
    }

    [Fact]
    public void The_declared_key_is_still_enforced_on_a_multi_device_slot()
    {
        var wl = Workload(
            parameters: new[] { new WorkloadParameter { Name = "acc", Type = WorkloadParameterType.DevicePath } },
            privileges: new WorkloadPrivileges
            {
                Devices = new[] { new WorkloadDevice { Parameter = "acc", MaxCount = 3 } },
            });

        var act = () => WorkloadParameterResolver.Resolve(
            wl,
            Values(("acc", "/etc/shadow"), ("acc[0]", "/dev/dri/renderD128"), ("acc[1]", "/dev/dri/renderD129")),
            StandInProviders.Kubernetes);

        act.Should().Throw<WorkloadParameterResolutionException>()
            .Which.Should().Match<WorkloadParameterResolutionException>(
                e => e.ParameterName == "acc" && e.Message.Contains("must be under /dev/**"));
    }

    [Fact]
    public void A_bare_dev_is_rejected_at_an_index()
    {
        var wl = Workload(
            parameters: new[] { new WorkloadParameter { Name = "acc", Type = WorkloadParameterType.DevicePath } },
            privileges: new WorkloadPrivileges
            {
                Devices = new[] { new WorkloadDevice { Parameter = "acc", MaxCount = 2 } },
            });

        var act = () => WorkloadParameterResolver.Resolve(
            wl, Values(("acc[0]", "/dev/dri/renderD128"), ("acc[1]", "/dev")), StandInProviders.Kubernetes);

        act.Should().Throw<WorkloadParameterResolutionException>()
            .Which.Should().Match<WorkloadParameterResolutionException>(
                e => e.ParameterName == "acc[1]" && e.Message.Contains("must be under /dev/**"));
    }

    [Fact]
    public void Every_index_is_checked_not_just_the_first_and_last()
    {
        var wl = Workload(
            parameters: new[] { new WorkloadParameter { Name = "acc", Type = WorkloadParameterType.DevicePath } },
            privileges: new WorkloadPrivileges
            {
                Devices = new[] { new WorkloadDevice { Parameter = "acc", MaxCount = 4 } },
            });

        var act = () => WorkloadParameterResolver.Resolve(
            wl,
            Values(
                ("acc[0]", "/dev/dri/renderD128"),
                ("acc[1]", "/etc/shadow"),
                ("acc[2]", "/dev/dri/renderD130"),
                ("acc[3]", "/dev/dri/renderD131")),
            StandInProviders.Kubernetes);

        act.Should().Throw<WorkloadParameterResolutionException>()
            .Which.Should().Match<WorkloadParameterResolutionException>(
                e => e.ParameterName == "acc[1]" && e.Message.Contains("must be under /dev/**"));
    }

    [Fact]
    public void Resolved_device_path_flows_into_the_consented_profile()
    {
        var wl = Workload(
            privileges: new WorkloadPrivileges { Devices = new[] { new WorkloadDevice { Parameter = "serialPort" } } },
            parameters: new[] { new WorkloadParameter { Name = "serialPort", Type = WorkloadParameterType.DevicePath } });

        var resolved = WorkloadParameterResolver.Resolve(
            wl, Values(("serialPort", "/dev/ttyUSB0")), StandInProviders.Kubernetes);

        var forProfile = resolved
            .Where(kv => kv.Value is not null)
            .ToDictionary(kv => kv.Key, kv => kv.Value!, StringComparer.Ordinal);
        var profile = WorkloadPrivilegeRequirements.FromWorkload(wl, forProfile);

        profile.DevicePaths.Should().BeEquivalentTo(new[] { "/dev/ttyUSB0" });
    }

    [Fact]
    public void A_single_device_slot_resolves_its_group_id_key_from_the_operator_values()
    {
        var wl = Workload(
            parameters: new[] { new WorkloadParameter { Name = "acc", Type = WorkloadParameterType.DevicePath } },
            privileges: new WorkloadPrivileges
            {
                Devices = new[] { new WorkloadDevice { Parameter = "acc", Group = WorkloadDeviceGroup.Auto } },
            });

        var resolved = WorkloadParameterResolver.Resolve(
            wl, Values(("acc", "/dev/dri/renderD128"), ("acc:gid", "993")), StandInProviders.Kubernetes);

        resolved["acc:gid"].Should().Be("993");
    }

    [Fact]
    public void A_multi_device_slot_resolves_a_group_id_key_per_index()
    {
        var wl = Workload(
            parameters: new[] { new WorkloadParameter { Name = "acc", Type = WorkloadParameterType.DevicePath } },
            privileges: new WorkloadPrivileges
            {
                Devices = new[] { new WorkloadDevice { Parameter = "acc", MaxCount = 3, Group = WorkloadDeviceGroup.Auto } },
            });

        var resolved = WorkloadParameterResolver.Resolve(
            wl, Values(("acc[1]:gid", "104")), StandInProviders.Kubernetes);

        resolved["acc[1]:gid"].Should().Be("104");
        resolved["acc[0]:gid"].Should().BeNull();
        resolved["acc[2]:gid"].Should().BeNull();
        resolved.Keys.Should().NotContain("acc:gid",
            "a multi slot binds only indexed keys — the same rule the factory and WorkloadPrivilegeRequirements apply");
    }

    [Fact]
    public void An_unsupplied_group_id_key_resolves_to_null_not_absent()
    {
        var wl = Workload(
            parameters: new[] { new WorkloadParameter { Name = "acc", Type = WorkloadParameterType.DevicePath } },
            privileges: new WorkloadPrivileges
            {
                Devices = new[] { new WorkloadDevice { Parameter = "acc", Group = WorkloadDeviceGroup.Auto } },
            });

        var resolved = WorkloadParameterResolver.Resolve(
            wl, Values(("acc", "/dev/dri/renderD128")), StandInProviders.Kubernetes);

        resolved.Should().ContainKey("acc:gid");
        resolved["acc:gid"].Should().BeNull();
    }

    [Fact]
    public void A_group_none_slot_synthesizes_no_group_id_key()
    {
        var wl = Workload(
            parameters: new[] { new WorkloadParameter { Name = "acc", Type = WorkloadParameterType.DevicePath } },
            privileges: new WorkloadPrivileges
            {
                Devices = new[] { new WorkloadDevice { Parameter = "acc", Group = WorkloadDeviceGroup.None } },
            });

        var resolved = WorkloadParameterResolver.Resolve(
            wl, Values(("acc", "/dev/dri/renderD128")), StandInProviders.Kubernetes);

        resolved.Keys.Should().NotContain(k => k.Contains(':'));
    }

    [Fact]
    public void The_group_id_key_survives_the_path_policy_enforcement()
    {
        var wl = Workload(
            parameters: new[] { new WorkloadParameter { Name = "acc", Type = WorkloadParameterType.DevicePath } },
            privileges: new WorkloadPrivileges
            {
                Devices = new[] { new WorkloadDevice { Parameter = "acc", Group = WorkloadDeviceGroup.Auto } },
            });

        var act = () => WorkloadParameterResolver.Resolve(
            wl, Values(("acc", "/dev/dri/renderD128"), ("acc:gid", "993")), StandInProviders.Kubernetes);

        act.Should().NotThrow();
        var resolved = act();
        resolved["acc"].Should().Be("/dev/dri/renderD128");
        resolved["acc:gid"].Should().Be("993", "the gid is carried through, not silently stripped by the path pass");
    }

    [Theory]
    [InlineData("0")]
    [InlineData("65536")]
    [InlineData("-1")]
    [InlineData("80x")]
    public void A_host_port_outside_1_to_65535_is_refused(string value)
    {
        var wl = Workload(network: new WorkloadNetwork { Ports = [new WorkloadPort { Name = "web", Port = 8080, Protocol = "TCP" }] });

        var act = () => WorkloadParameterResolver.Resolve(wl, Values(("port.web.hostPort", value)), StandInProviders.Docker);

        act.Should().Throw<WorkloadParameterResolutionException>().Which.ParameterName.Should().Be("port.web.hostPort");
    }

    [Fact]
    public void A_host_port_in_range_resolves()
    {
        var wl = Workload(network: new WorkloadNetwork { Ports = [new WorkloadPort { Name = "web", Port = 8080, Protocol = "TCP" }] });

        WorkloadParameterResolver.Resolve(wl, Values(("port.web.hostPort", "65535")), StandInProviders.Docker)
            ["port.web.hostPort"].Should().Be("65535");
    }

    [Theory]
    [InlineData("Backend-Config")]
    [InlineData("bad_name")]
    [InlineData("-leading")]
    public void A_ConfigMap_name_that_is_not_a_DNS_subdomain_is_refused(string value)
    {
        var wl = Workload(parameters: [new WorkloadParameter { Name = "cfg", Type = WorkloadParameterType.Text }]) with
        {
            ConfigMapMounts = [new ConfigMapMount { MountPath = "/config", Parameter = "cfg" }],
        };

        var act = () => WorkloadParameterResolver.Resolve(wl, Values(("cfg", value)), StandInProviders.Kubernetes);

        act.Should().Throw<WorkloadParameterResolutionException>().Which.ParameterName.Should().Be("cfg");
    }

    [Theory]
    [InlineData("matter-paa")]
    [InlineData("")]
    public void A_DNS_subdomain_or_blank_ConfigMap_name_resolves(string value)
    {
        var wl = Workload(parameters: [new WorkloadParameter { Name = "cfg", Type = WorkloadParameterType.Text }]) with
        {
            ConfigMapMounts = [new ConfigMapMount { MountPath = "/config", Parameter = "cfg" }],
        };

        var act = () => WorkloadParameterResolver.Resolve(wl, Values(("cfg", value)), StandInProviders.Kubernetes);

        act.Should().NotThrow();
    }
}
