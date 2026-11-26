using Microsoft.AspNetCore.DataProtection;
using Reeve.Workloads.Core.Update.Stages;
using Reeve.Pipeline;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using static Reeve.Workloads.Core.Tests.Update.WorkloadUpdatePlanBuilder;
using Reeve.Workloads.Abstractions;
using Reeve.Workloads.Abstractions.Contracts;
using Reeve.Workloads.Abstractions.Contracts.Deployment;
using Reeve.Workloads.Abstractions.Domain;
using Reeve.Workloads.Abstractions.Domain.Entities;
using Reeve.Workloads.Abstractions.Domain.Entities.Documents;
using Reeve.Workloads.Abstractions.Domain.Network;
using Reeve.Workloads.Abstractions.Update;
using Reeve.Workloads.Core;
using Reeve.Workloads.Core.Tests.Groups;
using Reeve.Workloads.Core.Update;
using Reeve.Workloads.Domain;
using Reeve.Workloads.Domain.DesiredSpec;
using Xunit;

namespace Reeve.Workloads.Core.Tests.Update;

public sealed class WorkloadUpdateInitiatorTests
{
    private static readonly Guid Instance = InstanceGuid;
    private static readonly DateTimeOffset Seeded = new(2026, 9, 1, 0, 0, 0, TimeSpan.Zero);

    private readonly FakeInstances instances = new();

    private WorkloadUpdateInitiator Build(OrchestratorHarness h, RecordingNudge nudge)
        => new(h.Repo, h.Planner, instances, nudge, h.Pipeline, h.Clock, NullLogger<WorkloadUpdateInitiator>.Instance);

    private static DeploymentManifest Manifest(bool privileged)
        => new()
        {
            SchemaVersion = 1,
            Workloads =
            [
                new WorkloadManifest
                {
                    Id = "gateway",
                    DisplayName = "Gateway",
                    Image = new WorkloadImage { Ref = "reeve/zwave", Digest = "sha256:deadbeef", Platforms = ["linux/amd64"] },
                    Kind = WorkloadKind.Service,
                    Privileges = privileged ? new WorkloadPrivileges { Privileged = true } : null,
                    Health = new WorkloadHealth { Mode = new WorkloadHealthMode("adapter-session"), HeartbeatSeconds = 30 },
                },
            ],
        };

    private static IReadOnlyDictionary<string, WorkloadPrivilegeRequirements> RequirementsFor(DeploymentManifest manifest)
        => manifest.Workloads.ToDictionary(
            w => w.Id,
            w => WorkloadPrivilegeRequirements.FromWorkload(w, new Dictionary<string, string>(StringComparer.Ordinal)),
            StringComparer.Ordinal);

    private static WorkloadUpdatePlan PlanWith(DeploymentManifest previous, DeploymentManifest candidate)
        => Plan(
            Instance,
            [Item("gateway", Instance, "sha-new", "sha-old")],
            previous: previous,
            candidate: candidate,
            prevRequirements: RequirementsFor(previous),
            candRequirements: RequirementsFor(candidate));

    private static WorkloadUpdatePlan CleanPlan() => PlanWith(Manifest(privileged: false), Manifest(privileged: false));

    private static WorkloadUpdateInitiationRequest Request(
        WorkloadUpdatePlan plan,
        string? fingerprint = null,
        IReadOnlyDictionary<string, WorkloadAttachmentPin>? pins = null,
        string? version = null,
        IReadOnlyDictionary<string, string?>? operatorParameters = null,
        IReadOnlyCollection<string>? adopt = null)
        => new()
        {
            PluginInstanceId = Instance,
            CandidateVersion = version ?? plan.ToVersion,
            Precondition = new WorkloadWritePrecondition
            {
                RequirementsFingerprint = fingerprint ?? plan.RequirementsFingerprint,
                AttachmentPins = pins ?? new Dictionary<string, WorkloadAttachmentPin>(StringComparer.Ordinal),
            },
            OperatorParameters = operatorParameters,
            AdoptDeclaredResourceWorkloadIds = adopt,
        };

