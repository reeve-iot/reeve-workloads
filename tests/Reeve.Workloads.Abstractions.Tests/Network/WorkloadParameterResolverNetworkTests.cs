using FluentAssertions;
using Reeve.Workloads.Abstractions.Contracts;
using Reeve.Workloads.Abstractions.Contracts.Deployment;
using Reeve.Workloads.Abstractions.Domain.DesiredSpec;
using Reeve.Workloads.Abstractions.Domain.Network;

using Reeve.Workloads.Abstractions.Providers;
using Reeve.Workloads.Domain.DesiredSpec;
using Reeve.Workloads.Domain.Network;
using Reeve.Workloads.Tests.Shared;

namespace Reeve.Workloads.Abstractions.Tests.Network;

public sealed class WorkloadParameterResolverNetworkTests
{
    private static WorkloadManifest Workload(
        IReadOnlyList<WorkloadParameter>? parameters = null,
        WorkloadPrivileges? privileges = null,
        WorkloadNetwork? network = null,
        IReadOnlyList<WorkloadVolume>? volumes = null)
        => new()
        {
            Id = "w",
            DisplayName = "W",
            Image = new WorkloadImage { Ref = "reg/img", Digest = "sha256:" + new string('a', 64) },
            Kind = WorkloadKind.SingleNode,
            Privileges = privileges,
            Network = network,
            Volumes = volumes ?? Array.Empty<WorkloadVolume>(),
            Parameters = parameters ?? Array.Empty<WorkloadParameter>(),
            Health = new WorkloadHealth { Mode = WorkloadHealthMode.Container },
        };

    private static Dictionary<string, string?> Values(params (string k, string? v)[] pairs)
        => pairs.ToDictionary(p => p.k, p => p.v, StringComparer.Ordinal);

    private static WorkloadManifest HostNetworkWorkload()
        => Workload(network: new WorkloadNetwork { HostNetwork = true });

    private static WorkloadManifest SlotDeclaringWorkload()
        => Workload(
            parameters: new[]
            {
                new WorkloadParameter { Name = "netAttach", Type = WorkloadParameterType.NetworkAttachment },
            },
            network: new WorkloadNetwork
            {
                HostNetwork = false,
                Attachments = new[] { new WorkloadNetworkAttachmentSlot { Parameter = "netAttach" } },
            });

    [Theory]
    [InlineData("kubernetes", true, "hostNetwork")]
    [InlineData("kubernetes", false, "cluster")]
    [InlineData("docker", true, "hostNetwork")]
    [InlineData("docker", false, "cluster")]
    [InlineData("manual", true, "hostNetwork")]
    [InlineData("manual", false, "cluster")]
    public void The_resolved_map_carries_both_reserved_network_keys_on_every_provider(
        string providerId,
        bool hostNetworkLimb,
        string expectedMode)
    {
        var provider = StandInProviders.For(WorkloadProviderId.Parse(providerId));
        var wl = hostNetworkLimb ? HostNetworkWorkload() : SlotDeclaringWorkload();

        var resolved = WorkloadParameterResolver.Resolve(wl, Values(), provider);

        resolved.Should().ContainKey(WorkloadNetworkAttachmentGrammar.NetworkModeParameter);
        resolved[WorkloadNetworkAttachmentGrammar.NetworkModeParameter].Should().Be(expectedMode);

        resolved.Should().ContainKey(WorkloadNetworkAttachmentGrammar.NetworkAttachmentsParameter);
        resolved[WorkloadNetworkAttachmentGrammar.NetworkAttachmentsParameter].Should().BeNullOrEmpty();
    }

    [Theory]
    [InlineData("kubernetes")]
    [InlineData("docker")]
    [InlineData("manual")]
    public void A_plain_workload_resolves_neither_reserved_network_key(string providerId)
    {
        var provider = StandInProviders.For(WorkloadProviderId.Parse(providerId));
        var noNetwork = WorkloadParameterResolver.Resolve(Workload(), Values(), provider);

        noNetwork.Should().NotContainKey(WorkloadNetworkAttachmentGrammar.NetworkModeParameter);
        noNetwork.Should().NotContainKey(WorkloadNetworkAttachmentGrammar.NetworkAttachmentsParameter);

        var withPorts = Workload(network: new WorkloadNetwork
        {
            HostNetwork = false,
            Ports = new[] { new WorkloadPort { Name = "http", Port = 8080, Protocol = "TCP" } },
        });
        var resolved = WorkloadParameterResolver.Resolve(withPorts, Values(), provider);

        resolved.Should().NotContainKey(WorkloadNetworkAttachmentGrammar.NetworkModeParameter);
        resolved.Should().NotContainKey(WorkloadNetworkAttachmentGrammar.NetworkAttachmentsParameter);

        resolved.Should().ContainKey("resources.requests.cpu");
        resolved.Should().ContainKey("resources.requests.memory");
        resolved.Should().ContainKey("resources.limits.cpu");
        resolved.Should().ContainKey("resources.limits.memory");
        if (provider.PublishesHostPorts)
            resolved.Should().ContainKey("port.http.hostPort");
        else
            resolved.Should().NotContainKey("port.http.hostPort");
    }

    [Theory]
    [InlineData("kubernetes")]
    [InlineData("docker")]
    [InlineData("manual")]
    public void An_operator_value_wins_over_the_mapped_author_default_for_network_mode(string providerId)
    {
        var provider = StandInProviders.For(WorkloadProviderId.Parse(providerId));
        var resolved = WorkloadParameterResolver.Resolve(
            HostNetworkWorkload(),
            Values(
                (WorkloadNetworkAttachmentGrammar.NetworkModeParameter, "operatorNetwork"),
                (WorkloadNetworkAttachmentGrammar.NetworkAttachmentsParameter, "iot-macvlan")),
            provider);

        resolved[WorkloadNetworkAttachmentGrammar.NetworkModeParameter].Should().Be("operatorNetwork");
        resolved[WorkloadNetworkAttachmentGrammar.NetworkAttachmentsParameter].Should().Be("iot-macvlan");
    }

    [Theory]
    [InlineData("kubernetes")]
    [InlineData("docker")]
    [InlineData("manual")]
    public void An_author_re_declared_network_mode_keeps_its_overridden_default_at_site_2(string providerId)
    {
        var provider = StandInProviders.For(WorkloadProviderId.Parse(providerId));
        var wl = Workload(
            parameters: new[]
            {
                new WorkloadParameter
                {
                    Name = WorkloadNetworkAttachmentGrammar.NetworkModeParameter,
                    Type = WorkloadParameterType.Enum,
                    Default = "cluster",
                },
            },
            network: new WorkloadNetwork { HostNetwork = true });

        var resolved = WorkloadParameterResolver.Resolve(wl, Values(), provider);

        resolved[WorkloadNetworkAttachmentGrammar.NetworkModeParameter].Should().Be("cluster");

        resolved.Should().ContainKey(WorkloadNetworkAttachmentGrammar.NetworkAttachmentsParameter);
    }
}
