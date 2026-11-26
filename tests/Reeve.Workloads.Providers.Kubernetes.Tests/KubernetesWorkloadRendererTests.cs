using Reeve.Workloads.Abstractions.Providers;
using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using k8s.Models;
using Reeve.Workloads.Abstractions.Contracts;
using Reeve.Workloads.Abstractions.Contracts.Deployment;
using Reeve.Workloads.Abstractions.Domain;
using Reeve.Workloads.Abstractions.Domain.DesiredSpec;
using Reeve.Workloads.Domain;
using Reeve.Workloads.Domain.DesiredSpec;
using Reeve.Workloads.Providers.Kubernetes;
using Xunit;

namespace Reeve.Workloads.Providers.Kubernetes.Tests;

public sealed class KubernetesWorkloadRendererTests
{
    private const string InstanceIdStr = "11111111-2222-3333-4444-555555555555";
    private const string Digest = "sha256:1111111111111111111111111111111111111111111111111111111111111111";

    private static readonly KubernetesRenderContext Ctx = new() { Namespace = "reeve" };

    private static WorkloadDesiredSpec Spec(
        WorkloadKind kind = WorkloadKind.SingleNode,
        ResolvedNetwork? network = null,
        ResolvedPrivileges? privileges = null,
        IReadOnlyList<ResolvedVolume>? volumes = null,
        IReadOnlyList<ResolvedConfigMapMount>? configMaps = null,
        IReadOnlyList<ResolvedEnvVar>? env = null,
        IReadOnlyDictionary<string, string>? nodeSelector = null,
        IReadOnlyList<string>? platforms = null,
        ResolvedResources? resources = null,
        WorkloadObservability? observability = null)
    {
        const string specHash = "abc123def456";
        var instanceId = System.Guid.Parse(InstanceIdStr);
        var labels = DefaultWorkloadResourceNamer.Instance.Labels(new WorkloadLabelSubject
                     {
                         InstanceId = instanceId,
                         InstallationId = "install-1",
                         SpecHash = specHash,
                     });

        return new WorkloadDesiredSpec
        {
            GroupInstanceId = System.Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee"),
            GroupId = "com.reeve.matter",
            WorkloadId = "matter",
            InstanceId = instanceId,
            Inst8 = "11111111",
            Provider = KubernetesProvider.Id,
            ResourceName = "reeve-wl-com-reeve-matter-matter-11111111",
            SecretName = "reeve-wl-com-reeve-matter-matter-11111111",
            Labels = labels,
            Image = $"ghcr.io/reeve/matter@{Digest}",
            Platforms = platforms ?? new List<string>(),
            Kind = kind,
            NodeSelector = nodeSelector ?? new Dictionary<string, string>(),
            Resources = resources,
            Network = network,
            Privileges = privileges,
            Volumes = volumes ?? new List<ResolvedVolume>(),
            ConfigMapMounts = configMaps ?? new List<ResolvedConfigMapMount>(),
            Health = new WorkloadHealth { Mode = WorkloadHealthMode.Container },
            Observability = observability,
            Env = env ?? new List<ResolvedEnvVar>(),
            SpecHash = specHash,
        };
    }

    private static readonly KubernetesWorkloadRenderer Renderer = new();

    private static V1Container Container(RenderedKubernetesObjects r)
    {
        var pod = PodSpec(r);
        return pod.Containers.Single();
    }

    private static V1PodSpec PodSpec(RenderedKubernetesObjects r)
        => r.Deployment?.Spec.Template.Spec ?? r.DaemonSet!.Spec.Template.Spec;

    private static V1ObjectMeta PodMeta(RenderedKubernetesObjects r)
        => r.Deployment?.Spec.Template.Metadata ?? r.DaemonSet!.Spec.Template.Metadata;


    [Fact]
    public void Service_kind_renders_a_recreate_deployment_with_one_replica()
    {
        var r = Renderer.Render(Spec(kind: WorkloadKind.Service), Ctx);

        r.Deployment.Should().NotBeNull();
        r.DaemonSet.Should().BeNull();
        r.Deployment!.Spec.Replicas.Should().Be(1);
        r.Deployment.Spec.Strategy.Type.Should().Be("Recreate");
    }

    [Fact]
    public void SingleNode_kind_renders_a_recreate_deployment()
    {
        var r = Renderer.Render(Spec(kind: WorkloadKind.SingleNode), Ctx);

        r.Deployment.Should().NotBeNull();
        r.Deployment!.Spec.Strategy.Type.Should().Be("Recreate");
        r.Deployment.Spec.Replicas.Should().Be(1);
    }

    [Fact]
    public void PerNode_kind_renders_a_daemonset_with_no_deployment()
    {
        var r = Renderer.Render(Spec(kind: WorkloadKind.PerNode), Ctx);

        r.DaemonSet.Should().NotBeNull();
        r.Deployment.Should().BeNull();
    }


    [Fact]
    public void PerNode_daemonset_injects_NODE_NAME_from_the_downward_api()
    {
        var r = Renderer.Render(Spec(kind: WorkloadKind.PerNode), Ctx);

        var nodeNameEnv = Container(r).Env.Single(e => e.Name == "NODE_NAME");
        nodeNameEnv.Value.Should().BeNull("it is a downward-API valueFrom, not a literal");
        nodeNameEnv.ValueFrom.Should().NotBeNull();
        nodeNameEnv.ValueFrom.FieldRef.Should().NotBeNull();
        nodeNameEnv.ValueFrom.FieldRef.FieldPath.Should().Be("spec.nodeName");
    }

    [Fact]
    public void Non_per_node_workloads_do_not_get_NODE_NAME_injected()
    {
        var svc = Renderer.Render(Spec(kind: WorkloadKind.Service), Ctx);
        var single = Renderer.Render(Spec(kind: WorkloadKind.SingleNode), Ctx);

        Container(svc).Env.Should().NotContain(e => e.Name == "NODE_NAME");
        Container(single).Env.Should().NotContain(e => e.Name == "NODE_NAME");
    }

    [Fact]
    public void PerNode_NODE_NAME_injection_does_not_move_when_workload_already_declares_it()
    {
        var declared = new List<ResolvedEnvVar>
        {
            new() { Name = "NODE_NAME", Kind = ResolvedEnvKind.Literal, Value = "explicit" },
        };

        var r = Renderer.Render(Spec(kind: WorkloadKind.PerNode, env: declared), Ctx);

        var nodeNameEnvs = Container(r).Env.Where(e => e.Name == "NODE_NAME").ToList();
        nodeNameEnvs.Should().ContainSingle("a workload-declared NODE_NAME must not be duplicated by the injector");
        nodeNameEnvs.Single().Value.Should().Be("explicit");
    }


    [Fact]
    public void A_repeated_env_name_renders_one_pod_env_entry_each_in_order()
    {
        var env = new List<ResolvedEnvVar>
        {
            new() { Name = "FEED_URL", Kind = ResolvedEnvKind.Literal, Value = "first" },
            new() { Name = "FEED_URL", Kind = ResolvedEnvKind.Literal, Value = "second" },
        };

        var r = Renderer.Render(Spec(kind: WorkloadKind.Service, env: env), Ctx);

        Container(r).Env.Where(e => e.Name == "FEED_URL").Select(e => e.Value).Should().Equal("first", "second");
    }

    private static V1PodSpec JobPodSpec(V1Job job) => job.Spec.Template.Spec;

    private static V1Container JobContainer(V1Job job) => JobPodSpec(job).Containers.Single();

    [Fact]
    public void ScanPerNode_renders_exactly_one_job_per_distinct_ready_node()
    {
        var r = Renderer.Render(
            Spec(kind: WorkloadKind.ScanPerNode),
            Ctx,
            readyNodeNames: new[] { "node-a", "node-b", "node-c" },
            scanRunId: "run-1");

        r.Jobs.Should().HaveCount(3);
        r.Deployment.Should().BeNull();
        r.DaemonSet.Should().BeNull();

        r.Jobs.Select(j => JobPodSpec(j).NodeName).Should().BeEquivalentTo(new[] { "node-a", "node-b", "node-c" });
        r.Jobs.Should().OnlyContain(j => j.ApiVersion == "batch/v1" && j.Kind == "Job");
    }

