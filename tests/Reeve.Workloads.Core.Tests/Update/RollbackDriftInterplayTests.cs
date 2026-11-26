using FluentAssertions;
using FluentAssertions.Execution;
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
using Reeve.Workloads.Providers.Kubernetes;
using Reeve.Workloads.Tests.Shared;

namespace Reeve.Workloads.Core.Tests.Update;

public sealed class RollbackDriftInterplayTests
{
    private const string WorkloadId = "sidecar";
    private const string TargetHash = "hash-n-plus-1";
    private static readonly DateTimeOffset T0 = new(2026, 9, 23, 12, 0, 0, TimeSpan.Zero);

    private static readonly TimeSpan PassInterval = TimeSpan.FromSeconds(60);

    [Fact]
    public async Task A_health_gate_rollback_is_not_undone_by_drift_once_the_engine_recorded_the_target()
    {
        var h = await Rig.CreateAsync();
        h.Gate.ForwardVerdict = WorkloadHealthGateVerdict.Failed;

        await h.RunUntilSettledAsync();

        h.DriftAppliesOfTargetWhileRollingBack.Should().Equal(
            Array.Empty<int>(),
            "the engine recorded the target as applied on the forward leg, so drift sees no drift while RollingBack");
        h.Intent.Phase.Should().Be(WorkloadUpdatePhase.RolledBack);
        h.Runtime.Should().Be(h.PreviousHash);
    }

    [Fact]
    public async Task A_target_rejected_after_its_workload_object_was_written_does_not_undo_the_rollback()
    {
        var h = await Rig.CreateAsync();
        h.TargetRejectedAfterMaterializing = true;

        await h.RunUntilSettledAsync();

        using var scope = new AssertionScope();
        h.DriftAppliesOfTargetWhileRollingBack.Should().Equal(
            Array.Empty<int>(),
            "while an intent is RollingBack the desired spec must not be the target the rollback is undoing");
        h.Intent.Phase.Should().Be(WorkloadUpdatePhase.RolledBack,
            "the previous spec was restorable and healthy; only the drift pass kept replacing it");
    }

    [Fact]
    public async Task A_cleared_applied_hash_during_a_rollback_does_not_re_apply_the_target()
    {
        var h = await Rig.CreateAsync();
        h.Gate.ForwardVerdict = WorkloadHealthGateVerdict.Failed;
        h.OnEnteringRollingBack = instance => instance.SetAppliedSpecHash(null, h.Clock.UtcNow);

        await h.RunUntilSettledAsync();

        using var scope = new AssertionScope();
        h.DriftAppliesOfTargetWhileRollingBack.Should().Equal(
            Array.Empty<int>(),
            "while an intent is RollingBack the desired spec must not be the target the rollback is undoing");
        h.Intent.Phase.Should().Be(WorkloadUpdatePhase.RolledBack);
        h.Runtime.Should().Be(h.PreviousHash);
    }

    private sealed class Rig
    {
        private readonly WorkloadReconcileEngine _engine;
        private readonly WorkloadUpdateOrchestrator _orchestrator;
        private readonly InMemoryIntentRepository _intents;
        private bool _rollingBackSeen;

        private Rig(
            MutableClock clock,
            RecordingProvider provider,
            RuntimeGate gate,
            WorkloadInstance instance,
            InMemoryIntentRepository intents,
            WorkloadReconcileEngine engine,
            WorkloadUpdateOrchestrator orchestrator)
        {
            Clock = clock;
            Provider = provider;
            Gate = gate;
            Instance = instance;
            _intents = intents;
            _engine = engine;
            _orchestrator = orchestrator;
        }

        public MutableClock Clock { get; }
        public RecordingProvider Provider { get; }
        public RuntimeGate Gate { get; }
        public WorkloadInstance Instance { get; }
        public WorkloadUpdateIntent Intent { get; private set; } = null!;
        public string PreviousHash { get; private set; } = null!;

        public string? Runtime { get; private set; }

        public bool TargetRejectedAfterMaterializing { get; set; }
        public Action<WorkloadInstance>? OnEnteringRollingBack { get; set; }

        public List<int> DriftAppliesOfTargetWhileRollingBack { get; } = [];

        private int _pass;

