using Reeve.Workloads.Abstractions.Providers;
using System;
using System.Collections.Generic;
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

public sealed class KubernetesPatchRevalidatorAttachmentTests
{
    private const string InstanceIdStr = "11111111-2222-3333-4444-555555555555";
    private const string Digest = "sha256:1111111111111111111111111111111111111111111111111111111111111111";

    private const string NetworksKey = "k8s.v1.cni.cncf.io/networks";

    private const string AddNetworksPatch =
        """[{"op":"add","path":"/spec/template/metadata/annotations","value":{"k8s.v1.cni.cncf.io/networks":"reeve-vlan100"}}]""";

    private const string ReplaceNetworksPatch =
        """[{"op":"replace","path":"/spec/template/metadata/annotations","value":{"k8s.v1.cni.cncf.io/networks":"someone-elses-nad"}}]""";

    private const string RemoveDnsPolicyPatch = """[{"op":"remove","path":"/spec/template/spec/dnsPolicy"}]""";

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

    private static WorkloadDesiredSpec ClusterModeSpec() => Spec();

    private static WorkloadDesiredSpec HostNetworkSpec() => Spec(network: new ResolvedNetwork { HostNetwork = true });

    private static IReadOnlyList<string> ValidatePatched(WorkloadDesiredSpec spec, string patchJson)
        => Revalidator.Validate(spec, Applier.Apply(Render(spec), patchJson));

    private static string SetDnsPolicyPatch(string value)
        => $$"""[{"op":"add","path":"/spec/template/spec/dnsPolicy","value":"{{value}}"}]""";

    private static RenderedKubernetesObjects AlterAnnotationOn(RenderedKubernetesObjects rendered)
        => Applier.Apply(Applier.Apply(rendered, AddNetworksPatch), ReplaceNetworksPatch);

    private static IDictionary<string, string>? PodTemplateAnnotations(RenderedKubernetesObjects r)
        => r.Deployment is not null
            ? r.Deployment.Spec.Template.Metadata.Annotations
            : r.DaemonSet!.Spec.Template.Metadata.Annotations;

    [Fact]
    public void A_patch_may_not_forge_strip_or_alter_the_attachment_annotation()
    {
        var cluster = ClusterModeSpec();
        var host = HostNetworkSpec();

        ValidatePatched(cluster, AddNetworksPatch)
            .Should().ContainSingle().Which.Should().Contain(NetworksKey);

        Revalidator.Validate(cluster, AlterAnnotationOn(Render(cluster)))
            .Should().ContainSingle().Which.Should().Contain(NetworksKey);

        ValidatePatched(cluster, SetDnsPolicyPatch("ClusterFirstWithHostNet"))
            .Should().ContainSingle().Which.Should().Contain("dnsPolicy");

        ValidatePatched(host, RemoveDnsPolicyPatch)
            .Should().ContainSingle().Which.Should().Contain("dnsPolicy");

        ValidatePatched(cluster, """[{"op":"add","path":"/spec/template/metadata/annotations/team","value":"iot"}]""")
            .Should().BeEmpty();
    }

    [Fact]
    public void Adding_the_networks_annotation_to_a_cluster_mode_workload_is_rejected()
    {
        var spec = ClusterModeSpec();
        var patched = Applier.Apply(Render(spec), AddNetworksPatch);

        PodTemplateAnnotations(patched).Should().ContainKey(NetworksKey);

        Revalidator.Validate(spec, patched).Should().ContainSingle().Which.Should().Contain(NetworksKey);
    }

    [Fact]
    public void Adding_the_networks_annotation_alongside_the_renderers_own_annotations_is_rejected()
    {
        var spec = ClusterModeSpec();

        var violations = ValidatePatched(
            spec,
            """
            [{"op":"add","path":"/spec/template/metadata/annotations","value":{
              "reeve-iot.dev/spec-hash":"abc123def456",
              "k8s.v1.cni.cncf.io/networks":"reeve-vlan100"
            }}]
            """);

        violations.Should().ContainSingle().Which.Should().Contain(NetworksKey);
    }

    [Fact]
    public void Adding_the_networks_annotation_through_its_escaped_member_pointer_is_rejected()
    {
        var spec = ClusterModeSpec();
        var patched = Applier.Apply(
            Render(spec),
            """[{"op":"add","path":"/spec/template/metadata/annotations/k8s.v1.cni.cncf.io~1networks","value":"reeve-vlan100"}]""");

        PodTemplateAnnotations(patched).Should().ContainKey(NetworksKey)
            .WhoseValue.Should().Be("reeve-vlan100");

        Revalidator.Validate(spec, patched).Should().ContainSingle().Which.Should().Contain(NetworksKey);
    }

    [Fact]
    public void Replacing_the_networks_annotation_value_is_rejected()
    {
        var spec = ClusterModeSpec();

        var patched = AlterAnnotationOn(Render(spec));

        PodTemplateAnnotations(patched).Should().ContainKey(NetworksKey)
            .WhoseValue.Should().Be("someone-elses-nad");

        Revalidator.Validate(spec, patched).Should().ContainSingle().Which.Should().Contain(NetworksKey);
    }

    [Theory]
    [InlineData("ClusterFirstWithHostNet")]
    [InlineData("None")]
    [InlineData("Default")]
    public void Overriding_dnsPolicy_beyond_what_the_typed_spec_declares_is_rejected(string dnsPolicy)
    {
        var spec = ClusterModeSpec();

        var violations = ValidatePatched(spec, SetDnsPolicyPatch(dnsPolicy));

        violations.Should().ContainSingle().Which.Should().Contain("dnsPolicy");
    }

