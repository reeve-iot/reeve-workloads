using System;
using FluentAssertions;
using Reeve.Workloads.Abstractions.Domain.Entities;
using Reeve.Workloads.Abstractions.Providers;
using Reeve.Workloads.Domain;
using Reeve.Workloads.Providers.Manual;
using Xunit;

namespace Reeve.Workloads.Providers.Manual.Tests;

public sealed class ManualResourceNamerTests
{
    private static readonly WorkloadNamingSubject Subject = new()
    {
        GroupId = "zwave",
        WorkloadId = "zwave-js",
        Inst8 = "AbCdEf12",
    };

    private readonly ManualResourceNamer namer = new();

    [Fact]
    public void A_volume_takes_the_docker_scheme_name_a_compose_deployment_creates()
    {
        namer.VolumeName(Subject, "state").Should().Be("reeve-zwave-state-abcdef12");
        namer.VolumeScheme.Should().Be(RetainedVolumeSchemeId.Docker);
    }

    [Fact]
    public void Objects_secrets_and_labels_keep_the_default_names()
    {
        var instanceId = Guid.Parse("22222222-3333-4444-5555-666666666666");
        var labels = new WorkloadLabelSubject { InstanceId = instanceId, InstallationId = "inst", SpecHash = "hash" };

        namer.Provider.Should().Be(ManualProvider.Id);
        namer.ObjectName(Subject).Should().Be(DefaultWorkloadResourceNamer.Instance.ObjectName(Subject));
        namer.SecretName(Subject).Should().Be(DefaultWorkloadResourceNamer.Instance.SecretName(Subject));
        namer.Labels(labels).Should().Equal(DefaultWorkloadResourceNamer.Instance.Labels(labels));
        namer.InstanceSelectorLabels(instanceId)
            .Should().Equal(DefaultWorkloadResourceNamer.Instance.InstanceSelectorLabels(instanceId));
    }
}