    private static WorkloadAttachmentPin Pin(string name = "iot-macvlan") => new()
    {
        Mode = WorkloadNetworkMode.OperatorNetwork,
        AttachmentNames = [name],
    };

    private WorkloadInstance SeedRow(string workloadId)
    {
        var row = WorkloadInstance.Create(Guid.NewGuid(), Instance, "zwave", workloadId, Seeded);
        instances.Instances.Add(row);
        return row;
    }

    [Fact]
    public async Task A_started_update_names_its_point_of_no_return()
    {
        var plan = CleanPlan();
        var h = new OrchestratorHarness(plan);

        var started = await Build(h, new RecordingNudge()).InitiateAsync(Request(plan));

        started.PointOfNoReturn.Should().Be(new WorkloadUpdatePointOfNoReturn(
            ScriptedHostStage.StageId, ScriptedHostStage.IrreversibleReason));
    }

    [Fact]
    public async Task A_host_stage_that_is_irreversible_earlier_is_the_point_of_no_return()
    {
        var plan = CleanPlan();
        var h = new OrchestratorHarness(plan);
        var early = new IrreversibleStage("stage.host.migrate", StagePlacement.After(WorkloadUpdateStages.CommitVersion, -1), "the host migrates its schema");
        var initiator = new WorkloadUpdateInitiator(h.Repo, h.Planner, instances, new RecordingNudge(),
            h.PipelineWith(h.Gate, h.Clock, h.Options, early), h.Clock, NullLogger<WorkloadUpdateInitiator>.Instance);

        var started = await initiator.InitiateAsync(Request(plan));

        started.PointOfNoReturn.Should().Be(new WorkloadUpdatePointOfNoReturn("stage.host.migrate", "the host migrates its schema"));
    }

    [Fact]
    public async Task A_refused_initiation_names_no_point_of_no_return()
    {
        var plan = CleanPlan();
        var h = new OrchestratorHarness(plan);
        var initiator = Build(h, new RecordingNudge());
        await initiator.InitiateAsync(Request(plan));

        var second = await initiator.InitiateAsync(Request(plan, version: "2.7.0"));

        second.PointOfNoReturn.Should().BeNull("nothing was started, so there is nothing to be past");
    }

    private sealed record IrreversibleStage(string Id, StagePlacement Placement, string Reason) : IStage<WorkloadUpdateStageContext>
    {
        public StageReversibility Reversibility => StageReversibility.IrreversibleOnceEntered(Reason);

        public StageBudget Budget => StageBudget.None;

        public ValueTask<StageOutcome> UpAsync(WorkloadUpdateStageContext context, CancellationToken cancellationToken)
            => ValueTask.FromResult(StageOutcome.Done);

        public ValueTask<StageOutcome> DownAsync(WorkloadUpdateStageContext context, CancellationToken cancellationToken)
            => ValueTask.FromResult(StageOutcome.Failed("irreversible"));
    }

    [Fact]
    public async Task Initiate_returns_already_in_progress_when_an_active_intent_exists()
    {
        var plan = CleanPlan();
        var h = new OrchestratorHarness(plan);
        var initiator = Build(h, new RecordingNudge());

        var first = await initiator.InitiateAsync(Request(plan, version: "2.6.0"));
        first.Outcome.Should().Be(WorkloadUpdateInitiationOutcome.Started);

        var second = await initiator.InitiateAsync(Request(plan, version: "2.7.0"));
        second.Outcome.Should().Be(WorkloadUpdateInitiationOutcome.AlreadyInProgress);
        second.IntentId.Should().Be(first.IntentId, "the 409 echoes the in-flight intent id");
        h.Repo.All.Should().HaveCount(1, "no second intent is staged");
    }

    [Fact]
    public async Task An_active_intent_answers_before_the_precondition_is_read()
    {
        var plan = CleanPlan();
        var h = new OrchestratorHarness(plan);
        var initiator = Build(h, new RecordingNudge());
        await initiator.InitiateAsync(Request(plan));
        var builds = h.Planner.BuildCount;

        var second = await initiator.InitiateAsync(Request(plan, fingerprint: "stale"));

        second.Outcome.Should().Be(WorkloadUpdateInitiationOutcome.AlreadyInProgress);
        h.Planner.BuildCount.Should().Be(builds);
    }

