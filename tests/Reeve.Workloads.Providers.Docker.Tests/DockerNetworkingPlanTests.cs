using Reeve.Workloads.Abstractions.Providers;
using System;
using System.Collections.Generic;
using System.Linq;
using Docker.DotNet.Models;
using FluentAssertions;
using Reeve.Workloads.Abstractions.Contracts;
using Reeve.Workloads.Abstractions.Contracts.Deployment;
using Reeve.Workloads.Abstractions.Domain;
using Reeve.Workloads.Abstractions.Domain.DesiredSpec;
using Reeve.Workloads.Domain;
using Reeve.Workloads.Domain.DesiredSpec;
using Reeve.Workloads.Providers.Docker;
using Xunit;

namespace Reeve.Workloads.Providers.Docker.Tests;

public sealed class DockerNetworkingPlanTests
{
    private readonly IDockerNetworkManager _sut = new DockerNetworkManager();

    private const string SharedBridge = "reeve-workloads";

    [Theory]
    [InlineData(WorkloadNetworkMode.Cluster)]
    [InlineData(WorkloadNetworkMode.OperatorNetwork)]
    public void the_shared_bridge_stays_the_NetworkMode_for_every_mode_but_hostNetwork(WorkloadNetworkMode mode)
    {
        string[] attachments = mode == WorkloadNetworkMode.OperatorNetwork ? ["iot-macvlan"] : [];

        var plan = _sut.RenderNetworking(Spec(Network(mode, attachments)), SharedBridge);

        plan.HostConfig.NetworkMode.Should().Be(
            SharedBridge,
            "the reeve shared bridge is RETAINED whenever the mode is not hostNetwork — an attachment "
            + "is an ADDITIONAL endpoint, never a replacement, and losing the bridge loses the WS-gateway route");
    }

    [Fact]
    public void an_operatorNetwork_attachment_never_becomes_the_NetworkMode()
    {
        var plan = _sut.RenderNetworking(
            Spec(Network(WorkloadNetworkMode.OperatorNetwork, ["iot-macvlan", "lab-ipvlan"])),
            SharedBridge);

        plan.HostConfig.NetworkMode.Should().NotBe("iot-macvlan");
        plan.HostConfig.NetworkMode.Should().NotBe("lab-ipvlan");
        plan.HostConfig.NetworkMode.Should().Be(SharedBridge);
    }

    [Fact]
    public void hostNetwork_still_renders_NetworkMode_host_and_no_attachments()
    {
        var plan = _sut.RenderNetworking(
            Spec(new ResolvedNetwork
            {
                HostNetwork = true,
                Mode = WorkloadNetworkMode.HostNetwork,
                Ports = [Port("api", 8080, "tcp", hostPort: 8080)],
            }),
            SharedBridge);

        plan.HostConfig.NetworkMode.Should().Be("host");
        plan.Attachments.Should().BeEmpty();
        (plan.HostConfig.PortBindings ?? new Dictionary<string, IList<PortBinding>>())
            .Should().BeEmpty("host networking has no per-port publishing");
    }

    [Fact]
    public void attachments_are_returned_in_declared_order_and_are_never_sorted()
    {
        string[] declared = ["zeta-net", "mid-net", "alpha-net"];

        var plan = _sut.RenderNetworking(
            Spec(Network(WorkloadNetworkMode.OperatorNetwork, declared)),
            SharedBridge);

        plan.Attachments.Should().Equal(declared, "declared order is reproduced verbatim and NEVER sorted");
        plan.Attachments.Should().NotEqual(
            declared.OrderBy(n => n, StringComparer.Ordinal).ToArray(),
            "a sorted result is precisely the defect this case exists to catch");
    }

    [Fact]
    public void a_cluster_mode_workload_renders_no_attachments()
    {
        var plan = _sut.RenderNetworking(Spec(Network(WorkloadNetworkMode.Cluster, [])), SharedBridge);

        plan.Attachments.Should().BeEmpty();
    }

    [Fact]
    public void a_spec_with_no_network_block_at_all_renders_the_bridge_and_no_attachments()
    {
        var plan = _sut.RenderNetworking(Spec(network: null), SharedBridge);

        plan.HostConfig.NetworkMode.Should().Be(SharedBridge);
        plan.Attachments.Should().BeEmpty();
    }

    [Fact]
    public void the_renderer_refuses_an_incoherent_mode_attachment_pair()
    {
        var hostNetworkWithAttachments = Spec(new ResolvedNetwork
        {
            HostNetwork = true,
            Mode = WorkloadNetworkMode.HostNetwork,
            Attachments = ["iot-macvlan"],
        });

        var hostNetworkThrow = FluentActions
            .Invoking(() => _sut.RenderNetworking(hostNetworkWithAttachments, SharedBridge))
            .Should().Throw<InvalidOperationException>(
                "the renderer FAILS rather than emitting an incoherent plan — it is the second, "
                + "independent layer and never relies on WorkloadParameterResolver having normalized upstream");
        hostNetworkThrow.Which.Message.Should().Contain("iot-macvlan", "the refusal names what it dropped");

        var operatorNetworkWithoutAttachments = Spec(new ResolvedNetwork
        {
            HostNetwork = false,
            Mode = WorkloadNetworkMode.OperatorNetwork,
            Attachments = [],
        });

        FluentActions
            .Invoking(() => _sut.RenderNetworking(operatorNetworkWithoutAttachments, SharedBridge))
            .Should().Throw<InvalidOperationException>(
                "operatorNetwork with nothing to attach is incoherent — rendering it as an ordinary bridge-only "
                + "container is a SILENT posture change");
    }

