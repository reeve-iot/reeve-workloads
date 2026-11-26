using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using Reeve.Workloads.Abstractions.Contracts;
using Reeve.Workloads.Abstractions.Contracts.Deployment;
using Reeve.Workloads.Abstractions.Domain.DesiredSpec;
using Reeve.Workloads.Abstractions.Providers;
using Reeve.Workloads.Providers.Kubernetes;
using Xunit;

namespace Reeve.Workloads.Providers.Kubernetes.Tests;

public sealed class KubernetesWorkloadPreflightCheckerTests
{
    private const string Digest = "sha256:1111111111111111111111111111111111111111111111111111111111111111";
    private static readonly KubernetesWorkloadPreflightChecker Checker = new();

    private static WorkloadDesiredSpec Spec(
        WorkloadKind kind = WorkloadKind.Service,
        IReadOnlyList<string>? platforms = null,
        IReadOnlyDictionary<string, string>? nodeSelector = null,
        ResolvedPrivileges? privileges = null,
        ResolvedNetwork? network = null,
        ResolvedResources? resources = null,
        IReadOnlyList<ResolvedVolume>? volumes = null)
        => new()
        {
            GroupInstanceId = System.Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee"),
            GroupId = "com.reeve.matter",
            WorkloadId = "matter",
            InstanceId = System.Guid.Parse("11111111-2222-3333-4444-555555555555"),
            Inst8 = "11111111",
            Provider = KubernetesProvider.Id,
            ResourceName = "reeve-wl-com-reeve-matter-matter-11111111",
            SecretName = "reeve-wl-com-reeve-matter-matter-11111111",
            Labels = new Dictionary<string, string>(),
            Image = $"ghcr.io/reeve/matter@{Digest}",
            Platforms = platforms ?? new List<string>(),
            Kind = kind,
            NodeSelector = nodeSelector ?? new Dictionary<string, string>(),
            Resources = resources,
            Network = network,
            Privileges = privileges,
            Volumes = volumes ?? new List<ResolvedVolume>(),
            Health = new WorkloadHealth { Mode = WorkloadHealthMode.Container },
            Env = new List<ResolvedEnvVar>(),
            SpecHash = "abc123def456",
        };

    private static KubernetesClusterInspection Cluster(
        IReadOnlyList<ClusterNodeInfo>? nodes = null,
        IReadOnlyDictionary<string, string>? psaLabels = null,
        IReadOnlyList<ClusterStorageClassInfo>? storageClasses = null,
        bool quotaRequiresRequests = false,
        bool nodesAvailable = true,
        bool psaAvailable = true,
        bool storageClassesAvailable = true,
        bool resourceQuotaAvailable = true,
        System.Version? serverVersion = null)
        => new()
        {
            ApiReachable = true,
            Nodes = nodes ?? new List<ClusterNodeInfo>(),
            NodesAvailable = nodesAvailable,
            NamespacePsaLabels = psaLabels ?? new Dictionary<string, string>(),
            NamespacePsaAvailable = psaAvailable,
            StorageClasses = storageClasses ?? new List<ClusterStorageClassInfo>(),
            StorageClassesAvailable = storageClassesAvailable,
            NamespaceHasResourceQuotaRequiringRequests = quotaRequiresRequests,
            ResourceQuotaAvailable = resourceQuotaAvailable,
            ServerVersion = serverVersion,
        };

    private static ClusterNodeInfo Node(string name, string arch, IReadOnlyDictionary<string, string>? labels = null, bool schedulable = true, bool? userNamespaces = null)
    {
        var l = new Dictionary<string, string>(labels ?? new Dictionary<string, string>())
        {
            ["kubernetes.io/arch"] = arch,
        };
        return new ClusterNodeInfo { Name = name, Arch = arch, Labels = l, Schedulable = schedulable, UserNamespacesSupported = userNamespaces };
    }

    private static KubernetesProviderSettings Settings(string? defaultStorageClass = null)
        => new() { DefaultStorageClassName = defaultStorageClass };

    private static ResolvedPrivileges Privileged() => new() { Privileged = true };

    private static WorkloadDesiredSpec UserNsSpec(IReadOnlyDictionary<string, string>? nodeSelector = null, bool runAsRoot = false)
        => Spec(nodeSelector: nodeSelector, privileges: runAsRoot ? new ResolvedPrivileges { RunAsRoot = true } : null)
            with { UserNamespace = true };

    private static ResolvedVolume Volume(string? storageClassName)
        => new()
        {
            Id = "data",
            MountPath = "/data",
            Size = "1Gi",
            Criticality = VolumeCriticality.Critical,
            ResourceName = "reeve-wl-pvc-data-11111111",
            StorageClassName = storageClassName,
        };

    private static PreflightCheck Find(WorkloadPreflightResult r, string id) => r.Checks.Single(c => c.Id == id);

    [Fact]
    public void Unreachable_api_short_circuits_to_a_single_blocking_error()
    {
        var r = Checker.Check(Spec(), Settings(), KubernetesClusterInspection.Unreachable("connection refused"));

        r.Checks.Should().ContainSingle();
        var check = r.Checks[0];
        check.Id.Should().Be("api-reachable");
        check.Severity.Should().Be(PreflightSeverity.Error);
        check.Passed.Should().BeFalse();
        check.Message.Should().Contain("connection refused");
        r.HasBlockingError.Should().BeTrue();
        r.AllPassed.Should().BeFalse();
    }

    [Fact]
    public void Reachable_api_emits_a_passing_api_reachable_check()
    {
        var r = Checker.Check(Spec(), Settings(), Cluster(nodes: new[] { Node("n1", "amd64") }));

        Find(r, "api-reachable").Passed.Should().BeTrue();
    }

    [Fact]
    public void Psa_baseline_plus_privileged_workload_is_a_blocking_error_with_fix()
    {
        var cluster = Cluster(
            nodes: new[] { Node("n1", "amd64") },
            psaLabels: new Dictionary<string, string> { ["pod-security.kubernetes.io/enforce"] = "baseline" });

        var r = Checker.Check(Spec(privileges: Privileged()), Settings(), cluster);

        var check = Find(r, "psa-enforcement");
        check.Passed.Should().BeFalse();
        check.Severity.Should().Be(PreflightSeverity.Error);
        check.Message.Should().Contain("baseline");
        check.FixSnippet.Should().NotBeNullOrWhiteSpace();
        r.HasBlockingError.Should().BeTrue();
    }

    [Fact]
    public void Psa_restricted_plus_hostnetwork_workload_is_a_blocking_error()
    {
        var cluster = Cluster(
            nodes: new[] { Node("n1", "amd64") },
            psaLabels: new Dictionary<string, string> { ["pod-security.kubernetes.io/enforce"] = "restricted" });

        var r = Checker.Check(Spec(network: new ResolvedNetwork { HostNetwork = true }), Settings(), cluster);

        var check = Find(r, "psa-enforcement");
        check.Passed.Should().BeFalse();
        check.Severity.Should().Be(PreflightSeverity.Error);
        check.Message.Should().Contain("restricted");
    }

