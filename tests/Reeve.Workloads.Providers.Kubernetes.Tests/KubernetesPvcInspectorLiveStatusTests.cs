using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using k8s;
using k8s.Autorest;
using k8s.Models;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Reeve.Workloads.Abstractions.Contracts;
using Reeve.Workloads.Providers.Kubernetes.Relocate;
using Xunit;

namespace Reeve.Workloads.Providers.Kubernetes.Tests;

public sealed class KubernetesPvcInspectorLiveStatusTests
{
    private const string Namespace = "reeve-workloads";
    private const string PvcName = "reeve-wl-acme-thread-state-11111111";

    private static HttpOperationException Forbidden() =>
        new("forbidden")
        {
            Response = new HttpResponseMessageWrapper(new HttpResponseMessage(HttpStatusCode.Forbidden), "{}"),
        };

    private static HttpOperationException NotFound() =>
        new("not found")
        {
            Response = new HttpResponseMessageWrapper(new HttpResponseMessage(HttpStatusCode.NotFound), "{}"),
        };

    private sealed class FakeInspector(Func<V1PersistentVolumeClaim> pvc)
        : KubernetesPvcInspector(NullLogger<KubernetesPvcInspector>.Instance)
    {
        internal override Task<V1PersistentVolumeClaim> ReadPvcRawAsync(IKubernetes client, string ns, string pvcName, CancellationToken ct)
            => Task.FromResult(pvc());
    }

    private static IKubernetes AnyClient() => Substitute.For<IKubernetes>();

    private static V1PersistentVolumeClaim Pvc(
        string requested,
        string? capacity,
        string phase = "Bound",
        string? storageClass = "local-path",
        IList<V1PersistentVolumeClaimCondition>? conditions = null) => new()
        {
            Metadata = new V1ObjectMeta { Name = PvcName },
            Spec = new V1PersistentVolumeClaimSpec
            {
                StorageClassName = storageClass,
                Resources = new V1VolumeResourceRequirements
                {
                    Requests = new Dictionary<string, ResourceQuantity> { ["storage"] = new(requested) },
                },
            },
            Status = new V1PersistentVolumeClaimStatus
            {
                Phase = phase,
                Capacity = capacity is null
                    ? null
                    : new Dictionary<string, ResourceQuantity> { ["storage"] = new(capacity) },
                Conditions = conditions,
            },
        };

    [Fact]
    public async Task A_stable_pvc_reports_Stable_with_capacity_equal_to_requested()
    {
        var inspector = new FakeInspector(() => Pvc(requested: "1Gi", capacity: "1Gi"));

        var status = await inspector.ReadPvcLiveStatusAsync(AnyClient(), Namespace, PvcName, CancellationToken.None);

        status.Should().NotBeNull();
        status!.ResizeState.Should().Be(WorkloadVolumeResizeState.Stable);
        status.RequestedSize.Should().Be("1Gi");
        status.ActualCapacity.Should().Be("1Gi");
        status.StorageClassName.Should().Be("local-path");
        status.Phase.Should().Be("Bound");
        status.ResizeMessage.Should().BeNull();
    }

    [Fact]
    public async Task A_pvc_mid_resize_reports_Resizing_with_both_sizes()
    {
        var inspector = new FakeInspector(() => Pvc(
            requested: "2Gi",
            capacity: "1Gi",
            conditions: new List<V1PersistentVolumeClaimCondition>
            {
                new() { Type = "Resizing", Status = "True", Message = "Controller is expanding the volume" },
            }));

        var status = await inspector.ReadPvcLiveStatusAsync(AnyClient(), Namespace, PvcName, CancellationToken.None);

        status!.ResizeState.Should().Be(WorkloadVolumeResizeState.Resizing);
        status.RequestedSize.Should().Be("2Gi");
        status.ActualCapacity.Should().Be("1Gi");
        status.ResizeMessage.Should().Be("Controller is expanding the volume");
    }

    [Fact]
    public async Task A_pvc_with_a_pending_filesystem_resize_reports_FileSystemResizePending_and_wins_over_Resizing()
    {
        var inspector = new FakeInspector(() => Pvc(
            requested: "2Gi",
            capacity: "2Gi",
            conditions: new List<V1PersistentVolumeClaimCondition>
            {
                new() { Type = "Resizing", Status = "True", Message = "resizing" },
                new() { Type = "FileSystemResizePending", Status = "True", Message = "Waiting for user to (re-)start a pod to finish file system resize of volume on node." },
            }));

        var status = await inspector.ReadPvcLiveStatusAsync(AnyClient(), Namespace, PvcName, CancellationToken.None);

        status!.ResizeState.Should().Be(WorkloadVolumeResizeState.FileSystemResizePending);
        status.ResizeMessage.Should().Contain("file system resize");
    }

    [Fact]
    public async Task A_403_on_the_read_degrades_to_Unknown_and_NEVER_a_false_stable_no_expansion()
    {
        var inspector = new FakeInspector(() => throw Forbidden());

        var status = await inspector.ReadPvcLiveStatusAsync(AnyClient(), Namespace, PvcName, CancellationToken.None);

        status.Should().NotBeNull("a forbidden read is an HONEST unknown, not an absent PVC");
        status!.ResizeState.Should().Be(WorkloadVolumeResizeState.Unknown);
        status.ResizeState.Should().NotBe(WorkloadVolumeResizeState.Stable);
        status.RequestedSize.Should().BeNull("no size may be fabricated when the read was forbidden");
        status.ActualCapacity.Should().BeNull();
        status.StorageClassName.Should().BeNull();
    }

    [Fact]
    public async Task A_404_on_the_read_reports_Missing_distinct_from_a_forbidden_Unknown()
    {
        var inspector = new FakeInspector(() => throw NotFound());

        var status = await inspector.ReadPvcLiveStatusAsync(AnyClient(), Namespace, PvcName, CancellationToken.None);

        status!.ResizeState.Should().Be(WorkloadVolumeResizeState.Missing,
            "the API server answered that the claim does not exist, which is a fact, not an unreadable status");
        status.RequestedSize.Should().BeNull();
        status.ActualCapacity.Should().BeNull();
    }

    [Fact]
    public async Task A_condition_present_but_not_True_does_not_report_a_resize()
    {
        var inspector = new FakeInspector(() => Pvc(
            requested: "1Gi",
            capacity: "1Gi",
            conditions: new List<V1PersistentVolumeClaimCondition>
            {
                new() { Type = "Resizing", Status = "False" },
            }));

        var status = await inspector.ReadPvcLiveStatusAsync(AnyClient(), Namespace, PvcName, CancellationToken.None);

        status!.ResizeState.Should().Be(WorkloadVolumeResizeState.Stable);
    }
}
