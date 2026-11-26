using Reeve.Workloads.Abstractions.Contracts;
using Reeve.Workloads.Abstractions.Contracts.Deployment;
using Reeve.Workloads.Abstractions.Domain.DesiredSpec;

namespace Reeve.Workloads.Tests.Shared;

internal static class CredentialSpecs
{
    public static readonly IReadOnlyList<ProvisionedCredential> Provisioned =
    [
        new("workload-token", "REEVE_WORKLOAD_TOKEN"),
        new("feed", "FEED_TOKEN"),
    ];

    public static WorkloadDesiredSpec Spec() => new()
    {
        GroupInstanceId = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee"),
        GroupId = "zwave",
        WorkloadId = "gateway",
        InstanceId = Guid.Parse("12345678-0000-0000-0000-000000000000"),
        Inst8 = "12345678",
        Provider = WorkloadProviderId.Parse("kubernetes"),
        ResourceName = "reeve-gateway",
        SecretName = "reeve-gateway-secret",
        Labels = new Dictionary<string, string>(),
        Image = "ghcr.io/reeve/gateway@sha256:" + new string('a', 64),
        Kind = WorkloadKind.Service,
        Health = new WorkloadHealth { Mode = WorkloadHealthMode.Container },
        SpecHash = "0123abcd",
        Env =
        [
            new ResolvedEnvVar
            {
                Name = "REEVE_WORKLOAD_TOKEN",
                Kind = ResolvedEnvKind.SecretRef,
                SecretRef = "reeve-gateway-secret",
                CredentialId = "workload-token",
            },
            new ResolvedEnvVar { Name = "LOG_LEVEL", Kind = ResolvedEnvKind.Literal, Value = "info" },
        ],
        ProvisionedCredentials = Provisioned,
    };

    public static Dictionary<string, WorkloadDesiredSpec> Map() => new(StringComparer.Ordinal) { ["gateway"] = Spec() };
}