    [Fact]
    public void Psa_baseline_plus_non_privileged_workload_passes()
    {
        var cluster = Cluster(
            nodes: new[] { Node("n1", "amd64") },
            psaLabels: new Dictionary<string, string> { ["pod-security.kubernetes.io/enforce"] = "baseline" });

        var r = Checker.Check(Spec(), Settings(), cluster);

        Find(r, "psa-enforcement").Passed.Should().BeTrue();
        r.HasBlockingError.Should().BeFalse();
    }

    [Fact]
    public void Psa_privileged_level_plus_privileged_workload_passes()
    {
        var cluster = Cluster(
            nodes: new[] { Node("n1", "amd64") },
            psaLabels: new Dictionary<string, string> { ["pod-security.kubernetes.io/enforce"] = "privileged" });

        var r = Checker.Check(Spec(privileges: Privileged()), Settings(), cluster);

        Find(r, "psa-enforcement").Passed.Should().BeTrue();
    }

    [Fact]
    public void Psa_unrecognized_enforce_value_is_inconclusive_info_not_a_green_no_enforcement_pass()
    {
        var cluster = Cluster(
            nodes: new[] { Node("n1", "amd64") },
            psaLabels: new Dictionary<string, string> { ["pod-security.kubernetes.io/enforce"] = "baselien" });

        var r = Checker.Check(Spec(privileges: Privileged()), Settings(), cluster);

        var check = Find(r, "psa-enforcement");
        check.Severity.Should().Be(PreflightSeverity.Info);
        check.Message.Should().Contain("unknown");
        check.Message.Should().NotContain("No blocking");
        r.HasBlockingError.Should().BeFalse();
        r.AllPassed.Should().BeFalse();
    }

    [Fact]
    public void Psa_empty_enforce_value_is_a_green_no_enforcement_pass()
    {
        var cluster = Cluster(
            nodes: new[] { Node("n1", "amd64") },
            psaLabels: new Dictionary<string, string> { ["pod-security.kubernetes.io/enforce"] = "" });

        var r = Checker.Check(Spec(privileges: Privileged()), Settings(), cluster);

        var check = Find(r, "psa-enforcement");
        check.Passed.Should().BeTrue();
        check.Message.Should().Contain("No blocking");
    }

    [Fact]
    public void Psa_absent_plus_privileged_workload_passes()
    {
        var r = Checker.Check(Spec(privileges: Privileged()), Settings(), Cluster(nodes: new[] { Node("n1", "amd64") }));

        Find(r, "psa-enforcement").Passed.Should().BeTrue();
    }

    [Fact]
    public void Psa_baseline_plus_added_capability_only_workload_is_a_blocking_error()
    {
        var cluster = Cluster(
            nodes: new[] { Node("n1", "amd64") },
            psaLabels: new Dictionary<string, string> { ["pod-security.kubernetes.io/enforce"] = "baseline" });

        var r = Checker.Check(Spec(privileges: new ResolvedPrivileges { Capabilities = new[] { "NET_ADMIN" } }), Settings(), cluster);

        var check = Find(r, "psa-enforcement");
        check.Passed.Should().BeFalse();
        check.Severity.Should().Be(PreflightSeverity.Error);
        check.Message.Should().Contain("baseline");
        check.FixSnippet.Should().NotBeNullOrWhiteSpace();
        (check.Message + " " + check.FixSnippet).Should().Contain("capabilit");
        r.HasBlockingError.Should().BeTrue();
    }

    [Fact]
    public void Psa_restricted_plus_added_capability_only_workload_is_a_blocking_error()
    {
        var cluster = Cluster(
            nodes: new[] { Node("n1", "amd64") },
            psaLabels: new Dictionary<string, string> { ["pod-security.kubernetes.io/enforce"] = "restricted" });

        var r = Checker.Check(Spec(privileges: new ResolvedPrivileges { Capabilities = new[] { "SYS_ADMIN" } }), Settings(), cluster);

        var check = Find(r, "psa-enforcement");
        check.Passed.Should().BeFalse();
        check.Severity.Should().Be(PreflightSeverity.Error);
        check.Message.Should().Contain("restricted");
        r.HasBlockingError.Should().BeTrue();
    }

    [Fact]
    public void Psa_baseline_plus_net_bind_service_only_capability_passes()
    {
        var cluster = Cluster(
            nodes: new[] { Node("n1", "amd64") },
            psaLabels: new Dictionary<string, string> { ["pod-security.kubernetes.io/enforce"] = "baseline" });

        var r = Checker.Check(Spec(privileges: new ResolvedPrivileges { Capabilities = new[] { "NET_BIND_SERVICE" } }), Settings(), cluster);

        Find(r, "psa-enforcement").Passed.Should().BeTrue();
        r.HasBlockingError.Should().BeFalse();
    }

    [Fact]
    public void Psa_baseline_tolerates_cap_prefix_and_case_on_the_baseline_allowed_capability()
    {
        var cluster = Cluster(
            nodes: new[] { Node("n1", "amd64") },
            psaLabels: new Dictionary<string, string> { ["pod-security.kubernetes.io/enforce"] = "baseline" });

        var r = Checker.Check(Spec(privileges: new ResolvedPrivileges { Capabilities = new[] { "CAP_net_bind_service" } }), Settings(), cluster);

        Find(r, "psa-enforcement").Passed.Should().BeTrue();
    }

    [Fact]
    public void Psa_baseline_mixed_capabilities_beyond_allowlist_is_a_blocking_error()
    {
        var cluster = Cluster(
            nodes: new[] { Node("n1", "amd64") },
            psaLabels: new Dictionary<string, string> { ["pod-security.kubernetes.io/enforce"] = "baseline" });

        var r = Checker.Check(
            Spec(privileges: new ResolvedPrivileges { Capabilities = new[] { "NET_BIND_SERVICE", "NET_ADMIN" } }),
            Settings(),
            cluster);

        Find(r, "psa-enforcement").Passed.Should().BeFalse();
    }

    [Fact]
    public void Psa_absent_plus_added_capability_only_workload_passes()
    {
        var r = Checker.Check(
            Spec(privileges: new ResolvedPrivileges { Capabilities = new[] { "NET_ADMIN" } }),
            Settings(),
            Cluster(nodes: new[] { Node("n1", "amd64") }));

        Find(r, "psa-enforcement").Passed.Should().BeTrue();
    }

