using Reeve.Workloads.Abstractions.Contracts;
using Reeve.Workloads.Abstractions.Domain.Entities.Documents;
using System.Reflection;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Reeve.Pipeline;
using static Reeve.Workloads.Core.Tests.Update.WorkloadUpdatePlanBuilder;
using Reeve.Workloads.Abstractions;
using Reeve.Workloads.Abstractions.Contracts.Deployment;
using Reeve.Workloads.Abstractions.Domain;
using Reeve.Workloads.Abstractions.Domain.Entities;
using Reeve.Workloads.Abstractions.Domain.Network;
using Reeve.Workloads.Abstractions.Update;
using Reeve.Workloads.Core.Update;
using Reeve.Workloads.Core;
using Xunit;
using Reeve.Workloads.Domain;
using Reeve.Workloads.Domain.Network;

namespace Reeve.Workloads.Core.Tests.Update;

public sealed class WorkloadUpdateReadServiceTests
{
    private static readonly Guid Instance = InstanceGuid;
    private static readonly DateTimeOffset T0 = new(2026, 7, 6, 12, 0, 0, TimeSpan.Zero);

    private static readonly WorkloadExtensions ReeveEnvelope = new()
    {
        ByNamespace = System.Text.Json.JsonDocument
            .Parse("{\"reeve\":{\"protocol\":{\"version\":\"1\",\"minSupported\":\"1\"}}}")
            .RootElement.EnumerateObject()
            .ToDictionary(p => p.Name, p => p.Value.Clone(), StringComparer.Ordinal),
    };

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
                    Image = new WorkloadImage { Ref = "reeve/zwave", Digest = privileged ? "sha256:new" : "sha256:old", Platforms = ["linux/amd64"] },
                    Kind = WorkloadKind.Service,
                    Privileges = privileged ? new WorkloadPrivileges { Privileged = true } : null,
                    Health = new WorkloadHealth { Mode = new WorkloadHealthMode("adapter-session"), HeartbeatSeconds = 30 },
                    Extensions = ReeveEnvelope,
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

    private static readonly DateTimeOffset Stamp = new(2026, 9, 12, 10, 0, 0, TimeSpan.Zero);

    private static WorkloadUpdateIntent StagedIntent()
    {
        var intent = WorkloadUpdateIntent.Create(Guid.NewGuid(), Instance, "2.5.0", "2.6.0", Stamp);
        intent.StageSnapshots(
            new WorkloadUpdateSnapshots
            {
                FromDigests = new Dictionary<string, string?>(StringComparer.Ordinal) { ["gateway"] = "sha-old" },
                ToDigests = new Dictionary<string, string?>(StringComparer.Ordinal) { ["gateway"] = "sha-new" },
            },
            Stamp);
        return intent;
    }

    private static async Task<WorkloadUpdateIntentResponse?> ResponseFor(WorkloadUpdateIntent intent)
    {
        var repo = new InMemoryIntentRepository();
        await repo.AddAsync(intent);
        var planner = new ScriptedPlanner(PlanWith(Manifest(privileged: false), Manifest(privileged: false)));
        return await Build(planner, repo).GetIntentAsync(intent.Id);
    }

    [Fact]
    public async Task The_response_takes_the_digests_the_document_holds()
    {
        var intent = StagedIntent();
        Set(intent, nameof(WorkloadUpdateIntent.FromDigestsDocument), StringMap("FROM-DOCUMENT"));

        var response = await ResponseFor(intent);

        response!.FromDigests["gateway"].Should().Be("FROM-DOCUMENT");
        response.ToDigests["gateway"].Should().Be("sha-new");
    }

    [Fact]
    public async Task An_unreadable_digests_document_reads_as_no_digests()
    {
        var intent = StagedIntent();
        Set(intent, nameof(WorkloadUpdateIntent.FromDigestsDocument),
            new WorkloadStringMapDocument { Unreadable = true });

        var response = await ResponseFor(intent);

        response!.FromDigests.Should().BeEmpty();
        response.ToDigests["gateway"].Should().Be("sha-new");
    }

