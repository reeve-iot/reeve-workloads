using Reeve.Workloads.Abstractions.Providers;
using System;
using System.Collections.Generic;
using FluentAssertions;
using Reeve.Workloads.Abstractions.Contracts;
using Reeve.Workloads.Abstractions.Domain;
using Reeve.Workloads.Abstractions.Domain.Entities;
using Reeve.Workloads.Domain;
using Reeve.Workloads.Tests.Shared;

namespace Reeve.Workloads.Abstractions.Tests;

public sealed class WorkloadResourceNamingTests
{
    private const string Inst8 = "abcdef12";

    [Fact]
    public void Kubernetes_object_name_golden()
    {
        DefaultWorkloadResourceNamer.Instance.ObjectName(new WorkloadNamingSubject
        {
            GroupId = "matter",
            WorkloadId = "matter-server",
            Inst8 = Inst8,
        })
            .Should().Be("reeve-wl-matter-matter-server-abcdef12");
    }

    [Fact]
    public void Kubernetes_pvc_name_golden_includes_volume_id()
    {
        DefaultWorkloadResourceNamer.Instance.VolumeName(
            new WorkloadNamingSubject
            {
                GroupId = "bt",
                WorkloadId = "ble",
                Inst8 = Inst8,
            },
            "bindkeys")
            .Should().Be("reeve-wl-bt-ble-bindkeys-abcdef12");
    }



    [Fact]
    public void Suspend_selector_key_golden()
    {
        WorkloadResourceNaming.SuspendSelectorKey(Inst8).Should().Be("reeve.io/suspended-abcdef12");
    }

    [Fact]
    public void ResolveRetainedResourceName_for_a_MANUAL_instance_uses_the_k8s_pvc_scheme_not_a_docker_phantom()
    {
        var manual = WorkloadInstance.Create(Guid.NewGuid(), Guid.NewGuid(), "zwave", "zwave-js", Now);
        manual.SetProvider(StandInProviders.ManualId, Now);

        var namer = DefaultWorkloadResourceNamer.Instance;
        var name = WorkloadResourceNaming.ResolveRetainedResourceName(manual, "state", namer);
        var scheme = WorkloadResourceNaming.ResolveRetainedResourceScheme(manual, "state", namer);

        name.Should().Be(
            DefaultWorkloadResourceNamer.Instance.VolumeName(
                new WorkloadNamingSubject
                {
                    GroupId = "zwave",
                    WorkloadId = "zwave-js",
                    Inst8 = manual.Inst8,
                },
                "state"),
            "a manual instance's real physical volume is the k8s PVC name its k8s/helm artifact emits, not a docker phantom");
        name.Should().StartWith("reeve-wl-");
        scheme.Should().Be(RetainedVolumeSchemeId.Kubernetes);
    }


    [Fact]
    public void ResolveRetainedResourceScheme_for_an_adopted_claim_reflects_the_adopted_name_shape()
    {
        var k8sAdopter = WorkloadInstance.Create(Guid.NewGuid(), Guid.NewGuid(), "zwave", "zwave-js", Now);
        k8sAdopter.SetProvider(StandInProviders.ManualId, Now);
        k8sAdopter.SetResolvedParameters(
            new Dictionary<string, string?> { ["existingClaim.state"] = "reeve-wl-zwave-zwave-js-state-deadbeef" }, Now);

        var namer = DefaultWorkloadResourceNamer.Instance;
        WorkloadResourceNaming.ResolveRetainedResourceName(k8sAdopter, "state", namer)
            .Should().Be("reeve-wl-zwave-zwave-js-state-deadbeef");
        WorkloadResourceNaming.ResolveRetainedResourceScheme(k8sAdopter, "state", namer)
            .Should().Be(RetainedVolumeSchemeId.Kubernetes);
    }