    [Fact]
    public void Psa_privileged_level_plus_added_capability_only_workload_passes()
    {
        var cluster = Cluster(
            nodes: new[] { Node("n1", "amd64") },
            psaLabels: new Dictionary<string, string> { ["pod-security.kubernetes.io/enforce"] = "privileged" });

        var r = Checker.Check(Spec(privileges: new ResolvedPrivileges { Capabilities = new[] { "NET_ADMIN" } }), Settings(), cluster);

        Find(r, "psa-enforcement").Passed.Should().BeTrue();
    }

    [Fact]
    public void Quota_requiring_requests_with_no_requests_is_a_blocking_error()
    {
        var cluster = Cluster(nodes: new[] { Node("n1", "amd64") }, quotaRequiresRequests: true);

        var r = Checker.Check(Spec(resources: null), Settings(), cluster);

        var check = Find(r, "resourcequota-requests");
        check.Passed.Should().BeFalse();
        check.Severity.Should().Be(PreflightSeverity.Error);
        check.FixSnippet.Should().NotBeNullOrWhiteSpace();
        r.HasBlockingError.Should().BeTrue();
    }

    [Fact]
    public void Quota_requiring_requests_with_requests_declared_passes()
    {
        var cluster = Cluster(nodes: new[] { Node("n1", "amd64") }, quotaRequiresRequests: true);
        var resources = new ResolvedResources { RequestsCpu = "100m", RequestsMemory = "128Mi" };

        var r = Checker.Check(Spec(resources: resources), Settings(), cluster);

        Find(r, "resourcequota-requests").Passed.Should().BeTrue();
        r.HasBlockingError.Should().BeFalse();
    }

    [Fact]
    public void No_quota_requiring_requests_passes_even_without_requests()
    {
        var r = Checker.Check(Spec(resources: null), Settings(), Cluster(nodes: new[] { Node("n1", "amd64") }));

        Find(r, "resourcequota-requests").Passed.Should().BeTrue();
    }

    [Fact]
    public void Amd64_image_on_arm_only_nodes_is_a_non_blocking_warning()
    {
        var cluster = Cluster(nodes: new[] { Node("n1", "arm64"), Node("n2", "arm64") });

        var r = Checker.Check(Spec(platforms: new[] { "linux/amd64" }), Settings(), cluster);

        var check = Find(r, "arch-affinity");
        check.Passed.Should().BeFalse();
        check.Severity.Should().Be(PreflightSeverity.Warning);
        check.Message.Should().Contain("amd64");
        r.HasBlockingError.Should().BeFalse();
    }

    [Fact]
    public void Arch_matching_node_passes()
    {
        var cluster = Cluster(nodes: new[] { Node("n1", "arm64"), Node("n2", "amd64") });

        var r = Checker.Check(Spec(platforms: new[] { "linux/amd64" }), Settings(), cluster);

        Find(r, "arch-affinity").Passed.Should().BeTrue();
        r.HasBlockingError.Should().BeFalse();
    }

    [Fact]
    public void Arch_check_honours_node_selector_when_scoping_candidate_nodes()
    {
        var cluster = Cluster(nodes: new[]
        {
            Node("arm-ble", "arm64", new Dictionary<string, string> { ["reeve.io/ble"] = "true" }),
            Node("amd-plain", "amd64"),
        });

        var r = Checker.Check(
            Spec(platforms: new[] { "linux/amd64" }, nodeSelector: new Dictionary<string, string> { ["reeve.io/ble"] = "true" }),
            Settings(),
            cluster);

        Find(r, "arch-affinity").Passed.Should().BeFalse();
    }

    [Fact]
    public void Arch_check_with_unsatisfiable_node_selector_is_inconclusive_not_a_fake_arch_error()
    {
        var cluster = Cluster(nodes: new[] { Node("n1", "amd64"), Node("n2", "amd64"), Node("n3", "amd64") });

        var r = Checker.Check(
            Spec(platforms: new[] { "linux/amd64" }, nodeSelector: new Dictionary<string, string> { ["reeve.io/ble"] = "true" }),
            Settings(),
            cluster);

        var check = Find(r, "arch-affinity");
        check.Passed.Should().BeFalse();
        check.Severity.Should().Be(PreflightSeverity.Info);
        check.Message.Should().Contain("node selector");
        check.Message.Should().Contain("could not be evaluated");
        r.HasBlockingError.Should().BeFalse();
    }

    [Fact]
    public void Arch_check_is_skipped_when_no_platforms_declared()
    {
        var r = Checker.Check(Spec(platforms: new List<string>()), Settings(), Cluster(nodes: new[] { Node("n1", "arm64") }));

        Find(r, "arch-affinity").Passed.Should().BeTrue();
    }

    [Fact]
    public void Arch_check_with_platforms_but_zero_nodes_is_an_inconclusive_info_not_a_green_pass()
    {
        var r = Checker.Check(Spec(platforms: new[] { "linux/amd64" }), Settings(), Cluster(nodes: new List<ClusterNodeInfo>()));

        var check = Find(r, "arch-affinity");
        check.Passed.Should().BeFalse();
        check.Severity.Should().Be(PreflightSeverity.Info);
        check.Message.Should().Contain("no nodes");
        check.Message.Should().Contain("could not be evaluated");
        r.HasBlockingError.Should().BeFalse();
    }

    [Fact]
    public void Arch_check_with_no_platforms_and_zero_nodes_is_a_normal_pass()
    {
        var r = Checker.Check(Spec(platforms: new List<string>()), Settings(), Cluster(nodes: new List<ClusterNodeInfo>()));

        var check = Find(r, "arch-affinity");
        check.Passed.Should().BeTrue();
        check.Message.Should().NotContain("could not be evaluated");
    }

    [Fact]
    public void SingleNode_selector_matching_zero_nodes_is_a_non_blocking_warning()
    {
        var cluster = Cluster(nodes: new[] { Node("n1", "amd64") });

        var r = Checker.Check(
            Spec(kind: WorkloadKind.SingleNode, nodeSelector: new Dictionary<string, string> { ["reeve.io/ble"] = "true" }),
            Settings(),
            cluster);

        var check = Find(r, "single-node-selector");
        check.Passed.Should().BeFalse();
        check.Severity.Should().Be(PreflightSeverity.Warning);
        check.FixSnippet.Should().NotBeNullOrWhiteSpace();
        r.HasBlockingError.Should().BeFalse();
    }

    [Fact]
    public void SingleNode_selector_matching_multiple_nodes_passes_k8s_schedules_the_single_replica_on_one()
    {
        var cluster = Cluster(nodes: new[]
        {
            Node("n1", "amd64", new Dictionary<string, string> { ["reeve.io/ble"] = "true" }),
            Node("n2", "amd64", new Dictionary<string, string> { ["reeve.io/ble"] = "true" }),
        });

        var r = Checker.Check(
            Spec(kind: WorkloadKind.SingleNode, nodeSelector: new Dictionary<string, string> { ["reeve.io/ble"] = "true" }),
            Settings(),
            cluster);

        var check = Find(r, "single-node-selector");
        check.Passed.Should().BeTrue();
        check.Message.Should().Contain("2");
        check.Message.Should().Contain("nodePin");
        r.HasBlockingError.Should().BeFalse();
    }

