using Reeve.Workloads.Domain;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using k8s;
using k8s.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Reeve.Workloads.Abstractions;
using Reeve.Workloads.Abstractions.Contracts;
using Reeve.Workloads.Abstractions.Domain;
using Reeve.Workloads.Core.Adoption;
using Reeve.Workloads.Providers.Kubernetes;
using Reeve.Workloads.Providers.Kubernetes.Adoption;
using Xunit;

using Reeve.Workloads.Abstractions.Providers;

namespace Reeve.Workloads.Providers.Kubernetes.Tests;

public sealed class KubernetesWorkloadAdoptionServiceTests
{
    private const string Ns = "reeve";
    private const string ThisInstallation = "install-this";
    private const string ForeignInstallation = "install-previous";
    private static readonly string InstanceA = Guid.Parse("aaaaaaaa-1111-2222-3333-444444444444").ToString("D");
    private static readonly string InstanceB = Guid.Parse("bbbbbbbb-1111-2222-3333-444444444444").ToString("D");
    private static readonly string InstanceOwn = Guid.Parse("cccccccc-1111-2222-3333-444444444444").ToString("D");
    private const string Image = "ghcr.io/reeve/matter@sha256:1111111111111111111111111111111111111111111111111111111111111111";

    [Fact]
    public async Task Detect_classifies_foreign_installation_and_manual_managed_and_excludes_own()
    {
        var client = new FakeObjectClient
        {
            Adoptable =
            {
                Deployment(InstanceA, installation: ForeignInstallation),
                Pvc(InstanceA, installation: ForeignInstallation, name: "pvc-a"),

                DeploymentManual(InstanceB, installation: ThisInstallation),

                Deployment(InstanceOwn, installation: ThisInstallation),
            },
        };

        var service = Build(client);

        var candidates = await service.DetectAsync(CancellationToken.None);

        candidates.Should().HaveCount(2, "own managed resources are not adoption candidates");

        var foreign = candidates.Single(c => c.InstanceLabel == InstanceA);
        foreign.Class.Should().Be(AdoptionClass.ForeignInstallation);
        foreign.InstallationLabel.Should().Be(ForeignInstallation);
        foreign.Resources.Should().HaveCount(2, "the Deployment and its PVC are grouped by the instance label");

        var manual = candidates.Single(c => c.InstanceLabel == InstanceB);
        manual.Class.Should().Be(AdoptionClass.ManualManaged);

        candidates.Should().NotContain(c => c.InstanceLabel == InstanceOwn);
    }

    [Fact]
    public async Task Detect_returns_empty_when_provider_unreachable()
    {
        var client = new FakeObjectClient { Unreachable = true };
        var service = Build(client);

        var candidates = await service.DetectAsync(CancellationToken.None);

        candidates.Should().BeEmpty("adoption is never offered blind against an unreachable API server");
    }

    [Fact]
    public async Task VerifyEvidence_passes_when_labels_image_and_pvc_binding_all_match()
    {
        var client = new FakeObjectClient
        {
            Adoptable =
            {
                Deployment(InstanceA, installation: ForeignInstallation, image: Image),
                Pvc(InstanceA, installation: ForeignInstallation, name: "pvc-a"),
            },
        };
        var service = Build(client);
        var candidate = (await service.DetectAsync(CancellationToken.None)).Single();

        var evidence = new AdoptionEvidence
        {
            ExpectedLabels = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [WorkloadResourceLabels.InstanceLabelKey] = InstanceA,
            },
            ExpectedImage = Image,
            ExpectedPvcNames = new[] { "pvc-a" },
        };

        var result = await service.VerifyEvidenceAsync(candidate, evidence, CancellationToken.None);

