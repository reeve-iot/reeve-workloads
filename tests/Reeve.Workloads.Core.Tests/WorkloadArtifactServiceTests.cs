using Microsoft.AspNetCore.DataProtection;
using Reeve.Workloads.Domain;
using Reeve.Workloads.Domain.DesiredSpec;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Reeve.Workloads.Abstractions;
using Reeve.Workloads.Abstractions.Contracts;
using Reeve.Workloads.Abstractions.Contracts.Deployment;
using Reeve.Workloads.Abstractions.Domain;
using Reeve.Workloads.Abstractions.Domain.DesiredSpec;
using Reeve.Workloads.Abstractions.Domain.Entities;
using Reeve.Workloads.Abstractions.Rendering;
using Reeve.Workloads.Abstractions.Update;
using Reeve.Workloads.Core;
using Reeve.Workloads.Providers.Manual;
using Reeve.Workloads.Providers.Kubernetes;
using Reeve.Workloads.Providers.Kubernetes.Rendering;
using Reeve.Workloads.Core.Rendering;

using Reeve.Workloads.Core.Providers;

using Reeve.Workloads.Abstractions.Providers;
using Reeve.Workloads.Core.Groups;
using Reeve.Workloads.Core.Tests.Groups;
using Reeve.Workloads.Providers.Docker;
using Reeve.Workloads.Tests.Shared;
using Reeve.Workloads.Testing;
using Microsoft.Extensions.Options;

namespace Reeve.Workloads.Core.Tests;


public sealed class WorkloadArtifactServiceTests
{
    private static readonly Guid InstanceId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid GroupInstanceId = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");
    private const string GroupId = "com.reeve.matter";

    private readonly InMemoryRepository repository = new();
    private readonly FakeInputsProvider inputsProvider = new();
    private readonly FakeRendererRegistry rendererRegistry = new();
    private readonly RecordingCredentialPort credentialPort = new() { Answer = (_, _) => "live-token" };
    private readonly RecordingAuditSink auditSink = new();

    private IReadOnlyList<IWorkloadResourceNamer> namers = [];
    private WorkloadProviderRegistry providers = ProviderRegistryFakes.FirstPartyRegistry();
    private FakeHostBootstrapSource host = new()
    {
        ProvisionedCredentials = [new ProvisionedCredential(Token, "REEVE_WORKLOAD_TOKEN")],
    };

    private const string Token = FakeHostBootstrapSource.TokenCredentialId;
    private IWorkloadHostBootstrapSource? source;
    private IWorkloadReconcileInputsProvider? inputs;

    [Fact]
    public async Task Artifact_render_uses_the_marker_aware_reference_projection()
    {
        const string binding = "ref:vault:password";
        var instance = Instance("gateway");
        instance.SetResolvedParameters(new Dictionary<string, string?> { ["password"] = binding }, DateTimeOffset.UnixEpoch);
        instance.SetReferenceRolloutState(
            "{\"hashPinnedBindings\":{\"password\":{\"binding\":\"ref:vault:password\",\"isVersionPinned\":false}}}",
            DateTimeOffset.UnixEpoch);
        repository.Add(instance);
        inputsProvider.Manifest = Manifest("gateway") with
        {
            Parameters = [new WorkloadParameter { Name = "password", Type = WorkloadParameterType.Password }],
        };
        parameterReferences = new FixedReferenceResolver("s3cret");

        var withMarker = await Build().RenderAsync(InstanceId, WorkloadArtifactFormatId.Parse("compose"),
            revealSecrets: false, actorUserId: null);
        var projectedHash = rendererRegistry.LastSpec!.SpecHash;
        instance.SetReferenceRolloutState(null, DateTimeOffset.UnixEpoch);
        var withoutMarker = await Build().RenderAsync(InstanceId, WorkloadArtifactFormatId.Parse("compose"),
            revealSecrets: false, actorUserId: null);

        withMarker.Outcome.Should().Be(WorkloadArtifactRenderOutcome.Success);
        withoutMarker.Outcome.Should().Be(WorkloadArtifactRenderOutcome.Success);
        projectedHash.Should().NotBe(rendererRegistry.LastSpec!.SpecHash,
            "the literal-equivalent marker drops the binding from the hash, as the reconciler's projection does");
    }

    private IWorkloadHostBootstrapSource Host => source ?? host;

    private readonly WorkloadParameterSecretCodec parameterSecrets = new(new EphemeralDataProtectionProvider());
    private IWorkloadParameterReferenceResolver parameterReferences = new NoWorkloadParameterReferenceResolver();
    private IActiveWorkloadIntentProvider intents = new NullActiveWorkloadIntentProvider();
    private FakeManifests manifests = new();
    private InMemoryWorkloadGroupRepository modules = new();

    private WorkloadArtifactService Build(IWorkloadArtifactRendererRegistry? renderers = null) => new(
        repository, inputs ?? inputsProvider, Host, renderers ?? rendererRegistry, credentialPort,
        providers,
        new WorkloadResourceNamerRegistry(namers),
        auditSink, parameterSecrets, parameterReferences, intents, manifests, modules,
        NullLogger<WorkloadArtifactService>.Instance);

