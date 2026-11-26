using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using static Reeve.Workloads.Core.Tests.Rendering.ArtifactSpecFactory;
using Reeve.Workloads.Abstractions.Contracts;
using Reeve.Workloads.Abstractions.Contracts.Deployment;
using Reeve.Workloads.Abstractions.Domain;
using Reeve.Workloads.Abstractions.Domain.DesiredSpec;
using Reeve.Workloads.Abstractions.Rendering;
using Reeve.Workloads.Core.Rendering;
using Reeve.Workloads.Providers.Kubernetes.Rendering;
using Xunit;

namespace Reeve.Workloads.Core.Tests.Rendering;

public sealed class HelmValuesArtifactRendererTests
{
    private static readonly HelmValuesArtifactRenderer Renderer = new();

    private static WorkloadArtifactRenderOptions Reveal(string token = "rwt_secrettoken123") =>
        Revealing(token);

    private static WorkloadArtifactRenderOptions External() =>
        new();

    [Fact]
    public void Format_is_helm_values()
    {
        Renderer.Format.Should().Be(WorkloadArtifactFormatId.Parse("helm-values"));
        Renderer.IsSelfContained.Should().BeFalse();
        Renderer.Render(Spec(), External()).Format.Should().Be(WorkloadArtifactFormatId.Parse("helm-values"));
    }

    [Fact]
    public void Image_is_rendered_as_a_repository_and_digest_free_form_ref()
    {
        var content = Renderer.Render(Spec(), External()).PrimaryContent;

        content.Should().Contain("image:");
        content.Should().Contain($"ghcr.io/reeve/matter@{Digest}");
    }

    [Fact]
    public void Env_is_a_sorted_name_value_list_with_gateway_and_spec_hash()
    {
        var content = Renderer.Render(Spec(), External()).PrimaryContent;

        content.Should().Contain("env:");
        content.Should().Contain("- name: \"LOG_LEVEL\"");
        content.Should().Contain("value: \"info\"");
        content.Should().Contain("- name: \"REEVE_GATEWAY_URL\"");
        content.Should().Contain("- name: \"REEVE_SPEC_HASH\"");
    }

    [Fact]
    public void A_literal_env_value_is_escaped_against_the_kubelets_variable_expansion()
    {
        var spec = Spec() with
        {
            Env = [new ResolvedEnvVar { Name = "PASSWORD", Kind = ResolvedEnvKind.Literal, Value = "pa$$w$(ord)" }],
        };

        Renderer.Render(spec, External()).PrimaryContent.Should().Contain("value: \"pa$$$$w$$(ord)\"");
    }

    [Fact]
    public void A_placeholder_comes_with_the_note_to_double_dollar_signs_typed_over_it()
        => Renderer.Render(Spec(), External()).Notes.Should().Contain(n => n.Contains("write every $ in it as $$"));

    [Fact]
    public void Revealed_token_is_inlined_and_external_mode_is_a_placeholder()
    {
        Renderer.Render(Spec(), Reveal("rwt_abc")).PrimaryContent
            .Should().Contain("value: \"rwt_abc\"");
        Renderer.Render(Spec(), External()).PrimaryContent
            .Should().Contain("value: \"<set-this-secret>\"");
    }

    [Fact]
    public void Resources_render_requests_and_limits()
    {
        var content = Renderer.Render(Spec(resources: new ResolvedResources
        {
            RequestsCpu = "100m",
            RequestsMemory = "256Mi",
            LimitsMemory = "512Mi",
        }), External()).PrimaryContent;

        content.Should().Contain("resources:");
        content.Should().Contain("requests:");
        content.Should().Contain("cpu: \"100m\"");
        content.Should().Contain("memory: \"256Mi\"");
        content.Should().Contain("limits:");
    }

    [Fact]
    public void Ports_render_with_names_and_protocols()
    {
        var content = Renderer.Render(Spec(network: new ResolvedNetwork
        {
            Ports = new[] { new ResolvedPort { Name = "api", Port = 8080, Protocol = "tcp" } },
        }), External()).PrimaryContent;

        content.Should().Contain("ports:");
        content.Should().Contain("- name: \"api\"");
        content.Should().Contain("containerPort: 8080");
        content.Should().Contain("protocol: TCP");
    }

    [Fact]
    public void A_privilege_needed_for_device_passthrough_renders_privileged()
    {
        var content = Renderer.Render(Spec(privileges: new ResolvedPrivileges
        {
            DevicePassthroughPrivileged = true,
            DevicePaths = new[] { "/dev/ttyUSB0" },
        }), External()).PrimaryContent;

        content.Should().Contain("privileged: true");
        content.Should().Contain("- /dev/ttyUSB0");
    }

