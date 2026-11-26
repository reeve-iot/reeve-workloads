using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using k8s;
using k8s.Models;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Reeve.Workloads.Abstractions;
using Reeve.Workloads.Abstractions.Contracts;
using Reeve.Workloads.Abstractions.Contracts.Deployment;
using Reeve.Workloads.Abstractions.Domain;
using Reeve.Workloads.Abstractions.Domain.DesiredSpec;
using Reeve.Workloads.Abstractions.Providers;
using Reeve.Workloads.Domain;
using Reeve.Workloads.Providers.Kubernetes;
using Xunit;

using Reeve.Workloads.Tests.Shared;

namespace Reeve.Workloads.Providers.Kubernetes.Tests;

public sealed class KubernetesWorkloadProviderTests
{
    private const string Ns = "reeve";
    private static readonly Guid InstanceId = Guid.Parse("11111111-2222-3333-4444-555555555555");
    private const string Digest = "sha256:1111111111111111111111111111111111111111111111111111111111111111";

    private const string TokenId = "workload-token";

    private const string SecretName = "reeve-wl-com-reeve-matter-matter-11111111";

    private static WorkloadDesiredSpec Spec(
        WorkloadKind kind = WorkloadKind.SingleNode,
        UnsupportedPodPatchSpec? patch = null,
        ResolvedNetwork? network = null,
        ResolvedPrivileges? privileges = null,
        IReadOnlyList<ResolvedVolume>? volumes = null,
        IReadOnlyList<ResolvedEnvVar>? env = null,
        string? pendingScanRunId = null,
        IReadOnlyList<string>? scanReadyNodeNames = null,
        ResolvedResources? resources = null,
        IReadOnlyList<ProvisionedCredential>? provisioned = null)
    {
        const string specHash = "abc123def456";
        var labels = DefaultWorkloadResourceNamer.Instance.Labels(new WorkloadLabelSubject
                     {
                         InstanceId = InstanceId,
                         InstallationId = "install-1",
                         SpecHash = specHash,
                     });

        return new WorkloadDesiredSpec
        {
            GroupInstanceId = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee"),
            GroupId = "com.reeve.matter",
            WorkloadId = "matter",
            InstanceId = InstanceId,
            Inst8 = "11111111",
            Provider = KubernetesProvider.Id,
            ResourceName = "reeve-wl-com-reeve-matter-matter-11111111",
            SecretName = "reeve-wl-com-reeve-matter-matter-11111111",
            Labels = labels,
            Image = $"ghcr.io/reeve/matter@{Digest}",
            Kind = kind,
            Network = network,
            Privileges = privileges,
            Volumes = volumes ?? new List<ResolvedVolume>(),
            Env = env ?? new List<ResolvedEnvVar>
            {
                new() { Name = "REEVE_WORKLOAD_TOKEN", Kind = ResolvedEnvKind.SecretRef, SecretRef = SecretName, CredentialId = TokenId },
            },
            ProvisionedCredentials = provisioned ?? [new ProvisionedCredential(TokenId, "REEVE_WORKLOAD_TOKEN")],
            Health = new WorkloadHealth { Mode = WorkloadHealthMode.Container },
            UnsupportedPodPatch = patch,
            PendingScanRunId = pendingScanRunId,
            ScanReadyNodeNames = scanReadyNodeNames,
            Resources = resources,
            SpecHash = specHash,
        };
    }

    private static ResolvedVolume Volume() => new()
    {
        Id = "data",
        MountPath = "/data",
        Size = "1Gi",
        Criticality = VolumeCriticality.Critical,
        ResourceName = "reeve-wl-com-reeve-matter-matter-data-11111111",
    };

    private static ResolvedVolume AdoptedVolume() => new()
    {
        Id = "data",
        MountPath = "/data",
        Size = "1Gi",
        Criticality = VolumeCriticality.Critical,
        ResourceName = "reeve-wl-com-reeve-matter-matter-data-retained99",
        ExistingClaim = true,
    };

    private static ResolvedNetwork NetworkWithPorts() => new()
    {
        Ports = new List<ResolvedPort> { new() { Name = "api", Port = 8080, Protocol = "tcp" } },
    };

    private static WorkloadRef Ref() => new()
    {
        InstanceId = InstanceId,
        GroupId = "com.reeve.matter",
        WorkloadId = "matter",
        Inst8 = "11111111",
        Provider = KubernetesProvider.Id,
    };

    private static KubernetesWorkloadProvider Build(
        FakeObjectClient objectClient,
        RecordingCredentialPort credentials,
        FakeSettingsService? settings = null,
        FakeLogReader? logReader = null,
        IKubernetesClientFactory? clientFactory = null,
        IWorkloadImageRegistryCredentialResolver? imagePullResolver = null,
        ILogger<KubernetesWorkloadProvider>? logger = null,
        string? podNamespace = null)
        => new(
            clientFactory ?? new FakeClientFactory(),
            objectClient,
            new KubernetesWorkloadRenderer(),
            new UnsupportedPodPatchApplier(),
            new KubernetesPatchRevalidator(),
            logReader ?? new FakeLogReader(),
            new FakeClusterInspector(),
            new KubernetesWorkloadPreflightChecker(),
            ScopeFactoryFor(settings ?? new FakeSettingsService(), credentials, imagePullResolver),
            new KubernetesNamespaceResolver(new InClusterEnvironment(_ => true, InClusterEnvironment.ServiceAccountTokenPath, () => podNamespace), new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build()),
            logger ?? NullLogger<KubernetesWorkloadProvider>.Instance);

    private static IServiceScopeFactory ScopeFactoryFor(
        IWorkloadOrchestratorSettingsService settingsService,
        IWorkloadCredentialPort credentials,
        IWorkloadImageRegistryCredentialResolver? imagePullResolver = null)
    {
        var services = new ServiceCollection()
            .AddSingleton(settingsService)
            .AddSingleton(credentials);
        if (imagePullResolver is not null)
        {
            services.AddSingleton(imagePullResolver);
        }

        var provider = services.BuildServiceProvider();
        return provider.GetRequiredService<IServiceScopeFactory>();
    }

    [Theory]
    [InlineData("64 MB")]
    [InlineData("garbage")]
    public async Task An_unrenderable_quantity_is_rejected_rather_than_thrown_out_of_apply(string limitsMemory)
    {
        var provider = Build(new FakeObjectClient(), new RecordingCredentialPort());

        var result = await provider.ApplyAsync(
            Spec(resources: new ResolvedResources { LimitsMemory = limitsMemory }), progress: null);

        result.Succeeded.Should().BeFalse();
        result.Reason.Should().Be(WorkloadApplyReason.Rejected);
        result.Message.Should().Contain(limitsMemory);
    }

    [Fact]
    public void Reports_kubernetes_provider()
    {
        var provider = Build(new FakeObjectClient(), new RecordingCredentialPort());

        provider.Descriptor.Id.Should().Be(KubernetesProvider.Id);
    }

    [Fact]
    public async Task Apply_fresh_mints_token_applies_secret_workload_service_and_pvcs_and_is_not_ready()
    {
        var objectClient = new FakeObjectClient();
        var credentials = new RecordingCredentialPort();
        var provider = Build(objectClient, credentials);

        var result = await provider.ApplyAsync(
            Spec(kind: WorkloadKind.SingleNode, network: NetworkWithPorts(), volumes: new[] { Volume() }), progress: null);

        result.Succeeded.Should().BeTrue();
        result.Reason.Should().Be(WorkloadApplyReason.Applied);
        result.AppliedSpecHash.Should().Be("abc123def456");

        credentials.Minted.Count.Should().Be(1);

        objectClient.AppliedKinds.Should().Contain(new[] { KubernetesObjectKind.Secret, KubernetesObjectKind.Deployment, KubernetesObjectKind.Service });
        objectClient.EnsuredPvcNames.Should().ContainSingle();

        objectClient.LastSecretManifest.Should().Contain("minted-workload-token-1");
    }

    [Fact]
    public async Task Apply_defaults_to_force_false_so_steady_state_reconcile_surfaces_drift()
    {
        var objectClient = new FakeObjectClient();
        var provider = Build(objectClient, new RecordingCredentialPort());

        await provider.ApplyAsync(Spec(kind: WorkloadKind.SingleNode, network: NetworkWithPorts()), progress: null);

        objectClient.AppliedForce.Should().NotBeEmpty();
        objectClient.AppliedForce.Should().OnlyContain(x => !x.Force,
            "the default apply must NOT force — a steady-state reconcile surfaces a foreign field manager as a Conflict");
    }

    [Fact]
    public async Task Apply_with_forceConflicts_threads_SSA_force_true_to_every_object()
    {
        var objectClient = new FakeObjectClient();
        var provider = Build(objectClient, new RecordingCredentialPort());

        await provider.ApplyAsync(
            Spec(kind: WorkloadKind.SingleNode, network: NetworkWithPorts()), progress: null,
            cancellationToken: default, forceConflicts: true);

        objectClient.AppliedForce.Should().NotBeEmpty();
        objectClient.AppliedForce.Should().OnlyContain(x => x.Force,
            "forceConflicts must thread to SSA force on EVERY object (Secret/Deployment/Service) so the update's authoritative apply adopts a foreign field manager instead of 409-ing");
    }

    private const string AutoPullSecretName = "reeve-wl-com-reeve-matter-matter-11111111-imagepull";

    [Fact]
    public async Task Apply_materializes_an_image_pull_secret_when_a_fallback_credential_matches_the_image_host()
    {
        var objectClient = new FakeObjectClient();
        var resolver = new FakeImagePullResolver("ghcr.io", new RegistryCredential
        {
            HostPattern = "ghcr.io",
            Username = "oauth2",
            Token = "official-index-env-token",
            AuthType = RegistryAuthType.Basic,
        });
        var provider = Build(objectClient, new RecordingCredentialPort(), imagePullResolver: resolver);

        var result = await provider.ApplyAsync(Spec(kind: WorkloadKind.SingleNode), progress: null);

        result.Succeeded.Should().BeTrue();
        resolver.AskedHosts.Should().ContainSingle().Which.Should().Be("ghcr.io");

        var pullSecret = objectClient.AppliedSecrets
            .Should().ContainSingle(s => s.Name == AutoPullSecretName).Subject;
        pullSecret.Manifest.Should().Contain(".dockerconfigjson");
        pullSecret.Manifest.Should().Contain("kubernetes.io/dockerconfigjson");
        pullSecret.Manifest.Should().Contain("official-index-env-token", "the token lives in the Secret data");
        AutoPullSecretName.Should().NotContain("official-index-env-token", "the Secret NAME must never embed the token");

        objectClient.LastDeploymentManifest.Should().Contain("imagePullSecrets");
        objectClient.LastDeploymentManifest.Should().Contain(AutoPullSecretName);
    }

    [Fact]
    public async Task Apply_does_not_materialize_a_pull_secret_when_no_fallback_matches_the_image_host()
    {
        var objectClient = new FakeObjectClient();
        var resolver = new FakeImagePullResolver("registry.example.com:5000", new RegistryCredential
        {
            HostPattern = "registry.example.com:5000",
            Username = "oauth2",
            Token = "official-index-env-token",
            AuthType = RegistryAuthType.Basic,
        });
        var provider = Build(objectClient, new RecordingCredentialPort(), imagePullResolver: resolver);

        var result = await provider.ApplyAsync(Spec(kind: WorkloadKind.SingleNode), progress: null);

        result.Succeeded.Should().BeTrue();
        objectClient.AppliedSecrets.Should().NotContain(s => s.Name == AutoPullSecretName);
        objectClient.LastDeploymentManifest.Should().NotContain("imagePullSecrets");
        (objectClient.LastDeploymentManifest ?? "").Should().NotContain("official-index-env-token");
    }

