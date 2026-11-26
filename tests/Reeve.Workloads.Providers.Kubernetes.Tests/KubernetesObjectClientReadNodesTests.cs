using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
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

public sealed class KubernetesObjectClientReadNodesTests
{
    private static readonly IReadOnlyDictionary<string, string> NoSelector = new Dictionary<string, string>();

    private static KubernetesObjectClient NewClient() =>
        new(NullLogger<KubernetesObjectClient>.Instance);

    private static HttpOperationResponse<T> Response<T>(T body) =>
        new() { Body = body, Response = new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.OK) };

    [Fact]
    public async Task ListReadyNodes_applies_the_R18_predicate_and_returns_only_the_eligible_names()
    {
        var harness = new Harness(new[]
        {
            ReadyNode("worker-ready"),
            Cordoned("worker-cordoned"),
            Tainted("worker-noexecute", effect: "NoExecute"),
            Tainted("control-plane", effect: "NoSchedule",
                key: "node-role.kubernetes.io/control-plane"),
            NotReady("worker-notready"),
            NoReadyCondition("worker-noreadycond"),
            NullConditions("worker-nullconds"),
        });

        var partition = await NewClient().ListReadyNodesAsync(harness.Client, NoSelector, CancellationToken.None);

        partition.Matching.Should().Equal(new[] { "worker-ready", "control-plane" },
            "with an EMPTY selector the widened read yields exactly the pre-widening names, in the " +
            "pre-widening (API list) order — only Ready=True, uncordoned, non-NoExecute nodes are eligible");
        partition.Excluded.Should().BeEmpty(
            "an empty selector excludes NOBODY — every Ready node is eligible, so the *skipped, ineligible* report is empty");
    }

    [Fact]
    public async Task ListReadyNodes_with_an_empty_selector_never_reports_a_predicate_excluded_node_as_INELIGIBLE()
    {
        var harness = new Harness(new[] { NotReady("a"), Cordoned("b") });

        var partition = await NewClient().ListReadyNodesAsync(harness.Client, NoSelector, CancellationToken.None);

        partition.Matching.Should().BeEmpty("a genuinely zero-Ready cluster, never an unread one");
        partition.Excluded.Should().BeEmpty(
            "the partition splits the READY set by the selector; a non-Ready node belongs to neither side");
    }

    [Fact]
    public async Task ListReadyNodes_includes_a_PreferNoSchedule_tainted_ready_node()
    {
        var harness = new Harness(new[]
        {
            Tainted("worker-prefer", effect: "PreferNoSchedule", key: "example.com/soft"),
        });

        var partition = await NewClient().ListReadyNodesAsync(harness.Client, NoSelector, CancellationToken.None);

        partition.Matching.Should().Equal("worker-prefer");
    }

    [Fact]
    public async Task ListReadyNodes_returns_empty_for_a_genuinely_zero_Ready_cluster()
    {
        var harness = new Harness(new[] { NotReady("a"), Cordoned("b") });

        var partition = await NewClient().ListReadyNodesAsync(harness.Client, NoSelector, CancellationToken.None);

        partition.Matching.Should().BeEmpty();
    }

    [Fact]
    public async Task ListReadyNodes_partitions_the_ready_set_into_eligible_and_INELIGIBLE_for_a_selector()
    {
        var selector = new Dictionary<string, string> { ["reeve.io/vlan100"] = "true" };
        var harness = new Harness(new[]
        {
            Labelled("node-with-vlan", ("reeve.io/vlan100", "true")),
            Labelled("node-no-vlan"),
            Labelled("node-wrong-value", ("reeve.io/vlan100", "false")),
            Labelled("node-also-vlan", ("reeve.io/vlan100", "true"), ("x", "y")),
            Cordoned("node-cordoned"),
        });

        var partition = await NewClient().ListReadyNodesAsync(harness.Client, selector, CancellationToken.None);

        partition.Matching.Should().Equal(new[] { "node-with-vlan", "node-also-vlan" },
            "the eligible side keeps API order and is what reaches ScanReadyNodeNames / the DaemonSet");
        partition.Excluded.Should().Equal(new[] { "node-no-vlan", "node-wrong-value" },
            "the excluded side is the *skipped, ineligible* report (IneligibleNodeNames)");

        partition.Matching.Should().NotBeEmpty();
        partition.Excluded.Should().NotBeEmpty();
        partition.Excluded.Should().Contain("node-no-vlan");
        partition.Matching.Should().NotContain("node-no-vlan");
        partition.Matching.Concat(partition.Excluded).Should().OnlyHaveUniqueItems(
            "a node appears on at most one side of the partition");
    }

    [Fact]
    public async Task ListReadyNodes_requires_EVERY_selector_label_to_match_not_merely_one()
    {
        var selector = new Dictionary<string, string>
        {
            ["reeve.io/vlan100"] = "true",
            ["kubernetes.io/arch"] = "amd64",
        };
        var harness = new Harness(new[]
        {
            Labelled("both", ("reeve.io/vlan100", "true"), ("kubernetes.io/arch", "amd64")),
            Labelled("only-vlan", ("reeve.io/vlan100", "true")),
            Labelled("only-arch", ("kubernetes.io/arch", "amd64")),
        });

        var partition = await NewClient().ListReadyNodesAsync(harness.Client, selector, CancellationToken.None);

        partition.Matching.Should().Equal("both");
        partition.Excluded.Should().Equal("only-vlan", "only-arch");
    }

    [Fact]
    public async Task ListReadyNodes_propagates_a_403_transport_fault_rather_than_swallowing_it_to_empty()
    {
        var harness = new Harness(throwStatus: System.Net.HttpStatusCode.Forbidden);

        var act = async () => await NewClient().ListReadyNodesAsync(harness.Client, NoSelector, CancellationToken.None);

        (await act.Should().ThrowAsync<HttpOperationException>())
            .Which.Response.StatusCode.Should().Be(System.Net.HttpStatusCode.Forbidden);
        harness.ListNodeWasCalled.Should().BeTrue("the concrete client must have attempted the cluster-scoped node list");
    }

    [Fact]
    public async Task ListReadyNodes_still_propagates_a_403_when_a_NON_empty_selector_is_supplied()
    {
        var harness = new Harness(throwStatus: System.Net.HttpStatusCode.Forbidden);
        var selector = new Dictionary<string, string> { ["reeve.io/vlan100"] = "true" };

        var act = async () => await NewClient().ListReadyNodesAsync(harness.Client, selector, CancellationToken.None);

        (await act.Should().ThrowAsync<HttpOperationException>())
            .Which.Response.StatusCode.Should().Be(System.Net.HttpStatusCode.Forbidden);
    }

    [Fact]
    public void IKubernetesObjectClient_declares_EXACTLY_ONE_ListReadyNodesAsync_with_no_default_implementation()
    {
        var members = typeof(IKubernetesObjectClient)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(m => m.Name == nameof(IKubernetesObjectClient.ListReadyNodesAsync))
            .ToList();

        members.Should().ContainSingle(
            "no overload of the OLD signature may survive beside the widened one — every implementer and every call " +
            "site moves in the same commit");
        members[0].IsAbstract.Should().BeTrue(
            "a default interface implementation is barred: it would let an implementer silently keep the old behaviour");
        members[0].GetParameters().Select(p => p.ParameterType).Should().Equal(
            new[] { typeof(IKubernetes), typeof(IReadOnlyDictionary<string, string>), typeof(CancellationToken) },
            "the widened read takes the RESOLVED node-eligibility selector");
        members[0].ReturnType.Should().Be(typeof(Task<KubernetesReadyNodePartition>),
            "it returns the matching AND the excluded names — labels cross none of the downstream seams");
    }

    private static V1Node ReadyNode(string name) => Node(name, ready: "True");

    private static V1Node NotReady(string name) => Node(name, ready: "False");

    private static V1Node Cordoned(string name) => Node(name, ready: "True", unschedulable: true);

    private static V1Node Tainted(string name, string effect, string key = "example.com/taint") =>
        Node(name, ready: "True", taints: new List<V1Taint> { new() { Key = key, Effect = effect } });

    private static V1Node Labelled(string name, params (string Key, string Value)[] labels) =>
        Node(name, ready: "True", labels: labels.ToDictionary(l => l.Key, l => l.Value, StringComparer.Ordinal));

    private static V1Node NoReadyCondition(string name) => new()
    {
        Metadata = new V1ObjectMeta { Name = name },
        Spec = new V1NodeSpec(),
        Status = new V1NodeStatus
        {
            Conditions = new List<V1NodeCondition> { new() { Type = "MemoryPressure", Status = "False" } },
        },
    };

    private static V1Node NullConditions(string name) => new()
    {
        Metadata = new V1ObjectMeta { Name = name },
        Spec = new V1NodeSpec(),
        Status = new V1NodeStatus { Conditions = null },
    };

    private static V1Node Node(
        string name,
        string ready,
        bool unschedulable = false,
        IList<V1Taint>? taints = null,
        IDictionary<string, string>? labels = null) => new()
    {
        Metadata = new V1ObjectMeta { Name = name, Labels = labels },
        Spec = new V1NodeSpec { Unschedulable = unschedulable ? true : (bool?)null, Taints = taints },
        Status = new V1NodeStatus
        {
            Conditions = new List<V1NodeCondition> { new() { Type = "Ready", Status = ready } },
        },
    };

    private sealed class Harness
    {
        public IKubernetes Client { get; }
        public bool ListNodeWasCalled { get; private set; }

        public Harness(IEnumerable<V1Node>? nodes = null, System.Net.HttpStatusCode? throwStatus = null)
        {
            var core = Substitute.For<ICoreV1Operations>();
            var call = core.ListNodeWithHttpMessagesAsync(
                Arg.Any<bool?>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<int?>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<bool?>(), Arg.Any<string>(),
                Arg.Any<int?>(), Arg.Any<bool?>(), Arg.Any<bool?>(),
                Arg.Any<IReadOnlyDictionary<string, IReadOnlyList<string>>>(), Arg.Any<CancellationToken>());

            if (throwStatus is { } status)
            {
                call.Returns<Task<HttpOperationResponse<V1NodeList>>>(_ =>
                {
                    ListNodeWasCalled = true;
                    throw new HttpOperationException($"nodes read rejected ({(int)status})")
                    {
                        Response = new HttpResponseMessageWrapper(
                            new System.Net.Http.HttpResponseMessage(status), string.Empty),
                    };
                });
            }
            else
            {
                var list = new V1NodeList { Items = new List<V1Node>(nodes ?? Array.Empty<V1Node>()) };
                call.Returns(_ =>
                {
                    ListNodeWasCalled = true;
                    return Task.FromResult(Response(list));
                });
            }

            Client = Substitute.For<IKubernetes>();
            Client.CoreV1.Returns(core);
        }
    }
}