    [Fact]
    public void SingleNode_selector_matching_exactly_one_node_passes()
    {
        var cluster = Cluster(nodes: new[]
        {
            Node("n1", "amd64", new Dictionary<string, string> { ["reeve.io/ble"] = "true" }),
            Node("n2", "amd64"),
        });

        var r = Checker.Check(
            Spec(kind: WorkloadKind.SingleNode, nodeSelector: new Dictionary<string, string> { ["reeve.io/ble"] = "true" }),
            Settings(),
            cluster);

        Find(r, "single-node-selector").Passed.Should().BeTrue();
        r.HasBlockingError.Should().BeFalse();
    }

    [Fact]
    public void Non_single_node_kind_does_not_emit_the_single_node_check()
    {
        var r = Checker.Check(Spec(kind: WorkloadKind.Service), Settings(), Cluster(nodes: new[] { Node("n1", "amd64") }));

        r.Checks.Should().NotContain(c => c.Id == "single-node-selector");
    }

    [Fact]
    public void WaitForFirstConsumer_storageclass_is_a_warning_not_a_blocker()
    {
        var cluster = Cluster(
            nodes: new[] { Node("n1", "amd64") },
            storageClasses: new[]
            {
                new ClusterStorageClassInfo { Name = "ebs", Provisioner = "ebs.csi.aws.com", VolumeBindingMode = "WaitForFirstConsumer" },
            });

        var r = Checker.Check(Spec(volumes: new[] { Volume("ebs") }), Settings(), cluster);

        var check = Find(r, "storageclass-topology");
        check.Passed.Should().BeFalse();
        check.Severity.Should().Be(PreflightSeverity.Warning);
        check.Message.Should().Contain("ebs");
        r.HasBlockingError.Should().BeFalse();
    }

    [Fact]
    public void LocalPath_provisioner_storageclass_is_a_warning()
    {
        var cluster = Cluster(
            nodes: new[] { Node("n1", "amd64") },
            storageClasses: new[]
            {
                new ClusterStorageClassInfo { Name = "local-path", Provisioner = "rancher.io/local-path", VolumeBindingMode = "Immediate" },
            });

        var r = Checker.Check(Spec(volumes: new[] { Volume("local-path") }), Settings(), cluster);

        var check = Find(r, "storageclass-topology");
        check.Passed.Should().BeFalse();
        check.Severity.Should().Be(PreflightSeverity.Warning);
        r.HasBlockingError.Should().BeFalse();
    }

    [Fact]
    public void Immediate_binding_storageclass_passes()
    {
        var cluster = Cluster(
            nodes: new[] { Node("n1", "amd64") },
            storageClasses: new[]
            {
                new ClusterStorageClassInfo { Name = "ebs", Provisioner = "ebs.csi.aws.com", VolumeBindingMode = "Immediate" },
            });

        var r = Checker.Check(Spec(volumes: new[] { Volume("ebs") }), Settings(), cluster);

        Find(r, "storageclass-topology").Passed.Should().BeTrue();
        Find(r, "storageclass-missing").Passed.Should().BeTrue();
        r.AllPassed.Should().BeTrue();
    }

    [Fact]
    public void Volume_without_explicit_class_resolves_the_settings_default_storageclass()
    {
        var cluster = Cluster(
            nodes: new[] { Node("n1", "amd64") },
            storageClasses: new[]
            {
                new ClusterStorageClassInfo { Name = "local-path", Provisioner = "rancher.io/local-path", VolumeBindingMode = "WaitForFirstConsumer" },
            });

        var r = Checker.Check(Spec(volumes: new[] { Volume(null) }), Settings(defaultStorageClass: "local-path"), cluster);

        Find(r, "storageclass-topology").Severity.Should().Be(PreflightSeverity.Warning);
    }

    [Fact]
    public void No_volumes_does_not_emit_the_storageclass_check()
    {
        var r = Checker.Check(Spec(volumes: new List<ResolvedVolume>()), Settings(), Cluster(nodes: new[] { Node("n1", "amd64") }));

        r.Checks.Should().NotContain(c => c.Id == "storageclass-topology" || c.Id == "storageclass-missing");
    }

    [Fact]
    public void Volume_with_no_class_and_no_settings_default_resolves_the_cluster_default_storageclass()
    {
        var cluster = Cluster(
            nodes: new[] { Node("n1", "amd64") },
            storageClasses: new[]
            {
                new ClusterStorageClassInfo { Name = "fast", Provisioner = "ebs.csi.aws.com", VolumeBindingMode = "Immediate" },
                new ClusterStorageClassInfo { Name = "local-path", Provisioner = "rancher.io/local-path", VolumeBindingMode = "WaitForFirstConsumer", IsDefault = true },
            });

        var r = Checker.Check(Spec(volumes: new[] { Volume(null) }), Settings(defaultStorageClass: null), cluster);

        Find(r, "storageclass-topology").Severity.Should().Be(PreflightSeverity.Warning);
    }

    [Fact]
    public void Volume_with_no_class_and_immediate_cluster_default_passes()
    {
        var cluster = Cluster(
            nodes: new[] { Node("n1", "amd64") },
            storageClasses: new[]
            {
                new ClusterStorageClassInfo { Name = "ebs", Provisioner = "ebs.csi.aws.com", VolumeBindingMode = "Immediate", IsDefault = true },
            });

        var r = Checker.Check(Spec(volumes: new[] { Volume(null) }), Settings(defaultStorageClass: null), cluster);

        Find(r, "storageclass-topology").Passed.Should().BeTrue();
    }

    [Fact]
    public void Psa_baseline_plus_a_baseline_allowed_capability_CHOWN_passes()
    {
        var cluster = Cluster(
            nodes: new[] { Node("n1", "amd64") },
            psaLabels: new Dictionary<string, string> { ["pod-security.kubernetes.io/enforce"] = "baseline" });

        var r = Checker.Check(Spec(privileges: new ResolvedPrivileges { Capabilities = new[] { "CHOWN" } }), Settings(), cluster);

        Find(r, "psa-enforcement").Passed.Should().BeTrue();
        r.HasBlockingError.Should().BeFalse();
    }