    [Fact]
    public async Task Apply_prefers_an_operator_global_pull_secret_over_the_auto_fallback()
    {
        var objectClient = new FakeObjectClient();
        var resolver = new FakeImagePullResolver("ghcr.io", new RegistryCredential
        {
            HostPattern = "ghcr.io",
            Username = "oauth2",
            Token = "official-index-env-token",
            AuthType = RegistryAuthType.Basic,
        });
        var settings = new FakeSettingsService { ImagePullSecretName = "operator-pull-secret" };
        var provider = Build(objectClient, new RecordingCredentialPort(), settings: settings, imagePullResolver: resolver);

        var result = await provider.ApplyAsync(Spec(kind: WorkloadKind.SingleNode), progress: null);

        result.Succeeded.Should().BeTrue();
        resolver.AskedHosts.Should().BeEmpty("the operator's global pull secret takes precedence");
        objectClient.AppliedSecrets.Should().NotContain(s => s.Name == AutoPullSecretName);
        objectClient.LastDeploymentManifest.Should().Contain("operator-pull-secret");
    }

    [Fact]
    public async Task Apply_threads_the_adopted_claim_flag_to_EnsurePvc_for_an_existingClaim_volume()
    {
        var objectClient = new FakeObjectClient();
        var provider = Build(objectClient, new RecordingCredentialPort());

        await provider.ApplyAsync(Spec(volumes: new[] { AdoptedVolume() }), progress: null);

        objectClient.EnsuredPvcs.Should().ContainSingle();
        objectClient.EnsuredPvcs[0].Name.Should().Be("reeve-wl-com-reeve-matter-matter-data-retained99");
        objectClient.EnsuredPvcs[0].Adopted.Should().BeTrue(
            "an existingClaim (restore) volume is an adopted claim — the object-client must enforce must-exist-else-fail on it");
    }

    [Fact]
    public async Task Apply_of_a_fresh_volume_threads_isAdoptedClaim_false()
    {
        var objectClient = new FakeObjectClient();
        var provider = Build(objectClient, new RecordingCredentialPort());

        await provider.ApplyAsync(Spec(volumes: new[] { Volume() }), progress: null);

        objectClient.EnsuredPvcs.Should().ContainSingle();
        objectClient.EnsuredPvcs[0].Adopted.Should().BeFalse(
            "a fresh volume keeps create-if-absent — only an existingClaim (restore) volume is must-exist-else-fail");
    }

    [Fact]
    public async Task Apply_FAILS_Rejected_and_does_not_deploy_when_an_adopted_claim_PVC_is_absent()
    {
        var objectClient = new FakeObjectClient { ThrowOnAdoptedPvcEnsure = true };
        var provider = Build(objectClient, new RecordingCredentialPort());

        var result = await provider.ApplyAsync(Spec(volumes: new[] { AdoptedVolume() }), progress: null);

        result.Succeeded.Should().BeFalse();
        result.Reason.Should().Be(WorkloadApplyReason.Rejected,
            "a restore whose retained PVC is absent is a fail-CLOSED rejection, not a transient blip");
        result.AppliedSpecHash.Should().BeNull("a failed apply leaves AppliedSpecHash unset so drift persists");
        objectClient.AppliedKinds.Should().NotContain(KubernetesObjectKind.Deployment,
            "the apply must STOP at the missing adopted PVC — never deploy a fresh empty volume while claiming a restore");
    }

    [Fact]
    public async Task Apply_meeting_a_terminating_PVC_applies_the_rest_but_does_not_settle_until_the_claim_is_gone()
    {
        var terminating = Volume();
        var healthy = Volume() with { Id = "cache", MountPath = "/cache", ResourceName = "reeve-wl-com-reeve-matter-matter-cache-11111111" };
        var objectClient = new FakeObjectClient();
        objectClient.TerminatingPvcNames.Add(terminating.ResourceName);
        var provider = Build(objectClient, new RecordingCredentialPort());
        var spec = Spec(kind: WorkloadKind.SingleNode, network: NetworkWithPorts(), volumes: new[] { terminating, healthy });

        var whileTerminating = await provider.ApplyAsync(spec, progress: null);

        whileTerminating.Succeeded.Should().BeFalse("a claim being deleted is not present");
        whileTerminating.Reason.Should().Be(WorkloadApplyReason.ProviderUnreachable,
            "a terminating claim clears by itself — a retryable reason, never a rollback-worthy error");
        whileTerminating.AppliedSpecHash.Should().BeNull("the hash must not settle while a declared claim is going away");
        whileTerminating.Message.Should().Contain(terminating.ResourceName).And.NotContain(healthy.ResourceName);
        objectClient.EnsuredPvcNames.Should().Equal(new[] { terminating.ResourceName, healthy.ResourceName },
            "a terminating claim must not stop the claims declared after it");
        objectClient.AppliedKinds.Should().Contain(new[] { KubernetesObjectKind.Deployment, KubernetesObjectKind.Service },
            "the workload object and the Service are still applied while a claim terminates");

        objectClient.TerminatingPvcNames.Clear();
        var onceGone = await provider.ApplyAsync(spec, progress: null);

        onceGone.Succeeded.Should().BeTrue("the claim is ensured on the next pass and the apply settles");
    }

    [Fact]
    public async Task Apply_perNode_ssas_a_daemonset_not_a_deployment()
    {
        var objectClient = new FakeObjectClient();
        var provider = Build(objectClient, new RecordingCredentialPort());

        await provider.ApplyAsync(Spec(kind: WorkloadKind.PerNode), progress: null);

        objectClient.AppliedKinds.Should().Contain(KubernetesObjectKind.DaemonSet);
        objectClient.AppliedKinds.Should().NotContain(KubernetesObjectKind.Deployment);
    }

    [Theory]
    [InlineData(WorkloadKind.Service, nameof(KubernetesObjectKind.Deployment))]
    [InlineData(WorkloadKind.SingleNode, nameof(KubernetesObjectKind.Deployment))]
    [InlineData(WorkloadKind.PerNode, nameof(KubernetesObjectKind.DaemonSet))]
    [InlineData(WorkloadKind.ScanPerNode, nameof(KubernetesObjectKind.Job))]
    public async Task Apply_removes_the_instances_objects_of_every_other_workload_kind_before_applying_its_own(
        WorkloadKind kind, string keptKind)
    {
        var kept = Enum.Parse<KubernetesObjectKind>(keptKind);
        var objectClient = new FakeObjectClient();
        var provider = Build(objectClient, new RecordingCredentialPort());

        var result = await provider.ApplyAsync(Spec(kind: kind), progress: null);

        result.Succeeded.Should().BeTrue();
        objectClient.OtherKindDeletes.Should().Equal(kept);
    }

    [Fact]
    public async Task Apply_fails_without_applying_the_workload_when_the_previous_kind_cannot_be_removed()
    {
        var objectClient = new FakeObjectClient { ThrowOnOtherKindDelete = true };
        var provider = Build(objectClient, new RecordingCredentialPort());

        var result = await provider.ApplyAsync(Spec(kind: WorkloadKind.PerNode), progress: null);

        result.Succeeded.Should().BeFalse();
        result.Reason.Should().Be(WorkloadApplyReason.ProviderUnreachable);
        objectClient.AppliedKinds.Should().NotContain(KubernetesObjectKind.DaemonSet);
    }

    [Fact]
    public async Task Apply_reuses_an_existing_active_token_without_minting_again()
    {
        var objectClient = new FakeObjectClient
        {
            ExistingSecretToken = "rwt_existing_active_token",
        };
        var credentials = new RecordingCredentialPort
        {
            Live = { "rwt_existing_active_token" },
        };
        var provider = Build(objectClient, credentials);

        await provider.ApplyAsync(Spec(), progress: null);

        credentials.Minted.Count.Should().Be(0);
        objectClient.LastSecretManifest.Should().Contain("rwt_existing_active_token");
    }

    [Fact]
    public async Task Apply_mints_a_new_token_when_the_stored_one_is_no_longer_active()
    {
        var objectClient = new FakeObjectClient
        {
            ExistingSecretToken = "rwt_revoked_token",
        };
        var credentials = new RecordingCredentialPort();
        var provider = Build(objectClient, credentials);

        await provider.ApplyAsync(Spec(), progress: null);

        credentials.Minted.Count.Should().Be(1);
        objectClient.LastSecretManifest.Should().NotContain("rwt_revoked_token");
    }

    private static IReadOnlyDictionary<string, string> AppliedSecretData(FakeObjectClient objectClient)
    {
        using var json = System.Text.Json.JsonDocument.Parse(objectClient.LastSecretManifest!);
        return json.RootElement.GetProperty("stringData").EnumerateObject()
            .ToDictionary(p => p.Name, p => p.Value.GetString()!, StringComparer.Ordinal);
    }

    private static readonly IReadOnlyList<ResolvedEnvVar> CredentialEnv =
    [
        new() { Name = "WORKLOAD_TOKEN", Kind = ResolvedEnvKind.SecretRef, SecretRef = SecretName, CredentialId = TokenId },
        new() { Name = "FOO_TOKEN", Kind = ResolvedEnvKind.SecretRef, SecretRef = SecretName, CredentialId = "foo" },
    ];

    [Fact]
    public async Task Apply_writes_a_declared_credential_under_its_own_name_and_a_provisioned_one_as_secret_keys()
    {
        var objectClient = new FakeObjectClient();
        var provider = Build(objectClient, new RecordingCredentialPort());

        var result = await provider.ApplyAsync(Spec(env: CredentialEnv), progress: null);

        result.Succeeded.Should().BeTrue(result.Message);
        var data = AppliedSecretData(objectClient);
        data["REEVE_WORKLOAD_TOKEN"].Should().Be("minted-workload-token-1", "the provisioned credential is where the token lives");
        data["WORKLOAD_TOKEN"].Should().Be("minted-workload-token-1", "one credential, one value, under every name carrying its id");
        data["FOO_TOKEN"].Should().Be("minted-foo-2", "a declared credential need not be named REEVE_WORKLOAD_TOKEN");
    }

    [Fact]
    public async Task Apply_lets_secret_material_under_a_credential_name_win()
    {
        var objectClient = new FakeObjectClient();
        var provider = Build(objectClient, new RecordingCredentialPort());
        var spec = Spec() with
        {
            SecretMaterial = new Dictionary<string, string>(StringComparer.Ordinal) { ["REEVE_WORKLOAD_TOKEN"] = "from-material" },
        };

        var result = await provider.ApplyAsync(spec, progress: null);

        result.Succeeded.Should().BeTrue(result.Message);
        AppliedSecretData(objectClient)["REEVE_WORKLOAD_TOKEN"].Should().Be("from-material");
    }

