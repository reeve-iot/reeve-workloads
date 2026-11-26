using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using Reeve.Workloads.Abstractions.Domain.DesiredSpec;
using Xunit;
using Reeve.Workloads.Domain.DesiredSpec;

namespace Reeve.Workloads.Abstractions.Tests;

public sealed class WorkloadNodeSelectorGrammarTests
{
    private static IReadOnlyList<string> Problems(params (string Key, string Value)[] pairs) =>
        WorkloadNodeSelectorGrammar.DescribeInvalidLabels(
            pairs.ToDictionary(p => p.Key, p => p.Value, System.StringComparer.Ordinal));

    [Fact]
    public void A_quoted_value_is_refused_and_the_reason_names_the_offending_entry()
    {
        var problems = Problems(("intel.feature.node.kubernetes.io/gpu", "'true'"));

        problems.Should().ContainSingle();
        problems[0].Should().Contain("intel.feature.node.kubernetes.io/gpu",
            "the operator has to know WHICH pair to fix when several are entered at once");
        problems[0].Should().Contain("'true'", "quoting the value is what makes the mistake obvious");
    }

    [Theory]
    [InlineData("true")]
    [InlineData("A_Series")]
    [InlineData("worker02")]
    [InlineData("1.2.3")]
    [InlineData("a-b_c.d")]
    [InlineData("")]
    public void A_legal_label_value_passes(string value) =>
        Problems(("reeve.io/has-zwave-radio", value)).Should().BeEmpty();

    [Theory]
    [InlineData("'true'")]
    [InlineData("\"true\"")]
    [InlineData("true ")]
    [InlineData("-true")]
    [InlineData("true-")]
    [InlineData("tr ue")]
    [InlineData("gpu=true")]
    [InlineData("true\n")]
    public void An_illegal_label_value_is_refused(string value) =>
        Problems(("reeve.io/has-zwave-radio", value)).Should().ContainSingle();

    [Fact]
    public void A_value_over_63_characters_is_refused() =>
        Problems(("reeve.io/x", new string('a', 64))).Should().ContainSingle(
            "Kubernetes caps a label value at 63 characters, and the API server would reject the apply");

    [Theory]
    [InlineData("kubernetes.io/hostname")]
    [InlineData("intel.feature.node.kubernetes.io/gpu")]
    [InlineData("reeve.io/has-zwave-radio")]
    [InlineData("bare-name")]
    public void A_legal_label_key_passes(string key) =>
        Problems((key, "true")).Should().BeEmpty();

    [Theory]
    [InlineData("")]
    [InlineData("a/b/c")]
    [InlineData("/gpu")]
    [InlineData("intel.io/")]
    [InlineData("UPPER.PREFIX/gpu")]
    [InlineData("-leading/gpu")]
    [InlineData("intel io/gpu")]
    [InlineData("gpu\n")]
    [InlineData("reeve.io\n/gpu")]
    public void An_illegal_label_key_is_refused(string key) =>
        Problems((key, "true")).Should().ContainSingle();

    [Fact]
    public void Every_offending_entry_is_reported_not_just_the_first()
    {
        var problems = Problems(
            ("intel.feature.node.kubernetes.io/gpu", "'true'"),
            ("reeve.io/rack", "\"a\""),
            ("kubernetes.io/hostname", "worker02"));

        problems.Should().HaveCount(2);
    }

    [Fact]
    public void An_empty_selector_is_valid_because_it_means_run_anywhere() =>
        WorkloadNodeSelectorGrammar
            .DescribeInvalidLabels(new Dictionary<string, string>(System.StringComparer.Ordinal))
            .Should().BeEmpty();

    [Fact]
    public void What_the_grammar_parses_is_what_the_validator_judges()
    {
        var parsed = WorkloadNodeSelectorGrammar
            .Parse("intel.feature.node.kubernetes.io/gpu='true'")
            .ToDictionary(kv => kv.Key, kv => kv.Value, System.StringComparer.Ordinal);

        parsed.Should().ContainKey("intel.feature.node.kubernetes.io/gpu");
        WorkloadNodeSelectorGrammar.DescribeInvalidLabels(parsed).Should().ContainSingle(
            "the quotes survive the parse as part of the value, which is precisely what the API server refused");
    }
}