    [Fact]
    public void Privileges_render_into_a_security_context_and_host_devices()
    {
        var content = Renderer.Render(Spec(privileges: new ResolvedPrivileges
        {
            Privileged = true,
            RunAsRoot = true,
            Capabilities = new[] { "NET_ADMIN" },
            DevicePaths = new[] { "/dev/ttyUSB0" },
            TunDevice = true,
        }), External()).PrimaryContent;

        content.Should().Contain("securityContext:");
        content.Should().Contain("privileged: true");
        content.Should().Contain("runAsNonRoot: false");
        content.Should().Contain("- NET_ADMIN");
        content.Should().Contain("hostDevices:");
        content.Should().Contain("- /dev/net/tun");
        content.Should().Contain("- /dev/ttyUSB0");
    }

    [Fact]
    public void Volumes_render_with_size_and_criticality()
    {
        var content = Renderer.Render(Spec(volumes: new[]
        {
            new ResolvedVolume
            {
                Id = "data", MountPath = "/data", Size = "1Gi",
                Criticality = VolumeCriticality.Critical, ResourceName = "pvc-data",
            },
        }), External()).PrimaryContent;

        content.Should().Contain("persistence:");
        content.Should().Contain("- id: data");
        content.Should().Contain("mountPath: /data");
        content.Should().Contain("size: \"1Gi\"");
        content.Should().Contain("criticality: critical");
    }

    [Fact]
    public void I1_adopted_claim_volume_is_an_existing_claim_reference_not_a_fresh_provision_and_warns()
    {
        var result = Renderer.Render(Spec(volumes: new[]
        {
            new ResolvedVolume
            {
                Id = "state", MountPath = "/state", Size = "2Gi",
                Criticality = VolumeCriticality.Critical,
                ResourceName = "reeve-wl-pvc-retained-state",
                ExistingClaim = true,
            },
        }), External());

        result.PrimaryContent.Should().Contain("persistence:");
        result.PrimaryContent.Should().Contain("- id: state");
        result.PrimaryContent.Should().Contain("mountPath: /state");
        result.PrimaryContent.Should().Contain("existingClaim: reeve-wl-pvc-retained-state");
        result.PrimaryContent.Should().NotContain("size: \"2Gi\"");
        result.Notes.Should().Contain(n =>
            n.Contains("reeve-wl-pvc-retained-state") && n.Contains("must already exist"));
    }

    [Fact]
    public void I1_a_fresh_volume_alongside_an_adopted_claim_still_provisions_only_the_fresh_one()
    {
        var result = Renderer.Render(Spec(volumes: new[]
        {
            new ResolvedVolume
            {
                Id = "cache", MountPath = "/cache", Size = "1Gi",
                Criticality = VolumeCriticality.Recoverable, ResourceName = "reeve-wl-pvc-fresh-cache",
            },
            new ResolvedVolume
            {
                Id = "state", MountPath = "/state", Size = "2Gi",
                Criticality = VolumeCriticality.Critical, ResourceName = "reeve-wl-pvc-retained-state",
                ExistingClaim = true,
            },
        }), External());

        result.PrimaryContent.Should().Contain("- id: cache");
        result.PrimaryContent.Should().Contain("size: \"1Gi\"");
        result.PrimaryContent.Should().Contain("- id: state");
        result.PrimaryContent.Should().Contain("existingClaim: reeve-wl-pvc-retained-state");
        result.PrimaryContent.Should().NotContain("size: \"2Gi\"");
        result.Notes.Should().Contain(n =>
            n.Contains("reeve-wl-pvc-retained-state") && n.Contains("must already exist"));
    }

    [Fact]
    public void I1_a_fresh_volume_has_no_existing_claim_key_and_no_restore_note()
    {
        var result = Renderer.Render(Spec(volumes: new[]
        {
            new ResolvedVolume
            {
                Id = "data", MountPath = "/data", Size = "1Gi",
                Criticality = VolumeCriticality.Critical, ResourceName = "pvc-data",
            },
        }), External());

        result.PrimaryContent.Should().NotContain("existingClaim");
        result.Notes.Should().NotContain(n => n.Contains("must already exist"));
    }

    [Fact]
    public void I4_host_network_emits_host_network_true()
    {
        var content = Renderer.Render(Spec(network: new ResolvedNetwork { HostNetwork = true }), External())
            .PrimaryContent;

        content.Should().Contain("hostNetwork: true");
    }

    [Fact]
    public void I4_no_host_network_does_not_emit_host_network()
    {
        var content = Renderer.Render(Spec(network: new ResolvedNetwork { HostNetwork = false }), External())
            .PrimaryContent;

        content.Should().NotContain("hostNetwork");
    }