    [Fact]
    public void An_explicit_ClusterFirst_dnsPolicy_is_accepted_on_a_non_hostNetwork_workload()
    {
        var spec = ClusterModeSpec();

        ValidatePatched(spec, SetDnsPolicyPatch("ClusterFirst")).Should().BeEmpty();
    }

    [Fact]
    public void An_absent_dnsPolicy_is_accepted_on_a_non_hostNetwork_workload()
    {
        var spec = ClusterModeSpec();
        var rendered = Render(spec);

        Pod(rendered).DnsPolicy.Should().BeNull();

        Revalidator.Validate(spec, rendered).Should().BeEmpty();
    }

    [Fact]
    public void The_renderer_declared_ClusterFirstWithHostNet_is_accepted_on_a_hostNetwork_workload()
    {
        var spec = HostNetworkSpec();
        var rendered = Render(spec);

        Pod(rendered).DnsPolicy.Should().Be("ClusterFirstWithHostNet");

        Revalidator.Validate(spec, rendered).Should().BeEmpty();
    }

    [Fact]
    public void Removing_the_declared_dnsPolicy_from_a_hostNetwork_workload_is_rejected()
    {
        var spec = HostNetworkSpec();

        var violations = ValidatePatched(spec, RemoveDnsPolicyPatch);

        violations.Should().ContainSingle().Which.Should().Contain("dnsPolicy");
    }

    [Fact]
    public void Replacing_the_declared_dnsPolicy_on_a_hostNetwork_workload_is_rejected()
    {
        var spec = HostNetworkSpec();

        var violations = ValidatePatched(
            spec,
            """[{"op":"replace","path":"/spec/template/spec/dnsPolicy","value":"ClusterFirst"}]""");

        violations.Should().ContainSingle().Which.Should().Contain("dnsPolicy");
    }

    [Fact]
    public void Unrelated_pod_template_annotations_are_still_patchable()
    {
        var spec = ClusterModeSpec();
        var patched = Applier.Apply(Render(spec), """
            [{"op":"replace","path":"/spec/template/metadata/annotations","value":{
              "reeve-iot.dev/restart-generation":"7",
              "prometheus.io/scrape":"true"
            }}]
            """);

        var annotations = PodTemplateAnnotations(patched);
        annotations.Should().ContainKey("reeve-iot.dev/restart-generation");
        annotations.Should().ContainKey("prometheus.io/scrape");
        annotations.Should().NotContainKey("reeve-iot.dev/spec-hash");

        Revalidator.Validate(spec, patched).Should().BeEmpty();
    }

    [Fact]
    public void Removing_every_pod_template_annotation_is_allowed_when_none_was_the_attachment()
    {
        var spec = ClusterModeSpec();

        var violations = ValidatePatched(spec, """[{"op":"remove","path":"/spec/template/metadata/annotations"}]""");

        violations.Should().BeEmpty();
    }

    [Fact]
    public void The_annotation_guard_reads_the_DaemonSet_pod_template_too()
    {
        var spec = Spec(WorkloadKind.PerNode);
        var rendered = Render(spec);
        rendered.DaemonSet.Should().NotBeNull();
        rendered.Deployment.Should().BeNull();
        Revalidator.Validate(spec, rendered).Should().BeEmpty("the unpatched DaemonSet render is clean");

        var violations = Revalidator.Validate(spec, Applier.Apply(rendered, AddNetworksPatch));

        violations.Should().ContainSingle().Which.Should().Contain(NetworksKey);
    }

    private static WorkloadDesiredSpec OperatorNetworkSpec() => Spec(network: new ResolvedNetwork
    {
        Mode = WorkloadNetworkMode.OperatorNetwork,
        HostNetwork = false,
        Attachments = new[] { "iot-macvlan" },
    });

    [Fact]
    public void Removing_the_networks_annotation_from_an_operatorNetwork_workload_is_rejected()
    {
        var spec = OperatorNetworkSpec();
        var rendered = Render(spec);

        PodTemplateAnnotations(rendered).Should().ContainKey(NetworksKey);
        Revalidator.Validate(spec, rendered).Should().BeEmpty("the unpatched operatorNetwork render is clean");

        var patched = Applier.Apply(rendered, """[{"op":"remove","path":"/spec/template/metadata/annotations"}]""");

        Revalidator.Validate(spec, patched)
            .Should().ContainSingle().Which.Should().Contain(NetworksKey);
    }

    [Fact]
    public void Replacing_the_annotation_map_without_the_networks_key_on_an_operatorNetwork_workload_is_rejected()
    {
        var spec = OperatorNetworkSpec();

        var violations = ValidatePatched(spec, """
            [{"op":"replace","path":"/spec/template/metadata/annotations","value":{
              "reeve-iot.dev/spec-hash":"abc123def456"
            }}]
            """);

        violations.Should().ContainSingle().Which.Should().Contain(NetworksKey);
    }

    [Fact]
    public void Altering_the_networks_annotation_of_an_operatorNetwork_workload_to_another_nad_is_rejected()
    {
        var spec = OperatorNetworkSpec();

        var violations = ValidatePatched(
            spec,
            """[{"op":"replace","path":"/spec/template/metadata/annotations","value":{"k8s.v1.cni.cncf.io/networks":"[{\"name\":\"someone-elses-nad\",\"namespace\":\"kube-system\"}]"}}]""");

        violations.Should().ContainSingle().Which.Should().Contain(NetworksKey);
    }

    [Fact]
    public void The_rendered_operatorNetwork_annotation_is_accepted_unchanged()
    {
        var spec = OperatorNetworkSpec();

        ValidatePatched(spec, """[{"op":"add","path":"/spec/template/metadata/annotations/team","value":"iot"}]""")
            .Should().BeEmpty();
    }
}