    [Fact]
    public void Psa_baseline_plus_SYS_ADMIN_capability_is_a_blocking_error()
    {
        var cluster = Cluster(
            nodes: new[] { Node("n1", "amd64") },
            psaLabels: new Dictionary<string, string> { ["pod-security.kubernetes.io/enforce"] = "baseline" });

        var r = Checker.Check(Spec(privileges: new ResolvedPrivileges { Capabilities = new[] { "SYS_ADMIN" } }), Settings(), cluster);

        var check = Find(r, "psa-enforcement");
        check.Passed.Should().BeFalse();
        check.Severity.Should().Be(PreflightSeverity.Error);
        r.HasBlockingError.Should().BeTrue();
    }

    [Fact]
    public void Psa_baseline_plus_run_as_root_passes()
    {
        var cluster = Cluster(
            nodes: new[] { Node("n1", "amd64") },
            psaLabels: new Dictionary<string, string> { ["pod-security.kubernetes.io/enforce"] = "baseline" });

        var r = Checker.Check(Spec(privileges: new ResolvedPrivileges { RunAsRoot = true }), Settings(), cluster);

        Find(r, "psa-enforcement").Passed.Should().BeTrue();
        r.HasBlockingError.Should().BeFalse();
    }

    [Fact]
    public void Psa_restricted_plus_run_as_root_is_a_blocking_error()
    {
        var cluster = Cluster(
            nodes: new[] { Node("n1", "amd64") },
            psaLabels: new Dictionary<string, string> { ["pod-security.kubernetes.io/enforce"] = "restricted" });

        var r = Checker.Check(Spec(privileges: new ResolvedPrivileges { RunAsRoot = true }), Settings(), cluster);

        var check = Find(r, "psa-enforcement");
        check.Passed.Should().BeFalse();
        check.Severity.Should().Be(PreflightSeverity.Error);
        check.Message.Should().Contain("restricted");
        r.HasBlockingError.Should().BeTrue();
    }

    [Fact]
    public void Psa_baseline_plus_run_as_root_AND_baseline_cap_passes()
    {
        var cluster = Cluster(
            nodes: new[] { Node("n1", "amd64") },
            psaLabels: new Dictionary<string, string> { ["pod-security.kubernetes.io/enforce"] = "baseline" });

        var r = Checker.Check(
            Spec(privileges: new ResolvedPrivileges { RunAsRoot = true, Capabilities = new[] { "KILL" } }),
            Settings(),
            cluster);

        Find(r, "psa-enforcement").Passed.Should().BeTrue();
    }

    [Fact]
    public void Psa_restricted_plus_NET_ADMIN_capability_is_a_blocking_error()
    {
        var cluster = Cluster(
            nodes: new[] { Node("n1", "amd64") },
            psaLabels: new Dictionary<string, string> { ["pod-security.kubernetes.io/enforce"] = "restricted" });

        var r = Checker.Check(Spec(privileges: new ResolvedPrivileges { Capabilities = new[] { "NET_ADMIN" } }), Settings(), cluster);

        var check = Find(r, "psa-enforcement");
        check.Passed.Should().BeFalse();
        check.Severity.Should().Be(PreflightSeverity.Error);
        r.HasBlockingError.Should().BeTrue();
    }

    [Fact]
    public void Psa_restricted_plus_a_baseline_only_cap_CHOWN_is_a_blocking_error()
    {
        var cluster = Cluster(
            nodes: new[] { Node("n1", "amd64") },
            psaLabels: new Dictionary<string, string> { ["pod-security.kubernetes.io/enforce"] = "restricted" });

        var r = Checker.Check(Spec(privileges: new ResolvedPrivileges { Capabilities = new[] { "CHOWN" } }), Settings(), cluster);

        Find(r, "psa-enforcement").Passed.Should().BeFalse();
        r.HasBlockingError.Should().BeTrue();
    }

    [Fact]
    public void Psa_restricted_plus_NET_BIND_SERVICE_only_capability_passes()
    {
        var cluster = Cluster(
            nodes: new[] { Node("n1", "amd64") },
            psaLabels: new Dictionary<string, string> { ["pod-security.kubernetes.io/enforce"] = "restricted" });

        var r = Checker.Check(Spec(privileges: new ResolvedPrivileges { Capabilities = new[] { "NET_BIND_SERVICE" } }), Settings(), cluster);

        Find(r, "psa-enforcement").Passed.Should().BeTrue();
    }

    [Fact]
    public void Psa_restricted_plus_plain_non_privileged_workload_passes()
    {
        var cluster = Cluster(
            nodes: new[] { Node("n1", "amd64") },
            psaLabels: new Dictionary<string, string> { ["pod-security.kubernetes.io/enforce"] = "restricted" });

        var r = Checker.Check(Spec(), Settings(), cluster);

        Find(r, "psa-enforcement").Passed.Should().BeTrue();
        r.HasBlockingError.Should().BeFalse();
    }

    [Fact]
    public void Psa_level_unknown_because_namespace_unreadable_is_inconclusive_info_not_pass_or_error()
    {
        var cluster = Cluster(
            nodes: new[] { Node("n1", "amd64") },
            psaLabels: new Dictionary<string, string>(),
            psaAvailable: false);

        var r = Checker.Check(Spec(privileges: Privileged()), Settings(), cluster);

        var check = Find(r, "psa-enforcement");
        check.Severity.Should().Be(PreflightSeverity.Info);
        check.Passed.Should().BeFalse();
        check.Message.Should().Contain("unknown");
        r.HasBlockingError.Should().BeFalse();
    }

    [Fact]
    public void Quota_requiring_requests_with_limits_declared_passes()
    {
        var cluster = Cluster(nodes: new[] { Node("n1", "amd64") }, quotaRequiresRequests: true);
        var resources = new ResolvedResources { LimitsCpu = "500m", LimitsMemory = "256Mi" };

        var r = Checker.Check(Spec(resources: resources), Settings(), cluster);

        Find(r, "resourcequota-requests").Passed.Should().BeTrue();
        r.HasBlockingError.Should().BeFalse();
    }

    [Fact]
    public void Quota_requiring_requests_but_quota_unreadable_is_inconclusive_info_not_error()
    {
        var cluster = Cluster(nodes: new[] { Node("n1", "amd64") }, quotaRequiresRequests: false, resourceQuotaAvailable: false);

        var r = Checker.Check(Spec(resources: null), Settings(), cluster);

        var check = Find(r, "resourcequota-requests");
        check.Severity.Should().Be(PreflightSeverity.Info);
        check.Passed.Should().BeFalse();
        r.HasBlockingError.Should().BeFalse();
    }