    [Fact]
    public async Task The_response_takes_the_evidence_the_document_holds()
    {
        var intent = StagedIntent();
        intent.AdvanceTo(WorkloadUpdatePhase.RollingBack, Stamp);
        intent.CaptureEvidence(new WorkloadUpdateEvidence(
            "2.6.0", Stamp, [new WorkloadEvidenceCapture("gateway", ["from-staging"], [], null)]));
        intent.AdvanceTo(WorkloadUpdatePhase.RolledBack, Stamp);
        Set(intent, nameof(WorkloadUpdateIntent.EvidenceDocument), new WorkloadUpdateEvidenceDocument
        {
            ToVersion = "2.6.0",
            CapturedAtUtc = Stamp,
            Workloads =
            [
                new EvidenceWorkloadDocument { WorkloadId = "gateway", PreviousContainerLogTail = ["FROM-DOCUMENT"] },
            ],
        });

        var response = await ResponseFor(intent);

        response!.Evidence!.Workloads[0].PreviousContainerLogTail[0].Should().Be("FROM-DOCUMENT");
    }

    private static WorkloadStringMapDocument StringMap(string value) => new()
    {
        Entries = [new WorkloadStringPairDocument { Key = "gateway", Value = value }],
    };

    private static void Set(object entity, string property, object? value)
        => entity.GetType().GetProperty(property, BindingFlags.Instance | BindingFlags.Public)!.SetValue(entity, value);

    private static WorkloadUpdateReadService Build(ScriptedPlanner planner, InMemoryIntentRepository repo)
        => new(planner, repo, new OrchestratorHarness(PlanWith(Manifest(privileged: false), Manifest(privileged: false))).Pipeline,
            NullLogger<WorkloadUpdateReadService>.Instance);

    [Fact]
    public async Task Diff_preview_names_the_point_of_no_return_ahead()
    {
        var plan = PlanWith(Manifest(privileged: false), Manifest(privileged: false));
        var svc = Build(new ScriptedPlanner(plan), new InMemoryIntentRepository());

        var r = await svc.GetDiffPreviewAsync(Instance, "2.6.0");

        r.PointOfNoReturn.Should().BeEquivalentTo(new WorkloadUpdatePointOfNoReturnDto
        {
            StageId = "stage.host.plugin-roll",
            Reason = "a certified N+1 may have forward-migrated data",
            PassedAtUtc = null,
        });
    }

    [Fact]
    public async Task An_intent_past_its_point_of_no_return_says_when_it_passed_it()
    {
        var plan = PlanWith(Manifest(privileged: false), Manifest(privileged: false));
        var h = new OrchestratorHarness(plan);
        var intent = await h.StageManualAsync(plan);
        h.HostStage.KeepWorking();
        await h.Orchestrator.WalkAsync(intent);
        var entered = h.Clock.UtcNow;
        h.Clock.Advance(TimeSpan.FromMinutes(7));
        await h.Orchestrator.WalkAsync(intent);

        var r = await new WorkloadUpdateReadService(h.Planner, h.Repo, h.Pipeline, NullLogger<WorkloadUpdateReadService>.Instance)
            .GetIntentAsync(intent.Id);

        r!.PointOfNoReturn!.StageId.Should().Be("stage.host.plugin-roll");
        r.PointOfNoReturn.PassedAtUtc.Should().Be(entered);
        r.CurrentStage.Should().Be("stage.host.plugin-roll", "the detail names the stage the walk is on, host stages included");
        r.Direction.Should().Be(nameof(WorkloadUpdateDirection.Forward));
        r.Outcome.Should().Be(nameof(WorkloadUpdateOutcome.InProgress));
    }

    [Fact]
    public async Task The_detail_and_the_status_carry_the_walk_fault_the_row_records()
    {
        var plan = PlanWith(Manifest(privileged: false), Manifest(privileged: false));
        var h = new OrchestratorHarness(plan);
        var intent = StagedIntent();
        intent.RecordWalkFault("plan unavailable: manifest store down", Stamp.AddMinutes(3));
        await h.Repo.AddAsync(intent);
        var service = new WorkloadUpdateReadService(h.Planner, h.Repo, h.Pipeline, NullLogger<WorkloadUpdateReadService>.Instance);

        var detail = await service.GetIntentAsync(intent.Id);
        var summary = (await service.GetStatusAsync(intent.GroupInstanceId)).Intents.Single();

        detail!.WalkFault.Should().Be("plan unavailable: manifest store down");
        detail.WalkFaultSinceUtc.Should().Be(Stamp.AddMinutes(3));
        summary.WalkFault.Should().Be("plan unavailable: manifest store down");
        summary.WalkFaultSinceUtc.Should().Be(Stamp.AddMinutes(3));
    }

