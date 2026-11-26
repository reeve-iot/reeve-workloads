using System;
using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using Reeve.Workloads.Abstractions.Contracts;
using Reeve.Workloads.Abstractions.Contracts.Deployment;
using Reeve.Workloads.Abstractions.Domain.DesiredSpec;
using Reeve.Workloads.Abstractions.Providers;
using Reeve.Workloads.Providers.Docker;
using Xunit;

namespace Reeve.Workloads.Providers.Docker.Tests;

public sealed class DockerStaticValidationTests
{
    private static readonly IDockerDeploymentValidator Validator = new DockerDeploymentValidator();

    private const string PerNodeId = "docker-rejects-per-node";
    private const string ScanPerNodeId = "docker-rejects-scan-per-node";
    private const string PodPatchId = "docker-rejects-unsupported-pod-patch";
    private const string ConfigMapId = "docker-rejects-operator-configmap";
    private const string HttpHealthId = "docker-rejects-http-health";

    [Fact]
    public void Rejects_a_per_node_workload_with_the_named_error()
    {
        var checks = Validator.Validate(Spec(kind: WorkloadKind.PerNode));

        checks.Should().ContainSingle(c => c.Id == PerNodeId)
            .Which.Should().Match<PreflightCheck>(c => !c.Passed && c.Severity == PreflightSeverity.Error);
    }

    [Fact]
    public void Rejects_a_scan_per_node_workload_with_the_named_error()
    {
        var checks = Validator.Validate(Spec(kind: WorkloadKind.ScanPerNode));

        checks.Should().ContainSingle(c => c.Id == ScanPerNodeId)
            .Which.Should().Match<PreflightCheck>(c => !c.Passed && c.Severity == PreflightSeverity.Error);
    }

    [Fact]
    public void Rejects_an_unsupported_pod_patch_with_the_named_error()
    {
        var patch = new UnsupportedPodPatchSpec { PatchJson = "[{\"op\":\"add\",\"path\":\"/spec\",\"value\":{}}]" };

        var checks = Validator.Validate(Spec(unsupportedPodPatch: patch));

        checks.Should().ContainSingle(c => c.Id == PodPatchId)
            .Which.Should().Match<PreflightCheck>(c => !c.Passed && c.Severity == PreflightSeverity.Error);
    }

    [Fact]
    public void Rejects_an_operator_set_configmap_mount_with_the_named_error()
    {
        var mount = new ResolvedConfigMapMount { MountPath = "/etc/reeve/config", ConfigMapName = "operator-map" };

        var checks = Validator.Validate(Spec(configMapMounts: new[] { mount }));

        checks.Should().ContainSingle(c => c.Id == ConfigMapId)
            .Which.Should().Match<PreflightCheck>(c => !c.Passed && c.Severity == PreflightSeverity.Error);
    }

    [Fact]
    public void Rejects_an_http_health_workload_with_the_named_error()
    {
        var health = new WorkloadHealth
        {
            Mode = WorkloadHealthMode.Http,
            Http = new WorkloadHttpProbe { Port = 8080, LivenessPath = "/healthz" },
        };

        var checks = Validator.Validate(Spec(health: health));

        checks.Should().ContainSingle(c => c.Id == HttpHealthId)
            .Which.Should().Match<PreflightCheck>(c => !c.Passed && c.Severity == PreflightSeverity.Error);

        DockerDeploymentValidator.HttpHealthRejectionId.Should().Be(HttpHealthId);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void An_unset_configmap_mount_is_filtered_and_produces_no_error(string configMapName)
    {
        var mount = new ResolvedConfigMapMount { MountPath = "/etc/reeve/config", ConfigMapName = configMapName };

        var checks = Validator.Validate(Spec(configMapMounts: new[] { mount }));

        checks.Should().NotContain(c => c.Id == ConfigMapId);
        BlockingErrors(checks).Should().BeEmpty();
    }

    [Fact]
    public void A_clean_docker_spec_yields_zero_error_checks()
    {
        var checks = Validator.Validate(Spec());

        BlockingErrors(checks).Should().BeEmpty();
    }

    [Theory]
    [InlineData("adapter-session")]
    [InlineData("container")]
    public void Adapter_session_and_container_health_pass_without_a_health_rejection(string modeToken)
    {
        var mode = new WorkloadHealthMode(modeToken);
        var health = new WorkloadHealth
        {
            Mode = mode,
            HeartbeatSeconds = mode.IsLibraryOwned ? 0 : 30,
        };

        var checks = Validator.Validate(Spec(health: health));

        checks.Should().NotContain(c => c.Id == HttpHealthId);
        BlockingErrors(checks).Should().BeEmpty();
    }

    [Fact]
    public void A_spec_that_trips_every_rule_reports_all_four_named_errors_distinctly()
    {
        var spec = Spec(
            kind: WorkloadKind.PerNode,
            unsupportedPodPatch: new UnsupportedPodPatchSpec { PatchJson = "[]" },
            configMapMounts: new[] { new ResolvedConfigMapMount { MountPath = "/etc/x", ConfigMapName = "cm" } },
            health: new WorkloadHealth { Mode = WorkloadHealthMode.Http, Http = new WorkloadHttpProbe { Port = 80 } });

        var checks = Validator.Validate(spec);

        BlockingErrors(checks).Select(c => c.Id).Should()
            .BeEquivalentTo(new[] { PerNodeId, PodPatchId, ConfigMapId, HttpHealthId });
        checks.Should().OnlyContain(c => !c.Passed && c.Severity == PreflightSeverity.Error);
    }

    private static IReadOnlyList<PreflightCheck> BlockingErrors(IReadOnlyList<PreflightCheck> checks) =>
        checks.Where(c => !c.Passed && c.Severity == PreflightSeverity.Error).ToList();

    private static WorkloadDesiredSpec Spec(
        WorkloadKind kind = WorkloadKind.Service,
        UnsupportedPodPatchSpec? unsupportedPodPatch = null,
        IReadOnlyList<ResolvedConfigMapMount>? configMapMounts = null,
        WorkloadHealth? health = null,
        ResolvedNetwork? network = null) =>
        new()
        {
            GroupInstanceId = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee"),
            GroupId = "com.reeve.matter",
            WorkloadId = "matter-server",
            InstanceId = Guid.Parse("11111111-2222-3333-4444-555555555555"),
            Inst8 = "11111111",
            Provider = DockerProvider.Id,
            ResourceName = "reeve-matter-11111111",
            SecretName = "reeve-matter-11111111-secret",
            Labels = new Dictionary<string, string>(),
            Image = "ghcr.io/reeve/matter@sha256:0000000000000000000000000000000000000000000000000000000000000000",
            Kind = kind,
            ConfigMapMounts = configMapMounts ?? Array.Empty<ResolvedConfigMapMount>(),
            Health = health ?? new WorkloadHealth { Mode = WorkloadHealthMode.Container },
            Network = network,
            UnsupportedPodPatch = unsupportedPodPatch,
            SpecHash = "deadbeef",
        };
}
