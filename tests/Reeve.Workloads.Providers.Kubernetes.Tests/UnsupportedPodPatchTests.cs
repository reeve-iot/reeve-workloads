using Reeve.Workloads.Abstractions.Providers;
using System;
using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using k8s.Models;
using Reeve.Workloads.Abstractions.Contracts;
using Reeve.Workloads.Abstractions.Contracts.Deployment;
using Reeve.Workloads.Abstractions.Domain;
using Reeve.Workloads.Abstractions.Domain.DesiredSpec;
using Reeve.Workloads.Domain;
using Reeve.Workloads.Providers.Kubernetes;
using Xunit;

namespace Reeve.Workloads.Providers.Kubernetes.Tests;

public sealed class UnsupportedPodPatchTests
{
    private const string InstanceIdStr = "11111111-2222-3333-4444-555555555555";
    private const string Digest = "sha256:1111111111111111111111111111111111111111111111111111111111111111";

    private static readonly KubernetesRenderContext Ctx = new() { Namespace = "reeve" };
    private static readonly KubernetesWorkloadRenderer Renderer = new();
    private static readonly UnsupportedPodPatchApplier Applier = new();
    private static readonly KubernetesPatchRevalidator Revalidator = new();

    private static WorkloadDesiredSpec Spec(
        WorkloadKind kind = WorkloadKind.SingleNode,
        ResolvedNetwork? network = null,
        ResolvedPrivileges? privileges = null)
    {
        const string specHash = "abc123def456";
        var instanceId = Guid.Parse(InstanceIdStr);
        var labels = DefaultWorkloadResourceNamer.Instance.Labels(new WorkloadLabelSubject
                     {
                         InstanceId = instanceId,
                         InstallationId = "install-1",
                         SpecHash = specHash,
                     });

        return new WorkloadDesiredSpec
        {
            GroupInstanceId = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee"),
            GroupId = "com.reeve.matter",
            WorkloadId = "matter",
            InstanceId = instanceId,
            Inst8 = "11111111",
            Provider = KubernetesProvider.Id,
            ResourceName = "reeve-wl-com-reeve-matter-matter-11111111",
            SecretName = "reeve-wl-com-reeve-matter-matter-11111111",
            Labels = labels,
            Image = $"ghcr.io/reeve/matter@{Digest}",
            Platforms = new List<string>(),
            Kind = kind,
            NodeSelector = new Dictionary<string, string>(),
            Network = network,
            Privileges = privileges,
            Volumes = new List<ResolvedVolume>(),
            ConfigMapMounts = new List<ResolvedConfigMapMount>(),
            Health = new WorkloadHealth { Mode = WorkloadHealthMode.Container },
            Env = new List<ResolvedEnvVar>(),
            SpecHash = specHash,
        };
    }

    private static RenderedKubernetesObjects Render(WorkloadDesiredSpec spec) => Renderer.Render(spec, Ctx);

    private static V1PodSpec Pod(RenderedKubernetesObjects r)
        => r.Deployment?.Spec.Template.Spec ?? r.DaemonSet!.Spec.Template.Spec;

    [Fact]
    public void Applier_add_env_var_mutates_the_deployment_pod_template()
    {
        var spec = Spec();
        var rendered = Render(spec);
        const string patch = """[{"op":"add","path":"/spec/template/spec/containers/0/env","value":[{"name":"EXTRA","value":"1"}]}]""";

        var patched = Applier.Apply(rendered, patch);

        patched.Deployment.Should().NotBeNull();
        patched.DaemonSet.Should().BeNull();
        var env = Pod(patched).Containers.Single().Env;
        env.Should().ContainSingle(e => e.Name == "EXTRA" && e.Value == "1");
        ReferenceEquals(patched, rendered).Should().BeFalse();
    }

    [Fact]
    public void Applier_add_privileged_true_is_reflected_in_the_security_context()
    {
        var spec = Spec(privileges: new ResolvedPrivileges());
        var rendered = Render(spec);
        const string patch = """[{"op":"add","path":"/spec/template/spec/containers/0/securityContext/privileged","value":true}]""";

        var patched = Applier.Apply(rendered, patch);

        Pod(patched).Containers.Single().SecurityContext!.Privileged.Should().BeTrue();
    }

    [Fact]
    public void Applier_add_hostPath_volume_is_reflected_in_the_pod_volumes()
    {
        var spec = Spec();
        var rendered = Render(spec);
        const string patch = """
            [{"op":"add","path":"/spec/template/spec/volumes","value":[{"name":"host","hostPath":{"path":"/var/run/docker.sock"}}]}]
            """;

        var patched = Applier.Apply(rendered, patch);

        Pod(patched).Volumes.Should().ContainSingle(v => v.HostPath != null && v.HostPath.Path == "/var/run/docker.sock");
    }

