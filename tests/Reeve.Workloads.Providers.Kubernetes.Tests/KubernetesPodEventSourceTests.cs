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
using Xunit;

namespace Reeve.Workloads.Providers.Kubernetes.Tests;

public sealed class KubernetesPodEventSourceTests
{
    private static readonly DateTime Stamp = new(2026, 10, 8, 12, 0, 0, DateTimeKind.Utc);

    private static IKubernetes ClientReturning(Func<Task<HttpOperationResponse<Corev1EventList>>> answer)
    {
        var core = Substitute.For<ICoreV1Operations>();
        core.ListNamespacedEventWithHttpMessagesAsync(
                Arg.Any<string>(), Arg.Any<bool?>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<int?>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<bool?>(), Arg.Any<string>(), Arg.Any<int?>(),
                Arg.Any<bool?>(), Arg.Any<bool?>(),
                Arg.Any<IReadOnlyDictionary<string, IReadOnlyList<string>>>(), Arg.Any<CancellationToken>())
            .Returns(_ => answer());
        var client = Substitute.For<IKubernetes>();
        client.CoreV1.Returns(core);
        return client;
    }

    private static Corev1Event Event(string reason, string message, string type = "Warning") => new()
    {
        Metadata = new V1ObjectMeta { Name = Guid.NewGuid().ToString() },
        Reason = reason,
        Message = message,
        Type = type,
    };

    [Fact]
    public async Task Each_event_is_dated_from_the_first_timestamp_the_api_server_filled_in()
    {
        var last = Event("A", "last");
        last.LastTimestamp = Stamp;
        var first = Event("B", "first");
        first.FirstTimestamp = Stamp.AddMinutes(-1);
        var created = Event("C", "created");
        created.Metadata.CreationTimestamp = Stamp.AddMinutes(-2);
        var none = Event("D", "none", type: "Normal");
        var client = ClientReturning(() => Task.FromResult(new HttpOperationResponse<Corev1EventList>
        {
            Body = new Corev1EventList { Items = [last, first, created, none] },
        }));

        var events = await new KubernetesPodLogSource(NullLogger<KubernetesPodLogSource>.Instance)
            .ListPodEventsAsync(client, "ns", "pod", CancellationToken.None);

        events.Select(e => e.AtUtc).Should().Equal(
            new DateTimeOffset(Stamp),
            new DateTimeOffset(Stamp.AddMinutes(-1)),
            new DateTimeOffset(Stamp.AddMinutes(-2)),
            DateTimeOffset.MinValue);
        events.Select(e => e.Warning).Should().Equal(true, true, true, false);
        events[0].Reason.Should().Be("A");
        events[0].Message.Should().Be("last");
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task An_api_refusal_yields_no_events_instead_of_a_fault(HttpStatusCode status)
    {
        var client = ClientReturning(() => throw new HttpOperationException("refused")
        {
            Response = new HttpResponseMessageWrapper(new HttpResponseMessage(status), "{}"),
        });

        var events = await new KubernetesPodLogSource(NullLogger<KubernetesPodLogSource>.Instance)
            .ListPodEventsAsync(client, "ns", "pod", CancellationToken.None);

        events.Should().BeEmpty();
    }

    [Fact]
    public async Task A_listed_pod_carries_its_phase_and_the_Multus_network_status_annotation()
    {
        const string status = """[{"name":"reeve/vlan100-matter-adapter","interface":"net1"}]""";
        var core = Substitute.For<ICoreV1Operations>();
        core.ListNamespacedPodWithHttpMessagesAsync(
                Arg.Any<string>(), Arg.Any<bool?>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<int?>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<bool?>(), Arg.Any<string>(), Arg.Any<int?>(),
                Arg.Any<bool?>(), Arg.Any<bool?>(),
                Arg.Any<IReadOnlyDictionary<string, IReadOnlyList<string>>>(), Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult(new HttpOperationResponse<V1PodList>
            {
                Body = new V1PodList
                {
                    Items =
                    [
                        new V1Pod
                        {
                            Metadata = new V1ObjectMeta
                            {
                                Name = "adapter",
                                Annotations = new Dictionary<string, string> { ["k8s.v1.cni.cncf.io/network-status"] = status },
                            },
                            Status = new V1PodStatus { Phase = "Running" },
                        },
                        new V1Pod { Metadata = new V1ObjectMeta { Name = "plain" }, Status = new V1PodStatus { Phase = "Succeeded" } },
                    ],
                },
            }));
        var client = Substitute.For<IKubernetes>();
        client.CoreV1.Returns(core);

        var pods = await new KubernetesPodLogSource(NullLogger<KubernetesPodLogSource>.Instance)
            .ListPodsAsync(client, "ns", new Dictionary<string, string>(), CancellationToken.None);

        var adapter = pods.Single(p => p.PodName == "adapter");
        adapter.Phase.Should().Be("Running");
        adapter.NetworkStatusJson.Should().Be(status);
        var plain = pods.Single(p => p.PodName == "plain");
        plain.Phase.Should().Be("Succeeded");
        plain.NetworkStatusJson.Should().BeNull();
    }
}