    [Fact]
    public void SingleNode_selector_matching_only_a_cordoned_node_is_a_non_blocking_warning()
    {
        var cluster = Cluster(nodes: new[]
        {
            Node("n1", "amd64", new Dictionary<string, string> { ["reeve.io/ble"] = "true" }, schedulable: false),
        });

        var r = Checker.Check(
            Spec(kind: WorkloadKind.SingleNode, nodeSelector: new Dictionary<string, string> { ["reeve.io/ble"] = "true" }),
            Settings(),
            cluster);

        var check = Find(r, "single-node-selector");
        check.Passed.Should().BeFalse();
        check.Severity.Should().Be(PreflightSeverity.Warning);
        check.Message.Should().NotContain("matches exactly one node");
        check.Message.Should().Contain("cordoned");
        r.HasBlockingError.Should().BeFalse();
    }

    [Fact]
    public void SingleNode_selector_with_one_schedulable_and_one_cordoned_match_passes_as_exactly_one()
    {
        var cluster = Cluster(nodes: new[]
        {
            Node("live", "amd64", new Dictionary<string, string> { ["reeve.io/ble"] = "true" }),
            Node("cordoned", "amd64", new Dictionary<string, string> { ["reeve.io/ble"] = "true" }, schedulable: false),
        });

        var r = Checker.Check(
            Spec(kind: WorkloadKind.SingleNode, nodeSelector: new Dictionary<string, string> { ["reeve.io/ble"] = "true" }),
            Settings(),
            cluster);

        var check = Find(r, "single-node-selector");
        check.Passed.Should().BeTrue();
        check.Message.Should().Contain("live");
    }

    [Fact]
    public void SingleNode_check_is_inconclusive_when_nodes_unreadable()
    {
        var cluster = Cluster(nodes: new List<ClusterNodeInfo>(), nodesAvailable: false);

        var r = Checker.Check(
            Spec(kind: WorkloadKind.SingleNode, nodeSelector: new Dictionary<string, string> { ["reeve.io/ble"] = "true" }),
            Settings(),
            cluster);

        var check = Find(r, "single-node-selector");
        check.Severity.Should().Be(PreflightSeverity.Info);
        check.Passed.Should().BeFalse();
        r.HasBlockingError.Should().BeFalse();
    }

    [Fact]
    public void Arch_check_is_inconclusive_when_nodes_unreadable()
    {
        var cluster = Cluster(nodes: new List<ClusterNodeInfo>(), nodesAvailable: false);

        var r = Checker.Check(Spec(platforms: new[] { "linux/amd64" }), Settings(), cluster);

        var check = Find(r, "arch-affinity");
        check.Severity.Should().Be(PreflightSeverity.Info);
        check.Passed.Should().BeFalse();
        r.HasBlockingError.Should().BeFalse();
    }

    [Fact]
    public void Named_but_nonexistent_storageclass_is_a_warning_not_a_silent_pass()
    {
        var cluster = Cluster(
            nodes: new[] { Node("n1", "amd64") },
            storageClasses: new[]
            {
                new ClusterStorageClassInfo { Name = "ebs", Provisioner = "ebs.csi.aws.com", VolumeBindingMode = "Immediate" },
            });

        var r = Checker.Check(Spec(volumes: new[] { Volume("does-not-exist") }), Settings(), cluster);

        var check = Find(r, "storageclass-missing");
        check.Passed.Should().BeFalse();
        check.Severity.Should().Be(PreflightSeverity.Warning);
        check.Message.Should().Contain("does-not-exist");
        check.Message.Should().Contain("Pending");
        r.HasBlockingError.Should().BeFalse();

        var topology = Find(r, "storageclass-topology");
        topology.Severity.Should().Be(PreflightSeverity.Info);
        topology.Passed.Should().BeFalse();
    }

    [Fact]
    public void Volume_with_no_class_and_no_cluster_default_discoverable_is_a_warning()
    {
        var cluster = Cluster(
            nodes: new[] { Node("n1", "amd64") },
            storageClasses: new[]
            {
                new ClusterStorageClassInfo { Name = "ebs", Provisioner = "ebs.csi.aws.com", VolumeBindingMode = "Immediate", IsDefault = false },
            });

        var r = Checker.Check(Spec(volumes: new[] { Volume(null) }), Settings(defaultStorageClass: null), cluster);

        var check = Find(r, "storageclass-missing");
        check.Passed.Should().BeFalse();
        check.Severity.Should().Be(PreflightSeverity.Warning);
        check.Message.Should().Contain("cluster default: none marked");
        r.HasBlockingError.Should().BeFalse();
    }

    [Fact]
    public void Missing_and_topology_bound_classes_are_reported_under_their_own_ids()
    {
        var cluster = Cluster(
            nodes: new[] { Node("n1", "amd64") },
            storageClasses: new[]
            {
                new ClusterStorageClassInfo { Name = "local-path", Provisioner = "rancher.io/local-path", VolumeBindingMode = "WaitForFirstConsumer" },
            });

        var r = Checker.Check(Spec(volumes: new[] { Volume("gone"), Volume("local-path") }), Settings(), cluster);

        var missing = Find(r, "storageclass-missing");
        missing.Passed.Should().BeFalse();
        missing.Severity.Should().Be(PreflightSeverity.Warning);
        missing.Message.Should().Contain("gone").And.NotContain("local-path");

        var topology = Find(r, "storageclass-topology");
        topology.Passed.Should().BeFalse();
        topology.Severity.Should().Be(PreflightSeverity.Warning);
        topology.Message.Should().Contain("local-path").And.NotContain("gone");
    }

    [Fact]
    public void Topology_bound_class_that_exists_passes_the_missing_check()
    {
        var cluster = Cluster(
            nodes: new[] { Node("n1", "amd64") },
            storageClasses: new[]
            {
                new ClusterStorageClassInfo { Name = "local-path", Provisioner = "rancher.io/local-path", VolumeBindingMode = "WaitForFirstConsumer" },
            });

        var r = Checker.Check(Spec(volumes: new[] { Volume("local-path") }), Settings(), cluster);

        Find(r, "storageclass-missing").Passed.Should().BeTrue();
        Find(r, "storageclass-topology").Passed.Should().BeFalse();
    }

    [Fact]
    public void Storageclass_check_is_inconclusive_when_storageclasses_unreadable()
    {
        var cluster = Cluster(
            nodes: new[] { Node("n1", "amd64") },
            storageClasses: new List<ClusterStorageClassInfo>(),
            storageClassesAvailable: false);

        var r = Checker.Check(Spec(volumes: new[] { Volume("ebs") }), Settings(), cluster);

        foreach (var id in new[] { "storageclass-missing", "storageclass-topology" })
        {
            var check = Find(r, id);
            check.Severity.Should().Be(PreflightSeverity.Info);
            check.Passed.Should().BeFalse();
        }

        r.HasBlockingError.Should().BeFalse();
    }