    [Fact]
    public void Applier_replace_op_mutates_an_existing_field()
    {
        var spec = Spec();
        var rendered = Render(spec);
        const string patch = """[{"op":"replace","path":"/spec/template/spec/containers/0/image","value":"ghcr.io/evil/x:latest"}]""";

        var patched = Applier.Apply(rendered, patch);

        Pod(patched).Containers.Single().Image.Should().Be("ghcr.io/evil/x:latest");
    }

    [Fact]
    public void Applier_remove_op_removes_a_field()
    {
        var spec = Spec();
        var rendered = Render(spec);
        const string patch = """[{"op":"remove","path":"/spec/replicas"}]""";

        var patched = Applier.Apply(rendered, patch);

        patched.Deployment!.Spec.Replicas.Should().BeNull();
    }

    [Fact]
    public void Applier_patches_the_daemonset_when_that_is_the_pod_bearing_object()
    {
        var spec = Spec(kind: WorkloadKind.PerNode);
        var rendered = Render(spec);
        rendered.DaemonSet.Should().NotBeNull();
        const string patch = """[{"op":"add","path":"/spec/template/spec/hostPID","value":true}]""";

        var patched = Applier.Apply(rendered, patch);

        patched.DaemonSet.Should().NotBeNull();
        Pod(patched).HostPID.Should().BeTrue();
    }

    [Fact]
    public void Applier_throws_a_clear_error_on_a_failing_patch()
    {
        var spec = Spec();
        var rendered = Render(spec);
        const string patch = """[{"op":"remove","path":"/spec/template/spec/doesNotExist"}]""";

        var act = () => Applier.Apply(rendered, patch);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*patch*", "the message names the patch failure so the provider can mark the apply Failed");
    }

