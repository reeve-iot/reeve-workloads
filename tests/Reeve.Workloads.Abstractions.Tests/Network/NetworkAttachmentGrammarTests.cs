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

public sealed class NetworkAttachmentGrammarTests
{
    private const string ModeKey = WorkloadNetworkAttachmentGrammar.NetworkModeParameter;
    private const string AttachKey = WorkloadNetworkAttachmentGrammar.NetworkAttachmentsParameter;

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

    private static WorkloadManifest ReservedSlotWorkload()
        => Workload(network: new WorkloadNetwork { HostNetwork = true });

    [Theory]
    [InlineData("iot-macvlan", false)]
    [InlineData("n1,n2,n3,n4,n5,n6,n7,n8", false)]
    [InlineData("n1,n2,n3,n4,n5,n6,n7,n8,n9", true)]
    [InlineData("a,,b", true)]
    [InlineData("a,a", true)]
    [InlineData("bad name", true)]
    public void An_OPERATOR_SUPPLIED_value_is_validated_not_only_the_author_default(
        string operatorValue,
        bool expectThrow)
    {
        var wl = ReservedSlotWorkload();
        var values = Values(
            (ModeKey, WorkloadNetworkAttachmentGrammar.OperatorNetworkMode),
            (AttachKey, operatorValue));

        var act = () => WorkloadParameterResolver.Resolve(wl, values, StandInProviders.Docker);

        if (expectThrow)
        {
            var ex = act.Should().Throw<WorkloadParameterResolutionException>().Which;
            ex.ParameterName.Should().Be(AttachKey);
            ex.Message.Should().Contain(operatorValue);
        }
        else
        {
            var resolved = act.Should().NotThrow().Which;
            resolved[AttachKey].Should().Be(operatorValue);
        }
    }

    [Theory]
    [InlineData("docker", "Iot_Macvlan.30-x", false)]
    [InlineData("kubernetes", "Iot_Macvlan.30-x", true)]
    [InlineData("manual", "Iot_Macvlan.30-x", true)]
    [InlineData("docker", "iot-macvlan.30-x", false)]
    [InlineData("kubernetes", "iot-macvlan.30-x", false)]
    [InlineData("manual", "iot-macvlan.30-x", false)]
    [InlineData("docker", "iot-macvlan", false)]
    [InlineData("kubernetes", "iot-macvlan", false)]
    [InlineData("manual", "iot-macvlan", false)]
    public void A_Docker_legal_name_that_is_not_an_RFC_1123_subdomain_is_rejected_on_Kubernetes_and_on_Manual(
        string providerId,
        string operatorValue,
        bool expectThrow)
    {
        var provider = StandInProviders.For(WorkloadProviderId.Parse(providerId));
        var values = Values(
            (ModeKey, WorkloadNetworkAttachmentGrammar.OperatorNetworkMode),
            (AttachKey, operatorValue));

        var act = () => WorkloadParameterResolver.Resolve(ReservedSlotWorkload(), values, provider);

        if (expectThrow)
        {
            var ex = act.Should().Throw<WorkloadParameterResolutionException>().Which;
            ex.ParameterName.Should().Be(AttachKey);
            ex.Message.Should().Contain(operatorValue);
        }
        else
        {
            var resolved = act.Should().NotThrow().Which;
            resolved[AttachKey].Should().Be(operatorValue);
        }
    }

    [Theory]
    [InlineData("kubernetes", "banana")]
    [InlineData("docker", "banana")]
    [InlineData("manual", "banana")]
    [InlineData("kubernetes", "hostnetwork")]
    [InlineData("docker", "HostNetwork")]
    [InlineData("manual", "OperatorNetwork")]
    public void A_network_mode_value_outside_the_enum_is_rejected(string providerId, string mode)
    {
        var provider = StandInProviders.For(WorkloadProviderId.Parse(providerId));
        var values = Values((ModeKey, mode), (AttachKey, "iot-macvlan"));

        var act = () => WorkloadParameterResolver.Resolve(ReservedSlotWorkload(), values, provider);

        var ex = act.Should().Throw<WorkloadParameterResolutionException>().Which;
        ex.ParameterName.Should().Be(ModeKey);
        ex.Message.Should().Contain(mode);
    }

