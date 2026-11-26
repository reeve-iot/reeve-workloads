using System;
using FluentAssertions;
using Reeve.Workloads.Abstractions.Contracts;
using Reeve.Workloads.Abstractions.Contracts.Deployment;
using Reeve.Workloads.Abstractions.Domain;
using Reeve.Workloads.Abstractions.Domain.DesiredSpec;
using Reeve.Workloads.Abstractions.Providers;
using Reeve.Workloads.Providers.Docker;
using Xunit;

namespace Reeve.Workloads.Providers.Docker.Tests;

public sealed class DockerResourceNamerLabelTests
{
    [Fact]
    public void A_container_carries_the_installation_the_docker_namer_labelled_its_spec_with()
    {
        var instanceId = Guid.Parse("11111111-2222-3333-4444-555555555555");
        var labels = DockerResourceNamer.Instance.Labels(new WorkloadLabelSubject
        {
            InstanceId = instanceId,
            InstallationId = "install-1",
            SpecHash = "deadbeef",
        });

        var built = DockerProviderConstants.BuildResourceLabels(Spec(instanceId) with { Labels = labels });

        built.Should().ContainKey(DockerProviderConstants.InstallationLabelKey).WhoseValue.Should().Be("install-1");
        built.Should().ContainKey(DockerProviderConstants.InstanceLabelKey).WhoseValue.Should().Be(instanceId.ToString("D"));
    }

    private static WorkloadDesiredSpec Spec(Guid instanceId) => new()
    {
        GroupInstanceId = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee"),
        GroupId = "com.reeve.matter",
        WorkloadId = "matter-server",
        InstanceId = instanceId,
        Inst8 = "11111111",
        Provider = DockerProvider.Id,
        ResourceName = "reeve-wl-com-reeve-matter-matter-server-11111111",
        SecretName = "reeve-wl-com-reeve-matter-matter-server-11111111",
        Labels = new System.Collections.Generic.Dictionary<string, string>(),
        Image = "ghcr.io/reeve/matter@sha256:0000000000000000000000000000000000000000000000000000000000000000",
        Kind = WorkloadKind.Service,
        Health = new WorkloadHealth { Mode = WorkloadHealthMode.Container },
        SpecHash = "deadbeef",
    };
}
