using System;
using System.Collections.Generic;
using System.Linq;
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
using Reeve.Workloads.Providers.Kubernetes;
using Xunit;

namespace Reeve.Workloads.Providers.Kubernetes.Tests;

public sealed class KubernetesObjectClientF3bTests
{
    private const string Namespace = "reeve";
    private const string DeploymentName = "reeve-wl-matter-11111111";
    private const string PodName = "reeve-wl-matter-11111111-abcde";

    private static KubernetesObjectClient NewClient() =>
        new(NullLogger<KubernetesObjectClient>.Instance);

    private static readonly IReadOnlyDictionary<string, string> Selector =
        new Dictionary<string, string> { ["reeve-iot.dev/workload-instance"] = "11111111" };

    private static HttpOperationResponse<T> Ok<T>(T body) =>
        new() { Body = body, Response = new HttpResponseMessage(HttpStatusCode.OK) };

    private static HttpOperationException Http(HttpStatusCode code, string body = "{}") =>
        new("http error")
        {
            Response = new HttpResponseMessageWrapper(new HttpResponseMessage(code), body),
        };

    private static IAppsV1Operations AppsWithOneDeployment(V1DeploymentStatus? status = null)
    {
        var deploymentList = new V1DeploymentList
        {
            Items = new List<V1Deployment>
            {
                new()
                {
                    Metadata = new V1ObjectMeta { Name = DeploymentName },
                    Spec = new V1DeploymentSpec { Replicas = 1 },
                    Status = status ?? new V1DeploymentStatus { ReadyReplicas = 1, AvailableReplicas = 1 },
                },
            },
        };

        var appsV1 = Substitute.For<IAppsV1Operations>();
        appsV1
            .ListNamespacedDeploymentWithHttpMessagesAsync(
                Namespace, Arg.Any<bool?>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<int?>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<bool?>(), Arg.Any<string>(), Arg.Any<int?>(),
                Arg.Any<bool?>(), Arg.Any<bool?>(),
                Arg.Any<IReadOnlyDictionary<string, IReadOnlyList<string>>>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Ok(deploymentList)));
        appsV1
            .ListNamespacedDaemonSetWithHttpMessagesAsync(
                Namespace, Arg.Any<bool?>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<int?>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<bool?>(), Arg.Any<string>(), Arg.Any<int?>(),
                Arg.Any<bool?>(), Arg.Any<bool?>(),
                Arg.Any<IReadOnlyDictionary<string, IReadOnlyList<string>>>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Ok(new V1DaemonSetList { Items = new List<V1DaemonSet>() })));
        return appsV1;
    }

    private static V1Pod CrashLoopingPod() => new()
    {
        Metadata = new V1ObjectMeta { Name = PodName },
        Status = new V1PodStatus
        {
            Phase = "Running",
            ContainerStatuses = new List<V1ContainerStatus>
            {
                new()
                {
                    Name = "matter",
                    RestartCount = 5,
                    Image = "matter@sha256:abc",
                    State = new V1ContainerState
                    {
                        Waiting = new V1ContainerStateWaiting { Reason = "CrashLoopBackOff" },
                    },
                    Ready = false,
                    Started = false,
                },
            },
        },
    };

    [Fact]
    public async Task ReadWorkload_includes_pod_level_events_in_recent_events()
    {
        var appsV1 = AppsWithOneDeployment();

        var podEvent = new Corev1Event
        {
            Metadata = new V1ObjectMeta { Name = "ev-pod", CreationTimestamp = DateTime.UtcNow },
            InvolvedObject = new V1ObjectReference { Kind = "Pod", Name = PodName },
            Reason = "BackOff",
            Message = "Back-off restarting failed container matter",
            Type = "Warning",
            LastTimestamp = DateTime.UtcNow,
        };
        var deploymentEvent = new Corev1Event
        {
            Metadata = new V1ObjectMeta { Name = "ev-dep", CreationTimestamp = DateTime.UtcNow.AddMinutes(-1) },
            InvolvedObject = new V1ObjectReference { Kind = "Deployment", Name = DeploymentName },
            Reason = "ScalingReplicaSet",
            Message = "Scaled up replica set",
            Type = "Normal",
            LastTimestamp = DateTime.UtcNow.AddMinutes(-1),
        };

        var coreV1 = Substitute.For<ICoreV1Operations>();
        coreV1
            .ListNamespacedPodWithHttpMessagesAsync(
                Namespace, Arg.Any<bool?>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<int?>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<bool?>(), Arg.Any<string>(), Arg.Any<int?>(),
                Arg.Any<bool?>(), Arg.Any<bool?>(),
                Arg.Any<IReadOnlyDictionary<string, IReadOnlyList<string>>>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Ok(new V1PodList { Items = new List<V1Pod> { CrashLoopingPod() } })));

        var allEvents = new List<Corev1Event> { deploymentEvent, podEvent };
        coreV1
            .ListNamespacedEventWithHttpMessagesAsync(
                Namespace, Arg.Any<bool?>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<int?>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<bool?>(), Arg.Any<string>(), Arg.Any<int?>(),
                Arg.Any<bool?>(), Arg.Any<bool?>(),
                Arg.Any<IReadOnlyDictionary<string, IReadOnlyList<string>>>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                var fieldSelector = ci.ArgAt<string>(3);
                var matched = allEvents.Where(e =>
                    string.IsNullOrEmpty(fieldSelector)
                    || fieldSelector.Contains($"involvedObject.name={e.InvolvedObject?.Name}", StringComparison.Ordinal))
                    .ToList();
                return Task.FromResult(Ok(new Corev1EventList { Items = matched }));
            });

        var client = Substitute.For<IKubernetes>();
        client.AppsV1.Returns(appsV1);
        client.CoreV1.Returns(coreV1);

        var raw = await NewClient().ReadWorkloadAsync(client, Namespace, Selector, CancellationToken.None);

        raw.Should().NotBeNull();
        raw!.Events.Should().Contain(e => e.Reason == "BackOff",
            "a pod-level BackOff/CrashLoopBackOff event (involvedObject = the pod) must be surfaced in recent events");
        raw.Events.Should().Contain(e => e.Reason == "ScalingReplicaSet",
            "the workload object's own events are still included alongside the pod events");
    }

    [Fact]
    public async Task ReadWorkload_still_returns_status_when_events_read_blips_and_pods_read()
    {
        var appsV1 = AppsWithOneDeployment();

        var coreV1 = Substitute.For<ICoreV1Operations>();
        coreV1
            .ListNamespacedPodWithHttpMessagesAsync(
                Namespace, Arg.Any<bool?>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<int?>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<bool?>(), Arg.Any<string>(), Arg.Any<int?>(),
                Arg.Any<bool?>(), Arg.Any<bool?>(),
                Arg.Any<IReadOnlyDictionary<string, IReadOnlyList<string>>>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Ok(new V1PodList { Items = new List<V1Pod>() })));
        coreV1
            .ListNamespacedEventWithHttpMessagesAsync(
                Namespace, Arg.Any<bool?>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<int?>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<bool?>(), Arg.Any<string>(), Arg.Any<int?>(),
                Arg.Any<bool?>(), Arg.Any<bool?>(),
                Arg.Any<IReadOnlyDictionary<string, IReadOnlyList<string>>>(), Arg.Any<CancellationToken>())
            .Returns<Task<HttpOperationResponse<Corev1EventList>>>(_ => throw new HttpRequestException("events blip"));

        var client = Substitute.For<IKubernetes>();
        client.AppsV1.Returns(appsV1);
        client.CoreV1.Returns(coreV1);

        var raw = await NewClient().ReadWorkloadAsync(client, Namespace, Selector, CancellationToken.None);

        raw.Should().NotBeNull();
        raw!.Found.Should().BeTrue();
        raw.Events.Should().BeEmpty("the events read blipped — events are best-effort");
    }

    private static V1PersistentVolumeClaim Pvc(string storage, string? storageClassName = "standard") => new()
    {
        Metadata = new V1ObjectMeta { Name = "reeve-wl-matter-data-11111111" },
        Spec = new V1PersistentVolumeClaimSpec
        {
            StorageClassName = storageClassName,
            Resources = new V1VolumeResourceRequirements
            {
                Requests = new Dictionary<string, ResourceQuantity> { ["storage"] = new(storage) },
            },
        },
    };

    private sealed class PvcRecorder
    {
        public int ResizePatchCount { get; set; }
        public V1Patch? ResizePatch { get; set; }
        public bool StorageClassRead { get; set; }
    }

    private static (IKubernetes Client, PvcRecorder Recorder) ClientWithForbiddenStorageClass(
        Func<HttpOperationResponse<V1PersistentVolumeClaim>>? onResize = null)
    {
        var recorder = new PvcRecorder();

        var coreV1 = Substitute.For<ICoreV1Operations>();
        coreV1
            .ReadNamespacedPersistentVolumeClaimWithHttpMessagesAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<bool?>(),
                Arg.Any<IReadOnlyDictionary<string, IReadOnlyList<string>>>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Ok(Pvc("1Gi"))));
        coreV1
            .PatchNamespacedPersistentVolumeClaimWithHttpMessagesAsync(
                Arg.Any<V1Patch>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<bool?>(), Arg.Any<bool?>(),
                Arg.Any<IReadOnlyDictionary<string, IReadOnlyList<string>>>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                recorder.ResizePatchCount++;
                recorder.ResizePatch = ci.ArgAt<V1Patch>(0);
                return Task.FromResult(onResize is null ? Ok(Pvc("2Gi")) : onResize());
            });

        var storageV1 = Substitute.For<IStorageV1Operations>();
        storageV1
            .ReadStorageClassWithHttpMessagesAsync(
                Arg.Any<string>(), Arg.Any<bool?>(),
                Arg.Any<IReadOnlyDictionary<string, IReadOnlyList<string>>>(), Arg.Any<CancellationToken>())
            .Returns<Task<HttpOperationResponse<V1StorageClass>>>(_ =>
            {
                recorder.StorageClassRead = true;
                throw Http(HttpStatusCode.Forbidden, "storageclasses is forbidden");
            });

        var client = Substitute.For<IKubernetes>();
        client.CoreV1.Returns(coreV1);
        client.StorageV1.Returns(storageV1);
        return (client, recorder);
    }

    [Fact]
    public async Task EnsurePvc_attempts_upward_resize_even_when_storageclass_read_is_forbidden()
    {
        var (client, recorder) = ClientWithForbiddenStorageClass();

        await NewClient().EnsurePvcAsync(client, Namespace, Pvc("2Gi"), isAdoptedClaim: false, CancellationToken.None);

        recorder.ResizePatchCount.Should().Be(1,
            "an upward resize must be ATTEMPTED under default RBAC — a forbidden StorageClass read must not skip it");
        recorder.ResizePatch.Should().NotBeNull();
        var body = (string)recorder.ResizePatch!.Content;
        body.Should().Contain("storage", "the merge-patch sets spec.resources.requests.storage");
        body.Should().Contain("2Gi");
    }

    [Fact]
    public async Task EnsurePvc_handles_a_non_expandable_class_rejection_gracefully()
    {
        var (client, _) = ClientWithForbiddenStorageClass(
            onResize: () => throw Http(HttpStatusCode.UnprocessableEntity,
                "persistentvolumeclaims \"x\" is forbidden: only dynamically provisioned pvc … allow expansion"));

        var act = async () => await NewClient().EnsurePvcAsync(client, Namespace, Pvc("2Gi"), isAdoptedClaim: false, CancellationToken.None);

        await act.Should().NotThrowAsync(
            "an API rejection of a non-expandable-class resize must be handled gracefully, not thrown");
    }

    [Fact]
    public async Task EnsurePvc_does_not_resize_when_desired_is_not_larger()
    {
        var (client, recorder) = ClientWithForbiddenStorageClass();

        await NewClient().EnsurePvcAsync(client, Namespace, Pvc("1Gi"), isAdoptedClaim: false, CancellationToken.None);

        recorder.ResizePatchCount.Should().Be(0, "an equal (or smaller) desired size must never patch the PVC");
    }

    [Fact]
    public async Task EnsurePvc_does_not_shrink_a_larger_existing_pvc()
    {
        var (client, recorder) = ClientWithForbiddenStorageClass();

        await NewClient().EnsurePvcAsync(client, Namespace, Pvc("512Mi"), isAdoptedClaim: false, CancellationToken.None);

        recorder.ResizePatchCount.Should().Be(0, "a downward resize must never be attempted (immutable/shrink-unsafe)");
    }

    private static (IKubernetes Client, List<string> PatchBodies) ClientWithExistingLabelledPvc(
        IDictionary<string, string>? existingLabels)
    {
        var patchBodies = new List<string>();
        var existing = new V1PersistentVolumeClaim
        {
            Metadata = new V1ObjectMeta { Name = "reeve-wl-matter-data-11111111", Labels = existingLabels },
            Spec = new V1PersistentVolumeClaimSpec
            {
                Resources = new V1VolumeResourceRequirements
                {
                    Requests = new Dictionary<string, ResourceQuantity> { ["storage"] = new("1Gi") },
                },
            },
        };

        var coreV1 = Substitute.For<ICoreV1Operations>();
        coreV1
            .ReadNamespacedPersistentVolumeClaimWithHttpMessagesAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<bool?>(),
                Arg.Any<IReadOnlyDictionary<string, IReadOnlyList<string>>>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Ok(existing)));
        coreV1
            .PatchNamespacedPersistentVolumeClaimWithHttpMessagesAsync(
                Arg.Any<V1Patch>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<bool?>(), Arg.Any<bool?>(),
                Arg.Any<IReadOnlyDictionary<string, IReadOnlyList<string>>>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                patchBodies.Add((string)ci.ArgAt<V1Patch>(0).Content);
                return Task.FromResult(Ok(existing));
            });

        var client = Substitute.For<IKubernetes>();
        client.CoreV1.Returns(coreV1);
        return (client, patchBodies);
    }

    private static V1PersistentVolumeClaim DesiredLabelledPvc() => new()
    {
        Metadata = new V1ObjectMeta
        {
            Name = "reeve-wl-matter-data-11111111",
            Labels = new Dictionary<string, string>
            {
                ["reeve-iot.dev/workload-instance"] = "22222222-2222-2222-2222-222222222222",
            },
        },
        Spec = new V1PersistentVolumeClaimSpec
        {
            Resources = new V1VolumeResourceRequirements
            {
                Requests = new Dictionary<string, ResourceQuantity> { ["storage"] = new("1Gi") },
            },
        },
    };

    [Fact]
    public async Task EnsurePvc_relabels_an_adopted_pvc_that_carries_a_different_instance_label()
    {
        var (client, patches) = ClientWithExistingLabelledPvc(new Dictionary<string, string>
        {
            ["reeve-iot.dev/workload-instance"] = "11111111-1111-1111-1111-111111111111",
        });

        await NewClient().EnsurePvcAsync(client, Namespace, DesiredLabelledPvc(), isAdoptedClaim: true, CancellationToken.None);

        patches.Should().ContainSingle("the adopted PVC is relabelled once (no resize needed — same size)");
        patches[0].Should().Contain("labels", "the adoption patch sets the metadata labels");
        patches[0].Should().Contain("22222222-2222-2222-2222-222222222222",
            "the adopted PVC is relabelled to the CURRENT instance so label-based teardown reaches it");
    }

    [Fact]
    public async Task EnsurePvc_does_not_relabel_when_the_instance_label_already_matches()
    {
        var (client, patches) = ClientWithExistingLabelledPvc(new Dictionary<string, string>
        {
            ["reeve-iot.dev/workload-instance"] = "22222222-2222-2222-2222-222222222222",
        });

        await NewClient().EnsurePvcAsync(client, Namespace, DesiredLabelledPvc(), isAdoptedClaim: true, CancellationToken.None);

        patches.Should().BeEmpty("an already-correctly-labelled PVC needs no relabel patch (idempotent)");
    }

    [Fact]
    public async Task EnsurePvc_THROWS_when_the_adoption_relabel_fails_so_the_apply_retries()
    {
        var existing = new V1PersistentVolumeClaim
        {
            Metadata = new V1ObjectMeta
            {
                Name = "reeve-wl-matter-data-11111111",
                Labels = new Dictionary<string, string> { ["reeve-iot.dev/workload-instance"] = "11111111-1111-1111-1111-111111111111" },
            },
            Spec = new V1PersistentVolumeClaimSpec
            {
                Resources = new V1VolumeResourceRequirements
                {
                    Requests = new Dictionary<string, ResourceQuantity> { ["storage"] = new("1Gi") },
                },
            },
        };

        var coreV1 = Substitute.For<ICoreV1Operations>();
        coreV1
            .ReadNamespacedPersistentVolumeClaimWithHttpMessagesAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<bool?>(),
                Arg.Any<IReadOnlyDictionary<string, IReadOnlyList<string>>>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Ok(existing)));
        coreV1
            .PatchNamespacedPersistentVolumeClaimWithHttpMessagesAsync(
                Arg.Any<V1Patch>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<bool?>(), Arg.Any<bool?>(),
                Arg.Any<IReadOnlyDictionary<string, IReadOnlyList<string>>>(), Arg.Any<CancellationToken>())
            .Returns<Task<HttpOperationResponse<V1PersistentVolumeClaim>>>(_ =>
                throw Http(HttpStatusCode.InternalServerError, "relabel patch failed (test)"));

        var client = Substitute.For<IKubernetes>();
        client.CoreV1.Returns(coreV1);

        var act = async () => await NewClient().EnsurePvcAsync(client, Namespace, DesiredLabelledPvc(), isAdoptedClaim: true, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>(
            "a failed adoption relabel must THROW (fail-the-apply → retry), never be swallowed silently");
    }

    private static V1PersistentVolumeClaim AdoptedPvc(string name = "reeve-wl-matter-data-11111111") => new()
    {
        Metadata = new V1ObjectMeta
        {
            Name = name,
            Labels = new Dictionary<string, string>
            {
                ["reeve-iot.dev/workload-instance"] = "22222222-2222-2222-2222-222222222222",
            },
        },
        Spec = new V1PersistentVolumeClaimSpec
        {
            Resources = new V1VolumeResourceRequirements
            {
                Requests = new Dictionary<string, ResourceQuantity> { ["storage"] = new("1Gi") },
            },
        },
    };

    private static (IKubernetes Client, PvcAbsentRecorder Recorder) ClientWithAbsentPvc()
    {
        var recorder = new PvcAbsentRecorder();
        var coreV1 = Substitute.For<ICoreV1Operations>();
        coreV1
            .ReadNamespacedPersistentVolumeClaimWithHttpMessagesAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<bool?>(),
                Arg.Any<IReadOnlyDictionary<string, IReadOnlyList<string>>>(), Arg.Any<CancellationToken>())
            .Returns<Task<HttpOperationResponse<V1PersistentVolumeClaim>>>(_ => throw Http(HttpStatusCode.NotFound, "pvc not found"));
        coreV1
            .CreateNamespacedPersistentVolumeClaimWithHttpMessagesAsync(
                Arg.Any<V1PersistentVolumeClaim>(), Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<bool?>(),
                Arg.Any<IReadOnlyDictionary<string, IReadOnlyList<string>>>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                recorder.CreateCount++;
                return Task.FromResult(Ok(ci.ArgAt<V1PersistentVolumeClaim>(0)));
            });

        var client = Substitute.For<IKubernetes>();
        client.CoreV1.Returns(coreV1);
        return (client, recorder);
    }

    private sealed class PvcAbsentRecorder
    {
        public int CreateCount { get; set; }
    }

    [Fact]
    public async Task EnsurePvc_FAILS_when_an_adopted_claim_PVC_is_absent_and_never_creates_a_fresh_empty_one()
    {
        var (client, recorder) = ClientWithAbsentPvc();

        var act = async () => await NewClient().EnsurePvcAsync(client, Namespace, AdoptedPvc(), isAdoptedClaim: true, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>(
            "an adopted (restore) claim whose retained PVC is absent must FAIL the apply, never create a fresh empty PVC (fail-closed)");
        recorder.CreateCount.Should().Be(0,
            "the central invariant must NEVER create-if-absent for a restore — a fresh empty PVC would silently lose the data");
    }

    [Fact]
    public async Task EnsurePvc_CREATES_a_fresh_non_adopted_PVC_when_absent()
    {
        var (client, recorder) = ClientWithAbsentPvc();

        await NewClient().EnsurePvcAsync(client, Namespace, AdoptedPvc(), isAdoptedClaim: false, CancellationToken.None);

        recorder.CreateCount.Should().Be(1,
            "a fresh (non-adopted) volume is still created when absent — the must-exist invariant is restore-only");
    }

    [Fact]
    public async Task EnsurePvc_REUSES_an_adopted_claim_PVC_that_already_exists()
    {
        var (client, patches) = ClientWithExistingLabelledPvc(new Dictionary<string, string>
        {
            ["reeve-iot.dev/workload-instance"] = "11111111-1111-1111-1111-111111111111",
        });

        var act = async () => await NewClient().EnsurePvcAsync(client, Namespace, AdoptedPvc(), isAdoptedClaim: true, CancellationToken.None);

        await act.Should().NotThrowAsync("a genuine restore whose retained PVC EXISTS must succeed (reuse + relabel)");
        patches.Should().ContainSingle("the existing adopted PVC is relabelled to the current instance, not recreated");
    }

    [Fact]
    public async Task EnsurePvc_does_not_count_a_terminating_PVC_as_present_and_creates_it_once_it_is_gone()
    {
        var terminating = Pvc("1Gi");
        terminating.Metadata.DeletionTimestamp = DateTime.UtcNow;
        var gone = false;
        var creates = 0;
        var patches = 0;

        var coreV1 = Substitute.For<ICoreV1Operations>();
        coreV1
            .ReadNamespacedPersistentVolumeClaimWithHttpMessagesAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<bool?>(),
                Arg.Any<IReadOnlyDictionary<string, IReadOnlyList<string>>>(), Arg.Any<CancellationToken>())
            .Returns<Task<HttpOperationResponse<V1PersistentVolumeClaim>>>(_ =>
                gone ? throw Http(HttpStatusCode.NotFound, "pvc not found") : Task.FromResult(Ok(terminating)));
        coreV1
            .CreateNamespacedPersistentVolumeClaimWithHttpMessagesAsync(
                Arg.Any<V1PersistentVolumeClaim>(), Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<bool?>(),
                Arg.Any<IReadOnlyDictionary<string, IReadOnlyList<string>>>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                creates++;
                return Task.FromResult(Ok(ci.ArgAt<V1PersistentVolumeClaim>(0)));
            });
        coreV1
            .PatchNamespacedPersistentVolumeClaimWithHttpMessagesAsync(
                Arg.Any<V1Patch>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<bool?>(), Arg.Any<bool?>(),
                Arg.Any<IReadOnlyDictionary<string, IReadOnlyList<string>>>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                patches++;
                return Task.FromResult(Ok(terminating));
            });
        var client = Substitute.For<IKubernetes>();
        client.CoreV1.Returns(coreV1);

        var whileTerminating = async () => await NewClient().EnsurePvcAsync(client, Namespace, DesiredLabelledPvc(), isAdoptedClaim: false, CancellationToken.None);

        (await whileTerminating.Should().ThrowAsync<KubernetesPvcTerminatingException>(
            "a claim being deleted is not present — the apply must not settle on it"))
            .Which.ClaimName.Should().Be("reeve-wl-matter-data-11111111");
        creates.Should().Be(0, "the name is still taken while the claim terminates");
        patches.Should().Be(0, "a dying claim is neither relabelled nor resized");

        gone = true;
        await NewClient().EnsurePvcAsync(client, Namespace, DesiredLabelledPvc(), isAdoptedClaim: false, CancellationToken.None);

        creates.Should().Be(1, "once the terminating claim is gone, the next apply creates it again");
    }
}