    [Fact]
    public async Task Apply_reads_each_credential_back_and_asks_once_per_id_so_a_benign_reapply_rotates_nothing()
    {
        var objectClient = new FakeObjectClient
        {
            SecretValues = { ["REEVE_WORKLOAD_TOKEN"] = "held-tok", ["WORKLOAD_TOKEN"] = "held-tok", ["FOO_TOKEN"] = "held-foo" },
        };
        var credentials = new RecordingCredentialPort { Live = { "held-tok", "held-foo" } };
        var provider = Build(objectClient, credentials);

        await provider.ApplyAsync(Spec(env: CredentialEnv), progress: null);

        credentials.Asks.Select(a => (a.CredentialId, a.Existing)).Should().Equal(("workload-token", "held-tok"), ("foo", "held-foo"));
        credentials.Minted.Count.Should().Be(0);
        objectClient.SecretReads.Should().ContainSingle("the Secret is read once for every owned name")
            .Which.Should().Equal("REEVE_WORKLOAD_TOKEN", "WORKLOAD_TOKEN", "FOO_TOKEN");
        AppliedSecretData(objectClient).Should().Contain(new Dictionary<string, string>
        {
            ["REEVE_WORKLOAD_TOKEN"] = "held-tok",
            ["WORKLOAD_TOKEN"] = "held-tok",
            ["FOO_TOKEN"] = "held-foo",
        });
    }

    private static readonly Dictionary<string, string> FirstHeld = new(StringComparer.Ordinal)
    {
        ["REEVE_WORKLOAD_TOKEN"] = "first-workload-token",
        ["WORKLOAD_TOKEN"] = "first-workload-token",
        ["FOO_TOKEN"] = "first-foo",
    };

    [Fact]
    public async Task Apply_rolls_the_pod_when_the_held_credential_was_revoked_and_minted_again()
    {
        var first = await PodTemplateAnnotationsAfterApply([], new RecordingCredentialPort { Answer = (_, id) => $"first-{id}" });

        var reminted = await PodTemplateAnnotationsAfterApply(FirstHeld, new RecordingCredentialPort());

        reminted.Should().NotBeEquivalentTo(first, "a pod reads its Secret when it starts, so only a new pod gets the new value");
    }

    [Fact]
    public async Task Apply_leaves_the_pod_template_alone_when_the_held_credentials_are_still_valid()
    {
        var first = await PodTemplateAnnotationsAfterApply([], new RecordingCredentialPort { Answer = (_, id) => $"first-{id}" });

        var reapplied = await PodTemplateAnnotationsAfterApply(
            FirstHeld, new RecordingCredentialPort { Live = { "first-workload-token", "first-foo" } });

        reapplied.Should().BeEquivalentTo(first);
    }

    [Fact]
    public async Task Apply_rolls_a_per_node_pod_when_the_held_credential_was_revoked_and_minted_again()
    {
        var first = await PodTemplateAnnotationsAfterApply(
            [], new RecordingCredentialPort { Answer = (_, id) => $"first-{id}" }, WorkloadKind.PerNode);

        var reminted = await PodTemplateAnnotationsAfterApply(FirstHeld, new RecordingCredentialPort(), WorkloadKind.PerNode);

        reminted.Should().NotBeEquivalentTo(first, "a DaemonSet pod reads its Secret when it starts, like a Deployment's");
    }

    [Fact]
    public async Task Apply_leaves_the_pod_template_alone_for_a_host_that_issues_no_credential()
    {
        var annotations = await PodTemplateAnnotationsAfterApply([], new RecordingCredentialPort { Answer = (_, _) => null });

        annotations.Keys.Should().NotContain(KubernetesWorkloadRenderer.CredentialDigestAnnotationKey,
            "a host with no credential has nothing to re-mint, so its pods never roll for one");
    }

    private static async Task<Dictionary<string, string>> PodTemplateAnnotationsAfterApply(
        Dictionary<string, string> held, RecordingCredentialPort credentials, WorkloadKind kind = WorkloadKind.SingleNode)
    {
        var objectClient = new FakeObjectClient { SecretValues = new Dictionary<string, string>(held, StringComparer.Ordinal) };
        var result = await Build(objectClient, credentials).ApplyAsync(Spec(kind: kind, env: CredentialEnv), progress: null);
        result.Succeeded.Should().BeTrue(result.Message);

        var manifest = kind == WorkloadKind.PerNode ? objectClient.LastDaemonSetManifest : objectClient.LastDeploymentManifest;
        using var json = System.Text.Json.JsonDocument.Parse(manifest!);
        var metadata = json.RootElement.GetProperty("spec").GetProperty("template").GetProperty("metadata");
        return metadata.TryGetProperty("annotations", out var annotations)
            ? annotations.EnumerateObject().ToDictionary(a => a.Name, a => a.Value.GetString()!, StringComparer.Ordinal)
            : new Dictionary<string, string>(StringComparer.Ordinal);
    }

    [Fact]
    public async Task Apply_reads_a_credential_back_under_its_provisioned_name_before_a_declared_env()
    {
        var objectClient = new FakeObjectClient
        {
            SecretValues = { ["WORKLOAD_TOKEN"] = "from-declared", ["REEVE_WORKLOAD_TOKEN"] = "from-provisioned" },
        };
        var credentials = new RecordingCredentialPort();
        var provider = Build(objectClient, credentials);

        await provider.ApplyAsync(Spec(env: CredentialEnv), progress: null);

        credentials.Asks.Select(a => (a.CredentialId, a.Existing)).Should().Contain(("workload-token", "from-provisioned"));
    }

    [Fact]
    public async Task Apply_counts_a_held_empty_value_as_none_so_the_next_owned_name_answers()
    {
        var objectClient = new FakeObjectClient
        {
            SecretValues = { ["REEVE_WORKLOAD_TOKEN"] = "", ["WORKLOAD_TOKEN"] = "held-tok", ["FOO_TOKEN"] = "" },
        };
        var credentials = new RecordingCredentialPort();
        var provider = Build(objectClient, credentials);

        await provider.ApplyAsync(Spec(env: CredentialEnv), progress: null);

        credentials.Asks.Select(a => (a.CredentialId, a.Existing)).Should().Equal(("workload-token", "held-tok"), ("foo", null));
    }

    [Fact]
    public async Task Apply_lets_a_declared_literal_keep_a_provisioned_name_and_asks_nothing_for_the_nameless_credential()
    {
        var objectClient = new FakeObjectClient { SecretValues = { ["REEVE_WORKLOAD_TOKEN"] = "rwt_existing" } };
        var credentials = new RecordingCredentialPort();
        var provider = Build(objectClient, credentials);
        var env = new List<ResolvedEnvVar>
        {
            new() { Name = "REEVE_WORKLOAD_TOKEN", Kind = ResolvedEnvKind.Literal, Value = "declared" },
        };

        var result = await provider.ApplyAsync(Spec(env: env), progress: null);

        result.Succeeded.Should().BeTrue(result.Message);
        credentials.Asks.Should().BeEmpty("the declared env owns the only name the credential had");
        objectClient.SecretReads.Should().BeEmpty();
        AppliedSecretData(objectClient).Should().NotContainKey("REEVE_WORKLOAD_TOKEN");
    }

    [Fact]
    public async Task Apply_never_hands_the_port_a_value_held_under_a_name_a_declared_literal_keeps()
    {
        var objectClient = new FakeObjectClient { SecretValues = { ["REEVE_WORKLOAD_TOKEN"] = "declared" } };
        var credentials = new RecordingCredentialPort();
        var provider = Build(objectClient, credentials);
        var env = CredentialEnv.Append(new ResolvedEnvVar { Name = "REEVE_WORKLOAD_TOKEN", Kind = ResolvedEnvKind.Literal, Value = "declared" }).ToList();

        await provider.ApplyAsync(Spec(env: env), progress: null);

        credentials.Asks.Select(a => (a.CredentialId, a.Existing)).Should().Equal(("workload-token", null), ("foo", null));
        objectClient.SecretReads.Should().ContainSingle().Which.Should().Equal("WORKLOAD_TOKEN", "FOO_TOKEN");
        var data = AppliedSecretData(objectClient);
        data.Should().NotContainKey("REEVE_WORKLOAD_TOKEN");
        data["WORKLOAD_TOKEN"].Should().Be("minted-workload-token-1");
    }

    [Fact]
    public async Task Apply_never_hands_the_port_secret_material_held_under_a_credential_name()
    {
        var objectClient = new FakeObjectClient { SecretValues = { ["REEVE_WORKLOAD_TOKEN"] = "from-material" } };
        var credentials = new RecordingCredentialPort();
        var provider = Build(objectClient, credentials);
        var spec = Spec(env: CredentialEnv) with
        {
            SecretMaterial = new Dictionary<string, string>(StringComparer.Ordinal) { ["REEVE_WORKLOAD_TOKEN"] = "from-material" },
        };

        await provider.ApplyAsync(spec, progress: null);

        credentials.Asks.Select(a => (a.CredentialId, a.Existing)).Should().Equal(("workload-token", null), ("foo", null));
        var data = AppliedSecretData(objectClient);
        data["REEVE_WORKLOAD_TOKEN"].Should().Be("from-material");
        data["WORKLOAD_TOKEN"].Should().Be("minted-workload-token-1");
    }

    [Fact]
    public async Task Apply_refuses_two_credentials_on_one_name_before_reading_or_asking_anything()
    {
        var objectClient = new FakeObjectClient();
        var credentials = new RecordingCredentialPort();
        var provider = Build(objectClient, credentials);
        var spec = Spec(env: CredentialEnv, provisioned: [new ProvisionedCredential(TokenId, "REEVE_WORKLOAD_TOKEN"), new ProvisionedCredential("foo", "WORKLOAD_TOKEN")]);

        var result = await provider.ApplyAsync(spec, progress: null);

        result.Reason.Should().Be(WorkloadApplyReason.Rejected);
        result.Message.Should().Contain("WORKLOAD_TOKEN");
        credentials.Asks.Should().BeEmpty();
        objectClient.SecretReads.Should().BeEmpty();
        objectClient.AppliedKinds.Should().BeEmpty();
    }

    [Fact]
    public async Task Apply_refuses_a_secret_reference_nothing_backs_before_reading_or_asking_anything()
    {
        var objectClient = new FakeObjectClient();
        var credentials = new RecordingCredentialPort();
        var provider = Build(objectClient, credentials);
        var env = CredentialEnv.Append(new ResolvedEnvVar { Name = "DB_PASSWORD", Kind = ResolvedEnvKind.SecretRef, SecretRef = SecretName }).ToList();

        var result = await provider.ApplyAsync(Spec(env: env), progress: null);

        result.Reason.Should().Be(WorkloadApplyReason.Rejected);
        result.Message.Should().Contain("DB_PASSWORD");
        credentials.Asks.Should().BeEmpty();
        objectClient.SecretReads.Should().BeEmpty();
        objectClient.AppliedKinds.Should().BeEmpty();
    }