    [Fact]
    public async Task A_group_with_a_host_held_member_is_refused_before_anything_is_written()
    {
        var plan = CleanPlan();
        var h = new OrchestratorHarness(plan);
        var nudge = new RecordingNudge();
        var initiator = Build(h, nudge);
        var held = SeedRow("gateway");
        held.TransitionTo(WorkloadInstanceState.Suspended, WorkloadStateReason.SuspendedByHost, "withdrawn", Seeded);

        var r = await initiator.InitiateAsync(Request(plan));

        r.Outcome.Should().Be(WorkloadUpdateInitiationOutcome.Refused);
        r.RefusalMessage.Should().Contain("gateway").And.Contain("held");
        h.Repo.All.Should().BeEmpty();
        instances.Updated.Should().BeEmpty();
        nudge.Nudges.Should().BeEmpty();
    }

    [Fact]
    public async Task A_fingerprint_that_does_not_match_the_plan_writes_nothing()
    {
        var plan = CleanPlan();
        var h = new OrchestratorHarness(plan);
        var nudge = new RecordingNudge();
        var initiator = Build(h, nudge);
        SeedRow("gateway");

        var r = await initiator.InitiateAsync(Request(
            plan,
            fingerprint: plan.RequirementsFingerprint + "-judged-earlier",
            pins: new Dictionary<string, WorkloadAttachmentPin> { ["gateway"] = Pin() }));

        r.Outcome.Should().Be(WorkloadUpdateInitiationOutcome.PreconditionFailed);
        r.IntentId.Should().BeNull();
        r.RefusalMessage.Should().Contain("zwave").And.Contain(plan.ToVersion);
        h.Repo.All.Should().BeEmpty();
        instances.Updated.Should().BeEmpty("a pin rides the write it was judged with, and there is no write");
        nudge.Nudges.Should().BeEmpty();
    }

    [Fact]
    public async Task An_escalating_update_with_a_matching_fingerprint_starts()
    {
        var plan = PlanWith(Manifest(privileged: false), Manifest(privileged: true));
        plan.CandidateRequirements["gateway"].Privileged.Should().BeTrue("the fixture escalates");
        var h = new OrchestratorHarness(plan);
        var nudge = new RecordingNudge();
        var initiator = Build(h, nudge);

        var r = await initiator.InitiateAsync(Request(plan));

        r.Outcome.Should().Be(WorkloadUpdateInitiationOutcome.Started);
        r.RefusalMessage.Should().BeNull();
        h.Repo.All.Should().ContainSingle().Which.Id.Should().Be(r.IntentId!.Value);
        nudge.Nudges.Should().ContainSingle(n => n.Instance == Instance && n.Reason == "workload-update-initiated");
    }

    [Fact]
    public async Task A_fingerprint_that_matches_only_ignoring_case_writes_nothing()
    {
        var plan = CleanPlan();
        var h = new OrchestratorHarness(plan);
        var initiator = Build(h, new RecordingNudge());

        var r = await initiator.InitiateAsync(Request(plan, fingerprint: plan.RequirementsFingerprint.ToUpperInvariant()));

        plan.RequirementsFingerprint.Should().NotBe(plan.RequirementsFingerprint.ToUpperInvariant(), "the fixture needs letters");
        r.Outcome.Should().Be(WorkloadUpdateInitiationOutcome.PreconditionFailed);
        h.Repo.All.Should().BeEmpty();
    }

