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

public sealed class KubernetesObjectClientReadJobsTests
{
    private const string Namespace = "reeve";
    private const string DeploymentName = "reeve-wl-matter-11111111";

    private static readonly IReadOnlyDictionary<string, string> Selector =
        new Dictionary<string, string> { ["reeve.io/workload-instance"] = "11111111" };

    private static KubernetesObjectClient NewClient() =>
        new(NullLogger<KubernetesObjectClient>.Instance);

    private static HttpOperationResponse<T> Response<T>(T body) =>
        new() { Body = body, Response = new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.OK) };

    [Fact]
    public async Task ReadWorkload_projects_each_Job_status_faithfully_via_MapJobs()
    {
        var completed = new DateTime(2026, 7, 20, 10, 30, 0, DateTimeKind.Unspecified);
        var harness = new Harness(
            withDeployment: true,
            seededJobs: new[]
            {
                MakeJob("reeve-wl-node1-run1", new V1JobStatus { Succeeded = 3, CompletionTime = completed }),
                MakeJob("reeve-wl-node2-run1", new V1JobStatus { Active = 1 }),
                MakeJob("reeve-wl-node3-run1", status: null),
            });

        var raw = await NewClient().ReadWorkloadAsync(harness.Client, Namespace, Selector, CancellationToken.None);

        raw.Should().NotBeNull();
        raw!.Found.Should().BeTrue("the Deployment read succeeded");
        raw.Jobs.Should().HaveCount(3);

        var completedJob = raw.Jobs.Single(j => j.Name == "reeve-wl-node1-run1");
        completedJob.Succeeded.Should().Be(3);
        completedJob.Failed.Should().Be(0, "a null status.failed must project to 0");
        completedJob.Active.Should().Be(0, "a null status.active must project to 0");
        completedJob.CompletionTime.Should().NotBeNull();
        completedJob.CompletionTime!.Value.Offset.Should().Be(TimeSpan.Zero,
            "completionTime must be projected as a UTC (zero-offset) DateTimeOffset");
        completedJob.CompletionTime!.Value.UtcDateTime.Should().Be(
            DateTime.SpecifyKind(completed, DateTimeKind.Utc),
            "the wall-clock instant must be preserved and interpreted as UTC");

        var runningJob = raw.Jobs.Single(j => j.Name == "reeve-wl-node2-run1");
        runningJob.Active.Should().Be(1);
        runningJob.Succeeded.Should().Be(0);
        runningJob.Failed.Should().Be(0);
        runningJob.CompletionTime.Should().BeNull("a still-running Job has no completion time");

        var statuslessJob = raw.Jobs.Single(j => j.Name == "reeve-wl-node3-run1");
        statuslessJob.Succeeded.Should().Be(0);
        statuslessJob.Failed.Should().Be(0);
        statuslessJob.Active.Should().Be(0);
        statuslessJob.CompletionTime.Should().BeNull();
    }

    [Fact]
    public async Task ReadWorkload_surfaces_Jobs_when_no_Deployment_or_DaemonSet_matched()
    {
        var harness = new Harness(
            withDeployment: false,
            withDaemonSet: false,
            seededJobs: new[]
            {
                MakeJob("reeve-wl-node1-run1", new V1JobStatus { Succeeded = 1, CompletionTime = new DateTime(2026, 7, 20, 9, 0, 0, DateTimeKind.Unspecified) }),
                MakeJob("reeve-wl-node2-run1", new V1JobStatus { Active = 1 }),
            });

        var raw = await NewClient().ReadWorkloadAsync(harness.Client, Namespace, Selector, CancellationToken.None);

        raw.Should().NotBeNull("a reachable cluster with only Jobs is NOT unreachable");
        raw!.Found.Should().BeFalse("no Deployment/DaemonSet matched — there is no primary workload object");
        raw.Jobs.Should().HaveCount(2, "the per-node scan Jobs must be surfaced, not dropped to a bare NotFound");
        raw.Jobs.Select(j => j.Name).Should().BeEquivalentTo(
            new[] { "reeve-wl-node1-run1", "reeve-wl-node2-run1" });
    }

    [Fact]
    public async Task ReadWorkload_returns_NotFound_with_empty_Jobs_when_nothing_matches()
    {
        var harness = new Harness(
            withDeployment: false,
            withDaemonSet: false,
            seededJobs: Array.Empty<V1Job>());

        var raw = await NewClient().ReadWorkloadAsync(harness.Client, Namespace, Selector, CancellationToken.None);

        raw.Should().NotBeNull("a reachable-but-empty cluster is NotFound, not unreachable/null");
        raw!.Found.Should().BeFalse();
        raw.Jobs.Should().BeEmpty("zero Jobs ⇒ an empty list");
    }

    [Fact]
    public async Task ReadWorkload_projects_the_node_a_scan_Job_was_pinned_to()
    {
        var harness = new Harness(
            withDeployment: false,
            seededJobs: new[]
            {
                MakeJob("reeve-wl-node1-run1", new V1JobStatus { Succeeded = 1 },
                    nodeLabel: "worker-1-lan-example-com", pinnedNode: "worker-1.lan.example.com"),
                MakeJob("reeve-wl-node2-run1", new V1JobStatus { Failed = 1 }, nodeLabel: "control-plane"),
                MakeJob("reeve-wl-node3-run1", new V1JobStatus { Succeeded = 1 }),
            });

        var raw = await NewClient().ReadWorkloadAsync(harness.Client, Namespace, Selector, CancellationToken.None);

        raw.Should().NotBeNull();
        raw!.Jobs.Single(j => j.Name == "reeve-wl-node1-run1").NodeName.Should().Be("worker-1.lan.example.com",
            "the pinned node name is exact — the label is a sanitised copy of it");
        raw.Jobs.Single(j => j.Name == "reeve-wl-node2-run1").NodeName.Should().Be("control-plane",
            "an unpinned Job still reports the node from its label");
        raw.Jobs.Single(j => j.Name == "reeve-wl-node3-run1").NodeName.Should().BeNull(
            "a Job carrying neither reports no node rather than a fabricated one");
    }

    [Fact]
    public async Task ReadWorkload_tolerates_a_Job_list_blip_without_faulting_the_workload_read()
    {
        var harness = new Harness(
            withDeployment: true,
            seededJobs: Array.Empty<V1Job>(),
            throwOnJobList: true);

        var raw = await NewClient().ReadWorkloadAsync(harness.Client, Namespace, Selector, CancellationToken.None);

        raw.Should().NotBeNull("a Job-list blip must not fault an otherwise-successful workload read");
        raw!.Found.Should().BeTrue("the Deployment read succeeded independently of the Job list");
        raw.Kind.Should().Be("Deployment");
        raw.DesiredReplicas.Should().Be(1);
        raw.ReadyReplicas.Should().Be(1);
        raw.Jobs.Should().BeEmpty("the Job list blipped — Jobs are best-effort, left empty (never null/throw)");

        harness.JobListWasCalled.Should().BeTrue("the concrete client must have attempted the batch/v1 Job list");
    }

    [Fact]
    public async Task ReadWorkload_projects_spec_suspend_into_the_Job_projection()
    {
        var harness = new Harness(
            withDeployment: true,
            seededJobs: new[]
            {
                MakeJob("reeve-wl-node1-run1", new V1JobStatus { Active = 1 }, suspend: true),
                MakeJob("reeve-wl-node2-run1", new V1JobStatus { Active = 1 }, suspend: false),
                MakeJob("reeve-wl-node3-run1", status: null),
            });

        var raw = await NewClient().ReadWorkloadAsync(harness.Client, Namespace, Selector, CancellationToken.None);

        raw.Should().NotBeNull();
        raw!.Jobs.Single(j => j.Name == "reeve-wl-node1-run1").Suspended.Should().BeTrue(
            "spec.suspend=true is the batch/v1 hold and must survive the projection");
        raw.Jobs.Single(j => j.Name == "reeve-wl-node2-run1").Suspended.Should().BeFalse();
        raw.Jobs.Single(j => j.Name == "reeve-wl-node3-run1").Suspended.Should().BeFalse(
            "an absent spec projects to not-held, never to unknown");
    }

    private static V1Job MakeJob(
        string name, V1JobStatus? status, string? nodeLabel = null, string? pinnedNode = null, bool? suspend = null)
    {
        var labels = new Dictionary<string, string> { ["reeve.io/workload-instance"] = "11111111" };
        if (nodeLabel is not null)
        {
            labels["reeve.io/scan-node"] = nodeLabel;
        }

        return new V1Job
        {
            Metadata = new V1ObjectMeta { Name = name, Labels = labels },
            Spec = pinnedNode is null && suspend is null
                ? null
                : new V1JobSpec
                {
                    Suspend = suspend,
                    Template = new V1PodTemplateSpec { Spec = new V1PodSpec { NodeName = pinnedNode, Containers = new List<V1Container>() } },
                },
            Status = status,
        };
    }

    private sealed class Harness
    {
        public IKubernetes Client { get; }
        public bool JobListWasCalled { get; private set; }

        public Harness(
            IEnumerable<V1Job> seededJobs,
            bool withDeployment = false,
            bool withDaemonSet = false,
            bool throwOnJobList = false)
        {
            var jobs = seededJobs.ToList();

            var apps = Substitute.For<IAppsV1Operations>();
            apps.ListNamespacedDeploymentWithHttpMessagesAsync(
                    Namespace, Arg.Any<bool?>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
                    Arg.Any<int?>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<bool?>(), Arg.Any<string>(), Arg.Any<int?>(),
                    Arg.Any<bool?>(), Arg.Any<bool?>(),
                    Arg.Any<IReadOnlyDictionary<string, IReadOnlyList<string>>>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(Response(new V1DeploymentList
                {
                    Items = withDeployment
                        ? new List<V1Deployment>
                        {
                            new()
                            {
                                Metadata = new V1ObjectMeta { Name = DeploymentName },
                                Spec = new V1DeploymentSpec { Replicas = 1 },
                                Status = new V1DeploymentStatus { ReadyReplicas = 1 },
                            },
                        }
                        : new List<V1Deployment>(),
                })));

            apps.ListNamespacedDaemonSetWithHttpMessagesAsync(
                    Namespace, Arg.Any<bool?>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
                    Arg.Any<int?>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<bool?>(), Arg.Any<string>(), Arg.Any<int?>(),
                    Arg.Any<bool?>(), Arg.Any<bool?>(),
                    Arg.Any<IReadOnlyDictionary<string, IReadOnlyList<string>>>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(Response(new V1DaemonSetList
                {
                    Items = withDaemonSet
                        ? new List<V1DaemonSet> { new() { Metadata = new V1ObjectMeta { Name = "ds" } } }
                        : new List<V1DaemonSet>(),
                })));

            var core = Substitute.For<ICoreV1Operations>();
            core.ListNamespacedPodWithHttpMessagesAsync(
                    Namespace, Arg.Any<bool?>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
                    Arg.Any<int?>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<bool?>(), Arg.Any<string>(), Arg.Any<int?>(),
                    Arg.Any<bool?>(), Arg.Any<bool?>(),
                    Arg.Any<IReadOnlyDictionary<string, IReadOnlyList<string>>>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(Response(new V1PodList { Items = new List<V1Pod>() })));
            core.ListNamespacedEventWithHttpMessagesAsync(
                    Namespace, Arg.Any<bool?>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
                    Arg.Any<int?>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<bool?>(), Arg.Any<string>(), Arg.Any<int?>(),
                    Arg.Any<bool?>(), Arg.Any<bool?>(),
                    Arg.Any<IReadOnlyDictionary<string, IReadOnlyList<string>>>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(Response(new Corev1EventList { Items = new List<Corev1Event>() })));

            var batch = Substitute.For<IBatchV1Operations>();
            var jobListCall = batch.ListNamespacedJobWithHttpMessagesAsync(
                Namespace, Arg.Any<bool?>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<int?>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<bool?>(), Arg.Any<string>(), Arg.Any<int?>(),
                Arg.Any<bool?>(), Arg.Any<bool?>(),
                Arg.Any<IReadOnlyDictionary<string, IReadOnlyList<string>>>(), Arg.Any<CancellationToken>());

            if (throwOnJobList)
            {
                jobListCall.Returns<Task<HttpOperationResponse<V1JobList>>>(_ =>
                {
                    JobListWasCalled = true;
                    throw new System.Net.Http.HttpRequestException("Job list blip");
                });
            }
            else
            {
                jobListCall.Returns(_ =>
                {
                    JobListWasCalled = true;
                    return Task.FromResult(Response(new V1JobList { Items = jobs }));
                });
            }

            Client = Substitute.For<IKubernetes>();
            Client.AppsV1.Returns(apps);
            Client.CoreV1.Returns(core);
            Client.BatchV1.Returns(batch);
        }
    }
}