    [Fact]
    public void User_namespace_sets_hostUsers_false_only_when_enabled()
    {
        PodSpec(Renderer.Render(Spec(), Ctx)).HostUsers.Should().BeNull();
        PodSpec(Renderer.Render(Spec() with { UserNamespace = true }, Ctx)).HostUsers.Should().BeFalse();
    }

    [Fact]
    public void A_user_namespace_renders_hostUsers_false_and_nothing_else_changes()
    {
        var plain = PodSpec(Renderer.Render(Spec(), Ctx));
        var withUserNs = PodSpec(Renderer.Render(Spec() with { UserNamespace = true }, Ctx));

        plain.HostUsers.Should().BeNull();
        withUserNs.HostUsers.Should().BeFalse();
        withUserNs.Containers.Single().SecurityContext.Should().BeEquivalentTo(plain.Containers.Single().SecurityContext);
    }

    [Fact]
    public void A_skipped_host_networked_workload_renders_without_hostUsers_and_its_sibling_does()
    {
        var hostNetworked = Spec(network: new ResolvedNetwork { HostNetwork = true, Mode = WorkloadNetworkMode.HostNetwork })
            with { UserNamespaceSkippedForHostNetwork = true };
        var sibling = Spec() with { UserNamespace = true };

        var hostPod = PodSpec(Renderer.Render(hostNetworked, Ctx));
        hostPod.HostNetwork.Should().BeTrue();
        hostPod.HostUsers.Should().BeNull();
        PodSpec(Renderer.Render(sibling, Ctx)).HostUsers.Should().BeFalse();
    }

    [Fact]
    public void User_namespace_is_refused_with_host_network()
    {
        var spec = Spec(network: new ResolvedNetwork { HostNetwork = true, Mode = WorkloadNetworkMode.HostNetwork }) with { UserNamespace = true };
        var act = () => Renderer.Render(spec, Ctx);
        act.Should().Throw<System.InvalidOperationException>().WithMessage("*user namespace*hostNetwork*");
    }

    [Fact]
    public void Scan_jobs_carry_the_user_namespace()
    {
        var r = Renderer.Render(Spec(kind: WorkloadKind.ScanPerNode) with { UserNamespace = true }, Ctx,
            readyNodeNames: new[] { "node-a", "node-b" }, scanRunId: "run-1");
        r.Jobs.Should().HaveCount(2);
        r.Jobs.Should().OnlyContain(j => j.Spec.Template.Spec.HostUsers == false);
    }

    [Fact]
    public void ScanPerNode_with_no_ready_nodes_renders_zero_jobs()
    {
        var empty = Renderer.Render(Spec(kind: WorkloadKind.ScanPerNode), Ctx, readyNodeNames: System.Array.Empty<string>(), scanRunId: "run-1");
        empty.Jobs.Should().BeEmpty();
        empty.Deployment.Should().BeNull();
        empty.DaemonSet.Should().BeNull();

        var noArg = Renderer.Render(Spec(kind: WorkloadKind.ScanPerNode), Ctx);
        noArg.Jobs.Should().BeEmpty();
        noArg.Deployment.Should().BeNull();
        noArg.DaemonSet.Should().BeNull();
    }

    [Fact]
    public void ScanPerNode_job_spec_sets_the_bounded_run_once_fields()
    {
        var r = Renderer.Render(Spec(kind: WorkloadKind.ScanPerNode), Ctx, new[] { "node-a", "node-b" }, "run-1");

        foreach (var job in r.Jobs)
        {
            JobPodSpec(job).RestartPolicy.Should().Be("Never");
            job.Spec.BackoffLimit.Should().Be(0);
            job.Spec.Completions.Should().Be(1);
            job.Spec.Parallelism.Should().Be(1);
            job.Spec.ActiveDeadlineSeconds.Should().Be(300);
            job.Spec.TtlSecondsAfterFinished.Should().Be(300);
        }
    }

    [Fact]
    public void ScanPerNode_pod_carries_NODE_NAME_from_the_downward_api()
    {
        var r = Renderer.Render(Spec(kind: WorkloadKind.ScanPerNode), Ctx, new[] { "node-a" }, "run-1");

        var nodeNameEnv = JobContainer(r.Jobs.Single()).Env.Single(e => e.Name == "NODE_NAME");
        nodeNameEnv.Value.Should().BeNull("it is a downward-API valueFrom, not a literal");
        nodeNameEnv.ValueFrom.FieldRef.FieldPath.Should().Be("spec.nodeName");
    }

    [Fact]
    public void ScanPerNode_pod_security_surface_is_net_raw_plus_drop_all()
    {
        var r = Renderer.Render(
            Spec(kind: WorkloadKind.ScanPerNode, privileges: new ResolvedPrivileges { Capabilities = new[] { "NET_RAW" } }),
            Ctx,
            new[] { "node-a" },
            "run-1");

        var sc = JobContainer(r.Jobs.Single()).SecurityContext;
        sc.Capabilities.Add.Should().BeEquivalentTo(new[] { "NET_RAW" });
        sc.Capabilities.Drop.Should().Contain("ALL");
    }

    [Fact]
    public void ScanPerNode_host_path_is_read_only_with_a_tolerant_null_type()
    {
        var r = Renderer.Render(
            Spec(kind: WorkloadKind.ScanPerNode, privileges: new ResolvedPrivileges
            {
                HostPaths = new[] { new ResolvedHostPath { HostPath = "/dev/serial", MountPath = "/dev/serial" } },
            }),
            Ctx,
            new[] { "node-a" },
            "run-1");

        var pod = JobPodSpec(r.Jobs.Single());
        var vol = pod.Volumes.Single(v => v.HostPath?.Path == "/dev/serial");
        vol.HostPath.Type.Should().BeNull("a missing /dev/serial dir must not crash the pod");
        var mount = JobContainer(r.Jobs.Single()).VolumeMounts.Single(m => m.Name == vol.Name);
        mount.ReadOnlyProperty.Should().Be(true, "hostPaths[] is read-only by definition");
    }

    [Fact]
    public void ScanPerNode_job_names_are_dns_safe_distinct_and_carry_the_run_id()
    {
        var r = Renderer.Render(Spec(kind: WorkloadKind.ScanPerNode), Ctx, new[] { "node-a", "node-b" }, "run-42");

        var names = r.Jobs.Select(j => j.Metadata.Name).ToList();
        names.Should().OnlyHaveUniqueItems();
        names.Should().OnlyContain(n => n.Length <= WorkloadResourceNaming.Dns1123MaxLength);
        names.Should().OnlyContain(n => n.Contains("run-42"));

        var run1 = Renderer.Render(Spec(kind: WorkloadKind.ScanPerNode), Ctx, new[] { "node-a" }, "run-1").Jobs.Single().Metadata.Name;
        var run2 = Renderer.Render(Spec(kind: WorkloadKind.ScanPerNode), Ctx, new[] { "node-a" }, "run-2").Jobs.Single().Metadata.Name;
        run1.Should().NotBe(run2);
    }

