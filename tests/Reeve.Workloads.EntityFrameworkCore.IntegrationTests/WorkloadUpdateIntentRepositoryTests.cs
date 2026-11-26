using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Reeve.Workloads.Abstractions;
using Reeve.Workloads.Abstractions.Domain.Entities;
using Reeve.Workloads.Abstractions.Update;
using Reeve.Workloads.EntityFrameworkCore;
using Reeve.Workloads.EntityFrameworkCore.Persistence;

namespace Reeve.Workloads.EntityFrameworkCore.IntegrationTests;

[Collection(WorkloadsStoreCollection.Name)]
public sealed class WorkloadUpdateIntentRepositoryTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 6, 12, 0, 0, TimeSpan.Zero);

    private readonly PostgresFixture postgres;

    public WorkloadUpdateIntentRepositoryTests(PostgresFixture postgres) => this.postgres = postgres;

    private static WorkloadUpdateIntent Intent(Guid pluginInstanceId, string to = "2.6.0")
        => WorkloadUpdateIntent.Create(Guid.NewGuid(), pluginInstanceId, "2.5.0", to, Now);

    private static WorkloadUpdateIntent CohortIntent(Guid instanceId, Guid cohortId, int ordinal, int size = 2)
        => WorkloadUpdateIntent.Create(Guid.NewGuid(), instanceId, "2.5.0", "2.6.0", Now,
            new WorkloadUpdateCohortPlace(cohortId, ordinal, size));

    private static WorkloadUpdateIntent Terminal(Guid pluginInstanceId)
    {
        var intent = Intent(pluginInstanceId);
        intent.AdvanceTo(WorkloadUpdatePhase.ApplyingSidecar, Now.AddSeconds(1));
        intent.AdvanceTo(WorkloadUpdatePhase.HealthGate, Now.AddSeconds(2));
        intent.AdvanceTo(WorkloadUpdatePhase.SwappingModule, Now.AddSeconds(3));
        intent.Complete(Now.AddSeconds(4));
        return intent;
    }

    private async Task<(WorkloadUpdateIntentRepository Sut, WorkloadsDbContext Context, WorkloadsStore Store)> RepositoryAsync()
    {
        var store = await postgres.CreateStoreAsync();
        var context = store.NewContext();
        return (new WorkloadUpdateIntentRepository(context), context, store);
    }

    [SkippableFact]
    public async Task Add_then_get_active_round_trips_with_steps()
    {
        var (sut, context, _) = await RepositoryAsync();
        await using var _context = context;

        var pluginInstanceId = Guid.NewGuid();
        var intent = Intent(pluginInstanceId);
        intent.AddStep(WorkloadUpdateIntentStep.Create(Guid.NewGuid(), intent.Id, "matter-server", Now));
        await sut.AddAsync(intent);

        var loaded = await sut.GetActiveForInstanceAsync(pluginInstanceId);

        loaded.Should().NotBeNull();
        loaded!.Id.Should().Be(intent.Id);
        loaded.Steps.Should().ContainSingle().Which.WorkloadId.Should().Be("matter-server");
    }

    [SkippableFact]
    public async Task Get_active_returns_null_when_only_terminal_intents_exist()
    {
        var (sut, context, _) = await RepositoryAsync();
        await using var _context = context;

        var pluginInstanceId = Guid.NewGuid();
        await sut.AddAsync(Terminal(pluginInstanceId));

        (await sut.GetActiveForInstanceAsync(pluginInstanceId)).Should().BeNull();
        (await sut.GetActiveIntentIdAsync(pluginInstanceId)).Should().BeNull();
    }

    [SkippableFact]
    public async Task Get_active_intent_id_returns_the_id_only_for_the_active_intent()
    {
        var (sut, context, _) = await RepositoryAsync();
        await using var _context = context;

        var pluginInstanceId = Guid.NewGuid();
        await sut.AddAsync(Terminal(pluginInstanceId));
        var active = Intent(pluginInstanceId);
        await sut.AddAsync(active);

        (await sut.GetActiveIntentIdAsync(pluginInstanceId)).Should().Be(active.Id);
    }

    [SkippableFact]
    public async Task Get_active_ignores_other_instances()
    {
        var (sut, context, _) = await RepositoryAsync();
        await using var _context = context;

        var target = Guid.NewGuid();
        await sut.AddAsync(Intent(Guid.NewGuid()));
        var mine = Intent(target);
        await sut.AddAsync(mine);

        (await sut.GetActiveForInstanceAsync(target))!.Id.Should().Be(mine.Id);
    }

    [SkippableFact]
    public async Task Get_open_intents_returns_exactly_the_non_terminal_set()
    {
        var (sut, context, _) = await RepositoryAsync();
        await using var _context = context;

        var openA = Intent(Guid.NewGuid());
        var openB = Intent(Guid.NewGuid());
        openB.AdvanceTo(WorkloadUpdatePhase.ApplyingSidecar, Now.AddSeconds(1));
        await sut.AddAsync(openA);
        await sut.AddAsync(openB);
        await sut.AddAsync(Terminal(Guid.NewGuid()));

        var rollingBack = Intent(Guid.NewGuid());
        rollingBack.AdvanceTo(WorkloadUpdatePhase.ApplyingSidecar, Now.AddSeconds(1));
        rollingBack.AdvanceTo(WorkloadUpdatePhase.HealthGate, Now.AddSeconds(2));
        rollingBack.Fail("gate failed", Now.AddSeconds(3));
        rollingBack.AdvanceTo(WorkloadUpdatePhase.RolledBack, Now.AddSeconds(4));
        await sut.AddAsync(rollingBack);

        var open = await sut.GetOpenIntentsAsync();

        open.Select(i => i.Id).Should().BeEquivalentTo(new[] { openA.Id, openB.Id });
    }

    [SkippableFact]
    public async Task Get_by_id_includes_steps_and_returns_null_for_missing()
    {
        var (sut, context, _) = await RepositoryAsync();
        await using var _context = context;

        var intent = Intent(Guid.NewGuid());
        intent.AddStep(WorkloadUpdateIntentStep.Create(Guid.NewGuid(), intent.Id, "zwave-js", Now));
        await sut.AddAsync(intent);

        (await sut.GetByIdAsync(intent.Id))!.Steps.Should().ContainSingle();
        (await sut.GetByIdAsync(Guid.NewGuid())).Should().BeNull();
    }

    [SkippableFact]
    public async Task Update_persists_a_phase_advance_and_snapshot()
    {
        var (sut, context, _) = await RepositoryAsync();
        await using var _context = context;

        var intent = Intent(Guid.NewGuid());
        intent.StageSnapshots(
            new WorkloadUpdateSnapshots
            {
                FromDigests = new Dictionary<string, string?>(StringComparer.Ordinal) { ["gateway"] = "sha256:from" },
            },
            Now);
        await sut.AddAsync(intent);

        intent.AdvanceTo(WorkloadUpdatePhase.ApplyingSidecar, Now.AddSeconds(1));
        await sut.UpdateAsync(intent);

        var reloaded = await sut.GetByIdAsync(intent.Id);
        reloaded!.Phase.Should().Be(WorkloadUpdatePhase.ApplyingSidecar);
        reloaded.ReadFromDigests()["gateway"].Should().Be("sha256:from");
    }

    [SkippableFact]
    public async Task Completing_a_materialised_intent_persists_the_dropped_parameter_snapshots()
    {
        var (sut, context, store) = await RepositoryAsync();
        await using var _context = context;

        var pluginInstanceId = Guid.NewGuid();
        var intent = Intent(pluginInstanceId);
        intent.StageSnapshots(
            new WorkloadUpdateSnapshots
            {
                PreviousParameters = new Dictionary<string, IReadOnlyDictionary<string, string?>>(StringComparer.Ordinal)
                {
                    ["matter-adapter"] = new Dictionary<string, string?>(StringComparer.Ordinal)
                    {
                        ["attestationMode"] = "test",
                        ["bleAdapterId"] = null,
                    },
                },
                OperatorParameters = new Dictionary<string, string?>(StringComparer.Ordinal) { ["matterLogLevel"] = "info" },
            },
            Now);
        await sut.AddAsync(intent);

        await using var swapContext = store.NewContext();
        var swap = new WorkloadUpdateIntentRepository(swapContext);
        var loaded = await swap.GetActiveForInstanceAsync(pluginInstanceId);
        loaded!.AdvanceTo(WorkloadUpdatePhase.ApplyingSidecar, Now.AddSeconds(1));
        loaded.AdvanceTo(WorkloadUpdatePhase.HealthGate, Now.AddSeconds(2));
        loaded.AdvanceTo(WorkloadUpdatePhase.SwappingModule, Now.AddSeconds(3));
        loaded.Complete(Now.AddSeconds(4));

        await swap.UpdateAsync(loaded);

        await using var reader = store.NewContext();
        var settled = await reader.WorkloadUpdateIntents.SingleAsync(row => row.Id == intent.Id);
        settled.Phase.Should().Be(WorkloadUpdatePhase.Completed, "the swap tick's save is what takes the row out of SwappingModule");
        settled.PreviousParametersDocument!.Workloads.Should().BeEmpty();
        settled.OperatorParametersDocument!.Entries.Should().BeEmpty();
        settled.ReadOperatorParameters().Should().BeEmpty();
    }

    [SkippableFact]
    public async Task A_swap_tick_completes_an_intent_that_carries_every_snapshot()
    {
        var (sut, context, store) = await RepositoryAsync();
        await using var _context = context;

        var pluginInstanceId = Guid.NewGuid();
        var intent = Intent(pluginInstanceId);
        intent.AddStep(WorkloadUpdateIntentStep.Create(Guid.NewGuid(), intent.Id, "matter-adapter", Now));
        intent.StageSnapshots(
            new WorkloadUpdateSnapshots
            {
                PreviousParameters = new Dictionary<string, IReadOnlyDictionary<string, string?>>(StringComparer.Ordinal)
                {
                    ["matter-adapter"] = new Dictionary<string, string?>(StringComparer.Ordinal)
                    {
                        ["attestationMode"] = "test",
                        ["bleAdapterId"] = null,
                    },
                },
                OperatorParameters = new Dictionary<string, string?>(StringComparer.Ordinal)
                {
                    ["matterLogLevel"] = "info",
                    ["radioDevicePath"] = null,
                },
                FromDigests = new Dictionary<string, string?>(StringComparer.Ordinal) { ["matter-adapter"] = "sha256:from" },
                ToDigests = new Dictionary<string, string?>(StringComparer.Ordinal) { ["matter-adapter"] = "sha256:to" },
            },
            Now);
        intent.AdvanceTo(WorkloadUpdatePhase.ApplyingSidecar, Now.AddSeconds(1));
        intent.AdvanceTo(WorkloadUpdatePhase.HealthGate, Now.AddSeconds(2));
        intent.AdvanceTo(WorkloadUpdatePhase.SwappingModule, Now.AddSeconds(3));
        await sut.AddAsync(intent);

        await using var swapContext = store.NewContext();
        var swap = new WorkloadUpdateIntentRepository(swapContext);
        var loaded = (await swap.GetOpenIntentsAsync()).Single(row => row.Id == intent.Id);
        loaded.Steps.Single().Advance(WorkloadUpdatePhase.Completed, WorkloadUpdateStepState.Done, "swap acknowledged", Now.AddSeconds(5));
        loaded.Complete(Now.AddSeconds(5));

        await swap.UpdateAsync(loaded);

        await using var reader = store.NewContext();
        var settled = await reader.WorkloadUpdateIntents.SingleAsync(row => row.Id == intent.Id);
        settled.Phase.Should().Be(WorkloadUpdatePhase.Completed);
        settled.ReadToDigests()["matter-adapter"].Should().Be("sha256:to", "a settled intent keeps the digests it moved between");
    }

    [SkippableFact]
    public async Task Get_recent_for_instance_returns_active_and_terminal_newest_first_capped()
    {
        var (sut, context, _) = await RepositoryAsync();
        await using var _context = context;

        var target = Guid.NewGuid();

        var oldTerminal = Terminal(target);
        var midActive = Intent(target, to: "3.0.0");
        typeof(WorkloadUpdateIntent).GetProperty("CreatedAtUtc")!.SetValue(midActive, Now.AddMinutes(1));
        var newestTerminal = Terminal(target);
        typeof(WorkloadUpdateIntent).GetProperty("CreatedAtUtc")!.SetValue(newestTerminal, Now.AddMinutes(2));

        await sut.AddAsync(oldTerminal);
        await sut.AddAsync(midActive);
        await sut.AddAsync(newestTerminal);
        await sut.AddAsync(Terminal(Guid.NewGuid()));

        var recent = await sut.GetRecentForInstanceAsync(target, limit: 10);

        recent.Select(i => i.Id).Should().ContainInOrder(newestTerminal.Id, midActive.Id, oldTerminal.Id);
        recent.Should().HaveCount(3, "the other instance's intent is excluded");
    }

    [SkippableFact]
    public async Task Get_recent_for_instance_honours_the_limit_and_a_zero_limit_yields_empty()
    {
        var (sut, context, _) = await RepositoryAsync();
        await using var _context = context;

        var target = Guid.NewGuid();
        for (var n = 0; n < 4; n++)
        {
            var i = Terminal(target);
            typeof(WorkloadUpdateIntent).GetProperty("CreatedAtUtc")!.SetValue(i, Now.AddMinutes(n));
            await sut.AddAsync(i);
        }

        (await sut.GetRecentForInstanceAsync(target, limit: 2)).Should().HaveCount(2);
        (await sut.GetRecentForInstanceAsync(target, limit: 0)).Should().BeEmpty();
    }

    [SkippableFact]
    public async Task Second_active_intent_for_the_same_instance_is_rejected_by_the_partial_unique_index()
    {
        var (sut, context, _) = await RepositoryAsync();
        await using var _context = context;

        var pluginInstanceId = Guid.NewGuid();
        await sut.AddAsync(Intent(pluginInstanceId));

        var exception = await Record.ExceptionAsync(() => sut.AddAsync(Intent(pluginInstanceId)));

        exception.Should().BeOfType<DbUpdateException>(
            "a second ACTIVE intent for the same instance must be rejected by the partial unique index");
        exception!.InnerException.Should().BeOfType<PostgresException>()
            .Which.SqlState.Should().Be(PostgresErrorCodes.UniqueViolation);
    }

    [SkippableFact]
    public async Task Terminal_history_coexists_and_a_new_active_intent_is_permitted_after_completion()
    {
        var (sut, context, _) = await RepositoryAsync();
        await using var _context = context;

        var pluginInstanceId = Guid.NewGuid();

        await sut.AddAsync(Terminal(pluginInstanceId));
        await sut.AddAsync(Terminal(pluginInstanceId));
        var active = Intent(pluginInstanceId);

        var act = () => sut.AddAsync(active);

        await act.Should().NotThrowAsync();
        (await sut.GetActiveIntentIdAsync(pluginInstanceId)).Should().Be(active.Id);
    }

    [SkippableFact]
    public async Task Distinct_instances_each_get_their_own_active_intent()
    {
        var (sut, context, _) = await RepositoryAsync();
        await using var _context = context;

        var a = Intent(Guid.NewGuid());
        var b = Intent(Guid.NewGuid());

        await sut.AddAsync(a);
        var act = () => sut.AddAsync(b);

        await act.Should().NotThrowAsync("the uniqueness scope is per-instance, not global");
    }

    [SkippableFact]
    public async Task Steps_cascade_delete_with_the_intent()
    {
        var (sut, context, store) = await RepositoryAsync();
        await using var _context = context;

        var intent = Intent(Guid.NewGuid());
        intent.AddStep(WorkloadUpdateIntentStep.Create(Guid.NewGuid(), intent.Id, "matter-server", Now));
        intent.AddStep(WorkloadUpdateIntentStep.Create(Guid.NewGuid(), intent.Id, "zwave-js", Now));
        await sut.AddAsync(intent);

        context.WorkloadUpdateIntents.Remove(intent);
        await context.SaveChangesAsync();

        await using var reader = store.NewContext();
        (await reader.WorkloadUpdateIntentSteps.CountAsync(s => s.IntentId == intent.Id))
            .Should().Be(0, "the FK is ON DELETE CASCADE — orphan steps must not survive their intent");
    }

    [SkippableFact]
    public async Task Add_cohort_is_atomic_when_an_active_instance_is_duplicated()
    {
        var (sut, context, store) = await RepositoryAsync();
        await using var _context = context;
        var instance = Guid.NewGuid();
        var cohortId = Guid.NewGuid();
        var members = new[] { CohortIntent(instance, cohortId, 0), CohortIntent(instance, cohortId, 1) };

        var write = () => sut.AddCohortAsync(members);
        await write.Should().ThrowAsync<DbUpdateException>();

        await using var reader = store.NewContext();
        (await new WorkloadUpdateIntentRepository(reader).GetActiveForInstanceAsync(instance))
            .Should().BeNull("a failed batch must not leave a partial cohort");
    }

    [SkippableFact]
    public async Task A_failed_add_cohort_leaves_nothing_added_in_the_change_tracker()
    {
        var (sut, context, store) = await RepositoryAsync();
        await using var _context = context;
        var instance = Guid.NewGuid();
        var cohortId = Guid.NewGuid();
        var members = new[] { CohortIntent(instance, cohortId, 0), CohortIntent(instance, cohortId, 1) };

        var write = () => sut.AddCohortAsync(members);
        await write.Should().ThrowAsync<DbUpdateException>();

        context.ChangeTracker.Entries().Should().NotContain(entry => entry.State == EntityState.Added,
            "the refused batch must not be written by the next save of this scope");
        await context.SaveChangesAsync();
        await using var reader = store.NewContext();
        (await new WorkloadUpdateIntentRepository(reader).GetActiveForInstanceAsync(instance)).Should().BeNull();
    }

    [SkippableFact]
    public async Task A_failed_add_cohort_leaves_an_unrelated_added_entity_of_the_same_context_tracked()
    {
        var (sut, context, store) = await RepositoryAsync();
        await using var _context = context;
        var unrelated = WorkloadGroup.Record("unrelated-added-group", "source", "2.5.0", Now);
        context.WorkloadGroups.Add(unrelated);
        var instance = Guid.NewGuid();
        var cohortId = Guid.NewGuid();
        var members = new[] { CohortIntent(instance, cohortId, 0), CohortIntent(instance, cohortId, 1) };

        var write = () => sut.AddCohortAsync(members);
        await write.Should().ThrowAsync<DbUpdateException>();

        context.Entry(unrelated).State.Should().Be(EntityState.Added,
            "the failed batch detaches what it added, not what the caller had already added to the scope");
        context.ChangeTracker.Entries().Should().NotContain(entry => entry.Entity is WorkloadUpdateIntent,
            "the refused members are gone from the context");
        await context.SaveChangesAsync();
        await using var reader = store.NewContext();
        (await new Reeve.Workloads.EntityFrameworkCore.WorkloadGroupRepository(reader).GetAsync("unrelated-added-group"))
            .Should().NotBeNull("the caller's own pending add is still saved by its own next save");
    }

    [SkippableFact]
    public async Task Get_cohort_returns_terminal_members_in_ordinal_order_untracked()
    {
        var (sut, context, _) = await RepositoryAsync();
        await using var _context = context;
        var cohortId = Guid.NewGuid();
        var second = CohortIntent(Guid.NewGuid(), cohortId, 1);
        var first = CohortIntent(Guid.NewGuid(), cohortId, 0);
        first.AdvanceTo(WorkloadUpdatePhase.ApplyingSidecar, Now.AddSeconds(1));
        first.AdvanceTo(WorkloadUpdatePhase.HealthGate, Now.AddSeconds(2));
        first.AdvanceTo(WorkloadUpdatePhase.SwappingModule, Now.AddSeconds(3));
        first.Complete(Now.AddSeconds(4));
        await sut.AddCohortAsync([second, first]);

        var members = await sut.GetCohortAsync(cohortId);

        members.Select(member => member.CohortOrdinal).Should().Equal(0, 1);
        members[0].IsTerminal.Should().BeTrue();
        members.Should().OnlyContain(member => context.Entry(member).State == EntityState.Detached);
    }

    [SkippableFact]
    public async Task Open_intents_walk_cohort_members_in_submitted_order()
    {
        var (sut, context, _) = await RepositoryAsync();
        await using var _context = context;
        var cohortId = Guid.NewGuid();
        var second = CohortIntent(Guid.NewGuid(), cohortId, 1);
        var first = CohortIntent(Guid.NewGuid(), cohortId, 0);
        await sut.AddCohortAsync([second, first]);

        var open = await sut.GetOpenIntentsAsync();

        open.Where(member => member.CohortId == cohortId)
            .Select(member => member.CohortOrdinal)
            .Should().Equal(0, 1);
    }

    [SkippableFact]
    public async Task Save_cohort_decision_writes_the_group_token_and_refuses_a_stale_token()
    {
        var (firstRepository, firstContext, store) = await RepositoryAsync();
        await using var _firstContext = firstContext;
        var cohortId = Guid.NewGuid();
        var first = CohortIntent(Guid.NewGuid(), cohortId, 0);
        var second = CohortIntent(Guid.NewGuid(), cohortId, 1);
        await firstRepository.AddCohortAsync([first, second]);
        var groups = new Reeve.Workloads.EntityFrameworkCore.WorkloadGroupRepository(firstContext);
        await groups.TryAddAsync(WorkloadGroup.Record("cohort-group", "source", "2.5.0", Now));

        await using var secondContext = store.NewContext();
        var secondRepository = new WorkloadUpdateIntentRepository(secondContext);
        var firstGroup = await groups.GetAsync("cohort-group");
        var staleGroup = await new Reeve.Workloads.EntityFrameworkCore.WorkloadGroupRepository(secondContext).GetAsync("cohort-group");
        var firstTracked = await firstRepository.GetByIdAsync(first.Id);
        var secondTracked = await secondRepository.GetByIdAsync(second.Id);
        var initialToken = staleGroup!.Version;
        firstTracked!.Fail("member decision", Now.AddMinutes(1));
        secondTracked!.Fail("stale decision", Now.AddMinutes(1));

        await firstRepository.SaveCohortDecisionAsync(firstTracked, firstGroup!);
        var conflict = () => secondRepository.SaveCohortDecisionAsync(secondTracked, staleGroup);
        (await conflict.Should().ThrowAsync<DbUpdateConcurrencyException>())
            .Which.Entries.Should().Contain(entry => entry.Entity is WorkloadGroup,
                "the stale group token is what lost, and the exception hosts map to 409 for a single intent says so");

        await using var verify = store.NewContext();
        var savedGroup = await new Reeve.Workloads.EntityFrameworkCore.WorkloadGroupRepository(verify).GetAsync("cohort-group");
        savedGroup!.Version.Should().Be(initialToken + 1, "even an unchanged group write moves its decision fence");
        var savedIntent = await new WorkloadUpdateIntentRepository(verify).GetByIdAsync(first.Id);
        savedIntent!.Outcome.Should().Be(WorkloadUpdateOutcome.InProgress);
        savedIntent.Direction.Should().Be(WorkloadUpdateDirection.RollingBack);
    }

    [SkippableFact]
    public async Task Save_cohort_decision_losing_on_the_intent_row_names_the_intent_not_the_group()
    {
        var (firstRepository, firstContext, store) = await RepositoryAsync();
        await using var _firstContext = firstContext;
        var cohortId = Guid.NewGuid();
        var first = CohortIntent(Guid.NewGuid(), cohortId, 0);
        await firstRepository.AddCohortAsync([first, CohortIntent(Guid.NewGuid(), cohortId, 1)]);
        var groups = new Reeve.Workloads.EntityFrameworkCore.WorkloadGroupRepository(firstContext);
        await groups.TryAddAsync(WorkloadGroup.Record("cohort-group-intent", "source", "2.5.0", Now));

        await using var secondContext = store.NewContext();
        var secondRepository = new WorkloadUpdateIntentRepository(secondContext);
        var firstTracked = await firstRepository.GetByIdAsync(first.Id);
        var secondTracked = await secondRepository.GetByIdAsync(first.Id);
        firstTracked!.Fail("the other writer", Now.AddMinutes(1));
        await firstRepository.UpdateAsync(firstTracked);
        secondTracked!.Fail("the loser", Now.AddMinutes(1));
        var freshGroup = await new Reeve.Workloads.EntityFrameworkCore.WorkloadGroupRepository(secondContext)
            .GetAsync("cohort-group-intent");

        var conflict = () => secondRepository.SaveCohortDecisionAsync(secondTracked, freshGroup!);

        (await conflict.Should().ThrowAsync<DbUpdateConcurrencyException>())
            .Which.Entries.Should().Contain(entry => entry.Entity is WorkloadUpdateIntent)
            .And.NotContain(entry => entry.Entity is WorkloadGroup,
                "the group row read fresh did not lose, so the conflict is not reported as a group conflict");
    }
}
