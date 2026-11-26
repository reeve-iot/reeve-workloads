using FluentAssertions;
using Reeve.Workloads.Abstractions.Domain.Entities;
using Xunit;

namespace Reeve.Workloads.Abstractions.Tests;

public sealed class WorkloadGroupTests
{
    private static readonly DateTimeOffset At = new(2026, 9, 17, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Later = At.AddHours(1);

    [Fact]
    public void A_new_row_records_the_trimmed_module_its_source_and_version()
    {
        var module = WorkloadGroup.Record("  zigbee2mqtt ", " repo-1 ", " 1.2.0 ", At);

        module.GroupId.Should().Be("zigbee2mqtt");
        module.SourceKey.Should().Be("repo-1");
        module.InstalledVersion.Should().Be("1.2.0");
        module.CreatedAtUtc.Should().Be(At);
        module.UpdatedAtUtc.Should().Be(At);
        module.Id.Should().NotBeEmpty();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void A_row_without_a_version_records_none(string? version)
    {
        WorkloadGroup.Record("zigbee2mqtt", "repo-1", version, At).InstalledVersion.Should().BeNull(
            "a bundled module or an adopted image has no version, and a blank one would be asked of the manifest source");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void A_row_needs_a_module_id(string? groupId)
        => FluentActions.Invoking(() => WorkloadGroup.Record(groupId!, "repo-1", "1.0.0", At))
            .Should().Throw<ArgumentException>();

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void A_row_needs_a_source_key(string? sourceKey)
        => FluentActions.Invoking(() => WorkloadGroup.Record("zigbee2mqtt", sourceKey!, "1.0.0", At))
            .Should().Throw<ArgumentException>();

    [Fact]
    public void Values_longer_than_a_column_are_refused()
    {
        var tooLong = new string('a', WorkloadGroup.MaxLength + 1);
        var longest = new string('a', WorkloadGroup.MaxLength);

        FluentActions.Invoking(() => WorkloadGroup.Record(tooLong, "repo-1", "1.0.0", At)).Should().Throw<ArgumentException>();
        FluentActions.Invoking(() => WorkloadGroup.Record("zigbee2mqtt", tooLong, "1.0.0", At)).Should().Throw<ArgumentException>();
        FluentActions.Invoking(() => WorkloadGroup.Record("zigbee2mqtt", "repo-1", tooLong, At)).Should().Throw<ArgumentException>();
        FluentActions.Invoking(() => WorkloadGroup.Record(longest, longest, longest, At)).Should().NotThrow();
    }

    [Fact]
    public void Moving_to_another_version_records_it()
    {
        var module = WorkloadGroup.Record("zigbee2mqtt", "repo-1", "1.0.0", At);

        module.MoveTo(" 1.1.0 ", Later).Should().BeTrue();

        module.InstalledVersion.Should().Be("1.1.0");
        module.UpdatedAtUtc.Should().Be(Later);
    }

    [Fact]
    public void Moving_to_the_version_already_recorded_changes_nothing()
    {
        var module = WorkloadGroup.Record("zigbee2mqtt", "repo-1", "1.0.0", At);

        module.MoveTo("1.0.0", Later).Should().BeFalse(
            "a re-tick or a failover repeats the commit, and a repeat must not look like a write");

        module.UpdatedAtUtc.Should().Be(At);
    }

    [Fact]
    public void Moving_a_row_that_recorded_no_version_records_one()
    {
        var module = WorkloadGroup.Record("zigbee2mqtt", "repo-1", null, At);

        module.MoveTo("2.0.0", Later).Should().BeTrue();

        module.InstalledVersion.Should().Be("2.0.0");
    }

    [Theory]
    [InlineData(null)]
    [InlineData(" ")]
    public void Moving_needs_a_version(string? version)
        => FluentActions.Invoking(() => WorkloadGroup.Record("zigbee2mqtt", "repo-1", "1.0.0", At).MoveTo(version!, Later))
            .Should().Throw<ArgumentException>();

    [Fact]
    public void Reinstalling_records_the_new_source_and_version()
    {
        var module = WorkloadGroup.Record("zigbee2mqtt", "repo-1", "1.0.0", At);

        module.Reinstall(" repo-2 ", " 2.0.0 ", Later);

        module.SourceKey.Should().Be("repo-2");
        module.InstalledVersion.Should().Be("2.0.0");
        module.CreatedAtUtc.Should().Be(At);
        module.UpdatedAtUtc.Should().Be(Later);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(" ")]
    public void Reinstalling_without_a_version_records_none(string? version)
    {
        var module = WorkloadGroup.Record("zigbee2mqtt", "repo-1", "1.0.0", At);

        module.Reinstall("repo-1", version, Later);

        module.InstalledVersion.Should().BeNull();
    }

    [Theory]
    [InlineData(null)]
    [InlineData(" ")]
    public void Reinstalling_needs_a_source(string? sourceKey)
        => FluentActions.Invoking(() => WorkloadGroup.Record("zigbee2mqtt", "repo-1", "1.0.0", At).Reinstall(sourceKey!, "2.0.0", Later))
            .Should().Throw<ArgumentException>();

    [Theory]
    [InlineData("zigbee2mqtt", true)]
    [InlineData(" ZigBee2MQTT ", true)]
    [InlineData("zigbee", false)]
    public void A_module_id_is_matched_trimmed_and_without_case(string candidate, bool expected)
        => WorkloadGroup.Record("zigbee2mqtt", "repo-1", "1.0.0", At).IsGroup(candidate).Should().Be(expected);
}
