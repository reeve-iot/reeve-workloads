using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using k8s;
using k8s.Models;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Reeve.Workloads.Providers.Kubernetes.Relocate;
using Xunit;

namespace Reeve.Workloads.Providers.Kubernetes.Tests;

public sealed class KubernetesPvcInspectorBindingTests
{
    private const string PvcName = "reeve-wl-matter-ota-images";

    private sealed class FakeInspector(V1PersistentVolumeClaim pvc, V1PersistentVolume? pv)
        : KubernetesPvcInspector(NullLogger<KubernetesPvcInspector>.Instance)
    {
        internal override Task<V1PersistentVolumeClaim> ReadPvcRawAsync(IKubernetes client, string ns, string pvcName, CancellationToken ct)
            => Task.FromResult(pvc);

        internal override Task<V1PersistentVolume> ReadPvRawAsync(IKubernetes client, string pvName, CancellationToken ct)
            => Task.FromResult(pv ?? new V1PersistentVolume());
    }

    private static V1PersistentVolumeClaim BoundPvc(string? selectedNode = null) => new()
    {
        Metadata = new V1ObjectMeta
        {
            Name = PvcName,
            Annotations = selectedNode is null
                ? null
                : new Dictionary<string, string> { ["volume.kubernetes.io/selected-node"] = selectedNode },
        },
        Spec = new V1PersistentVolumeClaimSpec { VolumeName = "pv-1", StorageClassName = "csi-rbd-sc" },
        Status = new V1PersistentVolumeClaimStatus { Phase = "Bound" },
    };

    private static V1PersistentVolume Pv(params V1NodeSelectorTerm[] terms) => new()
    {
        Spec = new V1PersistentVolumeSpec
        {
            NodeAffinity = terms.Length == 0
                ? null
                : new V1VolumeNodeAffinity { Required = new V1NodeSelector { NodeSelectorTerms = terms } },
        },
    };

    private static V1NodeSelectorTerm Hostname(string op, params string[] values) => new()
    {
        MatchExpressions = [new V1NodeSelectorRequirement { Key = "kubernetes.io/hostname", OperatorProperty = op, Values = values }],
    };

    private static Task<PvcBinding?> ReadAsync(V1PersistentVolumeClaim pvc, V1PersistentVolume? pv)
        => new FakeInspector(pvc, pv).ReadPvcBindingAsync(Substitute.For<IKubernetes>(), "reeve", PvcName, CancellationToken.None);

    [Fact]
    public async Task A_pv_pinned_to_one_hostname_names_that_node()
        => (await ReadAsync(BoundPvc(), Pv(Hostname("In", "k8s-02"))))!.BoundNode.Should().Be("k8s-02");

    [Fact]
    public async Task The_selected_node_annotation_alone_is_not_a_bound_node()
        => (await ReadAsync(BoundPvc(selectedNode: "k8s-02"), Pv()))!.BoundNode.Should().BeNull();

    [Theory]
    [InlineData("NotIn", new[] { "k8s-02" })]
    [InlineData("In", new[] { "k8s-01", "k8s-02" })]
    public async Task An_affinity_that_admits_more_than_one_node_is_not_a_bound_node(string op, string[] values)
        => (await ReadAsync(BoundPvc(), Pv(Hostname(op, values))))!.BoundNode.Should().BeNull();

    private sealed class ForbiddenPvInspector(V1PersistentVolumeClaim pvc)
        : KubernetesPvcInspector(NullLogger<KubernetesPvcInspector>.Instance)
    {
        internal override Task<V1PersistentVolumeClaim> ReadPvcRawAsync(IKubernetes client, string ns, string pvcName, CancellationToken ct)
            => Task.FromResult(pvc);

        internal override Task<V1PersistentVolume> ReadPvRawAsync(IKubernetes client, string pvName, CancellationToken ct)
            => Task.FromException<V1PersistentVolume>(new InvalidOperationException("persistentvolumes is forbidden"));
    }

    [Fact]
    public async Task An_unreadable_pv_marks_the_binding_unreadable()
    {
        var binding = await new ForbiddenPvInspector(BoundPvc())
            .ReadPvcBindingAsync(Substitute.For<IKubernetes>(), "reeve", PvcName, CancellationToken.None);

        binding!.BoundNode.Should().BeNull();
        binding.BindingUnreadable.Should().BeTrue();
    }

    [Fact]
    public async Task A_readable_pv_without_affinity_is_not_unreadable()
        => (await ReadAsync(BoundPvc(), Pv()))!.BindingUnreadable.Should().BeFalse();

    [Fact]
    public async Task Two_alternative_terms_are_not_a_bound_node()
        => (await ReadAsync(BoundPvc(), Pv(Hostname("In", "k8s-01"), Hostname("In", "k8s-02"))))!.BoundNode.Should().BeNull();
}