        result.Verified.Should().BeTrue();
        result.Failures.Should().BeEmpty();
    }

    [Fact]
    public async Task VerifyEvidence_refuses_empty_evidence()
    {
        var client = new FakeObjectClient
        {
            Adoptable =
            {
                Deployment(InstanceA, installation: ForeignInstallation, image: Image),
            },
        };
        var service = Build(client);
        var candidate = (await service.DetectAsync(CancellationToken.None)).Single();

        var result = await service.VerifyEvidenceAsync(candidate, new AdoptionEvidence(), CancellationToken.None);

        result.Verified.Should().BeFalse("empty evidence is not ownership proof");
        result.Failures.Should().NotBeEmpty();
    }

    [Fact]
    public async Task VerifyEvidence_refuses_instance_label_pin_only_without_corroboration()
    {
        var client = new FakeObjectClient
        {
            Adoptable =
            {
                Deployment(InstanceA, installation: ForeignInstallation, image: Image),
            },
        };
        var service = Build(client);
        var candidate = (await service.DetectAsync(CancellationToken.None)).Single();

        var evidence = new AdoptionEvidence
        {
            ExpectedLabels = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [WorkloadResourceLabels.InstanceLabelKey] = InstanceA,
            },
        };

        var result = await service.VerifyEvidenceAsync(candidate, evidence, CancellationToken.None);

        result.Verified.Should().BeFalse("the instance-label pin alone is not corroborated ownership evidence");
        result.Failures.Should().NotBeEmpty();
    }

    [Fact]
    public async Task VerifyEvidence_passes_with_instance_label_pin_plus_matching_image()
    {
        var client = new FakeObjectClient
        {
            Adoptable =
            {
                Deployment(InstanceA, installation: ForeignInstallation, image: Image),
            },
        };
        var service = Build(client);
        var candidate = (await service.DetectAsync(CancellationToken.None)).Single();

        var evidence = new AdoptionEvidence
        {
            ExpectedLabels = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [WorkloadResourceLabels.InstanceLabelKey] = InstanceA,
            },
            ExpectedImage = Image,
        };

        var result = await service.VerifyEvidenceAsync(candidate, evidence, CancellationToken.None);

        result.Verified.Should().BeTrue("instance-label pin + matching image is sufficient corroborated evidence");
        result.Failures.Should().BeEmpty();
    }

    [Fact]
    public async Task Adopt_with_empty_evidence_is_refused_and_issues_no_surgery_or_ssa()
    {
        var client = new FakeObjectClient
        {
            Adoptable =
            {
                Deployment(InstanceA, installation: ForeignInstallation, image: Image),
                Pvc(InstanceA, installation: ForeignInstallation, name: "pvc-a"),
            },
        };
        var service = Build(client);
        var candidate = (await service.DetectAsync(CancellationToken.None)).Single();

        var result = await service.AdoptAsync(candidate, new AdoptionEvidence(), CancellationToken.None);

        result.Outcome.Should().Be(AdoptionOutcome.EvidenceNotVerified);
        result.Failures.Should().NotBeEmpty();
        client.MetadataPatches.Should().BeEmpty("no PVC metadata surgery on empty evidence");
        client.ForceApplies.Should().BeEmpty("no force:true SSA on empty evidence");
    }

    [Fact]
    public async Task VerifyEvidence_refuses_a_decoy_whose_name_matches_but_labels_and_image_differ()
    {
        var client = new FakeObjectClient
        {
            Adoptable =
            {
                Deployment(InstanceA, installation: "some-other-install", image: "ghcr.io/evil/decoy@sha256:2222222222222222222222222222222222222222222222222222222222222222"),
            },
        };
        var service = Build(client);
        var candidate = (await service.DetectAsync(CancellationToken.None)).Single();

        var evidence = new AdoptionEvidence
        {
            ExpectedLabels = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [WorkloadResourceLabels.InstanceLabelKey] = InstanceA,
                [WorkloadResourceLabels.InstallationLabelKey] = ForeignInstallation,
            },
            ExpectedImage = Image,
        };

        var result = await service.VerifyEvidenceAsync(candidate, evidence, CancellationToken.None);

        result.Verified.Should().BeFalse("a name match is not ownership evidence");
        result.Failures.Should().NotBeEmpty();
    }

    [Fact]
    public async Task Adopt_performs_pvc_metadata_surgery_and_force_ssa_after_evidence_verified()
    {
        var deploymentName = $"reeve-wl-matter-{InstanceA[..8]}";
        var client = new FakeObjectClient
        {
            Adoptable =
            {
                Deployment(InstanceA, installation: ForeignInstallation, image: Image),
                Pvc(
                    InstanceA,
                    installation: ForeignInstallation,
                    name: "pvc-a",
                    labels: new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        [WorkloadResourceLabels.InstanceLabelKey] = InstanceA,
                        [WorkloadResourceLabels.InstallationLabelKey] = ForeignInstallation,
                        [KubernetesAdoptionLabels.ManagedByLabel] = WorkloadAdoptionLabels.HelmManagedByValue,
                        [KubernetesAdoptionLabels.InstanceLabel] = "reeve-app",
                        [WorkloadAdoptionLabels.ArgoInstanceLabel] = "reeve-app",
                    },
                    annotations: new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        [WorkloadAdoptionLabels.HelmReleaseNameAnnotation] = "reeve",
                        [WorkloadAdoptionLabels.HelmReleaseNamespaceAnnotation] = "reeve",
                        [WorkloadAdoptionLabels.HelmResourcePolicyAnnotation] = "keep",
                        ["argocd.argoproj.io/tracking-id"] = "reeve-app:apps/Deployment:reeve/pvc-a",
                    }),
            },
            LiveManifests =
            {
                [deploymentName] = LiveDeploymentManifest(deploymentName, InstanceA, ForeignInstallation, Image),
            },
        };
        var service = Build(client);
        var candidate = (await service.DetectAsync(CancellationToken.None)).Single();
        var evidence = new AdoptionEvidence
        {
            ExpectedLabels = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [WorkloadResourceLabels.InstanceLabelKey] = InstanceA,
            },
            ExpectedImage = Image,
            ExpectedPvcNames = new[] { "pvc-a" },
        };

        var result = await service.AdoptAsync(candidate, evidence, CancellationToken.None);

        result.Outcome.Should().Be(AdoptionOutcome.Adopted);
        result.AdoptedPvcNames.Should().Contain("pvc-a");
        result.AdoptedObjectNames.Should().NotBeEmpty();

        var patch = client.MetadataPatches.Should().ContainSingle().Subject;
        patch.Name.Should().Be("pvc-a");
        patch.LabelsToRemove.Should().Contain(KubernetesAdoptionLabels.ManagedByLabel);
        patch.LabelsToRemove.Should().Contain(WorkloadAdoptionLabels.ArgoInstanceLabel);
        patch.LabelsToRemove.Should().Contain(KubernetesAdoptionLabels.InstanceLabel);
        patch.AnnotationsToRemove.Should().Contain(WorkloadAdoptionLabels.HelmReleaseNameAnnotation);
        patch.AnnotationsToRemove.Should().Contain(WorkloadAdoptionLabels.HelmReleaseNamespaceAnnotation);
        patch.AnnotationsToRemove.Should().Contain(WorkloadAdoptionLabels.HelmResourcePolicyAnnotation);
        patch.AnnotationsToRemove.Should().Contain("argocd.argoproj.io/tracking-id");
        patch.LabelsToSet[WorkloadResourceLabels.InstallationLabelKey].Should().Be(ThisInstallation);
        patch.LabelsToSet[WorkloadResourceLabels.InstanceLabelKey].Should().Be(InstanceA);

        client.ForceApplies.Should().NotBeEmpty();
        client.ForceApplies.Should().OnlyContain(a => a.Force);
    }

    [Fact]
    public async Task Adopt_force_applies_the_full_live_body_so_a_later_normal_reconcile_does_not_conflict()
    {
        var deploymentName = $"reeve-wl-matter-{InstanceA[..8]}";
        var client = new FakeObjectClient
        {
            Adoptable =
            {
                Deployment(InstanceA, installation: ForeignInstallation, image: Image),
            },
            LiveManifests =
            {
                [deploymentName] = LiveDeploymentManifest(deploymentName, InstanceA, ForeignInstallation, Image),
            },
        };
        var service = Build(client);
        var candidate = (await service.DetectAsync(CancellationToken.None)).Single();
        var evidence = new AdoptionEvidence
        {
            ExpectedLabels = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [WorkloadResourceLabels.InstanceLabelKey] = InstanceA,
            },
            ExpectedImage = Image,
        };

        var result = await service.AdoptAsync(candidate, evidence, CancellationToken.None);

        result.Outcome.Should().Be(AdoptionOutcome.Adopted);

        var apply = client.ForceApplies.Should().ContainSingle().Subject;
        apply.Force.Should().BeTrue();

        using var doc = System.Text.Json.JsonDocument.Parse(apply.ManifestJson);
        var root = doc.RootElement;
        root.TryGetProperty("spec", out var spec).Should().BeTrue("the full live body (including spec) is force-applied");
        spec.TryGetProperty("replicas", out _).Should().BeTrue("the real spec fields are owned, not just labels");

        var labels = root.GetProperty("metadata").GetProperty("labels");
        labels.GetProperty(WorkloadResourceLabels.InstanceLabelKey).GetString().Should().Be(InstanceA);
        labels.GetProperty(WorkloadResourceLabels.InstallationLabelKey).GetString().Should().Be(ThisInstallation);
    }

    [Fact]
    public async Task Adopt_without_verified_evidence_is_refused_and_issues_no_ssa()
    {
        var client = new FakeObjectClient
        {
            Adoptable =
            {
                Deployment(InstanceA, installation: "some-other-install", image: "ghcr.io/evil/decoy@sha256:2222222222222222222222222222222222222222222222222222222222222222"),
            },
        };
        var service = Build(client);
        var candidate = (await service.DetectAsync(CancellationToken.None)).Single();
        var evidence = new AdoptionEvidence
        {
            ExpectedImage = Image,
        };

        var result = await service.AdoptAsync(candidate, evidence, CancellationToken.None);

        result.Outcome.Should().Be(AdoptionOutcome.EvidenceNotVerified);
        result.Failures.Should().NotBeEmpty();
        client.ForceApplies.Should().BeEmpty("no force:true SSA on unverified evidence — a name match is not evidence");
        client.MetadataPatches.Should().BeEmpty("no metadata surgery without verified evidence");
    }

    [Fact]
    public async Task Detect_returns_empty_when_the_client_factory_faults()
    {
        var client = new FakeObjectClient
        {
            Adoptable = { Deployment(InstanceA, installation: ForeignInstallation, image: Image) },
        };
        var service = Build(
            client, new ThrowingClientFactory(new InvalidOperationException("remote kubeconfig build failed")));

        var candidates = await service.DetectAsync(CancellationToken.None);

        candidates.Should().BeEmpty("a client-build fault degrades detection to no candidates, never a 500");
    }

    [Fact]
    public async Task Adopt_returns_provider_unreachable_when_the_client_factory_faults_and_issues_no_surgery_or_ssa()
    {
        var deploymentName = $"reeve-wl-matter-{InstanceA[..8]}";
        var objectClient = new FakeObjectClient
        {
            Adoptable =
            {
                Deployment(InstanceA, installation: ForeignInstallation, image: Image),
                Pvc(InstanceA, installation: ForeignInstallation, name: "pvc-a"),
            },
            LiveManifests =
            {
                [deploymentName] = LiveDeploymentManifest(deploymentName, InstanceA, ForeignInstallation, Image),
            },
        };

        var candidate = (await Build(objectClient).DetectAsync(CancellationToken.None)).Single();
        var evidence = new AdoptionEvidence
        {
            ExpectedLabels = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [WorkloadResourceLabels.InstanceLabelKey] = InstanceA,
            },
            ExpectedImage = Image,
            ExpectedPvcNames = new[] { "pvc-a" },
        };

        var service = Build(
            objectClient, new ThrowingClientFactory(new InvalidOperationException("remote kubeconfig build failed")));
        var result = await service.AdoptAsync(candidate, evidence, CancellationToken.None);

        result.Outcome.Should().Be(AdoptionOutcome.ProviderUnreachable);
        objectClient.MetadataPatches.Should().BeEmpty("no PVC metadata surgery when the client cannot be built");
        objectClient.ForceApplies.Should().BeEmpty("no force:true SSA when the client cannot be built");
    }

    private static KubernetesWorkloadAdoptionService Build(
        FakeObjectClient client, IKubernetesClientFactory? clientFactory = null)
        => new(
            clientFactory ?? new FakeClientFactory(),
            client,
            new FakeSettingsService(),
            new FakeInstallationIdentity(ThisInstallation),
            NullLogger<KubernetesWorkloadAdoptionService>.Instance,
            new Reeve.Workloads.Abstractions.NoWorkloadAuditSink(),
            new Reeve.Workloads.Abstractions.NoWorkloadActorAccessor(),
            new KubernetesNamespaceResolver(new InClusterEnvironment(_ => false, ""), new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build()));

    private static AdoptableResource Deployment(string instance, string installation, string? image = null)
        => new()
        {
            Kind = AdoptableResourceKind.Deployment,
            Name = $"reeve-wl-matter-{instance[..8]}",
            Image = image ?? Image,
            Labels = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [WorkloadResourceLabels.InstanceLabelKey] = instance,
                [WorkloadResourceLabels.InstallationLabelKey] = installation,
            },
        };

    private static AdoptableResource DeploymentManual(string instance, string installation, string? image = null)
        => new()
        {
            Kind = AdoptableResourceKind.Deployment,
            Name = $"reeve-wl-matter-{instance[..8]}",
            Image = image ?? Image,
            Labels = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [WorkloadResourceLabels.InstanceLabelKey] = instance,
                [WorkloadResourceLabels.InstallationLabelKey] = installation,
                [WorkloadAdoptionLabels.ManagedByLabelKey] = WorkloadAdoptionLabels.ManagedByOperatorValue,
            },
        };

    private static AdoptableResource Pvc(
        string instance,
        string installation,
        string name,
        IReadOnlyDictionary<string, string>? labels = null,
        IReadOnlyDictionary<string, string>? annotations = null)
        => new()
        {
            Kind = AdoptableResourceKind.PersistentVolumeClaim,
            Name = name,
            Labels = labels ?? new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [WorkloadResourceLabels.InstanceLabelKey] = instance,
                [WorkloadResourceLabels.InstallationLabelKey] = installation,
            },
            Annotations = annotations ?? new Dictionary<string, string>(StringComparer.Ordinal),
        };

    private static string LiveDeploymentManifest(string name, string instance, string installation, string image)
        => System.Text.Json.JsonSerializer.Serialize(new
        {
            apiVersion = "apps/v1",
            kind = "Deployment",
            metadata = new
            {
                name,
                labels = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    [WorkloadResourceLabels.InstanceLabelKey] = instance,
                    [WorkloadResourceLabels.InstallationLabelKey] = installation,
                },
            },
            spec = new
            {
                replicas = 1,
                selector = new { matchLabels = new Dictionary<string, string>(StringComparer.Ordinal) { [WorkloadResourceLabels.InstanceLabelKey] = instance } },
                template = new
                {
                    metadata = new { labels = new Dictionary<string, string>(StringComparer.Ordinal) { [WorkloadResourceLabels.InstanceLabelKey] = instance } },
                    spec = new { containers = new[] { new { name = "app", image } } },
                },
            },
        });

    private sealed record RecordedMetadataPatch(
        KubernetesObjectKind Kind,
        string Name,
        IReadOnlyDictionary<string, string> LabelsToSet,
        IReadOnlyList<string> LabelsToRemove,
        IReadOnlyList<string> AnnotationsToRemove);

    private sealed record RecordedApply(KubernetesObjectKind Kind, string Name, bool Force, string ManifestJson);

    private sealed class FakeClientFactory : IKubernetesClientFactory
    {
        public Task<IKubernetes> CreateClientAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IKubernetes>(null!);
    }

    private sealed class ThrowingClientFactory : IKubernetesClientFactory
    {
        private readonly Exception _fault;
        public ThrowingClientFactory(Exception fault) => _fault = fault;
        public Task<IKubernetes> CreateClientAsync(CancellationToken cancellationToken = default) =>
            Task.FromException<IKubernetes>(_fault);
    }

    private sealed class FakeInstallationIdentity : IWorkloadInstallationIdentity
    {
        private readonly string _id;
        public FakeInstallationIdentity(string id) => _id = id;
        public Task<string> GetInstallationIdAsync(CancellationToken cancellationToken = default) => Task.FromResult(_id);
    }

    private sealed class FakeSettingsService : IWorkloadOrchestratorSettingsService
    {
        public Task<WorkloadOrchestratorSettingsResponse> GetAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(new WorkloadOrchestratorSettingsResponse
            {
                Settings = new WorkloadOrchestratorSettings
                {
                    Provider = KubernetesProvider.Id,
                }
                    .WithProvider(KubernetesProvider.Id, new KubernetesProviderSettings { Namespace = Ns }),
                EffectiveProvider = KubernetesProvider.Id,
                AutoDetected = false,
                Providers = Array.Empty<WorkloadProviderInfo>(),
            });


        public Task<WorkloadProviderProbeResult> TestAsync(WorkloadProviderId? requestedProvider, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }

    private sealed class FakeObjectClient : IKubernetesObjectClient
    {
        public List<AdoptableResource> Adoptable { get; } = new();
        public bool Unreachable { get; init; }
        public List<RecordedMetadataPatch> MetadataPatches { get; } = new();
        public List<RecordedApply> ForceApplies { get; } = new();

        public Dictionary<string, string> LiveManifests { get; } = new(StringComparer.Ordinal);

        public IAsyncEnumerable<WorkloadWatchEvent> WatchWorkloadsAsync(
            IKubernetes client, string @namespace, string labelSelector, CancellationToken ct)
            => throw new NotSupportedException("the workload watch is not exercised by these tests");

        public Task<IReadOnlyList<AdoptableResource>> ListAdoptableAsync(IKubernetes client, string @namespace, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<AdoptableResource>>(
                Unreachable ? Array.Empty<AdoptableResource>() : Adoptable.ToArray());

        public Task<KubernetesReadyNodePartition> ListReadyNodesAsync(
            IKubernetes client, IReadOnlyDictionary<string, string> nodeEligibilitySelector, CancellationToken ct)
            => Task.FromResult(KubernetesReadyNodePartition.Empty);

        public Task<KubernetesNetworkAttachmentListResult> ListNetworkAttachmentDefinitionsAsync(
            IKubernetes client, string @namespace, CancellationToken ct)
            => Task.FromResult(KubernetesNetworkAttachmentListResult.Unavailable(
                KubernetesNetworkAttachmentUnavailableReasons.NoneInNamespace));

        public Task PatchResourceMetadataAsync(
            IKubernetes client, string @namespace, KubernetesObjectKind kind, string name,
            IReadOnlyDictionary<string, string> labelsToSet, IReadOnlyList<string> labelsToRemove,
            IReadOnlyList<string> annotationsToRemove, CancellationToken ct)
        {
            MetadataPatches.Add(new RecordedMetadataPatch(kind, name, labelsToSet, labelsToRemove, annotationsToRemove));
            return Task.CompletedTask;
        }

        public Task<string?> ReadObjectManifestAsync(
            IKubernetes client, string @namespace, KubernetesObjectKind kind, string name, CancellationToken ct)
            => Task.FromResult(Unreachable ? null : LiveManifests.TryGetValue(name, out var body) ? body : null);

        public Task<KubernetesApplyOutcome> ServerSideApplyAsync(
            IKubernetes client, string @namespace, KubernetesObjectKind kind, string name, string manifestJson,
            string fieldManager, bool force, CancellationToken ct)
        {
            ForceApplies.Add(new RecordedApply(kind, name, force, manifestJson));
            return Task.FromResult(KubernetesApplyOutcome.Ok());
        }

        public Task<IReadOnlyDictionary<string, string>> ReadSecretValuesAsync(
            IKubernetes client, string @namespace, string secretName, IReadOnlyCollection<string> keys, CancellationToken ct)
            => Task.FromResult<IReadOnlyDictionary<string, string>>(new Dictionary<string, string>());

        public Task EnsurePvcAsync(IKubernetes client, string @namespace, V1PersistentVolumeClaim pvc, bool isAdoptedClaim, CancellationToken ct)
            => Task.CompletedTask;

        public Task<KubernetesWorkloadRaw?> ReadWorkloadAsync(IKubernetes client, string @namespace, IReadOnlyDictionary<string, string> selectorLabels, CancellationToken ct)
            => Task.FromResult<KubernetesWorkloadRaw?>(KubernetesWorkloadRaw.NotFound());

        public Task DeleteWorkloadByLabelAsync(IKubernetes client, string @namespace, IReadOnlyDictionary<string, string> selectorLabels, bool retainPvc, CancellationToken ct)
            => Task.CompletedTask;

        public Task DeleteOtherWorkloadKindsAsync(IKubernetes client, string @namespace, IReadOnlyDictionary<string, string> selectorLabels, KubernetesObjectKind keptKind, CancellationToken ct)
            => Task.CompletedTask;

        public Task DeleteScanJobsByLabelAsync(IKubernetes client, string @namespace, IReadOnlyDictionary<string, string> selectorLabels, string? exceptRunId, CancellationToken ct)
            => Task.CompletedTask;

        public Task ScaleDeploymentAsync(IKubernetes client, string @namespace, IReadOnlyDictionary<string, string> selectorLabels, int replicas, CancellationToken ct)
            => Task.CompletedTask;

        public Task SetDaemonSetSuspendedAsync(IKubernetes client, string @namespace, IReadOnlyDictionary<string, string> selectorLabels, bool suspended, string inst8, CancellationToken ct)
            => Task.CompletedTask;

        public Task SetScanJobsSuspendedAsync(IKubernetes client, string @namespace, IReadOnlyDictionary<string, string> selectorLabels, bool suspended, CancellationToken ct)
            => Task.CompletedTask;
    }
}
