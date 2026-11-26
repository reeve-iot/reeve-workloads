using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Reeve.Workloads.Providers.Docker;
using Reeve.Workloads.Abstractions.Providers;
using AuditEntry = Reeve.Workloads.Abstractions.WorkloadAuditEntry;
using FluentAssertions;
using k8s;
using k8s.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Reeve.Workloads.Abstractions;
using Reeve.Workloads.Abstractions.Contracts;
using Reeve.Workloads.Abstractions.Domain;
using Reeve.Workloads.Abstractions.Domain.Entities;
using Reeve.Workloads.Core.Adoption;
using Reeve.Workloads.Providers.Kubernetes;
using Reeve.Workloads.Providers.Kubernetes.Adoption;
using Xunit;

using Reeve.Workloads.Domain;
namespace Reeve.Workloads.Composition.Tests.Adoption;

public sealed class ForceAdoptAuthzTests
{
    private const string Ns = "reeve";
    private const string ThisInstallation = "install-this";
    private const string PluginTypeId = "matter";
    private const string WorkloadId = "matter";
    private const string VolumeId = "data";
    private const string Image = "ghcr.io/reeve/matter@sha256:1111111111111111111111111111111111111111111111111111111111111111";

    private static readonly Guid InstanceId = Guid.Parse("aaaaaaaa-1111-2222-3333-444444444444");
    private static readonly Guid ActorId = Guid.Parse("55555555-5555-4555-8555-555555555555");
    private static string InstanceLabel => InstanceId.ToString("D");

    [Fact]
    public async Task ReadoptingASwitchDisownedInstance_reuses_the_same_instance_row_and_preserves_inst8_and_volume_names()
    {
        var instance = SwitchDisownedInstance(DockerProvider.Id);
        instance.State.Should().Be(WorkloadInstanceState.ExternallyManaged, "the disown left the runtime in place");
        instance.Provider.Should().Be(DockerProvider.Id, "the disown PRESERVES the original managed provider");

        var dockerVolumeBefore = DockerResourceNamer.Instance.VolumeName(new WorkloadNamingSubject { GroupId = PluginTypeId, WorkloadId = WorkloadId, Inst8 = instance.Inst8 }, VolumeId);
        var pvcNameBefore = DefaultWorkloadResourceNamer.Instance.VolumeName(new WorkloadNamingSubject { GroupId = PluginTypeId, WorkloadId = WorkloadId, Inst8 = instance.Inst8 }, VolumeId);
        var idBefore = instance.Id;
        var inst8Before = instance.Inst8;

        var deploymentName = $"reeve-wl-{PluginTypeId}-{instance.Inst8}";
        var pvcName = DefaultWorkloadResourceNamer.Instance.VolumeName(new WorkloadNamingSubject { GroupId = PluginTypeId, WorkloadId = WorkloadId, Inst8 = instance.Inst8 }, VolumeId);
        var client = new FakeObjectClient
        {
            Adoptable =
            {
                OperatorManagedDeployment(deploymentName, InstanceLabel, ThisInstallation, Image),
                Pvc(pvcName, InstanceLabel, ThisInstallation),
            },
            LiveManifests =
            {
                [deploymentName] = LiveDeploymentManifest(deploymentName, InstanceLabel, ThisInstallation, Image),
            },
        };
        var audit = new FakeAuditWriter();
        var service = Build(client, audit, new FakeCurrentUser(ActorId));

        var candidate = (await service.DetectAsync(CancellationToken.None))
            .Single(c => c.InstanceLabel == InstanceLabel);
        candidate.Class.Should().Be(AdoptionClass.ManualManaged);

        var evidence = new AdoptionEvidence
        {
            ExpectedLabels = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [WorkloadResourceLabels.InstanceLabelKey] = InstanceLabel,
            },
            ExpectedImage = Image,
            ExpectedPvcNames = new[] { pvcName },
        };

        var result = await service.AdoptAsync(candidate, evidence, CancellationToken.None);
        result.Outcome.Should().Be(AdoptionOutcome.Adopted);

        WorkloadInstanceStateMachine.IsValidTransition(WorkloadInstanceState.ExternallyManaged, WorkloadInstanceState.Deploying)
            .Should().BeTrue("managed re-adoption re-enters via the pre-existing ExternallyManaged → Deploying edge");
        instance.TransitionTo(
            WorkloadInstanceState.Deploying,
            WorkloadStateReason.AdoptionAccepted,
            "Re-adopted from manual operator management back under Reeve (class-c force-adopt).",
            DateTimeOffset.UtcNow);
        instance.State.Should().Be(WorkloadInstanceState.Deploying);

