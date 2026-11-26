using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using k8s;
using k8s.Autorest;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Reeve.Workloads.Providers.Kubernetes;
using Xunit;

namespace Reeve.Workloads.Providers.Kubernetes.Tests;

public sealed class KubernetesObjectClientReadNadsTests
{
    private const string Ns = "reeve-workloads";

    private static KubernetesObjectClient NewClient() =>
        new(NullLogger<KubernetesObjectClient>.Instance);

    [Fact]
    public async Task Populated_list_yields_the_NAD_names_and_no_reason()
    {
        var harness = Harness.Returning(NadList(
            Nad("vlan100", Conf("macvlan", master: "eth0.100")),
            Nad("ipvlan-lan", Conf("ipvlan", master: "eth1.100")),
            Nad("br0", Conf("bridge"))));

        var result = await NewClient().ListNetworkAttachmentDefinitionsAsync(harness.Client, Ns, CancellationToken.None);

        result.UnavailableReason.Should().BeNull("a populated 200 is a plain success — no degraded reason");
        result.Items.Select(i => i.Name).Should().Equal("vlan100", "ipvlan-lan", "br0");
    }

    [Fact]
    public async Task The_read_goes_straight_to_the_CustomObjects_group_for_the_Multus_CRD_in_the_workloads_namespace()
    {
        var harness = Harness.Returning(NadList(Nad("vlan100", Conf("macvlan"))));

        await NewClient().ListNetworkAttachmentDefinitionsAsync(harness.Client, Ns, CancellationToken.None);

        harness.LastCall.Should().NotBeNull("the member must reach CustomObjects.ListNamespacedCustomObjectAsync");
        harness.LastCall!.Value.Should().Be(
            ("k8s.cni.cncf.io", "v1", Ns, "network-attachment-definitions"),
            "the read targets k8s.cni.cncf.io/v1 network-attachment-definitions in Reeve's WORKLOADS namespace " +
            "(the namespaced read grant — a cluster-scoped read would need a ClusterRole, which is not granted)");
    }

    [Fact]
    public async Task Empty_namespace_is_none_in_namespace_NOT_multus_not_installed()
    {
        var harness = Harness.Returning(NadList());

        var result = await NewClient().ListNetworkAttachmentDefinitionsAsync(harness.Client, Ns, CancellationToken.None);

        result.Items.Should().BeEmpty();
        result.UnavailableReason.Should().Be("none-in-namespace");
    }

    [Fact]
    public async Task A_403_is_forbidden()
    {
        var harness = Harness.Throwing(HttpFault(System.Net.HttpStatusCode.Forbidden));

        var result = await NewClient().ListNetworkAttachmentDefinitionsAsync(harness.Client, Ns, CancellationToken.None);

        result.Items.Should().BeEmpty();
        result.UnavailableReason.Should().Be("forbidden");
    }

    [Fact]
    public async Task A_404_CRD_NotFound_is_multus_not_installed()
    {
        var harness = Harness.Throwing(HttpFault(System.Net.HttpStatusCode.NotFound));

        var result = await NewClient().ListNetworkAttachmentDefinitionsAsync(harness.Client, Ns, CancellationToken.None);

        result.Items.Should().BeEmpty();
        result.UnavailableReason.Should().Be("multus-not-installed");
    }

    [Fact]
    public async Task A_transport_fault_is_provider_unavailable()
    {
        var harness = Harness.Throwing(new System.Net.Http.HttpRequestException("connection refused"));

        var result = await NewClient().ListNetworkAttachmentDefinitionsAsync(harness.Client, Ns, CancellationToken.None);

        result.Items.Should().BeEmpty();
        result.UnavailableReason.Should().Be("provider-unavailable");
    }

    [Fact]
    public async Task A_500_from_the_API_server_is_provider_unavailable_and_never_escapes_as_an_exception()
    {
        var harness = Harness.Throwing(HttpFault(System.Net.HttpStatusCode.InternalServerError));

        var act = async () => await NewClient()
            .ListNetworkAttachmentDefinitionsAsync(harness.Client, Ns, CancellationToken.None);

        var result = await act.Should().NotThrowAsync();
        result.Subject.Items.Should().BeEmpty();
        result.Subject.UnavailableReason.Should().Be("provider-unavailable");
    }

    [Fact]
    public async Task Caller_cancellation_still_propagates()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        var harness = Harness.Throwing(new OperationCanceledException(cts.Token));