    [Fact]
    public void User_namespace_off_keeps_the_existing_helm_values_unchanged()
    {
        var untouched = Renderer.Render(Spec(), External()).PrimaryContent;
        Renderer.Render(Spec() with { UserNamespace = false }, External()).PrimaryContent.Should().Be(untouched);
        Renderer.Render(Spec() with { UserNamespaceSkippedForHostNetwork = true }, External()).PrimaryContent.Should().Be(untouched);
        untouched.Should().NotContain("hostUsers");
        untouched.Should().NotContain("securityContext");
    }

    [Fact]
    public void User_namespace_on_adds_only_the_hostUsers_line()
    {
        var off = Renderer.Render(Spec(), External()).PrimaryContent;
        var on = Renderer.Render(Spec() with { UserNamespace = true }, External()).PrimaryContent;
        on.Should().Contain("hostUsers: false");
        on.Replace("hostUsers: false", string.Empty).TrimEnd('\n').Should().Be(off);
        on.Should().NotContain("securityContext");
    }

    [Fact]
    public void R30_an_attachment_renders_the_attachment_list_next_to_host_network()
    {
        var content = Renderer.Render(Spec(network: OperatorNetwork("iot-vlan100")), External()).PrimaryContent;

        content.Should().Contain("networkAttachments:\n  - iot-vlan100");
        content.Should().NotContain("hostNetwork");
    }