    [Fact]
    public async Task The_current_stage_of_a_settled_row_is_its_journals_last()
    {
        var plan = PlanWith(Manifest(privileged: false), Manifest(privileged: false));
        var h = new OrchestratorHarness(plan);
        var intent = StagedIntent();
        foreach (var phase in (WorkloadUpdatePhase[])[WorkloadUpdatePhase.ApplyingSidecar, WorkloadUpdatePhase.HealthGate, WorkloadUpdatePhase.SwappingModule])
        {
            intent.AdvanceTo(phase, Stamp.AddMinutes(1));
        }

        intent.RecordStageJournal(new PipelineJournal(
            [.. WorkloadUpdateStages.Core.Select(id => new StageEntry(id, Stamp, PipelineDirection.Forward, StageEntryState.Done, null))],
            PipelineDirection.Forward,
            PipelineState.Completed));
        intent.Complete(Stamp.AddMinutes(2));
        await h.Repo.AddAsync(intent);
        var service = new WorkloadUpdateReadService(h.Planner, h.Repo, h.Pipeline, NullLogger<WorkloadUpdateReadService>.Instance);

        var detail = await service.GetIntentAsync(intent.Id);
        var status = await service.GetStatusAsync(intent.GroupInstanceId);

        detail!.CurrentStage.Should().Be(WorkloadUpdateStages.CommitParams);
        status.Intents.Single().CurrentStage.Should().Be(WorkloadUpdateStages.CommitParams);
    }