    [Fact]
    public void ScanPerNode_job_labels_delegate_to_the_StampScanLabels_SSOT_over_the_full_spec_label_set()
    {
        var spec = Spec(kind: WorkloadKind.ScanPerNode);
        var longNode = new string('N', 80);
        var r = Renderer.Render(spec, Ctx, new[] { longNode }, "run-1");

        var job = r.Jobs.Single();
        var labels = job.Metadata.Labels;

        labels.Should().BeEquivalentTo(WorkloadResourceNaming.StampScanLabels(spec.Labels, longNode, "run-1"));

        foreach (var (key, value) in spec.Labels)
        {
            labels.Should().ContainKey(key).WhoseValue.Should().Be(value);
        }

        labels.Should().ContainKey(WorkloadResourceLabels.InstanceLabelKey);
        labels[WorkloadResourceLabels.ScanNodeLabelKey].Should()
            .Be(WorkloadResourceNaming.TruncateForDns(WorkloadResourceNaming.Sanitize(longNode)));
        labels[WorkloadResourceLabels.ScanNodeLabelKey].Should().MatchRegex("^[a-z0-9-]+$").And.NotEndWith("-");
        labels.Should().ContainKey(WorkloadResourceLabels.ScanRunLabelKey);
        labels.Values.Should().OnlyContain(v => v.Length <= WorkloadResourceNaming.Dns1123MaxLength);

        job.Spec.Template.Metadata.Labels.Should().BeEquivalentTo(labels);

        var run2 = Renderer.Render(spec, Ctx, new[] { longNode }, "run-2").Jobs.Single().Metadata.Labels;
        run2[WorkloadResourceLabels.ScanRunLabelKey].Should().NotBe(labels[WorkloadResourceLabels.ScanRunLabelKey]);
        run2[WorkloadResourceLabels.ScanNodeLabelKey].Should().Be(labels[WorkloadResourceLabels.ScanNodeLabelKey]);
    }

    [Fact]
    public void Existing_kinds_stay_unchanged_and_render_no_jobs()
    {
        Renderer.Render(Spec(kind: WorkloadKind.Service), Ctx).Jobs.Should().BeEmpty();
        Renderer.Render(Spec(kind: WorkloadKind.SingleNode), Ctx).Jobs.Should().BeEmpty();
        Renderer.Render(Spec(kind: WorkloadKind.PerNode), Ctx).Jobs.Should().BeEmpty();
    }


    [Fact]
    public void ScanPerNode_renderer_fabricates_no_gateway_env_beyond_the_node_name_injection()
    {
        var r = Renderer.Render(
            Spec(kind: WorkloadKind.ScanPerNode, env: new[]
            {
                new ResolvedEnvVar { Name = "REEVE_WORKLOAD_TOKEN", Kind = ResolvedEnvKind.SecretRef, SecretRef = "unused-legacy-ref" },
            }),
            Ctx,
            new[] { "node-a" },
            "run-1");

        var env = JobContainer(r.Jobs.Single()).Env;

        env.Should().NotContain(e => e.Name == "REEVE_GATEWAY_URL");
        env.Select(e => e.Name).Should().BeEquivalentTo(new[] { "REEVE_WORKLOAD_TOKEN", "NODE_NAME" });
    }

    [Fact]
    public void ScanPerNode_renderer_passes_a_manifest_declared_env_through_verbatim_without_stripping_by_kind()
    {
        var r = Renderer.Render(
            Spec(kind: WorkloadKind.ScanPerNode, env: new[]
            {
                new ResolvedEnvVar { Name = "REEVE_WORKLOAD_TOKEN", Kind = ResolvedEnvKind.SecretRef, SecretRef = "unused-legacy-ref" },
                new ResolvedEnvVar { Name = "SOME_MANIFEST_URL", Kind = ResolvedEnvKind.Literal, Value = "http://example.invalid" },
            }),
            Ctx,
            new[] { "node-a" },
            "run-1");

        var passthrough = JobContainer(r.Jobs.Single()).Env.Single(e => e.Name == "SOME_MANIFEST_URL");
        passthrough.Value.Should().Be("http://example.invalid",
            "a manifest-declared literal survives verbatim for the scan kind — the renderer would ALSO carry a gateway var if a manifest declared one, so it does not strip by kind");
        passthrough.ValueFrom.Should().BeNull();
    }

    [Theory]
    [InlineData("pa$$word", "pa$$$$word")]
    [InlineData("x$(REEVE_WORKLOAD_TOKEN)", "x$$(REEVE_WORKLOAD_TOKEN)")]
    [InlineData("price: 5$", "price: 5$$")]
    [InlineData("no dollar", "no dollar")]
    public void A_literal_env_value_is_escaped_so_the_kubelet_expansion_gives_back_the_literal(string literal, string rendered)
    {
        var r = Renderer.Render(
            Spec(env: new[]
            {
                new ResolvedEnvVar { Name = "REEVE_WORKLOAD_TOKEN", Kind = ResolvedEnvKind.SecretRef, SecretRef = "unused-legacy-ref" },
                new ResolvedEnvVar { Name = "PASSWORD", Kind = ResolvedEnvKind.Literal, Value = literal },
            }),
            Ctx);

        Container(r).Env.Single(e => e.Name == "PASSWORD").Value.Should().Be(rendered);
    }

    [Fact]
    public void ScanPerNode_self_rest_env_injects_NODE_NAME_from_the_downward_api()
    {
        var spec = Spec(kind: WorkloadKind.ScanPerNode, env: new[]
        {
            new ResolvedEnvVar { Name = "REEVE_WORKLOAD_TOKEN", Kind = ResolvedEnvKind.SecretRef, SecretRef = "unused-legacy-ref" },
        });
        var r = Renderer.Render(spec, Ctx, new[] { "node-a" }, "run-1");

        var nodeNameEnv = JobContainer(r.Jobs.Single()).Env.Single(e => e.Name == "NODE_NAME");
        nodeNameEnv.Value.Should().BeNull("it is a downward-API valueFrom, not a literal");
        nodeNameEnv.ValueFrom.Should().NotBeNull();
        nodeNameEnv.ValueFrom.FieldRef.Should().NotBeNull();
        nodeNameEnv.ValueFrom.FieldRef.FieldPath.Should().Be("spec.nodeName");
    }

    [Fact]
    public void ScanPerNode_self_rest_token_becomes_a_secret_key_ref_into_the_instance_secret()
    {
        var spec = Spec(kind: WorkloadKind.ScanPerNode, env: new[]
        {
            new ResolvedEnvVar { Name = "REEVE_WORKLOAD_TOKEN", Kind = ResolvedEnvKind.SecretRef, SecretRef = "unused-legacy-ref" },
        });
        var r = Renderer.Render(spec, Ctx, new[] { "node-a" }, "run-1");

        var e = JobContainer(r.Jobs.Single()).Env.Single(x => x.Name == "REEVE_WORKLOAD_TOKEN");
        e.Value.Should().BeNull();
        e.ValueFrom.Should().NotBeNull();
        e.ValueFrom.SecretKeyRef.Name.Should().Be(spec.SecretName);
        e.ValueFrom.SecretKeyRef.Key.Should().Be("REEVE_WORKLOAD_TOKEN");
    }


    [Fact]
    public void Object_and_pod_template_carry_the_full_label_set()
    {
        var spec = Spec();
        var r = Renderer.Render(spec, Ctx);

        r.Deployment!.Metadata.Name.Should().Be(spec.ResourceName);
        r.Deployment.Metadata.NamespaceProperty.Should().Be("reeve");
        r.Deployment.Metadata.Labels.Should().BeEquivalentTo(spec.Labels);
        PodMeta(r).Labels.Should().BeEquivalentTo(spec.Labels);
    }

    [Fact]
    public void Selector_uses_only_the_instance_label()
    {
        var spec = Spec();
        var r = Renderer.Render(spec, Ctx);
        var instanceValue = spec.Labels[WorkloadResourceLabels.InstanceLabelKey];

        r.Deployment!.Spec.Selector.MatchLabels.Should().Equal(
            new Dictionary<string, string> { [WorkloadResourceLabels.InstanceLabelKey] = instanceValue });
    }

    [Fact]
    public void Pod_template_carries_the_spec_hash_annotation()
    {
        var spec = Spec();
        var r = Renderer.Render(spec, Ctx);

        PodMeta(r).Annotations.Should().ContainKey(WorkloadResourceLabels.SpecHashLabelKey);
        PodMeta(r).Annotations[WorkloadResourceLabels.SpecHashLabelKey].Should().Be(spec.SpecHash);
    }


