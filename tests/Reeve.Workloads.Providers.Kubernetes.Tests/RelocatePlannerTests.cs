using FluentAssertions;
using Reeve.Workloads.Abstractions.Contracts.Deployment;
using Reeve.Workloads.Providers.Kubernetes.Relocate;
using Xunit;

namespace Reeve.Workloads.Providers.Kubernetes.Tests;

public sealed class RelocatePlannerTests
{
    private static readonly Guid Instance = Guid.NewGuid();

    private static RelocateVolumeFact Vol(
        string? boundNode,
        IReadOnlyDictionary<string, string>? boundNodeLabels = null,
        VolumeCriticality criticality = VolumeCriticality.Critical,
        string id = "state",
        bool adoptedClaim = false,
        bool bindingUnknown = false)
        => new()
        {
            VolumeId = id,
            Criticality = criticality,
            Description = "desc-" + id,
            PvcName = "pvc-" + id,
            BoundNode = boundNode,
            BoundNodeLabels = boundNodeLabels,
            IsAdoptedClaim = adoptedClaim,
            BindingUnknown = bindingUnknown,
        };

    private static Dictionary<string, string> HostSelector(string node)
        => new() { ["kubernetes.io/hostname"] = node };

    private static Dictionary<string, string> HostLabels(string node)
        => new() { ["kubernetes.io/hostname"] = node };

    [Fact]
    public void A_volume_bound_to_a_node_the_selector_moves_off_is_reported_with_its_node_and_description()
    {
        var plan = KubernetesRelocatePlanner.Plan(
            Instance, HostSelector("node-b"), [Vol("node-a", HostLabels("node-a"), VolumeCriticality.Recoverable)]);

        var volume = plan.NodeBoundVolumes.Should().ContainSingle().Subject;
        volume.VolumeId.Should().Be("state");
        volume.PvcName.Should().Be("pvc-state");
        volume.BoundNode.Should().Be("node-a");
        volume.Description.Should().Be("desc-state");
        volume.Criticality.Should().Be(VolumeCriticality.Recoverable);
        plan.HasStrandedAdoptedClaim.Should().BeFalse();
    }

    [Fact]
    public void A_volume_staying_on_its_bound_node_is_not_reported()
    {
        var plan = KubernetesRelocatePlanner.Plan(
            Instance, HostSelector("node-a"), [Vol("node-a", HostLabels("node-a"))]);

        plan.NodeBoundVolumes.Should().BeEmpty();
    }

    [Fact]
    public void A_volume_with_an_unknown_bound_node_is_assumed_to_follow_the_pod()
    {
        var plan = KubernetesRelocatePlanner.Plan(
            Instance, HostSelector("node-b"), [Vol(boundNode: null, criticality: VolumeCriticality.Cache)]);

        plan.NodeBoundVolumes.Should().BeEmpty();
        plan.UnverifiedVolumes.Should().BeEmpty();
    }