        var act = async () => await NewClient()
            .ListNetworkAttachmentDefinitionsAsync(harness.Client, Ns, cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task A_nad_whose_config_is_not_json_or_names_an_unknown_type_is_listed_by_name()
    {
        var harness = Harness.Returning(NadList(
            Nad("broken", "this is not json {{"),
            Nad("sriov-net", Conf("sriov")),
            Nad("no-type", """{"cniVersion":"0.3.1","name":"x"}"""),
            Nad("no-config", config: null)));

        var result = await NewClient().ListNetworkAttachmentDefinitionsAsync(harness.Client, Ns, CancellationToken.None);

        result.UnavailableReason.Should().BeNull();
        result.Items.Select(i => i.Name).Should().Equal("broken", "sriov-net", "no-type", "no-config");
    }

    [Fact]
    public async Task A_nad_whose_spec_config_changed_lists_identically()
    {
        var before = await NewClient().ListNetworkAttachmentDefinitionsAsync(
            Harness.Returning(NadList(Nad("vlan100", Conf("macvlan", master: "eth0.100")))).Client, Ns, CancellationToken.None);
        var after = await NewClient().ListNetworkAttachmentDefinitionsAsync(
            Harness.Returning(NadList(Nad("vlan100", Conf("ipvlan", master: "eth1.200")))).Client, Ns, CancellationToken.None);

        after.Should().BeEquivalentTo(before);
    }

    [Theory]
    [InlineData("this is not a listing")]
    [InlineData("""{"apiVersion":"k8s.cni.cncf.io/v1","kind":"Status","status":"Failure"}""")]
    [InlineData("""{"items":"nope"}""")]
    [InlineData("[]")]
    public async Task A_200_whose_body_is_not_a_listing_is_provider_unavailable_not_none_in_namespace(string body)
    {
        var harness = Harness.Returning(body.StartsWith('{') || body.StartsWith('[')
            ? (object)JsonSerializer.Deserialize<JsonElement>(body)
            : body);

        var result = await NewClient().ListNetworkAttachmentDefinitionsAsync(harness.Client, Ns, CancellationToken.None);

        result.Items.Should().BeEmpty();
        result.UnavailableReason.Should().Be("provider-unavailable");
    }

    private static string Conf(string type, string? master = null) =>
        master is null
            ? $$"""{"cniVersion":"0.3.1","name":"n","type":"{{type}}"}"""
            : $$"""{"cniVersion":"0.3.1","name":"n","type":"{{type}}","master":"{{master}}"}""";

    private static string Nad(string name, string? config)
    {
        var spec = config is null ? "{}" : $$"""{"config":{{JsonSerializer.Serialize(config)}}}""";
        return $$"""{"apiVersion":"k8s.cni.cncf.io/v1","kind":"NetworkAttachmentDefinition","metadata":{"name":"{{name}}","namespace":"{{Ns}}"},"spec":{{spec}}}""";
    }

    private static JsonElement NadList(params string[] items) => JsonSerializer.Deserialize<JsonElement>(
        $$"""{"apiVersion":"k8s.cni.cncf.io/v1","kind":"NetworkAttachmentDefinitionList","items":[{{string.Join(",", items)}}]}""");

    private static HttpOperationException HttpFault(System.Net.HttpStatusCode status) =>
        new($"network-attachment-definitions read rejected ({(int)status})")
        {
            Response = new HttpResponseMessageWrapper(new System.Net.Http.HttpResponseMessage(status), string.Empty),
        };

    private sealed class Harness
    {
        public IKubernetes Client { get; }

        public (string Group, string Version, string Namespace, string Plural)? LastCall { get; private set; }

        private Harness(object? body, Exception? fault)
        {
            var custom = Substitute.For<ICustomObjectsOperations>();
            custom.ListNamespacedCustomObjectWithHttpMessagesAsync(
                    Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
                    Arg.Any<bool?>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
                    Arg.Any<int?>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int?>(),
                    Arg.Any<bool?>(), Arg.Any<bool?>(),
                    Arg.Any<IReadOnlyDictionary<string, IReadOnlyList<string>>>(), Arg.Any<CancellationToken>())
                .Returns(call =>
                {
                    LastCall = ((string)call[0], (string)call[1], (string)call[2], (string)call[3]);
                    if (fault is not null)
                    {
                        throw fault;
                    }

                    return Task.FromResult(new HttpOperationResponse<object>
                    {
                        Body = body!,
                        Response = new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.OK),
                    });
                });

            Client = Substitute.For<IKubernetes>();
            Client.CustomObjects.Returns(custom);
        }

        public static Harness Returning(object body) => new(body, fault: null);

        public static Harness Throwing(Exception fault) => new(body: null, fault);
    }
}