        public static async Task<Rig> CreateAsync()
        {
            var clock = new MutableClock(T0);
            var log = new CallLog();
            var provider = new RecordingProvider(KubernetesProvider.Id, log);
            var registry = new FakeProviderRegistry(provider);
            var intents = new InMemoryIntentRepository();
            var gate = new RuntimeGate();

            var instance = WorkloadInstance.Create(Guid.NewGuid(), Guid.NewGuid(), "zwave", WorkloadId, T0);
            instance.TransitionTo(WorkloadInstanceState.PendingConfiguration, WorkloadStateReason.ConfigRequired, null, T0);
            instance.TransitionTo(WorkloadInstanceState.Deploying, WorkloadStateReason.Provisioning, null, T0);

            var engine = Engine(clock, registry, new WorkloadActiveIntentProvider(intents));

            WorkloadUpdatePlan? plan = null;
            var planner = new ScriptedPlanner(null!) { Override = () => plan! };
            var effects = new RecordingEffects(log);
            var repairs = new SpyRepairsFactory(new SpyIssueRegistry());
            var options = new WorkloadUpdateOrchestratorOptions();
            var orchestrator = new WorkloadUpdateOrchestrator(
                intents,
                planner,
                WorkloadUpdatePipeline.Resolve(
                [
                    new ParamsStage(effects),
                    new SidecarStage(registry, gate, clock, options,
                        NSubstitute.Substitute.For<Reeve.Workloads.Abstractions.IWorkloadInstanceRepository>(),
                        new Reeve.Workloads.Core.UnpinnedReferenceRolloutGuard(new Microsoft.AspNetCore.DataProtection.EphemeralDataProtectionProvider()), new Reeve.Workloads.Abstractions.NoCredentialPort()),
                    new HealthGateStage(gate, clock, options),
                    new OperatorRedeployStage(registry, new FakeObservedWorkloadVersionReader(), new Reeve.Workloads.Core.Health.WorkloadHealthContributorDispatch([]), repairs, clock,
                        NullLogger<OperatorRedeployStage>.Instance),
                    new CommitVersionStage(effects, new RecordingRowEffects(log)),
                    new ScriptedHostStage(log),
                    new CommitParamsStage(effects, new RecordingRowEffects(log)),
                ]),
                clock,
                NullLogger<WorkloadUpdateOrchestrator>.Instance,
                new Reeve.Workloads.Core.Tests.Hosting.FakeHostApplicationLifetime());

            var rig = new Rig(clock, provider, gate, instance, intents, engine, orchestrator);
            gate.RuntimeAtPassStart = () => rig._runtimeAtPassStart;

            WorkloadDesiredSpec? rendered = null;
            provider.ApplyBehaviour[WorkloadId] = spec =>
            {
                rendered ??= spec;
                return rig.OnApply(spec);
            };

            await engine.ReconcileInstanceAsync(instance);
            var previous = rendered!;
            var target = previous with { SpecHash = TargetHash, Image = previous.Image + "-n-plus-1" };
            rig.PreviousHash = previous.SpecHash;

            var item = WorkloadUpdatePlanBuilder.Item(WorkloadId, instance.Id, TargetHash, previous.SpecHash)
                with { TargetSpec = target, PreviousSpec = previous };
            plan = WorkloadUpdatePlanBuilder.Plan(instance.GroupInstanceId, [item]);

            var intent = WorkloadUpdateIntent.Create(Guid.NewGuid(), instance.GroupInstanceId, "2.5.0", "2.6.0", T0);
            intent.RecordStageJournal(Reeve.Pipeline.PipelineJournal.New());
            intent.StageSnapshots(new WorkloadUpdateSnapshots
            {
                TargetSpecs = new Dictionary<string, WorkloadDesiredSpec>(StringComparer.Ordinal) { [WorkloadId] = target },
                PreviousSpecs = new Dictionary<string, WorkloadDesiredSpec>(StringComparer.Ordinal) { [WorkloadId] = previous },
            }, T0);
            await intents.AddAsync(intent);
            rig.Intent = intent;
            return rig;
        }

        private string? _runtimeAtPassStart;

        private WorkloadApplyResult OnApply(WorkloadDesiredSpec spec)
        {
            var force = Provider.ApplyForce[^1].Force;
            if (!force && spec.SpecHash == TargetHash && Intent?.Phase == WorkloadUpdatePhase.RollingBack)
            {
                DriftAppliesOfTargetWhileRollingBack.Add(_pass);
            }

            Runtime = spec.SpecHash;
            if (TargetRejectedAfterMaterializing && spec.SpecHash == TargetHash)
            {
                return WorkloadApplyResult.Failed(WorkloadApplyReason.Rejected, "Service \"sidecar\" is invalid");
            }

            return WorkloadApplyResult.Applied(spec.SpecHash);
        }

