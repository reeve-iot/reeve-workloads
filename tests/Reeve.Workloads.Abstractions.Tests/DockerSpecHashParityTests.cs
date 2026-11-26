using FluentAssertions;
using Reeve.Workloads.Abstractions.Contracts;
using Reeve.Workloads.Abstractions.Contracts.Deployment;
using Reeve.Workloads.Abstractions.Domain;
using Reeve.Workloads.Abstractions.Domain.DesiredSpec;
using Reeve.Workloads.Abstractions.Domain.Entities;
using Reeve.Workloads.Abstractions.Providers;
using Reeve.Workloads.Domain;
using Reeve.Workloads.Domain.DesiredSpec;
using Reeve.Workloads.Tests.Shared;

namespace Reeve.Workloads.Abstractions.Tests;

public sealed class DockerSpecHashParityTests
{
    private static readonly Guid InstanceId = Guid.Parse("11111111-2222-3333-4444-555555555555");
    private static readonly Guid GroupInstanceId = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");
    private const string Digest = "sha256:" + "1111111111111111111111111111111111111111111111111111111111111111";

    private static WorkloadDesiredSpec BaseSpec(WorkloadProviderId provider)
        => new()
        {
            GroupInstanceId = GroupInstanceId,
            GroupId = "com.reeve.x",
            WorkloadId = "w",
            InstanceId = InstanceId,
            Inst8 = WorkloadInstance.ComputeInst8(InstanceId),
            Provider = provider,
            ResourceName = "reeve-wl-x-w-inst",
            SecretName = "reeve-wl-x-w-inst",
            Labels = new Dictionary<string, string>(),
            Image = $"ghcr.io/x@{Digest}",
            Platforms = new[] { "linux/amd64", "linux/arm64" },
            Kind = WorkloadKind.SingleNode,
            Privileges = new ResolvedPrivileges
            {
                Capabilities = new[] { "NET_ADMIN", "NET_RAW" },
                DevicePaths = new[] { "/dev/ttyUSB0", "/dev/ttyUSB1" },
            },
            Volumes = new[]
            {
                new ResolvedVolume { Id = "a", MountPath = "/a", Size = "1Gi", Criticality = VolumeCriticality.Cache, ResourceName = "pvc-a" },
                new ResolvedVolume { Id = "b", MountPath = "/b", Size = "2Gi", Criticality = VolumeCriticality.Critical, ResourceName = "pvc-b" },
            },
            Health = new WorkloadHealth { Mode = WorkloadHealthMode.Container },
            Env = new[]
            {
                new ResolvedEnvVar { Name = "A", Kind = ResolvedEnvKind.Literal, Value = "1" },
                new ResolvedEnvVar { Name = "B", Kind = ResolvedEnvKind.Literal, Value = "2" },
            },
            SpecHash = string.Empty,
        };

    [Fact]
    public void Provider_is_included_in_the_hash()
    {
        var k8s = SpecHasher.Compute(BaseSpec(StandInProviders.KubernetesId));
        var docker = SpecHasher.Compute(BaseSpec(StandInProviders.DockerId));
        docker.Should().NotBe(k8s, "provider identity is a hashed input by design (SpecHasher.cs:69)");
    }

    [Fact]
    public void Two_kubernetes_specs_hash_equal_control()
    {
        SpecHasher.Compute(BaseSpec(StandInProviders.KubernetesId))
            .Should().Be(SpecHasher.Compute(BaseSpec(StandInProviders.KubernetesId)));
    }

    [Fact]
    public void Two_docker_specs_hash_equal_control()
    {
        SpecHasher.Compute(BaseSpec(StandInProviders.DockerId))
            .Should().Be(SpecHasher.Compute(BaseSpec(StandInProviders.DockerId)));
    }

