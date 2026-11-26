using Reeve.Workloads.Abstractions.Providers;
using System;
using System.Collections.Generic;
using Reeve.Workloads.Abstractions.Contracts;
using Reeve.Workloads.Abstractions.Contracts.Deployment;
using Reeve.Workloads.Abstractions.Domain;
using Reeve.Workloads.Abstractions.Domain.DesiredSpec;
using Reeve.Workloads.Abstractions.Domain.Network;
using Reeve.Workloads.Abstractions.Rendering;
using Reeve.Workloads.Domain;
using Reeve.Workloads.Domain.Network;
using Reeve.Workloads.Providers.Manual;

namespace Reeve.Workloads.Core.Tests.Rendering;

internal static class ArtifactSpecFactory
{
    internal const string InstanceIdStr = "11111111-2222-3333-4444-555555555555";
    internal const string Inst8 = "11111111";
    internal const string Digest = "sha256:1111111111111111111111111111111111111111111111111111111111111111";
    internal const string SpecHashValue = "abc123def456";
    internal const string GatewayUrl = "wss://reeve.example.local/workloads/gw";

    internal const string TokenCredentialId = "workload-token";

    internal const string ResourceName = "reeve-wl-com-reeve-matter-matter-11111111";

    internal static WorkloadDesiredSpec Spec(
        WorkloadKind kind = WorkloadKind.SingleNode,
        ResolvedNetwork? network = null,
        ResolvedPrivileges? privileges = null,
        IReadOnlyList<ResolvedVolume>? volumes = null,
        IReadOnlyList<ResolvedConfigMapMount>? configMaps = null,
        IReadOnlyList<ResolvedEnvVar>? env = null,
        IReadOnlyDictionary<string, string>? nodeSelector = null,
        IReadOnlyList<string>? platforms = null,
        ResolvedResources? resources = null,
        WorkloadObservability? observability = null,
        UnsupportedPodPatchSpec? unsupportedPodPatch = null,
        string pluginTypeId = "com.reeve.matter",
        string workloadId = "matter",
        string resourceName = ResourceName,
        string secretName = ResourceName,
        IReadOnlyList<ProvisionedCredential>? provisioned = null)
    {
        var instanceId = Guid.Parse(InstanceIdStr);
        var labels = DefaultWorkloadResourceNamer.Instance.Labels(new WorkloadLabelSubject
                     {
                         InstanceId = instanceId,
                         InstallationId = "install-1",
                         SpecHash = SpecHashValue,
                     });

        return new WorkloadDesiredSpec
        {
            GroupInstanceId = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee"),
            GroupId = pluginTypeId,
            WorkloadId = workloadId,
            InstanceId = instanceId,
            Inst8 = Inst8,
            Provider = ManualProvider.Id,
            ResourceName = resourceName,
            SecretName = secretName,
            Labels = labels,
            Image = $"ghcr.io/reeve/matter@{Digest}",
            Platforms = platforms ?? new List<string>(),
            Kind = kind,
            NodeSelector = nodeSelector ?? new Dictionary<string, string>(),
            Resources = resources,
            Network = network,
            Privileges = privileges,
            Volumes = volumes ?? new List<ResolvedVolume>(),
            ConfigMapMounts = configMaps ?? new List<ResolvedConfigMapMount>(),
            Health = new WorkloadHealth { Mode = WorkloadHealthMode.Container },
            Observability = observability,
            UnsupportedPodPatch = unsupportedPodPatch,
            Env = env ?? DefaultEnv(),
            ProvisionedCredentials = provisioned ?? [new ProvisionedCredential(TokenCredentialId, "REEVE_WORKLOAD_TOKEN")],
            SpecHash = SpecHashValue,
        };
    }

    internal static ResolvedNetwork OperatorNetwork(params string[] names) => new()
    {
        HostNetwork = false,
        Mode = WorkloadNetworkMode.OperatorNetwork,
        Attachments = names,
    };

    internal static readonly string IsolationCopy =
        "The node cannot reach this workload on its attachment address (macvlan/ipvlan parent-to-child isolation).";

    internal static readonly string LateralReachCopy = "This workload gets direct network access to everything else on ";

    internal static readonly string DriverUnknownCopy =
        "OPERATOR NETWORK: this workload is attached to the operator-supplied network(s)";

    internal static readonly string[] DriverTokens = ["macvlan", "ipvlan", "bridge"];

    internal static string Everything(RenderedWorkloadArtifact artifact)
        => artifact.PrimaryContent + "\n" + string.Join("\n", artifact.Notes) + "\n" + (artifact.SecretOneLiner ?? string.Empty);

    internal static WorkloadArtifactRenderOptions Revealing(string token) => new()
    {
        RevealedCredentials = new Dictionary<string, string>(StringComparer.Ordinal) { [TokenCredentialId] = token },
    };

    internal static IReadOnlyList<ResolvedEnvVar> DefaultEnv() => new[]
    {
        new ResolvedEnvVar { Name = "REEVE_WORKLOAD_TOKEN", Kind = ResolvedEnvKind.SecretRef, SecretRef = "REEVE_WORKLOAD_TOKEN", CredentialId = TokenCredentialId },
        new ResolvedEnvVar { Name = "REEVE_GATEWAY_URL", Kind = ResolvedEnvKind.Literal, Value = GatewayUrl },
        new ResolvedEnvVar { Name = "LOG_LEVEL", Kind = ResolvedEnvKind.Literal, Value = "info" },
        new ResolvedEnvVar { Name = "REEVE_SPEC_HASH", Kind = ResolvedEnvKind.SpecHash, Value = SpecHashValue },
    };
}