    [Fact]
    public void A_clean_spec_on_a_healthy_cluster_all_pass()
    {
        var cluster = Cluster(
            nodes: new[] { Node("n1", "amd64") },
            storageClasses: new[]
            {
                new ClusterStorageClassInfo { Name = "fast", Provisioner = "ebs.csi.aws.com", VolumeBindingMode = "Immediate" },
            });

        var r = Checker.Check(
            Spec(platforms: new[] { "linux/amd64" }, resources: new ResolvedResources { RequestsCpu = "50m", RequestsMemory = "64Mi" }, volumes: new[] { Volume("fast") }),
            Settings(),
            cluster);

        r.AllPassed.Should().BeTrue();
        r.HasBlockingError.Should().BeFalse();
    }

    [Fact]
    public void A_result_with_only_inconclusive_checks_is_not_AllPassed_but_does_not_block_deploy()
    {
        var cluster = Cluster(
            nodes: new List<ClusterNodeInfo>(),
            nodesAvailable: false,
            psaAvailable: false,
            storageClassesAvailable: false,
            resourceQuotaAvailable: false);

        var r = Checker.Check(
            Spec(
                platforms: new[] { "linux/amd64" },
                kind: WorkloadKind.SingleNode,
                nodeSelector: new Dictionary<string, string> { ["reeve.io/ble"] = "true" },
                volumes: new[] { Volume("ebs") }),
            Settings(),
            cluster);

        r.Checks.Where(c => c.Id != "api-reachable").Should().OnlyContain(c => !c.Passed && c.Severity == PreflightSeverity.Info);
        r.AllPassed.Should().BeFalse();
        r.HasBlockingError.Should().BeFalse();
    }

    [Fact]
    public void ScanPerNode_with_unreadable_nodes_is_a_blocking_error_not_a_silent_zero_success()
    {
        var r = Checker.Check(
            Spec(kind: WorkloadKind.ScanPerNode),
            Settings(),
            Cluster(nodesAvailable: false));

        var check = Find(r, "scan-nodes-readable");
        check.Passed.Should().BeFalse();
        check.Severity.Should().Be(PreflightSeverity.Error);
        r.HasBlockingError.Should().BeTrue("a scan-per-node run must fail loud on a nodes 403, never create zero Jobs as a fake success");
    }

    [Fact]
    public void ScanPerNode_with_readable_nodes_passes_the_scan_nodes_readable_check()
    {
        var r = Checker.Check(
            Spec(kind: WorkloadKind.ScanPerNode),
            Settings(),
            Cluster(nodes: new[] { Node("n1", "amd64") }, nodesAvailable: true));

        Find(r, "scan-nodes-readable").Passed.Should().BeTrue();
        r.HasBlockingError.Should().BeFalse();
    }

    [Fact]
    public void Non_scan_kind_does_not_emit_the_scan_nodes_readable_check()
    {
        foreach (var kind in new[] { WorkloadKind.Service, WorkloadKind.SingleNode, WorkloadKind.PerNode })
        {
            var r = Checker.Check(Spec(kind: kind), Settings(), Cluster(nodesAvailable: false));
            r.Checks.Should().NotContain(c => c.Id == "scan-nodes-readable");
        }
    }

    [Fact]
    public void WorkloadPreflightResult_ReadyNodeNames_defaults_to_null_and_is_additive()
    {
        var r = new WorkloadPreflightResult { Checks = System.Array.Empty<PreflightCheck>() };
        r.ReadyNodeNames.Should().BeNull("non-scan / non-k8s / unreadable ⇒ null; only a scan-per-node k8s preflight populates it");
        (r with { ReadyNodeNames = new[] { "n1", "n2" } }).ReadyNodeNames.Should().Equal("n1", "n2");
    }

    [Fact]
    public void A_device_binding_workload_is_told_it_runs_privileged_and_why()
    {
        var r = Checker.Check(
            Spec(privileges: new ResolvedPrivileges { DevicePassthroughPrivileged = true, DevicePaths = new[] { "/dev/ttyUSB0" } }),
            Settings(),
            Cluster(nodes: new[] { Node("n1", "amd64") }));

        var check = Find(r, "device-passthrough");
        check.Passed.Should().BeTrue();
        check.Message.Should().Contain("/dev/ttyUSB0").And.Contain("privileged").And.Contain("device cgroup");
    }

    [Fact]
    public void A_namespace_that_refuses_privileged_pods_blocks_a_device_binding_workload()
    {
        var r = Checker.Check(
            Spec(privileges: new ResolvedPrivileges { DevicePassthroughPrivileged = true, DevicePaths = new[] { "/dev/ttyUSB0" } }),
            Settings(),
            Cluster(
                nodes: new[] { Node("n1", "amd64") },
                psaLabels: new Dictionary<string, string> { ["pod-security.kubernetes.io/enforce"] = "baseline" }));

        var check = Find(r, "psa-enforcement");
        check.Passed.Should().BeFalse();
        check.Message.Should().Contain("privileged container");
        r.HasBlockingError.Should().BeTrue();
    }

    [Fact]
    public void A_user_namespace_skipped_for_a_bound_device_is_a_warning_and_never_blocks()
    {
        var spec = Spec(privileges: new ResolvedPrivileges { DevicePassthroughPrivileged = true, DevicePaths = new[] { "/dev/ttyUSB0" } })
            with { UserNamespaceSkippedForDevicePassthrough = true };
        var r = Checker.Check(spec, Settings(), Cluster(nodes: new[] { Node("n1", "amd64") }, serverVersion: new System.Version(1, 35)));
        var check = Find(r, "user-namespaces");

        check.Passed.Should().BeFalse();
        check.Severity.Should().Be(PreflightSeverity.Warning);
        check.Message.Should().Contain("binds a device");
        r.HasBlockingError.Should().BeFalse();
    }

    [Fact]
    public void A_workload_without_devices_gets_no_device_passthrough_check()
        => Checker.Check(Spec(), Settings(), Cluster(nodes: new[] { Node("n1", "amd64") }))
            .Checks.Should().NotContain(c => c.Id == "device-passthrough");

    private const string DeviceGroupCheckId = "device-group-resolved";

    private static WorkloadPreflightResult CheckGroupGrant(bool joins, IReadOnlyList<int> gids, bool runAsRoot)
        => Checker.Check(
            Spec(privileges: new ResolvedPrivileges
            {
                JoinsDeviceGroups = joins,
                DeviceGroupIds = gids,
                RunAsRoot = runAsRoot,
                DevicePassthroughPrivileged = true,
                DevicePaths = new[] { "/dev/dri/renderD128" },
            }),
            Settings(),
            Cluster(nodes: new[] { Node("n1", "amd64") }));