    [Fact]
    public void A_restart_nonce_bump_changes_the_pod_template_annotation_but_not_the_spec_hash()
    {
        var n1 = Spec() with { RestartNonce = 1 };
        var n2 = Spec() with { RestartNonce = 2 };

        var r1 = Renderer.Render(n1, Ctx);
        var r2 = Renderer.Render(n2, Ctx);

        PodMeta(r1).Annotations[KubernetesWorkloadRenderer.RestartGenerationAnnotationKey].Should().Be("1");
        PodMeta(r2).Annotations[KubernetesWorkloadRenderer.RestartGenerationAnnotationKey].Should().Be("2");
        PodMeta(r1).Annotations[KubernetesWorkloadRenderer.RestartGenerationAnnotationKey]
            .Should().NotBe(PodMeta(r2).Annotations[KubernetesWorkloadRenderer.RestartGenerationAnnotationKey],
                "a restart bumps the nonce so k8s rolls even an unchanged-spec workload");

        PodMeta(r1).Annotations[WorkloadResourceLabels.SpecHashLabelKey]
            .Should().Be(PodMeta(r2).Annotations[WorkloadResourceLabels.SpecHashLabelKey],
                "the spec-hash annotation must not move when only the restart nonce changes");

        SpecHasher.Compute(n1).Should().Be(SpecHasher.Compute(n2),
            "the restart nonce must be excluded from the SpecHash so it never poisons drift or REEVE_SPEC_HASH");
    }

    [Fact]
    public void A_never_restarted_spec_omits_the_restart_annotation_so_fresh_renders_are_unchanged()
    {
        var fresh = Renderer.Render(Spec(), Ctx);

        PodMeta(fresh).Annotations.Should().NotContainKey(KubernetesWorkloadRenderer.RestartGenerationAnnotationKey);
        PodMeta(fresh).Annotations.Should().ContainKey(WorkloadResourceLabels.SpecHashLabelKey);
    }


    [Fact]
    public void Image_is_passed_through_verbatim()
    {
        var r = Renderer.Render(Spec(), Ctx);
        Container(r).Image.Should().Be($"ghcr.io/reeve/matter@{Digest}");
    }

    [Fact]
    public void Resources_map_only_the_present_requests_and_limits()
    {
        var r = Renderer.Render(Spec(resources: new ResolvedResources
        {
            RequestsCpu = "100m",
            RequestsMemory = "256Mi",
            LimitsMemory = "512Mi",
        }), Ctx);

        var req = Container(r).Resources.Requests;
        var lim = Container(r).Resources.Limits;
        req.Should().ContainKey("cpu");
        req["cpu"].ToString().Should().Be("100m");
        req["memory"].ToString().Should().Be("256Mi");
        lim.Should().ContainKey("memory");
        lim.Should().NotContainKey("cpu");
    }


    [Fact]
    public void Privileged_and_run_as_root_and_caps_map_to_the_security_context()
    {
        var r = Renderer.Render(Spec(privileges: new ResolvedPrivileges
        {
            Privileged = true,
            RunAsRoot = true,
            Capabilities = new[] { "NET_ADMIN", "NET_RAW" },
        }), Ctx);

        var sc = Container(r).SecurityContext;
        sc.Privileged.Should().Be(true);
        sc.RunAsUser.Should().Be(0);
        sc.RunAsNonRoot.Should().Be(false);
        sc.Capabilities.Add.Should().BeEquivalentTo(new[] { "NET_ADMIN", "NET_RAW" });
        sc.Capabilities.Drop.Should().Contain("ALL");
    }

    [Fact]
    public void Capabilities_drop_all_by_default_even_without_adds()
    {
        var r = Renderer.Render(Spec(privileges: new ResolvedPrivileges { Privileged = false }), Ctx);

        Container(r).SecurityContext.Capabilities.Drop.Should().Contain("ALL");
    }

    [Fact]
    public void No_privilege_workload_still_gets_a_hardened_baseline_security_context()
    {
        var r = Renderer.Render(Spec(privileges: null), Ctx);

        var sc = Container(r).SecurityContext;
        sc.Should().NotBeNull();
        sc!.Capabilities.Drop.Should().Contain("ALL");
        sc.Capabilities.Add.Should().BeNull();
        sc.AllowPrivilegeEscalation.Should().Be(false);
        sc.RunAsNonRoot.Should().Be(true);
        sc.Privileged.Should().NotBe(true);
    }

    [Fact]
    public void No_privilege_workload_baseline_does_not_force_root()
    {
        var r = Renderer.Render(Spec(privileges: null), Ctx);

        var sc = Container(r).SecurityContext;
        sc!.RunAsUser.Should().NotBe(0);
    }

    [Fact]
    public void No_privilege_workload_baseline_sets_seccomp_runtime_default()
    {
        var r = Renderer.Render(Spec(privileges: null), Ctx);

        var sc = Container(r).SecurityContext;
        sc!.SeccompProfile.Should().NotBeNull();
        sc.SeccompProfile!.Type.Should().Be("RuntimeDefault");
    }

    [Fact]
    public void A_no_adds_privilege_spec_also_sets_seccomp_runtime_default()
    {
        var r = Renderer.Render(Spec(privileges: new ResolvedPrivileges()), Ctx);

        var sc = Container(r).SecurityContext;
        sc!.SeccompProfile.Should().NotBeNull();
        sc.SeccompProfile!.Type.Should().Be("RuntimeDefault");
    }

    [Fact]
    public void A_no_adds_privilege_spec_still_sets_the_hardened_baseline_flags()
    {
        var r = Renderer.Render(Spec(privileges: new ResolvedPrivileges()), Ctx);

        var sc = Container(r).SecurityContext;
        sc!.Capabilities.Drop.Should().Contain("ALL");
        sc.AllowPrivilegeEscalation.Should().Be(false);
        sc.RunAsNonRoot.Should().Be(true);
    }

    [Fact]
    public void A_privileged_spec_keeps_privileged_and_does_not_force_runAsNonRoot()
    {
        var r = Renderer.Render(Spec(privileges: new ResolvedPrivileges { Privileged = true, RunAsRoot = true }), Ctx);

        var sc = Container(r).SecurityContext;
        sc!.Privileged.Should().Be(true);
        sc.RunAsUser.Should().Be(0);
        sc.RunAsNonRoot.Should().Be(false);
    }


    [Fact]
    public void A_privilege_needed_for_device_passthrough_renders_a_privileged_container()
    {
        var r = Renderer.Render(Spec(privileges: new ResolvedPrivileges
        {
            DevicePassthroughPrivileged = true,
            DevicePaths = new[] { "/dev/ttyUSB0" },
        }), Ctx);

        var sc = Container(r).SecurityContext;
        sc!.Privileged.Should().Be(true);
        sc.AllowPrivilegeEscalation.Should().BeNull();
    }


    [Fact]
    public void Host_network_sets_the_pod_flag_and_dns_policy()
    {
        var r = Renderer.Render(Spec(network: new ResolvedNetwork { HostNetwork = true }), Ctx);

        PodSpec(r).HostNetwork.Should().Be(true);
        PodSpec(r).DnsPolicy.Should().Be("ClusterFirstWithHostNet");
    }


    [Fact]
    public void Platforms_become_arch_node_affinity()
    {
        var r = Renderer.Render(Spec(platforms: new[] { "linux/amd64", "linux/arm64" }), Ctx);

        var term = PodSpec(r).Affinity.NodeAffinity
            .RequiredDuringSchedulingIgnoredDuringExecution.NodeSelectorTerms.Single();
        var req = term.MatchExpressions.Single();
        req.Key.Should().Be("kubernetes.io/arch");
        req.OperatorProperty.Should().Be("In");
        req.Values.Should().Equal("amd64", "arm64");
    }

    [Fact]
    public void No_platforms_means_no_node_affinity()
    {
        PodSpec(Renderer.Render(Spec(), Ctx)).Affinity.Should().BeNull();
    }


    [Fact]
    public void NodeSelector_flows_to_the_pod_node_selector()
    {
        var r = Renderer.Render(Spec(nodeSelector: new Dictionary<string, string> { ["reeve.io/ble"] = "true" }), Ctx);

        PodSpec(r).NodeSelector.Should().Equal(new Dictionary<string, string> { ["reeve.io/ble"] = "true" });
    }

    [Fact]
    public void Empty_node_selector_leaves_the_pod_selector_unset()
    {
        PodSpec(Renderer.Render(Spec(), Ctx)).NodeSelector.Should().BeNull();
    }


