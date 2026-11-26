using System.Collections.Generic;
using FluentAssertions;
using static Reeve.Workloads.Core.Tests.Rendering.ArtifactSpecFactory;
using Reeve.Workloads.Abstractions.Contracts.Deployment;
using Reeve.Workloads.Abstractions.Domain;
using Reeve.Workloads.Abstractions.Domain.DesiredSpec;
using Reeve.Workloads.Abstractions.Rendering;
using Reeve.Workloads.Providers.Kubernetes;
using Reeve.Workloads.Providers.Kubernetes.Rendering;
using Xunit;

namespace Reeve.Workloads.Core.Tests.Rendering;

public sealed class KubernetesArtifactRendererTests
{
    private static readonly KubernetesArtifactRenderer Renderer = new();

    private static WorkloadArtifactRenderOptions Reveal(string token = "rwt_secrettoken123", bool external = false) =>
        external ? new() { ExternalSecretMode = true } : Revealing(token);

    private static WorkloadArtifactRenderOptions External() =>
        new() { ExternalSecretMode = true };

    [Fact]
    public void Format_is_kubernetes()
    {
        Renderer.Format.Should().Be(WorkloadArtifactFormatId.Parse("kubernetes"));
        Renderer.IsSelfContained.Should().BeTrue();
        Renderer.Render(Spec(), External()).Format.Should().Be(WorkloadArtifactFormatId.Parse("kubernetes"));
    }

    [Fact]
    public void Manifest_contains_the_deployment_and_references_the_named_secret_via_secret_key_ref()
    {
        var content = Renderer.Render(Spec(), Reveal("rwt_supersecret")).PrimaryContent;

        content.Should().Contain("kind: Deployment");
        content.Should().Contain($"image: ghcr.io/reeve/matter@{Digest}");
        content.Should().Contain("secretKeyRef");
        content.Should().Contain(ResourceName);
    }

    [Fact]
    public void CRITICAL_the_token_is_never_inlined_in_the_manifest_even_when_revealed()
    {
        var result = Renderer.Render(Spec(), Reveal("rwt_supersecret"));

        result.PrimaryContent.Should().NotContain("rwt_supersecret");
        result.PrimaryContent.Should().NotContain("stringData");
    }

    [Fact]
    public void CRITICAL_the_revealed_token_lives_only_in_the_kubectl_create_secret_one_liner()
    {
        var result = Renderer.Render(Spec(), Reveal("rwt_supersecret"));

        result.SecretOneLiner.Should().NotBeNull();
        result.SecretOneLiner.Should().Contain("kubectl create secret generic");
        result.SecretOneLiner.Should().Contain(ResourceName);
        result.SecretOneLiner.Should().Contain("--from-literal='REEVE_WORKLOAD_TOKEN=rwt_supersecret'");
        result.SecretOneLiner.Should().Contain("-n reeve");
    }

    [Fact]
    public void External_secret_mode_emits_no_one_liner_and_only_name_key_guidance()
    {
        var result = Renderer.Render(Spec(), External());

        result.SecretOneLiner.Should().BeNull();
        result.PrimaryContent.Should().NotContain("rwt_");
        result.Notes.Should().Contain(n => n.Contains(ResourceName) && n.Contains("REEVE_WORKLOAD_TOKEN"));
    }

    private static WorkloadDesiredSpec CredentialSpec() => Spec(
        env: [.. DefaultEnv(),
            new ResolvedEnvVar { Name = "FOO_TOKEN", Kind = ResolvedEnvKind.SecretRef, SecretRef = ResourceName, CredentialId = "foo" },
            new ResolvedEnvVar { Name = "MQTT_PASSWORD", Kind = ResolvedEnvKind.SecretRef, SecretRef = ResourceName }],
        provisioned:
        [
            new ProvisionedCredential(TokenCredentialId, "REEVE_WORKLOAD_TOKEN"),
            new ProvisionedCredential("side", "SIDE_TOKEN"),
        ]) with
    {
        SecretMaterial = new Dictionary<string, string> { ["MQTT_PASSWORD"] = "broker-secret" },
    };

