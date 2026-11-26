using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using FluentAssertions;
using Reeve.Workloads.Abstractions.Providers;
using Reeve.Workloads.Providers.Docker;
using Xunit;

namespace Reeve.Workloads.Providers.Docker.Tests;

public sealed class DockerResourceNamerParityTests
{
    private static readonly JsonElement Golden = JsonDocument.Parse(
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", "resource-names.json"))).RootElement;

    public static TheoryData<int> Rows()
    {
        var data = new TheoryData<int>();
        for (var i = 0; i < Golden.GetProperty("rows").GetArrayLength(); i++)
        {
            data.Add(i);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Rows))]
    public void Container_and_volume_names_are_what_6_2_0_named_them(int row)
    {
        var golden = Golden.GetProperty("rows")[row];
        var subject = new WorkloadNamingSubject
        {
            GroupId = golden.GetProperty("M").GetString()!,
            WorkloadId = golden.GetProperty("W").GetString()!,
            Inst8 = golden.GetProperty("I8").GetString()!,
        };

        DockerResourceNamer.Instance.ObjectName(subject)
            .Should().Be(golden.GetProperty("Container").GetString(), "golden DockerContainerName");
        DockerResourceNamer.Instance.VolumeName(subject, golden.GetProperty("V").GetString()!)
            .Should().Be(golden.GetProperty("DockerVolume").GetString(), "golden DockerVolumeName");
    }

    [Fact]
    public void The_instance_selector_is_what_6_2_0_selected_on()
        => DockerResourceNamer.Instance
            .InstanceSelectorLabels(Guid.Parse("22222222-3333-4444-5555-666666666666"))
            .Should().Equal(Golden.GetProperty("dockerSelector").Deserialize<Dictionary<string, string>>());
}