    [Fact]
    public void Applier_throws_on_malformed_patch_json()
    {
        var spec = Spec();
        var rendered = Render(spec);

        var act = () => Applier.Apply(rendered, "not-json");

        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Revalidator_flags_privileged_true_when_desired_is_not_privileged()
    {
        var spec = Spec(privileges: new ResolvedPrivileges { Privileged = false });
        var patched = Applier.Apply(Render(spec),
            """[{"op":"add","path":"/spec/template/spec/containers/0/securityContext/privileged","value":true}]""");

        var violations = Revalidator.Validate(spec, patched);

        violations.Should().ContainSingle().Which.Should().ContainEquivalentOf("privileged");
    }

    [Fact]
    public void Revalidator_allows_privileged_true_when_desired_already_declared_it()
    {
        var spec = Spec(privileges: new ResolvedPrivileges { Privileged = true });
        var patched = Applier.Apply(Render(spec),
            """[{"op":"add","path":"/spec/template/spec/containers/0/securityContext/privileged","value":true}]""");

        Revalidator.Validate(spec, patched).Should().BeEmpty();
    }

    [Fact]
    public void Revalidator_flags_hostNetwork_true_when_desired_did_not_declare_it()
    {
        var spec = Spec(network: new ResolvedNetwork { HostNetwork = false });
        var patched = Applier.Apply(Render(spec),
            """[{"op":"add","path":"/spec/template/spec/hostNetwork","value":true}]""");

        Revalidator.Validate(spec, patched).Should().ContainSingle().Which.Should().ContainEquivalentOf("hostNetwork");
    }

    [Fact]
    public void Revalidator_flags_a_hostPath_outside_the_declared_set()
    {
        var spec = Spec();
        var patched = Applier.Apply(Render(spec),
            """[{"op":"add","path":"/spec/template/spec/volumes","value":[{"name":"h","hostPath":{"path":"/var/run/docker.sock"}}]}]""");

        var violations = Revalidator.Validate(spec, patched);

        violations.Should().ContainSingle().Which.Should().Contain("/var/run/docker.sock");
    }

    [Fact]
    public void Revalidator_allows_a_hostPath_the_typed_spec_declared()
    {
        var spec = Spec(privileges: new ResolvedPrivileges
        {
            NodeLocalState = new ResolvedNodeLocalState
            {
                HostPath = "/var/lib/reeve/com.reeve.matter/matter",
                Criticality = VolumeCriticality.Critical,
            },
        });
        var patched = Render(spec);

        Revalidator.Validate(spec, patched).Should().BeEmpty();
    }

    [Fact]
    public void Revalidator_allows_the_dbus_hostPath_the_typed_spec_declared()
    {
        var spec = Spec(privileges: new ResolvedPrivileges { DbusHostPath = "/run/dbus" });
        var patched = Render(spec);

        Revalidator.Validate(spec, patched).Should().BeEmpty();
    }

    [Fact]
    public void Revalidator_allows_the_tun_device_hostPath_when_TunDevice_is_declared()
    {
        var spec = Spec(privileges: new ResolvedPrivileges { TunDevice = true });
        var patched = Render(spec);

        Revalidator.Validate(spec, patched).Should().BeEmpty();
    }

    [Fact]
    public void Revalidator_flags_hostPID_which_the_typed_schema_never_declares()
    {
        var spec = Spec();
        var patched = Applier.Apply(Render(spec),
            """[{"op":"add","path":"/spec/template/spec/hostPID","value":true}]""");

        Revalidator.Validate(spec, patched).Should().ContainSingle().Which.Should().ContainEquivalentOf("hostPID");
    }

    [Fact]
    public void Revalidator_flags_hostIPC_which_the_typed_schema_never_declares()
    {
        var spec = Spec();
        var patched = Applier.Apply(Render(spec),
            """[{"op":"add","path":"/spec/template/spec/hostIPC","value":true}]""");

        Revalidator.Validate(spec, patched).Should().ContainSingle().Which.Should().ContainEquivalentOf("hostIPC");
    }

    [Fact]
    public void Revalidator_flags_serviceAccountName_which_the_typed_schema_never_declares()
    {
        var spec = Spec();
        var patched = Applier.Apply(Render(spec),
            """[{"op":"add","path":"/spec/template/spec/serviceAccountName","value":"cluster-admin-sa"}]""");

        Revalidator.Validate(spec, patched).Should().ContainSingle().Which.Should().ContainEquivalentOf("serviceAccountName");
    }

    [Fact]
    public void Revalidator_flags_a_capability_not_in_the_consented_set()
    {
        var spec = Spec(privileges: new ResolvedPrivileges { Capabilities = new[] { "NET_ADMIN" } });
        var patched = Applier.Apply(Render(spec),
            """[{"op":"add","path":"/spec/template/spec/containers/0/securityContext/capabilities/add/-","value":"SYS_ADMIN"}]""");

        var violations = Revalidator.Validate(spec, patched);

        violations.Should().ContainSingle().Which.Should().Contain("SYS_ADMIN");
    }

    [Fact]
    public void Revalidator_allows_a_capability_already_in_the_consented_set()
    {
        var spec = Spec(privileges: new ResolvedPrivileges { Capabilities = new[] { "NET_ADMIN", "NET_RAW" } });
        var patched = Render(spec);

        Revalidator.Validate(spec, patched).Should().BeEmpty();
    }

    [Fact]
    public void Revalidator_flags_runAsUser_root_when_desired_did_not_declare_RunAsRoot()
    {
        var spec = Spec(privileges: new ResolvedPrivileges { RunAsRoot = false });
        var patched = Applier.Apply(Render(spec),
            """[{"op":"add","path":"/spec/template/spec/containers/0/securityContext/runAsUser","value":0}]""");

        Revalidator.Validate(spec, patched).Should().ContainSingle().Which.Should().ContainEquivalentOf("root");
    }

    [Fact]
    public void Revalidator_allows_runAsUser_root_when_desired_declared_RunAsRoot()
    {
        var spec = Spec(privileges: new ResolvedPrivileges { RunAsRoot = true });
        var patched = Render(spec);

        Revalidator.Validate(spec, patched).Should().BeEmpty();
    }

    [Fact]
    public void Revalidator_passes_a_benign_patch_that_adds_a_label_annotation_env()
    {
        var spec = Spec();
        var patched = Applier.Apply(Render(spec),
            """
            [
              {"op":"add","path":"/spec/template/metadata/annotations","value":{"team":"iot"}},
              {"op":"add","path":"/spec/template/spec/containers/0/env","value":[{"name":"LOG_LEVEL","value":"debug"}]}
            ]
            """);

        Revalidator.Validate(spec, patched).Should().BeEmpty();
    }

    [Fact]
    public void Revalidator_reports_multiple_violations_when_a_patch_escalates_several_fields()
    {
        var spec = Spec(privileges: new ResolvedPrivileges());
        var patched = Applier.Apply(Render(spec),
            """
            [
              {"op":"add","path":"/spec/template/spec/containers/0/securityContext/privileged","value":true},
              {"op":"add","path":"/spec/template/spec/hostPID","value":true},
              {"op":"add","path":"/spec/template/spec/serviceAccountName","value":"admin"}
            ]
            """);

        Revalidator.Validate(spec, patched).Should().HaveCount(3);
    }

    [Fact]
    public void Revalidator_flags_privileged_in_an_INIT_container_the_patch_smuggles_in()
    {
        var spec = Spec(privileges: new ResolvedPrivileges { Privileged = false });
        var patched = Applier.Apply(Render(spec),
            """[{"op":"add","path":"/spec/template/spec/initContainers","value":[{"name":"init-evil","image":"busybox","securityContext":{"privileged":true}}]}]""");

        Revalidator.Validate(spec, patched).Should()
            .ContainSingle(v => v.Contains("privileged") && v.Contains("init-evil"));
    }

    [Fact]
    public void Revalidator_flags_privileged_in_an_EPHEMERAL_container_the_patch_smuggles_in()
    {
        var spec = Spec(privileges: new ResolvedPrivileges { Privileged = false });
        var patched = Applier.Apply(Render(spec),
            """[{"op":"add","path":"/spec/template/spec/ephemeralContainers","value":[{"name":"debug-evil","image":"busybox","securityContext":{"privileged":true}}]}]""");

        Revalidator.Validate(spec, patched).Should()
            .ContainSingle(v => v.Contains("privileged") && v.Contains("debug-evil"));
    }

    [Fact]
    public void Revalidator_flags_a_capability_added_to_an_INIT_container_beyond_the_consented_set()
    {
        var spec = Spec(privileges: new ResolvedPrivileges { Capabilities = new[] { "NET_ADMIN" } });
        var patched = Applier.Apply(Render(spec),
            """[{"op":"add","path":"/spec/template/spec/initContainers","value":[{"name":"init-x","image":"busybox","securityContext":{"capabilities":{"add":["SYS_ADMIN"]}}}]}]""");

        Revalidator.Validate(spec, patched).Should()
            .ContainSingle(v => v.Contains("SYS_ADMIN") && v.Contains("init-x"));
    }

    [Fact]
    public void Revalidator_flags_runAsUser_root_in_an_EPHEMERAL_container()
    {
        var spec = Spec(privileges: new ResolvedPrivileges { RunAsRoot = false });
        var patched = Applier.Apply(Render(spec),
            """[{"op":"add","path":"/spec/template/spec/ephemeralContainers","value":[{"name":"spy","image":"busybox","securityContext":{"runAsUser":0}}]}]""");

        Revalidator.Validate(spec, patched).Should()
            .ContainSingle(v => v.Contains("runAsUser", StringComparison.OrdinalIgnoreCase) && v.Contains("spy", StringComparison.Ordinal));
    }

    [Fact]
    public void Revalidator_flags_allowPrivilegeEscalation_true_which_the_typed_schema_never_grants()
    {
        var spec = Spec(privileges: new ResolvedPrivileges());
        var patched = Applier.Apply(Render(spec),
            """[{"op":"add","path":"/spec/template/spec/containers/0/securityContext/allowPrivilegeEscalation","value":true}]""");

        Revalidator.Validate(spec, patched).Should()
            .ContainSingle(v => v.Contains("allowPrivilegeEscalation", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Revalidator_flags_a_hostPort_binding_which_the_typed_schema_never_grants()
    {
        var spec = Spec();
        var patched = Applier.Apply(Render(spec),
            """[{"op":"add","path":"/spec/template/spec/containers/0/ports","value":[{"containerPort":8080,"hostPort":8080}]}]""");

        Revalidator.Validate(spec, patched).Should()
            .ContainSingle(v => v.Contains("hostPort", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Revalidator_flags_removal_of_drop_ALL_from_a_container_capabilities()
    {
        var spec = Spec(privileges: new ResolvedPrivileges());
        var patched = Applier.Apply(Render(spec),
            """[{"op":"replace","path":"/spec/template/spec/containers/0/securityContext/capabilities/drop","value":[]}]""");

        Revalidator.Validate(spec, patched).Should()
            .ContainSingle(v => v.Contains("drop", StringComparison.OrdinalIgnoreCase) && v.Contains("ALL", StringComparison.Ordinal));
    }

    [Fact]
    public void Revalidator_flags_removal_of_the_whole_capabilities_object_from_a_rendered_container()
    {
        var spec = Spec(privileges: new ResolvedPrivileges());
        var patched = Applier.Apply(Render(spec),
            """[{"op":"remove","path":"/spec/template/spec/containers/0/securityContext/capabilities"}]""");

        Revalidator.Validate(spec, patched).Should()
            .ContainSingle(v => v.Contains("drop", StringComparison.OrdinalIgnoreCase) && v.Contains("ALL", StringComparison.Ordinal));
    }

    [Fact]
    public void Revalidator_flags_pod_level_runAsUser_root_which_containers_inherit()
    {
        var spec = Spec(privileges: new ResolvedPrivileges { RunAsRoot = false });
        var patched = Applier.Apply(Render(spec),
            """[{"op":"add","path":"/spec/template/spec/securityContext","value":{"runAsUser":0}}]""");

        Revalidator.Validate(spec, patched).Should()
            .ContainSingle(v => v.Contains("root", StringComparison.OrdinalIgnoreCase)
                && v.Contains("pod", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Revalidator_flags_pod_level_runAsNonRoot_false_which_containers_inherit()
    {
        var spec = Spec(privileges: new ResolvedPrivileges { RunAsRoot = false });
        var patched = Applier.Apply(Render(spec),
            """[{"op":"add","path":"/spec/template/spec/securityContext","value":{"runAsNonRoot":false}}]""");

        Revalidator.Validate(spec, patched).Should()
            .ContainSingle(v => v.Contains("runAsNonRoot", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Revalidator_flags_the_serviceAccount_ALIAS_the_api_server_honors()
    {
        var spec = Spec();
        var patched = Applier.Apply(Render(spec),
            """[{"op":"add","path":"/spec/template/spec/serviceAccount","value":"cluster-admin-sa"}]""");

        Revalidator.Validate(spec, patched).Should()
            .ContainSingle(v => v.Contains("serviceAccount", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Revalidator_flags_automountServiceAccountToken_true()
    {
        var spec = Spec();
        var patched = Applier.Apply(Render(spec),
            """[{"op":"add","path":"/spec/template/spec/automountServiceAccountToken","value":true}]""");

        Revalidator.Validate(spec, patched).Should()
            .ContainSingle(v => v.Contains("automountServiceAccountToken", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Revalidator_flags_a_declared_hostPath_with_a_trailing_slash_bypass()
    {
        var spec = Spec(privileges: new ResolvedPrivileges { DbusHostPath = "/run/dbus" });
        var patched = Applier.Apply(Render(spec),
            """[{"op":"add","path":"/spec/template/spec/volumes/-","value":{"name":"dbus2","hostPath":{"path":"/run/dbus/"}}}]""");

        Revalidator.Validate(spec, patched).Should().BeEmpty();
    }

    [Fact]
    public void Revalidator_flags_a_hostPath_containing_a_dot_dot_traversal_segment()
    {
        var spec = Spec(privileges: new ResolvedPrivileges { DbusHostPath = "/run/dbus" });
        var patched = Applier.Apply(Render(spec),
            """[{"op":"add","path":"/spec/template/spec/volumes/-","value":{"name":"evil","hostPath":{"path":"/run/dbus/../../etc"}}}]""");

        Revalidator.Validate(spec, patched).Should()
            .Contain(v => v.Contains("/run/dbus/../../etc"));
    }

    [Fact]
    public void Revalidator_flags_replicas_greater_than_one_on_a_single_instance_deployment()
    {
        var spec = Spec(kind: WorkloadKind.SingleNode);
        var patched = Applier.Apply(Render(spec),
            """[{"op":"replace","path":"/spec/replicas","value":2}]""");

        Revalidator.Validate(spec, patched).Should()
            .ContainSingle(v => v.Contains("replicas", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Revalidator_flags_a_deployment_strategy_changed_away_from_Recreate()
    {
        var spec = Spec(kind: WorkloadKind.SingleNode);
        var patched = Applier.Apply(Render(spec),
            """[{"op":"replace","path":"/spec/strategy/type","value":"RollingUpdate"}]""");

        Revalidator.Validate(spec, patched).Should()
            .ContainSingle(v => v.Contains("strategy", StringComparison.OrdinalIgnoreCase)
                || v.Contains("Recreate", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Revalidator_allows_replicas_one_and_Recreate_on_a_benign_single_instance_deployment()
    {
        var spec = Spec(kind: WorkloadKind.SingleNode);
        var patched = Render(spec);

        Revalidator.Validate(spec, patched).Should().BeEmpty();
    }

    [Fact]
    public void Revalidator_flags_removal_of_the_whole_container_securityContext()
    {
        var spec = Spec(privileges: new ResolvedPrivileges());
        var patched = Applier.Apply(Render(spec),
            """[{"op":"remove","path":"/spec/template/spec/containers/0/securityContext"}]""");

        Revalidator.Validate(spec, patched).Should()
            .Contain(v => v.Contains("securityContext", StringComparison.OrdinalIgnoreCase)
                && v.Contains("baseline", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Revalidator_flags_removal_of_allowPrivilegeEscalation_reverting_to_default_true()
    {
        var spec = Spec(privileges: new ResolvedPrivileges());
        var patched = Applier.Apply(Render(spec),
            """[{"op":"remove","path":"/spec/template/spec/containers/0/securityContext/allowPrivilegeEscalation"}]""");

        Revalidator.Validate(spec, patched).Should()
            .Contain(v => v.Contains("allowPrivilegeEscalation", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Revalidator_flags_removal_of_runAsNonRoot_reverting_to_default_unenforced()
    {
        var spec = Spec(privileges: new ResolvedPrivileges());
        var patched = Applier.Apply(Render(spec),
            """[{"op":"remove","path":"/spec/template/spec/containers/0/securityContext/runAsNonRoot"}]""");

        Revalidator.Validate(spec, patched).Should()
            .Contain(v => v.Contains("runAsNonRoot", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Revalidator_flags_a_bare_added_container_with_no_securityContext()
    {
        var spec = Spec(privileges: new ResolvedPrivileges());
        var patched = Applier.Apply(Render(spec),
            """[{"op":"add","path":"/spec/template/spec/containers/-","value":{"name":"bare","image":"busybox"}}]""");

        Revalidator.Validate(spec, patched).Should()
            .Contain(v => v.Contains("bare", StringComparison.Ordinal)
                && v.Contains("securityContext", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Revalidator_flags_a_bare_added_INIT_container_with_no_securityContext()
    {
        var spec = Spec(privileges: new ResolvedPrivileges());
        var patched = Applier.Apply(Render(spec),
            """[{"op":"add","path":"/spec/template/spec/initContainers","value":[{"name":"init-bare","image":"busybox"}]}]""");

        Revalidator.Validate(spec, patched).Should()
            .Contain(v => v.Contains("init-bare", StringComparison.Ordinal)
                && v.Contains("securityContext", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Revalidator_does_NOT_flag_null_allowPrivilegeEscalation_on_a_consented_privileged_workload()
    {
        var spec = Spec(privileges: new ResolvedPrivileges { Privileged = true });
        var patched = Render(spec);

        Revalidator.Validate(spec, patched).Should().BeEmpty();
    }

    [Fact]
    public void Revalidator_does_NOT_flag_runAsNonRoot_false_on_a_consented_runAsRoot_workload()
    {
        var spec = Spec(privileges: new ResolvedPrivileges { RunAsRoot = true });
        var patched = Render(spec);

        Revalidator.Validate(spec, patched).Should().BeEmpty();
    }

    [Fact]
    public void Revalidator_flags_a_daemonset_updateStrategy_maxSurge_greater_than_zero()
    {
        var spec = Spec(kind: WorkloadKind.PerNode);
        var patched = Applier.Apply(Render(spec),
            """[{"op":"add","path":"/spec/updateStrategy","value":{"type":"RollingUpdate","rollingUpdate":{"maxSurge":1}}}]""");

        Revalidator.Validate(spec, patched).Should()
            .Contain(v => v.Contains("maxSurge", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Revalidator_flags_a_daemonset_updateStrategy_maxSurge_percent()
    {
        var spec = Spec(kind: WorkloadKind.PerNode);
        var patched = Applier.Apply(Render(spec),
            """[{"op":"add","path":"/spec/updateStrategy","value":{"type":"RollingUpdate","rollingUpdate":{"maxSurge":"25%"}}}]""");

        Revalidator.Validate(spec, patched).Should()
            .Contain(v => v.Contains("maxSurge", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Revalidator_allows_a_daemonset_updateStrategy_with_maxSurge_zero()
    {
        var spec = Spec(kind: WorkloadKind.PerNode);
        var patched = Applier.Apply(Render(spec),
            """[{"op":"add","path":"/spec/updateStrategy","value":{"type":"RollingUpdate","rollingUpdate":{"maxSurge":0}}}]""");

        Revalidator.Validate(spec, patched).Should().BeEmpty();
    }

    [Fact]
    public void Revalidator_allows_a_benign_daemonset_with_no_updateStrategy_patch()
    {
        var spec = Spec(kind: WorkloadKind.PerNode);
        var patched = Render(spec);

        Revalidator.Validate(spec, patched).Should().BeEmpty();
    }

    [Fact]
    public void Revalidator_flags_a_secret_volume_referencing_another_workloads_secret()
    {
        var spec = Spec();
        var patched = Applier.Apply(Render(spec),
            """[{"op":"add","path":"/spec/template/spec/volumes","value":[{"name":"steal","secret":{"secretName":"reeve-wl-com-reeve-other-other-99999999"}}]}]""");

        Revalidator.Validate(spec, patched).Should()
            .Contain(v => v.Contains("secret", StringComparison.OrdinalIgnoreCase)
                && v.Contains("reeve-wl-com-reeve-other-other-99999999", StringComparison.Ordinal));
    }

    [Fact]
    public void Revalidator_allows_a_secret_volume_referencing_the_pods_own_secret()
    {
        var spec = Spec();
        var patched = Applier.Apply(Render(spec),
            """[{"op":"add","path":"/spec/template/spec/volumes","value":[{"name":"own","secret":{"secretName":"reeve-wl-com-reeve-matter-matter-11111111"}}]}]""");

        Revalidator.Validate(spec, patched).Should().BeEmpty();
    }

    [Fact]
    public void Revalidator_flags_a_projected_serviceAccountToken_volume()
    {
        var spec = Spec();
        var patched = Applier.Apply(Render(spec),
            """[{"op":"add","path":"/spec/template/spec/volumes","value":[{"name":"proj","projected":{"sources":[{"serviceAccountToken":{"path":"token","expirationSeconds":3600}}]}}]}]""");

        Revalidator.Validate(spec, patched).Should()
            .Contain(v => v.Contains("serviceAccountToken", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Revalidator_flags_shareProcessNamespace_true()
    {
        var spec = Spec();
        var patched = Applier.Apply(Render(spec),
            """[{"op":"add","path":"/spec/template/spec/shareProcessNamespace","value":true}]""");

        Revalidator.Validate(spec, patched).Should()
            .Contain(v => v.Contains("shareProcessNamespace", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Revalidator_allows_a_benign_emptyDir_and_configMap_volume()
    {
        var spec = Spec();
        var patched = Applier.Apply(Render(spec),
            """
            [
              {"op":"add","path":"/spec/template/spec/volumes","value":[{"name":"scratch","emptyDir":{}}]},
              {"op":"add","path":"/spec/template/spec/containers/0/env","value":[{"name":"LOG","value":"1"}]}
            ]
            """);

        Revalidator.Validate(spec, patched).Should().BeEmpty();
    }

    [Fact]
    public void Revalidator_flags_removal_of_the_container_seccompProfile_reverting_to_Unconfined()
    {
        var spec = Spec(privileges: new ResolvedPrivileges());
        var patched = Applier.Apply(Render(spec),
            """[{"op":"remove","path":"/spec/template/spec/containers/0/securityContext/seccompProfile"}]""");

        Revalidator.Validate(spec, patched).Should()
            .ContainSingle(v => v.Contains("seccompProfile", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Revalidator_flags_a_container_seccompProfile_set_to_Unconfined()
    {
        var spec = Spec(privileges: new ResolvedPrivileges());
        var patched = Applier.Apply(Render(spec),
            """[{"op":"replace","path":"/spec/template/spec/containers/0/securityContext/seccompProfile/type","value":"Unconfined"}]""");

        Revalidator.Validate(spec, patched).Should()
            .ContainSingle(v => v.Contains("seccompProfile", StringComparison.OrdinalIgnoreCase)
                && v.Contains("Unconfined", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Revalidator_flags_an_INIT_container_seccompProfile_set_to_Unconfined()
    {
        var spec = Spec(privileges: new ResolvedPrivileges());
        var patched = Applier.Apply(Render(spec),
            """[{"op":"add","path":"/spec/template/spec/initContainers","value":[{"name":"init-seccomp","image":"busybox","securityContext":{"allowPrivilegeEscalation":false,"runAsNonRoot":true,"capabilities":{"drop":["ALL"]},"seccompProfile":{"type":"Unconfined"}}}]}]""");

        Revalidator.Validate(spec, patched).Should()
            .ContainSingle(v => v.Contains("seccompProfile", StringComparison.OrdinalIgnoreCase)
                && v.Contains("init-seccomp", StringComparison.Ordinal));
    }

    [Fact]
    public void Revalidator_flags_an_EPHEMERAL_container_with_no_seccompProfile()
    {
        var spec = Spec(privileges: new ResolvedPrivileges());
        var patched = Applier.Apply(Render(spec),
            """[{"op":"add","path":"/spec/template/spec/ephemeralContainers","value":[{"name":"debug-seccomp","image":"busybox","securityContext":{"allowPrivilegeEscalation":false,"runAsNonRoot":true,"capabilities":{"drop":["ALL"]}}}]}]""");

        Revalidator.Validate(spec, patched).Should()
            .ContainSingle(v => v.Contains("seccompProfile", StringComparison.OrdinalIgnoreCase)
                && v.Contains("debug-seccomp", StringComparison.Ordinal));
    }

    [Fact]
    public void Revalidator_does_NOT_flag_the_rendered_RuntimeDefault_seccompProfile_on_a_benign_workload()
    {
        var spec = Spec(privileges: new ResolvedPrivileges());
        var patched = Applier.Apply(Render(spec),
            """[{"op":"add","path":"/spec/template/spec/containers/0/env","value":[{"name":"LOG","value":"1"}]}]""");

        Revalidator.Validate(spec, patched).Should().BeEmpty();
    }

    [Fact]
    public void Revalidator_does_NOT_flag_the_rendered_seccompProfile_on_a_consented_privileged_workload()
    {
        var spec = Spec(privileges: new ResolvedPrivileges { Privileged = true });
        var patched = Render(spec);

        Revalidator.Validate(spec, patched).Should().BeEmpty();
    }

    [Fact]
    public void Revalidator_does_NOT_flag_the_rendered_seccompProfile_on_a_consented_runAsRoot_workload()
    {
        var spec = Spec(privileges: new ResolvedPrivileges { RunAsRoot = true });
        var patched = Render(spec);

        Revalidator.Validate(spec, patched).Should().BeEmpty();
    }

    [Fact]
    public void Revalidator_allows_a_container_seccompProfile_of_Localhost()
    {
        var spec = Spec(privileges: new ResolvedPrivileges());
        var patched = Applier.Apply(Render(spec),
            """[{"op":"replace","path":"/spec/template/spec/containers/0/securityContext/seccompProfile","value":{"type":"Localhost","localhostProfile":"profiles/audit.json"}}]""");

        Revalidator.Validate(spec, patched).Should().BeEmpty();
    }

    [Fact]
    public void Revalidator_does_NOT_flag_pod_level_runAsNonRoot_false_on_a_consented_runAsRoot_workload()
    {
        var spec = Spec(privileges: new ResolvedPrivileges { RunAsRoot = true });
        var patched = Applier.Apply(Render(spec),
            """[{"op":"add","path":"/spec/template/spec/securityContext","value":{"runAsNonRoot":false}}]""");

        Revalidator.Validate(spec, patched).Should().BeEmpty();
    }

    [Fact]
    public void Patch_adding_a_supplemental_group_the_spec_did_not_declare_is_a_violation()
    {
        var spec = Spec(privileges: new ResolvedPrivileges { DeviceGroupIds = new List<int> { 993 } });
        var patched = Applier.Apply(Render(spec),
            """[{"op":"replace","path":"/spec/template/spec/securityContext/supplementalGroups","value":[993,0]}]""");

        Revalidator.Validate(spec, patched).Should()
            .ContainSingle(v => v.Contains("supplementalGroups", StringComparison.Ordinal)
                && v.Contains("supplementalGroups=0", StringComparison.Ordinal));
    }

    [Fact]
    public void Patch_adding_supplemental_groups_when_the_spec_declares_none_is_a_violation()
    {
        var spec = Spec(privileges: new ResolvedPrivileges { DevicePaths = new List<string> { "/dev/ttyUSB0" } });
        var patched = Applier.Apply(Render(spec),
            """[{"op":"add","path":"/spec/template/spec/securityContext","value":{"supplementalGroups":[27]}}]""");

        Revalidator.Validate(spec, patched).Should()
            .ContainSingle(v => v.Contains("supplementalGroups=27", StringComparison.Ordinal));
    }

    [Fact]
    public void Patch_keeping_exactly_the_declared_supplemental_groups_is_clean()
    {
        var spec = Spec(privileges: new ResolvedPrivileges { DeviceGroupIds = new List<int> { 993 } });
        var patched = Applier.Apply(Render(spec),
            """[{"op":"replace","path":"/spec/template/spec/securityContext/supplementalGroups","value":[993]}]""");

        Pod(patched).SecurityContext!.SupplementalGroups.Should().Equal(new long?[] { 993 });
        Revalidator.Validate(spec, patched).Should().BeEmpty();
    }

    [Fact]
    public void Patch_removing_the_supplemental_groups_is_not_a_violation()
    {
        var spec = Spec(privileges: new ResolvedPrivileges { DeviceGroupIds = new List<int> { 993 } });
        var patched = Applier.Apply(Render(spec),
            """[{"op":"remove","path":"/spec/template/spec/securityContext/supplementalGroups"}]""");

        Pod(patched).SecurityContext!.SupplementalGroups.Should().BeNullOrEmpty();
        Revalidator.Validate(spec, patched).Should().BeEmpty();
    }

    [Fact]
    public void Patch_smuggling_a_null_supplemental_group_entry_is_a_violation()
    {
        var spec = Spec(privileges: new ResolvedPrivileges { DeviceGroupIds = new List<int> { 993 } });
        var patched = Applier.Apply(Render(spec),
            """[{"op":"replace","path":"/spec/template/spec/securityContext/supplementalGroups","value":[993,null]}]""");

        Pod(patched).SecurityContext!.SupplementalGroups.Should().Equal(new long?[] { 993, null });
        Revalidator.Validate(spec, patched).Should()
            .ContainSingle(v => v.Contains("supplementalGroups=null", StringComparison.Ordinal));
    }

    [Fact]
    public void Patch_smuggling_a_null_supplemental_group_is_flagged_even_when_group_zero_is_declared()
    {
        var spec = Spec(privileges: new ResolvedPrivileges { DeviceGroupIds = new List<int> { 0 } });
        var patched = Applier.Apply(Render(spec),
            """[{"op":"replace","path":"/spec/template/spec/securityContext/supplementalGroups","value":[0,null]}]""");

        Revalidator.Validate(spec, patched).Should()
            .ContainSingle(v => v.Contains("supplementalGroups=null", StringComparison.Ordinal));
    }
}