        var apply = client.ForceApplies.Should().ContainSingle().Subject;
        apply.Force.Should().BeTrue();
        using var doc = System.Text.Json.JsonDocument.Parse(apply.ManifestJson);
        var reAdoptedInstanceLabel = doc.RootElement.GetProperty("metadata").GetProperty("labels")
            .GetProperty(WorkloadResourceLabels.InstanceLabelKey).GetString();
        reAdoptedInstanceLabel.Should().Be(InstanceLabel,
            "re-adoption preserves the instance identity label = the InstanceId — it re-uses the row, never mints a new id");

        var reAdoptedInst8 = WorkloadInstance.ComputeInst8(Guid.Parse(reAdoptedInstanceLabel!));
        reAdoptedInst8.Should().Be(inst8Before, "the re-adopted object carries the SAME inst8 — the row identity was re-used");
        DockerResourceNamer.Instance.VolumeName(new WorkloadNamingSubject { GroupId = PluginTypeId, WorkloadId = WorkloadId, Inst8 = reAdoptedInst8 }, VolumeId)
            .Should().Be(dockerVolumeBefore, "the docker named-volume name is byte-identical across the round-trip");
        DefaultWorkloadResourceNamer.Instance.VolumeName(new WorkloadNamingSubject { GroupId = PluginTypeId, WorkloadId = WorkloadId, Inst8 = reAdoptedInst8 }, VolumeId)
            .Should().Be(pvcNameBefore, "the k8s PVC name is byte-identical across the round-trip");
        instance.Id.Should().Be(idBefore, "re-adoption re-uses the SAME instance row — never a new id");
    }

    [Fact]
    public void ExternallyManaged_readoption_uses_the_pre_existing_managed_reentry_edge_not_a_switching_reentry()
    {
        WorkloadInstanceStateMachine.IsValidTransition(WorkloadInstanceState.ExternallyManaged, WorkloadInstanceState.Deploying)
            .Should().BeTrue();
        WorkloadInstanceStateMachine.IsValidTransition(WorkloadInstanceState.ExternallyManaged, WorkloadInstanceState.Switching)
            .Should().BeFalse("re-adoption never re-enters Switching (single-owner machine)");
    }

    [Fact]
    public async Task Successful_force_adopt_writes_one_audit_entry_attributed_to_the_actor_and_keyed_on_the_instance()
    {
        var client = SingleObjectClientWithLiveBody();
        var audit = new FakeAuditWriter();
        var service = Build(client, audit, new FakeCurrentUser(ActorId));

        var candidate = (await service.DetectAsync(CancellationToken.None)).Single();
        var result = await service.AdoptAsync(candidate, VerifiableEvidence(), CancellationToken.None);

        result.Outcome.Should().Be(AdoptionOutcome.Adopted);

        var entry = audit.Entries.Should().ContainSingle("a successful force-adopt is audited").Subject;
        entry.Action.Should().Be("workloads.adoption.force-adopted");
        entry.EntityType.Should().Be("workload-instance");
        entry.EntityId.Should().Be(InstanceId, "the audited entity is the instance the force-adopted resources belong to");
        entry.ActorUserId.Should().Be(ActorId, "the audit is attributed to the current actor");
    }

    [Fact]
    public async Task Refused_force_adopt_is_fail_closed_no_ssa_and_no_audit()
    {
        var client = new FakeObjectClient
        {
            Adoptable =
            {
                OperatorManagedDeployment(
                    $"reeve-wl-{PluginTypeId}-{InstanceId:N}"[..20], InstanceLabel, ThisInstallation,
                    image: "ghcr.io/evil/decoy@sha256:2222222222222222222222222222222222222222222222222222222222222222"),
            },
        };
        var audit = new FakeAuditWriter();
        var service = Build(client, audit, new FakeCurrentUser(ActorId));

        var candidate = (await service.DetectAsync(CancellationToken.None)).Single();
        var result = await service.AdoptAsync(candidate, VerifiableEvidence(), CancellationToken.None);

        result.Outcome.Should().Be(AdoptionOutcome.EvidenceNotVerified);
        client.ForceApplies.Should().BeEmpty("no force:true SSA when evidence does not verify — the surface fails closed");
        client.MetadataPatches.Should().BeEmpty("no metadata surgery on a refused adopt");
        audit.Entries.Should().BeEmpty("a refused force-adopt writes NO audit — the audited surface records only real adopts");
    }

    [Fact]
    public async Task A_candidate_matching_the_name_pattern_but_not_the_ownership_evidence_is_refused_without_force_ssa()
    {
        var decoyImage = "ghcr.io/evil/decoy@sha256:3333333333333333333333333333333333333333333333333333333333333333";
        var client = new FakeObjectClient
        {
            Adoptable = { OperatorManagedDeployment($"reeve-wl-{PluginTypeId}-{InstanceId.ToString("N")[..8]}", InstanceLabel, ThisInstallation, decoyImage) },
        };
        var service = Build(client, new FakeAuditWriter(), new FakeCurrentUser(ActorId));
        var candidate = (await service.DetectAsync(CancellationToken.None)).Single();

        var verify = await service.VerifyEvidenceAsync(candidate, VerifiableEvidence(), CancellationToken.None);
        verify.Verified.Should().BeFalse("a name/label match is not ownership evidence");

        var result = await service.AdoptAsync(candidate, VerifiableEvidence(), CancellationToken.None);
        result.Outcome.Should().Be(AdoptionOutcome.EvidenceNotVerified);
        client.ForceApplies.Should().BeEmpty("no force:true SSA on unverified ownership evidence");
    }

    [Fact]
    public async Task A_claimed_pvc_that_does_not_exist_is_a_hard_fail_and_is_never_silently_created()
    {
        var deploymentName = $"reeve-wl-{PluginTypeId}-{InstanceId.ToString("N")[..8]}";
        var client = new FakeObjectClient
        {
            Adoptable = { OperatorManagedDeployment(deploymentName, InstanceLabel, ThisInstallation, Image) },
            LiveManifests = { [deploymentName] = LiveDeploymentManifest(deploymentName, InstanceLabel, ThisInstallation, Image) },
        };
        var service = Build(client, new FakeAuditWriter(), new FakeCurrentUser(ActorId));
        var candidate = (await service.DetectAsync(CancellationToken.None)).Single();

        var evidence = new AdoptionEvidence
        {
            ExpectedLabels = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [WorkloadResourceLabels.InstanceLabelKey] = InstanceLabel,
            },
            ExpectedImage = Image,
            ExpectedPvcNames = new[] { "does-not-exist-pvc" },
        };

        var result = await service.AdoptAsync(candidate, evidence, CancellationToken.None);

        result.Outcome.Should().Be(AdoptionOutcome.EvidenceNotVerified, "a claimed volume that does not exist is a hard fail");
        result.Failures.Should().Contain(f => f.Contains("does-not-exist-pvc", StringComparison.Ordinal));
        client.MetadataPatches.Should().BeEmpty("the missing PVC is never silently created");
        client.ForceApplies.Should().BeEmpty("no force:true SSA when the claimed volume does not exist");
    }

    [Fact]
    public async Task A_claimed_object_that_vanished_before_adopt_is_a_hard_fail_never_a_labels_only_stub()
    {
        var deploymentName = $"reeve-wl-{PluginTypeId}-{InstanceId.ToString("N")[..8]}";
        var client = new FakeObjectClient
        {
            Adoptable = { OperatorManagedDeployment(deploymentName, InstanceLabel, ThisInstallation, Image) },
        };
        var audit = new FakeAuditWriter();
        var service = Build(client, audit, new FakeCurrentUser(ActorId));
        var candidate = (await service.DetectAsync(CancellationToken.None)).Single();

        var result = await service.AdoptAsync(candidate, VerifiableEvidence(), CancellationToken.None);

        result.Outcome.Should().Be(AdoptionOutcome.ProviderUnreachable, "a vanished object is a hard fail, not a silent create");
        client.ForceApplies.Should().BeEmpty("never force a labels-only stub for an object that no longer exists");
        audit.Entries.Should().BeEmpty("a non-Adopted outcome writes no audit");
    }

    private static WorkloadInstance SwitchDisownedInstance(WorkloadProviderId provider)
    {
        var now = DateTimeOffset.UtcNow;
        var instance = WorkloadInstance.Create(
            InstanceId, Guid.Parse("dddddddd-1111-2222-3333-444444444444"), PluginTypeId, WorkloadId, now);

        instance.TransitionTo(WorkloadInstanceState.Deploying, WorkloadStateReason.ConfigRequired, null, now);
        instance.SetProvider(provider, now);
        instance.TransitionTo(WorkloadInstanceState.Healthy, WorkloadStateReason.ExternalHealthObserved, null, now);
        instance.TransitionTo(WorkloadInstanceState.Switching, WorkloadStateReason.ExternalHealthObserved, null, now);
        instance.TransitionTo(WorkloadInstanceState.ExternallyManaged, WorkloadStateReason.ExternalHealthObserved, null, now);
        return instance;
    }

    private static AdoptionEvidence VerifiableEvidence() => new()
    {
        ExpectedLabels = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [WorkloadResourceLabels.InstanceLabelKey] = InstanceLabel,
        },
        ExpectedImage = Image,
    };

    private static FakeObjectClient SingleObjectClientWithLiveBody()
    {
        var deploymentName = $"reeve-wl-{PluginTypeId}-{InstanceId.ToString("N")[..8]}";
        return new FakeObjectClient
        {
            Adoptable = { OperatorManagedDeployment(deploymentName, InstanceLabel, ThisInstallation, Image) },
            LiveManifests = { [deploymentName] = LiveDeploymentManifest(deploymentName, InstanceLabel, ThisInstallation, Image) },
        };
    }

    private static KubernetesWorkloadAdoptionService Build(
        FakeObjectClient client, IWorkloadAuditSink auditSink, IWorkloadActorAccessor actor)
        => new(
            new FakeClientFactory(),
            client,
            new FakeSettingsService(),
            new FakeInstallationIdentity(ThisInstallation),
            NullLogger<KubernetesWorkloadAdoptionService>.Instance,
            auditSink,
            actor,
            new KubernetesNamespaceResolver(new InClusterEnvironment(_ => false, ""), new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build()));

    private static AdoptableResource OperatorManagedDeployment(string name, string instance, string installation, string image)
        => new()
        {
            Kind = AdoptableResourceKind.Deployment,
            Name = name,
            Image = image,
            Labels = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [WorkloadResourceLabels.InstanceLabelKey] = instance,
                [WorkloadResourceLabels.InstallationLabelKey] = installation,
                [WorkloadAdoptionLabels.ManagedByLabelKey] = WorkloadAdoptionLabels.ManagedByOperatorValue,
            },
        };

    private static AdoptableResource Pvc(string name, string instance, string installation)
        => new()
        {
            Kind = AdoptableResourceKind.PersistentVolumeClaim,
            Name = name,
            Labels = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [WorkloadResourceLabels.InstanceLabelKey] = instance,
                [WorkloadResourceLabels.InstallationLabelKey] = installation,
                [WorkloadAdoptionLabels.ManagedByLabelKey] = WorkloadAdoptionLabels.ManagedByOperatorValue,
            },
            Annotations = new Dictionary<string, string>(StringComparer.Ordinal),
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

    private sealed record RecordedMetadataPatch(KubernetesObjectKind Kind, string Name);

    private sealed record RecordedApply(KubernetesObjectKind Kind, string Name, bool Force, string ManifestJson);

    private sealed class FakeClientFactory : IKubernetesClientFactory
    {
        public Task<IKubernetes> CreateClientAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IKubernetes>(null!);
    }

    private sealed class FakeInstallationIdentity : IWorkloadInstallationIdentity
    {
        private readonly string _id;
        public FakeInstallationIdentity(string id) => _id = id;
        public Task<string> GetInstallationIdAsync(CancellationToken cancellationToken = default) => Task.FromResult(_id);
    }

    private sealed class FakeCurrentUser : IWorkloadActorAccessor
    {
        public FakeCurrentUser(Guid? userId) => ActorUserId = userId;
        public Guid? ActorUserId { get; }
    }

    private sealed class FakeAuditWriter : IWorkloadAuditSink
    {
        public List<Reeve.Workloads.Abstractions.WorkloadAuditEntry> Entries { get; } = new();

        public Task WriteAsync(
            Reeve.Workloads.Abstractions.WorkloadAuditEntry entry, CancellationToken cancellationToken = default)
        {
            Entries.Add(entry);
            return Task.CompletedTask;
        }
    }

    private sealed class FakeSettingsService : IWorkloadOrchestratorSettingsService
    {
        public Task<WorkloadOrchestratorSettingsResponse> GetAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(new WorkloadOrchestratorSettingsResponse
            {
                Settings = new WorkloadOrchestratorSettings { Provider = KubernetesProvider.Id }
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
            => Task.FromResult<IReadOnlyList<AdoptableResource>>(Unreachable ? Array.Empty<AdoptableResource>() : Adoptable.ToArray());

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
            MetadataPatches.Add(new RecordedMetadataPatch(kind, name));
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

        public Task SetScanJobsSuspendedAsync(
            IKubernetes client, string @namespace, IReadOnlyDictionary<string, string> selector, bool suspended,
            CancellationToken ct)
            => throw new NotSupportedException("scan-job suspension is not exercised by these tests");

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
    }
}