    [Fact]
    public void the_refusal_keys_on_the_hostNetwork_boolean_too_so_attachments_can_never_be_dropped_silently()
    {
        var staleBooleanWithAttachments = Spec(new ResolvedNetwork
        {
            HostNetwork = true,
            Mode = WorkloadNetworkMode.Cluster,
            Attachments = ["iot-macvlan"],
        });

        FluentActions
            .Invoking(() => _sut.RenderNetworking(staleBooleanWithAttachments, SharedBridge))
            .Should().Throw<InvalidOperationException>(
                "host networking plus attachments FAILS however the pair arrived — an enum-only guard would render "
                + "NetworkMode=host here and discard the attachment in silence");
    }

    [Fact]
    public void the_refusal_is_the_renderers_own_layer_and_fires_on_a_spec_upstream_validation_would_never_produce()
    {
        var clusterWithNames = Spec(new ResolvedNetwork
        {
            HostNetwork = false,
            Mode = WorkloadNetworkMode.Cluster,
            Attachments = ["iot-macvlan"],
        });

        var plan = _sut.RenderNetworking(clusterWithNames, SharedBridge);

        plan.HostConfig.NetworkMode.Should().Be(SharedBridge);
        plan.Attachments.Should().Equal(["iot-macvlan"],
            "the renderer reproduces what it was given verbatim; normalizing cluster+names away is the job of "
            + "the resolver, and duplicating it here would hide a resolver regression instead of surfacing it");
    }

    [Fact]
    public void an_attachment_plus_hostPort_yields_the_same_PortBindings_as_without_the_attachment()
    {
        ResolvedPort[] ports =
        [
            Port("http", 8080, "tcp", hostPort: 18080),
            Port("mdns", 5353, "udp", hostPort: 5353),
            Port("cluster-only", 9090, "tcp", hostPort: null),
        ];

        var withoutAttachment = _sut.RenderNetworking(
            Spec(new ResolvedNetwork { HostNetwork = false, Mode = WorkloadNetworkMode.Cluster, Ports = ports }),
            SharedBridge);

        var withAttachment = _sut.RenderNetworking(
            Spec(new ResolvedNetwork
            {
                HostNetwork = false,
                Mode = WorkloadNetworkMode.OperatorNetwork,
                Attachments = ["iot-macvlan"],
                Ports = ports,
            }),
            SharedBridge);

        Flatten(withAttachment.HostConfig.PortBindings).Should().Equal(
            Flatten(withoutAttachment.HostConfig.PortBindings),
            "an attachment does not change port publishing — the bridge is still there to publish from");

        Flatten(withAttachment.HostConfig.PortBindings).Should().Equal(
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["8080/tcp"] = "18080",
                ["5353/udp"] = "5353",
            },
            "and the concrete expectation, so the case cannot pass by both sides being empty");
    }

    private static IReadOnlyDictionary<string, string> Flatten(IDictionary<string, IList<PortBinding>>? bindings)
        => bindings is null
            ? new Dictionary<string, string>(StringComparer.Ordinal)
            : bindings.ToDictionary(
                kv => kv.Key,
                kv => string.Join(",", kv.Value.Select(b => b.HostPort)),
                StringComparer.Ordinal);

    private static ResolvedNetwork Network(WorkloadNetworkMode mode, IReadOnlyList<string> attachments) =>
        new()
        {
            HostNetwork = mode == WorkloadNetworkMode.HostNetwork,
            Mode = mode,
            Attachments = attachments,
        };

    private static ResolvedPort Port(string name, int port, string protocol, int? hostPort) =>
        new() { Name = name, Port = port, Protocol = protocol, HostPort = hostPort };

    private static WorkloadDesiredSpec Spec(ResolvedNetwork? network)
    {
        const string specHash = "abc123def456";
        var instanceId = Guid.Parse("11111111-2222-3333-4444-555555555555");

        return new WorkloadDesiredSpec
        {
            GroupInstanceId = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee"),
            GroupId = "com.reeve.matter",
            WorkloadId = "matter",
            InstanceId = instanceId,
            Inst8 = "11111111",
            Provider = DockerProvider.Id,
            ResourceName = "reeve-wl-com-reeve-matter-matter-11111111",
            SecretName = "reeve-wl-com-reeve-matter-matter-11111111",
            Labels = DefaultWorkloadResourceNamer.Instance.Labels(new WorkloadLabelSubject
                     {
                         InstanceId = instanceId,
                         InstallationId = "install-1",
                         SpecHash = specHash,
                     }),
            Image = "ghcr.io/reeve/matter@sha256:1111111111111111111111111111111111111111111111111111111111111111",
            Kind = WorkloadKind.SingleNode,
            Network = network,
            Health = new WorkloadHealth { Mode = WorkloadHealthMode.Container },
            SpecHash = specHash,
        };
    }
}