    [Fact]
    public async Task A_walk_past_a_stage_this_build_does_not_register_reads_as_past_its_point_of_no_return()
    {
        var plan = PlanWith(Manifest(privileged: false), Manifest(privileged: false));
        var h = new OrchestratorHarness(plan);
        var intent = StagedIntent();
        foreach (var phase in (WorkloadUpdatePhase[])[WorkloadUpdatePhase.ApplyingSidecar, WorkloadUpdatePhase.HealthGate, WorkloadUpdatePhase.SwappingModule])
        {
            intent.AdvanceTo(phase, Stamp.AddMinutes(1));
        }

        var retiredAt = Stamp.AddMinutes(5);
        intent.RecordStageJournal(new PipelineJournal(
            [
                .. ((string[])[WorkloadUpdateStages.Params, WorkloadUpdateStages.Sidecar, WorkloadUpdateStages.OperatorRedeploy,
                    WorkloadUpdateStages.HealthGate, WorkloadUpdateStages.CommitVersion])
                    .Select(id => new StageEntry(id, Stamp, PipelineDirection.Forward, StageEntryState.Done, null)),
                new StageEntry("stage.host.retired", retiredAt, PipelineDirection.Forward, StageEntryState.Done, null),
                new StageEntry(WorkloadUpdateStages.CommitParams, Stamp.AddMinutes(6), PipelineDirection.Forward, StageEntryState.Working, null),
            ],
            PipelineDirection.Forward,
            PipelineState.Running));
        await h.Repo.AddAsync(intent);
        var service = new WorkloadUpdateReadService(h.Planner, h.Repo, h.Pipeline, NullLogger<WorkloadUpdateReadService>.Instance);

        var r = await service.GetIntentAsync(intent.Id);

        r!.PointOfNoReturn!.StageId.Should().Be("stage.host.retired");
        r.PointOfNoReturn.PassedAtUtc.Should().Be(retiredAt);
        r.PointOfNoReturn.Reason.Should().Contain("does not register");
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, false)]
    [InlineData(false, true)]
    public async Task A_walk_rolled_back_through_a_stage_this_build_does_not_register_has_not_passed_it(bool settled, bool rollbackFailed)
    {
        var plan = PlanWith(Manifest(privileged: false), Manifest(privileged: false));
        var h = new OrchestratorHarness(plan);
        var intent = StagedIntent();
        foreach (var phase in (WorkloadUpdatePhase[])[WorkloadUpdatePhase.ApplyingSidecar, WorkloadUpdatePhase.HealthGate, WorkloadUpdatePhase.SwappingModule])
        {
            intent.AdvanceTo(phase, Stamp.AddMinutes(1));
        }

        intent.Fail("stage 'stage.host.retired' failed", Stamp.AddMinutes(2));
        string[] walked = [WorkloadUpdateStages.Params, WorkloadUpdateStages.Sidecar, WorkloadUpdateStages.OperatorRedeploy,
            WorkloadUpdateStages.HealthGate, WorkloadUpdateStages.CommitVersion, "stage.host.retired"];
        var undone = settled ? walked.Reverse().ToArray() : ["stage.host.retired"];
        intent.RecordStageJournal(new PipelineJournal(
            [
                .. walked.Select(id => new StageEntry(id, Stamp, PipelineDirection.Forward, StageEntryState.Done, null)),
                .. undone.Select(id => new StageEntry(id, Stamp.AddMinutes(3), PipelineDirection.Compensating, StageEntryState.Done, null)),
            ],
            PipelineDirection.Compensating,
            settled ? PipelineState.Compensated : rollbackFailed ? PipelineState.Failed : PipelineState.Running));
        if (settled)
        {
            intent.AdvanceTo(WorkloadUpdatePhase.RolledBack, Stamp.AddMinutes(4));
        }
        else if (rollbackFailed)
        {
            intent.MarkRollbackFailed("the rollback failed", Stamp.AddMinutes(4));
        }

        await h.Repo.AddAsync(intent);
        var service = new WorkloadUpdateReadService(h.Planner, h.Repo, h.Pipeline, NullLogger<WorkloadUpdateReadService>.Instance);

        var r = await service.GetIntentAsync(intent.Id);

        r!.PointOfNoReturn!.PassedAtUtc.Should().BeNull();
        r.PointOfNoReturn.StageId.Should().Be(ScriptedHostStage.StageId, "the pipeline's own, still ahead of a new update");
    }

    [Fact]
    public async Task A_walk_that_failed_past_its_irreversible_stage_reads_as_past_it()
    {
        var plan = PlanWith(Manifest(privileged: false), Manifest(privileged: false));
        var h = new OrchestratorHarness(plan);
        var intent = StagedIntent();
        foreach (var phase in (WorkloadUpdatePhase[])[WorkloadUpdatePhase.ApplyingSidecar, WorkloadUpdatePhase.HealthGate, WorkloadUpdatePhase.SwappingModule])
        {
            intent.AdvanceTo(phase, Stamp.AddMinutes(1));
        }

        var enteredAt = Stamp.AddMinutes(5);
        intent.RecordStageJournal(new PipelineJournal(
            [
                .. ((string[])[WorkloadUpdateStages.Params, WorkloadUpdateStages.Sidecar, WorkloadUpdateStages.OperatorRedeploy,
                    WorkloadUpdateStages.HealthGate, WorkloadUpdateStages.CommitVersion])
                    .Select(id => new StageEntry(id, Stamp, PipelineDirection.Forward, StageEntryState.Done, null)),
                new StageEntry(ScriptedHostStage.StageId, enteredAt, PipelineDirection.Forward, StageEntryState.Failed, "reload refused"),
            ],
            PipelineDirection.Forward,
            PipelineState.Failed));
        intent.MarkRollbackFailed("failed past the point of no return", Stamp.AddMinutes(6));
        await h.Repo.AddAsync(intent);
        var service = new WorkloadUpdateReadService(h.Planner, h.Repo, h.Pipeline, NullLogger<WorkloadUpdateReadService>.Instance);

        var r = await service.GetIntentAsync(intent.Id);

        r!.PointOfNoReturn!.StageId.Should().Be(ScriptedHostStage.StageId);
        r.PointOfNoReturn.PassedAtUtc.Should().Be(enteredAt);
    }

    [Fact]
    public async Task An_intent_short_of_its_point_of_no_return_names_it_as_ahead()
    {
        var plan = PlanWith(Manifest(privileged: false), Manifest(privileged: false));
        var h = new OrchestratorHarness(plan);
        var intent = await h.StageManualAsync(plan);
        h.Gate.DefaultVerdict = WorkloadHealthGateVerdict.Pending;
        await h.Orchestrator.WalkAsync(intent);

        var r = await new WorkloadUpdateReadService(h.Planner, h.Repo, h.Pipeline, NullLogger<WorkloadUpdateReadService>.Instance)
            .GetIntentAsync(intent.Id);

        r!.PointOfNoReturn!.StageId.Should().Be("stage.host.plugin-roll");
        r.PointOfNoReturn.PassedAtUtc.Should().BeNull();
    }

    [Fact]
    public async Task Diff_preview_surfaces_a_security_escalation()
    {
        var plan = PlanWith(Manifest(privileged: false), Manifest(privileged: true));
        var svc = Build(new ScriptedPlanner(plan), new InMemoryIntentRepository());

        var r = await svc.GetDiffPreviewAsync(Instance, "2.6.0");

        r.Ok.Should().BeTrue();
        r.AnySecurityEscalation.Should().BeTrue();
        var gw = r.Workloads.Should().ContainSingle(w => w.WorkloadId == "gateway").Subject;
        gw.SecurityChange.Should().Be(RequirementChangeKind.Escalation.ToString());
        gw.Escalations.Should().NotBeEmpty();
    }

    [Fact]
    public async Task Diff_preview_compares_the_requirements_the_plan_carries()
    {
        var manifest = Manifest(privileged: false);
        var plan = Plan(
            Instance,
            [Item("gateway", Instance, "sha-new", "sha-old")],
            previous: manifest,
            candidate: manifest,
            prevRequirements: RequirementsFor(Manifest(privileged: true)),
            candRequirements: RequirementsFor(manifest));
        var svc = Build(new ScriptedPlanner(plan), new InMemoryIntentRepository());

        var r = await svc.GetDiffPreviewAsync(Instance, "2.6.0");

        r.AnySecurityEscalation.Should().BeFalse();
        var gw = r.Workloads.Should().ContainSingle().Subject;
        gw.SecurityChange.Should().Be(RequirementChangeKind.Narrowing.ToString());
        gw.Narrowings.Should().NotBeEmpty();
    }

    [Fact]
    public async Task Diff_preview_of_a_clean_update_is_ok_without_escalation()
    {
        var plan = PlanWith(Manifest(privileged: false), Manifest(privileged: false));
        var svc = Build(new ScriptedPlanner(plan), new InMemoryIntentRepository());

        var r = await svc.GetDiffPreviewAsync(Instance, "2.6.0");

        r.Ok.Should().BeTrue();
        r.AnySecurityEscalation.Should().BeFalse();
        r.Workloads.Should().ContainSingle().Which.SecurityChange.Should().Be(RequirementChangeKind.Unchanged.ToString());
    }

    private static WorkloadExtensions Extensions(string json)
    {
        using var document = System.Text.Json.JsonDocument.Parse(json);
        return new WorkloadExtensions
        {
            ByNamespace = document.RootElement.EnumerateObject()
                .ToDictionary(p => p.Name, p => p.Value.Clone(), StringComparer.Ordinal),
        };
    }

    private static DeploymentManifest Declaring(DeploymentManifest manifest, WorkloadExtensions extensions)
        => manifest with { Workloads = [.. manifest.Workloads.Select(w => w with { Extensions = extensions })] };

    [Fact]
    public async Task Diff_preview_carries_each_sides_extensions_verbatim()
    {
        var previous = Declaring(Manifest(false), Extensions("""{"acme":{"tier":"gold"}}"""));
        var candidate = Declaring(Manifest(false), Extensions("""{"acme":{"tier":"platinum"},"other":{"k":1}}"""));
        var svc = Build(new ScriptedPlanner(PlanWith(previous, candidate)), new InMemoryIntentRepository());

        var r = await svc.GetDiffPreviewAsync(Instance, "2.6.0");

        var gw = r.Workloads.Should().ContainSingle().Subject;
        gw.FromExtensions!.For("acme")!.Value.GetProperty("tier").GetString().Should().Be("gold");
        gw.ToExtensions.For("acme")!.Value.GetProperty("tier").GetString().Should().Be("platinum");
        gw.ToExtensions.For("other").Should().NotBeNull();
        gw.PreviousUnreadable.Should().BeFalse();
    }

    [Fact]
    public async Task Diff_preview_of_an_added_workload_has_no_from_extensions()
    {
        var previous = new DeploymentManifest { SchemaVersion = 1, Workloads = [] };
        var candidate = Declaring(Manifest(false), Extensions("""{"acme":{"tier":"gold"}}"""));
        var svc = Build(new ScriptedPlanner(PlanWith(previous, candidate)), new InMemoryIntentRepository());

        var r = await svc.GetDiffPreviewAsync(Instance, "2.6.0");

        var gw = r.Workloads.Should().ContainSingle().Subject;
        gw.IsAdded.Should().BeTrue();
        gw.FromExtensions.Should().BeNull();
        gw.ToExtensions.For("acme").Should().NotBeNull();
        gw.PreviousUnreadable.Should().BeFalse();
    }

    [Fact]
    public async Task Diff_preview_of_a_refused_previous_manifest_says_the_previous_side_is_unreadable()
    {
        var candidate = Declaring(Manifest(false), Extensions("""{"acme":{"tier":"gold"}}"""));
        var plan = PlanWith(Declaring(candidate, WorkloadExtensions.None), candidate) with { PreviousUnreadable = true };
        var svc = Build(new ScriptedPlanner(plan), new InMemoryIntentRepository());

        var r = await svc.GetDiffPreviewAsync(Instance, "2.6.0");

        r.Ok.Should().BeTrue();
        var gw = r.Workloads.Should().ContainSingle().Subject;
        gw.PreviousUnreadable.Should().BeTrue();
        gw.IsAdded.Should().BeFalse();
        gw.FromExtensions.Should().NotBeNull().And.Subject.As<WorkloadExtensions>().ByNamespace.Should().BeEmpty();
        gw.ToExtensions.For("acme").Should().NotBeNull();
    }

    [Fact]
    public async Task Diff_preview_serializes_extensions_as_the_bare_namespace_map()
    {
        var previous = Declaring(Manifest(false), Extensions("""{"acme":{"tier":"gold"}}"""));
        var candidate = Declaring(Manifest(false), Extensions("""{"acme":{"tier":"platinum"}}"""));
        var svc = Build(new ScriptedPlanner(PlanWith(previous, candidate)), new InMemoryIntentRepository());

        var r = await svc.GetDiffPreviewAsync(Instance, "2.6.0");
        var json = System.Text.Json.JsonSerializer.Serialize(r);

        using var doc = System.Text.Json.JsonDocument.Parse(json);
        var workload = doc.RootElement.GetProperty("workloads")[0];

        workload.GetProperty("fromExtensions").GetProperty("acme").GetProperty("tier").GetString().Should().Be("gold");
        workload.GetProperty("toExtensions").GetProperty("acme").GetProperty("tier").GetString().Should().Be("platinum");
        workload.GetProperty("previousUnreadable").GetBoolean().Should().BeFalse();

        foreach (var removed in new[]
        {
            "fromProtocolVersion", "toProtocolVersion", "protocolFloorRaised", "protocolUnparseable",
            "fromApiSchema", "toApiSchema", "apiSchemaChanged", "apiSchemaUnparseable",
        })
        {
            workload.TryGetProperty(removed, out _).Should().BeFalse(because: $"{removed} left the wire with the reeve extension");
        }

        foreach (var removed in new[]
        {
            "anyProtocolFloorRaised", "anyProtocolUnparseable", "anyApiSchemaChanged", "anyApiSchemaUnparseable",
        })
        {
            doc.RootElement.TryGetProperty(removed, out _).Should().BeFalse(because: $"{removed} left the wire with the reeve extension");
        }
    }

    [Fact]
    public async Task Diff_preview_of_an_added_workload_serializes_from_extensions_as_null_not_an_empty_object()
    {
        var previous = new DeploymentManifest { SchemaVersion = 1, Workloads = [] };
        var candidate = Declaring(Manifest(false), Extensions("""{"acme":{"tier":"gold"}}"""));
        var svc = Build(new ScriptedPlanner(PlanWith(previous, candidate)), new InMemoryIntentRepository());

        var r = await svc.GetDiffPreviewAsync(Instance, "2.6.0");
        var json = System.Text.Json.JsonSerializer.Serialize(r);

        using var doc = System.Text.Json.JsonDocument.Parse(json);
        var workload = doc.RootElement.GetProperty("workloads")[0];

        workload.GetProperty("fromExtensions").ValueKind.Should().Be(System.Text.Json.JsonValueKind.Null);
    }

    [Fact]
    public async Task Diff_preview_stages_no_intent()
    {
        var plan = PlanWith(Manifest(false), Manifest(false));
        var repo = new InMemoryIntentRepository();
        var svc = Build(new ScriptedPlanner(plan), repo);

        await svc.GetDiffPreviewAsync(Instance, "2.6.0");

        repo.All.Should().BeEmpty("the preview is read-only — it never stages an intent");
    }

    [Fact]
    public async Task Diff_preview_of_an_unresolvable_candidate_is_blocked_not_a_false_empty_diff()
    {
        var planner = new ScriptedPlanner(PlanWith(Manifest(false), Manifest(false)))
        {
            Override = () => throw new InvalidOperationException(
                "Candidate manifest not found on disk at /var/lib/reeve/plugins/zwave/2.9.0/reeve-plugin.json."),
        };
        var svc = Build(planner, new InMemoryIntentRepository());

        var r = await svc.GetDiffPreviewAsync(Instance, "2.9.0");

        r.Ok.Should().BeFalse("a resolution fault must fail closed, never masquerade as an all-clear");
        r.Error.Should().NotBeNullOrWhiteSpace();
        r.Error.Should().NotContain("/var/lib/reeve", "the blocked reason must not echo the raw filesystem path from ex.Message");
        r.Error.Should().NotContain("reeve-plugin.json");
        r.Workloads.Should().BeEmpty();
        r.AnySecurityEscalation.Should().BeFalse();
        r.GroupInstanceId.Should().Be(Instance);
        r.ToVersion.Should().Be("2.9.0");
    }

    [Fact]
    public async Task Diff_preview_the_host_refused_is_blocked_with_the_hosts_reason()
    {
        var planner = new ScriptedPlanner(PlanWith(Manifest(false), Manifest(false)))
        {
            Override = () => throw new WorkloadHostRefusedException("the broker is not provisioned for this workload"),
        };
        var svc = Build(planner, new InMemoryIntentRepository());

        var r = await svc.GetDiffPreviewAsync(Instance, "2.9.0");

        r.Ok.Should().BeFalse();
        r.Error.Should().Contain("the broker is not provisioned for this workload");
        r.Workloads.Should().BeEmpty();
        r.GroupInstanceId.Should().Be(Instance);
        r.ToVersion.Should().Be("2.9.0");
    }

    [Fact]
    public async Task Status_lists_active_first_then_terminal_newest_first_with_step_counts()
    {
        var repo = new InMemoryIntentRepository();

        var completed = Completed(Instance, to: "2.4.0", created: T0);
        var rolledBack = RolledBack(Instance, to: "2.5.0", created: T0.AddMinutes(1));
        var active = Active(Instance, to: "2.6.0", created: T0.AddMinutes(2));
        await repo.AddAsync(completed);
        await repo.UpdateAsync(rolledBack);
        await repo.UpdateAsync(active);

        var svc = Build(new ScriptedPlanner(PlanWith(Manifest(false), Manifest(false))), repo);
        var r = await svc.GetStatusAsync(Instance);

        r.ActiveIntentId.Should().Be(active.Id, "the single non-terminal intent is the active one");
        r.Intents.First().Id.Should().Be(active.Id, "the active intent is listed first");
        r.Intents.Select(i => i.Id).Should().Contain(new[] { completed.Id, rolledBack.Id });

        var activeRow = r.Intents.Single(i => i.Id == active.Id);
        activeRow.IsTerminal.Should().BeFalse();
        activeRow.Outcome.Should().Be(nameof(WorkloadUpdateOutcome.InProgress));
        activeRow.Direction.Should().Be(nameof(WorkloadUpdateDirection.Forward));
        activeRow.CurrentStage.Should().BeNull("a row the walk has not ticked has called no stage");
        var rolledBackRow = r.Intents.Single(i => i.Id == rolledBack.Id);
        rolledBackRow.Outcome.Should().Be(nameof(WorkloadUpdateOutcome.RolledBack));
        rolledBackRow.CompletedStepCount.Should().Be(1, "a workload put back to its previous spec has settled too");
        activeRow.StepCount.Should().Be(1);
        activeRow.CompletedStepCount.Should().Be(0, "the active intent's single step has not reached a terminal state");

        r.Intents.Single(i => i.Id == completed.Id).IsTerminal.Should().BeTrue();
    }

    [Fact]
    public async Task Intent_detail_of_a_terminal_rollback_failed_parses_the_evidence()
    {
        var repo = new InMemoryIntentRepository();
        var intent = RollbackFailed(Instance, to: "2.6.0", created: T0);
        await repo.UpdateAsync(intent);

        var svc = Build(new ScriptedPlanner(PlanWith(Manifest(false), Manifest(false))), repo);
        var r = await svc.GetIntentAsync(intent.Id);

        r.Should().NotBeNull();
        r!.Phase.Should().Be(WorkloadUpdatePhase.RollbackFailed.ToString());
        r.Outcome.Should().Be(nameof(WorkloadUpdateOutcome.RollbackFailed));
        r.Direction.Should().Be(nameof(WorkloadUpdateDirection.RollingBack));
        r.IsTerminal.Should().BeTrue();
        r.Evidence.Should().NotBeNull("a terminal rolled-back intent that captured evidence exposes it parsed");
        r.Evidence!.ToVersion.Should().Be("2.6.0");
        var wl = r.Evidence.Workloads.Should().ContainSingle(w => w.WorkloadId == "gateway").Subject;
        wl.PreviousContainerLogTail.Should().Contain("panic: boom");
        r.Steps.Should().ContainSingle(s => s.WorkloadId == "gateway")
            .Which.Progress.Should().Be(nameof(WorkloadUpdateStepProgress.Failed));
    }

    [Fact]
    public async Task Intent_detail_of_an_active_intent_has_no_evidence()
    {
        var repo = new InMemoryIntentRepository();
        var intent = Active(Instance, to: "2.6.0", created: T0);
        await repo.UpdateAsync(intent);

        var svc = Build(new ScriptedPlanner(PlanWith(Manifest(false), Manifest(false))), repo);
        var r = await svc.GetIntentAsync(intent.Id);

        r.Should().NotBeNull();
        r!.IsTerminal.Should().BeFalse();
        r.Evidence.Should().BeNull("an in-flight intent has no rollback evidence yet");
    }

    [Fact]
    public async Task Intent_detail_returns_null_for_an_unknown_id()
    {
        var svc = Build(new ScriptedPlanner(PlanWith(Manifest(false), Manifest(false))), new InMemoryIntentRepository());
        (await svc.GetIntentAsync(Guid.NewGuid())).Should().BeNull();
    }

    private static WorkloadUpdateIntent WithStep(WorkloadUpdateIntent intent)
    {
        intent.AddStep(WorkloadUpdateIntentStep.Create(Guid.NewGuid(), intent.Id, "gateway", intent.CreatedAtUtc));
        return intent;
    }

    private static WorkloadUpdateIntent Active(Guid instance, string to, DateTimeOffset created)
        => WithStep(WorkloadUpdateIntent.Create(Guid.NewGuid(), instance, "2.5.0", to, created));

    private static WorkloadUpdateIntent Completed(Guid instance, string to, DateTimeOffset created)
    {
        var i = WithStep(WorkloadUpdateIntent.Create(Guid.NewGuid(), instance, "2.5.0", to, created));
        i.AdvanceTo(WorkloadUpdatePhase.ApplyingSidecar, created.AddSeconds(1));
        i.AdvanceTo(WorkloadUpdatePhase.HealthGate, created.AddSeconds(2));
        i.AdvanceTo(WorkloadUpdatePhase.SwappingModule, created.AddSeconds(3));
        i.Complete(created.AddSeconds(4));
        i.Steps.Single().Advance(WorkloadUpdatePhase.Completed, WorkloadUpdateStepState.Done, "ok", created.AddSeconds(4));
        return i;
    }

    private static WorkloadUpdateIntent RolledBack(Guid instance, string to, DateTimeOffset created)
    {
        var i = WithStep(WorkloadUpdateIntent.Create(Guid.NewGuid(), instance, "2.5.0", to, created));
        i.AdvanceTo(WorkloadUpdatePhase.ApplyingSidecar, created.AddSeconds(1));
        i.Fail("apply failed", created.AddSeconds(2));
        i.Steps.Single().Advance(WorkloadUpdatePhase.RolledBack, WorkloadUpdateStepState.RolledBack, "restored", created.AddSeconds(3));
        i.AdvanceTo(WorkloadUpdatePhase.RolledBack, created.AddSeconds(3));
        return i;
    }

    private static WorkloadUpdateIntent RollbackFailed(Guid instance, string to, DateTimeOffset created)
    {
        var i = WithStep(WorkloadUpdateIntent.Create(Guid.NewGuid(), instance, "2.5.0", to, created));
        i.AdvanceTo(WorkloadUpdatePhase.ApplyingSidecar, created.AddSeconds(1));
        i.Fail("apply failed", created.AddSeconds(2));
        var evidence = new WorkloadUpdateEvidence(
            to,
            created.AddSeconds(2),
            [new WorkloadEvidenceCapture("gateway", ["panic: boom"], ["Killing container"], null)]);
        i.CaptureEvidence(evidence);
        i.MarkRollbackFailed("old digest unpullable", created.AddSeconds(3));
        i.Steps.Single().Advance(WorkloadUpdatePhase.RollbackFailed, WorkloadUpdateStepState.Failed, "boom", created.AddSeconds(3));
        return i;
    }
}
