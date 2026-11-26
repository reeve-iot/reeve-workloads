using FluentAssertions;
using Reeve.Workloads.Abstractions.Contracts;
using Reeve.Workloads.Abstractions.Contracts.Deployment;
using Reeve.Workloads.Abstractions.Domain;
using Reeve.Workloads.Abstractions.Domain.DesiredSpec;
using Reeve.Workloads.Abstractions.Domain.Entities;
using Reeve.Workloads.Abstractions.Providers;
using Reeve.Workloads.Abstractions.Update;
using Reeve.Workloads.Tests.Shared;

namespace Reeve.Workloads.Abstractions.Tests;

public sealed class WorkloadUpdateIntentTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 6, 12, 0, 0, TimeSpan.Zero);

    private static WorkloadUpdateIntent NewIntent()
        => WorkloadUpdateIntent.Create(Guid.NewGuid(), Guid.NewGuid(), fromVersion: "2.5.0", toVersion: "2.6.0", Now);

    [Fact]
    public void Cohort_factory_records_validated_position_without_changing_the_single_factory()
    {
        var cohortId = Guid.NewGuid();
        var member = WorkloadUpdateIntent.Create(Guid.NewGuid(), Guid.NewGuid(), "2.5.0", "2.6.0", Now,
            new WorkloadUpdateCohortPlace(cohortId, 1, 2));
        var single = NewIntent();

        member.CohortId.Should().Be(cohortId);
        member.CohortOrdinal.Should().Be(1);
        member.CohortSize.Should().Be(2);
        single.CohortId.Should().BeNull();
        single.CohortOrdinal.Should().BeNull();
        single.CohortSize.Should().BeNull();

        var invalid = () => WorkloadUpdateIntent.Create(Guid.NewGuid(), Guid.NewGuid(), "2.5.0", "2.6.0", Now,
            new WorkloadUpdateCohortPlace(cohortId, 2, 2));
        invalid.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void StageTargetSpecs_is_one_shot_while_staging_params()
    {
        var intent = NewIntent();
        var targets = new Dictionary<string, WorkloadDesiredSpec> { ["alpha"] = Spec("alpha") };

        intent.StageTargetSpecs(targets, Now);
        var afterFirstStage = intent.Version;
        intent.StageTargetSpecs(new Dictionary<string, WorkloadDesiredSpec>(), Now.AddMinutes(1));

        intent.Version.Should().Be(afterFirstStage);
        intent.ReadTargetSpecs().Value["alpha"].Should().BeEquivalentTo(targets["alpha"]);
    }

    private static WorkloadEvidenceCapture Capture(string workloadId)
        => new(workloadId, ["line1", "line2"], [], null);

    private static WorkloadDesiredSpec Spec(string workloadId) => new()
    {
        GroupInstanceId = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee"),
        GroupId = "reeve",
        WorkloadId = workloadId,
        InstanceId = Guid.Parse("12345678-0000-0000-0000-000000000000"),
        Inst8 = "12345678",
        Provider = StandInProviders.KubernetesId,
        ResourceName = $"reeve-{workloadId}",
        SecretName = $"reeve-{workloadId}-secret",
        Labels = new Dictionary<string, string>(),
        Image = $"ghcr.io/reeve/{workloadId}@sha256:" + new string('a', 64),
        Kind = WorkloadKind.SingleNode,
        Health = new WorkloadHealth { Mode = WorkloadHealthMode.Container },
        SpecHash = "0123abcd",
    };

    private static WorkloadUpdateIntent InPhase(WorkloadUpdatePhase target)
    {
        var intent = NewIntent();
        var t = Now;
        foreach (var phase in PathTo(target))
        {
            t = t.AddSeconds(1);
            intent.AdvanceTo(phase, t);
        }

        intent.Phase.Should().Be(target);
        return intent;
    }

    private static IEnumerable<WorkloadUpdatePhase> PathTo(WorkloadUpdatePhase target)
        => target switch
        {
            WorkloadUpdatePhase.StagingParams => Array.Empty<WorkloadUpdatePhase>(),
            WorkloadUpdatePhase.ApplyingSidecar =>
            [
                WorkloadUpdatePhase.ApplyingSidecar
            ],
            WorkloadUpdatePhase.HealthGate =>
            [
                WorkloadUpdatePhase.ApplyingSidecar,
                WorkloadUpdatePhase.HealthGate
            ],
            WorkloadUpdatePhase.SwappingModule =>
            [
                WorkloadUpdatePhase.ApplyingSidecar,
                WorkloadUpdatePhase.HealthGate,
                WorkloadUpdatePhase.SwappingModule
            ],
            WorkloadUpdatePhase.Completed =>
            [
                WorkloadUpdatePhase.ApplyingSidecar,
                WorkloadUpdatePhase.HealthGate,
                WorkloadUpdatePhase.SwappingModule,
                WorkloadUpdatePhase.Completed
            ],
            WorkloadUpdatePhase.RollingBack =>
            [
                WorkloadUpdatePhase.ApplyingSidecar,
                WorkloadUpdatePhase.HealthGate,
                WorkloadUpdatePhase.RollingBack
            ],
            WorkloadUpdatePhase.RolledBack =>
            [
                WorkloadUpdatePhase.ApplyingSidecar,
                WorkloadUpdatePhase.HealthGate,
                WorkloadUpdatePhase.RollingBack,
                WorkloadUpdatePhase.RolledBack
            ],
            WorkloadUpdatePhase.RollbackFailed =>
            [
                WorkloadUpdatePhase.ApplyingSidecar,
                WorkloadUpdatePhase.HealthGate,
                WorkloadUpdatePhase.RollingBack,
                WorkloadUpdatePhase.RollbackFailed
            ],
            _ => throw new ArgumentOutOfRangeException(nameof(target))
        };

    [Fact]
    public void New_intent_starts_in_StagingParams_active_and_uncompleted()
    {
        var intent = NewIntent();

        intent.Phase.Should().Be(WorkloadUpdatePhase.StagingParams);
        intent.IsActive.Should().BeTrue();
        intent.IsTerminal.Should().BeFalse();
        intent.CompletedAtUtc.Should().BeNull();
        intent.PhaseEnteredAtUtc.Should().Be(Now);
        intent.CreatedAtUtc.Should().Be(Now);
        intent.SidecarAppliedAtUtc.Should().BeNull();
        intent.FromVersion.Should().Be("2.5.0");
        intent.ToVersion.Should().Be("2.6.0");
    }

    [Fact]
    public void Create_requires_a_plugin_instance_id_and_to_version()
    {
        var act1 = () => WorkloadUpdateIntent.Create(Guid.NewGuid(), Guid.Empty, "1", "2", Now);
        act1.Should().Throw<ArgumentException>();

        var act2 = () => WorkloadUpdateIntent.Create(Guid.NewGuid(), Guid.NewGuid(), "1", "  ", Now);
        act2.Should().Throw<ArgumentException>();

        var ok = () => WorkloadUpdateIntent.Create(Guid.NewGuid(), Guid.NewGuid(), null, "2", Now);
        ok.Should().NotThrow();
    }

    [Fact]
    public void Happy_path_advances_staging_to_completed()
    {
        var intent = NewIntent();

        intent.AdvanceTo(WorkloadUpdatePhase.ApplyingSidecar, Now.AddSeconds(1));
        intent.AdvanceTo(WorkloadUpdatePhase.HealthGate, Now.AddSeconds(2));
        intent.AdvanceTo(WorkloadUpdatePhase.SwappingModule, Now.AddSeconds(3));
        intent.Complete(Now.AddSeconds(4));

        intent.Phase.Should().Be(WorkloadUpdatePhase.Completed);
        intent.IsTerminal.Should().BeTrue();
        intent.CompletedAtUtc.Should().Be(Now.AddSeconds(4));
    }

    [Fact]
    public void Phase_entered_at_is_restamped_on_every_transition()
    {
        var intent = NewIntent();
        intent.PhaseEnteredAtUtc.Should().Be(Now);

        intent.AdvanceTo(WorkloadUpdatePhase.ApplyingSidecar, Now.AddSeconds(5));
        intent.PhaseEnteredAtUtc.Should().Be(Now.AddSeconds(5), "PhaseEnteredAtUtc anchors the park-age/gate clock");

        intent.AdvanceTo(WorkloadUpdatePhase.HealthGate, Now.AddSeconds(20));
        intent.PhaseEnteredAtUtc.Should().Be(Now.AddSeconds(20));
    }

    [Fact]
    public void Sidecar_applied_at_fallback_on_leaving_applying_sidecar_anchors_at_intent_creation_not_the_transition()
    {
        var intent = InPhase(WorkloadUpdatePhase.ApplyingSidecar);
        intent.SidecarAppliedAtUtc.Should().BeNull("the anchor is not set until the apply phase completes");

        var transitionAt = Now.AddMinutes(1);
        intent.AdvanceTo(WorkloadUpdatePhase.HealthGate, transitionAt);

        intent.SidecarAppliedAtUtc.Should().Be(intent.CreatedAtUtc,
            "the fallback HealthGate anchor is the intent's creation time, which precedes ANY (reconciler or orchestrator) apply");
        intent.SidecarAppliedAtUtc.Should().NotBe(transitionAt,
            "anchoring at the HealthGate-entry transition would wrongly reject a reconciler-brought-up new session as pre-apply");
    }

    [Fact]
    public void Sidecar_applied_at_is_stamped_at_apply_START_not_the_healthgate_exit()
    {
        var intent = InPhase(WorkloadUpdatePhase.ApplyingSidecar);
        intent.SidecarAppliedAtUtc.Should().BeNull();

        var applyStart = Now.AddSeconds(1);
        intent.StampSidecarApplyStart(applyStart);
        intent.SidecarAppliedAtUtc.Should().Be(applyStart, "the anchor is stamped when the apply BEGINS");

        var healthGateEntry = Now.AddMinutes(1);
        intent.AdvanceTo(WorkloadUpdatePhase.HealthGate, healthGateEntry);
        intent.SidecarAppliedAtUtc.Should().Be(applyStart,
            "the anchor stays at apply-start so a session that connects between apply-start and the HealthGate entry "
            + "still qualifies as post-apply");
    }

    [Fact]
    public void StampSidecarApplyStart_is_idempotent_and_only_stamps_during_applying_sidecar()
    {
        var staging = NewIntent();
        staging.StampSidecarApplyStart(Now.AddSeconds(1));
        staging.SidecarAppliedAtUtc.Should().BeNull("apply-start is only anchored during the apply phase");

        var intent = InPhase(WorkloadUpdatePhase.ApplyingSidecar);
        var first = Now.AddSeconds(2);
        intent.StampSidecarApplyStart(first);
        intent.StampSidecarApplyStart(Now.AddSeconds(9));
        intent.SidecarAppliedAtUtc.Should().Be(first, "only the first apply-start is anchored");
    }

    [Fact]
    public void Sidecar_applied_at_is_stable_once_stamped()
    {
        var intent = InPhase(WorkloadUpdatePhase.HealthGate);
        var anchor = intent.SidecarAppliedAtUtc;
        anchor.Should().NotBeNull();

        intent.AdvanceTo(WorkloadUpdatePhase.RollingBack, Now.AddMinutes(10));
        intent.SidecarAppliedAtUtc.Should().Be(anchor, "the recorded apply anchor is immutable for the intent");
    }

    [Fact]
    public void Completed_transition_stamps_completed_at_only_on_the_terminal_move()
    {
        var intent = InPhase(WorkloadUpdatePhase.SwappingModule);
        intent.CompletedAtUtc.Should().BeNull("no terminal phase reached yet");

        intent.Complete(Now.AddMinutes(2));
        intent.CompletedAtUtc.Should().Be(Now.AddMinutes(2));
    }

    [Fact]
    public void Rolled_back_and_rollback_failed_are_terminal_and_stamp_completed_at()
    {
        var rolledBack = InPhase(WorkloadUpdatePhase.RollingBack);
        rolledBack.CompletedAtUtc.Should().BeNull();
        rolledBack.AdvanceTo(WorkloadUpdatePhase.RolledBack, Now.AddMinutes(3));
        rolledBack.IsTerminal.Should().BeTrue();
        rolledBack.CompletedAtUtc.Should().Be(Now.AddMinutes(3));

        var failed = InPhase(WorkloadUpdatePhase.RollingBack);
        failed.MarkRollbackFailed("teardown never converged", Now.AddMinutes(4));
        failed.Phase.Should().Be(WorkloadUpdatePhase.RollbackFailed);
        failed.IsTerminal.Should().BeTrue();
        failed.CompletedAtUtc.Should().Be(Now.AddMinutes(4));
        failed.Error.Should().Be("teardown never converged");
    }

    [Fact]
    public void Fail_moves_an_active_intent_into_rolling_back_and_records_the_error()
    {
        var intent = InPhase(WorkloadUpdatePhase.HealthGate);

        intent.Fail("gate timed out: no HELLO", Now.AddMinutes(5));

        intent.Phase.Should().Be(WorkloadUpdatePhase.RollingBack, "a phase-2/3 failure enters RollingBack");
        intent.Error.Should().Be("gate timed out: no HELLO");
        intent.IsTerminal.Should().BeFalse("RollingBack is not terminal; RolledBack/RollbackFailed are");
        intent.CompletedAtUtc.Should().BeNull();
    }

    [Theory]
    [InlineData(nameof(WorkloadUpdatePhase.StagingParams), nameof(WorkloadUpdatePhase.ApplyingSidecar))]
    [InlineData(nameof(WorkloadUpdatePhase.ApplyingSidecar), nameof(WorkloadUpdatePhase.HealthGate))]
    [InlineData(nameof(WorkloadUpdatePhase.HealthGate), nameof(WorkloadUpdatePhase.SwappingModule))]
    [InlineData(nameof(WorkloadUpdatePhase.SwappingModule), nameof(WorkloadUpdatePhase.Completed))]
    [InlineData(nameof(WorkloadUpdatePhase.StagingParams), nameof(WorkloadUpdatePhase.RollingBack))]
    [InlineData(nameof(WorkloadUpdatePhase.ApplyingSidecar), nameof(WorkloadUpdatePhase.RollingBack))]
    [InlineData(nameof(WorkloadUpdatePhase.HealthGate), nameof(WorkloadUpdatePhase.RollingBack))]
    [InlineData(nameof(WorkloadUpdatePhase.SwappingModule), nameof(WorkloadUpdatePhase.RollingBack))]
    [InlineData(nameof(WorkloadUpdatePhase.SwappingModule), nameof(WorkloadUpdatePhase.RollbackFailed))]
    [InlineData(nameof(WorkloadUpdatePhase.RollingBack), nameof(WorkloadUpdatePhase.RolledBack))]
    [InlineData(nameof(WorkloadUpdatePhase.RollingBack), nameof(WorkloadUpdatePhase.RollbackFailed))]
    public void Legal_phase_transitions_are_accepted(string fromName, string toName)
    {
        var from = Enum.Parse<WorkloadUpdatePhase>(fromName);
        var to = Enum.Parse<WorkloadUpdatePhase>(toName);

        WorkloadUpdateIntentPhaseMachine.IsValidTransition(from, to).Should().BeTrue();

        var intent = InPhase(from);
        var act = () => intent.AdvanceTo(to, Now.AddMinutes(1));
        act.Should().NotThrow($"{from} → {to} is a legal phase transition");
        intent.Phase.Should().Be(to);
    }

    [Fact]
    public void Every_illegal_phase_transition_is_rejected_exhaustively()
    {
        var allPhases = Enum.GetValues<WorkloadUpdatePhase>();

        foreach (var from in allPhases)
        {
            foreach (var to in allPhases)
            {
                if (from == to)
                {
                    WorkloadUpdateIntentPhaseMachine.IsValidTransition(from, to).Should().BeFalse(
                        $"{from} → {to} (self) is not a modelled advance");
                    continue;
                }

                if (WorkloadUpdateIntentPhaseMachine.IsValidTransition(from, to))
                {
                    continue;
                }

                var intent = InPhase(from);
                var act = () => intent.AdvanceTo(to, Now.AddMinutes(1));
                act.Should().Throw<WorkloadUpdateIntentTransitionException>(
                        $"{from} → {to} is NOT a legal transition and must fail closed")
                    .Where(ex => ex.From == from && ex.To == to);
            }
        }
    }

    [Fact]
    public void Advancing_out_of_a_terminal_phase_is_rejected()
    {
        foreach (var terminal in new[]
                 {
                     WorkloadUpdatePhase.Completed,
                     WorkloadUpdatePhase.RolledBack,
                     WorkloadUpdatePhase.RollbackFailed
                 })
        {
            var intent = InPhase(terminal);
            intent.IsTerminal.Should().BeTrue();

            var act = () => intent.AdvanceTo(WorkloadUpdatePhase.ApplyingSidecar, Now.AddMinutes(1));
            act.Should().Throw<WorkloadUpdateIntentTransitionException>(
                $"{terminal} is terminal — no further advance is legal");
        }
    }

    [Fact]
    public void Complete_is_only_legal_from_swapping_module()
    {
        InPhase(WorkloadUpdatePhase.SwappingModule)
            .Invoking(i => i.Complete(Now.AddMinutes(1)))
            .Should().NotThrow();

        InPhase(WorkloadUpdatePhase.HealthGate)
            .Invoking(i => i.Complete(Now.AddMinutes(1)))
            .Should().Throw<WorkloadUpdateIntentTransitionException>(
                "Completed is reached only after the module swap is acknowledged");
    }

    [Fact]
    public void Mark_rollback_failed_is_only_legal_from_rolling_back()
    {
        InPhase(WorkloadUpdatePhase.RollingBack)
            .Invoking(i => i.MarkRollbackFailed("x", Now.AddMinutes(1)))
            .Should().NotThrow();

        InPhase(WorkloadUpdatePhase.HealthGate)
            .Invoking(i => i.MarkRollbackFailed("x", Now.AddMinutes(1)))
            .Should().Throw<WorkloadUpdateIntentTransitionException>(
                "RollbackFailed is only reachable from RollingBack");
    }

    [Fact]
    public void Mark_swapping_module_unresumable_terminalizes_directly_to_rollback_failed()
    {
        var intent = InPhase(WorkloadUpdatePhase.SwappingModule);

        intent.MarkSwappingModuleUnresumable("corrupt target after DB-restore", Now.AddMinutes(1));

        intent.Phase.Should().Be(WorkloadUpdatePhase.RollbackFailed);
        intent.IsTerminal.Should().BeTrue();
        intent.Error.Should().Contain("corrupt target after DB-restore");
        intent.CompletedAtUtc.Should().Be(Now.AddMinutes(1));
    }

    [Fact]
    public void Mark_swapping_module_unresumable_is_only_legal_from_swapping_module()
    {
        InPhase(WorkloadUpdatePhase.HealthGate)
            .Invoking(i => i.MarkSwappingModuleUnresumable("x", Now.AddMinutes(1)))
            .Should().Throw<WorkloadUpdateIntentTransitionException>(
                "the direct terminalization edge is only legal from SwappingModule");

        InPhase(WorkloadUpdatePhase.RollingBack)
            .Invoking(i => i.MarkSwappingModuleUnresumable("x", Now.AddMinutes(1)))
            .Should().Throw<WorkloadUpdateIntentTransitionException>(
                "a RollingBack intent terminalizes via MarkRollbackFailed, not the SwappingModule edge");
    }

    [Fact]
    public void Error_is_truncated_at_the_cap()
    {
        var intent = InPhase(WorkloadUpdatePhase.HealthGate);
        var longError = new string('e', WorkloadUpdateIntent.MaxErrorLength + 777);

        intent.Fail(longError, Now.AddMinutes(1));

        intent.Error!.Length.Should().Be(WorkloadUpdateIntent.MaxErrorLength);
    }

    [Fact]
    public void Stage_snapshots_carries_the_adopt_decision_so_a_re_plan_can_replay_it()
    {
        var intent = NewIntent();

        intent.StageSnapshots(new WorkloadUpdateSnapshots { AdoptedResourceWorkloadIds = ["zigbee2mqtt"] }, Now);

        intent.GetAdoptedResourceWorkloadIds().Should().BeEquivalentTo(["zigbee2mqtt"]);
    }

    [Fact]
    public void An_intent_that_staged_no_adopt_decision_replays_as_no_adoption()
    {
        var intent = NewIntent();

        intent.StageSnapshots(new WorkloadUpdateSnapshots(), Now);

        intent.GetAdoptedResourceWorkloadIds().Should().BeEmpty();
    }

    [Fact]
    public void Stage_snapshots_populates_all_five_snapshot_documents_and_does_not_advance()
    {
        var intent = NewIntent();

        intent.StageSnapshots(
            new WorkloadUpdateSnapshots
            {
                TargetSpecs = new Dictionary<string, WorkloadDesiredSpec> { ["a"] = Spec("a") },
                PreviousSpecs = new Dictionary<string, WorkloadDesiredSpec> { ["a"] = Spec("a") },
                PreviousParameters = new Dictionary<string, IReadOnlyDictionary<string, string?>>
                {
                    ["a"] = new Dictionary<string, string?> { ["p"] = "v" },
                },
                FromDigests = new Dictionary<string, string?> { ["a"] = "sha256:from" },
                ToDigests = new Dictionary<string, string?> { ["a"] = "sha256:to" },
            },
            Now.AddSeconds(3));

        intent.ReadTargetSpecs().Value.Should().ContainKey("a");
        intent.ReadPreviousSpecs().Value.Should().ContainKey("a");
        intent.ReadPreviousParameters()!["a"]["p"].Should().Be("v");
        intent.ReadFromDigests()["a"].Should().Be("sha256:from");
        intent.ReadToDigests()["a"].Should().Be("sha256:to");
        intent.Phase.Should().Be(WorkloadUpdatePhase.StagingParams, "staging snapshots does not itself advance phase");
    }

    [Theory]
    [InlineData(nameof(WorkloadUpdatePhase.Completed))]
    [InlineData(nameof(WorkloadUpdatePhase.RolledBack))]
    [InlineData(nameof(WorkloadUpdatePhase.RollbackFailed))]
    public void Reaching_a_terminal_phase_discards_the_submitted_parameters(string terminalName)
    {
        var terminal = Enum.Parse<WorkloadUpdatePhase>(terminalName);

        var intent = NewIntent();
        intent.StageSnapshots(
            new WorkloadUpdateSnapshots
            {
                OperatorParameters = new Dictionary<string, string?> { ["dbPassword"] = "s3cret" },
            },
            Now.AddMinutes(1));
        intent.ReadOperatorParameters().Should().NotBeNull("arrange: there must be something to discard");

        var walked = InPhase(terminal);

        walked.ReadOperatorParameters().Should().BeNull();
        intent.Phase.Should().Be(WorkloadUpdatePhase.StagingParams,
            "arrange sanity: the staged intent above is a separate one and must not have moved");
    }

    [Fact]
    public void An_intent_that_completes_from_a_staged_start_discards_its_submitted_parameters()
    {
        var intent = NewIntent();
        intent.StageSnapshots(
            new WorkloadUpdateSnapshots
            {
                OperatorParameters = new Dictionary<string, string?> { ["serialPort"] = "/dev/ttyACM1" },
            },
            Now.AddMinutes(1));

        var t = Now;
        foreach (var phase in PathTo(WorkloadUpdatePhase.SwappingModule))
        {
            t = t.AddSeconds(1);
            intent.AdvanceTo(phase, t);
        }

        intent.ReadOperatorParameters().Should().NotBeNull(
            "they are still needed while the plan is rebuilt on every non-terminal tick");

        intent.Complete(t.AddSeconds(1));

        intent.ReadOperatorParameters().Should().BeEmpty();
    }

    [Fact]
    public void An_intent_that_completes_from_a_staged_start_discards_its_adopt_decision()
    {
        var intent = NewIntent();
        intent.StageSnapshots(
            new WorkloadUpdateSnapshots { AdoptedResourceWorkloadIds = ["zigbee2mqtt"] }, Now.AddMinutes(1));

        var t = Now;
        foreach (var phase in PathTo(WorkloadUpdatePhase.SwappingModule))
        {
            t = t.AddSeconds(1);
            intent.AdvanceTo(phase, t);
        }

        intent.GetAdoptedResourceWorkloadIds().Should().BeEquivalentTo(["zigbee2mqtt"],
            "every non-terminal tick re-plans, and dropping it early would silently un-adopt mid-update");

        intent.Complete(t.AddSeconds(1));

        intent.AdoptedResourceWorkloadIds.Should().BeNull();
    }

    [Theory]
    [InlineData(nameof(WorkloadUpdatePhase.RolledBack))]
    [InlineData(nameof(WorkloadUpdatePhase.RollbackFailed))]
    public void A_terminal_rollback_also_discards_the_adopt_decision(string terminalName)
    {
        var terminal = Enum.Parse<WorkloadUpdatePhase>(terminalName);

        var intent = NewIntent();
        intent.StageSnapshots(
            new WorkloadUpdateSnapshots { AdoptedResourceWorkloadIds = ["zigbee2mqtt"] }, Now.AddMinutes(1));
        intent.AdoptedResourceWorkloadIds.Should().NotBeNull("arrange: there must be something to discard");

        var t = Now;
        foreach (var phase in PathTo(terminal))
        {
            t = t.AddSeconds(1);
            intent.AdvanceTo(phase, t);
        }

        intent.AdoptedResourceWorkloadIds.Should().BeNull();
    }

    [Fact]
    public void Stage_snapshots_is_rejected_outside_staging_params()
    {
        var intent = InPhase(WorkloadUpdatePhase.ApplyingSidecar);

        var act = () => intent.StageSnapshots(new WorkloadUpdateSnapshots(), Now.AddMinutes(1));

        act.Should().Throw<WorkloadUpdateIntentTransitionException>(
            "snapshots are committed only during StagingParams");
    }

    [Fact]
    public void Capture_evidence_only_when_rolling_back_and_is_recorded()
    {
        var rolling = InPhase(WorkloadUpdatePhase.RollingBack);

        rolling.CaptureEvidence(new WorkloadUpdateEvidence("2.0.0", Now, [Capture("zigbee")]));

        rolling.ReadEvidence()!.Workloads.Should().ContainSingle()
            .Which.PreviousContainerLogTail.Should().Equal("line1", "line2");
    }

    [Fact]
    public void Capture_evidence_is_rejected_when_not_rolling_back()
    {
        var intent = InPhase(WorkloadUpdatePhase.HealthGate);

        var act = () => intent.CaptureEvidence(new WorkloadUpdateEvidence("2.0.0", Now, []));

        act.Should().Throw<WorkloadUpdateIntentTransitionException>(
            "evidence is captured on RollingBack (before teardown), not mid-gate");
    }

    [Fact]
    public void Is_terminal_matches_exactly_the_three_terminal_phases()
    {
        foreach (var phase in Enum.GetValues<WorkloadUpdatePhase>())
        {
            var isTerminalExpected = phase is WorkloadUpdatePhase.Completed
                or WorkloadUpdatePhase.RolledBack
                or WorkloadUpdatePhase.RollbackFailed;

            var intent = InPhase(phase);
            intent.IsTerminal.Should().Be(isTerminalExpected, $"{phase} terminal-ness");
            intent.IsActive.Should().Be(!isTerminalExpected);
        }
    }

    [Theory]
    [InlineData(nameof(WorkloadUpdatePhase.StagingParams))]
    [InlineData(nameof(WorkloadUpdatePhase.ApplyingSidecar))]
    [InlineData(nameof(WorkloadUpdatePhase.HealthGate))]
    public void Abandon_before_the_swap_requests_a_rollback(string fromName)
    {
        var from = Enum.Parse<WorkloadUpdatePhase>(fromName);

        var intent = InPhase(from);
        var versionBefore = intent.Version;

        var outcome = intent.Abandon("abandoned by operator", Now.AddMinutes(5));

        outcome.Should().Be(WorkloadUpdateAbandonOutcome.RollbackRequested);
        intent.Phase.Should().Be(WorkloadUpdatePhase.RollingBack);
        intent.IsTerminal.Should().BeFalse();
        intent.CompletedAtUtc.Should().BeNull();
        intent.Error.Should().Contain("abandoned by operator");
        intent.PhaseEnteredAtUtc.Should().Be(Now.AddMinutes(5));
        intent.Version.Should().BeGreaterThan(versionBefore);
    }

    [Fact]
    public void Abandon_while_rolling_back_gives_up_and_terminalizes()
    {
        var intent = InPhase(WorkloadUpdatePhase.RollingBack);

        var outcome = intent.Abandon("rollback abandoned by operator", Now.AddMinutes(9));

        outcome.Should().Be(WorkloadUpdateAbandonOutcome.RollbackAbandoned);
        intent.Phase.Should().Be(WorkloadUpdatePhase.RollbackFailed);
        intent.IsTerminal.Should().BeTrue();
        intent.CompletedAtUtc.Should().Be(Now.AddMinutes(9));
        intent.Error.Should().Contain("rollback abandoned by operator");
    }

    [Fact]
    public void Abandon_past_the_gate_starts_a_rollback()
    {
        var intent = InPhase(WorkloadUpdatePhase.SwappingModule);

        var outcome = intent.Abandon("let me out", Now.AddMinutes(1));

        outcome.Should().Be(WorkloadUpdateAbandonOutcome.RollbackRequested);
        intent.Phase.Should().Be(WorkloadUpdatePhase.RollingBack);
        intent.Error.Should().Be("let me out");
        intent.IsTerminal.Should().BeFalse();
    }

    [Theory]
    [InlineData(nameof(WorkloadUpdatePhase.Completed))]
    [InlineData(nameof(WorkloadUpdatePhase.RolledBack))]
    [InlineData(nameof(WorkloadUpdatePhase.RollbackFailed))]
    public void Abandon_is_refused_once_terminal(string fromName)
    {
        var from = Enum.Parse<WorkloadUpdatePhase>(fromName);

        var intent = InPhase(from);
        var versionBefore = intent.Version;

        intent.Invoking(i => i.Abandon("too late", Now.AddMinutes(1)))
            .Should().Throw<WorkloadUpdateIntentTransitionException>();

        intent.Phase.Should().Be(from);
        intent.Version.Should().Be(versionBefore);
    }

    [Fact]
    public void Abandon_truncates_its_reason_at_the_error_cap()
    {
        var intent = InPhase(WorkloadUpdatePhase.HealthGate);
        var longReason = new string('a', WorkloadUpdateIntent.MaxErrorLength + 500);

        intent.Abandon(longReason, Now.AddMinutes(1));

        intent.Error!.Length.Should().Be(WorkloadUpdateIntent.MaxErrorLength);
    }

    [Fact]
    public void Abandon_requires_a_reason()
    {
        InPhase(WorkloadUpdatePhase.HealthGate)
            .Invoking(i => i.Abandon("  ", Now.AddMinutes(1)))
            .Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Abandon_adds_no_new_edges_to_the_phase_machine()
    {
        WorkloadUpdateIntentPhaseMachine.AllowedTargets(WorkloadUpdatePhase.SwappingModule)
            .Should().BeEquivalentTo(new[]
            {
                WorkloadUpdatePhase.Completed, WorkloadUpdatePhase.RollingBack, WorkloadUpdatePhase.RollbackFailed,
            });
        WorkloadUpdateIntentPhaseMachine.AllowedTargets(WorkloadUpdatePhase.HealthGate)
            .Should().Contain(WorkloadUpdatePhase.RollingBack);
        WorkloadUpdateIntentPhaseMachine.AllowedTargets(WorkloadUpdatePhase.RollingBack)
            .Should().Contain(WorkloadUpdatePhase.RollbackFailed);
    }
}

public sealed class WorkloadUpdateIntentStepTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 6, 12, 0, 0, TimeSpan.Zero);

    private static WorkloadUpdateIntentStep NewStep()
        => WorkloadUpdateIntentStep.Create(Guid.NewGuid(), Guid.NewGuid(), "matter-server", Now);

    [Fact]
    public void New_step_starts_pending_at_staging_params()
    {
        var step = NewStep();

        step.State.Should().Be(WorkloadUpdateStepState.Pending);
        step.Step.Should().Be(WorkloadUpdatePhase.StagingParams);
        step.WorkloadId.Should().Be("matter-server");
        step.Message.Should().BeNull();
    }

    [Fact]
    public void Create_requires_intent_id_and_workload_id()
    {
        var act1 = () => WorkloadUpdateIntentStep.Create(Guid.NewGuid(), Guid.Empty, "w", Now);
        act1.Should().Throw<ArgumentException>();

        var act2 = () => WorkloadUpdateIntentStep.Create(Guid.NewGuid(), Guid.NewGuid(), " ", Now);
        act2.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Advance_updates_step_phase_and_state_together()
    {
        var step = NewStep();

        step.Advance(WorkloadUpdatePhase.ApplyingSidecar, WorkloadUpdateStepState.Applying, "pulling image", Now.AddSeconds(1));

        step.Step.Should().Be(WorkloadUpdatePhase.ApplyingSidecar);
        step.State.Should().Be(WorkloadUpdateStepState.Applying);
        step.Message.Should().Be("pulling image");
        step.UpdatedAtUtc.Should().Be(Now.AddSeconds(1));
    }

    [Fact]
    public void Step_progresses_through_the_full_per_workload_lifecycle()
    {
        var step = NewStep();

        step.Advance(WorkloadUpdatePhase.ApplyingSidecar, WorkloadUpdateStepState.Applying, null, Now.AddSeconds(1));
        step.Advance(WorkloadUpdatePhase.HealthGate, WorkloadUpdateStepState.GateWaiting, null, Now.AddSeconds(2));
        step.Advance(WorkloadUpdatePhase.SwappingModule, WorkloadUpdateStepState.Swapping, null, Now.AddSeconds(3));
        step.Advance(WorkloadUpdatePhase.Completed, WorkloadUpdateStepState.Done, null, Now.AddSeconds(4));

        step.State.Should().Be(WorkloadUpdateStepState.Done);
        step.Step.Should().Be(WorkloadUpdatePhase.Completed);
    }

    [Fact]
    public void Step_can_record_a_rollback_or_failure_terminal_state()
    {
        var rolledBack = NewStep();
        rolledBack.Advance(WorkloadUpdatePhase.RolledBack, WorkloadUpdateStepState.RolledBack, "restored previous spec", Now.AddSeconds(1));
        rolledBack.State.Should().Be(WorkloadUpdateStepState.RolledBack);

        var failed = NewStep();
        failed.Advance(WorkloadUpdatePhase.RollbackFailed, WorkloadUpdateStepState.Failed, "old digest unpullable", Now.AddSeconds(1));
        failed.State.Should().Be(WorkloadUpdateStepState.Failed);
    }

    [Fact]
    public void Step_message_is_truncated_at_the_cap()
    {
        var step = NewStep();
        var longMessage = new string('m', WorkloadUpdateIntentStep.MaxMessageLength + 300);

        step.Advance(WorkloadUpdatePhase.HealthGate, WorkloadUpdateStepState.GateWaiting, longMessage, Now.AddSeconds(1));

        step.Message!.Length.Should().Be(WorkloadUpdateIntentStep.MaxMessageLength);
    }
}
