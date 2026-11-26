using System;
using System.Collections.Generic;
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

public sealed class KubernetesObjectClientReadWorkloadTests
{
    private const string Namespace = "reeve";
    private const string DeploymentName = "reeve-wl-matter-11111111";

    private static KubernetesObjectClient NewClient() =>
        new(NullLogger<KubernetesObjectClient>.Instance);

    private static readonly IReadOnlyDictionary<string, string> Selector =
        new Dictionary<string, string> { ["reeve.io/workload-instance"] = "11111111" };

    private static HttpOperationResponse<T> Response<T>(T body) =>
        new() { Body = body, Response = new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.OK) };

    private static (IKubernetes Client, EventsCall Recorder) ClientWithDeploymentButFailingEvents()
    {
        var recorder = new EventsCall();

        var deploymentList = new V1DeploymentList
        {
            Items = new List<V1Deployment>
            {
                new()
                {
                    Metadata = new V1ObjectMeta { Name = DeploymentName },
                    Spec = new V1DeploymentSpec { Replicas = 1 },
                    Status = new V1DeploymentStatus { ReadyReplicas = 1 },
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
            .Returns(Task.FromResult(Response(deploymentList)));
        appsV1
            .ListNamespacedDaemonSetWithHttpMessagesAsync(
                Namespace, Arg.Any<bool?>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<int?>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<bool?>(), Arg.Any<string>(), Arg.Any<int?>(),
                Arg.Any<bool?>(), Arg.Any<bool?>(),
                Arg.Any<IReadOnlyDictionary<string, IReadOnlyList<string>>>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Response(new V1DaemonSetList { Items = new List<V1DaemonSet>() })));

        var coreV1 = Substitute.For<ICoreV1Operations>();
        coreV1
            .ListNamespacedPodWithHttpMessagesAsync(
                Namespace, Arg.Any<bool?>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<int?>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<bool?>(), Arg.Any<string>(), Arg.Any<int?>(),
                Arg.Any<bool?>(), Arg.Any<bool?>(),
                Arg.Any<IReadOnlyDictionary<string, IReadOnlyList<string>>>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Response(new V1PodList { Items = new List<V1Pod>() })));
        coreV1
            .ListNamespacedEventWithHttpMessagesAsync(
                Namespace, Arg.Any<bool?>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<int?>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<bool?>(), Arg.Any<string>(), Arg.Any<int?>(),
                Arg.Any<bool?>(), Arg.Any<bool?>(),
                Arg.Any<IReadOnlyDictionary<string, IReadOnlyList<string>>>(), Arg.Any<CancellationToken>())
            .Returns<Task<HttpOperationResponse<Corev1EventList>>>(ci =>
            {
                recorder.WasCalled = true;
                recorder.FieldSelector = ci.ArgAt<string>(3);
                throw new System.Net.Http.HttpRequestException("events read blip");
            });

        var client = Substitute.For<IKubernetes>();
        client.AppsV1.Returns(appsV1);
        client.CoreV1.Returns(coreV1);
        return (client, recorder);
    }

    [Fact]
    public async Task ReadWorkload_returns_the_found_status_when_the_events_read_blips()
    {
        var (client, _) = ClientWithDeploymentButFailingEvents();

        var raw = await NewClient().ReadWorkloadAsync(client, Namespace, Selector, CancellationToken.None);

        raw.Should().NotBeNull("a successful workload read must survive a failed events read");
        raw!.Found.Should().BeTrue();
        raw.Kind.Should().Be("Deployment");
        raw.DesiredReplicas.Should().Be(1);
        raw.ReadyReplicas.Should().Be(1);
        raw.Events.Should().BeEmpty("the events read blipped — events are best-effort, not fatal");
    }

    [Fact]
    public async Task ReadWorkload_field_scopes_the_events_read_to_the_involved_object()
    {
        var (client, recorder) = ClientWithDeploymentButFailingEvents();

        await NewClient().ReadWorkloadAsync(client, Namespace, Selector, CancellationToken.None);

        recorder.WasCalled.Should().BeTrue();
        recorder.FieldSelector.Should().NotBeNullOrEmpty(
            "the events read must be field-scoped (involvedObject.name=…), not an unfiltered whole-namespace list");
        recorder.FieldSelector.Should().Contain(DeploymentName,
            "the field selector must target the workload object's involvedObject.name");
    }

    [Fact]
    public async Task ReadWorkload_reports_a_refused_list_as_unreachable_never_as_not_found()
    {
        var appsV1 = Substitute.For<IAppsV1Operations>();
        appsV1
            .ListNamespacedDeploymentWithHttpMessagesAsync(
                Namespace, Arg.Any<bool?>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<int?>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<bool?>(), Arg.Any<string>(), Arg.Any<int?>(),
                Arg.Any<bool?>(), Arg.Any<bool?>(),
                Arg.Any<IReadOnlyDictionary<string, IReadOnlyList<string>>>(), Arg.Any<CancellationToken>())
            .Returns<Task<HttpOperationResponse<V1DeploymentList>>>(_ => throw new HttpOperationException("Forbidden"));
        var client = Substitute.For<IKubernetes>();
        client.AppsV1.Returns(appsV1);

        var raw = await NewClient().ReadWorkloadAsync(client, Namespace, Selector, CancellationToken.None);

        raw.Should().BeNull("the reconciler recreates a workload read as not found, so a failed read must stay unknown");
    }

    [Fact]
    public async Task ReadWorkload_reports_not_found_only_when_the_lists_succeed_empty()
    {
        var appsV1 = Substitute.For<IAppsV1Operations>();
        appsV1
            .ListNamespacedDeploymentWithHttpMessagesAsync(
                Namespace, Arg.Any<bool?>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<int?>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<bool?>(), Arg.Any<string>(), Arg.Any<int?>(),
                Arg.Any<bool?>(), Arg.Any<bool?>(),
                Arg.Any<IReadOnlyDictionary<string, IReadOnlyList<string>>>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Response(new V1DeploymentList { Items = new List<V1Deployment>() })));
        appsV1
            .ListNamespacedDaemonSetWithHttpMessagesAsync(
                Namespace, Arg.Any<bool?>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<int?>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<bool?>(), Arg.Any<string>(), Arg.Any<int?>(),
                Arg.Any<bool?>(), Arg.Any<bool?>(),
                Arg.Any<IReadOnlyDictionary<string, IReadOnlyList<string>>>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Response(new V1DaemonSetList { Items = new List<V1DaemonSet>() })));
        var client = Substitute.For<IKubernetes>();
        client.AppsV1.Returns(appsV1);

        var raw = await NewClient().ReadWorkloadAsync(client, Namespace, Selector, CancellationToken.None);

        raw.Should().NotBeNull();
        raw!.Found.Should().BeFalse();
    }

    private sealed class EventsCall
    {
        public bool WasCalled { get; set; }
        public string? FieldSelector { get; set; }
    }
}