    [Fact]
    public async Task Apply_asks_once_for_reeves_own_shape_with_the_held_value_and_writes_one_key()
    {
        var objectClient = new FakeObjectClient { SecretValues = { ["REEVE_WORKLOAD_TOKEN"] = "held-tok" } };
        var credentials = new RecordingCredentialPort { Live = { "held-tok" } };
        var provider = Build(objectClient, credentials);

        var result = await provider.ApplyAsync(Spec(), progress: null);

        result.Succeeded.Should().BeTrue(result.Message);
        credentials.Asks.Select(a => (a.CredentialId, a.Existing)).Should().Equal((TokenId, "held-tok"));
        objectClient.SecretReads.Should().ContainSingle().Which.Should().Equal("REEVE_WORKLOAD_TOKEN");
        AppliedSecretData(objectClient).Should().Equal(new Dictionary<string, string> { ["REEVE_WORKLOAD_TOKEN"] = "held-tok" });
    }

    [Fact]
    public async Task Apply_never_reads_back_or_writes_a_credential_under_a_literal_holding_an_id()
    {
        var objectClient = new FakeObjectClient { SecretValues = { ["FEED_URL"] = "v" } };
        var credentials = new RecordingCredentialPort();
        var provider = Build(objectClient, credentials);
        var env = new List<ResolvedEnvVar> { new() { Name = "FEED_URL", Kind = ResolvedEnvKind.Literal, Value = "v", CredentialId = "feed" } };

        var result = await provider.ApplyAsync(Spec(env: env, provisioned: []), progress: null);

        result.Succeeded.Should().BeTrue(result.Message);
        credentials.Asks.Should().BeEmpty();
        objectClient.SecretReads.Should().BeEmpty();
        AppliedSecretData(objectClient).Should().NotContainKey("FEED_URL");
    }

    [Fact]
    public async Task Apply_refuses_a_credential_entry_whose_name_a_literal_also_fills_before_reading_or_asking()
    {
        var objectClient = new FakeObjectClient();
        var credentials = new RecordingCredentialPort();
        var provider = Build(objectClient, credentials);
        var env = CredentialEnv
            .Append(new ResolvedEnvVar { Name = "FEED_URL", Kind = ResolvedEnvKind.Literal, Value = "declared" })
            .Append(new ResolvedEnvVar { Name = "FEED_URL", Kind = ResolvedEnvKind.SecretRef, SecretRef = SecretName, CredentialId = TokenId })
            .ToList();

        var result = await provider.ApplyAsync(Spec(env: env), progress: null);

        result.Reason.Should().Be(WorkloadApplyReason.Rejected);
        result.Message.Should().Contain("FEED_URL");
        credentials.Asks.Should().BeEmpty();
        objectClient.SecretReads.Should().BeEmpty();
        objectClient.AppliedKinds.Should().BeEmpty();
    }

    [Fact]
    public async Task Apply_a_secret_read_fault_is_unreachable_and_mints_nothing_for_any_credential()
    {
        var objectClient = new FakeObjectClient
        {
            ThrowOnReadSecret = new System.Net.Http.HttpRequestException("connection refused"),
        };
        var credentials = new RecordingCredentialPort();
        var provider = Build(objectClient, credentials);

        var result = await provider.ApplyAsync(Spec(env: CredentialEnv), progress: null);

        result.Reason.Should().Be(WorkloadApplyReason.ProviderUnreachable);
        credentials.Asks.Should().BeEmpty("a read fault is not 'no existing value'");
        objectClient.AppliedKinds.Should().BeEmpty();
    }

    [Fact]
    public async Task Apply_with_no_credential_asks_nothing_and_writes_no_token_key()
    {
        var objectClient = new FakeObjectClient();
        var credentials = new RecordingCredentialPort();
        var provider = Build(objectClient, credentials);

        var result = await provider.ApplyAsync(Spec(env: [], provisioned: []), progress: null);

        result.Succeeded.Should().BeTrue(result.Message);
        credentials.Asks.Should().BeEmpty();
        AppliedSecretData(objectClient).Should().NotContainKey("REEVE_WORKLOAD_TOKEN");
    }

    [Fact]
    public async Task Apply_keeps_a_provisioned_credential_in_the_secret_and_out_of_the_pod_env()
    {
        var objectClient = new FakeObjectClient();
        var provider = Build(objectClient, new RecordingCredentialPort());

        await provider.ApplyAsync(Spec(env: []), progress: null);

        AppliedSecretData(objectClient).Should().ContainKey("REEVE_WORKLOAD_TOKEN");
        objectClient.LastDeploymentManifest.Should().NotContain("REEVE_WORKLOAD_TOKEN");
    }

    [Fact]
    public async Task Apply_refuses_by_name_a_secret_reference_with_neither_material_nor_a_credential_id()
    {
        var objectClient = new FakeObjectClient();
        var provider = Build(objectClient, new RecordingCredentialPort());
        var env = new List<ResolvedEnvVar>
        {
            new() { Name = "REEVE_WORKLOAD_TOKEN", Kind = ResolvedEnvKind.SecretRef, SecretRef = SecretName },
        };

        var result = await provider.ApplyAsync(Spec(env: env, provisioned: []), progress: null);

        result.Reason.Should().Be(WorkloadApplyReason.Rejected);
        result.Message.Should().Contain("REEVE_WORKLOAD_TOKEN");
        objectClient.AppliedKinds.Should().BeEmpty();
    }

    [Fact]
    public async Task Apply_with_patch_and_toggle_off_is_rejected_with_no_ssa()
    {
        var objectClient = new FakeObjectClient();
        var settings = new FakeSettingsService { AllowUnsupportedOverrides = false };
        var provider = Build(objectClient, new RecordingCredentialPort(), settings);

        var patch = new UnsupportedPodPatchSpec { PatchJson = """[{"op":"add","path":"/metadata/annotations/foo","value":"bar"}]""" };
        var result = await provider.ApplyAsync(Spec(patch: patch, network: NetworkWithPorts()), progress: null);

        result.Succeeded.Should().BeFalse();
        result.Reason.Should().Be(WorkloadApplyReason.Rejected);
        result.Message.Should().Contain("allowUnsupportedOverrides");
        objectClient.AppliedKinds.Should().BeEmpty();
    }

    [Fact]
    public async Task Apply_with_escalating_patch_and_toggle_on_is_rejected_and_not_applied()
    {
        var objectClient = new FakeObjectClient();
        var settings = new FakeSettingsService { AllowUnsupportedOverrides = true };
        var provider = Build(objectClient, new RecordingCredentialPort(), settings);

        var patch = new UnsupportedPodPatchSpec
        {
            PatchJson = """[{"op":"add","path":"/spec/template/spec/containers/0/securityContext","value":{"privileged":true}}]""",
        };

        var result = await provider.ApplyAsync(Spec(patch: patch), progress: null);

        result.Succeeded.Should().BeFalse();
        result.Reason.Should().Be(WorkloadApplyReason.Rejected);
        result.Message.Should().Contain("escalates beyond the desired spec's resolved privileges");
        objectClient.AppliedKinds.Should().BeEmpty();
    }

    [Fact]
    public async Task Apply_with_benign_patch_and_toggle_on_applies_the_patched_object()
    {
        var objectClient = new FakeObjectClient();
        var settings = new FakeSettingsService { AllowUnsupportedOverrides = true };
        var provider = Build(objectClient, new RecordingCredentialPort(), settings);

        var patch = new UnsupportedPodPatchSpec
        {
            PatchJson = """[{"op":"add","path":"/spec/template/spec/terminationGracePeriodSeconds","value":45}]""",
        };

        var result = await provider.ApplyAsync(Spec(patch: patch), progress: null);

        result.Succeeded.Should().BeTrue();
        objectClient.AppliedKinds.Should().Contain(KubernetesObjectKind.Deployment);
        objectClient.LastDeploymentManifest.Should().Contain("terminationGracePeriodSeconds");
    }

    [Fact]
    public async Task Apply_with_malformed_patch_is_rejected()
    {
        var objectClient = new FakeObjectClient();
        var settings = new FakeSettingsService { AllowUnsupportedOverrides = true };
        var provider = Build(objectClient, new RecordingCredentialPort(), settings);

        var patch = new UnsupportedPodPatchSpec { PatchJson = "not-a-json-patch" };

        var result = await provider.ApplyAsync(Spec(patch: patch), progress: null);

        result.Succeeded.Should().BeFalse();
        result.Reason.Should().Be(WorkloadApplyReason.Rejected);
        objectClient.AppliedKinds.Should().BeEmpty();
    }

    [Fact]
    public async Task Apply_ssa_409_returns_conflict_naming_the_field_manager()
    {
        var objectClient = new FakeObjectClient
        {
            ConflictOnKind = KubernetesObjectKind.Deployment,
            ConflictingManager = "kubectl-client-side-apply",
        };
        var provider = Build(objectClient, new RecordingCredentialPort());

        var result = await provider.ApplyAsync(Spec(), progress: null);

        result.Succeeded.Should().BeFalse();
        result.Reason.Should().Be(WorkloadApplyReason.FieldManagerConflict);
        result.ConflictingFieldManager.Should().Be("kubectl-client-side-apply");
    }

    [Fact]
    public async Task Apply_maps_an_ssa_rejection_to_Rejected_and_keeps_the_api_servers_message()
    {
        var objectClient = new FakeObjectClient
        {
            RejectOnKind = KubernetesObjectKind.Deployment,
            RejectionMessage = "spec.template.spec.nodeSelector: Invalid value: \"'true'\"",
        };
        var provider = Build(objectClient, new RecordingCredentialPort());

        var result = await provider.ApplyAsync(Spec(), progress: null);

        result.Succeeded.Should().BeFalse();
        result.Reason.Should().Be(WorkloadApplyReason.Rejected,
            "a refused spec is not a transient provider error — the engine only surfaces Rejected to the operator");
        result.Message.Should().Contain("nodeSelector",
            "the API server's own reason must survive the mapping, or the state message says nothing actionable");
    }

    [Fact]
    public async Task Apply_unreachable_returns_provider_unreachable_and_does_not_throw()
    {
        var objectClient = new FakeObjectClient { UnreachableOnAll = true };
        var provider = Build(objectClient, new RecordingCredentialPort());

        var act = async () => await provider.ApplyAsync(Spec(), progress: null);

        var result = await act.Should().NotThrowAsync();
        result.Subject.Succeeded.Should().BeFalse();
        result.Subject.Reason.Should().Be(WorkloadApplyReason.ProviderUnreachable);
    }

    [Fact]
    public async Task Apply_token_read_transport_throw_returns_provider_unreachable_and_does_not_mint()
    {
        var objectClient = new FakeObjectClient
        {
            ThrowOnReadSecret = new System.Net.Http.HttpRequestException("connection refused"),
        };
        var credentials = new RecordingCredentialPort();
        var provider = Build(objectClient, credentials);

        var act = async () => await provider.ApplyAsync(Spec(), progress: null);

        var result = await act.Should().NotThrowAsync();
        result.Subject.Succeeded.Should().BeFalse();
        result.Subject.Reason.Should().Be(WorkloadApplyReason.ProviderUnreachable,
            "a transport failure of the token READ is an unreachable provider, not a mint error");
        credentials.Minted.Count.Should().Be(0, "a token-read transport failure must not gratuitously mint a fresh token");
        objectClient.AppliedKinds.Should().BeEmpty("nothing is applied once the token read fails");
    }

    [Fact]
    public async Task Apply_with_no_namespace_configured_is_rejected()
    {
        var provider = Build(new FakeObjectClient(), new RecordingCredentialPort(), new FakeSettingsService { Namespace = null });

        var result = await provider.ApplyAsync(Spec(), progress: null);

        result.Succeeded.Should().BeFalse();
        result.Reason.Should().Be(WorkloadApplyReason.Rejected);
        result.Message.Should().Contain("namespace");
    }