    [Fact]
    public void A_volume_whose_bound_node_labels_are_unknown_is_reported_as_unverified()
    {
        var plan = KubernetesRelocatePlanner.Plan(
            Instance, HostSelector("node-b"), [Vol("node-a", boundNodeLabels: null)]);

        plan.NodeBoundVolumes.Should().BeEmpty();
        plan.UnverifiedVolumes.Should().ContainSingle().Which.PvcName.Should().Be("pvc-state");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_volume_whose_binding_could_not_be_read_is_unverified_and_never_blocks(bool adoptedClaim)
    {
        var plan = KubernetesRelocatePlanner.Plan(
            Instance, HostSelector("node-b"),
            [Vol(boundNode: null, criticality: VolumeCriticality.Recoverable, adoptedClaim: adoptedClaim, bindingUnknown: true)]);

        plan.NodeBoundVolumes.Should().BeEmpty();
        plan.HasStrandedAdoptedClaim.Should().BeFalse();
        var volume = plan.UnverifiedVolumes.Should().ContainSingle().Subject;
        volume.VolumeId.Should().Be("state");
        volume.Description.Should().Be("desc-state");
        volume.Criticality.Should().Be(VolumeCriticality.Recoverable);
    }

    [Fact]
    public void A_non_hostname_selector_the_bound_node_does_not_satisfy_reports_the_volume()
    {
        var selector = new Dictionary<string, string> { ["role"] = "new" };
        var labels = new Dictionary<string, string> { ["kubernetes.io/hostname"] = "node-a", ["role"] = "old" };

        var plan = KubernetesRelocatePlanner.Plan(Instance, selector, [Vol("node-a", labels)]);

        plan.NodeBoundVolumes.Should().ContainSingle();
    }

    [Fact]
    public void A_non_hostname_selector_the_bound_node_still_satisfies_does_not_report_the_volume()
    {
        var selector = new Dictionary<string, string> { ["role"] = "edge" };
        var labels = new Dictionary<string, string> { ["kubernetes.io/hostname"] = "node-a", ["role"] = "edge" };

        var plan = KubernetesRelocatePlanner.Plan(Instance, selector, [Vol("node-a", labels)]);

        plan.NodeBoundVolumes.Should().BeEmpty();
    }

    [Fact]
    public void A_multi_key_selector_where_the_bound_node_misses_one_label_reports_the_volume()
    {
        var selector = new Dictionary<string, string> { ["role"] = "edge", ["zone"] = "z2" };
        var labels = new Dictionary<string, string> { ["role"] = "edge", ["zone"] = "z1" };

        var plan = KubernetesRelocatePlanner.Plan(Instance, selector, [Vol("node-a", labels)]);

        plan.NodeBoundVolumes.Should().ContainSingle();
    }

    [Fact]
    public void An_empty_selector_admits_the_bound_node()
    {
        var plan = KubernetesRelocatePlanner.Plan(
            Instance, new Dictionary<string, string>(), [Vol("node-a", HostLabels("node-a"))]);

        plan.NodeBoundVolumes.Should().BeEmpty();
    }

    [Fact]
    public void An_adopted_claim_bound_off_the_new_selector_blocks_the_relocate_and_is_not_a_warning()
    {
        var plan = KubernetesRelocatePlanner.Plan(
            Instance, HostSelector("node-b"), [Vol("node-a", HostLabels("node-a"), adoptedClaim: true)]);

        plan.HasStrandedAdoptedClaim.Should().BeTrue();
        plan.AdoptedVolumesBlockingRelocate.Should().ContainSingle().Which.BoundNode.Should().Be("node-a");
        plan.NodeBoundVolumes.Should().BeEmpty();
    }

    [Fact]
    public void An_adopted_claim_with_an_unknown_bound_node_does_not_block()
    {
        var plan = KubernetesRelocatePlanner.Plan(
            Instance, HostSelector("node-b"), [Vol(boundNode: null, adoptedClaim: true)]);

        plan.HasStrandedAdoptedClaim.Should().BeFalse();
    }

    [Fact]
    public void An_adopted_claim_staying_on_its_node_does_not_block()
    {
        var plan = KubernetesRelocatePlanner.Plan(
            Instance, HostSelector("node-a"), [Vol("node-a", HostLabels("node-a"), adoptedClaim: true)]);

        plan.HasStrandedAdoptedClaim.Should().BeFalse();
    }

    [Fact]
    public void Mixed_volumes_report_only_the_ones_left_behind()
    {
        var plan = KubernetesRelocatePlanner.Plan(
            Instance,
            HostSelector("node-b"),
            [
                Vol("node-a", HostLabels("node-a"), id: "local"),
                Vol(boundNode: null, id: "network"),
                Vol("node-b", HostLabels("node-b"), id: "already-there"),
            ]);

        plan.NodeBoundVolumes.Select(v => v.VolumeId).Should().Equal("local");
    }
}