    [Fact]
    public void A_volume_emits_a_pvc_object_and_a_pod_volume_mount()
    {
        var vol = new ResolvedVolume
        {
            Id = "data",
            MountPath = "/data",
            Size = "64Mi",
            Criticality = VolumeCriticality.Critical,
            ResourceName = "reeve-wl-com-reeve-matter-matter-data-11111111",
            StorageClassName = "fast-ssd",
        };
        var spec = Spec(volumes: new[] { vol });
        var r = Renderer.Render(spec, Ctx);

        var pvc = r.PersistentVolumeClaims.Single();
        pvc.Metadata.Name.Should().Be(vol.ResourceName);
        pvc.Metadata.NamespaceProperty.Should().Be("reeve");
        pvc.Spec.AccessModes.Should().Equal("ReadWriteOnce");
        pvc.Spec.Resources.Requests["storage"].ToString().Should().Be("64Mi");
        pvc.Spec.StorageClassName.Should().Be("fast-ssd");
        pvc.Metadata.Labels.Should().ContainKey(WorkloadResourceLabels.CriticalityLabelKey);
        pvc.Metadata.Labels[WorkloadResourceLabels.CriticalityLabelKey].Should().Be("critical");

        var podVol = PodSpec(r).Volumes.Single(v => v.PersistentVolumeClaim != null);
        podVol.PersistentVolumeClaim.ClaimName.Should().Be(vol.ResourceName);
        var mount = Container(r).VolumeMounts.Single(m => m.Name == podVol.Name);
        mount.MountPath.Should().Be("/data");
    }

    [Fact]
    public void A_volume_with_no_storage_class_omits_it()
    {
        var vol = new ResolvedVolume
        {
            Id = "data", MountPath = "/data", Size = "1Gi", Criticality = VolumeCriticality.Cache,
            ResourceName = "pvc-data", StorageClassName = null,
        };
        var r = Renderer.Render(Spec(volumes: new[] { vol }), Ctx);

        r.PersistentVolumeClaims.Single().Spec.StorageClassName.Should().BeNull();
    }

    [Fact]
    public void An_existing_claim_volume_mounts_and_names_the_retained_pvc(  )
    {
        const string retainedPvc = "reeve-wl-com-reeve-matter-matter-state-deadbeef";
        var vol = new ResolvedVolume
        {
            Id = "state",
            MountPath = "/data",
            Size = "1Gi",
            Criticality = VolumeCriticality.Critical,
            ResourceName = retainedPvc,
            StorageClassName = null,
            ExistingClaim = true,
        };
        var r = Renderer.Render(Spec(volumes: new[] { vol }), Ctx);

        var podVol = PodSpec(r).Volumes.Single(v => v.PersistentVolumeClaim != null);
        podVol.PersistentVolumeClaim.ClaimName.Should().Be(retainedPvc,
            "the pod mounts the retained PVC, not a fresh one");
        r.PersistentVolumeClaims.Single().Metadata.Name.Should().Be(retainedPvc);
    }


    [Fact]
    public void NodeLocalState_becomes_a_directory_or_create_hostpath_mount()
    {
        var r = Renderer.Render(Spec(privileges: new ResolvedPrivileges
        {
            NodeLocalState = new ResolvedNodeLocalState { HostPath = "/var/lib/reeve/w/bindkeys", Criticality = VolumeCriticality.Critical },
        }), Ctx);

        var vol = PodSpec(r).Volumes.Single(v => v.HostPath?.Path == "/var/lib/reeve/w/bindkeys");
        vol.HostPath.Type.Should().Be("DirectoryOrCreate");
        Container(r).VolumeMounts.Should().Contain(m => m.Name == vol.Name && m.MountPath == "/var/lib/reeve/w/bindkeys");
    }

    [Fact]
    public void Dbus_becomes_a_read_only_hostpath_mount()
    {
        var r = Renderer.Render(Spec(privileges: new ResolvedPrivileges { DbusHostPath = "/var/run/dbus" }), Ctx);

        var vol = PodSpec(r).Volumes.Single(v => v.HostPath?.Path == "/var/run/dbus");
        var mount = Container(r).VolumeMounts.Single(m => m.Name == vol.Name);
        mount.ReadOnlyProperty.Should().Be(true);
    }

    [Fact]
    public void Tun_device_becomes_a_char_device_hostpath_at_dev_net_tun()
    {
        var r = Renderer.Render(Spec(privileges: new ResolvedPrivileges { TunDevice = true }), Ctx);

        var vol = PodSpec(r).Volumes.Single(v => v.HostPath?.Path == "/dev/net/tun");
        vol.HostPath.Type.Should().Be("CharDevice");
        Container(r).VolumeMounts.Should().Contain(m => m.Name == vol.Name && m.MountPath == "/dev/net/tun");
    }

    [Fact]
    public void A_device_path_becomes_a_char_device_hostpath_mount()
    {
        var r = Renderer.Render(Spec(privileges: new ResolvedPrivileges { DevicePaths = new[] { "/dev/ttyUSB0" } }), Ctx);

        var vol = PodSpec(r).Volumes.Single(v => v.HostPath?.Path == "/dev/ttyUSB0");
        vol.HostPath.Type.Should().Be("CharDevice");
        Container(r).VolumeMounts.Should().Contain(m => m.Name == vol.Name && m.MountPath == "/dev/ttyUSB0");
    }

    [Fact]
    public void Pod_securityContext_carries_only_the_supplemental_groups()
    {
        var r = Renderer.Render(Spec(privileges: new ResolvedPrivileges
        {
            DevicePaths = new[] { "/dev/dri/renderD128" },
            DeviceGroupIds = new[] { 993 },
        }), Ctx);

        var sc = PodSpec(r).SecurityContext;
        sc.Should().NotBeNull();
        sc!.SupplementalGroups.Should().Equal(993L);

        sc.RunAsUser.Should().BeNull();
        sc.RunAsGroup.Should().BeNull();
        sc.RunAsNonRoot.Should().BeNull();
        sc.FsGroup.Should().BeNull();
        sc.FsGroupChangePolicy.Should().BeNull();
        sc.SeccompProfile.Should().BeNull();
        sc.AppArmorProfile.Should().BeNull();
        sc.SeLinuxOptions.Should().BeNull();
        sc.SeLinuxChangePolicy.Should().BeNull();
        sc.SupplementalGroupsPolicy.Should().BeNull();
        sc.Sysctls.Should().BeNull();
        sc.WindowsOptions.Should().BeNull();
    }

    [Fact]
    public void Pod_securityContext_is_absent_when_no_device_group_was_resolved()
    {
        var r = Renderer.Render(Spec(privileges: new ResolvedPrivileges { DevicePaths = new[] { "/dev/ttyUSB0" } }), Ctx);

        PodSpec(r).SecurityContext.Should().BeNull();
        PodSpec(Renderer.Render(Spec(), Ctx)).SecurityContext.Should().BeNull();
    }

    [Fact]
    public void Container_securityContext_still_carries_the_hardened_baseline_beside_the_pod_groups()
    {
        var r = Renderer.Render(Spec(privileges: new ResolvedPrivileges { DeviceGroupIds = new[] { 993 } }), Ctx);

        var sc = Container(r).SecurityContext;
        sc.Should().NotBeNull();
        sc!.RunAsNonRoot.Should().Be(true);
        sc.AllowPrivilegeEscalation.Should().Be(false);
        sc.Capabilities!.Drop.Should().Contain("ALL");
        sc.SeccompProfile!.Type.Should().Be("RuntimeDefault");
    }

    [Fact]
    public void Pod_supplemental_groups_are_sorted()
    {
        var r = Renderer.Render(Spec(privileges: new ResolvedPrivileges
        {
            DeviceGroupIds = new[] { 993, 104 },
        }), Ctx);

        PodSpec(r).SecurityContext!.SupplementalGroups.Should().Equal(104L, 993L);
    }

    private static ResolvedVolume DataVolume() => new()
    {
        Id = "data",
        MountPath = "/data",
        Size = "64Mi",
        Criticality = VolumeCriticality.Cache,
        ResourceName = "reeve-wl-com-reeve-matter-matter-11111111-data",
    };