    [Fact]
    public void A_group_auto_slot_with_no_resolved_group_id_warns_that_the_device_may_be_unopenable()
    {
        var r = CheckGroupGrant(joins: true, gids: new List<int>(), runAsRoot: false);

        var check = Find(r, DeviceGroupCheckId);
        check.Passed.Should().BeFalse();
        check.Severity.Should().Be(PreflightSeverity.Warning);
        check.Message.Should().Contain("/dev/dri/renderD128");
        check.Message.Should().Contain("open");
        check.Message.Should().Contain("root");
        (check.Message + " " + check.FixSnippet).Should().Contain("discovery scan");
        r.HasBlockingError.Should().BeFalse("an unresolved group id must never block a deploy");
    }

    [Fact]
    public void A_group_auto_slot_with_a_resolved_group_id_warns_about_nothing()
    {
        var r = CheckGroupGrant(joins: true, gids: new[] { 993 }, runAsRoot: false);

        r.Checks.Should().NotContain(c => c.Id == DeviceGroupCheckId);
    }

    [Fact]
    public void A_workload_that_declares_no_group_grant_warns_about_nothing()
    {
        var r = CheckGroupGrant(joins: false, gids: new List<int>(), runAsRoot: false);

        r.Checks.Should().NotContain(c => c.Id == DeviceGroupCheckId);
    }

    [Fact]
    public void A_root_workload_with_no_resolved_group_id_warns_about_nothing()
    {
        var r = CheckGroupGrant(joins: true, gids: new List<int>(), runAsRoot: true);

        r.Checks.Should().NotContain(c => c.Id == DeviceGroupCheckId);
    }

    [Fact]
    public void User_namespaces_check_runs_only_when_the_spec_asks()
        => Checker.Check(Spec(), Settings(), Cluster(nodes: new[] { Node("n1", "amd64", userNamespaces: false) }))
            .Checks.Should().NotContain(c => c.Id == "user-namespaces");

    [Fact]
    public void A_skipped_host_networked_workload_is_a_warning_and_never_blocks()
    {
        var spec = Spec(network: new ResolvedNetwork { HostNetwork = true, Mode = WorkloadNetworkMode.HostNetwork })
            with { UserNamespaceSkippedForHostNetwork = true };
        var r = Checker.Check(spec, Settings(), Cluster(serverVersion: new System.Version(1, 35)));
        var check = Find(r, "user-namespaces");

        check.Passed.Should().BeFalse();
        check.Severity.Should().Be(PreflightSeverity.Warning);
        check.Message.Should().Contain("host network");
        r.HasBlockingError.Should().BeFalse();
    }

    [Theory]
    [InlineData(32, false)]
    [InlineData(33, true)]
    public void The_server_version_boundary_is_1_33(int minor, bool passes)
    {
        var check = Find(Checker.Check(UserNsSpec(), Settings(), Cluster(serverVersion: new System.Version(1, minor))), "user-namespaces");

        check.Passed.Should().Be(passes);
        if (!passes)
        {
            check.Severity.Should().Be(PreflightSeverity.Warning);
            check.Message.Should().Contain($"1.{minor}");
        }
    }

    [Fact]
    public void Nodes_reporting_no_support_are_named_in_a_warning_and_unknown_nodes_are_not()
    {
        var cluster = Cluster(serverVersion: new System.Version(1, 35), nodes: new[]
        {
            Node("refuses", "amd64", userNamespaces: false),
            Node("silent", "amd64"),
            Node("supports", "amd64", userNamespaces: true),
            Node("cordoned", "amd64", schedulable: false, userNamespaces: false),
        });

        var check = Find(Checker.Check(UserNsSpec(), Settings(), cluster), "user-namespaces");
        check.Severity.Should().Be(PreflightSeverity.Warning);
        check.Message.Should().Contain("refuses").And.NotContain("silent").And.NotContain("supports").And.NotContain("cordoned");
    }

    [Fact]
    public void A_node_outside_the_selector_is_not_named()
    {
        var cluster = Cluster(serverVersion: new System.Version(1, 35), nodes: new[]
        {
            Node("a", "amd64", new Dictionary<string, string> { ["zone"] = "a" }, userNamespaces: true),
            Node("b", "amd64", new Dictionary<string, string> { ["zone"] = "b" }, userNamespaces: false),
        });

        Find(Checker.Check(UserNsSpec(new Dictionary<string, string> { ["zone"] = "a" }), Settings(), cluster), "user-namespaces")
            .Passed.Should().BeTrue();
    }

    [Fact]
    public void A_supported_cluster_and_an_unknown_version_pass()
    {
        Find(Checker.Check(UserNsSpec(), Settings(), Cluster(serverVersion: new System.Version(1, 35))), "user-namespaces").Passed.Should().BeTrue();
        Find(Checker.Check(UserNsSpec(), Settings(), Cluster(serverVersion: null)), "user-namespaces").Passed.Should().BeTrue();
    }

    [Theory]
    [InlineData(35, true)]
    [InlineData(36, true)]
    [InlineData(34, false)]
    public void Restricted_psa_exempts_run_as_root_only_with_a_user_namespace_on_1_35_or_newer(int minor, bool passes)
    {
        var cluster = Cluster(nodes: new[] { Node("n1", "amd64") },
            psaLabels: new Dictionary<string, string> { ["pod-security.kubernetes.io/enforce"] = "restricted" },
            serverVersion: new System.Version(1, minor));
        Find(Checker.Check(UserNsSpec(runAsRoot: true), Settings(), cluster), "psa-enforcement").Passed.Should().Be(passes);
    }

    [Fact]
    public void Restricted_psa_does_not_exempt_run_as_root_when_the_server_version_is_unknown()
    {
        var cluster = Cluster(nodes: new[] { Node("n1", "amd64") },
            psaLabels: new Dictionary<string, string> { ["pod-security.kubernetes.io/enforce"] = "restricted" },
            serverVersion: null);
        Find(Checker.Check(UserNsSpec(runAsRoot: true), Settings(), cluster), "psa-enforcement").Passed.Should().BeFalse();
    }

    [Fact]
    public void An_unreadable_node_list_is_named_when_the_user_namespace_check_passes()
    {
        var cluster = Cluster(nodesAvailable: false, serverVersion: null);
        var check = Find(Checker.Check(UserNsSpec(), Settings(), cluster), "user-namespaces");

        check.Passed.Should().BeTrue();
        check.Message.Should().Contain("node list");
    }

    [Fact]
    public void Restricted_psa_still_refuses_run_as_root_without_a_user_namespace_on_1_35()
    {
        var cluster = Cluster(nodes: new[] { Node("n1", "amd64") },
            psaLabels: new Dictionary<string, string> { ["pod-security.kubernetes.io/enforce"] = "restricted" },
            serverVersion: new System.Version(1, 35));
        Find(Checker.Check(Spec(privileges: new ResolvedPrivileges { RunAsRoot = true }), Settings(), cluster), "psa-enforcement")
            .Passed.Should().BeFalse();
    }
}
