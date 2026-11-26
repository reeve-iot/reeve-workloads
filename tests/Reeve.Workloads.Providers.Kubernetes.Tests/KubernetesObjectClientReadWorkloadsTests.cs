using System;
using System.Collections.Generic;
using System.Linq;
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

public sealed class KubernetesObjectClientReadWorkloadsTests
{
    private const string Namespace = "reeve";
    private const string InstanceLabel = "reeve-iot.dev/workload-instance";

    private static readonly Guid Deployed = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid DaemonSet = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid ScanOnly = Guid.Parse("33333333-3333-3333-3333-333333333333");
    private static readonly Guid TornDown = Guid.Parse("44444444-4444-4444-4444-444444444444");
    private static readonly Guid Stranger = Guid.Parse("55555555-5555-5555-5555-555555555555");

    [Fact]
    public async Task The_whole_fleet_is_read_in_five_calls()
    {
        var (client, calls) = Cluster();

        await NewClient().ReadWorkloadsAsync(client, Namespace, [Deployed, DaemonSet, ScanOnly, TornDown], CancellationToken.None);

        calls.Deployments.Should().Be(1);
        calls.DaemonSets.Should().Be(1);
        calls.Jobs.Should().Be(1);
        calls.Pods.Should().Be(1);
        calls.Events.Should().Be(1,
            "the per-instance read field-scopes one event call per involved object; across a fleet that is the call count this finding is about");
    }

    [Fact]
    public async Task Each_instance_gets_its_own_objects()
    {
        var (client, _) = Cluster();

        var raws = await NewClient().ReadWorkloadsAsync(client, Namespace, [Deployed, DaemonSet], CancellationToken.None);

        raws[Deployed].Found.Should().BeTrue();
        raws[Deployed].Kind.Should().Be("Deployment");
        raws[Deployed].DesiredReplicas.Should().Be(1);
        raws[Deployed].ReadyReplicas.Should().Be(1);
        raws[Deployed].HasPods.Should().BeTrue();

        raws[DaemonSet].Found.Should().BeTrue();
        raws[DaemonSet].Kind.Should().Be("DaemonSet");
        raws[DaemonSet].DesiredReplicas.Should().Be(3);
        raws[DaemonSet].ReadyReplicas.Should().Be(2);
    }

    [Fact]
    public async Task Each_instance_carries_its_own_pods_block_reason()
    {
        var (client, _) = Cluster();

        var raws = await NewClient().ReadWorkloadsAsync(client, Namespace, [Deployed, DaemonSet], CancellationToken.None);

        raws[DaemonSet].PodBlockReason.Should().Be("ContainersNotReady: containers with unready status: [scan]");
        raws[Deployed].PodBlockReason.Should().BeNull();
    }

    [Fact]
    public async Task Events_are_attributed_to_the_instance_they_involve()
    {
        var (client, _) = Cluster();

        var raws = await NewClient().ReadWorkloadsAsync(client, Namespace, [Deployed, DaemonSet], CancellationToken.None);

        raws[Deployed].Events.Should().ContainSingle().Which.Reason.Should().Be("BackOff");
        raws[DaemonSet].Events.Should().BeEmpty("nothing in the namespace involved that instance's objects");
    }

    [Fact]
    public async Task An_instance_with_only_Jobs_keeps_them()
    {
        var (client, _) = Cluster();

        var raws = await NewClient().ReadWorkloadsAsync(client, Namespace, [ScanOnly], CancellationToken.None);

        raws[ScanOnly].Found.Should().BeFalse();
        raws[ScanOnly].Jobs.Should().ContainSingle();
    }

    [Fact]
    public async Task An_instance_with_nothing_of_its_own_is_NotFound_not_absent()
    {
        var (client, _) = Cluster();

        var raws = await NewClient().ReadWorkloadsAsync(client, Namespace, [TornDown], CancellationToken.None);

        raws.Should().ContainKey(TornDown, "absent means unread, which the caller reports as Unknown — this instance was read and has nothing");
        raws[TornDown].Found.Should().BeFalse();
        raws[TornDown].Jobs.Should().BeEmpty();
    }

    [Fact]
    public async Task Objects_labelled_for_an_instance_nobody_asked_about_are_dropped()
    {
        var (client, _) = Cluster();

        var raws = await NewClient().ReadWorkloadsAsync(client, Namespace, [Deployed], CancellationToken.None);

        raws.Should().NotContainKey(Stranger);
        raws.Should().HaveCount(1);
    }

    [Fact]
    public async Task An_unreadable_primary_list_reports_nothing_rather_than_a_fleet_of_NotFounds()
    {
        var client = ClusterWithFailingDeployments();

        var raws = await NewClient().ReadWorkloadsAsync(client, Namespace, [Deployed, DaemonSet], CancellationToken.None);

        raws.Should().BeEmpty();
    }