    [Fact]
    public void A_volume_group_becomes_pod_fsGroup_changed_only_when_the_volume_root_does_not_match()
    {
        var r = Renderer.Render(Spec(privileges: new ResolvedPrivileges { FsGroup = 1000 }, volumes: new[] { DataVolume() }), Ctx);

        var sc = PodSpec(r).SecurityContext;
        sc.Should().NotBeNull();
        sc!.FsGroup.Should().Be(1000);
        sc.FsGroupChangePolicy.Should().Be("OnRootMismatch");
        sc.SupplementalGroups.Should().BeNull();

        sc.RunAsUser.Should().BeNull();
        sc.RunAsGroup.Should().BeNull();
        sc.RunAsNonRoot.Should().BeNull();
        Container(r).SecurityContext!.RunAsNonRoot.Should().Be(true);
    }

    [Fact]
    public void A_volume_group_and_device_groups_share_one_pod_securityContext()
    {
        var r = Renderer.Render(Spec(
            privileges: new ResolvedPrivileges { FsGroup = 1000, DeviceGroupIds = new[] { 993, 104 } },
            volumes: new[] { DataVolume() }), Ctx);

        var sc = PodSpec(r).SecurityContext!;
        sc.FsGroup.Should().Be(1000);
        sc.SupplementalGroups.Should().Equal(104L, 993L);
    }

    [Fact]
    public void No_volume_group_renders_no_fsGroup_so_an_existing_workload_does_not_reconcile_on_upgrade()
    {
        var r = Renderer.Render(Spec(privileges: new ResolvedPrivileges { RunAsRoot = true }, volumes: new[] { DataVolume() }), Ctx);

        PodSpec(r).SecurityContext.Should().BeNull();
    }

    [Fact]
    public void A_host_path_becomes_a_read_only_hostpath_volume_at_its_container_mount_path()
    {
        var r = Renderer.Render(Spec(privileges: new ResolvedPrivileges
        {
            HostPaths = new[] { new ResolvedHostPath { HostPath = "/sys/bus/usb", MountPath = "/host/sys/bus/usb" } },
        }), Ctx);

        var vol = PodSpec(r).Volumes.Single(v => v.HostPath?.Path == "/sys/bus/usb");
        var mount = Container(r).VolumeMounts.Single(m => m.Name == vol.Name);
        mount.MountPath.Should().Be("/host/sys/bus/usb", "the container mount path is distinct from the host path");
        mount.ReadOnlyProperty.Should().Be(true, "hostPaths[] is read-only by definition");
    }


    [Fact]
    public void ConfigMap_mount_is_read_only_and_references_the_configmap()
    {
        var r = Renderer.Render(Spec(configMaps: new[]
        {
            new ResolvedConfigMapMount { MountPath = "/etc/matter/paa", ConfigMapName = "matter-paa-roots" },
        }), Ctx);

        var vol = PodSpec(r).Volumes.Single(v => v.ConfigMap != null);
        vol.ConfigMap.Name.Should().Be("matter-paa-roots");
        var mount = Container(r).VolumeMounts.Single(m => m.Name == vol.Name);
        mount.MountPath.Should().Be("/etc/matter/paa");
        mount.ReadOnlyProperty.Should().Be(true);
    }


    [Fact]
    public void Secret_ref_env_becomes_a_secret_key_ref_into_the_instance_secret()
    {
        var spec = Spec(env: new[]
        {
            new ResolvedEnvVar { Name = "REEVE_WORKLOAD_TOKEN", Kind = ResolvedEnvKind.SecretRef, SecretRef = "unused-legacy-ref" },
        });
        var r = Renderer.Render(spec, Ctx);

        var e = Container(r).Env.Single(x => x.Name == "REEVE_WORKLOAD_TOKEN");
        e.Value.Should().BeNull();
        e.ValueFrom.SecretKeyRef.Name.Should().Be(spec.SecretName);
        e.ValueFrom.SecretKeyRef.Key.Should().Be("REEVE_WORKLOAD_TOKEN");
    }

    [Fact]
    public void Literal_and_spec_hash_env_carry_their_value()
    {
        var r = Renderer.Render(Spec(env: new[]
        {
            new ResolvedEnvVar { Name = "LOG_LEVEL", Kind = ResolvedEnvKind.Literal, Value = "debug" },
            new ResolvedEnvVar { Name = "REEVE_SPEC_HASH", Kind = ResolvedEnvKind.SpecHash, Value = "abc123def456" },
        }), Ctx);

        var env = Container(r).Env;
        env.Single(x => x.Name == "LOG_LEVEL").Value.Should().Be("debug");
        env.Single(x => x.Name == "LOG_LEVEL").ValueFrom.Should().BeNull();
        env.Single(x => x.Name == "REEVE_SPEC_HASH").Value.Should().Be("abc123def456");
    }


    [Fact]
    public void Image_pull_secret_is_set_when_the_context_provides_one()
    {
        var r = Renderer.Render(Spec(), new KubernetesRenderContext { Namespace = "reeve", ImagePullSecretName = "reeve-pull" });

        PodSpec(r).ImagePullSecrets.Single().Name.Should().Be("reeve-pull");
    }

    [Fact]
    public void No_image_pull_secret_leaves_the_list_unset()
    {
        PodSpec(Renderer.Render(Spec(), Ctx)).ImagePullSecrets.Should().BeNull();
    }

    [Fact]
    public void Auto_image_pull_secret_emits_a_dockerconfigjson_shell_and_pod_reference()
    {
        var r = Renderer.Render(Spec(), new KubernetesRenderContext
        {
            Namespace = "reeve",
            AutoImagePullSecretName = "reeve-auto-pull",
        });

        r.ImagePullSecret.Should().NotBeNull();
        r.ImagePullSecret!.Type.Should().Be("kubernetes.io/dockerconfigjson");
        r.ImagePullSecret.Metadata!.Name.Should().Be("reeve-auto-pull");
        r.ImagePullSecret.StringData.Should().BeEmpty("the renderer never fabricates the credential");

        PodSpec(r).ImagePullSecrets.Single().Name.Should().Be("reeve-auto-pull");
    }

    [Fact]
    public void Global_and_auto_image_pull_secrets_both_appear_on_the_pod_operator_first()
    {
        var r = Renderer.Render(Spec(), new KubernetesRenderContext
        {
            Namespace = "reeve",
            ImagePullSecretName = "operator-pull",
            AutoImagePullSecretName = "reeve-auto-pull",
        });

        PodSpec(r).ImagePullSecrets.Select(s => s.Name)
            .Should().Equal("operator-pull", "reeve-auto-pull");
    }


    [Fact]
    public void Ports_render_a_clusterip_service_named_the_resource_name_with_the_instance_selector()
    {
        var spec = Spec(network: new ResolvedNetwork
        {
            Ports = new[]
            {
                new ResolvedPort { Name = "api", Port = 8080, Protocol = "tcp" },
                new ResolvedPort { Name = "disc", Port = 5353, Protocol = "udp" },
            },
        });
        var r = Renderer.Render(spec, Ctx);

        r.Service.Should().NotBeNull();
        r.Service!.Metadata.Name.Should().Be(spec.ResourceName);
        r.Service.Spec.Type.Should().Be("ClusterIP");
        r.Service.Spec.Selector.Should().Equal(
            new Dictionary<string, string> { [WorkloadResourceLabels.InstanceLabelKey] = spec.Labels[WorkloadResourceLabels.InstanceLabelKey] });
        r.Service.Spec.Ports.Should().Contain(p => p.Name == "api" && p.Port == 8080 && p.Protocol == "TCP");
        r.Service.Spec.Ports.Should().Contain(p => p.Name == "disc" && p.Port == 5353 && p.Protocol == "UDP");
    }

    [Fact]
    public void No_ports_means_no_service()
    {
        Renderer.Render(Spec(), Ctx).Service.Should().BeNull();
        Renderer.Render(Spec(network: new ResolvedNetwork { HostNetwork = true }), Ctx).Service.Should().BeNull();
    }

