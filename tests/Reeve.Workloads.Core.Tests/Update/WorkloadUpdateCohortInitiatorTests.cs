using Microsoft.AspNetCore.DataProtection;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Reeve.Workloads.Abstractions;
using Reeve.Workloads.Abstractions.Contracts;
using Reeve.Workloads.Abstractions.Domain.Entities;
using Reeve.Workloads.Abstractions.Domain.Network;
using Reeve.Workloads.Abstractions.Update;
using Reeve.Workloads.Core.Tests.Groups;
using Reeve.Workloads.Core.Update;
using Xunit;

namespace Reeve.Workloads.Core.Tests.Update;

public sealed class WorkloadUpdateCohortInitiatorTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid Instance = Guid.Parse("aaaaaaaa-1111-1111-1111-111111111111");

    [Fact]
    public async Task Unsupported_store_refuses_before_pinning_or_planning()
    {
        var plan = WorkloadUpdatePlanBuilder.Plan(Instance,
            [WorkloadUpdatePlanBuilder.Item("gateway", Instance, "sha-new", "sha-old")]);
        var harness = new OrchestratorHarness(plan);
        var instances = new FakeInstances();
        instances.Instances.Add(WorkloadInstance.Create(Guid.NewGuid(), Instance, "zwave", "gateway", Now));
        var row = instances.Instances.Single();
        var initiator = Build(harness, instances, new FakeGroups("zwave", "2.5.0"));

        var result = await initiator.InitiateCohortAsync(Request(plan, Pin("gateway")));

        result.Outcome.Should().Be(WorkloadUpdateInitiationOutcome.Refused);
        instances.Updated.Should().BeEmpty();
        harness.Planner.BuildCount.Should().Be(0);
        row.ReadAttachmentPin().Verdict.Should().Be(
            Reeve.Workloads.Abstractions.Domain.Entities.Documents.WorkloadDocumentVerdict.Absent);
    }

    [Fact]
    public async Task Cohort_stages_members_without_targets_and_passes_shared_origin()
    {
        var plan = WorkloadUpdatePlanBuilder.Plan(Instance,
            [WorkloadUpdatePlanBuilder.Item("gateway", Instance, "sha-new", "sha-old")]);
        var harness = new OrchestratorHarness(plan);
        harness.Repo.SupportsCohorts = true;
        var instances = new FakeInstances();
        instances.Instances.Add(WorkloadInstance.Create(Guid.NewGuid(), Instance, "zwave", "gateway", Now));
        var initiator = Build(harness, instances, new FakeGroups("zwave", "2.5.0"));

        var result = await initiator.InitiateCohortAsync(Request(plan));

        result.Outcome.Should().Be(WorkloadUpdateInitiationOutcome.Started);
        result.Members.Should().ContainSingle();
        var intent = harness.Repo.All.Single();
        intent.CohortId.Should().Be(result.CohortId);
        intent.CohortOrdinal.Should().Be(0);
        intent.CohortSize.Should().Be(1);
        intent.ReadTargetSpecs().Verdict.Should().Be(Reeve.Workloads.Abstractions.Domain.Entities.Documents.WorkloadDocumentVerdict.Absent);
        harness.Planner.LastOrigin.Should().Be(new WorkloadUpdatePlanOrigin("2.5.0"));
    }

    [Fact]
    public async Task Incomplete_coverage_refuses_before_any_pin_is_written()
    {
        var plan = WorkloadUpdatePlanBuilder.Plan(Instance,
            [WorkloadUpdatePlanBuilder.Item("gateway", Instance, "sha-new", "sha-old")]);
        var harness = new OrchestratorHarness(plan);
        harness.Repo.SupportsCohorts = true;
        var instances = new FakeInstances();
        instances.Instances.Add(WorkloadInstance.Create(Guid.NewGuid(), Instance, "zwave", "gateway", Now));
        instances.Instances.Add(WorkloadInstance.Create(Guid.NewGuid(), Guid.Parse("bbbbbbbb-2222-2222-2222-222222222222"), "zwave", "gateway", Now));
        var initiator = Build(harness, instances, new FakeGroups("zwave", "2.5.0"));

        var result = await initiator.InitiateCohortAsync(Request(plan, Pin("gateway")));

        result.Outcome.Should().Be(WorkloadUpdateInitiationOutcome.Refused);
        instances.Updated.Should().BeEmpty();
        harness.Repo.All.Should().BeEmpty();
    }

    [Fact]
    public async Task Empty_cohort_shape_refuses_without_writes_or_pins()
    {
        var plan = Plan();
        var harness = new OrchestratorHarness(plan);
        harness.Repo.SupportsCohorts = true;
        var instances = Instances(Instance);
        var initiator = Build(harness, instances, new FakeGroups("zwave", "2.5.0"));

        var result = await initiator.InitiateCohortAsync(Request(plan) with { Members = [] });

        result.Outcome.Should().Be(WorkloadUpdateInitiationOutcome.Refused);
        AssertNothingWritten(harness, instances);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Blank_candidate_refuses_without_writes_or_pins(string candidate)
    {
        var plan = Plan();
        var harness = new OrchestratorHarness(plan);
        harness.Repo.SupportsCohorts = true;
        var instances = Instances(Instance);
        var initiator = Build(harness, instances, new FakeGroups("zwave", "2.5.0"));

        var result = await initiator.InitiateCohortAsync(Request(plan, Pin("gateway")) with { CandidateVersion = candidate });

        result.Outcome.Should().Be(WorkloadUpdateInitiationOutcome.Refused);
        AssertNothingWritten(harness, instances);
    }

    [Fact]
    public async Task Duplicate_members_refuse_without_writes_or_pins()
    {
        var plan = Plan();
        var harness = new OrchestratorHarness(plan);
        harness.Repo.SupportsCohorts = true;
        var instances = Instances(Instance);
        var initiator = Build(harness, instances, new FakeGroups("zwave", "2.5.0"));

        var result = await initiator.InitiateCohortAsync(Request(plan, Pin("gateway")) with
        {
            Members = [Member(Instance, plan.RequirementsFingerprint, Pin("gateway")), Member(Instance, plan.RequirementsFingerprint)],
        });

        result.Outcome.Should().Be(WorkloadUpdateInitiationOutcome.Refused);
        AssertNothingWritten(harness, instances);
    }

    [Fact]
    public async Task Blank_member_fingerprint_refuses_without_writes_or_pins()
    {
        var plan = Plan();
        var harness = new OrchestratorHarness(plan);
        harness.Repo.SupportsCohorts = true;
        var instances = Instances(Instance);
        var initiator = Build(harness, instances, new FakeGroups("zwave", "2.5.0"));

        var result = await initiator.InitiateCohortAsync(Request(plan, Pin("gateway")) with
        {
            Members = [Member(Instance, "  ", Pin("gateway"))],
        });

        result.Outcome.Should().Be(WorkloadUpdateInitiationOutcome.Refused);
        AssertNothingWritten(harness, instances);
    }

    [Fact]
    public async Task A_member_with_a_host_held_workload_refuses_the_cohort_before_pinning()
    {
        var plan = Plan();
        var harness = new OrchestratorHarness(plan);
        harness.Repo.SupportsCohorts = true;
        var instances = Instances(Instance);
        instances.Instances.Single().TransitionTo(
            WorkloadInstanceState.Suspended, WorkloadStateReason.SuspendedByHost, "withdrawn", Now);
        var initiator = Build(harness, instances, new FakeGroups("zwave", "2.5.0"));

        var result = await initiator.InitiateCohortAsync(Request(plan, Pin("gateway")));

        result.Outcome.Should().Be(WorkloadUpdateInitiationOutcome.Refused);
        result.RefusedGroupInstanceId.Should().Be(Instance);
        AssertNothingWritten(harness, instances);
    }

    [Fact]
    public async Task Missing_group_refuses_before_pinning()
    {
        var plan = Plan();
        var harness = new OrchestratorHarness(plan);
        harness.Repo.SupportsCohorts = true;
        var instances = Instances(Instance);
        var initiator = Build(harness, instances, new FakeGroups("some-other-group", "2.5.0"));

        var result = await initiator.InitiateCohortAsync(Request(plan, Pin("gateway")));

        result.Outcome.Should().Be(WorkloadUpdateInitiationOutcome.Refused);
        AssertNothingWritten(harness, instances);
    }

    [Fact]
    public async Task Extra_requested_member_refuses_without_writes_or_pins()
    {
        var plan = Plan();
        var harness = new OrchestratorHarness(plan);
        harness.Repo.SupportsCohorts = true;
        var second = Guid.Parse("bbbbbbbb-2222-2222-2222-222222222222");
        var instances = Instances(Instance);
        var initiator = Build(harness, instances, new FakeGroups("zwave", "2.5.0"));
        var request = Request(plan, Pin("gateway")) with
        {
            Members = [Member(Instance, plan.RequirementsFingerprint, Pin("gateway")), Member(second, plan.RequirementsFingerprint)],
        };

        var result = await initiator.InitiateCohortAsync(request);

        result.Outcome.Should().Be(WorkloadUpdateInitiationOutcome.Refused);
        result.RefusedGroupInstanceId.Should().Be(second);
        instances.Updated.Should().BeEmpty();
        instances.Instances.Single().ReadAttachmentPin().Verdict.Should().Be(
            Reeve.Workloads.Abstractions.Domain.Entities.Documents.WorkloadDocumentVerdict.Absent);
        harness.Repo.All.Should().BeEmpty();
    }

    [Fact]
    public async Task Null_group_from_refuses_without_writes_or_pins()
    {
        var plan = Plan();
        var harness = new OrchestratorHarness(plan);
        harness.Repo.SupportsCohorts = true;
        var instances = Instances(Instance);
        var initiator = Build(harness, instances, new FakeGroups("zwave", null));

        var result = await initiator.InitiateCohortAsync(Request(plan, Pin("gateway")));

        result.Outcome.Should().Be(WorkloadUpdateInitiationOutcome.Refused);
        AssertNothingWritten(harness, instances);
    }

    [Fact]
    public async Task Equal_from_and_to_refuses_without_writes_or_pins()
    {
        var plan = Plan();
        var harness = new OrchestratorHarness(plan);
        harness.Repo.SupportsCohorts = true;
        var instances = Instances(Instance);
        var initiator = Build(harness, instances, new FakeGroups("zwave", plan.ToVersion));

        var result = await initiator.InitiateCohortAsync(Request(plan, Pin("gateway")));

        result.Outcome.Should().Be(WorkloadUpdateInitiationOutcome.Refused);
        AssertNothingWritten(harness, instances);
    }

    [Fact]
    public async Task Fingerprint_mismatch_refuses_without_writes_or_pins()
    {
        var plan = Plan();
        var harness = new OrchestratorHarness(plan);
        harness.Repo.SupportsCohorts = true;
        var instances = Instances(Instance);
        var initiator = Build(harness, instances, new FakeGroups("zwave", "2.5.0"));
        var request = Request(plan, Pin("gateway")) with
        {
            Members = [Member(Instance, "stale-fingerprint", Pin("gateway"))],
        };

        var result = await initiator.InitiateCohortAsync(request);

        result.Outcome.Should().Be(WorkloadUpdateInitiationOutcome.PreconditionFailed);
        AssertNothingWritten(harness, instances);
    }

    [Fact]
    public async Task Pin_for_a_missing_workload_row_refuses_without_writes()
    {
        var plan = Plan();
        var harness = new OrchestratorHarness(plan);
        harness.Repo.SupportsCohorts = true;
        var instances = Instances(Instance);
        var initiator = Build(harness, instances, new FakeGroups("zwave", "2.5.0"));

        var result = await initiator.InitiateCohortAsync(Request(plan, Pin("missing")));

        result.Outcome.Should().Be(WorkloadUpdateInitiationOutcome.PreconditionFailed);
        instances.Updated.Should().BeEmpty();
        instances.Instances.Single().ReadAttachmentPin().Verdict.Should().Be(
            Reeve.Workloads.Abstractions.Domain.Entities.Documents.WorkloadDocumentVerdict.Absent);
        harness.Repo.All.Should().BeEmpty();
    }

    [Fact]
    public async Task One_unplannable_member_refuses_before_any_member_pin_is_written()
    {
        var plan = Plan();
        var harness = new OrchestratorHarness(plan);
        harness.Repo.SupportsCohorts = true;
        var second = Guid.Parse("bbbbbbbb-2222-2222-2222-222222222222");
        var instances = Instances(Instance, second);
        var builds = 0;
        harness.Planner.Override = () =>
        {
            if (++builds == 2) throw new InvalidOperationException("second member cannot be planned");
            return plan;
        };
        var initiator = Build(harness, instances, new FakeGroups("zwave", "2.5.0"));
        var request = Request(plan) with
        {
            Members = [Member(Instance, plan.RequirementsFingerprint, Pin("gateway")), Member(second, plan.RequirementsFingerprint, Pin("gateway"))],
        };

        var result = await initiator.InitiateCohortAsync(request);

        result.Outcome.Should().Be(WorkloadUpdateInitiationOutcome.Refused);
        result.RefusedGroupInstanceId.Should().Be(second);
        instances.Updated.Should().BeEmpty();
        instances.Instances.Should().OnlyContain(row => row.ReadAttachmentPin().Verdict ==
            Reeve.Workloads.Abstractions.Domain.Entities.Documents.WorkloadDocumentVerdict.Absent);
        harness.Repo.All.Should().BeEmpty();
    }

    [Fact]
    public async Task Single_intent_refuses_while_group_has_an_open_cohort_member()
    {
        var plan = WorkloadUpdatePlanBuilder.Plan(Instance,
            [WorkloadUpdatePlanBuilder.Item("gateway", Instance, "sha-new", "sha-old")]);
        var harness = new OrchestratorHarness(plan);
        harness.Repo.SupportsCohorts = true;
        var instances = new FakeInstances();
        instances.Instances.Add(WorkloadInstance.Create(Guid.NewGuid(), Instance, "zwave", "gateway", Now));
        var initiator = Build(harness, instances, new FakeGroups("zwave", "2.5.0"));
        (await initiator.InitiateCohortAsync(Request(plan))).Outcome.Should().Be(WorkloadUpdateInitiationOutcome.Started);

        var single = await initiator.InitiateAsync(new WorkloadUpdateInitiationRequest
        {
            PluginInstanceId = Instance,
            CandidateVersion = plan.ToVersion,
            Precondition = new WorkloadWritePrecondition { RequirementsFingerprint = plan.RequirementsFingerprint },
        });

        single.Outcome.Should().Be(WorkloadUpdateInitiationOutcome.Refused);
        harness.Repo.All.Should().ContainSingle();
    }

    [Fact]
    public async Task Single_intent_refusal_does_not_need_the_group_repository()
    {
        var plan = Plan();
        var harness = new OrchestratorHarness(plan);
        var sibling = Guid.Parse("bbbbbbbb-2222-2222-2222-222222222222");
        var instances = Instances(Instance, sibling);
        var other = WorkloadUpdateIntent.Create(Guid.NewGuid(), sibling, "2.5.0", "2.6.0", Now,
            new WorkloadUpdateCohortPlace(Guid.NewGuid(), 1, 2));
        await harness.Repo.AddAsync(other);
        var withoutGroups = new WorkloadUpdateInitiator(harness.Repo, harness.Planner, instances, new RecordingNudge(),
            harness.Pipeline, harness.Clock, NullLogger<WorkloadUpdateInitiator>.Instance);

        var single = await withoutGroups.InitiateAsync(new WorkloadUpdateInitiationRequest
        {
            PluginInstanceId = Instance,
            CandidateVersion = plan.ToVersion,
            Precondition = new WorkloadWritePrecondition { RequirementsFingerprint = plan.RequirementsFingerprint },
        });

        single.Outcome.Should().Be(WorkloadUpdateInitiationOutcome.Refused,
            "a group with a cohort under way takes no single update, whichever constructor built the initiator");
        harness.Planner.BuildCount.Should().Be(0);
        harness.Repo.All.Should().ContainSingle();
    }

    [Fact]
    public async Task Matching_resubmit_is_idempotently_already_in_progress()
    {
        var plan = Plan();
        var harness = new OrchestratorHarness(plan);
        harness.Repo.SupportsCohorts = true;
        var instances = Instances(Instance);
        var initiator = Build(harness, instances, new FakeGroups("zwave", "2.5.0"));
        var request = Request(plan);
        var first = await initiator.InitiateCohortAsync(request);

        var second = await initiator.InitiateCohortAsync(request);

        second.Outcome.Should().Be(WorkloadUpdateInitiationOutcome.AlreadyInProgress);
        second.CohortId.Should().Be(first.CohortId);
        second.Members.Should().BeEquivalentTo(first.Members);
        harness.Repo.All.Should().ContainSingle();
    }

    [Fact]
    public async Task Unique_race_returns_already_in_progress_and_keeps_written_pins()
    {
        var plan = Plan();
        var harness = new OrchestratorHarness(plan);
        harness.Repo.SupportsCohorts = true;
        var instances = Instances(Instance);
        var row = instances.Instances.Single();
        harness.Repo.BeforeAddCohort = _ =>
        {
            var winner = WorkloadUpdateIntent.Create(Guid.NewGuid(), Instance, "2.5.0", "2.6.0", Now);
            harness.Repo.AddAsync(winner).GetAwaiter().GetResult();
        };
        var initiator = Build(harness, instances, new FakeGroups("zwave", "2.5.0"));

        var result = await initiator.InitiateCohortAsync(Request(plan, Pin("gateway")));

        result.Outcome.Should().Be(WorkloadUpdateInitiationOutcome.AlreadyInProgress);
        result.Members.Should().ContainSingle().Which.IntentId.Should().NotBeEmpty();
        row.ReadAttachmentPin().Value.Should().BeEquivalentTo(Pin("gateway")["gateway"]);
        instances.Updated.Should().ContainSingle();
        harness.Repo.All.Should().ContainSingle();
    }

    [Fact]
    public async Task Every_member_plan_receives_the_shared_group_from_version()
    {
        var plan = Plan();
        var harness = new OrchestratorHarness(plan);
        harness.Repo.SupportsCohorts = true;
        var second = Guid.Parse("bbbbbbbb-2222-2222-2222-222222222222");
        var instances = Instances(Instance, second);
        var initiator = Build(harness, instances, new FakeGroups("zwave", "2.5.0"));
        var request = Request(plan) with
        {
            Members = [Member(Instance, plan.RequirementsFingerprint), Member(second, plan.RequirementsFingerprint)],
        };

        var result = await initiator.InitiateCohortAsync(request);

        result.Outcome.Should().Be(WorkloadUpdateInitiationOutcome.Started);
        harness.Planner.Origins.Should().HaveCount(2).And.OnlyContain(origin => origin == new WorkloadUpdatePlanOrigin("2.5.0"));
        harness.Repo.All.Should().HaveCount(2).And.OnlyContain(intent => intent.ReadTargetSpecs().Verdict ==
            Reeve.Workloads.Abstractions.Domain.Entities.Documents.WorkloadDocumentVerdict.Absent);
    }

    private static WorkloadUpdateCohortInitiationRequest Request(
        WorkloadUpdatePlan plan, IReadOnlyDictionary<string, WorkloadAttachmentPin>? pins = null)
        => new()
        {
            CandidateVersion = plan.ToVersion,
            Members =
            [
                new WorkloadUpdateCohortMember
                {
                    GroupInstanceId = Instance,
                    Precondition = new WorkloadWritePrecondition
                    {
                        RequirementsFingerprint = plan.RequirementsFingerprint,
                        AttachmentPins = pins ?? new Dictionary<string, WorkloadAttachmentPin>(),
                    },
                },
            ],
        };

    private static WorkloadUpdateCohortMember Member(
        Guid groupInstanceId, string fingerprint, IReadOnlyDictionary<string, WorkloadAttachmentPin>? pins = null)
        => new()
        {
            GroupInstanceId = groupInstanceId,
            Precondition = new WorkloadWritePrecondition
            {
                RequirementsFingerprint = fingerprint,
                AttachmentPins = pins ?? new Dictionary<string, WorkloadAttachmentPin>(),
            },
        };

    private static WorkloadUpdatePlan Plan()
        => WorkloadUpdatePlanBuilder.Plan(Instance,
            [WorkloadUpdatePlanBuilder.Item("gateway", Instance, "sha-new", "sha-old")]);

    private static FakeInstances Instances(params Guid[] groupInstanceIds)
    {
        var instances = new FakeInstances();
        foreach (var groupInstanceId in groupInstanceIds)
        {
            instances.Instances.Add(WorkloadInstance.Create(Guid.NewGuid(), groupInstanceId, "zwave", "gateway", Now));
        }
        return instances;
    }

    private static void AssertNothingWritten(OrchestratorHarness harness, FakeInstances instances)
    {
        instances.Updated.Should().BeEmpty();
        instances.Instances.Single().ReadAttachmentPin().Verdict.Should().Be(
            Reeve.Workloads.Abstractions.Domain.Entities.Documents.WorkloadDocumentVerdict.Absent);
        harness.Repo.All.Should().BeEmpty();
    }

    private static IReadOnlyDictionary<string, WorkloadAttachmentPin> Pin(string workloadId)
        => new Dictionary<string, WorkloadAttachmentPin>
        {
            [workloadId] = new WorkloadAttachmentPin
            {
                Mode = WorkloadNetworkMode.OperatorNetwork,
                AttachmentNames = ["iot-macvlan"],
            },
        };

    private static WorkloadUpdateInitiator Build(OrchestratorHarness harness, FakeInstances instances, FakeGroups groups)
        => new(harness.Repo, harness.Planner, instances, new RecordingNudge(), harness.Pipeline, harness.Clock,
            NullLogger<WorkloadUpdateInitiator>.Instance, groups);

    private sealed class FakeGroups(string id, string? version) : IWorkloadGroupRepository
    {
        private readonly WorkloadGroup _group = WorkloadGroup.Record(id, "source", version, Now);
        public Task<WorkloadGroup?> GetAsync(string groupId, CancellationToken cancellationToken = default)
            => Task.FromResult<WorkloadGroup?>(string.Equals(groupId, id, StringComparison.OrdinalIgnoreCase) ? _group : null);
        public Task<bool> TryAddAsync(WorkloadGroup group, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
        public Task UpdateAsync(WorkloadGroup group, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }
}