    [Fact]
    public void The_one_liner_carries_every_credential_key_provisioned_ones_included_and_no_password()
    {
        var options = new WorkloadArtifactRenderOptions
        {
            RevealedCredentials = new Dictionary<string, string>
            {
                [TokenCredentialId] = "rwt_a",
                ["foo"] = "foo-b",
                ["side"] = "side-c",
            },
        };

        var result = Renderer.Render(CredentialSpec(), options);

        result.SecretOneLiner.Should().Be(
            $"kubectl create secret generic {ResourceName} "
            + "--from-literal='FOO_TOKEN=foo-b' --from-literal='REEVE_WORKLOAD_TOKEN=rwt_a' --from-literal='SIDE_TOKEN=side-c' "
            + "-n reeve");
        Everything(result).Should().NotContain("broker-secret").And.NotContain("MQTT_PASSWORD=");
    }

    [Fact]
    public void The_one_liner_single_quotes_each_literal_so_no_credential_reaches_the_shell()
    {
        var result = Renderer.Render(Spec(), Revealing("it's a $(reboot) value"));

        result.SecretOneLiner.Should().Be(
            $"kubectl create secret generic {ResourceName} --from-literal='REEVE_WORKLOAD_TOKEN=it'\\''s a $(reboot) value' -n reeve");
    }

    [Fact]
    public void A_provisioned_name_a_declared_literal_env_fills_is_left_out_of_the_one_liner_and_the_notes()
    {
        var spec = Spec(env: [new ResolvedEnvVar { Name = "REEVE_WORKLOAD_TOKEN", Kind = ResolvedEnvKind.Literal, Value = "author-wrote-this" }]);

        var result = Renderer.Render(spec, Revealing("rwt_a"));

        result.SecretOneLiner.Should().BeNull();
        Everything(result).Should().NotContain("rwt_a").And.NotContain("'REEVE_WORKLOAD_TOKEN'");
    }

    [Fact]
    public void A_name_two_credentials_claim_is_left_out_of_the_one_liner()
    {
        var spec = Spec(
            env: [new ResolvedEnvVar { Name = "REEVE_WORKLOAD_TOKEN", Kind = ResolvedEnvKind.SecretRef, SecretRef = ResourceName, CredentialId = "other" }],
            provisioned: [new ProvisionedCredential(TokenCredentialId, "REEVE_WORKLOAD_TOKEN")]);
        var options = new WorkloadArtifactRenderOptions
        {
            RevealedCredentials = new Dictionary<string, string> { [TokenCredentialId] = "rwt_a", ["other"] = "rwt_b" },
        };

        var result = Renderer.Render(spec, options);

        result.SecretOneLiner.Should().BeNull();
        Everything(result).Should().NotContain("rwt_a").And.NotContain("rwt_b");
    }

    [Fact]
    public void A_credential_with_no_revealed_value_is_left_out_of_the_one_liner_and_named_in_a_note()
    {
        var result = Renderer.Render(CredentialSpec(), Revealing("rwt_a"));

        result.SecretOneLiner.Should().Be(
            $"kubectl create secret generic {ResourceName} --from-literal='REEVE_WORKLOAD_TOKEN=rwt_a' -n reeve");
        result.SecretOneLiner.Should().NotContain(Reeve.Workloads.Core.Rendering.WorkloadEnvRendering.SecretPlaceholder);
        result.Notes.Should().Contain(n => n.Contains("FOO_TOKEN") && n.Contains("SIDE_TOKEN"));
    }

    [Fact]
    public void External_secret_mode_names_every_credential_key_and_no_password()
    {
        var result = Renderer.Render(CredentialSpec(), External());

        result.SecretOneLiner.Should().BeNull();
        result.Notes.Should().Contain(n => n.Contains(ResourceName)
            && n.Contains("FOO_TOKEN") && n.Contains("REEVE_WORKLOAD_TOKEN") && n.Contains("SIDE_TOKEN")
            && !n.Contains("MQTT_PASSWORD"));
    }