    [Fact]
    public void Observability_adds_a_metrics_port_and_prometheus_annotations_to_the_service()
    {
        var spec = Spec(
            network: new ResolvedNetwork { Ports = new[] { new ResolvedPort { Name = "api", Port = 8080, Protocol = "tcp" } } },
            observability: new WorkloadObservability { Metrics = new WorkloadMetrics { Port = 9090, Path = "/metrics" } });
        var r = Renderer.Render(spec, Ctx);

        r.Service!.Spec.Ports.Should().Contain(p => p.Port == 9090);
        r.Service.Metadata.Annotations["prometheus.io/scrape"].Should().Be("true");
        r.Service.Metadata.Annotations["prometheus.io/port"].Should().Be("9090");
        r.Service.Metadata.Annotations["prometheus.io/path"].Should().Be("/metrics");
    }

    [Fact]
    public void ScanPerNode_renders_no_service_even_when_ports_and_metrics_are_declared()
    {
        var spec = Spec(
            kind: WorkloadKind.ScanPerNode,
            network: new ResolvedNetwork { HostNetwork = true, Ports = new[] { new ResolvedPort { Name = "metrics", Port = 8080, Protocol = "tcp" } } },
            observability: new WorkloadObservability { Metrics = new WorkloadMetrics { Port = 8080, Path = "/metrics" } });

        var r = Renderer.Render(spec, Ctx, new[] { "node-a" }, "run-1");

        r.Service.Should().BeNull("scan-per-node exposes no stable service target");
        r.Jobs.Should().NotBeEmpty("the scan still fans out to per-node Jobs");
    }

    [Fact]
    public void A_port_shared_by_a_network_port_and_the_metrics_port_is_not_duplicated_in_the_service()
    {
        var spec = Spec(
            kind: WorkloadKind.Service,
            network: new ResolvedNetwork { Ports = new[] { new ResolvedPort { Name = "metrics", Port = 8080, Protocol = "tcp" } } },
            observability: new WorkloadObservability { Metrics = new WorkloadMetrics { Port = 8080, Path = "/metrics" } });

        var r = Renderer.Render(spec, Ctx);

        r.Service!.Spec.Ports.Where(p => p.Port == 8080 && p.Protocol == "TCP").Should().HaveCount(1,
            "a port shared by a network port and the metrics port must appear exactly once");
        r.Service.Metadata.Annotations["prometheus.io/scrape"].Should().Be("true", "scrape annotations are still emitted");
        r.Service.Metadata.Annotations["prometheus.io/port"].Should().Be("8080");
    }


    [Fact]
    public void A_secret_shell_is_always_emitted()
    {
        var spec = Spec();
        var r = Renderer.Render(spec, Ctx);

        r.Secret.Should().NotBeNull();
        r.Secret.Metadata.Name.Should().Be(spec.SecretName);
        r.Secret.Metadata.NamespaceProperty.Should().Be("reeve");
        r.Secret.Metadata.Labels.Should().BeEquivalentTo(spec.Labels);
        r.Secret.Type.Should().Be("Opaque");
    }


    private const string NetworksKey = "k8s.v1.cni.cncf.io/networks";
    private const string HostNetDnsPolicy = "ClusterFirstWithHostNet";

    private static ResolvedNetwork Attached(params string[] attachments) => new()
    {
        Mode = WorkloadNetworkMode.OperatorNetwork,
        HostNetwork = false,
        Attachments = attachments,
    };

    private static IDictionary<string, string> PodAnnotations(RenderedKubernetesObjects r)
        => PodMeta(r).Annotations;


    [Fact]
    public void OperatorNetwork_renders_the_annotation_hostNetwork_false_and_ClusterFirst_together()
    {
        var r = Renderer.Render(Spec(network: Attached("iot-macvlan")), Ctx);

        PodAnnotations(r).Should().ContainKey(NetworksKey);
        PodSpec(r).HostNetwork.Should().Be(false);
        PodSpec(r).DnsPolicy.Should().Be("ClusterFirst");
    }

    [Fact]
    public void The_annotation_is_a_json_array_of_typed_objects_with_the_namespace_stamped_and_order_preserved()
    {
        var r = Renderer.Render(Spec(network: Attached("iot-macvlan", "cams-vlan40")), Ctx);

        PodAnnotations(r)[NetworksKey].Should().Be(
            """[{"name":"iot-macvlan","namespace":"reeve"},{"name":"cams-vlan40","namespace":"reeve"}]""");
    }

    [Fact]
    public void The_annotation_namespace_is_the_render_contexts_namespace_not_a_hardcoded_one()
    {
        var r = Renderer.Render(Spec(network: Attached("iot-macvlan")), new KubernetesRenderContext { Namespace = "home-automation" });

        PodAnnotations(r)[NetworksKey].Should().Be("""[{"name":"iot-macvlan","namespace":"home-automation"}]""");
    }

    [Theory]
    [InlineData("""evil","namespace":"kube-system""")]
    [InlineData("a,b")]
    [InlineData("kube-system/privileged-nad")]
    [InlineData("""x"},{"name":"y","namespace":"kube-system""")]
    public void An_attachment_name_carrying_json_metacharacters_cannot_break_out_of_the_structure(string hostileName)
    {
        var r = Renderer.Render(Spec(network: Attached(hostileName)), Ctx);

        var raw = PodAnnotations(r)[NetworksKey];

        using var parsed = System.Text.Json.JsonDocument.Parse(raw);
        parsed.RootElement.ValueKind.Should().Be(System.Text.Json.JsonValueKind.Array);
        parsed.RootElement.GetArrayLength().Should().Be(1);

        var only = parsed.RootElement[0];
        only.GetProperty("name").GetString().Should().Be(hostileName);
        only.GetProperty("namespace").GetString().Should().Be("reeve");
        only.EnumerateObject().Select(p => p.Name).Should().BeEquivalentTo(["name", "namespace"]);

        foreach (var element in parsed.RootElement.EnumerateArray())
        {
            element.GetProperty("namespace").GetString().Should().NotBe("kube-system");
        }
    }

    [Fact]
    public void Cluster_mode_renders_no_annotation_and_leaves_hostNetwork_and_dnsPolicy_unset()
    {
        foreach (var spec in new[] { Spec(), Spec(network: new ResolvedNetwork()) })
        {
            var r = Renderer.Render(spec, Ctx);

            PodAnnotations(r).Should().NotContainKey(NetworksKey);
            PodSpec(r).HostNetwork.Should().BeNull();
            PodSpec(r).DnsPolicy.Should().BeNull();
        }
    }

    [Fact]
    public void HostNetwork_mode_renders_no_annotation_and_keeps_the_host_network_arm()
    {
        foreach (var net in new[]
                 {
                     new ResolvedNetwork { HostNetwork = true },
                     new ResolvedNetwork { HostNetwork = true, Mode = WorkloadNetworkMode.HostNetwork },
                 })
        {
            var r = Renderer.Render(Spec(network: net), Ctx);

            PodAnnotations(r).Should().NotContainKey(NetworksKey);
            PodSpec(r).HostNetwork.Should().Be(true);
            PodSpec(r).DnsPolicy.Should().Be(HostNetDnsPolicy);
        }
    }

    [Fact]
    public void The_scan_per_node_job_template_carries_the_same_annotation_the_pod_template_does()
    {
        var attached = Attached("iot-macvlan", "cams-vlan40");
        var expected = KubernetesWorkloadRenderer.RenderNetworksAnnotationValue(
            Spec(network: attached), "reeve");

        var scan = Renderer.Render(
            Spec(kind: WorkloadKind.ScanPerNode, network: attached), Ctx, new[] { "node-a", "node-b" }, "run-1");

        scan.Jobs.Should().HaveCount(2);
        foreach (var job in scan.Jobs)
        {
            job.Spec.Template.Metadata.Annotations.Should().ContainKey(NetworksKey)
                .WhoseValue.Should().Be(expected);
            job.Spec.Template.Spec.HostNetwork.Should().Be(false);
            job.Spec.Template.Spec.DnsPolicy.Should().Be("ClusterFirst");
        }

        PodAnnotations(Renderer.Render(Spec(network: attached), Ctx))[NetworksKey].Should().Be(expected);
    }