    [Fact]
    public async Task A_null_precondition_is_refused_before_anything_is_read()
    {
        var plan = CleanPlan();
        var h = new OrchestratorHarness(plan);
        var initiator = Build(h, new RecordingNudge());

        await FluentActions.Awaiting(() => initiator.InitiateAsync(Request(plan) with { Precondition = null! }))
            .Should().ThrowAsync<ArgumentNullException>();

        h.Planner.BuildCount.Should().Be(0);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task A_blank_fingerprint_is_refused_before_anything_is_read(string fingerprint)
    {
        var plan = CleanPlan();
        var h = new OrchestratorHarness(plan);
        var initiator = Build(h, new RecordingNudge());

        await FluentActions.Awaiting(() => initiator.InitiateAsync(Request(plan) with
            {
                Precondition = new WorkloadWritePrecondition { RequirementsFingerprint = fingerprint },
            }))
            .Should().ThrowAsync<ArgumentException>();

        h.Planner.BuildCount.Should().Be(0);
    }

    public static TheoryData<Exception> PlannerFailures => new()
    {
        new InvalidOperationException("Candidate manifest for 'zwave' version '2.6.0' could not be resolved."),
        new WorkloadParameterResolutionException("serialPort", "Parameter 'serialPort' is not a device path."),
        new WorkloadHostRefusedException("the broker is not provisioned for this workload"),
    };

    [Theory]
    [MemberData(nameof(PlannerFailures))]
    public async Task A_plan_that_cannot_be_built_is_refused_with_its_message(Exception failure)
    {
        var plan = CleanPlan();
        var h = new OrchestratorHarness(plan);
        h.Planner.Override = () => throw failure;
        var nudge = new RecordingNudge();
        var initiator = Build(h, nudge);
        SeedRow("gateway");

        var r = await initiator.InitiateAsync(Request(
            plan,
            operatorParameters: new Dictionary<string, string?> { ["serialPort"] = "/dev/ttyACM1" },
            pins: new Dictionary<string, WorkloadAttachmentPin> { ["gateway"] = Pin() }));

        r.Outcome.Should().Be(WorkloadUpdateInitiationOutcome.Refused);
        r.RefusalMessage.Should().Be(failure.Message);
        r.IntentId.Should().BeNull();
        h.Repo.All.Should().BeEmpty("nothing is staged, so nothing carries the edit forward");
        instances.Updated.Should().BeEmpty();
        nudge.Nudges.Should().BeEmpty();
    }

    [Fact]
    public async Task Another_planner_fault_propagates()
    {
        var plan = CleanPlan();
        var h = new OrchestratorHarness(plan);
        h.Planner.Override = () => throw new NullReferenceException("a bug");
        var initiator = Build(h, new RecordingNudge());

        await FluentActions.Awaiting(() => initiator.InitiateAsync(Request(plan)))
            .Should().ThrowAsync<NullReferenceException>();
        h.Repo.All.Should().BeEmpty();
    }

    [Fact]
    public async Task A_pin_for_an_existing_row_is_recorded_before_the_intent_is_staged()
    {
        var plan = CleanPlan();
        var h = new OrchestratorHarness(plan);
        var initiator = Build(h, new RecordingNudge());
        var row = SeedRow("gateway");
        var other = SeedRow("bridge");

        var r = await initiator.InitiateAsync(Request(
            plan, pins: new Dictionary<string, WorkloadAttachmentPin> { ["gateway"] = Pin("accepted-net") }));

        r.Outcome.Should().Be(WorkloadUpdateInitiationOutcome.Started);
        instances.Updated.Should().ContainSingle().Which.Should().BeSameAs(row);
        row.ReadAttachmentPin().Value.Should().BeEquivalentTo(Pin("accepted-net"));
        row.UpdatedAtUtc.Should().Be(h.Clock.UtcNow);
        other.ReadAttachmentPin().Verdict.Should().Be(WorkloadDocumentVerdict.Absent, "only the named workload is pinned");
    }

    [Fact]
    public async Task A_pin_for_a_workload_with_no_row_writes_nothing()
    {
        var plan = CleanPlan();
        var h = new OrchestratorHarness(plan);
        var nudge = new RecordingNudge();
        var initiator = Build(h, nudge);
        var row = SeedRow("gateway");

        var r = await initiator.InitiateAsync(Request(plan, pins: new Dictionary<string, WorkloadAttachmentPin>
        {
            ["gateway"] = Pin(),
            ["added-by-the-update"] = Pin(),
        }));

        r.Outcome.Should().Be(WorkloadUpdateInitiationOutcome.PreconditionFailed);
        r.RefusalMessage.Should().Contain("added-by-the-update").And.NotContain("'gateway'");
        instances.Updated.Should().BeEmpty();
        row.ReadAttachmentPin().Verdict.Should().Be(WorkloadDocumentVerdict.Absent);
        h.Repo.All.Should().BeEmpty();
        nudge.Nudges.Should().BeEmpty();
    }

    [Fact]
    public async Task A_pin_write_that_loses_one_race_is_re_read_and_the_fresh_row_is_pinned()
    {
        var plan = CleanPlan();
        var h = new OrchestratorHarness(plan);
        var initiator = Build(h, new RecordingNudge());
        var stale = SeedRow("gateway");
        var fresh = WorkloadInstance.Create(stale.Id, Instance, "zwave", "gateway", Seeded);
        instances.ReloadAnswer = fresh;
        instances.ConflictsBeforeSuccess = 1;

        var r = await initiator.InitiateAsync(Request(
            plan, pins: new Dictionary<string, WorkloadAttachmentPin> { ["gateway"] = Pin("accepted-net") }));

        r.Outcome.Should().Be(WorkloadUpdateInitiationOutcome.Started);
        instances.Reloaded.Should().Equal(stale.Id);
        instances.Updated.Should().ContainSingle().Which.Should().BeSameAs(fresh);
        fresh.ReadAttachmentPin().Value.Should().BeEquivalentTo(Pin("accepted-net"));
        h.Repo.All.Should().ContainSingle("the intent is staged once the pin is through");
    }

    [Fact]
    public async Task A_pin_write_that_loses_on_another_pinned_row_re_reads_that_row()
    {
        var plan = CleanPlan();
        var h = new OrchestratorHarness(plan);
        var initiator = Build(h, new RecordingNudge());
        var gateway = SeedRow("gateway");
        var stale = SeedRow("bridge");
        var fresh = WorkloadInstance.Create(stale.Id, Instance, "zwave", "bridge", Seeded);
        instances.ReloadAnswersById[stale.Id] = fresh;
        instances.StaleRows.Add(stale.Id);

        var r = await initiator.InitiateAsync(Request(
            plan, pins: new Dictionary<string, WorkloadAttachmentPin>
            {
                ["gateway"] = Pin("gateway-net"),
                ["bridge"] = Pin("bridge-net"),
            }));

        r.Outcome.Should().Be(WorkloadUpdateInitiationOutcome.Started);
        instances.Reloaded.Should().Contain(stale.Id);
        instances.Updated.Should().Equal(gateway, fresh);
        fresh.ReadAttachmentPin().Value.Should().BeEquivalentTo(Pin("bridge-net"));
        gateway.ReadAttachmentPin().Value.Should().BeEquivalentTo(Pin("gateway-net"));
    }

    [Fact]
    public async Task Several_pinned_rows_that_are_all_stale_do_not_exhaust_the_retry_budget()
    {
        var plan = CleanPlan();
        var h = new OrchestratorHarness(plan);
        var initiator = Build(h, new RecordingNudge());
        var rows = new[] { SeedRow("gateway"), SeedRow("bridge"), SeedRow("relay"), SeedRow("hub") };
        instances.StaleRows.AddRange(rows.Select(row => row.Id));

        var r = await initiator.InitiateAsync(Request(
            plan, pins: rows.ToDictionary(
                row => row.WorkloadId, _ => Pin("pinned-net"), StringComparer.Ordinal)));

        r.Outcome.Should().Be(WorkloadUpdateInitiationOutcome.Started);
        instances.Updated.Should().HaveCount(rows.Length);
        instances.Updated.Should().OnlyContain(row => row.ReadAttachmentPin().Verdict == WorkloadDocumentVerdict.Value);
    }

    [Fact]
    public async Task A_refusal_naming_no_pinned_row_stands_without_a_re_read()
    {
        var plan = CleanPlan();
        var h = new OrchestratorHarness(plan);
        var initiator = Build(h, new RecordingNudge());
        SeedRow("gateway");
        instances.ConflictsOn = Guid.NewGuid();
        instances.ConflictsBeforeSuccess = 1;

        var act = async () => await initiator.InitiateAsync(Request(
            plan, pins: new Dictionary<string, WorkloadAttachmentPin> { ["gateway"] = Pin() }));

        await act.Should().ThrowAsync<WorkloadConcurrencyConflictException>();
        instances.Reloaded.Should().BeEmpty();
    }

    [Fact]
    public async Task A_pin_write_that_keeps_losing_gives_up_and_the_refusal_stands()
    {
        var plan = CleanPlan();
        var h = new OrchestratorHarness(plan);
        var initiator = Build(h, new RecordingNudge());
        var row = SeedRow("gateway");
        instances.ConflictsBeforeSuccess = 99;

        var act = async () => await initiator.InitiateAsync(Request(
            plan, pins: new Dictionary<string, WorkloadAttachmentPin> { ["gateway"] = Pin() }));

        await act.Should().ThrowAsync<WorkloadConcurrencyConflictException>();
        instances.Reloaded.Should().Equal(new[] { row.Id, row.Id }, "three attempts re-read twice, then the third throws out");
        instances.Updated.Should().BeEmpty();
        h.Repo.All.Should().BeEmpty("no intent is staged behind a pin that never landed");
    }

    [Fact]
    public async Task A_row_that_vanished_under_the_write_is_refused_without_further_attempts()
    {
        var plan = CleanPlan();
        var h = new OrchestratorHarness(plan);
        var initiator = Build(h, new RecordingNudge());
        var row = SeedRow("gateway");
        instances.ConflictsBeforeSuccess = 99;
        instances.ReloadAnswersNull = true;

        var act = async () => await initiator.InitiateAsync(Request(
            plan, pins: new Dictionary<string, WorkloadAttachmentPin> { ["gateway"] = Pin() }));

        await act.Should().ThrowAsync<WorkloadConcurrencyConflictException>();
        instances.Reloaded.Should().Equal(new[] { row.Id }, "one re-read told us the row is gone; nothing to try again");
    }

    [Fact]
    public async Task A_malformed_pin_saves_no_pin_and_stages_no_intent()
    {
        var plan = CleanPlan();
        var h = new OrchestratorHarness(plan);
        var initiator = Build(h, new RecordingNudge());
        SeedRow("bridge");
        SeedRow("gateway");

        await FluentActions.Awaiting(() => initiator.InitiateAsync(Request(plan, pins: new Dictionary<string, WorkloadAttachmentPin>
            {
                ["bridge"] = Pin(),
                ["gateway"] = Pin(name: " "),
            })))
            .Should().ThrowAsync<ArgumentException>();

        instances.Updated.Should().BeEmpty();
        h.Repo.All.Should().BeEmpty();
    }

    [Fact]
    public async Task A_matching_fingerprint_starts_and_stages_snapshots()
    {
        var plan = CleanPlan();
        var h = new OrchestratorHarness(plan);
        var nudge = new RecordingNudge();
        var initiator = Build(h, nudge);

        var r = await initiator.InitiateAsync(Request(plan));

        r.Outcome.Should().Be(WorkloadUpdateInitiationOutcome.Started);
        instances.Updated.Should().BeEmpty("no pin was named");
        var intent = h.Repo.All.Single();
        intent.Phase.Should().Be(WorkloadUpdatePhase.StagingParams);
        intent.FromVersion.Should().Be(plan.FromVersion);
        intent.ToVersion.Should().Be(plan.ToVersion);
        intent.ReadTargetSpecs().Value.Should().BeEquivalentTo(plan.TargetSpecs);
        intent.ReadPreviousSpecs().Value.Should().BeEquivalentTo(plan.PreviousSpecs);
        intent.Steps.Should().ContainSingle(s => s.WorkloadId == "gateway");
        nudge.Nudges.Should().ContainSingle(n => n.Instance == Instance);
    }

    [Fact]
    public async Task Operator_submitted_parameters_are_staged_onto_the_intent_and_reach_the_planner()
    {
        var plan = CleanPlan();
        var h = new OrchestratorHarness(plan);
        var initiator = Build(h, new RecordingNudge());
        var submitted = new Dictionary<string, string?> { ["serialPort"] = "/dev/ttyACM1" };

        var r = await initiator.InitiateAsync(Request(plan, operatorParameters: submitted));

        r.Outcome.Should().Be(WorkloadUpdateInitiationOutcome.Started);
        h.Planner.LastSubmittedParameters.Should().BeEquivalentTo(submitted,
            "the plan must be built against the values the operator submitted, not the stored ones");
        h.Repo.All.Single().ReadOperatorParameters().Should().BeEquivalentTo(submitted,
            "every later tick rebuilds the plan and re-reads them from the intent");
    }

    [Fact]
    public async Task An_in_flight_update_flags_differing_submitted_parameters_as_a_conflict()
    {
        var plan = CleanPlan();
        var h = new OrchestratorHarness(plan);
        var initiator = Build(h, new RecordingNudge());
        await initiator.InitiateAsync(Request(
            plan, operatorParameters: new Dictionary<string, string?> { ["serialPort"] = "/dev/ttyACM1" }));

        var second = await initiator.InitiateAsync(Request(
            plan, operatorParameters: new Dictionary<string, string?> { ["serialPort"] = "/dev/ttyUSB9" }));

        second.Outcome.Should().Be(WorkloadUpdateInitiationOutcome.AlreadyInProgress);
        second.SubmittedParametersDiffer.Should().BeTrue(
            "the caller must refuse rather than report success over parameters that will never be applied");
    }

    [Fact]
    public async Task An_identical_resubmit_is_not_a_conflict()
    {
        var plan = CleanPlan();
        var h = new OrchestratorHarness(plan);
        var initiator = Build(h, new RecordingNudge());
        var submitted = new Dictionary<string, string?> { ["serialPort"] = "/dev/ttyACM1" };
        await initiator.InitiateAsync(Request(plan, operatorParameters: submitted));

        var second = await initiator.InitiateAsync(Request(plan, operatorParameters: submitted));

        second.Outcome.Should().Be(WorkloadUpdateInitiationOutcome.AlreadyInProgress);
        second.SubmittedParametersDiffer.Should().BeFalse();
    }

    [Fact]
    public async Task A_resubmit_that_omits_a_staged_parameter_is_a_conflict()
    {
        var plan = CleanPlan();
        var h = new OrchestratorHarness(plan);
        var initiator = Build(h, new RecordingNudge());
        await initiator.InitiateAsync(Request(
            plan,
            operatorParameters: new Dictionary<string, string?> { ["serialPort"] = "/dev/ttyACM1", ["baudRate"] = "115200" }));

        var second = await initiator.InitiateAsync(Request(
            plan, operatorParameters: new Dictionary<string, string?> { ["serialPort"] = "/dev/ttyACM1" }));

        second.Outcome.Should().Be(WorkloadUpdateInitiationOutcome.AlreadyInProgress);
        second.SubmittedParametersDiffer.Should().BeTrue(
            "dropping baudRate is an edit the in-flight intent will never apply, so reporting success would hide it");
    }

    [Fact]
    public async Task An_in_flight_update_with_no_parameters_on_either_side_is_not_a_conflict()
    {
        var plan = CleanPlan();
        var h = new OrchestratorHarness(plan);
        var initiator = Build(h, new RecordingNudge());
        await initiator.InitiateAsync(Request(plan));

        var second = await initiator.InitiateAsync(Request(plan));

        second.SubmittedParametersDiffer.Should().BeFalse();
    }

    [Fact]
    public async Task The_adoption_decision_is_forwarded_to_the_planner()
    {
        var plan = CleanPlan();
        var h = new OrchestratorHarness(plan);
        var initiator = Build(h, new RecordingNudge());

        await initiator.InitiateAsync(Request(plan, adopt: ["zigbee2mqtt"]));

        h.Planner.LastAdoptedResourceWorkloadIds.Should().Equal(["zigbee2mqtt"]);
        h.Repo.All.Single().GetAdoptedResourceWorkloadIds().Should().Equal("zigbee2mqtt");
    }

    [Fact]
    public async Task An_update_with_no_adoption_forwards_nothing()
    {
        var plan = CleanPlan();
        var h = new OrchestratorHarness(plan);
        var initiator = Build(h, new RecordingNudge());

        await initiator.InitiateAsync(Request(plan));

        h.Planner.LastAdoptedResourceWorkloadIds.Should().BeNull();
    }
}