    [Fact]
    public async Task With_no_provider_that_leaves_deployment_to_the_operator_nothing_renders()
    {
        providers = ProviderRegistryFakes.RegistryWith(KubernetesProvider.Id, DockerProvider.Id);
        repository.Add(Instance("gateway"));
        inputsProvider.Manifest = Manifest("gateway");

        var result = await Build().RenderAsync(InstanceId, WorkloadArtifactFormatId.Parse("compose"), revealSecrets: true, actorUserId: null);

        result.Outcome.Should().Be(WorkloadArtifactRenderOutcome.NoHandAppliedProvider);
        result.Error.Should().NotBeNullOrWhiteSpace();
        rendererRegistry.LastSpec.Should().BeNull();
        credentialPort.Asks.Should().BeEmpty();
        host.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task Each_sibling_is_asked_as_the_operator_will_apply_it_under_the_effective_providers_secret()
    {
        namers = [new TaggedResourceNamer(ManualProvider.Id, "manual")];
        inputsProvider.Namer = new TaggedResourceNamer(KubernetesProvider.Id, "effective");
        var gateway = Instance("gateway");
        var bridge = Instance("bridge", Guid.Parse("22222222-2222-2222-2222-222222222222"));
        repository.Add(gateway);
        repository.Add(bridge);
        inputsProvider.Manifest = Manifest("gateway");

        await Build().RenderAsync(InstanceId, WorkloadArtifactFormatId.Parse("compose"), revealSecrets: false, actorUserId: null);

        host.Requests.Should().HaveCount(2, "one request per sibling");
        host.Requests.Should().OnlyContain(r =>
            r.Provider == ManualProvider.Id && r.Audience == WorkloadBootstrapAudience.HandApplied);
        host.Requests.Single(r => r.WorkloadName == "gateway").Should().Match<WorkloadHostBootstrapRequest>(r =>
            r.InstanceId == gateway.Id && r.SecretName == $"effective-secret-{GroupId}-gateway-{gateway.Inst8}");
        host.Requests.Single(r => r.WorkloadName == "bridge").Should().Match<WorkloadHostBootstrapRequest>(r =>
            r.InstanceId == bridge.Id && r.SecretName == $"effective-secret-{GroupId}-bridge-{bridge.Inst8}");
    }

    [Fact]
    public async Task A_sibling_the_host_will_not_have_applied_by_hand_refuses_the_whole_render()
    {
        host = new FakeHostBootstrapSource
        {
            Refusal = "no external gateway address is configured",
            RefuseWhen = r => r.WorkloadName == "gateway",
        };
        repository.Add(Instance("gateway"));
        repository.Add(Instance("bridge", Guid.Parse("22222222-2222-2222-2222-222222222222")));
        inputsProvider.Manifest = Manifest("gateway");

        var result = await Build().RenderAsync(InstanceId, WorkloadArtifactFormatId.Parse("compose"), revealSecrets: true, actorUserId: null);

        result.Outcome.Should().Be(WorkloadArtifactRenderOutcome.HostRefused);
        result.Error.Should().Be("no external gateway address is configured");
        result.Workloads.Should().BeEmpty();
        rendererRegistry.LastSpec.Should().BeNull();
        credentialPort.Asks.Should().BeEmpty();
        auditSink.Entries.Should().BeEmpty();
        host.Requests.Should().Contain(r => r.WorkloadName == "bridge", "the sibling that sorts first was asked");
    }

    [Fact]
    public async Task A_takeover_render_asks_under_the_effective_secret_and_carries_the_unchanged_artifact_hash()
    {
        var manualNamer = new TaggedResourceNamer(ManualProvider.Id, "manual");
        var effectiveNamer = new TaggedResourceNamer(KubernetesProvider.Id, "effective");
        namers = [manualNamer, effectiveNamer];
        var instance = Instance("gateway");
        repository.Add(instance);
        var reconcile = ReconcilePath(KubernetesProvider.Id, SecretBoundManifest("gateway"));
        inputs = reconcile;

        await new WorkloadArtifactTakeoverRenderer(
                Build(), rendererRegistry, auditSink, NullLogger<WorkloadArtifactTakeoverRenderer>.Instance)
            .RenderTakeoverAsync(instance);

        var effectiveSecret = $"effective-secret-{GroupId}-gateway-{instance.Inst8}";
        var handApplied = host.Requests.Where(r => r.Audience == WorkloadBootstrapAudience.HandApplied).ToList();
        handApplied.Should().NotBeEmpty().And.OnlyContain(r => r.SecretName == effectiveSecret && r.Provider == ManualProvider.Id);

        var answer = (await reconcile.GetInputsAsync(instance)).Inputs!;
        answer.Bootstrap.SecretName.Should().Be(effectiveSecret, "the reconcile path names the secret the effective way");
        var reconcileSpec = Reconciled(answer, instance, ManualProvider.Descriptor, manualNamer);

        var rendered = rendererRegistry.LastSpec!;
        rendered.SpecHash.Should().Be(reconcileSpec.SpecHash);
        rendered.Env.Single(e => e.Name == WorkloadDesiredSpecFactory.SpecHashEnv).Value.Should().Be(reconcileSpec.SpecHash);
        rendered.SpecHash.Should().NotBe(Reconciled(answer, instance).SpecHash,
            "the effective spec is rendered for another provider, which the hash covers");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_volume_keeps_the_name_of_the_provider_the_row_is_bound_to(bool kubernetesBound)
    {
        namers =
        [
            new TaggedResourceNamer(ManualProvider.Id, "manual"),
            new TaggedResourceNamer(KubernetesProvider.Id, "effective"),
        ];
        var instance = Instance("gateway");
        repository.Add(instance);
        inputs = ReconcilePath(kubernetesBound ? KubernetesProvider.Id : ManualProvider.Id, Manifest("gateway") with
        {
            Volumes = [new WorkloadVolume { Id = "state", MountPath = "/data", Size = "1Gi", Criticality = VolumeCriticality.Critical }],
        });

        if (kubernetesBound)
        {
            await new WorkloadArtifactTakeoverRenderer(
                    Build(), rendererRegistry, auditSink, NullLogger<WorkloadArtifactTakeoverRenderer>.Instance)
                .RenderTakeoverAsync(instance);
        }
        else
        {
            await Build().RenderAsync(InstanceId, WorkloadArtifactFormatId.Parse("compose"), revealSecrets: false, actorUserId: null);
        }

        rendererRegistry.LastSpec!.Volumes.Single().ResourceName.Should().Be(
            $"{(kubernetesBound ? "effective" : "manual")}-vol-gateway-state-{instance.Inst8}",
            "a takeover manifest that renamed the live claims would mount the workload on empty ones");
    }

    [Fact]
    public async Task The_artifact_renders_the_hosts_answer_for_the_operator_not_for_the_runtime()
    {
        namers = [new TaggedResourceNamer(ManualProvider.Id, "manual")];
        source = new PerAudienceSource();
        repository.Add(Instance("gateway"));
        inputs = ReconcilePath(ManualProvider.Id, Manifest("gateway") with
        {
            Env = [new WorkloadEnv { Name = "HOST_ENDPOINT", From = "bootstrap:endpoint" }],
        });

        await Build().RenderAsync(InstanceId, WorkloadArtifactFormatId.Parse("compose"), revealSecrets: false, actorUserId: null);

        var env = rendererRegistry.LastSpec!.Env;
        env.Single(e => e.Name == "HOST_ENDPOINT").Value.Should().Be("wss://operator.example/ws");
        env.Single(e => e.Name == "FORCED").Value.Should().Be(nameof(WorkloadBootstrapAudience.HandApplied));
        rendererRegistry.LastSpec!.ProvisionedCredentials.Should().Equal(
            new ProvisionedCredential("operator-credential", "OPERATOR_TOKEN"));
    }

    [Fact]
    public async Task The_artifact_carries_the_reconcile_paths_hash_when_the_host_answers_each_audience_differently()
    {
        namers = [new TaggedResourceNamer(ManualProvider.Id, "manual")];
        source = new PerAudienceSource();
        var instance = Instance("gateway");
        repository.Add(instance);
        var reconcile = ReconcilePath(ManualProvider.Id, Manifest("gateway") with
        {
            Env = [new WorkloadEnv { Name = "HOST_ENDPOINT", From = "bootstrap:endpoint" }],
        });
        inputs = reconcile;

        await Build().RenderAsync(InstanceId, WorkloadArtifactFormatId.Parse("compose"), revealSecrets: false, actorUserId: null);

        var reconciledHash = Reconciled((await reconcile.GetInputsAsync(instance)).Inputs!, instance).SpecHash;
        var rendered = rendererRegistry.LastSpec!;
        rendered.Env.Single(e => e.Name == "HOST_ENDPOINT").Value.Should().Be("wss://operator.example/ws");
        rendered.SpecHash.Should().Be(reconciledHash,
            "a Manual runtime reports this hash, and the reconciler compares it with its own");
        rendered.Env.Single(e => e.Name == WorkloadDesiredSpecFactory.SpecHashEnv).Value.Should().Be(reconciledHash);
    }

    [Fact]
    public async Task A_manual_runtime_running_the_rendered_artifact_reads_live_to_the_reconciler_and_an_older_one_does_not()
    {
        namers = [new TaggedResourceNamer(ManualProvider.Id, "manual")];
        source = new PerAudienceSource();
        parameterReferences = new FixedReferenceResolver("s3cret");
        var instance = Instance("gateway");
        instance.SetProvider(ManualProvider.Id, DateTimeOffset.UnixEpoch);
        instance.SetResolvedParameters(
            new Dictionary<string, string?>(StringComparer.Ordinal) { ["dbPassword"] = "ref:provider:app#password" },
            DateTimeOffset.UnixEpoch);
        repository.Add(instance);
        var reconcile = ReconcilePath(ManualProvider.Id, Manifest("gateway") with
        {
            Health = new WorkloadHealth { Mode = RecordingRolloutCertifier.SessionMode, HeartbeatSeconds = 30 },
            Parameters = [new WorkloadParameter { Name = "dbPassword", Type = WorkloadParameterType.Password }],
            Env =
            [
                new WorkloadEnv { Name = "HOST_ENDPOINT", From = "bootstrap:endpoint" },
                new WorkloadEnv { Name = "DB_PASSWORD", From = "parameter:dbPassword" },
            ],
        });
        inputs = reconcile;
        var runtime = new InMemoryWorkloadProvider(ManualProvider.Descriptor);
        var presence = new FakeHostHealthContributor();
        var engine = new WorkloadReconcileEngine(
            reconcile,
            new DesiredSpecResolver(new NullActiveWorkloadIntentProvider()),
            new WorkloadProviderRegistry([runtime]),
            repository,
            presence.Dispatch(),
            new AdvancingClock(),
            NullLogger<WorkloadReconcileEngine>.Instance,
            NSubstitute.Substitute.For<IRetainedVolumeService>(),
            new NullActiveWorkloadIntentProvider(),
            parameterReferences,
            parameterSecrets,
            null,
            new UnpinnedReferenceRolloutGuard(new EphemeralDataProtectionProvider()),
            new NoCredentialPort());
        await engine.ReconcileInstanceAsync(instance);
        runtime.SimulateReady(instance.Id);

        await Build().RenderAsync(InstanceId, WorkloadArtifactFormatId.Parse("compose"), revealSecrets: false, actorUserId: null);
        var renderedHash = rendererRegistry.LastSpec!.SpecHash;
        renderedHash.Should().Be(instance.DesiredSpecHash, "the artifact must carry the hash the reconciler desires");

        presence.SeedMatching(instance.Id, renderedHash);
        await engine.ReconcileInstanceAsync(instance);
        instance.State.Should().Be(WorkloadInstanceState.ExternallyManaged);

        presence.SeedMatching(instance.Id, "hash-of-an-older-artifact");
        await engine.ReconcileInstanceAsync(instance);
        instance.State.Should().Be(WorkloadInstanceState.Deploying);
        instance.StateMessage.Should().Contain("redeploy");

        presence.SeedMatching(instance.Id, renderedHash);
        await engine.ReconcileInstanceAsync(instance);
        instance.State.Should().Be(WorkloadInstanceState.ExternallyManaged);
    }

    [Fact]
    public async Task A_parked_manual_version_update_passes_once_the_operator_runs_the_re_rendered_artifact()
    {
        namers = [new TaggedResourceNamer(ManualProvider.Id, "manual")];
        var instance = Instance("gateway");
        instance.SetProvider(ManualProvider.Id, DateTimeOffset.UnixEpoch);
        repository.Add(instance);
        inputs = ReconcilePath(ManualProvider.Id, Manifest("gateway"));
        var target = Manifest("gateway") with
        {
            Image = new WorkloadImage { Ref = "registry.example/gateway", Digest = "sha256:" + new string('c', 64) },
        };
        var plan = Update.WorkloadUpdatePlanBuilder.Plan(GroupInstanceId,
            [Update.WorkloadUpdatePlanBuilder.Item("gateway", InstanceId, "hash-target", "hash-installed", ManualProvider.Id)]);
        manifests.AtVersion[plan.ToVersion] = new GroupManifestRead(
            new DeploymentManifest { SchemaVersion = 1, Workloads = [target] }, Refused: false);
        var update = new Update.OrchestratorHarness(plan, providerKind: ManualProvider.Id);
        var intent = await update.StageManualAsync(plan);
        await update.Orchestrator.AdvanceAsync(intent);
        await update.Orchestrator.AdvanceAsync(intent);
        await update.Orchestrator.AdvanceAsync(intent);
        (await update.Orchestrator.WalkAsync(intent)).Phase.Should().Be(WorkloadUpdatePhase.ApplyingSidecar);
        intent.CurrentStage.Should().Be(Reeve.Workloads.Abstractions.Update.WorkloadUpdateStages.OperatorRedeploy);
        intents = new WorkloadActiveIntentProvider(update.Repo);

        await Build().RenderAsync(InstanceId, WorkloadArtifactFormatId.Parse("compose"), revealSecrets: false, actorUserId: null);

        var rendered = rendererRegistry.LastSpec!;
        rendered.Image.Should().Contain(new string('c', 64), "the artifact deploys the version the update moves to");
        var runningVersion = rendered.Env.Single(e => e.Name == WorkloadDesiredSpecFactory.AppVersionEnv).Value;
        runningVersion.Should().Be(plan.ToVersion);
        rendered.SpecHash.Should().Be("hash-target", "the reconciler desires the update's target spec while it is open");
        update.ObservedVersions.VersionsByInstance[InstanceId] = runningVersion;
        var walked = await update.Orchestrator.WalkAsync(intent);
        walked.Phase.Should().Be(WorkloadUpdatePhase.Completed);
        update.Effects.CommitInstalledVersionCount.Should().Be(1);
    }

    [Fact]
    public async Task A_manual_update_that_adds_a_workload_commits_after_one_redeploy_and_then_renders_the_new_one()
    {
        namers = [new TaggedResourceNamer(ManualProvider.Id, "manual")];
        var instance = Instance("gateway");
        instance.SetProvider(ManualProvider.Id, DateTimeOffset.UnixEpoch);
        repository.Add(instance);
        inputs = ReconcilePath(ManualProvider.Id, Manifest("gateway"));
        var target = new DeploymentManifest { SchemaVersion = 1, Workloads = [Manifest("gateway"), Manifest("bridge")] };
        WorkloadUpdatePlan PlanFor(Guid intentId)
        {
            var addedId = Reeve.Workloads.Core.Update.GroupManifestUpdatePlanner.AddedWorkloadInstanceId(intentId, "bridge");
            var bridge = Update.WorkloadUpdatePlanBuilder.Item("bridge", addedId, "hash-bridge", null, ManualProvider.Id);
            return Update.WorkloadUpdatePlanBuilder.Plan(GroupInstanceId,
            [
                Update.WorkloadUpdatePlanBuilder.Item("gateway", InstanceId, "hash-target", "hash-installed", ManualProvider.Id),
                bridge with { TargetSpec = bridge.TargetSpec with { InstanceId = addedId } },
            ]);
        }

        var seed = PlanFor(Guid.Empty);
        manifests.AtVersion[seed.ToVersion] = new GroupManifestRead(target, Refused: false);
        var update = new Update.OrchestratorHarness(seed, providerKind: ManualProvider.Id);
        update.Planner.Real = new Update.IntentAwarePlanner(PlanFor);
        var intent = await update.StageManualAsync(seed);
        await update.Orchestrator.AdvanceAsync(intent);
        await update.Orchestrator.AdvanceAsync(intent);
        await update.Orchestrator.AdvanceAsync(intent);
        (await update.Orchestrator.WalkAsync(intent)).Phase.Should().Be(WorkloadUpdatePhase.ApplyingSidecar);
        intent.CurrentStage.Should().Be(Reeve.Workloads.Abstractions.Update.WorkloadUpdateStages.OperatorRedeploy);
        intents = new WorkloadActiveIntentProvider(update.Repo);

        var parked = await Build().RenderAsync(InstanceId, WorkloadArtifactFormatId.Parse("compose"),
            revealSecrets: false, actorUserId: null);
        parked.Workloads.Should().ContainSingle().Which.WorkloadId.Should().Be("gateway",
            "the added workload has no row, so no identity to render for, until the update commits");
        update.ObservedVersions.VersionsByInstance[InstanceId] = rendererRegistry.LastSpec!.Env
            .Single(e => e.Name == WorkloadDesiredSpecFactory.AppVersionEnv).Value;

        var walked = await update.Orchestrator.WalkAsync(intent);

        walked.Phase.Should().Be(WorkloadUpdatePhase.Completed,
            "the redeploy of the existing sidecar is all the stage waits on; the added one has no old sidecar to skew");
        update.Effects.CommitInstalledVersionCount.Should().Be(1);
        update.Rows.EnsureCount.Should().Be(1);

        intents = new NullActiveWorkloadIntentProvider();
        inputs = ReconcilePath(ManualProvider.Id, Manifest("gateway"), Manifest("bridge"));
        repository.Add(Instance("bridge", Reeve.Workloads.Core.Update.GroupManifestUpdatePlanner.AddedWorkloadInstanceId(intent.Id, "bridge")));
        var afterCommit = await Build().RenderAsync(InstanceId, WorkloadArtifactFormatId.Parse("compose"),
            revealSecrets: false, actorUserId: null);
        afterCommit.Workloads.Select(item => item.WorkloadId).Should().BeEquivalentTo(["bridge", "gateway"],
            "once its row exists the added workload renders like one of a fresh install");
    }

    [Fact]
    public async Task A_manual_artifact_rendered_while_an_update_walks_back_deploys_the_version_it_started_from()
    {
        namers = [new TaggedResourceNamer(ManualProvider.Id, "manual")];
        var instance = Instance("gateway");
        instance.SetProvider(ManualProvider.Id, DateTimeOffset.UnixEpoch);
        instance.SetResolvedParameters(
            new Dictionary<string, string?>(StringComparer.Ordinal) { ["logLevel"] = "debug" }, DateTimeOffset.UnixEpoch);
        repository.Add(instance);
        var parameterised = Manifest("gateway") with
        {
            Parameters = [new WorkloadParameter { Name = "logLevel", Type = WorkloadParameterType.Text }],
            Env = [new WorkloadEnv { Name = "LOG_LEVEL", From = "parameter:logLevel" }],
        };
        inputs = ReconcilePath(ManualProvider.Id, parameterised with
        {
            Image = new WorkloadImage { Ref = "registry.example/gateway", Digest = "sha256:" + new string('c', 64) },
        });
        manifests.AtVersion["0.9.0"] = new GroupManifestRead(
            new DeploymentManifest { SchemaVersion = 1, Workloads = [parameterised] }, Refused: false);
        intents = new StubReplay(new ActiveWorkloadIntentReplay(
            Update.WorkloadUpdatePlanBuilder.Spec("gateway", "hash-previous", ManualProvider.Id),
            WorkloadUpdateDirection.RollingBack,
            new Dictionary<string, string?>())
        {
            StoredParameters = new Dictionary<string, string?>(StringComparer.Ordinal) { ["logLevel"] = "info" },
            Version = "0.9.0",
        });

        await Build().RenderAsync(InstanceId, WorkloadArtifactFormatId.Parse("compose"), revealSecrets: false, actorUserId: null);

        var rendered = rendererRegistry.LastSpec!;
        rendered.Image.Should().Contain(new string('b', 64));
        rendered.Env.Single(e => e.Name == WorkloadDesiredSpecFactory.AppVersionEnv).Value.Should().Be("0.9.0");
        rendered.Env.Single(e => e.Name == "LOG_LEVEL").Value.Should().Be("info", "the rollback restores the stored values");
        rendered.SpecHash.Should().Be("hash-previous");
    }

    [Fact]
    public async Task A_reference_resolver_fault_refuses_the_render_by_name()
    {
        namers = [new TaggedResourceNamer(ManualProvider.Id, "manual")];
        var instance = Instance("gateway");
        instance.SetResolvedParameters(
            new Dictionary<string, string?>(StringComparer.Ordinal) { ["dbPassword"] = "ref:provider:app#password" },
            DateTimeOffset.UnixEpoch);
        repository.Add(instance);
        inputs = ReconcilePath(ManualProvider.Id, Manifest("gateway") with
        {
            Parameters = [new WorkloadParameter { Name = "dbPassword", Type = WorkloadParameterType.Password }],
        });
        parameterReferences = new FaultingReferenceResolver();

        var result = await Build().RenderAsync(InstanceId, WorkloadArtifactFormatId.Parse("compose"),
            revealSecrets: false, actorUserId: null);

        result.Outcome.Should().Be(WorkloadArtifactRenderOutcome.HostRefused);
        result.Error.Should().Contain("gateway").And.Contain("vault unreachable");
    }

    [Fact]
    public async Task The_artifact_keeps_the_reconcile_paths_version_and_restart_marker()
    {
        namers = [new TaggedResourceNamer(ManualProvider.Id, "manual")];
        var instance = Instance("gateway");
        instance.MarkRestart(DateTimeOffset.UnixEpoch);
        instance.MarkRestart(DateTimeOffset.UnixEpoch);
        repository.Add(instance);
        inputs = ReconcilePath(ManualProvider.Id, SecretBoundManifest("gateway"));

        await Build().RenderAsync(InstanceId, WorkloadArtifactFormatId.Parse("compose"), revealSecrets: false, actorUserId: null);

        var rendered = rendererRegistry.LastSpec!;
        rendered.RestartNonce.Should().Be(2);
        rendered.Env.Single(e => e.Name == WorkloadDesiredSpecFactory.AppVersionEnv).Value.Should().Be("1.0.0");
    }

    [Fact]
    public async Task A_manual_effective_render_carries_the_reconcile_paths_hash()
    {
        namers = [new TaggedResourceNamer(ManualProvider.Id, "manual")];
        var instance = Instance("gateway");
        repository.Add(instance);
        var reconcile = ReconcilePath(ManualProvider.Id, SecretBoundManifest("gateway"));
        inputs = reconcile;

        await Build().RenderAsync(InstanceId, WorkloadArtifactFormatId.Parse("compose"), revealSecrets: false, actorUserId: null);

        var answer = (await reconcile.GetInputsAsync(instance)).Inputs!;
        rendererRegistry.LastSpec!.SpecHash.Should().Be(Reconciled(answer, instance).SpecHash);
    }

    [Fact]
    public async Task A_host_that_issues_no_credential_falls_back_to_external_secret_mode()
    {
        repository.Add(Instance("gateway"));
        inputsProvider.Manifest = Manifest("gateway");
        credentialPort.Answer = (_, _) => null;

        var result = await Build().RenderAsync(InstanceId, WorkloadArtifactFormatId.Parse("compose"), revealSecrets: true, actorUserId: null);

        result.SecretsRevealed.Should().BeFalse("no live credential was revealed, so the response must not say one was");
        rendererRegistry.LastOptions!.RevealedCredentials.Should().BeEmpty(
            "a null answer is no value: carried as an empty one it would be written in place of the placeholder");
        rendererRegistry.LastOptions.ExternalSecretMode.Should().BeTrue();
    }

    [Fact]
    public async Task A_reveal_that_mints_reports_the_reveal_and_renders_inline()
    {
        repository.Add(Instance("gateway"));
        inputsProvider.Manifest = Manifest("gateway");
        credentialPort.Answer = (_, _) => "live-token";

        var result = await Build().RenderAsync(InstanceId, WorkloadArtifactFormatId.Parse("compose"), revealSecrets: true, actorUserId: null);

        result.SecretsRevealed.Should().BeTrue();
        Revealed(rendererRegistry.LastOptions!).Should().Be("live-token");
        rendererRegistry.LastOptions!.ExternalSecretMode.Should().BeFalse();
    }

    [Fact]
    public async Task A_reveal_passes_no_existing_material_so_it_can_never_rotate()
    {
        repository.Add(Instance("gateway"));
        inputsProvider.Manifest = Manifest("gateway");

        await Build().RenderAsync(InstanceId, WorkloadArtifactFormatId.Parse("compose"), revealSecrets: true, actorUserId: null);

        credentialPort.Asks.Should().ContainSingle();
        credentialPort.Asks.Single().Existing.Should().BeNull();
    }

    [Fact]
    public async Task Each_workload_of_a_group_reveals_its_own_instances_token()
    {
        var bridgeId = Guid.Parse("22222222-2222-2222-2222-222222222222");
        repository.Add(Instance("gateway"));
        repository.Add(Instance("bridge", bridgeId));
        inputsProvider.Manifest = Manifest("gateway");
        credentialPort.Answer = (id, _) => $"token-{id:N}";

        var result = await Build().RenderAsync(InstanceId, WorkloadArtifactFormatId.Parse("compose"), revealSecrets: true, actorUserId: null);

        result.SecretsRevealed.Should().BeTrue();
        Revealed(rendererRegistry.OptionsByWorkload["gateway"]).Should().Be($"token-{InstanceId:N}");
        Revealed(rendererRegistry.OptionsByWorkload["bridge"]).Should().Be($"token-{bridgeId:N}");
        rendererRegistry.OptionsByWorkload.Values.Should().AllSatisfy(o => o.ExternalSecretMode.Should().BeFalse());
        credentialPort.Asks.Select(a => a.InstanceId).Should().BeEquivalentTo([InstanceId, bridgeId]);
        credentialPort.Asks.Select(a => a.Existing).Should().AllSatisfy(m => m.Should().BeNull(
            "resolving a sibling's token must mint beside its live credential, never rotate it"));
    }

    [Fact]
    public async Task The_reveal_audit_names_every_instance_whose_token_was_revealed()
    {
        var bridgeId = Guid.Parse("22222222-2222-2222-2222-222222222222");
        repository.Add(Instance("gateway"));
        repository.Add(Instance("bridge", bridgeId));
        inputsProvider.Manifest = Manifest("gateway");

        await Build().RenderAsync(InstanceId, WorkloadArtifactFormatId.Parse("compose"), revealSecrets: true, actorUserId: null);

        auditSink.Entries.Should().AllSatisfy(e => e.Action.Should().Be("workloads.artifact.revealed"));
        auditSink.Entries.Select(e => e.EntityId).Should().BeEquivalentTo([InstanceId, bridgeId]);
        auditSink.Entries.Should().AllSatisfy(e => System.Text.Json.JsonSerializer.Serialize(e.Metadata)
            .Should().Contain($"\"instanceId\":\"{e.EntityId}\""));
    }

    [Fact]
    public async Task A_reveal_that_inlines_any_workloads_token_reports_the_reveal()
    {
        var zuluId = Guid.Parse("33333333-3333-3333-3333-333333333333");
        repository.Add(Instance("gateway"));
        repository.Add(Instance("zulu", zuluId));
        inputsProvider.Manifest = Manifest("gateway");
        credentialPort.Answer = (id, _) => id == InstanceId ? "gateway-token" : null;

        var result = await Build().RenderAsync(zuluId, WorkloadArtifactFormatId.Parse("compose"), revealSecrets: true, actorUserId: null);

        result.SecretsRevealed.Should().BeTrue();
        Revealed(rendererRegistry.OptionsByWorkload["gateway"]).Should().Be("gateway-token");
        rendererRegistry.OptionsByWorkload["zulu"].ExternalSecretMode.Should().BeTrue(
            "the instance with no credential still gets the placeholder, not another instance's token");
    }

    [Fact]
    public async Task A_workload_the_manifest_dropped_is_minted_no_token()
    {
        var bridgeId = Guid.Parse("22222222-2222-2222-2222-222222222222");
        repository.Add(Instance("gateway"));
        repository.Add(Instance("bridge", bridgeId));
        inputsProvider.Manifest = Manifest("gateway");
        inputsProvider.UndeclaredWorkloadIds.Add("bridge");

        await Build().RenderAsync(bridgeId, WorkloadArtifactFormatId.Parse("compose"), revealSecrets: true, actorUserId: null);

        credentialPort.Asks.Select(a => a.InstanceId).Should().Equal(InstanceId);
        auditSink.Entries.Select(e => e.EntityId).Should().Equal(InstanceId);
    }

    [Theory]
    [InlineData("managed")]
    [InlineData("externally-managed")]
    [InlineData("mid-switch")]
    public async Task A_reveal_mints_nothing_for_a_workload_the_host_holds(string hold)
    {
        var bridgeId = Guid.Parse("22222222-2222-2222-2222-222222222222");
        repository.Add(Instance("gateway"));
        var held = Instance("bridge", bridgeId);
        var at = DateTimeOffset.UnixEpoch;
        held.TransitionTo(WorkloadInstanceState.Deploying, WorkloadStateReason.Provisioning, null, at);
        switch (hold)
        {
            case "managed":
                held.TransitionTo(WorkloadInstanceState.Suspended, WorkloadStateReason.SuspendedByHost, "withdrawn", at);
                break;
            case "externally-managed":
                held.TransitionTo(WorkloadInstanceState.ExternallyManaged, WorkloadStateReason.ExternalHealthObserved, null, at);
                held.TransitionTo(WorkloadInstanceState.Suspended, WorkloadStateReason.SuspendedByHostExternallyManaged, "withdrawn", at);
                break;
            default:
                held.TransitionTo(WorkloadInstanceState.Healthy, WorkloadStateReason.HealthObserved, null, at);
                held.TransitionTo(WorkloadInstanceState.Switching, WorkloadStateReason.HealthObserved, null, at);
                held.TransitionTo(WorkloadInstanceState.Switching, WorkloadStateReason.SuspendedByHost, "withdrawn", at);
                break;
        }

        repository.Add(held);
        inputsProvider.Manifest = Manifest("gateway");

        await Build().RenderAsync(InstanceId, WorkloadArtifactFormatId.Parse("compose"), revealSecrets: true, actorUserId: null);

        credentialPort.Asks.Select(a => a.InstanceId).Should().Equal(InstanceId);
        rendererRegistry.OptionsByWorkload["bridge"].ExternalSecretMode.Should().BeTrue();
    }

    [Fact]
    public async Task A_plain_render_never_reaches_the_credential_port()
    {
        repository.Add(Instance("gateway"));
        inputsProvider.Manifest = Manifest("gateway");

        var result = await Build().RenderAsync(InstanceId, WorkloadArtifactFormatId.Parse("compose"), revealSecrets: false, actorUserId: null);

        credentialPort.Asks.Should().BeEmpty();
        result.SecretsRevealed.Should().BeFalse();
        rendererRegistry.LastOptions!.ExternalSecretMode.Should().BeTrue();
    }

    [Fact]
    public async Task A_reveal_with_no_credential_to_give_is_still_audited()
    {
        repository.Add(Instance("gateway"));
        inputsProvider.Manifest = Manifest("gateway");
        credentialPort.Answer = (_, _) => null;

        var result = await Build().RenderAsync(InstanceId, WorkloadArtifactFormatId.Parse("compose"), revealSecrets: true, actorUserId: null);

        auditSink.Entries.Should().ContainSingle().Which.Action.Should().Be("workloads.artifact.revealed");
        result.SecretsRevealed.Should().BeFalse();
    }

    [Fact]
    public async Task A_reveal_resolves_each_siblings_own_credentials_once_per_id()
    {
        var bridgeId = Guid.Parse("22222222-2222-2222-2222-222222222222");
        host = new FakeHostBootstrapSource
        {
            ProvisionedCredentials =
            [
                new ProvisionedCredential(Token, "REEVE_WORKLOAD_TOKEN"),
                new ProvisionedCredential("second", "SECOND_TOKEN"),
            ],
        };
        repository.Add(Instance("gateway"));
        repository.Add(Instance("bridge", bridgeId));
        inputsProvider.Manifest = Manifest("gateway") with
        {
            Env = [new WorkloadEnv { Name = "WORKLOAD_TOKEN", From = "bootstrap:workloadToken" }],
        };
        credentialPort.Answer = (id, credential) => $"{credential}-{id:N}";

        var result = await Build().RenderAsync(InstanceId, WorkloadArtifactFormatId.Parse("compose"), revealSecrets: true, actorUserId: null);

        result.SecretsRevealed.Should().BeTrue();
        credentialPort.Asks.Should().BeEquivalentTo(
            new (Guid, string, string?)[]
            {
                (InstanceId, Token, null), (InstanceId, "second", null),
                (bridgeId, Token, null), (bridgeId, "second", null),
            },
            "a reveal mints beside the live credential, once per sibling and id");
        rendererRegistry.OptionsByWorkload["gateway"].RevealedCredentials.Should().BeEquivalentTo(
            new Dictionary<string, string> { [Token] = $"{Token}-{InstanceId:N}", ["second"] = $"second-{InstanceId:N}" });
        rendererRegistry.OptionsByWorkload["bridge"].RevealedCredentials.Should().BeEquivalentTo(
            new Dictionary<string, string> { [Token] = $"{Token}-{bridgeId:N}", ["second"] = $"second-{bridgeId:N}" });
    }

    [Fact]
    public async Task A_reveal_never_inlines_a_password_and_leaves_its_placeholder()
    {
        var instance = Instance("gateway");
        inputsProvider.Manifest = PasswordAndCredentialManifest("gateway");
        instance.SetResolvedParameters(
            parameterSecrets.Protect(inputsProvider.Manifest, new Dictionary<string, string?> { ["dbPassword"] = "hunter2" }),
            DateTimeOffset.UnixEpoch);
        repository.Add(instance);
        var renderers = new WorkloadArtifactRendererRegistry([new EnvArtifactRenderer(), new KubernetesArtifactRenderer()]);

        var env = await Build(renderers).RenderAsync(InstanceId, WorkloadArtifactFormatId.Parse("env"), revealSecrets: true, actorUserId: null);
        var kubernetes = await Build(renderers).RenderAsync(InstanceId, WorkloadArtifactFormatId.Parse("kubernetes"), revealSecrets: true, actorUserId: null);

        var envFile = env.Workloads.Single().Artifact!.PrimaryContent;
        envFile.Should().Contain("WORKLOAD_TOKEN=live-token")
            .And.Contain("DB_PASSWORD=<set-this-secret>")
            .And.Contain("MQTT_PASSWORD=<set-this-secret>")
            .And.NotContain("hunter2").And.NotContain("not-a-real-password");
        var oneLiner = kubernetes.Workloads.Single().Artifact!.SecretOneLiner;
        oneLiner.Should().Contain("--from-literal='REEVE_WORKLOAD_TOKEN=live-token'")
            .And.Contain("--from-literal='WORKLOAD_TOKEN=live-token'")
            .And.NotContain("DB_PASSWORD").And.NotContain("MQTT_PASSWORD")
            .And.NotContain("hunter2").And.NotContain("not-a-real-password");
    }

    [Fact]
    public async Task No_reveal_writes_a_placeholder_for_a_credential_the_host_has_no_value_for()
    {
        host = new FakeHostBootstrapSource
        {
            ProvisionedCredentials =
            [
                new ProvisionedCredential(Token, "REEVE_WORKLOAD_TOKEN"),
                new ProvisionedCredential("unissued", "UNISSUED_TOKEN"),
            ],
        };
        repository.Add(Instance("gateway"));
        inputsProvider.Manifest = PasswordAndCredentialManifest("gateway");
        credentialPort.Answer = (_, credential) => credential == Token ? "live-token" : null;
        var renderers = new WorkloadArtifactRendererRegistry([new EnvArtifactRenderer(), new KubernetesArtifactRenderer()]);

        var kubernetes = await Build(renderers).RenderAsync(InstanceId, WorkloadArtifactFormatId.Parse("kubernetes"), revealSecrets: true, actorUserId: null);
        var env = await Build(renderers).RenderAsync(InstanceId, WorkloadArtifactFormatId.Parse("env"), revealSecrets: true, actorUserId: null);

        var oneLiner = kubernetes.Workloads.Single().Artifact!.SecretOneLiner;
        oneLiner.Should().Contain("--from-literal='REEVE_WORKLOAD_TOKEN=live-token'")
            .And.NotContain("UNISSUED_TOKEN").And.NotContain(WorkloadEnvRendering.SecretPlaceholder);
        env.Workloads.Single().Artifact!.PrimaryContent.Should().NotContain("UNISSUED_TOKEN",
            "a provisioned credential is not a declared env, so only the one-liner could carry it");
    }

    [Fact]
    public async Task A_reveal_asks_nothing_for_a_workload_whose_spec_a_provider_would_refuse()
    {
        repository.Add(Instance("gateway"));
        inputsProvider.Manifest = Manifest("gateway") with
        {
            Env =
            [
                new WorkloadEnv { Name = "FEED_URL", From = "literal:declared" },
                new WorkloadEnv { Name = "FEED_URL", From = "bootstrap:workloadToken" },
            ],
        };
        var renderers = new WorkloadArtifactRendererRegistry([new EnvArtifactRenderer()]);

        var result = await Build(renderers).RenderAsync(InstanceId, WorkloadArtifactFormatId.Parse("env"), revealSecrets: true, actorUserId: null);

        credentialPort.Asks.Should().BeEmpty();
        result.SecretsRevealed.Should().BeFalse();
        result.Workloads.Single().Artifact!.PrimaryContent.Should().Contain("FEED_URL=declared")
            .And.Contain($"FEED_URL={WorkloadEnvRendering.SecretPlaceholder}")
            .And.NotContain("live-token");
    }

    private static WorkloadManifest PasswordAndCredentialManifest(string workloadId) => Manifest(workloadId) with
    {
        Parameters = [new WorkloadParameter { Name = "dbPassword", Type = WorkloadParameterType.Password, Required = true }],
        Env =
        [
            new WorkloadEnv { Name = "DB_PASSWORD", From = "parameter:dbPassword" },
            new WorkloadEnv { Name = "WORKLOAD_TOKEN", From = "bootstrap:workloadToken" },
            new WorkloadEnv { Name = "MQTT_PASSWORD", From = "bootstrap:mqttPassword" },
        ],
    };

    [Fact]
    public async Task A_failing_audit_sink_does_not_fail_the_reveal()
    {
        repository.Add(Instance("gateway"));
        inputsProvider.Manifest = Manifest("gateway");
        auditSink.Fault = new InvalidOperationException("audit db down");

        var result = await Build().RenderAsync(InstanceId, WorkloadArtifactFormatId.Parse("compose"), revealSecrets: true, actorUserId: null);

        result.Outcome.Should().Be(WorkloadArtifactRenderOutcome.Success);
        result.SecretsRevealed.Should().BeTrue();
    }

    [Fact]
    public async Task The_reveal_audit_names_the_actor_and_carries_no_token()
    {
        repository.Add(Instance("gateway"));
        inputsProvider.Manifest = Manifest("gateway");
        credentialPort.Answer = (_, _) => "live-token";
        var actor = Guid.Parse("99999999-9999-9999-9999-999999999999");

        await Build().RenderAsync(InstanceId, WorkloadArtifactFormatId.Parse("compose"), revealSecrets: true, actorUserId: actor);

        var entry = auditSink.Entries.Should().ContainSingle().Subject;
        entry.Action.Should().Be("workloads.artifact.revealed");
        entry.ActorUserId.Should().Be(actor);
        entry.EntityId.Should().Be(InstanceId);
        System.Text.Json.JsonSerializer.Serialize(entry.Metadata).Should().NotContain("live-token");
    }

    [Fact]
    public async Task The_reveal_audit_spells_the_format_the_way_6x_did()
    {
        repository.Add(Instance("gateway"));
        inputsProvider.Manifest = Manifest("gateway");
        credentialPort.Answer = (_, _) => "live-token";

        await Build().RenderAsync(InstanceId, WorkloadArtifactFormatId.Parse("helm-values"), revealSecrets: true, actorUserId: null);

        System.Text.Json.JsonSerializer.Serialize(auditSink.Entries.Should().ContainSingle().Subject.Metadata)
            .Should().Contain("\"format\":\"HelmValues\"");
    }

    [Fact]
    public async Task A_plain_render_writes_no_reveal_audit()
    {
        repository.Add(Instance("gateway"));
        inputsProvider.Manifest = Manifest("gateway");

        await Build().RenderAsync(InstanceId, WorkloadArtifactFormatId.Parse("compose"), revealSecrets: false, actorUserId: null);

        auditSink.Entries.Should().BeEmpty();
    }

    [Fact]
    public async Task A_workload_the_manifest_dropped_is_skipped_without_faulting_the_set()
    {
        repository.Add(Instance("gateway"));
        repository.Add(Instance("bridge", Guid.Parse("22222222-2222-2222-2222-222222222222")));
        inputsProvider.Manifest = Manifest("gateway");
        inputsProvider.UndeclaredWorkloadIds.Add("bridge");

        var result = await Build().RenderAsync(InstanceId, WorkloadArtifactFormatId.Parse("compose"), revealSecrets: false, actorUserId: null);

        result.Outcome.Should().Be(WorkloadArtifactRenderOutcome.Success);
        result.Workloads.Select(w => w.WorkloadId).Should().Equal("gateway");
    }

    [Fact]
    public async Task One_refused_sibling_refuses_the_whole_render_and_mints_nothing()
    {
        repository.Add(Instance("gateway"));
        repository.Add(Instance("bridge", Guid.Parse("22222222-2222-2222-2222-222222222222")));
        inputsProvider.Manifest = Manifest("gateway");
        inputsProvider.RefusedWorkloadIds["gateway"] = "host says no";

        var result = await Build().RenderAsync(InstanceId, WorkloadArtifactFormatId.Parse("compose"), revealSecrets: true, actorUserId: null);

        result.Outcome.Should().Be(WorkloadArtifactRenderOutcome.HostRefused);
        result.Error.Should().Contain("host says no");
        result.Workloads.Should().BeEmpty();
        credentialPort.Asks.Should().BeEmpty();
        auditSink.Entries.Should().BeEmpty();
    }

    [Fact]
    public async Task A_parameter_resolution_fault_refuses_the_whole_render_and_mints_nothing()
    {
        repository.Add(Instance("gateway"));
        repository.Add(Instance("bridge", Guid.Parse("22222222-2222-2222-2222-222222222222")));
        inputsProvider.Manifest = Manifest("gateway");
        inputsProvider.FaultingWorkloadIds.Add("bridge");

        var result = await Build().RenderAsync(InstanceId, WorkloadArtifactFormatId.Parse("compose"), revealSecrets: true, actorUserId: null);

        result.Outcome.Should().Be(WorkloadArtifactRenderOutcome.HostRefused);
        result.Error.Should().Contain("bridge").And.Contain("poisoned stored path");
        result.Workloads.Should().BeEmpty();
        credentialPort.Asks.Should().BeEmpty();
        auditSink.Entries.Should().BeEmpty();
    }

    [Fact]
    public async Task A_renderer_fault_surfaces_as_a_per_workload_error_not_a_missing_row()
    {
        repository.Add(Instance("gateway"));
        repository.Add(Instance("bridge", Guid.Parse("22222222-2222-2222-2222-222222222222")));
        inputsProvider.Manifest = Manifest("gateway");
        rendererRegistry.FaultOn = "bridge";

        var result = await Build().RenderAsync(InstanceId, WorkloadArtifactFormatId.Parse("compose"), revealSecrets: false, actorUserId: null);

        result.Workloads.Should().HaveCount(2);
        var faulted = result.Workloads.Single(w => w.WorkloadId == "bridge");
        faulted.Artifact.Should().BeNull();
        faulted.Error.Should().NotBeNullOrWhiteSpace();
        result.Workloads.Single(w => w.WorkloadId == "gateway").Artifact.Should().NotBeNull();
    }

    [Fact]
    public async Task Artifacts_come_back_in_ordinal_workload_id_order()
    {
        foreach (var (id, guid) in new[] { ("zulu", "d"), ("alpha", "b"), ("Mike", "c") })
        {
            repository.Add(Instance(id, Guid.Parse($"{guid}{guid}{guid}{guid}{guid}{guid}{guid}{guid}-1111-1111-1111-111111111111")));
        }
        repository.Add(Instance("gateway"));
        inputsProvider.Manifest = Manifest("gateway");

        var result = await Build().RenderAsync(InstanceId, WorkloadArtifactFormatId.Parse("compose"), revealSecrets: false, actorUserId: null);

        result.Workloads.Select(w => w.WorkloadId).Should().Equal("Mike", "alpha", "gateway", "zulu");
    }

    [Fact]
    public async Task The_renderers_own_notes_reach_the_operator_unchanged()
    {
        repository.Add(Instance("gateway"));
        inputsProvider.Manifest = Manifest("gateway");
        rendererRegistry.Notes = ["mount the config first"];

        var result = await Build().RenderAsync(InstanceId, WorkloadArtifactFormatId.Parse("compose"), revealSecrets: false, actorUserId: null);

        result.Workloads.Single().Artifact!.Notes.Should().Equal("mount the config first");
    }

    [Fact]
    public async Task The_rendered_spec_is_named_by_the_manual_namer()
    {
        namers =
        [
            new TaggedResourceNamer(DockerProvider.Id, "docker-namer"),
            new TaggedResourceNamer(ManualProvider.Id, "manual-namer"),
        ];
        var instance = Instance("gateway");
        repository.Add(instance);
        inputsProvider.Manifest = Manifest("gateway");

        await Build().RenderAsync(InstanceId, WorkloadArtifactFormatId.Parse("compose"), revealSecrets: false, actorUserId: null);

        rendererRegistry.LastSpec!.ResourceName.Should().Be($"manual-namer-obj-gateway-{instance.Inst8}");
    }

    [Fact]
    public async Task An_unknown_instance_is_not_found_and_reads_nothing_else()
    {
        var result = await Build().RenderAsync(InstanceId, WorkloadArtifactFormatId.Parse("compose"), revealSecrets: true, actorUserId: null);

        result.Outcome.Should().Be(WorkloadArtifactRenderOutcome.NotFound);
        inputsProvider.Calls.Should().Be(0);
        host.Requests.Should().BeEmpty();
        credentialPort.Asks.Should().BeEmpty();
    }

    [Fact]
    public async Task Cancellation_propagates_rather_than_becoming_a_per_workload_error()
    {
        repository.Add(Instance("gateway"));
        inputsProvider.Manifest = Manifest("gateway");
        rendererRegistry.CancelOn = "gateway";

        await FluentActions
            .Awaiting(() => Build().RenderAsync(InstanceId, WorkloadArtifactFormatId.Parse("compose"), revealSecrets: false, actorUserId: null))
            .Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public void Every_missing_port_is_refused_at_construction()
    {
        var log = NullLogger<WorkloadArtifactService>.Instance;
        var providerRegistry = new WorkloadProviderRegistry([]);
        var namerRegistry = new WorkloadResourceNamerRegistry([]);
        var attempts = new Action[]
        {
            () => _ = new WorkloadArtifactService(null!, inputsProvider, host, rendererRegistry, credentialPort, providerRegistry, namerRegistry, auditSink, parameterSecrets, parameterReferences, intents, manifests, modules, log),
            () => _ = new WorkloadArtifactService(repository, null!, host, rendererRegistry, credentialPort, providerRegistry, namerRegistry, auditSink, parameterSecrets, parameterReferences, intents, manifests, modules, log),
            () => _ = new WorkloadArtifactService(repository, inputsProvider, null!, rendererRegistry, credentialPort, providerRegistry, namerRegistry, auditSink, parameterSecrets, parameterReferences, intents, manifests, modules, log),
            () => _ = new WorkloadArtifactService(repository, inputsProvider, host, null!, credentialPort, providerRegistry, namerRegistry, auditSink, parameterSecrets, parameterReferences, intents, manifests, modules, log),
            () => _ = new WorkloadArtifactService(repository, inputsProvider, host, rendererRegistry, null!, providerRegistry, namerRegistry, auditSink, parameterSecrets, parameterReferences, intents, manifests, modules, log),
            () => _ = new WorkloadArtifactService(repository, inputsProvider, host, rendererRegistry, credentialPort, null!, namerRegistry, auditSink, parameterSecrets, parameterReferences, intents, manifests, modules, log),
            () => _ = new WorkloadArtifactService(repository, inputsProvider, host, rendererRegistry, credentialPort, providerRegistry, null!, auditSink, parameterSecrets, parameterReferences, intents, manifests, modules, log),
            () => _ = new WorkloadArtifactService(repository, inputsProvider, host, rendererRegistry, credentialPort, providerRegistry, namerRegistry, null!, parameterSecrets, parameterReferences, intents, manifests, modules, log),
            () => _ = new WorkloadArtifactService(repository, inputsProvider, host, rendererRegistry, credentialPort, providerRegistry, namerRegistry, auditSink, parameterSecrets, parameterReferences, intents, manifests, modules, null!),
            () => _ = new WorkloadArtifactService(repository, inputsProvider, host, rendererRegistry, credentialPort, providerRegistry, namerRegistry, auditSink, null!, parameterReferences, intents, manifests, modules, log),
            () => _ = new WorkloadArtifactService(repository, inputsProvider, host, rendererRegistry, credentialPort, providerRegistry, namerRegistry, auditSink, parameterSecrets, null!, intents, manifests, modules, log),
            () => _ = new WorkloadArtifactService(repository, inputsProvider, host, rendererRegistry, credentialPort, providerRegistry, namerRegistry, auditSink, parameterSecrets, parameterReferences, null!, manifests, modules, log),
            () => _ = new WorkloadArtifactService(repository, inputsProvider, host, rendererRegistry, credentialPort, providerRegistry, namerRegistry, auditSink, parameterSecrets, parameterReferences, intents, null!, modules, log),
            () => _ = new WorkloadArtifactService(repository, inputsProvider, host, rendererRegistry, credentialPort, providerRegistry, namerRegistry, auditSink, parameterSecrets, parameterReferences, intents, manifests, null!, log),
        };

        attempts.Should().AllSatisfy(attempt => attempt.Should().Throw<ArgumentNullException>());
    }

    private static string? Revealed(WorkloadArtifactRenderOptions options)
        => options.RevealedCredentials.TryGetValue(Token, out var value) ? value : null;

    private static WorkloadInstance Instance(string workloadId, Guid? id = null)
        => WorkloadInstance.Create(
            id ?? InstanceId, GroupInstanceId, "com.reeve.matter", workloadId, DateTimeOffset.UnixEpoch);

    private static WorkloadManifest Manifest(string workloadId) => new()
    {
        Id = workloadId,
        DisplayName = "Gateway",
        Image = new WorkloadImage { Ref = "registry.example/gateway", Digest = "sha256:" + new string('b', 64) },
        Kind = WorkloadKind.SingleNode,
        Health = new WorkloadHealth { Mode = WorkloadHealthMode.Container },
    };

    private static WorkloadManifest SecretBoundManifest(string workloadId) => Manifest(workloadId) with
    {
        Env =
        [
            new WorkloadEnv { Name = "WORKLOAD_TOKEN", From = "bootstrap:workloadToken" },
            new WorkloadEnv { Name = "MQTT_PASSWORD", From = "bootstrap:mqttPassword" },
        ],
    };

    private GroupManifestReconcileInputsProvider ReconcilePath(WorkloadProviderId effective, params WorkloadManifest[] workloads)
    {
        modules = new InMemoryWorkloadGroupRepository();
        var deployment = new DeploymentManifest { SchemaVersion = 1, Workloads = [.. workloads] };
        manifests = new FakeManifests().Install(modules, GroupId, new InstalledGroupManifestRead
        {
            InstalledVersion = "1.0.0",
            AtInstalledVersion = deployment,
            HighestAvailable = deployment,
        });
        return new GroupManifestReconcileInputsProvider(
            manifests, modules, new DefaultSettings(effective), new NoIdentity(), providers,
            new WorkloadResourceNamerRegistry(namers), Host, Options.Create(new ProductImageOptions()),
            NullLogger<GroupManifestReconcileInputsProvider>.Instance,
            new WorkloadParameterSecretCodec(new EphemeralDataProtectionProvider()),
            new NoWorkloadParameterReferenceResolver(),
            new InMemoryRepository());
    }

    private static WorkloadDesiredSpec Reconciled(
        WorkloadReconcileInputs answer,
        WorkloadInstance instance,
        WorkloadProviderDescriptor? provider = null,
        IWorkloadResourceNamer? namer = null)
        => WorkloadDesiredSpecFactory.Compute(
            answer.WorkloadManifest, answer.Settings, answer.ResolvedParameters, answer.Bootstrap,
            provider ?? answer.EffectiveProvider, namer ?? answer.Namer, instance.GroupInstanceId, instance.GroupId,
            instance.Id, answer.InstallationId, answer.ProductImages);

    private sealed class FixedReferenceResolver(string value) : IWorkloadParameterReferenceResolver
    {
        public ValueTask<WorkloadReferenceResolution> ResolveAsync(string reference, CancellationToken cancellationToken = default)
            => ValueTask.FromResult<WorkloadReferenceResolution>(new WorkloadReferenceResolution.Success(value));
    }

    private sealed class FaultingReferenceResolver : IWorkloadParameterReferenceResolver
    {
        public ValueTask<WorkloadReferenceResolution> ResolveAsync(string reference, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("vault unreachable");
    }

    private sealed class StubReplay(ActiveWorkloadIntentReplay replay) : IActiveWorkloadIntentProvider
    {
        public Task<WorkloadDesiredSpec?> TryGetActiveTargetSpecAsync(Guid pluginInstanceId, string workloadId, CancellationToken cancellationToken)
            => Task.FromResult<WorkloadDesiredSpec?>(replay.Spec);

        public Task<ActiveWorkloadIntentReplay?> TryGetActiveReplayAsync(Guid pluginInstanceId, string workloadId, CancellationToken cancellationToken)
            => Task.FromResult<ActiveWorkloadIntentReplay?>(replay);
    }

    private sealed class AdvancingClock : IWorkloadClock
    {
        private long ticks;

        public DateTimeOffset UtcNow => DateTimeOffset.UnixEpoch.AddSeconds(Interlocked.Increment(ref ticks));
    }

    private sealed class PerAudienceSource : IWorkloadHostBootstrapSource
    {
        public Task<WorkloadHostBootstrapResult> GetAsync(
            WorkloadHostBootstrapRequest request, CancellationToken cancellationToken = default)
            => Task.FromResult(new WorkloadHostBootstrapResult
            {
                Values = new Dictionary<string, BootstrapValue>(StringComparer.Ordinal)
                {
                    ["endpoint"] = BootstrapValue.Literal(request.Audience == WorkloadBootstrapAudience.HandApplied
                        ? "wss://operator.example/ws"
                        : "ws://in-cluster:8080/ws"),
                },
                HostEnv =
                [
                    new ResolvedEnvVar { Name = "FORCED", Kind = ResolvedEnvKind.Literal, Value = request.Audience.ToString() },
                ],
                ProvisionedCredentials =
                [
                    request.Audience == WorkloadBootstrapAudience.HandApplied
                        ? new ProvisionedCredential("operator-credential", "OPERATOR_TOKEN")
                        : new ProvisionedCredential("runtime-credential", "RUNTIME_TOKEN"),
                ],
            });
    }

    private sealed class InMemoryRepository : IWorkloadInstanceRepository
    {
        private readonly List<WorkloadInstance> instances = [];

        public void Add(WorkloadInstance instance) => instances.Add(instance);

        public Task<WorkloadInstance?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
            => Task.FromResult(instances.FirstOrDefault(i => i.Id == id));

        public Task<IReadOnlyList<WorkloadInstance>> ListByPluginInstanceAsync(Guid groupInstanceId, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<WorkloadInstance>>(
                instances.Where(i => i.GroupInstanceId == groupInstanceId).ToList());

        public Task<IReadOnlyList<WorkloadInstance>> ListAllAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<WorkloadInstance>>(instances.ToList());

        public Task AddAsync(WorkloadInstance instance, CancellationToken cancellationToken = default)
        {
            instances.Add(instance);
            return Task.CompletedTask;
        }

        public Task UpdateAsync(WorkloadInstance instance, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task DeleteAsync(WorkloadInstance instance, CancellationToken cancellationToken = default)
        {
            instances.RemoveAll(i => i.Id == instance.Id);
            return Task.CompletedTask;
        }

        public Task<WorkloadInstance?> GetByWorkloadAsync(Guid groupInstanceId, string workloadId, CancellationToken cancellationToken = default)
            => Task.FromResult(instances.FirstOrDefault(
                i => i.GroupInstanceId == groupInstanceId && i.WorkloadId == workloadId));

        public Task<IReadOnlyList<WorkloadInstance>> ListByStateAsync(WorkloadInstanceState state, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<WorkloadInstance>>(instances.Where(i => i.State == state).ToList());

        public Task<IReadOnlyDictionary<WorkloadInstanceState, int>> CountByStateAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyDictionary<WorkloadInstanceState, int>>(
                instances.GroupBy(i => i.State).ToDictionary(g => g.Key, g => g.Count()));

        public Task<bool> TrySetRestoreBindingUnresolvedAsync(Guid id, DateTimeOffset atUtc, CancellationToken cancellationToken = default)
            => throw new NotSupportedException("A render never writes.");
    }

    private sealed class FakeInputsProvider : IWorkloadReconcileInputsProvider
    {
        public WorkloadManifest? Manifest { get; set; }
        public IReadOnlyDictionary<string, string?> ParameterReferencesForHash { get; set; } = new Dictionary<string, string?>();

        public IWorkloadResourceNamer Namer { get; set; } = DefaultWorkloadResourceNamer.Instance;

        public int Calls { get; private set; }

        public HashSet<string> UndeclaredWorkloadIds { get; } = new(StringComparer.Ordinal);

        public Dictionary<string, string> RefusedWorkloadIds { get; } = new(StringComparer.Ordinal);

        public HashSet<string> FaultingWorkloadIds { get; } = new(StringComparer.Ordinal);

        public Task<WorkloadReconcileInputsResult> GetInputsAsync(WorkloadInstance instance, CancellationToken cancellationToken = default)
        {
            Calls++;
            if (RefusedWorkloadIds.TryGetValue(instance.WorkloadId, out var refusal))
            {
                return Task.FromResult(WorkloadReconcileInputsResult.HostRefused(refusal));
            }

            if (Manifest is null || UndeclaredWorkloadIds.Contains(instance.WorkloadId))
            {
                return ReconcileInputsAnswers.Of(null);
            }

            return ReconcileInputsAnswers.Of(new WorkloadReconcileInputs
            {
                Namer = Namer,
                WorkloadManifest = Manifest with { Id = instance.WorkloadId },
                ResolvedParameters = FaultingWorkloadIds.Contains(instance.WorkloadId)
                    ? new ThrowingParameters(new InvalidOperationException("poisoned stored path"))
                    : instance.GetResolvedParameters(),
                ParameterReferencesForHash = ParameterReferencesForHash,
                Settings = new WorkloadOrchestratorSettings(),
                Bootstrap = new WorkloadBootstrapContext { InstanceId = instance.Id, SecretName = "reeve-wl-secret" },
                EffectiveProvider = ManualProvider.Descriptor,
                InstallationId = "test-install",
            });
        }
    }

    private sealed class ThrowingParameters(Exception fault) : IReadOnlyDictionary<string, string?>
    {
        public string? this[string key] => throw fault;

        public IEnumerable<string> Keys => throw fault;

        public IEnumerable<string?> Values => throw fault;

        public int Count => throw fault;

        public bool ContainsKey(string key) => throw fault;

        public bool TryGetValue(string key, out string? value) => throw fault;

        public IEnumerator<KeyValuePair<string, string?>> GetEnumerator() => throw fault;

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => throw fault;
    }

    private sealed class FakeRendererRegistry : IWorkloadArtifactRendererRegistry
    {
        public WorkloadArtifactRenderOptions? LastOptions { get; private set; }

        public WorkloadDesiredSpec? LastSpec { get; private set; }

        public Dictionary<string, WorkloadArtifactRenderOptions> OptionsByWorkload { get; } = new(StringComparer.Ordinal);

        public IReadOnlyList<string> Notes { get; set; } = [];

        public string? FaultOn { get; set; }

        public string? CancelOn { get; set; }

        public IReadOnlyCollection<WorkloadArtifactFormatId> SupportedFormats { get; } =
            [WorkloadArtifactFormatId.Parse("compose"), WorkloadArtifactFormatId.Parse("kubernetes")];

        public IReadOnlyList<WorkloadArtifactFormatId> SelfContainedFormats { get; } =
            [WorkloadArtifactFormatId.Parse("compose"), WorkloadArtifactFormatId.Parse("kubernetes")];

        public RenderedWorkloadArtifact Render(
            WorkloadArtifactFormatId format, WorkloadDesiredSpec spec, WorkloadArtifactRenderOptions options)
        {
            LastOptions = options;
            LastSpec = spec;
            OptionsByWorkload[spec.WorkloadId] = options;
            if (CancelOn == spec.WorkloadId)
            {
                throw new OperationCanceledException();
            }

            if (FaultOn == spec.WorkloadId)
            {
                throw new InvalidOperationException("pod patch is not valid yaml");
            }

            return new RenderedWorkloadArtifact
            {
                Format = format,
                PrimaryContent = $"# {spec.WorkloadId}",
                Notes = Notes,
            };
        }
    }

    private sealed class RecordingAuditSink : IWorkloadAuditSink
    {
        public List<WorkloadAuditEntry> Entries { get; } = [];

        public Exception? Fault { get; set; }

        public Task WriteAsync(WorkloadAuditEntry entry, CancellationToken cancellationToken = default)
        {
            if (Fault is not null)
            {
                return Task.FromException(Fault);
            }

            Entries.Add(entry);
            return Task.CompletedTask;
        }
    }
}