        private async Task PassAsync()
        {
            _pass++;
            _runtimeAtPassStart = Runtime;

            foreach (var open in await _intents.GetOpenIntentsAsync())
            {
                await _orchestrator.WalkAsync(open);
            }

            if (!_rollingBackSeen && Intent.Phase == WorkloadUpdatePhase.RollingBack)
            {
                _rollingBackSeen = true;
                OnEnteringRollingBack?.Invoke(Instance);
            }

            await _engine.ReconcileInstanceAsync(Instance);
            Clock.Advance(PassInterval);
        }

        public async Task RunUntilSettledAsync()
        {
            for (var i = 0; i < 20 && Intent.IsActive; i++)
            {
                await PassAsync();
            }

            Intent.IsActive.Should().BeFalse("the intent must settle within the bounded passes");
        }

        private static WorkloadReconcileEngine Engine(
            IWorkloadClock clock, IWorkloadProviderRegistry registry, IActiveWorkloadIntentProvider activeIntents)
        {
            var health = Substitute.For<IWorkloadHealthContributor>();
            health.ObserveAsync(Arg.Any<WorkloadHealthObservation>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(new WorkloadHealthContribution()));

            var resolver = Substitute.For<IDesiredSpecResolver>();
            resolver.ResolveAsync(
                    Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<Func<WorkloadDesiredSpec>>(), Arg.Any<CancellationToken>())
                .Returns(call => Task.FromResult(call.Arg<Func<WorkloadDesiredSpec>>()()));

            var images = new ProductImageOptions();
            images.Components["controller"] = new ProductImageEntry
            {
                Ref = "example.test/controller",
                Digest = "sha256:" + new string('1', 64),
            };

            var inputs = Substitute.For<IWorkloadReconcileInputsProvider>();
            inputs.GetInputsAsync(Arg.Any<WorkloadInstance>(), Arg.Any<CancellationToken>())
                .Returns(ReconcileInputsAnswers.Of(new WorkloadReconcileInputs
                {
                    Namer = DefaultWorkloadResourceNamer.Instance,
                    WorkloadManifest = new WorkloadManifest
                    {
                        Id = WorkloadId,
                        DisplayName = WorkloadId,
                        Image = new WorkloadImage { Component = "controller" },
                        Kind = WorkloadKind.Service,
                        Health = new WorkloadHealth { Mode = WorkloadHealthMode.Container },
                    },
                    ResolvedParameters = new Dictionary<string, string?>(StringComparer.Ordinal),
                    Settings = new WorkloadOrchestratorSettings { Provider = KubernetesProvider.Id },
                    Bootstrap = new WorkloadBootstrapContext { InstanceId = Guid.NewGuid(), SecretName = "reeve-wl-secret" },
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
                NullLogger<WorkloadReconcileEngine>.Instance,
                Substitute.For<IRetainedVolumeService>(),
                activeIntents, new Reeve.Workloads.Core.NoWorkloadParameterReferenceResolver(), new Reeve.Workloads.Core.WorkloadParameterSecretCodec(new Microsoft.AspNetCore.DataProtection.EphemeralDataProtectionProvider()), null,
                new Reeve.Workloads.Core.UnpinnedReferenceRolloutGuard(new Microsoft.AspNetCore.DataProtection.EphemeralDataProtectionProvider()), new Reeve.Workloads.Abstractions.NoCredentialPort());
        }
    }

    private sealed class RuntimeGate : IWorkloadUpdateHealthGate
    {
        public WorkloadHealthGateVerdict ForwardVerdict { get; set; } = WorkloadHealthGateVerdict.Passed;
        public Func<string?> RuntimeAtPassStart { get; set; } = () => null;

        public Task<WorkloadHealthGateTickResult> EvaluateTickAsync(
            WorkloadHealthGateTickRequest request, CancellationToken cancellationToken = default)
        {
            var verdict = request.ExpectedSpecHash == TargetHash
                ? ForwardVerdict
                : RuntimeAtPassStart() == request.ExpectedSpecHash
                    ? WorkloadHealthGateVerdict.Passed
                    : WorkloadHealthGateVerdict.Pending;

            return Task.FromResult(new WorkloadHealthGateTickResult
            {
                Verdict = verdict,
                FailureReason = verdict == WorkloadHealthGateVerdict.Failed ? "CrashLoop" : null,
                NextClockState = new WorkloadGateClockState(),
            });
        }
    }
}
