using FluentAssertions;
using Reeve.Workloads.Abstractions;
using Reeve.Workloads.Abstractions.Domain.Entities;
using Reeve.Workloads.Abstractions.Update;
using Reeve.Workloads.Core.Tests.Groups;
using Reeve.Workloads.Core.Update;

namespace Reeve.Workloads.Core.Tests.Update;

public sealed class WorkloadUpdateCohortWalkTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Three_members_take_sequential_turns_and_commit_the_group_once_at_the_barrier()
    {
        var setup = Setup();
        var blocked = await setup.Walk(1);
        blocked.Should().NotContain("stage.sidecar");
        setup.Harness.Provider.AppliedInOrder.Should().NotContain("member-1");

        var firstPass = await setup.Walk(0);
        firstPass.Should().Contain("stage.commit-version");
        setup.Group.InstalledVersion.Should().Be("2.5.0");
        setup.Harness.Provider.AppliedInOrder.Should().Contain("member-0").And.NotContain("member-1");

        await setup.Drain();

        setup.Harness.Provider.AppliedInOrder.IndexOf("member-0")
            .Should().BeLessThan(setup.Harness.Provider.AppliedInOrder.IndexOf("member-1"));
        setup.Harness.Provider.AppliedInOrder.IndexOf("member-1")
            .Should().BeLessThan(setup.Harness.Provider.AppliedInOrder.IndexOf("member-2"));
        setup.Harness.Effects.CommitInstalledVersionCount.Should().Be(1);
        setup.Group.InstalledVersion.Should().Be("2.6.0");
        setup.Members.Should().OnlyContain(member => member.Outcome == WorkloadUpdateOutcome.Completed);
    }

    [Fact]
    public async Task A_second_member_gate_failure_rolls_back_applied_members_and_keeps_group_at_from()
    {
        var setup = Setup();
        setup.Harness.Gate.VerdictsByWorkload["member-1"] = new Queue<WorkloadHealthGateVerdict>([WorkloadHealthGateVerdict.Failed]);

        await setup.Drain();

        setup.Group.InstalledVersion.Should().Be("2.5.0");
        setup.Harness.Effects.CommitInstalledVersionCount.Should().Be(0);
        setup.Harness.Provider.AppliedInOrder.Count(workload => workload == "member-0").Should().Be(2);
        setup.Harness.Provider.AppliedInOrder.Count(workload => workload == "member-1").Should().Be(2);
        setup.Harness.Provider.AppliedInOrder.Should().NotContain("member-2");
        setup.Members[0].Outcome.Should().Be(WorkloadUpdateOutcome.RolledBack);
        setup.Members[1].Outcome.Should().Be(WorkloadUpdateOutcome.RolledBack,
            "the member whose gate failed unwound its own apply; a failure that is undone ends RolledBack");
        setup.Members[2].Outcome.Should().Be(WorkloadUpdateOutcome.RolledBack);
    }

    [Fact]
    public async Task Restart_at_the_barrier_resumes_saved_rows_without_reapplying_a_member()
    {
        var setup = Setup();
        await setup.Walk(0);
        await setup.Walk(1);
        var appliedBeforeRestart = setup.Harness.Provider.AppliedInOrder.ToArray();

        setup.RebuildOrchestrator();
        await setup.Drain();

        setup.Harness.Provider.AppliedInOrder.Take(appliedBeforeRestart.Length).Should().Equal(appliedBeforeRestart);
        setup.Harness.Provider.AppliedInOrder.Count(workload => workload == "member-0").Should().Be(1);
        setup.Harness.Provider.AppliedInOrder.Count(workload => workload == "member-1").Should().Be(1);
        setup.Harness.Effects.CommitInstalledVersionCount.Should().Be(1);
        setup.Group.InstalledVersion.Should().Be("2.6.0");
    }

    private static SetupState Setup()
    {
        var plans = Enumerable.Range(0, 3).Select(index =>
        {
            var instance = Guid.NewGuid();
            var plan = WorkloadUpdatePlanBuilder.Plan(instance,
                [WorkloadUpdatePlanBuilder.Item($"member-{index}", instance, $"new-{index}", $"old-{index}")]);
            return (instance, plan);
        }).ToArray();
        var harness = new OrchestratorHarness(plans[0].plan, Now);
        foreach (var item in plans) harness.Planner.PlansByInstance[item.instance] = item.plan;
        harness.Repo.SupportsCohorts = true;
        var instances = new FakeInstances();
        foreach (var (instance, _) in plans)
            instances.Instances.Add(WorkloadInstance.Create(Guid.NewGuid(), instance, "zwave", "member", Now));
        var group = WorkloadGroup.Record("zwave", "source", "2.5.0", Now);
        var groups = new MutableGroups(group);
        var cohortId = Guid.NewGuid();
        var members = plans.Select((item, ordinal) => WorkloadUpdateIntent.Create(Guid.NewGuid(), item.instance,
            "2.5.0", "2.6.0", Now, new WorkloadUpdateCohortPlace(cohortId, ordinal, plans.Length))).ToArray();
        foreach (var member in members)
        {
            member.RecordStageJournal(Reeve.Pipeline.PipelineJournal.New());
        }

        harness.Repo.AddCohortAsync(members).GetAwaiter().GetResult();
        harness.Effects.OnCommitInstalledVersion = (_, version) => group.MoveTo(version, Now.AddMinutes(1));
        var state = new SetupState(harness, groups, instances, group, members);
        state.RebuildOrchestrator();
        return state;
    }

    private sealed class SetupState(
        OrchestratorHarness harness,
        MutableGroups groups,
        FakeInstances instances,
        WorkloadGroup group,
        WorkloadUpdateIntent[] members)
    {
        private Reeve.Workloads.Core.Update.WorkloadUpdateOrchestrator _orchestrator = null!;
        public OrchestratorHarness Harness { get; } = harness;
        public WorkloadGroup Group { get; } = group;
        public WorkloadUpdateIntent[] Members { get; } = members;

        public void RebuildOrchestrator()
            => _orchestrator = Harness.CohortOrchestratorWith(Harness.Gate, Harness.Clock,
                Harness.Options, groups, instances);

        public async Task<string> Walk(int ordinal)
        {
            await _orchestrator.WalkAsync(Members[ordinal]);
            return Members[ordinal].ReadStageJournal().Value.Entries.LastOrDefault()?.StageId ?? string.Empty;
        }

        public async Task Drain()
        {
            for (var pass = 0; pass < 20 && Members.Any(member => !member.IsTerminal); pass++)
                foreach (var member in Members.Where(member => !member.IsTerminal).ToArray())
                    await _orchestrator.WalkAsync(member);
        }
    }

    private sealed class MutableGroups(WorkloadGroup group) : IWorkloadGroupRepository
    {
        public Task<WorkloadGroup?> GetAsync(string groupId, CancellationToken cancellationToken = default)
            => Task.FromResult(group.IsGroup(groupId) ? group : null);
        public Task<bool> TryAddAsync(WorkloadGroup value, CancellationToken cancellationToken = default)
            => Task.FromResult(false);
        public Task UpdateAsync(WorkloadGroup value, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }
}