    [Fact]
    public async Task A_blipped_pod_list_is_not_fatal_and_is_flagged_as_unread()
    {
        var client = ClusterWithFailingPods();

        var raws = await NewClient().ReadWorkloadsAsync(client, Namespace, [Deployed], CancellationToken.None);

        raws[Deployed].Found.Should().BeTrue();
        raws[Deployed].PodsRead.Should().BeFalse("the mapper must fall back to ready/desired rather than read a blip as no pods");
    }

    private static KubernetesObjectClient NewClient() => new(NullLogger<KubernetesObjectClient>.Instance);

    private sealed class CallCounts
    {
        public int Deployments { get; set; }
        public int DaemonSets { get; set; }
        public int Jobs { get; set; }
        public int Pods { get; set; }
        public int Events { get; set; }
        public string? EventFieldSelector { get; set; }
    }

    private static HttpOperationResponse<T> Response<T>(T body) =>
        new() { Body = body, Response = new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.OK) };

    private static V1ObjectMeta Meta(string name, Guid instanceId) =>
        new()
        {
            Name = name,
            Labels = new Dictionary<string, string> { [InstanceLabel] = instanceId.ToString("D") },
        };

    private static V1DeploymentList Deployments() => new()
    {
        Items =
        [
            new V1Deployment
            {
                Metadata = Meta("reeve-wl-thread-11111111", Deployed),
                Spec = new V1DeploymentSpec { Replicas = 1 },
                Status = new V1DeploymentStatus { ReadyReplicas = 1 },
            },
            new V1Deployment
            {
                Metadata = Meta("reeve-wl-other-55555555", Stranger),
                Spec = new V1DeploymentSpec { Replicas = 1 },
                Status = new V1DeploymentStatus { ReadyReplicas = 1 },
            },
        ],
    };

    private static V1DaemonSetList DaemonSets() => new()
    {
        Items =
        [
            new V1DaemonSet
            {
                Metadata = Meta("reeve-wl-scan-22222222", DaemonSet),
                Status = new V1DaemonSetStatus { DesiredNumberScheduled = 3, NumberReady = 2, CurrentNumberScheduled = 3, NumberMisscheduled = 0 },
            },
        ],
    };

    private static V1JobList Jobs() => new()
    {
        Items =
        [
            new V1Job
            {
                Metadata = Meta("reeve-scan-33333333-node-a", ScanOnly),
                Status = new V1JobStatus { Succeeded = 1 },
            },
        ],
    };

    private static V1PodList Pods() => new()
    {
        Items =
        [
            new V1Pod { Metadata = Meta("reeve-wl-thread-11111111-abc", Deployed), Status = new V1PodStatus { Phase = "Running" } },
            new V1Pod
            {
                Metadata = Meta("reeve-wl-scan-22222222-xyz", DaemonSet),
                Status = new V1PodStatus
                {
                    Phase = "Running",
                    Conditions = [new V1PodCondition { Type = "Ready", Status = "False", Reason = "ContainersNotReady", Message = "containers with unready status: [scan]" }],
                },
            },
        ],
    };

    private static Corev1EventList Events() => new()
    {
        Items =
        [
            new Corev1Event
            {
                InvolvedObject = new V1ObjectReference { Name = "reeve-wl-thread-11111111-abc" },
                Reason = "BackOff",
                Message = "Back-off restarting failed container",
                Type = "Warning",
                LastTimestamp = DateTime.UtcNow,
                Metadata = new V1ObjectMeta(),
            },
            new Corev1Event
            {
                InvolvedObject = new V1ObjectReference { Name = "reeve-wl-other-55555555" },
                Reason = "Scheduled",
                Message = "not ours",
                Type = "Normal",
                LastTimestamp = DateTime.UtcNow,
                Metadata = new V1ObjectMeta(),
            },
        ],
    };

    private static (IKubernetes Client, CallCounts Calls) Cluster()
    {
        var calls = new CallCounts();
        var appsV1 = Substitute.For<IAppsV1Operations>();
        ListDeployments(appsV1).Returns(_ => { calls.Deployments++; return Task.FromResult(Response(Deployments())); });
        ListDaemonSets(appsV1).Returns(_ => { calls.DaemonSets++; return Task.FromResult(Response(DaemonSets())); });

        var batchV1 = Substitute.For<IBatchV1Operations>();
        ListJobs(batchV1).Returns(_ => { calls.Jobs++; return Task.FromResult(Response(Jobs())); });

        var coreV1 = Substitute.For<ICoreV1Operations>();
        ListPods(coreV1).Returns(_ => { calls.Pods++; return Task.FromResult(Response(Pods())); });
        ListEvents(coreV1).Returns(ci =>
        {
            calls.Events++;
            calls.EventFieldSelector = ci.ArgAt<string>(3);
            return Task.FromResult(Response(Events()));
        });

        return (Client(appsV1, batchV1, coreV1), calls);
    }

    private static IKubernetes ClusterWithFailingDeployments()
    {
        var appsV1 = Substitute.For<IAppsV1Operations>();
        ListDeployments(appsV1).Returns<Task<HttpOperationResponse<V1DeploymentList>>>(
            _ => throw new System.Net.Http.HttpRequestException("api server unreachable"));
        ListDaemonSets(appsV1).Returns(_ => Task.FromResult(Response(new V1DaemonSetList { Items = [] })));

        var batchV1 = Substitute.For<IBatchV1Operations>();
        ListJobs(batchV1).Returns(_ => Task.FromResult(Response(new V1JobList { Items = [] })));

        var coreV1 = Substitute.For<ICoreV1Operations>();
        ListPods(coreV1).Returns(_ => Task.FromResult(Response(new V1PodList { Items = [] })));
        ListEvents(coreV1).Returns(_ => Task.FromResult(Response(new Corev1EventList { Items = [] })));

        return Client(appsV1, batchV1, coreV1);
    }

    private static IKubernetes ClusterWithFailingPods()
    {
        var appsV1 = Substitute.For<IAppsV1Operations>();
        ListDeployments(appsV1).Returns(_ => Task.FromResult(Response(Deployments())));
        ListDaemonSets(appsV1).Returns(_ => Task.FromResult(Response(new V1DaemonSetList { Items = [] })));

        var batchV1 = Substitute.For<IBatchV1Operations>();
        ListJobs(batchV1).Returns(_ => Task.FromResult(Response(new V1JobList { Items = [] })));

        var coreV1 = Substitute.For<ICoreV1Operations>();
        ListPods(coreV1).Returns<Task<HttpOperationResponse<V1PodList>>>(
            _ => throw new System.Net.Http.HttpRequestException("pod list blip"));
        ListEvents(coreV1).Returns(_ => Task.FromResult(Response(new Corev1EventList { Items = [] })));

        return Client(appsV1, batchV1, coreV1);
    }

    private static IKubernetes Client(IAppsV1Operations appsV1, IBatchV1Operations batchV1, ICoreV1Operations coreV1)
    {
        var client = Substitute.For<IKubernetes>();
        client.AppsV1.Returns(appsV1);
        client.BatchV1.Returns(batchV1);
        client.CoreV1.Returns(coreV1);
        return client;
    }

    private static Task<HttpOperationResponse<V1DeploymentList>> ListDeployments(IAppsV1Operations appsV1) =>
        appsV1.ListNamespacedDeploymentWithHttpMessagesAsync(
            Namespace, Arg.Any<bool?>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<int?>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<bool?>(), Arg.Any<string>(), Arg.Any<int?>(),
            Arg.Any<bool?>(), Arg.Any<bool?>(),
            Arg.Any<IReadOnlyDictionary<string, IReadOnlyList<string>>>(), Arg.Any<CancellationToken>());

    private static Task<HttpOperationResponse<V1DaemonSetList>> ListDaemonSets(IAppsV1Operations appsV1) =>
        appsV1.ListNamespacedDaemonSetWithHttpMessagesAsync(
            Namespace, Arg.Any<bool?>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<int?>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<bool?>(), Arg.Any<string>(), Arg.Any<int?>(),
            Arg.Any<bool?>(), Arg.Any<bool?>(),
            Arg.Any<IReadOnlyDictionary<string, IReadOnlyList<string>>>(), Arg.Any<CancellationToken>());

    private static Task<HttpOperationResponse<V1JobList>> ListJobs(IBatchV1Operations batchV1) =>
        batchV1.ListNamespacedJobWithHttpMessagesAsync(
            Namespace, Arg.Any<bool?>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<int?>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<bool?>(), Arg.Any<string>(), Arg.Any<int?>(),
            Arg.Any<bool?>(), Arg.Any<bool?>(),
            Arg.Any<IReadOnlyDictionary<string, IReadOnlyList<string>>>(), Arg.Any<CancellationToken>());

    private static Task<HttpOperationResponse<V1PodList>> ListPods(ICoreV1Operations coreV1) =>
        coreV1.ListNamespacedPodWithHttpMessagesAsync(
            Namespace, Arg.Any<bool?>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<int?>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<bool?>(), Arg.Any<string>(), Arg.Any<int?>(),
            Arg.Any<bool?>(), Arg.Any<bool?>(),
            Arg.Any<IReadOnlyDictionary<string, IReadOnlyList<string>>>(), Arg.Any<CancellationToken>());

    private static Task<HttpOperationResponse<Corev1EventList>> ListEvents(ICoreV1Operations coreV1) =>
        coreV1.ListNamespacedEventWithHttpMessagesAsync(
            Namespace, Arg.Any<bool?>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<int?>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<bool?>(), Arg.Any<string>(), Arg.Any<int?>(),
            Arg.Any<bool?>(), Arg.Any<bool?>(),
            Arg.Any<IReadOnlyDictionary<string, IReadOnlyList<string>>>(), Arg.Any<CancellationToken>());
}