    [Theory]
    [InlineData("kubernetes", "cluster")]
    [InlineData("kubernetes", "hostNetwork")]
    [InlineData("kubernetes", "operatorNetwork")]
    [InlineData("docker", "cluster")]
    [InlineData("docker", "hostNetwork")]
    [InlineData("docker", "operatorNetwork")]
    [InlineData("manual", "cluster")]
    [InlineData("manual", "hostNetwork")]
    [InlineData("manual", "operatorNetwork")]
    public void All_three_wire_spellings_of_network_mode_are_accepted(string providerId, string mode)
    {
        var provider = StandInProviders.For(WorkloadProviderId.Parse(providerId));
        var values = Values((ModeKey, mode), (AttachKey, "iot-macvlan"));

        var resolved = WorkloadParameterResolver.Resolve(ReservedSlotWorkload(), values, provider);

        resolved[ModeKey].Should().Be(mode);
    }

    [Theory]
    [InlineData("cluster", "iot-macvlan")]
    [InlineData("cluster", "a,b")]
    [InlineData("cluster", "a,,b")]
    [InlineData("hostNetwork", "iot-macvlan")]
    [InlineData("hostNetwork", "a,b")]
    [InlineData("hostNetwork", "a,,b")]
    public void A_stale_attachment_value_outside_operatorNetwork_is_normalized_to_empty_and_the_install_proceeds(
        string mode,
        string staleValue)
    {
        var values = Values((ModeKey, mode), (AttachKey, staleValue));

        var act = () => WorkloadParameterResolver.Resolve(
            ReservedSlotWorkload(), values, StandInProviders.Kubernetes);

        var resolved = act.Should().NotThrow().Which;
        string.IsNullOrEmpty(resolved[AttachKey]).Should().BeTrue();
        resolved[ModeKey].Should().Be(mode);
    }

    [Fact]
    public void Normalization_reports_the_dropped_attachment_names()
    {
        WorkloadNetworkAttachmentGrammar
            .NormalizeForMode(WorkloadNetworkMode.HostNetwork, "iot-macvlan,lab-vlan100", out var dropped)
            .Should().BeEmpty();
        dropped.Should().Equal("iot-macvlan", "lab-vlan100");

        WorkloadNetworkAttachmentGrammar
            .NormalizeForMode(WorkloadNetworkMode.Cluster, "a,,b", out var droppedFromMalformed)
            .Should().BeEmpty();
        droppedFromMalformed.Should().Equal("a", "b");

        WorkloadNetworkAttachmentGrammar
            .NormalizeForMode(WorkloadNetworkMode.OperatorNetwork, "iot-macvlan", out var droppedNone)
            .Should().Be("iot-macvlan");
        droppedNone.Should().BeEmpty();
    }

    [Theory]
    [InlineData("kubernetes", null)]
    [InlineData("kubernetes", "")]
    [InlineData("kubernetes", "   ")]
    [InlineData("docker", null)]
    [InlineData("docker", "")]
    [InlineData("docker", "   ")]
    [InlineData("manual", null)]
    [InlineData("manual", "")]
    [InlineData("manual", "   ")]
    public void operatorNetwork_with_an_empty_attachment_value_is_a_hard_failure(
        string providerId,
        string? attachmentValue)
    {
        var provider = StandInProviders.For(WorkloadProviderId.Parse(providerId));
        var values = attachmentValue is null
            ? Values((ModeKey, WorkloadNetworkAttachmentGrammar.OperatorNetworkMode))
            : Values((ModeKey, WorkloadNetworkAttachmentGrammar.OperatorNetworkMode), (AttachKey, attachmentValue));

        var act = () => WorkloadParameterResolver.Resolve(ReservedSlotWorkload(), values, provider);

        act.Should().Throw<WorkloadParameterResolutionException>()
            .Which.ParameterName.Should().Be(AttachKey);
    }
}
