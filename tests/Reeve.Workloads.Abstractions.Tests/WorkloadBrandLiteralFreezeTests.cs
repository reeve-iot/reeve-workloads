using System;
using System.Collections.Generic;
using FluentAssertions;
using Reeve.Workloads.Abstractions.Contracts;
using Reeve.Workloads.Abstractions.Contracts.Deployment;
using Reeve.Workloads.Abstractions.Domain;
using Reeve.Workloads.Abstractions.Domain.Entities;
using Reeve.Workloads.Abstractions.Providers;
using Reeve.Workloads.Domain;
using Reeve.Workloads.Tests.Shared;

namespace Reeve.Workloads.Abstractions.Tests;

public sealed class WorkloadBrandLiteralFreezeTests
{
    private static readonly Guid InstanceId = Guid.Parse("22222222-3333-4444-5555-666666666666");

    private static readonly WorkloadNamingSubject Subject = new()
    {
        GroupId = "acme.thread",
        WorkloadId = "gateway",
        Inst8 = "22222222",
    };

    [Fact]
    public void The_label_keys_live_resources_carry_are_frozen()
    {
        WorkloadResourceLabels.InstanceLabelKey.Should().Be("reeve-iot.dev/workload-instance");
        WorkloadResourceLabels.CriticalityLabelKey.Should().Be("reeve-iot.dev/criticality");
        WorkloadResourceLabels.InstallationLabelKey.Should().Be("reeve-iot.dev/installation");
        WorkloadResourceLabels.SpecHashLabelKey.Should().Be("reeve-iot.dev/spec-hash");
        WorkloadResourceLabels.ScanNodeLabelKey.Should().Be("reeve-iot.dev/scan-node");
        WorkloadResourceLabels.ScanRunLabelKey.Should().Be("reeve-iot.dev/scan-run");
    }

    [Fact]
    public void The_default_namers_object_name_still_starts_with_the_prefix_adoption_parses()
    {
        var namer = DefaultWorkloadResourceNamer.Instance;

        namer.ObjectName(Subject).Should().Be("reeve-wl-acme-thread-gateway-22222222");
        namer.SecretName(Subject).Should().Be("reeve-wl-acme-thread-gateway-22222222");
        namer.VolumeName(Subject, "data").Should().Be("reeve-wl-acme-thread-gateway-data-22222222");
    }

    [Fact]
    public void A_scan_job_name_still_starts_with_the_prefix_and_carries_the_run_token()
    {
        var name = WorkloadResourceNaming.ScanPerNodeJobName("acme.thread", "scan", "22222222", "node-a", "run-42");

        name.Should().StartWith("reeve-wl-acme-thread-scan-22222222-");
        name.Should().EndWith("-run-42");
    }

    [Fact]
    public void The_adoption_selector_is_still_the_instance_label()
    {
        DefaultWorkloadResourceNamer.Instance.InstanceSelectorLabels(InstanceId)
            .Should().Equal(new Dictionary<string, string>
            {
                ["reeve-iot.dev/workload-instance"] = "22222222-3333-4444-5555-666666666666",
            });
    }

    [Fact]
    public void The_stamped_kubernetes_label_set_still_uses_the_keys_a_backup_selector_matches_on()
    {
        var labels = DefaultWorkloadResourceNamer.Instance.Labels(new WorkloadLabelSubject
        {
            InstanceId = InstanceId,
            InstallationId = "install-1",
            SpecHash = "abc123",
            Criticality = "critical",
        });

        labels.Keys.Should().BeEquivalentTo(
            ["reeve-iot.dev/workload-instance", "reeve-iot.dev/installation", "reeve-iot.dev/spec-hash", "reeve-iot.dev/criticality"]);
    }

    [Fact]
    public void A_scan_job_still_carries_the_two_authority_labels_recovery_reads_it_back_by()
    {
        var labels = WorkloadResourceNaming.ScanPerNodeJobLabels(
            InstanceId, "install-1", "abc123", "node-a", "run-42");

        labels.Should().ContainKey("reeve-iot.dev/scan-node").WhoseValue.Should().Be("node-a");
        labels.Should().ContainKey("reeve-iot.dev/scan-run").WhoseValue.Should().Be("run-42");
    }

    [Fact]
    public void The_suspend_selector_still_names_a_key_no_node_carries()
    {
        WorkloadResourceNaming.SuspendSelectorKey("22222222").Should().Be("reeve-iot.dev/suspended-22222222");

        WorkloadResourceNaming.SuspendNodeSelector("22222222")
            .Should().Equal(new Dictionary<string, string> { ["reeve-iot.dev/suspended-22222222"] = "true" });
    }

    [Theory]
    [InlineData("reeve-wl-acme-thread-gateway-data-22222222", "kubernetes", "kubernetes")]
    [InlineData("reeve-wl-acme-thread-gateway-data-22222222", "docker", "kubernetes")]
    [InlineData("reeve-wl-acme-thread-gateway-data-22222222", "acme.nomad", "kubernetes")]
    [InlineData("reeve-acme-thread-data-22222222", "kubernetes", "docker")]
    [InlineData("operator-named-pvc", "kubernetes", "docker")]
    [InlineData("operator-named-pvc", "acme.nomad", "docker")]
    public void An_adopted_claims_scheme_is_still_inferred_from_the_object_prefix(
        string adoptedClaim, string namerScheme, string expected)
    {
        var instance = InstanceWithAdoptedClaim(adoptedClaim);

        WorkloadResourceNaming.ResolveRetainedResourceScheme(instance, "data", Namer(namerScheme))
            .Should().Be(RetainedVolumeSchemeId.Parse(expected));
        WorkloadResourceNaming.ResolveRetainedResourceName(instance, "data", Namer(namerScheme))
            .Should().Be(adoptedClaim);
    }

    private static IWorkloadResourceNamer Namer(string scheme) => new SchemeStub(RetainedVolumeSchemeId.Parse(scheme));

    private sealed class SchemeStub : IWorkloadResourceNamer
    {
        public SchemeStub(RetainedVolumeSchemeId scheme) => VolumeScheme = scheme;

        public WorkloadProviderId Provider => default;

        public RetainedVolumeSchemeId VolumeScheme { get; }

        public string ObjectName(WorkloadNamingSubject subject) => throw new NotSupportedException();

        public string SecretName(WorkloadNamingSubject subject) => throw new NotSupportedException();

        public string VolumeName(WorkloadNamingSubject subject, string volumeId) => throw new NotSupportedException();

        public IReadOnlyDictionary<string, string> Labels(WorkloadLabelSubject subject) => throw new NotSupportedException();

        public IReadOnlyDictionary<string, string> InstanceSelectorLabels(Guid instanceId) => throw new NotSupportedException();
    }

    private static WorkloadInstance InstanceWithAdoptedClaim(string adoptedClaim)
    {
        var instance = WorkloadInstance.Create(
            id: InstanceId,
            pluginInstanceId: Guid.Parse("11111111-1111-1111-1111-111111111111"),
            pluginTypeId: "acme.thread",
            workloadId: "gateway",
            createdAtUtc: DateTimeOffset.UnixEpoch);
        instance.SetProvider(StandInProviders.KubernetesId, DateTimeOffset.UnixEpoch);
        instance.SetResolvedParameters(
            new Dictionary<string, string?> { ["existingClaim.data"] = adoptedClaim },
            DateTimeOffset.UnixEpoch);
        return instance;
    }
}
