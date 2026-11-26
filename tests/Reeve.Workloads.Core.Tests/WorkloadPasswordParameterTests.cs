using Reeve.Workloads.Domain;
using FluentAssertions;
using Reeve.Workloads.Abstractions.Contracts;
using Reeve.Workloads.Abstractions.Contracts.Deployment;
using Reeve.Workloads.Abstractions.Domain.DesiredSpec;
using Reeve.Workloads.DeploymentFixtures;
using Reeve.Workloads.Domain.DesiredSpec;
using Reeve.Workloads.Providers.Kubernetes;

using Reeve.Workloads.Abstractions.Providers;
using Reeve.Workloads.Providers.Docker;

namespace Reeve.Workloads.Core.Tests;

public sealed class WorkloadPasswordParameterTests
{
    [Fact]
    public void APasswordParameter_BecomesASecretReferenceCarryingItsMaterialOutOfBand()
    {
        var spec = Compute(WorkloadParameterType.Password, "hunter2");

        var env = spec.Env.Should().ContainSingle(e => e.Name == "DB_PASSWORD").Subject;
        env.Kind.Should().Be(ResolvedEnvKind.SecretRef);
        env.Value.Should().BeNull();
        spec.SecretMaterial.Should().ContainKey("DB_PASSWORD").WhoseValue.Should().Be("hunter2");
    }

    [Fact]
    public void ANonPasswordParameter_IsStillAPlainLiteral()
    {
        var spec = Compute(WorkloadParameterType.Text, "eu-west-1");

        var env = spec.Env.Should().ContainSingle(e => e.Name == "DB_PASSWORD").Subject;
        env.Kind.Should().Be(ResolvedEnvKind.Literal);
        env.Value.Should().Be("eu-west-1");
        spec.SecretMaterial.Should().BeEmpty();
    }

    [Fact]
    public void APasswordValue_NeverEntersTheSpecHash()
    {
        SpecHasher.Compute(Compute(WorkloadParameterType.Password, "hunter2"))
            .Should().Be(SpecHasher.Compute(Compute(WorkloadParameterType.Password, "something-else")));
    }

    [Fact]
    public void ANonPasswordValue_StillMovesTheSpecHash()
    {
        SpecHasher.Compute(Compute(WorkloadParameterType.Text, "eu-west-1"))
            .Should().NotBe(SpecHasher.Compute(Compute(WorkloadParameterType.Text, "us-east-1")));
    }

    private static WorkloadDesiredSpec Compute(WorkloadParameterType type, string value)
    {
        var workload = DeploymentManifestBuilder.Workload(
            parameters: [DeploymentManifestBuilder.Param("dbPassword", required: true, type: type)]) with
        {
            Env = [new WorkloadEnv { Name = "DB_PASSWORD", From = "parameter:dbPassword" }],
        };

        return WorkloadDesiredSpecFactory.Compute(
            workload,
            new WorkloadOrchestratorSettings(),
            new Dictionary<string, string?> { ["dbPassword"] = value },
            new WorkloadBootstrapContext { InstanceId = InstanceId, SecretName = "reeve-wl-secret" },
            DockerProvider.Descriptor,
            DefaultWorkloadResourceNamer.Instance,
            GroupInstanceId,
            "plugin.type",
            InstanceId,
            "install-1");
    }

    private static readonly Guid InstanceId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid GroupInstanceId = Guid.Parse("22222222-2222-2222-2222-222222222222");
}