    [Fact]
    public void No_render_path_produces_an_attachment_beside_ClusterFirstWithHostNet()
    {
        foreach (var (label, rendered) in RenderCorpus())
        {
            foreach (var template in PodTemplates(rendered))
            {
                var carriesAttachment = template.Metadata?.Annotations?.ContainsKey(NetworksKey) == true;
                if (!carriesAttachment)
                {
                    continue;
                }

                template.Spec.DnsPolicy.Should().NotBe(HostNetDnsPolicy, $"[{label}] an attached pod must resolve names against the cluster resolver");
                template.Spec.DnsPolicy.Should().Be("ClusterFirst", $"[{label}] annotation, hostNetwork and dnsPolicy are managed as one unit");
                template.Spec.HostNetwork.Should().NotBe(true, $"[{label}] hostNetwork and an attachment are mutually exclusive");
            }
        }
    }


    [Fact]
    public void HostNetwork_with_a_non_empty_attachment_set_fails_rather_than_rendering_an_incoherent_pod_spec()
    {
        foreach (var net in new[]
                 {
                     new ResolvedNetwork { Mode = WorkloadNetworkMode.HostNetwork, HostNetwork = true, Attachments = new[] { "iot-macvlan" } },
                     new ResolvedNetwork { Mode = WorkloadNetworkMode.HostNetwork, HostNetwork = false, Attachments = new[] { "iot-macvlan" } },
                     new ResolvedNetwork { Mode = WorkloadNetworkMode.OperatorNetwork, HostNetwork = true, Attachments = new[] { "iot-macvlan" } },
                 })
        {
            var act = () => Renderer.Render(Spec(network: net), Ctx);

            act.Should().Throw<System.InvalidOperationException>().WithMessage("*hostNetwork*attachment*");
        }
    }

    [Fact]
    public void OperatorNetwork_with_an_empty_attachment_set_fails_rather_than_rendering_an_incoherent_pod_spec()
    {
        var act = () => Renderer.Render(
            Spec(network: new ResolvedNetwork { Mode = WorkloadNetworkMode.OperatorNetwork }), Ctx);

        act.Should().Throw<System.InvalidOperationException>().WithMessage("*operatorNetwork*");
    }

    [Fact]
    public void The_incoherent_guard_fires_on_the_scan_fan_out_too()
    {
        var act = () => Renderer.Render(
            Spec(kind: WorkloadKind.ScanPerNode, network: new ResolvedNetwork { Mode = WorkloadNetworkMode.OperatorNetwork }),
            Ctx,
            new[] { "node-a" },
            "run-1");

        act.Should().Throw<System.InvalidOperationException>();
    }


    [Fact]
    public void The_node_eligibility_selector_is_stamped_on_the_daemonset_and_never_on_a_pinned_job()
    {
        var selector = new Dictionary<string, string> { ["reeve.io/iot-parent"] = "true" };

        var daemonSet = Renderer.Render(
            Spec(kind: WorkloadKind.PerNode, network: Attached("iot-macvlan"), nodeSelector: selector), Ctx);

        daemonSet.DaemonSet.Should().NotBeNull();
        daemonSet.DaemonSet!.Spec.Template.Spec.NodeSelector.Should().Equal(selector);

        var scan = Renderer.Render(
            Spec(kind: WorkloadKind.ScanPerNode, network: Attached("iot-macvlan"), nodeSelector: selector),
            Ctx,
            new[] { "node-a" },
            "run-1");

        var job = scan.Jobs.Should().ContainSingle().Subject;

        job.Spec.Template.Spec.NodeName.Should().Be("node-a");
        job.Spec.Template.Spec.NodeSelector.Should().BeNull();
    }

    [Fact]
    public void The_scan_fan_out_renders_exactly_the_nodes_it_was_given()
    {
        var scan = Renderer.Render(
            Spec(kind: WorkloadKind.ScanPerNode, network: Attached("iot-macvlan"), nodeSelector: new Dictionary<string, string> { ["reeve.io/iot-parent"] = "true" }),
            Ctx,
            new[] { "node-a", "node-c" },
            "run-1");

        scan.Jobs.Select(j => j.Spec.Template.Spec.NodeName).Should().Equal("node-a", "node-c");
    }


    [Fact]
    public void No_rendered_container_carries_any_probe()
    {
        foreach (var (label, rendered) in RenderCorpus())
        {
            foreach (var container in AllContainers(rendered))
            {
                container.LivenessProbe.Should().BeNull($"[{label}] {container.Name}");
                container.ReadinessProbe.Should().BeNull($"[{label}] {container.Name}");
                container.StartupProbe.Should().BeNull($"[{label}] {container.Name}");
            }
        }
    }

    [Fact]
    public void Every_probe_the_renderer_emits_resolves_against_the_pods_primary_address()
    {
        foreach (var (label, rendered) in RenderCorpus())
        {
            foreach (var container in AllContainers(rendered))
            {
                foreach (var probe in new[] { container.LivenessProbe, container.ReadinessProbe, container.StartupProbe })
                {
                    if (probe is null)
                    {
                        continue;
                    }

                    probe.HttpGet?.Host.Should().BeNull($"[{label}] {container.Name}: an http probe must target the pod's primary address");
                    probe.TcpSocket?.Host.Should().BeNull($"[{label}] {container.Name}: a tcp probe must target the pod's primary address");
                }
            }
        }
    }


    private static IEnumerable<(string Label, RenderedKubernetesObjects Rendered)> RenderCorpus()
    {
        var attached = Attached("iot-macvlan", "cams-vlan40");
        var hostNet = new ResolvedNetwork { Mode = WorkloadNetworkMode.HostNetwork, HostNetwork = true };
        var ported = new ResolvedNetwork
        {
            Ports = new[] { new ResolvedPort { Name = "http", Port = 8080, Protocol = "tcp" } },
        };

        foreach (var kind in new[] { WorkloadKind.Service, WorkloadKind.SingleNode, WorkloadKind.PerNode, WorkloadKind.ScanPerNode })
        {
            foreach (var (netLabel, net) in new (string, ResolvedNetwork?)[]
                     {
                         ("no-network", null),
                         ("cluster", ported),
                         ("hostNetwork", hostNet),
                         ("operatorNetwork", attached),
                     })
            {
                yield return (
                    $"{kind}/{netLabel}",
                    Renderer.Render(
                        Spec(
                            kind: kind,
                            network: net,
                            privileges: new ResolvedPrivileges { Privileged = true, DevicePaths = new[] { "/dev/ttyUSB0" } },
                            env: new[] { new ResolvedEnvVar { Name = "TOKEN", Kind = ResolvedEnvKind.SecretRef } },
                            observability: new WorkloadObservability { Metrics = new WorkloadMetrics { Port = 8080, Path = "/metrics" } }),
                        Ctx,
                        new[] { "node-a", "node-b" },
                        "run-1"));
            }
        }
    }

    private static IEnumerable<V1PodTemplateSpec> PodTemplates(RenderedKubernetesObjects r)
    {
        if (r.Deployment is not null)
        {
            yield return r.Deployment.Spec.Template;
        }

        if (r.DaemonSet is not null)
        {
            yield return r.DaemonSet.Spec.Template;
        }

        foreach (var job in r.Jobs)
        {
            yield return job.Spec.Template;
        }
    }

    private static IEnumerable<V1Container> AllContainers(RenderedKubernetesObjects r)
    {
        foreach (var template in PodTemplates(r))
        {
            foreach (var c in template.Spec.Containers ?? [])
            {
                yield return c;
            }

            foreach (var c in template.Spec.InitContainers ?? [])
            {
                yield return c;
            }

            foreach (var c in template.Spec.EphemeralContainers ?? [])
            {
                yield return new V1Container { Name = c.Name, LivenessProbe = c.LivenessProbe, ReadinessProbe = c.ReadinessProbe, StartupProbe = c.StartupProbe };
            }
        }
    }
}