    [Fact]
    public void R30_attachments_render_in_declared_order_and_are_never_sorted()
    {
        var content = Renderer.Render(Spec(network: OperatorNetwork("zone-b-net", "alpha-net")), External())
            .PrimaryContent;

        content.Should().Contain("networkAttachments:\n  - zone-b-net\n  - alpha-net");
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
    public void R30_the_host_network_arm_is_kept_and_emits_no_attachment_list_and_no_operator_note()
    {
        var result = Renderer.Render(
            Spec(network: new ResolvedNetwork { HostNetwork = true, Mode = WorkloadNetworkMode.HostNetwork }),
            External());

        result.PrimaryContent.Should().Contain("hostNetwork: true");
        result.PrimaryContent.Should().NotContain("networkAttachments");
        result.Notes.Should().NotContain(n => n.Contains(DriverUnknownCopy));
    }

    [Fact]
    public void R30_an_incoherent_host_network_plus_attachments_spec_still_emits_no_attachment_list()
    {
        var result = Renderer.Render(
            Spec(network: new ResolvedNetwork
            {
                HostNetwork = true,
                Mode = WorkloadNetworkMode.HostNetwork,
                Attachments = new[] { "iot-vlan100" },
            }),
            External());

        result.PrimaryContent.Should().Contain("hostNetwork: true");
        result.PrimaryContent.Should().NotContain("networkAttachments");
        result.PrimaryContent.Should().NotContain("iot-vlan100");
        result.Notes.Should().NotContain(n => n.Contains(DriverUnknownCopy));
    }

    [Fact]
    public void R30_a_cluster_mode_workload_renders_no_attachment_list_and_no_operator_note()
    {
        var result = Renderer.Render(Spec(network: new ResolvedNetwork { HostNetwork = false }), External());

        result.PrimaryContent.Should().NotContain("networkAttachments");
        result.Notes.Should().NotContain(n => n.Contains(DriverUnknownCopy));
    }

    [Fact]
    public void I4_mdns_and_ipv6_are_surfaced_as_flags_or_a_note()
    {
        var result = Renderer.Render(
            Spec(network: new ResolvedNetwork { HostNetwork = false, Mdns = true, Ipv6Required = true }), External());

        var surfaced =
            (result.PrimaryContent.Contains("mdns") || result.Notes.Any(n => n.Contains("mDNS")))
            && (result.PrimaryContent.Contains("ipv6") || result.Notes.Any(n => n.Contains("IPv6")));
        surfaced.Should().BeTrue();
    }

    [Fact]
    public void I4_node_local_state_renders_a_structured_block()
    {
        var content = Renderer.Render(Spec(privileges: new ResolvedPrivileges
        {
            NodeLocalState = new ResolvedNodeLocalState
            {
                HostPath = "/var/lib/reeve/ble/state",
                Criticality = VolumeCriticality.Critical,
            },
        }), External()).PrimaryContent;

        content.Should().Contain("nodeLocalState:");
        content.Should().Contain("hostPath: /var/lib/reeve/ble/state");
        content.Should().Contain("criticality: critical");
    }

    [Fact]
    public void I4_config_map_mounts_render_a_config_maps_block()
    {
        var content = Renderer.Render(Spec(configMaps: new[]
        {
            new ResolvedConfigMapMount { MountPath = "/etc/reeve/config", ConfigMapName = "reeve-matter-config" },
        }), External()).PrimaryContent;

        content.Should().Contain("configMaps:");
        content.Should().Contain("mountPath: /etc/reeve/config");
        content.Should().Contain("name: \"reeve-matter-config\"");
    }

    [Fact]
    public void I4_observability_metrics_render_a_metrics_block()
    {
        var content = Renderer.Render(Spec(observability: new WorkloadObservability
        {
            Metrics = new WorkloadMetrics { Port = 9090, Path = "/metrics" },
        }), External()).PrimaryContent;

        content.Should().Contain("metrics:");
        content.Should().Contain("port: 9090");
        content.Should().Contain("path: /metrics");
    }

    [Fact]
    public void A_chmod_600_style_note_is_present()
    {
        Renderer.Render(Spec(), Reveal()).Notes
            .Should().Contain(n => n.Contains("token") && n.Contains("commit"));
    }

    [Fact]
    public void Golden_helm_values_external_secret_mode()
    {
        var spec = Spec(
            resources: new ResolvedResources { RequestsCpu = "100m", RequestsMemory = "256Mi", LimitsMemory = "512Mi" },
            network: new ResolvedNetwork { Ports = new[] { new ResolvedPort { Name = "api", Port = 8080, Protocol = "tcp" } } },
            privileges: new ResolvedPrivileges { Capabilities = new[] { "NET_ADMIN" }, DevicePaths = new[] { "/dev/ttyUSB0" } },
            volumes: new[]
            {
                new ResolvedVolume
                {
                    Id = "data", MountPath = "/data", Size = "1Gi",
                    Criticality = VolumeCriticality.Critical, ResourceName = "pvc-data",
                },
            });

        Renderer.Render(spec, External()).PrimaryContent.Should().Be(ArtifactGoldens.HelmValuesExternalSecret);
    }

    [Fact]
    public void Golden_helm_values_field_complete()
    {
        var spec = Spec(
            network: new ResolvedNetwork
            {
                HostNetwork = true,
                Ports = new[] { new ResolvedPort { Name = "api", Port = 8080, Protocol = "tcp" } },
            },
            observability: new WorkloadObservability { Metrics = new WorkloadMetrics { Port = 9090, Path = "/metrics" } },
            configMaps: new[]
            {
                new ResolvedConfigMapMount { MountPath = "/etc/reeve/config", ConfigMapName = "reeve-matter-config" },
            },
            privileges: new ResolvedPrivileges
            {
                Capabilities = new[] { "NET_ADMIN" },
                NodeLocalState = new ResolvedNodeLocalState
                {
                    HostPath = "/var/lib/reeve/ble/state",
                    Criticality = VolumeCriticality.Critical,
                },
            });

        Renderer.Render(spec, External()).PrimaryContent.Should().Be(ArtifactGoldens.HelmValuesFieldComplete);
    }

    [Fact]
    public void Golden_helm_values_adopted_claim_restore()
    {
        var spec = Spec(volumes: new[]
        {
            new ResolvedVolume
            {
                Id = "cache", MountPath = "/cache", Size = "1Gi",
                Criticality = VolumeCriticality.Recoverable, ResourceName = "reeve-wl-pvc-fresh-cache",
            },
            new ResolvedVolume
            {
                Id = "state", MountPath = "/state", Size = "2Gi",
                Criticality = VolumeCriticality.Critical, ResourceName = "reeve-wl-pvc-retained-state",
                ExistingClaim = true,
            },
        });

        Renderer.Render(spec, External()).PrimaryContent.Should().Be(ArtifactGoldens.HelmValuesAdoptedClaimRestore);
    }

    [Fact]
    public void Helm_values_emit_the_supplemental_groups_under_a_pod_security_context()
    {
        var content = Renderer.Render(Spec(privileges: new ResolvedPrivileges
        {
            DevicePaths = new[] { "/dev/dri/renderD128" },
            DeviceGroupIds = new[] { 993, 104 },
        }), External()).PrimaryContent;

        content.Should().Contain("podSecurityContext:\n  supplementalGroups:\n    - 104\n    - 993");
    }

    [Fact]
    public void Helm_values_emit_the_volume_group_with_its_change_policy_beside_the_device_groups()
    {
        var content = Renderer.Render(Spec(privileges: new ResolvedPrivileges
        {
            FsGroup = 1000,
            DeviceGroupIds = new[] { 104 },
        }), External()).PrimaryContent;

        content.Should().Contain(
            "podSecurityContext:\n  fsGroup: 1000\n  fsGroupChangePolicy: OnRootMismatch\n  supplementalGroups:\n    - 104");
    }

    [Fact]
    public void Helm_values_emit_no_pod_security_context_without_a_device_group()
    {
        var content = Renderer.Render(
            Spec(privileges: new ResolvedPrivileges { DevicePaths = new[] { "/dev/ttyUSB0" } }), External())
            .PrimaryContent;

        content.Should().NotContain("podSecurityContext");
    }
}
