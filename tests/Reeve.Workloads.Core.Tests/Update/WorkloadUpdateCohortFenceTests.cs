using System.Runtime.CompilerServices;
using FluentAssertions;
using Reeve.Pipeline;
using Reeve.Workloads.Abstractions;
using Reeve.Workloads.Abstractions.Domain.Entities;
using Reeve.Workloads.Abstractions.Update;
using Reeve.Workloads.Core.Tests.Groups;
using Reeve.Workloads.Core.Update;

namespace Reeve.Workloads.Core.Tests.Update;

public sealed class WorkloadUpdateCohortFenceTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task A_commit_that_landed_first_refuses_a_sibling_unwind()
    {
        var setup = Setup("2.6.0");
        var member = setup.Members[1];
        member.Fail("abandoned after the commit", Now);
        member.RecordStageJournal(Journal(WorkloadUpdateStages.CommitVersion, PipelineDirection.Forward));

        var result = await setup.Orchestrator.AdvanceAsync(member);

        result.IsTerminal.Should().BeTrue();
        member.Outcome.Should().Be(WorkloadUpdateOutcome.RollbackFailed);
        setup.Harness.Provider.AppliedInOrder.Should().BeEmpty("the stale unwind must not call Down");
        setup.Harness.Repo.CohortDecisionSaveCount.Should().Be(1);
        setup.Group.InstalledVersion.Should().Be("2.6.0");
    }

    [Fact]
    public async Task A_veto_that_landed_first_is_seen_before_a_sibling_can_commit()
    {
        var setup = Setup("2.5.0");
        setup.Members[1].Fail("member failed", Now);
        setup.Members[1].RecordStageJournal(Journal(WorkloadUpdateStages.Sidecar, PipelineDirection.Compensating));
        setup.Members[0].RecordStageJournal(PipelineJournal.New());

        await setup.Orchestrator.AdvanceAsync(setup.Members[0]);

        setup.Members[0].Phase.Should().Be(WorkloadUpdatePhase.RolledBack);
        setup.Harness.Effects.CommitInstalledVersionCount.Should().Be(0);
        setup.Group.InstalledVersion.Should().Be("2.5.0");
        setup.Harness.Repo.CohortDecisionSaveCount.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task A_same_scope_stale_group_read_cannot_save_a_second_cohort_decision()
    {
        var setup = Setup("2.5.0");
        await setup.Orchestrator.AdvanceAsync(setup.Members[0]);

        setup.Members[1].Fail("member failed", Now);
        setup.Members[1].RecordStageJournal(Journal(WorkloadUpdateStages.Sidecar, PipelineDirection.Compensating));
        var reads = new List<string>();
        setup.Groups.OnRead = () => reads.Add("group");
        setup.Harness.Repo.BeforeGetCohort = _ => reads.Add("members");
        setup.Harness.Repo.BeforeSaveCohortDecision = (_, _) =>
        {
            reads.Add("save");
            throw new WorkloadConcurrencyConflictException("stale group token");
        };

        var advance = () => setup.Orchestrator.AdvanceAsync(setup.Members[0]);
        await advance.Should().ThrowAsync<WorkloadConcurrencyConflictException>();

        reads.Should().Equal("group", "members", "save");
    }

    [Fact]
    public async Task A_commit_decided_over_a_group_read_is_refused_when_a_sibling_decision_landed_after_the_read()
    {
        var fence = new GroupTokenFence("zwave", "2.5.0");
        var setup = TokenSetup(fence, size: 2, atCommit: true);
        setup.Harness.Effects.OnCommitInstalledVersion = (_, version) =>
        {
            setup.Members[0].Fail("member 0 failed on another replica", Now);
            setup.Members[0].RecordStageJournal(new PipelineJournal(
                [new StageEntry(WorkloadUpdateStages.CommitVersion, Now, PipelineDirection.Forward, StageEntryState.Working, null)],
                PipelineDirection.Compensating, PipelineState.Running));
            setup.Harness.Repo.SaveCohortDecisionAsync(setup.Members[0], fence.NewRead()).GetAwaiter().GetResult();
            fence.Commit(fence.ScopeRead!, version, Now);
        };
        fence.NewScope();

        var advance = () => setup.Orchestrator.AdvanceAsync(setup.Members[1]);

        await advance.Should().ThrowAsync<WorkloadConcurrencyConflictException>();
        fence.Row.InstalledVersion.Should().Be("2.5.0", "the commit was decided over a token the sibling's decision had moved");
        setup.Harness.Repo.CohortDecisionSaveCount.Should().Be(1, "only the sibling's decision landed");
    }

    [Fact]
    public async Task A_veto_decided_over_a_group_read_is_refused_when_the_commit_landed_after_the_read()
    {
        var fence = new GroupTokenFence("zwave", "2.5.0");
        var setup = TokenSetup(fence, size: 3, atCommit: false);
        setup.Members[2].Fail("member 2 failed", Now);
        setup.Members[2].MarkRollbackFailed("member 2 failed", Now);
        setup.Harness.Repo.BeforeGetCohort = _ =>
        {
            setup.Harness.Repo.BeforeGetCohort = null;
            fence.Commit(fence.NewRead(), "2.6.0", Now);
        };
        fence.NewScope();

        var advance = () => setup.Orchestrator.AdvanceAsync(setup.Members[1]);

        await advance.Should().ThrowAsync<WorkloadConcurrencyConflictException>();
        fence.Row.InstalledVersion.Should().Be("2.6.0");
        setup.Harness.Repo.CohortDecisionSaveCount.Should().Be(0,
            "the veto read the group before the commit, and its save carries that token");
    }

    private static PipelineJournal Journal(string stage, PipelineDirection direction)
        => new([new StageEntry(stage, Now, direction, StageEntryState.Working, null)], direction, PipelineState.Running);

    private static SetupState Setup(string installedVersion)
    {
        var plan = WorkloadUpdatePlanBuilder.Plan(WorkloadUpdatePlanBuilder.InstanceGuid,
            [WorkloadUpdatePlanBuilder.Item("gateway", WorkloadUpdatePlanBuilder.InstanceGuid, "new", "old")]);
        var harness = new OrchestratorHarness(plan, Now);
        var group = WorkloadGroup.Record("zwave", "source", installedVersion, Now);
        var groups = new TestGroups(group);
        var instances = new FakeInstances();
        var cohort = Guid.NewGuid();
        var ids = new[] { Guid.NewGuid(), Guid.NewGuid() };
        foreach (var id in ids)
            instances.Instances.Add(WorkloadInstance.Create(Guid.NewGuid(), id, "zwave", "gateway", Now));
        var members = ids.Select((id, ordinal) => WorkloadUpdateIntent.Create(Guid.NewGuid(), id,
            "2.5.0", "2.6.0", Now, new WorkloadUpdateCohortPlace(cohort, ordinal, 2))).ToArray();
        foreach (var member in members)
        {
            member.RecordStageJournal(Reeve.Pipeline.PipelineJournal.New());
        }

        harness.Repo.AddCohortAsync(members).GetAwaiter().GetResult();
        var state = new SetupState(harness, group, groups, members);
        state.Orchestrator = harness.CohortOrchestratorWith(harness.Gate, harness.Clock,
            harness.Options, groups, instances);
        return state;
    }

    private static SetupState TokenSetup(GroupTokenFence fence, int size, bool atCommit)
    {
        var plan = WorkloadUpdatePlanBuilder.Plan(WorkloadUpdatePlanBuilder.InstanceGuid,
            [WorkloadUpdatePlanBuilder.Item("gateway", WorkloadUpdatePlanBuilder.InstanceGuid, "new", "old")]);
        var harness = new OrchestratorHarness(plan, Now);
        harness.Repo.SupportsCohorts = true;
        harness.Repo.BeforeSaveCohortDecision = (_, group) => fence.CheckAndMove(group);
        var instances = new FakeInstances();
        var cohort = Guid.NewGuid();
        var ids = Enumerable.Range(0, size).Select(_ => Guid.NewGuid()).ToArray();
        foreach (var id in ids)
            instances.Instances.Add(WorkloadInstance.Create(Guid.NewGuid(), id, "zwave", "gateway", Now));
        var members = ids.Select((id, ordinal) =>
        {
            var member = WorkloadUpdateIntent.Create(Guid.NewGuid(), id, "2.5.0", "2.6.0", Now,
                new WorkloadUpdateCohortPlace(cohort, ordinal, ids.Length));
            if (atCommit)
            {
                member.AdvanceTo(WorkloadUpdatePhase.ApplyingSidecar, Now);
                member.AdvanceTo(WorkloadUpdatePhase.HealthGate, Now);
                member.AdvanceTo(WorkloadUpdatePhase.SwappingModule, Now);
                member.RecordStageJournal(Journal(WorkloadUpdateStages.CommitVersion, PipelineDirection.Forward));
            }
            else
            {
                member.RecordStageJournal(PipelineJournal.New());
            }

            return member;
        }).ToArray();
        harness.Repo.AddCohortAsync(members).GetAwaiter().GetResult();
        var state = new SetupState(harness, fence.Row,
            new TestGroups(fence.Row) { Source = fence.Read, OnUpdate = fence.CheckAndMove }, members);
        state.Orchestrator = harness.CohortOrchestratorWith(harness.Gate, harness.Clock,
            harness.Options, state.Groups, instances);
        return state;
    }

    private sealed class GroupTokenFence(string groupId, string installedVersion)
    {
        private readonly ConditionalWeakTable<WorkloadGroup, StrongBox<int>> _readAt = new();
        private int _token;

        public WorkloadGroup Row { get; } = WorkloadGroup.Record(groupId, "source", installedVersion, Now);

        public WorkloadGroup? ScopeRead { get; private set; }

        public void NewScope() => ScopeRead = null;

        public WorkloadGroup NewRead()
        {
            var copy = WorkloadGroup.Record(Row.GroupId, Row.SourceKey, Row.InstalledVersion, Now);
            _readAt.Add(copy, new StrongBox<int>(_token));
            return copy;
        }

        public WorkloadGroup Read() => ScopeRead ??= NewRead();

        public void CheckAndMove(WorkloadGroup read)
        {
            if (!_readAt.TryGetValue(read, out var token) || token.Value != _token)
            {
                throw new WorkloadConcurrencyConflictException("the group row was written since it was read");
            }

            _token++;
        }

        public void Commit(WorkloadGroup read, string version, DateTimeOffset atUtc)
        {
            CheckAndMove(read);
            Row.MoveTo(version, atUtc);
        }
    }

    private sealed class SetupState(
        OrchestratorHarness harness,
        WorkloadGroup group,
        TestGroups groups,
        WorkloadUpdateIntent[] members)
    {
        public OrchestratorHarness Harness { get; } = harness;
        public WorkloadGroup Group { get; } = group;
        public TestGroups Groups { get; } = groups;
        public WorkloadUpdateIntent[] Members { get; } = members;
        public WorkloadUpdateOrchestrator Orchestrator { get; set; } = null!;
    }

    private sealed class TestGroups(WorkloadGroup group) : IWorkloadGroupRepository
    {
        public Action? OnRead { get; set; }
        public Func<WorkloadGroup>? Source { get; set; }
        public Action<WorkloadGroup>? OnUpdate { get; set; }
        public Task<WorkloadGroup?> GetAsync(string groupId, CancellationToken cancellationToken = default)
        {
            OnRead?.Invoke();
            return Task.FromResult(group.IsGroup(groupId) ? (Source?.Invoke() ?? group) : null);
        }
        public Task<bool> TryAddAsync(WorkloadGroup value, CancellationToken cancellationToken = default)
            => Task.FromResult(false);
        public Task UpdateAsync(WorkloadGroup value, CancellationToken cancellationToken = default)
        {
            OnUpdate?.Invoke(value);
            return Task.CompletedTask;
        }
    }
}
