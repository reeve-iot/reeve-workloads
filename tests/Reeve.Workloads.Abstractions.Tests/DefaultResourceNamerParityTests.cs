using System.Text.Json;
using FluentAssertions;
using Reeve.Workloads.Abstractions.Providers;
using Reeve.Workloads.Domain;

namespace Reeve.Workloads.Abstractions.Tests;

public sealed class DefaultResourceNamerParityTests
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
    public void Object_secret_and_volume_names_are_what_6_2_0_named_them(int row)
    {
        var golden = Golden.GetProperty("rows")[row];
        var subject = new WorkloadNamingSubject
        {
            GroupId = golden.GetProperty("M").GetString()!,
            WorkloadId = golden.GetProperty("W").GetString()!,
            Inst8 = golden.GetProperty("I8").GetString()!,
        };
        var namer = DefaultWorkloadResourceNamer.Instance;

        namer.ObjectName(subject).Should().Be(golden.GetProperty("Object").GetString(), "golden KubernetesObjectName");
        namer.SecretName(subject).Should().Be(golden.GetProperty("Secret").GetString(), "golden KubernetesSecretName");
        namer.VolumeName(subject, golden.GetProperty("V").GetString()!)
            .Should().Be(golden.GetProperty("Pvc").GetString(), "golden KubernetesPvcName");
    }

    [Theory]
    [InlineData(0, "install-1", "abc123", null)]
    [InlineData(1, "Install ID/With Spaces", "ffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff", "critical")]
    [InlineData(2, "", "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef", "Recoverable")]
    public void The_label_set_is_what_6_2_0_stamped(int row, string installation, string specHash, string? criticality)
    {
        var expected = Golden.GetProperty("labels")[row].Deserialize<Dictionary<string, string>>();

        DefaultWorkloadResourceNamer.Instance.Labels(new WorkloadLabelSubject
        {
            InstanceId = Guid.Parse("22222222-3333-4444-5555-666666666666"),
            InstallationId = installation,
            SpecHash = specHash,
            Criticality = criticality,
        }).Should().Equal(expected);
    }

    [Fact]
    public void The_instance_selector_is_what_6_2_0_selected_on()
        => DefaultWorkloadResourceNamer.Instance
            .InstanceSelectorLabels(Guid.Parse("22222222-3333-4444-5555-666666666666"))
            .Should().Equal(Golden.GetProperty("selector").Deserialize<Dictionary<string, string>>());
}