    [Fact]
    public async Task Apply_with_a_blank_namespace_in_cluster_deploys_into_the_pods_own_namespace()
    {
        var objectClient = new FakeObjectClient();
        var provider = Build(
            objectClient, new RecordingCredentialPort(), new FakeSettingsService { Namespace = null }, podNamespace: "pod-ns");

        var result = await provider.ApplyAsync(Spec(), progress: null);

        result.Succeeded.Should().BeTrue();
        using var manifest = System.Text.Json.JsonDocument.Parse(objectClient.LastDeploymentManifest!);
        manifest.RootElement.GetProperty("metadata").GetProperty("namespace").GetString().Should().Be("pod-ns");
        objectClient.LastDeploymentNamespace.Should().Be("pod-ns");
    }

    [Fact]
    public async Task Teardown_retain_true_deletes_with_retainPvc_true()
    {
        var objectClient = new FakeObjectClient();
        var provider = Build(objectClient, new RecordingCredentialPort());

        await provider.TeardownAsync(Ref(), retainVolumes: true);

        objectClient.DeleteCalled.Should().BeTrue();
        objectClient.LastDeleteRetainPvc.Should().BeTrue();
    }

    [Fact]
    public async Task Teardown_retain_false_deletes_with_retainPvc_false()
    {
        var objectClient = new FakeObjectClient();
        var provider = Build(objectClient, new RecordingCredentialPort());

        await provider.TeardownAsync(Ref(), retainVolumes: false);

        objectClient.LastDeleteRetainPvc.Should().BeFalse();
    }

    [Fact]
    public async Task Teardown_does_not_revoke_the_instance_tokens()
    {
        var credentials = new RecordingCredentialPort();
        var provider = Build(new FakeObjectClient(), credentials);

        await provider.TeardownAsync(Ref(), retainVolumes: false);

        credentials.RevokeAllCount.Should().Be(0);
    }

    [Fact]
    public async Task Teardown_unreachable_throws_so_the_instance_is_kept_for_a_retry()
    {
        var objectClient = new FakeObjectClient { ThrowOnDelete = true };
        var provider = Build(objectClient, new RecordingCredentialPort());

        var act = async () => await provider.TeardownAsync(Ref(), retainVolumes: false);

        await act.Should().ThrowAsync<Exception>(
            "a teardown that did not delete the workload must not read as done, or the instance row is deleted under a live Deployment");
    }

    [Fact]
    public async Task Teardown_without_a_resolvable_namespace_throws()
    {
        var objectClient = new FakeObjectClient();
        var provider = Build(objectClient, new RecordingCredentialPort(), new FakeSettingsService { Namespace = null });

        var act = async () => await provider.TeardownAsync(Ref(), retainVolumes: false);

        await act.Should().ThrowAsync<InvalidOperationException>();
        objectClient.DeleteCalled.Should().BeFalse();
    }

    [Fact]
    public async Task GetStatus_not_found_returns_NotFound()
    {
        var objectClient = new FakeObjectClient { StatusRaw = KubernetesWorkloadRaw.NotFound() };
        var provider = Build(objectClient, new RecordingCredentialPort());

        var status = await provider.GetStatusAsync(Ref());

        status.Phase.Should().Be(WorkloadProviderPhase.NotFound);
        status.ProviderReady.Should().BeFalse();
    }

    [Fact]
    public async Task GetStatus_ready_pods_returns_Ready_and_provider_ready()
    {
        var objectClient = new FakeObjectClient
        {
            StatusRaw = new KubernetesWorkloadRaw
            {
                Found = true,
                Kind = "Deployment",
                DesiredReplicas = 1,
                ReadyReplicas = 1,
                AllPodsRunning = true,
                HasPods = true,
                ObservedImage = "ghcr.io/reeve/matter@" + Digest,
                PodRestartCount = 2,
                Events = new (DateTimeOffset, string, string, bool)[]
                {
                    (DateTimeOffset.UtcNow, "Started", "Started container", false),
                    (DateTimeOffset.UtcNow, "BackOff", "Back-off restarting", true),
                },
            },
        };
        var provider = Build(objectClient, new RecordingCredentialPort());

        var status = await provider.GetStatusAsync(Ref());

        status.Phase.Should().Be(WorkloadProviderPhase.Ready);
        status.ProviderReady.Should().BeTrue();
        status.RestartCount.Should().Be(2);
        status.ObservedImage.Should().Contain("ghcr.io/reeve/matter");
        status.RecentEvents.Should().HaveCount(2);
        status.RecentEvents.Should().Contain(e => e.Severity == WorkloadEventSeverity.Warning);
    }

    [Fact]
    public async Task GetStatus_failed_pods_returns_Failed()
    {
        var objectClient = new FakeObjectClient
        {
            StatusRaw = new KubernetesWorkloadRaw
            {
                Found = true, Kind = "Deployment", DesiredReplicas = 1, ReadyReplicas = 0,
                HasPods = true, AnyPodFailed = true, PodRestartCount = 7,
            },
        };
        var provider = Build(objectClient, new RecordingCredentialPort());

        var status = await provider.GetStatusAsync(Ref());

        status.Phase.Should().Be(WorkloadProviderPhase.Failed);
        status.ProviderReady.Should().BeFalse();
        status.RestartCount.Should().Be(7);
    }

    [Fact]
    public async Task GetStatus_unreachable_returns_Unknown_and_does_not_throw()
    {
        var objectClient = new FakeObjectClient { StatusRaw = null };
        var provider = Build(objectClient, new RecordingCredentialPort());

        var status = await provider.GetStatusAsync(Ref());

        status.Phase.Should().Be(WorkloadProviderPhase.Unknown);
        status.ProviderReady.Should().BeFalse();
    }

    [Fact]
    public async Task GetStatus_with_a_faulting_client_factory_returns_Unknown_and_does_not_throw()
    {
        var provider = Build(new FakeObjectClient(), new RecordingCredentialPort(), clientFactory: new ThrowingClientFactory());

        var act = async () => await provider.GetStatusAsync(Ref());

        var status = await act.Should().NotThrowAsync();
        status.Subject.Phase.Should().Be(WorkloadProviderPhase.Unknown);
        status.Subject.ProviderReady.Should().BeFalse();
    }

    [Fact]
    public async Task Suspend_a_deployment_scales_to_zero()
    {
        var objectClient = new FakeObjectClient { StatusRaw = FoundKind("Deployment") };
        var provider = Build(objectClient, new RecordingCredentialPort());

        await provider.SuspendAsync(Ref());

        objectClient.LastScaleReplicas.Should().Be(0);
        objectClient.DaemonSetSuspendCalled.Should().BeFalse();
    }

    [Fact]
    public async Task Resume_a_deployment_scales_to_one()
    {
        var objectClient = new FakeObjectClient { StatusRaw = FoundKind("Deployment") };
        var provider = Build(objectClient, new RecordingCredentialPort());

        await provider.ResumeAsync(Ref());

        objectClient.LastScaleReplicas.Should().Be(1);
    }

    [Fact]
    public async Task Suspend_a_daemonset_sets_the_no_match_selector()
    {
        var objectClient = new FakeObjectClient { StatusRaw = FoundKind("DaemonSet") };
        var provider = Build(objectClient, new RecordingCredentialPort());

        await provider.SuspendAsync(Ref());

        objectClient.DaemonSetSuspendCalled.Should().BeTrue();
        objectClient.LastDaemonSetSuspended.Should().BeTrue();
        objectClient.LastScaleReplicas.Should().BeNull();
    }

    [Fact]
    public async Task Resume_a_daemonset_clears_the_no_match_selector()
    {
        var objectClient = new FakeObjectClient { StatusRaw = FoundKind("DaemonSet") };
        var provider = Build(objectClient, new RecordingCredentialPort());

        await provider.ResumeAsync(Ref());

        objectClient.DaemonSetSuspendCalled.Should().BeTrue();
        objectClient.LastDaemonSetSuspended.Should().BeFalse();
    }

    [Fact]
    public async Task Suspend_unreachable_does_not_throw_and_does_nothing()
    {
        var objectClient = new FakeObjectClient { StatusRaw = null };
        var provider = Build(objectClient, new RecordingCredentialPort());

        var act = async () => await provider.SuspendAsync(Ref());

        await act.Should().NotThrowAsync();
        objectClient.LastScaleReplicas.Should().BeNull();
        objectClient.DaemonSetSuspendCalled.Should().BeFalse();
    }

    private static KubernetesWorkloadRaw JobsOnly() => new()
    {
        Found = false,
        Jobs = new[] { new KubernetesJobRaw { Name = "reeve-wl-11111111-nodeA-run1", Active = 1 } },
    };

    [Fact]
    public async Task Suspend_a_scan_instance_suspends_its_Jobs()
    {
        var objectClient = new FakeObjectClient { StatusRaw = JobsOnly() };
        var provider = Build(objectClient, new RecordingCredentialPort());

        await provider.SuspendAsync(Ref());

        objectClient.ScanJobsSuspendCallCount.Should().Be(1);
        objectClient.LastScanJobsSuspended.Should().BeTrue();
        objectClient.LastScaleReplicas.Should().BeNull("a scan has no Deployment to scale");
        objectClient.DaemonSetSuspendCalled.Should().BeFalse("a scan has no DaemonSet to hold");
    }

    [Fact]
    public async Task Resume_a_scan_instance_clears_the_hold_on_its_Jobs()
    {
        var objectClient = new FakeObjectClient { StatusRaw = JobsOnly() };
        var provider = Build(objectClient, new RecordingCredentialPort());

        await provider.ResumeAsync(Ref());

        objectClient.ScanJobsSuspendCallCount.Should().Be(1);
        objectClient.LastScanJobsSuspended.Should().BeFalse();
    }

    [Fact]
    public async Task Suspend_an_absent_instance_writes_nothing()
    {
        var objectClient = new FakeObjectClient { StatusRaw = KubernetesWorkloadRaw.NotFound() };
        var provider = Build(objectClient, new RecordingCredentialPort());

        await provider.SuspendAsync(Ref());

        objectClient.ScanJobsSuspendCallCount.Should().Be(0);
        objectClient.LastScaleReplicas.Should().BeNull();
        objectClient.DaemonSetSuspendCalled.Should().BeFalse();
    }

    [Fact]
    public async Task Suspend_unreachable_does_not_reach_the_scan_seam()
    {
        var objectClient = new FakeObjectClient { StatusRaw = null };
        var provider = Build(objectClient, new RecordingCredentialPort());

        await provider.SuspendAsync(Ref());

        objectClient.ScanJobsSuspendCallCount.Should().Be(0);
    }

