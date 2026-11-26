using FluentAssertions;
using FluentAssertions.Execution;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Reeve.Workloads.Abstractions;
using Reeve.Workloads.Abstractions.Contracts;
using Reeve.Workloads.Abstractions.Contracts.Deployment;
using Reeve.Workloads.Abstractions.Domain;
using Reeve.Workloads.Abstractions.Domain.DesiredSpec;
using Reeve.Workloads.Abstractions.Domain.Entities;
using Reeve.Workloads.Abstractions.Providers;
using Reeve.Workloads.Abstractions.Update;
using Reeve.Workloads.Core.Update;
using Reeve.Workloads.Core.Update.Stages;
using Reeve.Workloads.Domain;
using Reeve.Workloads.Domain.DesiredSpec;
using Reeve.Workloads.Providers.Kubernetes;
using Reeve.Workloads.Tests.Shared;
using Xunit;

namespace Reeve.Workloads.Core.Tests.Update;

public sealed class RolloutWindowCredentialTests
{
    private const string WorkloadId = "sidecar";
    private const string TokenEnv = "WORKLOAD_TOKEN";
    private const string TokenKey = "workloadToken";
    private const string CredentialId = "token";
    private const string UnownedEnv = "NEW_SECRET";
    private const string FromVersion = "2.5.0";
    private const string ToVersion = "2.6.0";
    private const string DbPasswordEnv = "DB_PASSWORD";
    private const string DbPasswordKey = "dbPassword";
    private const string DbPassword = "not-a-real-password";
    private const string UpdatedDbPassword = "updated-not-a-real-password";
    private const string DbCredentialId = "db";
    private static readonly DateTimeOffset T0 = new(2026, 9, 26, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task A_rollback_restores_a_stored_spec_with_its_credential_id_and_ends_rolled_back()
    {
        var h = await Rig.CreateAsync(password: true, submittedPasswordChange: true);
        h.Gate.ForwardVerdict = WorkloadHealthGateVerdict.Failed;

        await h.WalkAsync();
        await h.WalkAsync();

        using var scope = new AssertionScope();
        var restore = h.AppliesOf(h.PreviousHash, forced: true).Should().ContainSingle(
            "the gate failed, so the walk turned and restored the recorded previous spec").Subject;
        restore.Env.Single(e => e.Name == TokenEnv).CredentialId.Should().Be(CredentialId,
            "the restore takes the id the rebuilt previous spec gives the same name");
        restore.ProvisionedCredentials.Should().ContainSingle(c => c.CredentialId == CredentialId);
        h.Refusals.Should().BeEmpty();
        h.Provider.TornDownReferences.Should().BeEmpty();
        h.Runtime.Should().Be(h.PreviousHash, "the previous spec runs again");
        h.Intent.Outcome.Should().Be(WorkloadUpdateOutcome.RolledBack);
    }

    [Fact]
    public async Task A_drift_replay_of_a_stored_target_of_another_version_takes_no_credential_and_is_refused()
    {
        var h = await Rig.CreateAsync();
        h.Gate.ForwardVerdict = WorkloadHealthGateVerdict.Pending;

        await h.WalkAsync();
        await h.DriftAsync();

        using var scope = new AssertionScope();
        h.AppliesOf(h.TargetHash, forced: true).Should().ContainSingle("the walk's own apply is the rebuilt target")
            .Which.Env.Single(e => e.Name == TokenEnv).CredentialId.Should().Be(CredentialId);
        h.Refusals.Should().ContainSingle().Which.Should().Match<(string SpecHash, bool Forced, string Reason)>(
            r => r.SpecHash == h.TargetHash && !r.Forced && r.Reason.Contains(TokenEnv));
        h.Provider.TornDownReferences.Should().BeEmpty("a refused apply tears nothing down");
        h.Runtime.Should().Be(h.TargetHash, "the walk's own apply of the rebuilt target still runs");
        h.Instance.AppliedSpecHash.Should().Be(h.PreviousHash, "a refused apply records nothing");
    }

    [Fact]
    public async Task A_drift_replay_of_the_stored_previous_spec_applies_it_with_its_credential_id()
    {
        var h = await Rig.CreateAsync(password: true, submittedPasswordChange: true);
        h.Gate.ForwardVerdict = WorkloadHealthGateVerdict.Pending;
        await h.WalkAsync();
        h.EngineRecordedTheTarget();

        h.UnreachableOnce = h.PreviousHash;
        (await h.AbandonAsync()).Status.Should().Be(WorkloadUpdateAbandonStatus.Abandoned);
        await h.WalkAsync();
        h.Intent.Direction.Should().Be(WorkloadUpdateDirection.RollingBack);
        h.Intent.IsActive.Should().BeTrue("an unreachable restore is transient, so the unwind waits");
        await h.DriftAsync();

        using var scope = new AssertionScope();
        h.AppliesOf(h.PreviousHash, forced: false).Should().HaveCount(2, "the steady state's apply, then the replay")
            .And.Subject.Last().Env.Single(e => e.Name == TokenEnv).CredentialId.Should().Be(CredentialId);
        h.Refusals.Should().BeEmpty();
        h.Provider.TornDownReferences.Should().BeEmpty();
        h.Runtime.Should().Be(h.PreviousHash);
        h.AppliesOf(h.PreviousHash, forced: false).Last().SecretMaterial
            .Should().Contain(new KeyValuePair<string, string>(DbPasswordEnv, DbPassword),
                "the compensating replay uses the row value rather than the forward submission");
    }

    [Fact]
    public async Task An_abandon_requested_before_the_walk_turn_keeps_forward_replay_values()
    {
        var h = await Rig.CreateAsync(password: true, submittedPasswordChange: true);
        h.Gate.ForwardVerdict = WorkloadHealthGateVerdict.Pending;
        await h.WalkAsync();
        await h.AbandonAsync();
        h.EngineRecordedThePreviousSpec();
        await h.DriftAsync();

        h.AppliesOf(h.TargetHash, forced: false).Last().SecretMaterial
            .Should().Contain(new KeyValuePair<string, string>(DbPasswordEnv, UpdatedDbPassword),
                "the journal still moves forward until the walk takes the requested abandon");
    }

    [Fact]
    public async Task A_masked_submitted_password_replays_the_stored_row_value_through_the_engine()
    {
        var h = await Rig.CreateAsync(password: true, submittedPasswordChange: true, submittedPassword: "***");
        h.Gate.ForwardVerdict = WorkloadHealthGateVerdict.Pending;
        await h.WalkAsync();
        await h.DriftAsync();

        h.AppliesOf(h.TargetHash, forced: false).Should().ContainSingle()
            .Which.SecretMaterial.Should().Contain(new KeyValuePair<string, string>(DbPasswordEnv, DbPassword));
    }

    [Fact]
    public async Task An_unresolvable_merged_set_logs_and_falls_back_without_changing_instance_state()
    {
        var h = await Rig.CreateAsync(declared: false, invalidHostPath: true);
        h.Gate.ForwardVerdict = WorkloadHealthGateVerdict.Pending;
        await h.WalkAsync();
        var stateBefore = h.Instance.State;
        await h.DriftAsync();

        h.LastDriftOutcome!.Reason.Should().NotBe(WorkloadReconcileReason.ConfigurationRequired);
        h.LastDriftOutcome.ResultingState.Should().Be(stateBefore);
        h.EngineLogger.ReceivedCalls().Should().Contain(call => call.GetMethodInfo().Name == "Log"
            && Equals(call.GetArguments()[0], LogLevel.Warning));
        h.AppliesOf(h.TargetHash, forced: false).Last().SecretMaterial.Should().BeEmpty();
    }

    [Fact]
    public async Task An_abandon_after_a_recorded_target_rolls_back_and_reconcile_stays_on_the_previous_spec()
    {
        var h = await Rig.CreateAsync();
        h.Gate.ForwardVerdict = WorkloadHealthGateVerdict.Pending;
        await h.WalkAsync();
        h.EngineRecordedTheTarget();

        var abandon = await h.AbandonAsync();
        await h.WalkAsync();
        await h.DriftAsync();

        using var scope = new AssertionScope();
        abandon.Status.Should().Be(WorkloadUpdateAbandonStatus.Abandoned);
        h.Intent.Outcome.Should().Be(WorkloadUpdateOutcome.RolledBack);
        h.Refusals.Should().BeEmpty();
        h.Provider.TornDownReferences.Should().BeEmpty();
        h.Runtime.Should().Be(h.PreviousHash);
        h.Instance.AppliedSpecHash.Should().Be(h.PreviousHash,
            "once the update ended, reconcile composed the installed version and recorded it");
    }

    [Fact]
    public async Task An_abandon_without_a_recorded_target_rolls_back_and_the_rows_match_what_runs()
    {
        var h = await Rig.CreateAsync();
        h.Gate.ForwardVerdict = WorkloadHealthGateVerdict.Pending;
        await h.WalkAsync();

        var abandon = await h.AbandonAsync();
        await h.WalkAsync();
        await h.DriftAsync();

        using var scope = new AssertionScope();
        abandon.Status.Should().Be(WorkloadUpdateAbandonStatus.Abandoned);
        h.Intent.Outcome.Should().Be(WorkloadUpdateOutcome.RolledBack);
        h.Refusals.Should().BeEmpty();
        h.Provider.TornDownReferences.Should().BeEmpty();
        h.Runtime.Should().Be(h.PreviousHash, "the restore applied, so the candidate no longer runs");
        h.Instance.AppliedSpecHash.Should().Be(h.PreviousHash);
    }

    [Fact]
    public async Task A_rollback_restores_an_undeclared_credential_the_stored_spec_does_not_provision()
    {
        var h = await Rig.CreateAsync(declared: false);
        h.Gate.ForwardVerdict = WorkloadHealthGateVerdict.Failed;

        await h.WalkAsync();
        await h.WalkAsync();

        using var scope = new AssertionScope();
        h.AppliesOf(h.PreviousHash, forced: true).Should().ContainSingle().Which.ProvisionedCredentials
            .Should().ContainSingle(c => c.CredentialId == CredentialId && c.Name == TokenEnv);
        h.Intent.Outcome.Should().Be(WorkloadUpdateOutcome.RolledBack);
        h.Runtime.Should().Be(h.PreviousHash);
    }

    [Fact]
    public async Task A_drift_replay_of_a_stored_target_of_another_version_provisions_nothing()
    {
        var h = await Rig.CreateAsync(declared: false);
        h.Gate.ForwardVerdict = WorkloadHealthGateVerdict.Pending;

        await h.WalkAsync();
        await h.DriftAsync();

        using var scope = new AssertionScope();
        h.Refusals.Should().BeEmpty();
        h.AppliesOf(h.TargetHash, forced: false).Should().ContainSingle("drift replays the stored target")
            .Which.ProvisionedCredentials.Should().BeEmpty("the stored target declares no credential of its own");
        h.Instance.AppliedSpecHash.Should().Be(h.TargetHash, "the replay applied, so reconcile recorded it");
    }

    [Fact]
    public async Task A_drift_replay_of_a_stored_target_of_another_version_keeps_a_password_s_material()
    {
        var h = await Rig.CreateAsync(declared: false, password: true);
        h.Gate.ForwardVerdict = WorkloadHealthGateVerdict.Pending;

        await h.WalkAsync();
        await h.DriftAsync();

        using var scope = new AssertionScope();
        h.Refusals.Should().BeEmpty("both versions render DB_PASSWORD as a password parameter");
        h.AppliesOf(h.TargetHash, forced: false).Should().ContainSingle("drift replays the stored target")
            .Which.SecretMaterial.Should().Equal(
                new Dictionary<string, string>(StringComparer.Ordinal) { [DbPasswordEnv] = DbPassword });
        h.Instance.AppliedSpecHash.Should().Be(h.TargetHash, "the replay applied, so reconcile recorded it");
    }

    [Fact]
    public async Task A_password_commit_renders_submitted_values_in_a_target_replay()
    {
        var h = await Rig.CreateAsync(password: true, submittedPasswordChange: true);
        h.Gate.ForwardVerdict = WorkloadHealthGateVerdict.Passed;
        var replayedInCommitWindow = false;
        IReadOnlyList<WorkloadDesiredSpec> commitWindowApplies = [];
        string? commitWindowRuntime = null;
        h.HostStage.AsyncOnCall = async () =>
        {
            if (!replayedInCommitWindow)
            {
                replayedInCommitWindow = true;
                var before = h.Provider.AppliedSpecs.Count;
                await h.ReconcileDuringCommitWindowAsync();
                commitWindowApplies = h.Provider.AppliedSpecs.Skip(before).ToArray();
                commitWindowRuntime = h.Runtime;
            }
        };
        h.Effects.OnCommitParams = plan => h.CommitPassword(
            plan.CandidateResolvedParameters[WorkloadId][DbPasswordKey]!);

        for (var tick = 0; tick < 12 && h.Intent.IsActive; tick++)
        {
            await h.WalkAsync();
        }

        h.Intent.Outcome.Should().Be(WorkloadUpdateOutcome.Completed);
        replayedInCommitWindow.Should().BeTrue();
        h.Refusals.Should().BeEmpty("the replayed provider apply must succeed");
        commitWindowRuntime.Should().Be(h.TargetHash, "the provider accepted the target during the parameter-commit window");
        commitWindowApplies.Should().ContainSingle("reconcile replays the active target between the version and parameter commits")
            .Which.SecretMaterial.Should().Contain(new KeyValuePair<string, string>(DbPasswordEnv, UpdatedDbPassword));

    }

    [Fact]
    public async Task A_replay_before_commit_version_uses_submitted_parameters()
    {
        var h = await Rig.CreateAsync(password: true, submittedPasswordChange: true);
        h.Gate.ForwardVerdict = WorkloadHealthGateVerdict.Pending;

        await h.WalkAsync();
        await h.DriftAsync();

        h.AppliesOf(h.TargetHash, forced: false).Should().ContainSingle(
                "the engine renders submitted password material even though this cross-version credential is refused")
            .Which.SecretMaterial.Should().Contain(new KeyValuePair<string, string>(DbPasswordEnv, UpdatedDbPassword));
    }

    [Fact]
    public async Task A_replay_before_sidecar_apply_keeps_submitted_password_after_same_hash_apply()
    {
        var h = await Rig.CreateAsync(password: true, submittedPasswordChange: true, sameHashIsConverged: true);
        h.Gate.ForwardVerdict = WorkloadHealthGateVerdict.Passed;

        h.EngineRecordedThePreviousSpec();
        await h.DriftAsync();
        h.AppliesOf(h.TargetHash, forced: false).Should().ContainSingle(
                "the engine must replay the staged target before the sidecar stage runs")
            .Which.SecretMaterial.Should().Contain(new KeyValuePair<string, string>(DbPasswordEnv, UpdatedDbPassword));

        for (var tick = 0; tick < 12 && h.Intent.IsActive; tick++)
        {
            await h.WalkAsync();
        }

        h.Intent.Outcome.Should().Be(WorkloadUpdateOutcome.Completed);
        h.Runtime.Should().Be(h.TargetHash);
        h.RuntimePassword.Should().Be(UpdatedDbPassword,
            "a same-hash sidecar apply leaves the earlier replay's rendered password running");
    }

    [Fact]
    public async Task A_required_parameter_supplied_by_update_does_not_hold_commit_window_replay()
    {
        var h = await Rig.CreateAsync(password: true, submittedPasswordChange: true);
        h.ClearPassword();
        h.Gate.ForwardVerdict = WorkloadHealthGateVerdict.Passed;
        WorkloadReconcileOutcome? replayOutcome = null;
        h.HostStage.AsyncOnCall = async () => replayOutcome = await h.ReconcileDuringCommitWindowAsync();
        h.Effects.OnCommitParams = plan => h.CommitPassword(
            plan.CandidateResolvedParameters[WorkloadId][DbPasswordKey]!);

        for (var tick = 0; tick < 12 && h.Intent.IsActive; tick++)
        {
            await h.WalkAsync();
        }

        h.Intent.Outcome.Should().Be(WorkloadUpdateOutcome.Completed);
        replayOutcome.Should().NotBeNull();
        replayOutcome!.Action.Should().Be(WorkloadReconcileAction.Applied);
        h.Provider.AppliedSpecs.Last().SecretMaterial.Should()
            .Contain(new KeyValuePair<string, string>(DbPasswordEnv, UpdatedDbPassword));
        h.RuntimePassword.Should().Be(UpdatedDbPassword);
    }

    [Fact]
    public async Task A_cross_version_blank_target_value_does_not_gate_the_installed_required_parameter()
    {
        var h = await Rig.CreateAsync(password: true, submittedPasswordChange: true, submittedPassword: "");
        h.Gate.ForwardVerdict = WorkloadHealthGateVerdict.Pending;
        await h.WalkAsync();
        await h.DriftAsync();

        h.LastDriftOutcome!.Reason.Should().NotBe(WorkloadReconcileReason.ConfigurationRequired,
            "the installed manifest is still satisfied by the row while the target version is not committed");
    }

    [Fact]
    public async Task The_other_version_s_password_does_not_fill_a_name_the_stored_target_backs_with_a_credential()
    {
        var h = await Rig.CreateAsync(declared: false, password: true, storeTarget: WithDbPasswordCredential);
        h.Gate.ForwardVerdict = WorkloadHealthGateVerdict.Pending;

        await h.WalkAsync();
        await h.DriftAsync();

        using var scope = new AssertionScope();
        h.Refusals.Should().BeEmpty();
        var replay = h.AppliesOf(h.TargetHash, forced: false).Should()
            .ContainSingle("drift replays the stored target").Which;
        replay.SecretMaterial.Should().BeEmpty("the name holds the candidate's own credential");
        replay.Env.Single(e => e.Name == DbPasswordEnv).CredentialId.Should().Be(DbCredentialId);
        h.Runtime.Should().Be(h.TargetHash);
    }

    [Fact]
    public async Task A_drift_replay_of_a_secret_reference_the_render_does_not_own_is_still_refused_by_name()
    {
        var h = await Rig.CreateAsync(storeTarget: WithUnownedSecretReference);
        h.Gate.ForwardVerdict = WorkloadHealthGateVerdict.Pending;

        await h.WalkAsync();
        await h.DriftAsync();

        using var scope = new AssertionScope();
        h.Refusals.Should().ContainSingle().Which.Should().Match<(string SpecHash, bool Forced, string Reason)>(
            r => r.SpecHash == h.TargetHash && !r.Forced && r.Reason.Contains(UnownedEnv));
        h.Provider.TornDownReferences.Should().BeEmpty("a refused apply tears nothing down");
        h.Runtime.Should().Be(h.TargetHash, "the walk's own apply of the rebuilt target still runs");
        h.Instance.AppliedSpecHash.Should().Be(h.PreviousHash, "a refused apply records nothing");
    }

    [Fact]
    public async Task A_restore_of_a_secret_reference_the_render_does_not_own_is_still_refused_by_name()
    {
        var h = await Rig.CreateAsync(storePrevious: WithUnownedSecretReference);
        h.Gate.ForwardVerdict = WorkloadHealthGateVerdict.Failed;

        await h.WalkAsync();
        await h.WalkAsync();

        using var scope = new AssertionScope();
        h.Refusals.Should().Contain(r => r.SpecHash == h.PreviousHash && r.Forced
            && r.Reason.Contains(UnownedEnv) && !r.Reason.Contains(TokenEnv));
        h.Provider.TornDownReferences.Should().BeEmpty("a refused apply tears nothing down");
        h.Runtime.Should().Be(h.TargetHash);
        h.Intent.Outcome.Should().Be(WorkloadUpdateOutcome.RollbackFailed,
            "a refusal is not transient, so the unwind fails where it stands");
        h.Intent.Error.Should().Contain(UnownedEnv);
    }

    private static WorkloadDesiredSpec WithDbPasswordCredential(WorkloadDesiredSpec spec) => spec with
    {
        Env = [.. spec.Env.Select(e => e.Name == DbPasswordEnv ? e with { CredentialId = DbCredentialId } : e)],
        ProvisionedCredentials = [new ProvisionedCredential(DbCredentialId, DbPasswordEnv)],
    };

    private static WorkloadDesiredSpec WithUnownedSecretReference(WorkloadDesiredSpec spec) => spec with
    {
        Env = [.. spec.Env, new ResolvedEnvVar { Name = UnownedEnv, Kind = ResolvedEnvKind.SecretRef, SecretRef = "reeve-wl-secret" }],
    };

    private sealed class Rig
    {
        private static readonly Reeve.Workloads.Core.WorkloadParameterSecretCodec Secrets =
            new(new Microsoft.AspNetCore.DataProtection.EphemeralDataProtectionProvider());

        private static IReadOnlyDictionary<string, string?> Stored(IReadOnlyDictionary<string, string?> clear)
            => Secrets.ProtectNamedParameters(clear, [DbPasswordKey]);

        private readonly WorkloadReconcileEngine _engine;
        private readonly WorkloadUpdateOrchestrator _orchestrator;
        private readonly WorkloadUpdateAbandonService _abandon;
        private readonly InMemoryIntentRepository _intents;
        private readonly Dictionary<string, string?> _resolvedParameters;
        private readonly RecordingEffects _effects;
        private readonly ScriptedHostStage _hostStage;

        private Rig(
            MutableClock clock,
            RecordingProvider provider,
            ScriptedGate gate,
            WorkloadInstance instance,
            Dictionary<string, string?> resolvedParameters,
            RecordingEffects effects,
            ScriptedHostStage hostStage,
            CallLog log,
            InMemoryIntentRepository intents,
            WorkloadReconcileEngine engine,
            WorkloadUpdateOrchestrator orchestrator,
            WorkloadUpdateAbandonService abandon)
        {
            Clock = clock;
            Provider = provider;
            Gate = gate;
            Instance = instance;
            _resolvedParameters = resolvedParameters;
            _effects = effects;
            _hostStage = hostStage;
            Log = log;
            _intents = intents;
            _engine = engine;
            _orchestrator = orchestrator;
            _abandon = abandon;
        }

        public MutableClock Clock { get; }
        public RecordingProvider Provider { get; }
        public ScriptedGate Gate { get; }
        public WorkloadInstance Instance { get; }
        public RecordingEffects Effects => _effects;
        public ScriptedHostStage HostStage => _hostStage;
        public CallLog Log { get; }
        public WorkloadUpdateIntent Intent { get; private set; } = null!;
        public string PreviousHash { get; private set; } = null!;
        public string TargetHash { get; private set; } = null!;

        public string? Runtime { get; private set; }
        public string? RuntimePassword { get; private set; }

        public string? UnreachableOnce { get; set; }
        public bool SameHashIsConverged { get; private set; }
        public ILogger<WorkloadReconcileEngine> EngineLogger { get; set; } = null!;
        public WorkloadReconcileOutcome? LastDriftOutcome { get; private set; }

        public List<(string SpecHash, bool Forced, string Reason)> Refusals { get; } = [];

        public IReadOnlyList<WorkloadDesiredSpec> AppliesOf(string specHash, bool forced)
            => Provider.AppliedSpecs
                .Where((spec, i) => spec.SpecHash == specHash && Provider.ApplyForce[i].Force == forced)
                .ToList();

        public static async Task<Rig> CreateAsync(
            bool declared = true,
            bool password = false,
            bool submittedPasswordChange = false,
            bool sameHashIsConverged = false,
            string? submittedPassword = null,
            bool invalidHostPath = false,
            Func<WorkloadDesiredSpec, WorkloadDesiredSpec>? storeTarget = null,
            Func<WorkloadDesiredSpec, WorkloadDesiredSpec>? storePrevious = null)
        {
            var clock = new MutableClock(T0);
            var log = new CallLog();
            var provider = new RecordingProvider(KubernetesProvider.Id, log);
            var registry = new FakeProviderRegistry(provider);
            var intents = new InMemoryIntentRepository();
            var gate = new ScriptedGate();

            var instance = WorkloadInstance.Create(Guid.NewGuid(), Guid.NewGuid(), "zwave", WorkloadId, T0);
            instance.TransitionTo(WorkloadInstanceState.PendingConfiguration, WorkloadStateReason.ConfigRequired, null, T0);
            instance.TransitionTo(WorkloadInstanceState.Deploying, WorkloadStateReason.Provisioning, null, T0);

            var resolvedParameters = new Dictionary<string, string?>(StringComparer.Ordinal);
            if (password)
            {
                resolvedParameters[DbPasswordKey] = DbPassword;
            }
            if (invalidHostPath)
            {
                resolvedParameters["hostPath"] = "/run/previous";
            }
            instance.SetResolvedParameters(Stored(resolvedParameters), T0);
            WorkloadUpdatePlan? plan = null;
            var planner = new ScriptedPlanner(null!) { Override = () => plan! };
            var effects = new RecordingEffects(log);
            var hostStage = new ScriptedHostStage(log);
            var engineLogger = Substitute.For<ILogger<WorkloadReconcileEngine>>();
            var engine = Engine(clock, registry, new WorkloadActiveIntentProvider(intents), declared, password, resolvedParameters, effects, log, invalidHostPath, engineLogger);
            var repairs = new SpyRepairsFactory(new SpyIssueRegistry());
            var options = new WorkloadUpdateOrchestratorOptions();
            var pipeline = WorkloadUpdatePipeline.Resolve(
            [
                new ParamsStage(effects),
                new SidecarStage(registry, gate, clock, options,
                    NSubstitute.Substitute.For<Reeve.Workloads.Abstractions.IWorkloadInstanceRepository>(),
                    new Reeve.Workloads.Core.UnpinnedReferenceRolloutGuard(new Microsoft.AspNetCore.DataProtection.EphemeralDataProtectionProvider()), new Reeve.Workloads.Abstractions.NoCredentialPort()),
                new HealthGateStage(gate, clock, options),
                new OperatorRedeployStage(registry, new FakeObservedWorkloadVersionReader(), new Reeve.Workloads.Core.Health.WorkloadHealthContributorDispatch([]), repairs, clock,
                    NullLogger<OperatorRedeployStage>.Instance),
                new CommitVersionStage(effects, new RecordingRowEffects(log)),
                hostStage,
                new CommitParamsStage(effects, new RecordingRowEffects(log)),
            ]);
            var orchestrator = new WorkloadUpdateOrchestrator(
                intents, planner, pipeline, clock, NullLogger<WorkloadUpdateOrchestrator>.Instance,
                new Reeve.Workloads.Core.Tests.Hosting.FakeHostApplicationLifetime());
            var abandon = new WorkloadUpdateAbandonService(
                intents, new RecordingNudge(), new RecordingAuditWriter(), clock, pipeline,
                NullLogger<WorkloadUpdateAbandonService>.Instance,
                NSubstitute.Substitute.For<IWorkloadGroupRepository>(), NSubstitute.Substitute.For<IWorkloadInstanceRepository>());

            var rig = new Rig(clock, provider, gate, instance, resolvedParameters, effects, hostStage, log, intents, engine, orchestrator, abandon);
            rig.SameHashIsConverged = sameHashIsConverged;
            rig.EngineLogger = engineLogger;
            provider.ApplyBehaviour[WorkloadId] = rig.OnApply;

            await engine.ReconcileInstanceAsync(instance);
            var previous = provider.AppliedSpecs.Single();
            previous.Env.Single(e => e.Name == WorkloadDesiredSpecFactory.AppVersionEnv).Value
                .Should().Be(FromVersion, "the rig's premise: the render is the installed version's");
            previous.ProvisionedCredentials.Should().ContainSingle(c => c.CredentialId == CredentialId, "the rig's premise");
            if (declared)
            {
                previous.Env.Single(e => e.Name == TokenEnv).CredentialId.Should().Be(CredentialId, "the rig's premise");
            }
            else
            {
                previous.Env.Should().NotContain(e => e.Name == TokenEnv, "the rig's premise");
            }

            var target = AtVersion(
                previous with { SpecHash = "hash-n-plus-1", Image = previous.Image + "-n-plus-1" }, ToVersion);
            if (submittedPasswordChange)
            {
                target = target with
                {
                    SecretMaterial = new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        [DbPasswordEnv] = UpdatedDbPassword,
                    },
                };
            }
            rig.PreviousHash = previous.SpecHash;
            rig.TargetHash = target.SpecHash;

            var item = WorkloadUpdatePlanBuilder.Item(WorkloadId, instance.Id, target.SpecHash, previous.SpecHash)
                with { TargetSpec = target, PreviousSpec = previous };
            plan = WorkloadUpdatePlanBuilder.Plan(instance.GroupInstanceId, [item]) with
            {
                CandidateResolvedParameters = password
                    ? new Dictionary<string, IReadOnlyDictionary<string, string?>>(StringComparer.Ordinal)
                    {
                        [WorkloadId] = new Dictionary<string, string?>(StringComparer.Ordinal)
                        {
                            [DbPasswordKey] = submittedPasswordChange ? UpdatedDbPassword : DbPassword,
                        },
                    }
                    : new Dictionary<string, IReadOnlyDictionary<string, string?>>(StringComparer.Ordinal),
            };

            var intent = WorkloadUpdateIntent.Create(Guid.NewGuid(), instance.GroupInstanceId, FromVersion, ToVersion, T0);
            intent.RecordStageJournal(Reeve.Pipeline.PipelineJournal.New());
            intent.StageSnapshots(new WorkloadUpdateSnapshots
            {
                TargetSpecs = new Dictionary<string, WorkloadDesiredSpec>(StringComparer.Ordinal)
                {
                    [WorkloadId] = (storeTarget ?? (spec => spec))(AsStoredWithoutCredentials(target)),
                },
                PreviousSpecs = new Dictionary<string, WorkloadDesiredSpec>(StringComparer.Ordinal)
                {
                    [WorkloadId] = (storePrevious ?? (spec => spec))(AsStoredWithoutCredentials(previous)),
                },
                OperatorParameters = submittedPasswordChange || invalidHostPath
                    ? SubmittedValues(submittedPasswordChange, submittedPassword, invalidHostPath)
                    : null,
            }, T0);
            await intents.AddAsync(intent);
            rig.Intent = intent;
            return rig;
        }

        private static WorkloadDesiredSpec AtVersion(WorkloadDesiredSpec spec, string version) => spec with
        {
            Env = [
                .. spec.Env.Where(e => !string.Equals(e.Name, WorkloadDesiredSpecFactory.AppVersionEnv, StringComparison.Ordinal)),
                new ResolvedEnvVar { Name = WorkloadDesiredSpecFactory.AppVersionEnv, Kind = ResolvedEnvKind.Literal, Value = version },
            ],
        };

        private static WorkloadDesiredSpec AsStoredWithoutCredentials(WorkloadDesiredSpec spec) => spec with
        {
            Env = [.. spec.Env.Select(e => e with { CredentialId = null })],
            ProvisionedCredentials = [],
            SecretMaterial = new Dictionary<string, string>(StringComparer.Ordinal),
        };

        public async Task WalkAsync()
        {
            foreach (var open in await _intents.GetOpenIntentsAsync())
            {
                await _orchestrator.WalkAsync(open);
            }

            Clock.Advance(TimeSpan.FromSeconds(60));
        }

        public void EngineRecordedTheTarget() => Instance.SetAppliedSpecHash(TargetHash, Clock.UtcNow);

        public void EngineRecordedThePreviousSpec() => Instance.SetAppliedSpecHash(PreviousHash, Clock.UtcNow);

        public void CommitPassword(string password)
        {
            _resolvedParameters[DbPasswordKey] = password;
            Instance.SetResolvedParameters(Stored(_resolvedParameters), Clock.UtcNow);
        }

        public void ClearPassword()
        {
            _resolvedParameters.Remove(DbPasswordKey);
            Instance.SetResolvedParameters(Stored(_resolvedParameters), Clock.UtcNow);
        }

        public Task<WorkloadReconcileOutcome> ReconcileDuringCommitWindowAsync()
            => _engine.ReconcileInstanceAsync(Instance);

        public async Task DriftAsync()
        {
            LastDriftOutcome = await _engine.ReconcileInstanceAsync(Instance);
            Clock.Advance(TimeSpan.FromSeconds(60));
        }

        public async Task<WorkloadUpdateAbandonResult> AbandonAsync()
        {
            var result = await _abandon.AbandonAsync(Intent.Id, actorUserId: null);
            Intent = (await _intents.GetByIdAsync(Intent.Id))!;
            return result;
        }

        private WorkloadApplyResult OnApply(WorkloadDesiredSpec spec)
        {
            var forced = Provider.ApplyForce[^1].Force;
            if (UnreachableOnce == spec.SpecHash)
            {
                UnreachableOnce = null;
                return WorkloadApplyResult.Failed(WorkloadApplyReason.ProviderUnreachable, "daemon unreachable");
            }

            if (WorkloadSpecCredentials.Of(spec).Refusal is { } refusal)
            {
                Refusals.Add((spec.SpecHash, forced, refusal));
                return WorkloadApplyResult.Failed(WorkloadApplyReason.Rejected, refusal);
            }

            if (SameHashIsConverged && Runtime == spec.SpecHash)
            {
                return WorkloadApplyResult.Applied(spec.SpecHash);
            }

            Runtime = spec.SpecHash;
            RuntimePassword = spec.SecretMaterial.GetValueOrDefault(DbPasswordEnv);
            return WorkloadApplyResult.Applied(spec.SpecHash);
        }

        private static WorkloadReconcileEngine Engine(
            IWorkloadClock clock, IWorkloadProviderRegistry registry, IActiveWorkloadIntentProvider activeIntents,
            bool declared, bool password, Dictionary<string, string?> parameters, RecordingEffects effects, CallLog log,
            bool invalidHostPath, ILogger<WorkloadReconcileEngine> engineLogger)
        {
            var health = Substitute.For<IWorkloadHealthContributor>();
            health.ObserveAsync(Arg.Any<WorkloadHealthObservation>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(new WorkloadHealthContribution()));

            var resolver = Substitute.For<IDesiredSpecResolver>();
            resolver.ResolveAsync(
                    Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<Func<WorkloadDesiredSpec>>(), Arg.Any<CancellationToken>())
                .Returns(call =>
                {
                    var spec = call.Arg<Func<WorkloadDesiredSpec>>()();
                    log.Add($"fresh-render:{spec.SpecHash}");
                    return Task.FromResult(spec);
                });

            var images = new ProductImageOptions();
            images.Components["controller"] = new ProductImageEntry
            {
                Ref = "example.test/controller",
                Digest = "sha256:" + new string('1', 64),
            };

            var env = new List<WorkloadEnv>();
            if (declared)
            {
                env.Add(new WorkloadEnv { Name = TokenEnv, From = "bootstrap:" + TokenKey });
            }

            if (password)
            {
                env.Add(new WorkloadEnv { Name = DbPasswordEnv, From = "parameter:" + DbPasswordKey });
            }

            var inputs = Substitute.For<IWorkloadReconcileInputsProvider>();
            inputs.GetInputsAsync(Arg.Any<WorkloadInstance>(), Arg.Any<CancellationToken>())
                .Returns(_ => ReconcileInputsAnswers.Of(new WorkloadReconcileInputs
                {
                    Namer = DefaultWorkloadResourceNamer.Instance,
                    WorkloadManifest = new WorkloadManifest
                    {
                        Id = WorkloadId,
                        DisplayName = WorkloadId,
                        Image = new WorkloadImage { Component = "controller" },
                        Kind = WorkloadKind.Service,
                        Health = new WorkloadHealth { Mode = WorkloadHealthMode.Container },
                        Parameters = [
                            .. (password
                                ? new[] { new WorkloadParameter { Name = DbPasswordKey, Type = WorkloadParameterType.Password, Required = true } }
                                : Array.Empty<WorkloadParameter>()),
                            .. (invalidHostPath
                                ? new[] { new WorkloadParameter { Name = "hostPath", Type = WorkloadParameterType.HostPath } }
                                : Array.Empty<WorkloadParameter>()),
                        ],
                        Privileges = invalidHostPath
                            ? new WorkloadPrivileges { HostPaths = [new WorkloadHostPath { Parameter = "hostPath", MountPath = "/host/data" }] }
                            : new WorkloadPrivileges(),
                        Env = env,
                    },
                    ResolvedParameters = Stored(parameters),
                    Settings = new WorkloadOrchestratorSettings { Provider = KubernetesProvider.Id },
                    Bootstrap = new WorkloadBootstrapContext
                    {
                        InstanceId = Guid.NewGuid(),
                        SecretName = "reeve-wl-secret",
                        AppVersion = effects.InstalledVersion ?? FromVersion,
                        Values = new Dictionary<string, BootstrapValue>(StringComparer.Ordinal)
                        {
                            [TokenKey] = BootstrapValue.HostCredential(CredentialId),
                        },
                        ProvisionedCredentials = [new ProvisionedCredential(CredentialId, TokenEnv)],
                    },
                    EffectiveProvider = KubernetesProvider.Descriptor,
                    ProductImages = images,
                }));

            return new WorkloadReconcileEngine(
                inputs,
                resolver,
                registry,
                Substitute.For<IWorkloadInstanceRepository>(),
                new Reeve.Workloads.Core.Health.WorkloadHealthContributorDispatch([health]),
                clock,
                engineLogger,
                Substitute.For<IRetainedVolumeService>(),
                activeIntents, new Reeve.Workloads.Core.NoWorkloadParameterReferenceResolver(), Secrets, null,
                new Reeve.Workloads.Core.UnpinnedReferenceRolloutGuard(new Microsoft.AspNetCore.DataProtection.EphemeralDataProtectionProvider()), new Reeve.Workloads.Abstractions.NoCredentialPort());
        }

        private static Dictionary<string, string?> SubmittedValues(bool password, string? submittedPassword, bool invalidPath)
        {
            var result = new Dictionary<string, string?>(StringComparer.Ordinal);
            if (password)
            {
                result[DbPasswordKey] = submittedPassword ?? UpdatedDbPassword;
            }
            if (invalidPath)
            {
                result["hostPath"] = "/etc";
            }
            return result;
        }
    }

    private sealed class ScriptedGate : IWorkloadUpdateHealthGate
    {
        public WorkloadHealthGateVerdict ForwardVerdict { get; set; } = WorkloadHealthGateVerdict.Pending;

        public Task<WorkloadHealthGateTickResult> EvaluateTickAsync(
            WorkloadHealthGateTickRequest request, CancellationToken cancellationToken = default)
        {
            var verdict = request.ExpectedSpecHash == "hash-n-plus-1" ? ForwardVerdict : WorkloadHealthGateVerdict.Passed;
            return Task.FromResult(new WorkloadHealthGateTickResult
            {
                Verdict = verdict,
                FailureReason = verdict == WorkloadHealthGateVerdict.Failed ? "CrashLoop" : null,
                NextClockState = new WorkloadGateClockState(),
            });
        }
    }
}
