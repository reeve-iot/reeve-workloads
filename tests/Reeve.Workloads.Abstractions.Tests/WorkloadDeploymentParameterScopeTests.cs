using FluentAssertions;
using Reeve.Workloads.Abstractions.Contracts;
using Reeve.Workloads.Abstractions.Contracts.Deployment;
using Reeve.Workloads.Abstractions.Domain.DesiredSpec;
using Reeve.Workloads.Abstractions.Domain.Network;
using Xunit;
using Reeve.Workloads.Domain.DesiredSpec;
using Reeve.Workloads.Domain.Network;

namespace Reeve.Workloads.Abstractions.Tests;

public sealed class WorkloadDeploymentParameterScopeTests
{
    [Fact]
    public void A_declared_name_is_kept_and_an_undeclared_one_is_dropped()
    {
        var scoped = WorkloadDeploymentParameterScope.ForWorkload(
            Workload(parameters: ["mine"]),
            new Dictionary<string, string?>(StringComparer.Ordinal) { ["mine"] = "a", ["theirs"] = "b" });

        scoped.Should().ContainKey("mine").And.NotContainKey("theirs");
    }

    [Fact]
    public void The_user_namespace_switch_is_kept_for_every_workload()
        => WorkloadDeploymentParameterScope.ForWorkload(
                Workload(), new Dictionary<string, string?>(StringComparer.Ordinal) { ["security.userNamespace"] = "true" })
            .Should().ContainKey("security.userNamespace").WhoseValue.Should().Be("true");

    [Fact]
    public void The_restore_binding_for_a_declared_volume_is_kept()
    {
        var scoped = WorkloadDeploymentParameterScope.ForWorkload(
            Workload(volumes: ["data"]),
            new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["existingClaim.data"] = "pvc-1",
                ["existingClaim.other"] = "pvc-2",
            });

        scoped.Should().ContainKey("existingClaim.data").And.NotContainKey("existingClaim.other");
    }

    [Fact]
    public void An_ambiguous_restore_binding_is_dropped_even_though_this_workload_declares_the_volume()
    {
        var deployment = Deployment(Workload("a", volumes: ["shared"]), Workload("b", volumes: ["shared"]));
        var ambiguous = WorkloadDeploymentParameterScope.AmbiguousExistingClaimKeys(deployment);

        ambiguous.Should().Contain("existingClaim.shared");

        var scoped = WorkloadDeploymentParameterScope.ForWorkload(
            deployment.Workloads[0],
            new Dictionary<string, string?>(StringComparer.Ordinal) { ["existingClaim.shared"] = "pvc-1" },
            ambiguous);

        scoped.Should().BeEmpty();
    }

    [Fact]
    public void A_volume_id_repeated_inside_ONE_workload_is_not_ambiguous()
    {
        var deployment = Deployment(Workload("a", volumes: ["data", "data"]));

        WorkloadDeploymentParameterScope.AmbiguousExistingClaimKeys(deployment).Should().BeEmpty();
    }

    [Fact]
    public void The_network_pair_survives_for_a_workload_that_reserves_it()
    {
        var scoped = WorkloadDeploymentParameterScope.ForWorkload(
            Workload(hostNetwork: true),
            new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                [WorkloadNetworkAttachmentGrammar.NetworkModeParameter] = "operatorNetwork",
                [WorkloadNetworkAttachmentGrammar.NetworkAttachmentsParameter] = "vlan",
            });

        scoped.Should().ContainKeys(
            WorkloadNetworkAttachmentGrammar.NetworkModeParameter,
            WorkloadNetworkAttachmentGrammar.NetworkAttachmentsParameter);
    }

    [Fact]
    public void The_network_pair_is_dropped_for_a_workload_that_reserves_nothing()
    {
        var scoped = WorkloadDeploymentParameterScope.ForWorkload(
            Workload(),
            new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                [WorkloadNetworkAttachmentGrammar.NetworkModeParameter] = "operatorNetwork",
            });

        scoped.Should().BeEmpty();
    }

    private static DeploymentManifest Deployment(params WorkloadManifest[] workloads) =>
        new() { SchemaVersion = 1, Workloads = workloads };

    private static WorkloadManifest Workload(
        string id = "controller",
        string[]? parameters = null,
        string[]? volumes = null,
        bool hostNetwork = false) => new()
    {
        Id = id,
        DisplayName = id,
        Image = new WorkloadImage { Component = "controller" },
        Kind = WorkloadKind.Service,
        Health = new WorkloadHealth { Mode = WorkloadHealthMode.Container },
        Parameters = (parameters ?? [])
            .Select(name => new WorkloadParameter { Name = name, Type = WorkloadParameterType.Text })
            .ToList(),
        Volumes = (volumes ?? [])
            .Select(volumeId => new WorkloadVolume
            {
                Id = volumeId,
                MountPath = "/d",
                Size = "64Mi",
                Criticality = VolumeCriticality.Recoverable,
            })
            .ToList(),
        Network = hostNetwork ? new WorkloadNetwork { HostNetwork = true } : null,
    };
}