    [Fact]
    public async Task Suspend_swallows_a_throw_from_the_scan_seam()
    {
        var objectClient = new FakeObjectClient
        {
            StatusRaw = JobsOnly(),
            ScanJobsSuspendThrows = new InvalidOperationException("boom"),
        };
        var provider = Build(objectClient, new RecordingCredentialPort());

        var act = async () => await provider.SuspendAsync(Ref());

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task Suspend_a_deployment_that_still_has_stale_scan_Jobs_scales_the_deployment()
    {
        var objectClient = new FakeObjectClient
        {
            StatusRaw = FoundKind("Deployment") with
            {
                Jobs = new[] { new KubernetesJobRaw { Name = "reeve-wl-11111111-nodeA-run1", Active = 1 } },
            },
        };
        var provider = Build(objectClient, new RecordingCredentialPort());

        await provider.SuspendAsync(Ref());

        objectClient.LastScaleReplicas.Should().Be(0);
        objectClient.ScanJobsSuspendCallCount.Should().Be(0);
    }

    [Fact]
    public async Task Suspend_unreachable_is_reported_as_a_no_op_not_a_failure()
    {
        var recordingLogger = new RecordingLogger();
        var objectClient = new FakeObjectClient { StatusRaw = null };
        var provider = Build(objectClient, new RecordingCredentialPort(), logger: recordingLogger);

        await provider.SuspendAsync(Ref());

        recordingLogger.Records.Should().NotContain(r => r.Level >= LogLevel.Warning);
    }

    private sealed class RecordingLogger : ILogger<KubernetesWorkloadProvider>
    {
        public List<(LogLevel Level, string Message)> Records { get; } = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => Records.Add((logLevel, formatter(state, exception)));
    }

    [Fact]
    public async Task Preflight_delegates_to_inspector_and_checker()
    {
        var provider = Build(new FakeObjectClient(), new RecordingCredentialPort());

        var result = await provider.PreflightAsync(Spec());

        result.Should().NotBeNull();
        result.Checks.Should().NotBeEmpty();
    }

    [Fact]
    public async Task Preflight_for_a_scan_spec_threads_the_object_client_ReadyNodeNames_onto_the_result()
    {
        var objectClient = new FakeObjectClient { ReadyNodeNames = new[] { "node-a", "node-b" } };
        var provider = Build(objectClient, new RecordingCredentialPort(), clientFactory: new StubClientFactory());

        var result = await provider.PreflightAsync(Spec(kind: WorkloadKind.ScanPerNode));

        result.HasBlockingError.Should().BeFalse("a reachable cluster with readable nodes has no blocking check");
        objectClient.ListReadyNodesCalled.Should().BeTrue("the scan branch must enumerate Ready nodes through the object client");
        result.ReadyNodeNames.Should().NotBeNull();
        result.ReadyNodeNames.Should().Equal(new[] { "node-a", "node-b" },
            "the enumerated Ready set must be threaded onto the result (not dropped)");
    }

    [Fact]
    public async Task Preflight_threads_the_specs_node_eligibility_selector_into_the_node_read_and_DISCARDS_the_excluded_side()
    {
        var selector = new Dictionary<string, string> { ["reeve-iot.dev/vlan100"] = "true" };
        var objectClient = new FakeObjectClient
        {
            ReadyNodeNames = new[] { "node-a", "node-b" },
            IneligibleNodeNames = new[] { "node-no-vlan" },
        };
        var provider = Build(objectClient, new RecordingCredentialPort(), clientFactory: new StubClientFactory());

        var spec = Spec(kind: WorkloadKind.ScanPerNode) with { NodeSelector = selector };
        var result = await provider.PreflightAsync(spec);

        objectClient.LastNodeEligibilitySelector.Should().BeEquivalentTo(selector,
            "the node read is the only seam that sees node LABELS, so the provider must thread the resolved selector into it");
        result.ReadyNodeNames.Should().Equal(new[] { "node-a", "node-b" },
            "the MATCHING side is assigned to ReadyNodeNames exactly as before the widening");
        result.ReadyNodeNames.Should().NotContain("node-no-vlan",
            "the excluded side is discarded at this seam — it must not leak into the eligible set");
    }

    [Fact]
    public async Task Preflight_for_a_scan_spec_whose_node_read_faults_reports_a_blocking_error_and_leaves_ReadyNodeNames_null()
    {
        var objectClient = new FakeObjectClient
        {
            ListReadyNodesThrows = new InvalidOperationException("403 Forbidden on cluster-scoped nodes"),
        };
        var provider = Build(objectClient, new RecordingCredentialPort(), clientFactory: new StubClientFactory());

        var result = await provider.PreflightAsync(Spec(kind: WorkloadKind.ScanPerNode));

        objectClient.ListReadyNodesCalled.Should().BeTrue("the branch must have attempted the enumeration");
        result.HasBlockingError.Should().BeTrue(
            "a node-read fault must be a LOUD scan failure, never a silent zero-eligible success");
        result.ReadyNodeNames.Should().BeNull("no Ready set could be enumerated — null, never an empty NoTargets set");
        result.Checks.Should().Contain(
            c => c.Id == "scan-nodes-readable" && !c.Passed && c.Severity == PreflightSeverity.Error,
            "the scan-nodes-readable check must be rebuilt as a blocking Error on the enumeration fault");
        result.Checks.Count(c => c.Id == "scan-nodes-readable").Should().Be(1,
            "the original passing scan-nodes-readable check must be REPLACED, not duplicated");
    }

    [Fact]
    public async Task Preflight_for_a_non_scan_spec_never_enumerates_nodes_and_leaves_ReadyNodeNames_null()
    {
        var objectClient = new FakeObjectClient { ReadyNodeNames = new[] { "node-a" } };
        var provider = Build(objectClient, new RecordingCredentialPort(), clientFactory: new StubClientFactory());

        var result = await provider.PreflightAsync(Spec(kind: WorkloadKind.SingleNode));

        objectClient.ListReadyNodesCalled.Should().BeFalse("a non-scan spec must not enumerate Ready nodes");
        result.ReadyNodeNames.Should().BeNull("a non-scan preflight leaves ReadyNodeNames null");
    }

    [Fact]
    public async Task Logs_delegate_to_the_reader()
    {
        var logReader = new FakeLogReader
        {
            Result = new WorkloadLogs { Lines = new[] { WorkloadLogLine.Line("hello") } },
        };
        var provider = Build(new FakeObjectClient(), new RecordingCredentialPort(), logReader: logReader);

        var logs = await provider.GetRecentLogsAsync(Ref(), tailLines: 50, previousContainer: true);

        logs.Lines.Should().ContainSingle(l => l.Text == "hello");
        logReader.LastTail.Should().Be(50);
        logReader.LastPrevious.Should().BeTrue();
    }

    [Fact]
    public async Task Logs_with_a_faulting_client_factory_return_Empty_and_do_not_throw()
    {
        var provider = Build(new FakeObjectClient(), new RecordingCredentialPort(), clientFactory: new ThrowingClientFactory());

        var act = async () => await provider.GetRecentLogsAsync(Ref(), tailLines: 50, previousContainer: false);

        var logs = await act.Should().NotThrowAsync();
        logs.Subject.Lines.Should().BeEmpty();
    }

    [Fact]
    public async Task Apply_with_a_faulting_client_factory_returns_ProviderUnreachable_and_does_not_throw()
    {
        var provider = Build(new FakeObjectClient(), new RecordingCredentialPort(), clientFactory: new ThrowingClientFactory());

        var act = async () => await provider.ApplyAsync(Spec(), progress: null);

        var result = await act.Should().NotThrowAsync();
        result.Subject.Succeeded.Should().BeFalse();
        result.Subject.Reason.Should().Be(WorkloadApplyReason.ProviderUnreachable);
    }

    [Fact]
    public async Task Teardown_with_a_faulting_client_factory_throws()
    {
        var provider = Build(new FakeObjectClient(), new RecordingCredentialPort(), clientFactory: new ThrowingClientFactory());

        var act = async () => await provider.TeardownAsync(Ref(), retainVolumes: false);

        await act.Should().ThrowAsync<Exception>();
    }

    [Fact]
    public async Task Suspend_with_a_faulting_client_factory_does_not_throw()
    {
        var provider = Build(new FakeObjectClient(), new RecordingCredentialPort(), clientFactory: new ThrowingClientFactory());

        var act = async () => await provider.SuspendAsync(Ref());

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task Preflight_with_a_faulting_client_factory_reports_api_unreachable_and_does_not_throw()
    {
        var provider = Build(new FakeObjectClient(), new RecordingCredentialPort(), clientFactory: new ThrowingClientFactory());

        var act = async () => await provider.PreflightAsync(Spec());

        var result = await act.Should().NotThrowAsync();
        result.Subject.Should().NotBeNull();
        result.Subject.Checks.Should().NotBeEmpty();
        result.Subject.Checks.Should().Contain(c => c.Severity == PreflightSeverity.Error,
            "a faulting factory means the API is unreachable → the preflight surfaces a failing check, not a throw");
    }

    private static KubernetesWorkloadRaw FoundKind(string kind) => new()
    {
        Found = true,
        Kind = kind,
        DesiredReplicas = 1,
        ReadyReplicas = 1,
        HasPods = true,
        AllPodsRunning = true,
    };

    [Fact]
    public void KubernetesWorkloadRaw_carries_per_node_job_statuses_with_exact_counts()
    {
        var completedAt = new DateTimeOffset(2026, 7, 19, 12, 0, 0, TimeSpan.Zero);

        var raw = new KubernetesWorkloadRaw
        {
            Found = true,
            Jobs = new[]
            {
                new KubernetesJobRaw { Name = "reeve-wl-abcd-nodeA-run1", Succeeded = 1, Failed = 0, Active = 0, CompletionTime = completedAt },
                new KubernetesJobRaw { Name = "reeve-wl-abcd-nodeB-run1", Succeeded = 0, Failed = 0, Active = 1, CompletionTime = null },
            },
        };

        raw.Jobs.Should().HaveCount(2);

        var completed = raw.Jobs.Single(j => j.Name == "reeve-wl-abcd-nodeA-run1");
        completed.Succeeded.Should().Be(1);
        completed.Failed.Should().Be(0);
        completed.Active.Should().Be(0);
        completed.CompletionTime.Should().Be(completedAt);

        var running = raw.Jobs.Single(j => j.Name == "reeve-wl-abcd-nodeB-run1");
        running.Succeeded.Should().Be(0);
        running.Active.Should().Be(1);
        running.CompletionTime.Should().BeNull();
    }

    [Fact]
    public void KubernetesWorkloadRaw_Jobs_defaults_to_empty_not_null_for_idle_instance()
    {
        KubernetesWorkloadRaw.NotFound().Jobs.Should().NotBeNull().And.BeEmpty();
        new KubernetesWorkloadRaw().Jobs.Should().NotBeNull();
        new KubernetesWorkloadRaw().Jobs.Count.Should().Be(0);
    }

    [Fact]
    public void KubernetesObjectKind_Job_is_defined_at_ordinal_five_without_renumbering_existing_kinds()
    {
        Enum.IsDefined(typeof(KubernetesObjectKind), KubernetesObjectKind.Job).Should().BeTrue();
        ((int)KubernetesObjectKind.Job).Should().Be(5);
        ((int)KubernetesObjectKind.Deployment).Should().Be(0);
        ((int)KubernetesObjectKind.DaemonSet).Should().Be(1);
        ((int)KubernetesObjectKind.Service).Should().Be(2);
        ((int)KubernetesObjectKind.Secret).Should().Be(3);
        ((int)KubernetesObjectKind.PersistentVolumeClaim).Should().Be(4);
    }

    [Fact]
    public void RenderedKubernetesObjects_carries_a_zero_to_n_Jobs_collection_defaulting_empty()
    {
        var secret = new V1Secret { Metadata = new V1ObjectMeta { Name = "reeve-wl-abcd-token" } };

        new RenderedKubernetesObjects { Secret = secret }.Jobs.Should().NotBeNull().And.BeEmpty();

        var rendered = new RenderedKubernetesObjects
        {
            Secret = secret,
            Jobs = new[] { new V1Job { Metadata = new V1ObjectMeta { Name = "reeve-wl-abcd-nodeA-run1" } } },
        };

        rendered.Jobs.Should().ContainSingle().Which.Metadata!.Name.Should().Be("reeve-wl-abcd-nodeA-run1");
    }

    [Fact]
    public async Task Apply_scan_without_a_pending_marker_creates_zero_jobs_and_settles_the_hash()
    {
        var objectClient = new FakeObjectClient();
        var provider = Build(objectClient, new RecordingCredentialPort());

        var result = await provider.ApplyAsync(Spec(kind: WorkloadKind.ScanPerNode), progress: null);

        result.Succeeded.Should().BeTrue();
        result.Reason.Should().Be(WorkloadApplyReason.Applied);
        result.AppliedSpecHash.Should().Be("abc123def456");
        objectClient.AppliedKinds.Should().NotContain(KubernetesObjectKind.Job);
        objectClient.AppliedJobNames.Should().BeEmpty("no marker ⇒ zero Jobs (spec-drift never scans)");
        objectClient.ScanDeleteExceptRunIds.Should().BeEmpty("no marker ⇒ no pre-run reconcile is attempted");
    }

    [Fact]
    public async Task Apply_scan_with_a_pending_marker_creates_one_job_per_ready_node_carrying_the_run_id()
    {
        var objectClient = new FakeObjectClient();
        var provider = Build(objectClient, new RecordingCredentialPort());

        var result = await provider.ApplyAsync(
            Spec(kind: WorkloadKind.ScanPerNode, pendingScanRunId: "run-1",
                scanReadyNodeNames: new[] { "node-a", "node-b", "node-c" }),
            progress: null);

        result.Succeeded.Should().BeTrue();
        result.AppliedSpecHash.Should().Be("abc123def456");
        objectClient.AppliedJobNames.Should().HaveCount(3, "one Job per Ready node");
        objectClient.AppliedJobNames.Distinct().Should().HaveCount(3, "the per-node discriminator makes each name unique");
        objectClient.AppliedJobNames.Should().OnlyContain(n => n.Contains("run-1"), "the current run-id rides the Job name");
    }

    [Fact]
    public async Task Apply_scan_sweeps_past_run_jobs_before_creating_the_current_run_jobs()
    {
        var objectClient = new FakeObjectClient();
        var provider = Build(objectClient, new RecordingCredentialPort());

        await provider.ApplyAsync(
            Spec(kind: WorkloadKind.ScanPerNode, pendingScanRunId: "run-1",
                scanReadyNodeNames: new[] { "node-a", "node-b" }),
            progress: null);

        objectClient.ScanDeleteExceptRunIds.Should().ContainSingle().Which.Should().Be("run-1",
            "the pre-run sweep excludes (preserves) the CURRENT run via exceptRunId");

        var deleteIndex = objectClient.ScanCallLog.FindIndex(e => e.StartsWith("delete:", StringComparison.Ordinal));
        var firstJobIndex = objectClient.ScanCallLog.FindIndex(e => e.StartsWith("ssa:Job:", StringComparison.Ordinal));
        deleteIndex.Should().BeGreaterThanOrEqualTo(0, "the past-run sweep must be issued");
        firstJobIndex.Should().BeGreaterThan(deleteIndex, "past-run delete must happen BEFORE any current-run Job SSA");
    }

    [Fact]
    public async Task Apply_scan_retry_reapplies_all_run_id_jobs_as_idempotent_ssa_no_ops()
    {
        var objectClient = new FakeObjectClient();
        var provider = Build(objectClient, new RecordingCredentialPort());
        var spec = Spec(kind: WorkloadKind.ScanPerNode, pendingScanRunId: "run-7",
            scanReadyNodeNames: new[] { "node-a", "node-b", "node-c" });

        var first = await provider.ApplyAsync(spec, progress: null);
        var second = await provider.ApplyAsync(spec, progress: null);

        first.Succeeded.Should().BeTrue();
        second.Succeeded.Should().BeTrue("re-apply of the same run-id Jobs is an idempotent no-op success, never a fault");
        second.AppliedSpecHash.Should().Be("abc123def456");
        objectClient.AppliedJobNames.Should().HaveCount(6, "each pass re-applies all three run-id Jobs via SSA");
        objectClient.AppliedJobNames.Distinct().Should().HaveCount(3, "the same three run-id Job names re-apply each pass");
    }

    [Fact]
    public async Task Apply_scan_never_deletes_or_replaces_a_current_run_job_in_place()
    {
        var objectClient = new FakeObjectClient();
        var provider = Build(objectClient, new RecordingCredentialPort());

        await provider.ApplyAsync(
            Spec(kind: WorkloadKind.ScanPerNode, pendingScanRunId: "run-1", scanReadyNodeNames: new[] { "node-a" }),
            progress: null);

        objectClient.ScanDeleteExceptRunIds.Should().OnlyContain(r => r == "run-1",
            "every sweep preserves the current run — the current-run Job is never swept");
        objectClient.AppliedJobNames.Should().ContainSingle("the single node's Job is SSA-created once, not delete+recreated");
        objectClient.DeleteCalled.Should().BeFalse("no teardown-style DeleteWorkloadByLabel of the live objects during a scan apply");
    }

    [Fact]
    public async Task Existence_of_a_listed_nad_is_read_and_an_unlisted_one_is_missing()
    {
        var objectClient = new FakeObjectClient
        {
            NetworkAttachmentList = KubernetesNetworkAttachmentListResult.Populated(
                [new KubernetesNetworkAttachment { Name = "present" }]),
        };
        var provider = Build(objectClient, new RecordingCredentialPort());

        var existence = await provider.GetAttachmentExistenceAsync(["present", "gone"]);

        existence.Status.Should().Be(WorkloadAttachmentExistenceStatus.Read);
        existence.MissingAttachments.Should().Equal("gone");

        var allThere = await provider.GetAttachmentExistenceAsync(["present"]);
        allThere.Status.Should().Be(WorkloadAttachmentExistenceStatus.Read);
        allThere.MissingAttachments.Should().BeEmpty();
        objectClient.NetworkAttachmentListCalls.Should().Be(2, "one listing per existence read, never one read per name");
    }

    [Theory]
    [InlineData(KubernetesNetworkAttachmentUnavailableReasons.Forbidden)]
    [InlineData(KubernetesNetworkAttachmentUnavailableReasons.ProviderUnavailable)]
    public async Task A_forbidden_nad_listing_is_unreadable_never_missing(string reason)
    {
        var objectClient = new FakeObjectClient
        {
            NetworkAttachmentList = KubernetesNetworkAttachmentListResult.Unavailable(reason),
        };

        var existence = await Build(objectClient, new RecordingCredentialPort())
            .GetAttachmentExistenceAsync(["present", "other"]);

        existence.Status.Should().Be(WorkloadAttachmentExistenceStatus.Unreadable);
        existence.MissingAttachments.Should().BeEmpty();
    }

    [Fact]
    public async Task A_faulting_nad_listing_is_unreadable_never_missing()
    {
        var objectClient = new FakeObjectClient { NetworkAttachmentListFault = new InvalidOperationException("boom") };

        var existence = await Build(objectClient, new RecordingCredentialPort())
            .GetAttachmentExistenceAsync(["present"]);

        existence.Status.Should().Be(WorkloadAttachmentExistenceStatus.Unreadable);
        existence.MissingAttachments.Should().BeEmpty();
    }

    [Theory]
    [InlineData(KubernetesNetworkAttachmentUnavailableReasons.MultusNotInstalled)]
    [InlineData(KubernetesNetworkAttachmentUnavailableReasons.NoneInNamespace)]
    public async Task An_absent_multus_crd_reports_every_name_missing(string reason)
    {
        var objectClient = new FakeObjectClient
        {
            NetworkAttachmentList = KubernetesNetworkAttachmentListResult.Unavailable(reason),
        };

        var existence = await Build(objectClient, new RecordingCredentialPort())
            .GetAttachmentExistenceAsync(["present", "other"]);

        existence.Status.Should().Be(WorkloadAttachmentExistenceStatus.Read);
        existence.MissingAttachments.Should().Equal("present", "other");
    }

    private sealed class FixedClientFactory(IKubernetes client) : IKubernetesClientFactory
    {
        public Task<IKubernetes> CreateClientAsync(CancellationToken cancellationToken = default) => Task.FromResult(client);
    }

    private sealed class FakeClientFactory : IKubernetesClientFactory
    {
        public Task<IKubernetes> CreateClientAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IKubernetes>(null!);
    }

    private sealed class ThrowingClientFactory : IKubernetesClientFactory
    {
        public Task<IKubernetes> CreateClientAsync(CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("no in-cluster config");
    }

    private sealed class StubClientFactory : IKubernetesClientFactory
    {
        public Task<IKubernetes> CreateClientAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(Substitute.For<IKubernetes>());
    }

    private sealed class FakeSettingsService : IWorkloadOrchestratorSettingsService
    {
        public string? Namespace { get; init; } = Ns;
        public bool AllowUnsupportedOverrides { get; init; }
        public string? ImagePullSecretName { get; init; }

        public Task<WorkloadOrchestratorSettingsResponse> GetAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(new WorkloadOrchestratorSettingsResponse
            {
                Settings = new WorkloadOrchestratorSettings
                {
                    Provider = KubernetesProvider.Id,
                }
                    .WithProvider(KubernetesProvider.Id, new KubernetesProviderSettings
                    {
                        Namespace = Namespace,
                        AllowUnsupportedOverrides = AllowUnsupportedOverrides,
                        ImagePullSecretName = ImagePullSecretName,
                    }),
                EffectiveProvider = KubernetesProvider.Id,
                AutoDetected = false,
                Providers = Array.Empty<WorkloadProviderInfo>(),
            });


        public Task<WorkloadProviderProbeResult> TestAsync(WorkloadProviderId? requestedProvider, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }

    private sealed class FakeClusterInspector : IKubernetesClusterInspector
    {
        public Task<KubernetesClusterInspection> InspectAsync(IKubernetes client, string @namespace, CancellationToken ct)
            => Task.FromResult(new KubernetesClusterInspection { ApiReachable = true });
    }

    private sealed class FakeLogReader : IKubernetesLogReader
    {
        public WorkloadLogs Result { get; init; } = WorkloadLogs.Empty();
        public int LastTail { get; private set; }
        public bool LastPrevious { get; private set; }

        public Task<WorkloadLogs> ReadAsync(IKubernetes client, string @namespace, WorkloadRef reference, int tailLines, bool previousContainer, CancellationToken ct)
        {
            LastTail = tailLines;
            LastPrevious = previousContainer;
            return Task.FromResult(Result);
        }
    }

    internal sealed class FakeObjectClient : IKubernetesObjectClient
    {
        public List<KubernetesObjectKind> AppliedKinds { get; } = new();

        public List<(KubernetesObjectKind Kind, bool Force)> AppliedForce { get; } = new();

        public List<string> AppliedJobNames { get; } = new();

        public List<string?> ScanDeleteExceptRunIds { get; } = new();

        public List<string> ScanCallLog { get; } = new();

        public string? LastSecretManifest { get; private set; }
        public string? LastDeploymentManifest { get; private set; }
        public string? LastDaemonSetManifest { get; private set; }

        public string? LastDeploymentNamespace { get; private set; }
        public List<(string Name, string Manifest)> AppliedSecrets { get; } = new();
        public List<string> EnsuredPvcNames { get; } = new();

        public IAsyncEnumerable<WorkloadWatchEvent> WatchWorkloadsAsync(
            IKubernetes client, string @namespace, string labelSelector, CancellationToken ct)
            => throw new NotSupportedException("the workload watch is not exercised by these tests");

        public List<(string Name, bool Adopted)> EnsuredPvcs { get; } = new();

        public bool ThrowOnAdoptedPvcEnsure { get; init; }

        public HashSet<string> TerminatingPvcNames { get; } = new(StringComparer.Ordinal);

        public string? ExistingSecretToken { get; init; }

        public Dictionary<string, string> SecretValues { get; init; } = new(StringComparer.Ordinal);

        public Exception? ThrowOnReadSecret { get; init; }

        public List<IReadOnlyList<string>> SecretReads { get; } = new();

        public KubernetesObjectKind? ConflictOnKind { get; init; }
        public string? ConflictingManager { get; init; }
        public bool UnreachableOnAll { get; init; }

        public KubernetesObjectKind? RejectOnKind { get; init; }
        public string RejectionMessage { get; init; } = "the API server refused the object";

        public KubernetesWorkloadRaw? StatusRaw { get; init; } = KubernetesWorkloadRaw.NotFound();

        public bool DeleteCalled { get; private set; }
        public bool? LastDeleteRetainPvc { get; private set; }
        public bool ThrowOnDelete { get; init; }

        public int? LastScaleReplicas { get; private set; }
        public bool DaemonSetSuspendCalled { get; private set; }
        public bool? LastDaemonSetSuspended { get; private set; }

        public int ScanJobsSuspendCallCount { get; private set; }
        public bool? LastScanJobsSuspended { get; private set; }
        public Exception? ScanJobsSuspendThrows { get; init; }

        public Task<KubernetesApplyOutcome> ServerSideApplyAsync(
            IKubernetes client, string @namespace, KubernetesObjectKind kind, string name, string manifestJson,
            string fieldManager, bool force, CancellationToken ct)
        {
            if (UnreachableOnAll)
            {
                return Task.FromResult(KubernetesApplyOutcome.UnreachableOutcome("connection refused"));
            }

            if (ConflictOnKind == kind)
            {
                return Task.FromResult(KubernetesApplyOutcome.Conflict(ConflictingManager));
            }

            if (RejectOnKind == kind)
            {
                return Task.FromResult(KubernetesApplyOutcome.RejectedOutcome(RejectionMessage));
            }

            if (kind is not (KubernetesObjectKind.Deployment or KubernetesObjectKind.DaemonSet
                or KubernetesObjectKind.Service or KubernetesObjectKind.Secret or KubernetesObjectKind.Job))
            {
                return Task.FromResult(KubernetesApplyOutcome.ErrorOutcome($"unknown object kind {kind}"));
            }

            AppliedKinds.Add(kind);
            AppliedForce.Add((kind, force));
            if (kind == KubernetesObjectKind.Secret)
            {
                LastSecretManifest = manifestJson;
                AppliedSecrets.Add((name, manifestJson));
            }
            if (kind == KubernetesObjectKind.Deployment)
            {
                LastDeploymentManifest = manifestJson;
                LastDeploymentNamespace = @namespace;
            }
            if (kind == KubernetesObjectKind.DaemonSet)
            {
                LastDaemonSetManifest = manifestJson;
            }
            if (kind == KubernetesObjectKind.Job)
            {
                AppliedJobNames.Add(name);
                ScanCallLog.Add($"ssa:Job:{name}");
            }
            return Task.FromResult(KubernetesApplyOutcome.Ok());
        }

        public Task<IReadOnlyDictionary<string, string>> ReadSecretValuesAsync(
            IKubernetes client, string @namespace, string secretName, IReadOnlyCollection<string> keys, CancellationToken ct)
        {
            SecretReads.Add(keys.ToList());
            if (ThrowOnReadSecret is { } ex)
            {
                throw ex;
            }

            var values = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var key in keys)
            {
                if ((SecretValues.TryGetValue(key, out var value) ? value : key == "REEVE_WORKLOAD_TOKEN" ? ExistingSecretToken : null) is { } held)
                {
                    values[key] = held;
                }
            }

            return Task.FromResult<IReadOnlyDictionary<string, string>>(values);
        }

        public Task EnsurePvcAsync(IKubernetes client, string @namespace, V1PersistentVolumeClaim pvc, bool isAdoptedClaim, CancellationToken ct)
        {
            var name = pvc.Metadata?.Name ?? "";
            EnsuredPvcNames.Add(name);
            EnsuredPvcs.Add((name, isAdoptedClaim));

            if (isAdoptedClaim && ThrowOnAdoptedPvcEnsure)
            {
                throw new InvalidOperationException(
                    $"Restore binding requires the retained PVC '{name}' to already exist, but it is absent (test).");
            }

            if (TerminatingPvcNames.Contains(name))
            {
                throw new KubernetesPvcTerminatingException(name, @namespace);
            }

            return Task.CompletedTask;
        }

        public Task<KubernetesWorkloadRaw?> ReadWorkloadAsync(IKubernetes client, string @namespace, IReadOnlyDictionary<string, string> selectorLabels, CancellationToken ct)
            => Task.FromResult(StatusRaw);

        public Task DeleteWorkloadByLabelAsync(IKubernetes client, string @namespace, IReadOnlyDictionary<string, string> selectorLabels, bool retainPvc, CancellationToken ct)
        {
            if (ThrowOnDelete)
            {
                throw new InvalidOperationException("connection refused");
            }

            DeleteCalled = true;
            LastDeleteRetainPvc = retainPvc;
            return Task.CompletedTask;
        }

        public List<KubernetesObjectKind> OtherKindDeletes { get; } = new();

        public bool ThrowOnOtherKindDelete { get; set; }

        public Task DeleteOtherWorkloadKindsAsync(IKubernetes client, string @namespace, IReadOnlyDictionary<string, string> selectorLabels, KubernetesObjectKind keptKind, CancellationToken ct)
        {
            if (ThrowOnOtherKindDelete)
            {
                throw new InvalidOperationException("connection refused");
            }

            OtherKindDeletes.Add(keptKind);
            ScanCallLog.Add($"delete-other-kinds:{keptKind}");
            return Task.CompletedTask;
        }

        public Task DeleteScanJobsByLabelAsync(IKubernetes client, string @namespace, IReadOnlyDictionary<string, string> selectorLabels, string? exceptRunId, CancellationToken ct)
        {
            ScanDeleteExceptRunIds.Add(exceptRunId);
            ScanCallLog.Add($"delete:{exceptRunId}");
            return Task.CompletedTask;
        }

        public Task ScaleDeploymentAsync(IKubernetes client, string @namespace, IReadOnlyDictionary<string, string> selectorLabels, int replicas, CancellationToken ct)
        {
            LastScaleReplicas = replicas;
            return Task.CompletedTask;
        }

        public Task SetDaemonSetSuspendedAsync(IKubernetes client, string @namespace, IReadOnlyDictionary<string, string> selectorLabels, bool suspended, string inst8, CancellationToken ct)
        {
            DaemonSetSuspendCalled = true;
            LastDaemonSetSuspended = suspended;
            return Task.CompletedTask;
        }

        public Task SetScanJobsSuspendedAsync(IKubernetes client, string @namespace, IReadOnlyDictionary<string, string> selectorLabels, bool suspended, CancellationToken ct)
        {
            if (ScanJobsSuspendThrows is not null)
            {
                throw ScanJobsSuspendThrows;
            }

            ScanJobsSuspendCallCount++;
            LastScanJobsSuspended = suspended;
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<AdoptableResource>> ListAdoptableAsync(IKubernetes client, string @namespace, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<AdoptableResource>>(Array.Empty<AdoptableResource>());

        public IReadOnlyList<string> ReadyNodeNames { get; set; } = Array.Empty<string>();

        public IReadOnlyList<string> IneligibleNodeNames { get; set; } = Array.Empty<string>();

        public IReadOnlyDictionary<string, string>? LastNodeEligibilitySelector { get; private set; }

        public Exception? ListReadyNodesThrows { get; set; }
        public bool ListReadyNodesCalled { get; private set; }

        public Task<KubernetesReadyNodePartition> ListReadyNodesAsync(
            IKubernetes client, IReadOnlyDictionary<string, string> nodeEligibilitySelector, CancellationToken ct)
        {
            ListReadyNodesCalled = true;
            LastNodeEligibilitySelector = nodeEligibilitySelector;
            if (ListReadyNodesThrows is not null)
            {
                throw ListReadyNodesThrows;
            }

            return Task.FromResult(new KubernetesReadyNodePartition
            {
                Matching = ReadyNodeNames,
                Excluded = IneligibleNodeNames,
            });
        }

        public Task<KubernetesNetworkAttachmentListResult> ListNetworkAttachmentDefinitionsAsync(
            IKubernetes client, string @namespace, CancellationToken ct)
        {
            NetworkAttachmentListCalls++;
            return NetworkAttachmentListFault is { } fault
                ? Task.FromException<KubernetesNetworkAttachmentListResult>(fault)
                : Task.FromResult(NetworkAttachmentList);
        }

        public KubernetesNetworkAttachmentListResult NetworkAttachmentList { get; set; } =
            KubernetesNetworkAttachmentListResult.Unavailable(KubernetesNetworkAttachmentUnavailableReasons.NoneInNamespace);

        public Exception? NetworkAttachmentListFault { get; set; }

        public int NetworkAttachmentListCalls { get; private set; }

        public Task PatchResourceMetadataAsync(
            IKubernetes client, string @namespace, KubernetesObjectKind kind, string name,
            IReadOnlyDictionary<string, string> labelsToSet, IReadOnlyList<string> labelsToRemove,
            IReadOnlyList<string> annotationsToRemove, CancellationToken ct)
            => Task.CompletedTask;

        public Task<string?> ReadObjectManifestAsync(
            IKubernetes client, string @namespace, KubernetesObjectKind kind, string name, CancellationToken ct)
            => Task.FromResult<string?>(null);
    }

    private sealed class FakeImagePullResolver : IWorkloadImageRegistryCredentialResolver
    {
        private readonly string? host;
        private readonly RegistryCredential? credential;

        public FakeImagePullResolver(string? matchHost, RegistryCredential? credential)
        {
            this.host = matchHost;
            this.credential = credential;
        }

        public List<string> AskedHosts { get; } = new();

        public Task<RegistryCredential?> ResolveAsync(string registryHost, CancellationToken cancellationToken = default)
        {
            AskedHosts.Add(registryHost);
            return Task.FromResult(
                string.Equals(registryHost, host, StringComparison.OrdinalIgnoreCase) ? credential : null);
        }
    }
}
