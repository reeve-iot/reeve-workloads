using FluentAssertions;
using Reeve.Workloads.Abstractions.Contracts.Deployment;
using Reeve.Workloads.Abstractions.Domain.DesiredSpec;
using Reeve.Workloads.Core.Relocate;
using Xunit;
using Reeve.Workloads.Domain.DesiredSpec;

namespace Reeve.Workloads.Core.Tests;

public sealed class WorkloadNodeSelectorParametersTests
{
    private static WorkloadParameter Selector(string name)
        => new() { Name = name, Type = WorkloadParameterType.NodeSelector };

    private static WorkloadParameter Scalar(string name)
        => new() { Name = name, Type = WorkloadParameterType.Text };

    [Fact]
    public void ApplyNodeSelector_writes_a_single_hostname_pin_onto_the_single_selector_param()
    {
        var parameters = new[] { Selector("nodeSelector") };
        var current = new Dictionary<string, string?> { ["nodeSelector"] = "kubernetes.io/hostname=node-a" };

        var updated = WorkloadNodeSelectorParameters.ApplyNodeSelector(
            parameters, current, new Dictionary<string, string> { ["kubernetes.io/hostname"] = "node-b" });

        updated["nodeSelector"].Should().Be("kubernetes.io/hostname=node-b");
    }

    [Fact]
    public void ApplyNodeSelector_leaves_non_selector_params_untouched()
    {
        var parameters = new[] { Selector("nodeSelector"), Scalar("logLevel") };
        var current = new Dictionary<string, string?> { ["nodeSelector"] = "role=old", ["logLevel"] = "debug" };

        var updated = WorkloadNodeSelectorParameters.ApplyNodeSelector(
            parameters, current, new Dictionary<string, string> { ["role"] = "new" });

        updated["logLevel"].Should().Be("debug");
        updated.Should().NotContainKey("role", "the value is encoded onto the selector param, not a new key");
        updated["nodeSelector"].Should().Be("role=new");
    }

    [Fact]
    public void CanRepresent_is_true_for_a_single_entry_on_a_single_selector_param()
    {
        var parameters = new[] { Selector("nodeSelector") };

        WorkloadNodeSelectorParameters.CanRepresent(
            parameters, new Dictionary<string, string> { ["kubernetes.io/hostname"] = "node-b" })
            .Should().BeTrue();
    }

    [Fact]
    public void A_workload_declaring_no_selector_param_can_still_be_placed()
    {
        var parameters = new[] { Scalar("logLevel") };

        WorkloadNodeSelectorParameters.CanRepresent(
            parameters, new Dictionary<string, string> { ["kubernetes.io/hostname"] = "node-b" })
            .Should().BeTrue("placement is the operator's, carried by a reserved name that exists whether or not the author declared one");
    }

    [Fact]
    public void One_param_carries_a_placement_of_more_than_one_label()
    {
        var parameters = new[] { Selector("nodeSelector") };
        var requested = new Dictionary<string, string> { ["role"] = "edge", ["zone"] = "west" };

        WorkloadNodeSelectorParameters.CanRepresent(parameters, requested)
            .Should().BeTrue("the value holds a list, so a second label is no longer dropped");

        WorkloadNodeSelectorParameters.ApplyNodeSelector(parameters, new Dictionary<string, string?>(), requested)["nodeSelector"]
            .Should().Be("role=edge,zone=west");
    }

    [Fact]
    public void Every_label_survives_the_encode_even_with_one_param()
    {
        var parameters = new[] { Selector("nodeSelector") };
        var requested = new Dictionary<string, string>
        {
            ["intel.feature.node.kubernetes.io/gpu"] = "true",
            ["topology.kubernetes.io/zone"] = "rack-b",
        };

        var encoded = WorkloadNodeSelectorParameters.ApplyNodeSelector(
            parameters, new Dictionary<string, string?>(), requested)["nodeSelector"];

        WorkloadNodeSelectorGrammar.Parse(encoded).Should().BeEquivalentTo(requested);
    }

    [Fact]
    public void CanRepresent_is_true_when_params_cover_every_entry()
    {
        var parameters = new[] { Selector("a"), Selector("b") };

        WorkloadNodeSelectorParameters.CanRepresent(
            parameters,
            new Dictionary<string, string> { ["role"] = "edge", ["zone"] = "west" })
            .Should().BeTrue();
    }

    [Fact]
    public void CanRepresent_is_true_for_an_empty_selector_even_with_no_params()
    {
        var parameters = new[] { Scalar("logLevel") };

        WorkloadNodeSelectorParameters.CanRepresent(parameters, new Dictionary<string, string>())
            .Should().BeTrue();
    }

    [Fact]
    public void CanRepresent_agrees_with_the_round_trip_of_ApplyNodeSelector()
    {
        var parameters = new[] { Selector("a"), Selector("b") };
        var requested = new Dictionary<string, string> { ["role"] = "edge", ["zone"] = "west" };

        WorkloadNodeSelectorParameters.CanRepresent(parameters, requested).Should().BeTrue();

        var updated = WorkloadNodeSelectorParameters.ApplyNodeSelector(
            parameters, new Dictionary<string, string?>(), requested);

        var decoded = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var name in new[] { "a", "b", "nodeSelector" })
        {
            if (!updated.TryGetValue(name, out var expr))
                continue;
            foreach (var (key, value) in WorkloadNodeSelectorGrammar.Parse(expr))
                decoded[key] = value;
        }

        decoded.Should().BeEquivalentTo(requested);
    }
}