    [Fact]
    public void Provider_is_the_only_differing_input()
    {
        var k8sA = SpecHasher.Compute(BaseSpec(StandInProviders.KubernetesId));
        var k8sB = SpecHasher.Compute(BaseSpec(StandInProviders.KubernetesId));
        var docker = SpecHasher.Compute(BaseSpec(StandInProviders.DockerId));

        k8sB.Should().Be(k8sA, "nothing else changed");
        docker.Should().NotBe(k8sA, "the ONLY changed input was Provider");
    }

    private const string DockerGoldenHash = "a80523faad3064a00dffec4c984b88ac3c7207b465344f546afbf9c0cb86501c";

    [Fact]
    public void Docker_spec_hash_is_stable()
    {
        SpecHasher.Compute(BaseSpec(StandInProviders.DockerId)).Should().Be(DockerGoldenHash);
    }

    private const string DockerWithTokenEnvHash = "9bea26fe08417073b26918b72c0d706cee4d1ef4109f1f8404f708e1402eb2c7";
    private const string DockerWithoutTokenEnvHash = "e564589bb42241861314896aff47bf4fa18d609cb07f048dbdf95538d3e78172";

    [Fact]
    public void A_docker_spec_declaring_the_token_env_keeps_its_hash()
    {
        RenderDocker(declaresTokenEnv: true).SpecHash.Should().Be(DockerWithTokenEnvHash);
    }

    [Fact]
    public void A_docker_spec_without_the_token_env_keeps_its_hash()
    {
        RenderDocker(declaresTokenEnv: false).SpecHash.Should().Be(DockerWithoutTokenEnvHash);
    }

    private static WorkloadDesiredSpec RenderDocker(bool declaresTokenEnv)
    {
        var env = new List<WorkloadEnv> { new() { Name = "A", From = "literal:1" } };
        if (declaresTokenEnv)
        {
            env.Add(new WorkloadEnv { Name = "REEVE_WORKLOAD_TOKEN", From = "bootstrap:workloadToken" });
        }

        var workload = new WorkloadManifest
        {
            Id = "w",
            DisplayName = "W",
            Image = new WorkloadImage { Ref = "ghcr.io/x", Digest = Digest, Platforms = new[] { "linux/amd64" } },
            Kind = WorkloadKind.Service,
            Health = new WorkloadHealth { Mode = WorkloadHealthMode.Container },
            Env = env,
        };
        var secretName = DefaultWorkloadResourceNamer.Instance.SecretName(new WorkloadNamingSubject
        {
            GroupId = "com.reeve.x",
            WorkloadId = workload.Id,
            Inst8 = WorkloadInstance.ComputeInst8(InstanceId),
        });

        return WorkloadDesiredSpecFactory.Compute(
            workload,
            new WorkloadOrchestratorSettings(),
            new Dictionary<string, string?>(StringComparer.Ordinal),
            new WorkloadBootstrapContext
            {
                InstanceId = InstanceId,
                SecretName = secretName,
                Values = new Dictionary<string, BootstrapValue>(StringComparer.Ordinal)
                {
                    ["workloadToken"] = BootstrapValue.HostCredential("workload-token"),
                    ["specHash"] = BootstrapValue.SpecHash,
                },
            },
            StandInProviders.Docker,
            DefaultWorkloadResourceNamer.Instance,
            GroupInstanceId,
            "com.reeve.x",
            InstanceId,
            "install-1");
    }

    [Fact]
    public void A_fixed_spec_hashes_differently_across_providers()
    {
        var fixedInputsExceptProvider_k8s = SpecHasher.Compute(BaseSpec(StandInProviders.KubernetesId));
        var fixedInputsExceptProvider_docker = SpecHasher.Compute(BaseSpec(StandInProviders.DockerId));
        fixedInputsExceptProvider_docker.Should().NotBe(
            fixedInputsExceptProvider_k8s,
            "there is NO cross-provider byte-identity golden — provider is a hashed input, so per-provider " +
            "hashes are distinct and HELLO-echo anchoring holds per applied (per-provider) spec");
    }
}