    private static readonly DateTimeOffset Now = new(2026, 7, 7, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Names_over_63_chars_are_truncated_with_a_hash_suffix_and_stay_within_the_limit()
    {
        var longType = new string('a', 40);
        var longWorkload = new string('b', 40);

        var name = DefaultWorkloadResourceNamer.Instance.ObjectName(new WorkloadNamingSubject
                   {
                       GroupId = longType,
                       WorkloadId = longWorkload,
                       Inst8 = Inst8,
                   });

        name.Length.Should().BeLessThanOrEqualTo(WorkloadResourceNaming.Dns1123MaxLength);
        name.Should().StartWith("reeve-wl-");
        name.Should().MatchRegex("^[a-z0-9-]+-[0-9a-f]{6}$");
        name.Should().NotEndWith("-");
    }

    [Fact]
    public void Truncation_is_deterministic()
    {
        var longType = new string('a', 40);
        var longWorkload = new string('b', 40);

        var first = DefaultWorkloadResourceNamer.Instance.ObjectName(new WorkloadNamingSubject
                    {
                        GroupId = longType,
                        WorkloadId = longWorkload,
                        Inst8 = Inst8,
                    });
        var second = DefaultWorkloadResourceNamer.Instance.ObjectName(new WorkloadNamingSubject
                     {
                         GroupId = longType,
                         WorkloadId = longWorkload,
                         Inst8 = Inst8,
                     });

        first.Should().Be(second);
    }

    [Fact]
    public void Distinct_long_inputs_that_share_a_visible_prefix_do_not_collide_after_truncation()
    {
        var head = new string('a', 60);
        var name1 = WorkloadResourceNaming.TruncateForDns(head + "-alpha");
        var name2 = WorkloadResourceNaming.TruncateForDns(head + "-omega");

        name1.Length.Should().BeLessThanOrEqualTo(63);
        name2.Length.Should().BeLessThanOrEqualTo(63);
        name1.Should().NotBe(name2);
    }

    [Fact]
    public void Truncate_leaves_short_names_untouched()
    {
        WorkloadResourceNaming.TruncateForDns("reeve-wl-matter-x").Should().Be("reeve-wl-matter-x");
    }

    [Fact]
    public void Two_different_instance_ids_produce_fully_disjoint_names_and_labels()
    {
        var idA = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var idB = Guid.Parse("22222222-2222-2222-2222-222222222222");
        var inst8A = idA.ToString("N")[..8];
        var inst8B = idB.ToString("N")[..8];

        var nameA = DefaultWorkloadResourceNamer.Instance.ObjectName(new WorkloadNamingSubject
                    {
                        GroupId = "zwave",
                        WorkloadId = "zwave-js",
                        Inst8 = inst8A,
                    });
        var nameB = DefaultWorkloadResourceNamer.Instance.ObjectName(new WorkloadNamingSubject
                    {
                        GroupId = "zwave",
                        WorkloadId = "zwave-js",
                        Inst8 = inst8B,
                    });
        nameA.Should().NotBe(nameB);

        var pvcA = DefaultWorkloadResourceNamer.Instance.VolumeName(
                       new WorkloadNamingSubject
                       {
                           GroupId = "zwave",
                           WorkloadId = "zwave-js",
                           Inst8 = inst8A,
                       },
                       "cache");
        var pvcB = DefaultWorkloadResourceNamer.Instance.VolumeName(
                       new WorkloadNamingSubject
                       {
                           GroupId = "zwave",
                           WorkloadId = "zwave-js",
                           Inst8 = inst8B,
                       },
                       "cache");
        pvcA.Should().NotBe(pvcB);

        var labelA = DefaultWorkloadResourceNamer.Instance.InstanceSelectorLabels(idA)[WorkloadResourceLabels.InstanceLabelKey];
        var labelB = DefaultWorkloadResourceNamer.Instance.InstanceSelectorLabels(idB)[WorkloadResourceLabels.InstanceLabelKey];
        labelA.Should().NotBe(labelB);
    }

    [Fact]
    public void Sanitize_lowercases_and_collapses_unsafe_chars_into_hyphens()
    {
        WorkloadResourceNaming.Sanitize("My_Plugin.Type!!").Should().Be("my-plugin-type");
        WorkloadResourceNaming.Sanitize("--Leading-And-Trailing--").Should().Be("leading-and-trailing");
        WorkloadResourceNaming.Sanitize("UPPER123").Should().Be("upper123");
    }

    [Fact]
    public void Sanitize_never_yields_an_empty_segment()
    {
        WorkloadResourceNaming.Sanitize("!!!").Should().Be("x");
        WorkloadResourceNaming.Sanitize("").Should().Be("x");
    }

    [Fact]
    public void Unsafe_plugin_and_workload_ids_are_sanitised_into_dns_safe_names()
    {
        DefaultWorkloadResourceNamer.Instance.ObjectName(new WorkloadNamingSubject
        {
            GroupId = "Matter.Server",
            WorkloadId = "My Workload",
            Inst8 = Inst8,
        })
            .Should().Be("reeve-wl-matter-server-my-workload-abcdef12");
    }

    [Fact]
    public void Instance_selector_labels_carry_the_guid_under_the_canonical_key()
    {
        var id = Guid.NewGuid();

        var k8s = DefaultWorkloadResourceNamer.Instance.InstanceSelectorLabels(id);
        k8s.Should().ContainKey(WorkloadResourceLabels.InstanceLabelKey)
            .WhoseValue.Should().Be(id.ToString("D"));

    }

    [Fact]
    public void Kubernetes_labels_include_installation_spec_hash_and_optional_criticality()
    {
        var id = Guid.NewGuid();

        var labels = DefaultWorkloadResourceNamer.Instance.Labels(new WorkloadLabelSubject
                     {
                         InstanceId = id,
                         InstallationId = "prod-install",
                         SpecHash = "spec123",
                         Criticality = "critical",
                     });

        labels[WorkloadResourceLabels.InstanceLabelKey].Should().Be(id.ToString("D"));
        labels[WorkloadResourceLabels.InstallationLabelKey].Should().Be("prod-install");
        labels[WorkloadResourceLabels.SpecHashLabelKey].Should().Be("spec123");
        labels[WorkloadResourceLabels.CriticalityLabelKey].Should().Be("critical");
    }

    [Fact]
    public void Spec_hash_label_stays_within_the_k8s_63_char_limit_for_a_full_sha256()
    {
        const string fullSha256 = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

        var value = DefaultWorkloadResourceNamer.Instance.Labels(new WorkloadLabelSubject
        {
            InstanceId = Guid.NewGuid(),
            InstallationId = "install",
            SpecHash = fullSha256,
            Criticality = "critical",
        })[WorkloadResourceLabels.SpecHashLabelKey];

        value.Length.Should().BeLessThanOrEqualTo(WorkloadResourceNaming.Dns1123MaxLength);
        value.Should().Be(WorkloadResourceNaming.TruncateForDns(WorkloadResourceNaming.Sanitize(fullSha256)));
        value.Should().MatchRegex("^[a-z0-9-]+$").And.NotEndWith("-");
    }

    [Fact]
    public void Kubernetes_labels_omit_criticality_when_not_supplied()
    {
        var labels = DefaultWorkloadResourceNamer.Instance.Labels(new WorkloadLabelSubject
                     {
                         InstanceId = Guid.NewGuid(),
                         InstallationId = "install",
                         SpecHash = "spec",
                     });

        labels.Should().NotContainKey(WorkloadResourceLabels.CriticalityLabelKey);
    }

    [Fact]
    public void Suspend_node_selector_matches_no_node()
    {
        var selector = WorkloadResourceNaming.SuspendNodeSelector(Inst8);

        selector[WorkloadResourceNaming.SuspendSelectorKey(Inst8)].Should().Be("true");
    }

    [Fact]
    public void Scan_per_node_job_name_embeds_the_object_scheme_node_and_run_and_is_dns_safe()
    {
        var name = WorkloadResourceNaming.ScanPerNodeJobName("discovery", "scan", Inst8, "node-a", "run7");

        name.Should().StartWith($"reeve-wl-discovery-scan-{Inst8}");
        name.Length.Should().BeLessThanOrEqualTo(WorkloadResourceNaming.Dns1123MaxLength);
        name.Should().MatchRegex("^[a-z0-9-]+$").And.NotEndWith("-");
        WorkloadResourceNaming.ScanPerNodeJobName("discovery", "scan", Inst8, "node-a", "run7")
            .Should().Be(name, "the (instance, node, run) name is the deterministic create-or-skip key");
    }

    [Fact]
    public void Scan_per_node_job_names_differ_across_nodes_for_the_same_run()
    {
        var a = WorkloadResourceNaming.ScanPerNodeJobName("discovery", "scan", Inst8, "node-a", "run7");
        var b = WorkloadResourceNaming.ScanPerNodeJobName("discovery", "scan", Inst8, "node-b", "run7");

        a.Should().NotBe(b);
    }

    [Fact]
    public void Scan_per_node_job_names_differ_across_runs_and_bound_long_node_names()
    {
        var run1 = WorkloadResourceNaming.ScanPerNodeJobName("discovery", "scan", Inst8, "node-a", "run7");
        var run2 = WorkloadResourceNaming.ScanPerNodeJobName("discovery", "scan", Inst8, "node-a", "run8");

        run1.Should().NotBe(run2);

        var longNode = new string('n', 80);
        var name = WorkloadResourceNaming.ScanPerNodeJobName("discovery", "scan", Inst8, longNode, "run7");

        name.Length.Should().BeLessThanOrEqualTo(WorkloadResourceNaming.Dns1123MaxLength);
        name.Should().MatchRegex("^[a-z0-9-]+$").And.NotEndWith("-");
    }

    [Fact]
    public void Scan_per_node_job_labels_carry_identity_plus_node_and_run_and_stay_dns_safe()
    {
        var id = Guid.NewGuid();
        var longNode = new string('N', 80);
        const string fullSha256 = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

        var run1 = WorkloadResourceNaming.ScanPerNodeJobLabels(id, "prod-install", fullSha256, longNode, "run7", "critical");

        run1.Should().ContainKey(WorkloadResourceLabels.InstanceLabelKey).WhoseValue.Should().Be(id.ToString("D"));
        run1[WorkloadResourceLabels.InstallationLabelKey].Should().Be("prod-install");
        run1.Should().ContainKey(WorkloadResourceLabels.SpecHashLabelKey);
        run1.Should().ContainKey(WorkloadResourceLabels.CriticalityLabelKey);
        run1.Should().ContainKey(WorkloadResourceLabels.ScanNodeLabelKey);
        run1.Should().ContainKey(WorkloadResourceLabels.ScanRunLabelKey);

        foreach (var value in run1.Values)
        {
            value.Length.Should().BeLessThanOrEqualTo(WorkloadResourceNaming.Dns1123MaxLength);
        }

        run1[WorkloadResourceLabels.ScanNodeLabelKey].Should()
            .Be(WorkloadResourceNaming.TruncateForDns(WorkloadResourceNaming.Sanitize(longNode)));
        run1[WorkloadResourceLabels.ScanNodeLabelKey].Should().MatchRegex("^[a-z0-9-]+$").And.NotEndWith("-");

        var run2 = WorkloadResourceNaming.ScanPerNodeJobLabels(id, "prod-install", fullSha256, longNode, "run8", "critical");
        run2[WorkloadResourceLabels.ScanRunLabelKey].Should().NotBe(run1[WorkloadResourceLabels.ScanRunLabelKey]);
        run2[WorkloadResourceLabels.ScanNodeLabelKey].Should().Be(run1[WorkloadResourceLabels.ScanNodeLabelKey]);
    }

    [Fact]
    public void Stamp_scan_labels_is_the_single_source_the_domain_helper_routes_through()
    {
        var id = Guid.NewGuid();
        const string fullSha256 = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
        var longNode = new string('N', 80);

        var baseLabels = DefaultWorkloadResourceNamer.Instance.Labels(new WorkloadLabelSubject
                        {
                            InstanceId = id,
                            InstallationId = "prod-install",
                            SpecHash = fullSha256,
                            Criticality = "critical",
                        });

        var viaHelper = WorkloadResourceNaming.ScanPerNodeJobLabels(id, "prod-install", fullSha256, longNode, "run7", "critical");
        var viaStamp = WorkloadResourceNaming.StampScanLabels(baseLabels, longNode, "run7");

        viaHelper.Should().BeEquivalentTo(viaStamp);

        foreach (var kv in baseLabels)
        {
            viaStamp.Should().ContainKey(kv.Key).WhoseValue.Should().Be(kv.Value);
        }

        viaStamp[WorkloadResourceLabels.ScanNodeLabelKey].Should()
            .Be(WorkloadResourceNaming.TruncateForDns(WorkloadResourceNaming.Sanitize(longNode)));
        viaStamp[WorkloadResourceLabels.ScanRunLabelKey].Should()
            .Be(WorkloadResourceNaming.TruncateForDns(WorkloadResourceNaming.Sanitize("run7")));
    }

    [Fact]
    public void Scan_run_label_value_is_the_single_source_the_stamper_and_delete_selector_share()
    {
        var id = Guid.NewGuid();
        const string fullSha256 = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
        const string node = "node-a";
        var runId = "Run/" + new string('R', 80) + "!";

        var baseLabels = DefaultWorkloadResourceNamer.Instance.Labels(new WorkloadLabelSubject
                        {
                            InstanceId = id,
                            InstallationId = "prod-install",
                            SpecHash = fullSha256,
                            Criticality = "critical",
                        });
        var stamped = WorkloadResourceNaming.StampScanLabels(baseLabels, node, runId);

        stamped[WorkloadResourceLabels.ScanRunLabelKey].Should()
            .Be(WorkloadResourceNaming.ScanRunLabelValue(runId));

        WorkloadResourceNaming.ScanRunLabelValue(runId).Should()
            .Be(WorkloadResourceNaming.TruncateForDns(WorkloadResourceNaming.Sanitize(runId)));
    }

}
