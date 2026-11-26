using FluentAssertions;
using Reeve.Workloads.Abstractions.Contracts;
using Reeve.Workloads.Abstractions.Contracts.Deployment;
using Reeve.Workloads.Abstractions.Domain.DesiredSpec;
using Reeve.Workloads.Core;
using Reeve.Workloads.Domain.DesiredSpec;
using Reeve.Workloads.Providers.Kubernetes;
using Reeve.Workloads.Providers.Docker;

namespace Reeve.Workloads.Core.Tests;

public sealed class WorkloadSecretHandlingTests
{
    [Fact]
    public void SecretMaterial_StillTravelsWithinTheProcess()
    {
        var spec = SpecWith(
            new ResolvedEnvVar { Name = "MQTT_PASSWORD", Kind = ResolvedEnvKind.SecretRef },
            material: new Dictionary<string, string> { ["MQTT_PASSWORD"] = "hunter2" });

        spec.SecretMaterial.Should().ContainKey("MQTT_PASSWORD").WhoseValue.Should().Be("hunter2");
    }

    [Fact]
    public void SecretMaterial_IsNotPartOfTheSpecHash()
    {
        var withMaterial = SpecWith(
            new ResolvedEnvVar { Name = "MQTT_PASSWORD", Kind = ResolvedEnvKind.SecretRef },
            material: new Dictionary<string, string> { ["MQTT_PASSWORD"] = "hunter2" });
        var withOther = withMaterial with
        {
            SecretMaterial = new Dictionary<string, string> { ["MQTT_PASSWORD"] = "different" },
        };

        SpecHasher.Compute(withOther).Should().Be(SpecHasher.Compute(withMaterial));
    }

    private static WorkloadDesiredSpec SpecWith(
        ResolvedEnvVar env, IReadOnlyDictionary<string, string> material)
        => new()
        {
            GroupInstanceId = Guid.NewGuid(),
            GroupId = "mod",
            WorkloadId = "w",
            InstanceId = Guid.NewGuid(),
            Inst8 = "abcd1234",
            Provider = DockerProvider.Id,
            ResourceName = "res",
            SecretName = "res",
            Labels = new Dictionary<string, string>(),
            Image = "registry.example/app@sha256:" + new string('a', 64),
            Kind = WorkloadKind.SingleNode,
            Health = new WorkloadHealth { Mode = WorkloadHealthMode.Container },
            SpecHash = "unset",
            Env = [env],
            SecretMaterial = material,
        };
}