    [Fact]
    public void Per_node_kind_renders_a_daemonset()
    {
        var content = Renderer.Render(Spec(kind: WorkloadKind.PerNode), Reveal()).PrimaryContent;

        content.Should().Contain("kind: DaemonSet");
        content.Should().NotContain("kind: Deployment");
    }

    [Fact]
    public void Ports_render_a_service_document()
    {
        var content = Renderer.Render(Spec(network: new ResolvedNetwork
        {
            Ports = new[] { new ResolvedPort { Name = "api", Port = 8080, Protocol = "tcp" } },
        }), Reveal()).PrimaryContent;

        content.Should().Contain("kind: Service");
    }

    [Fact]
    public void Volumes_render_pvc_documents()
    {
        var content = Renderer.Render(Spec(volumes: new[]
        {
            new ResolvedVolume
            {
                Id = "data", MountPath = "/data", Size = "1Gi",
                Criticality = VolumeCriticality.Critical, ResourceName = "reeve-wl-pvc-data",
            },
        }), Reveal()).PrimaryContent;

        content.Should().Contain("kind: PersistentVolumeClaim");
        content.Should().Contain("reeve-wl-pvc-data");
    }

    [Fact]
    public void NEW_C_a_adopted_claim_volume_omits_its_pvc_document_and_warns_it_must_already_exist()
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
        }), Reveal());

        result.PrimaryContent.Should().NotContain("kind: PersistentVolumeClaim");
        result.PrimaryContent.Should().Contain("claimName: reeve-wl-pvc-retained-state");
        result.Notes.Should().Contain(n =>
            n.Contains("reeve-wl-pvc-retained-state") && n.Contains("must already exist"));
    }

    [Fact]
    public void NEW_C_a_a_fresh_volume_alongside_an_adopted_claim_still_emits_only_the_fresh_pvc()
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
        }), Reveal());

        System.Text.RegularExpressions.Regex.Matches(result.PrimaryContent, "kind: PersistentVolumeClaim")
            .Count.Should().Be(1, "only the fresh (non-adopted) volume yields a create-able PVC document");
        result.PrimaryContent.Should().Contain("name: reeve-wl-pvc-fresh-cache");
        result.PrimaryContent.Should().NotContain("name: reeve-wl-pvc-retained-state");
        result.PrimaryContent.Should().Contain("claimName: reeve-wl-pvc-retained-state");
        result.Notes.Should().Contain(n =>
            n.Contains("reeve-wl-pvc-retained-state") && n.Contains("must already exist"));
    }

    [Fact]
    public void Documents_are_separated_by_yaml_document_markers()
    {
        var content = Renderer.Render(Spec(network: new ResolvedNetwork
        {
            Ports = new[] { new ResolvedPort { Name = "api", Port = 8080, Protocol = "tcp" } },
        }), Reveal()).PrimaryContent;

        content.Should().Contain("---");
    }

    [Fact]
    public void A_ref_at_digest_pull_path_caveat_note_is_present()
    {
        Renderer.Render(Spec(), Reveal()).Notes
            .Should().Contain(n => n.Contains("out-of-band") || n.Contains("pull"));
    }

    [Fact]
    public void Golden_kubernetes_manifest_has_no_token_and_the_one_liner_carries_it()
    {
        var result = Renderer.Render(Spec(), Reveal("rwt_goldentoken"));

        result.PrimaryContent.Should().Be(ArtifactGoldens.KubernetesManifest);
        result.PrimaryContent.Should().NotContain("rwt_goldentoken");
        result.SecretOneLiner.Should().Be(
            "kubectl create secret generic reeve-wl-com-reeve-matter-matter-11111111 "
            + "--from-literal='REEVE_WORKLOAD_TOKEN=rwt_goldentoken' -n reeve");
    }

    [Fact]
    public void I1_the_unsupported_pod_patch_is_applied_to_the_rendered_manifest()
    {
        var spec = Spec(unsupportedPodPatch: new UnsupportedPodPatchSpec
        {
            PatchJson =
                """[{"op":"add","path":"/spec/template/spec/hostname","value":"matter-sidecar"}]""",
        });

        var content = Renderer.Render(spec, Reveal("rwt_supersecret")).PrimaryContent;

        content.Should().Contain("hostname: matter-sidecar");
    }

    [Fact]
    public void I1_the_pod_patch_applies_to_the_daemonset_for_per_node_workloads()
    {
        var spec = Spec(kind: WorkloadKind.PerNode, unsupportedPodPatch: new UnsupportedPodPatchSpec
        {
            PatchJson =
                """[{"op":"add","path":"/spec/template/spec/hostname","value":"ble-sidecar"}]""",
        });

        var content = Renderer.Render(spec, Reveal()).PrimaryContent;

        content.Should().Contain("kind: DaemonSet");
        content.Should().Contain("hostname: ble-sidecar");
    }

    [Fact]
    public void I1_no_pod_patch_leaves_the_manifest_unchanged()
    {
        var result = Renderer.Render(Spec(), Reveal("rwt_goldentoken"));

        result.PrimaryContent.Should().Be(ArtifactGoldens.KubernetesManifest);
    }

    [Fact]
    public void I1_golden_manifest_with_applied_pod_patch_still_omits_the_token()
    {
        var spec = Spec(unsupportedPodPatch: new UnsupportedPodPatchSpec
        {
            PatchJson =
                """[{"op":"add","path":"/spec/template/spec/hostname","value":"matter-sidecar"}]""",
        });

        var result = Renderer.Render(spec, Reveal("rwt_goldentoken"));

        result.PrimaryContent.Should().Be(ArtifactGoldens.KubernetesManifestWithPodPatch);
        result.PrimaryContent.Should().NotContain("rwt_goldentoken");
        result.Notes.Should().Contain(n => n.Contains("unsupportedPodPatch"));
    }

    [Fact]
    public void M5_a_patch_that_escalates_beyond_consent_emits_a_warning_note()
    {
        var spec = Spec(unsupportedPodPatch: new UnsupportedPodPatchSpec
        {
            PatchJson =
                """[{"op":"add","path":"/spec/template/spec/hostNetwork","value":true}]""",
        });

        var result = Renderer.Render(spec, External());

        result.PrimaryContent.Should().Contain("hostNetwork: true");
        result.Notes.Should().Contain(n =>
            n.Contains("WARNING") && n.Contains("hostNetwork"),
            "the manual path surfaces the revalidator's violations as an informational warning (parity)");
    }

    [Fact]
    public void M5_a_within_consent_patch_emits_no_revalidation_warning()
    {
        var spec = Spec(unsupportedPodPatch: new UnsupportedPodPatchSpec
        {
            PatchJson =
                """[{"op":"add","path":"/spec/template/spec/hostname","value":"matter-sidecar"}]""",
        });

        var result = Renderer.Render(spec, External());

        result.Notes.Should().NotContain(n => n.Contains("WARNING"));
    }

    [Fact]
    public void R30_the_manual_manifest_inherits_the_multus_annotation_from_the_shared_kubernetes_renderer()
    {
        var content = Renderer.Render(Spec(network: OperatorNetwork("iot-vlan100", "alpha-net")), External())
            .PrimaryContent;

        content.Should().Contain(KubernetesWorkloadRenderer.NetworksAnnotationKey);
        content.Should().Contain(
            """{"name":"iot-vlan100","namespace":"reeve"},{"name":"alpha-net","namespace":"reeve"}""");
        content.Should().Contain("dnsPolicy: ClusterFirst");
    }

    [Fact]
    public void R30_a_cluster_mode_workload_carries_no_multus_annotation()
    {
        var content = Renderer.Render(Spec(), External()).PrimaryContent;

        content.Should().NotContain(KubernetesWorkloadRenderer.NetworksAnnotationKey);
    }

    [Fact]
    public void R30_a_manual_artifact_never_carries_the_isolation_or_lateral_reach_copy_even_for_driver_named_networks()
    {
        var everything = Everything(
            Renderer.Render(Spec(network: OperatorNetwork("iot-macvlan", "lab-bridge")), External()));

        everything.Should().NotContain(IsolationCopy);
        everything.Should().NotContain(LateralReachCopy);
    }
}
