using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Reeve.Workloads.Abstractions;
using Reeve.Workloads.Abstractions.Domain.DesiredSpec;
using Reeve.Workloads.Abstractions.Domain.Entities;
using Reeve.Workloads.Abstractions.Contracts;
using Reeve.Workloads.Abstractions.Providers;
using Reeve.Workloads.Abstractions.Update;
using Reeve.Workloads.Core;
using Reeve.Workloads.Core.Providers;
using Reeve.Workloads.Providers.Kubernetes;
using Xunit;

namespace Reeve.Workloads.Core.Tests;

public sealed class WorkloadIntentResumerTests
{
    private static readonly DateTimeOffset T0 = new(2026, 7, 6, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid InstanceId = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");

    [Fact]
    public async Task Resumable_intent_is_left_active_and_untouched()
    {
        var intent = ActiveStagedIntent(WorkloadUpdatePhase.HealthGate, target: true, previous: true);
        var repo = new FakeRepo(intent);
        var resumer = NewResumer(repo);

        await resumer.ResumeOpenIntentsAsync(CancellationToken.None);

        intent.Phase.Should().Be(WorkloadUpdatePhase.HealthGate, "a resumable intent's phase is not disturbed");
        intent.IsActive.Should().BeTrue();
        repo.Updated.Should().NotContain(intent, "no persistence for a resumable intent — the drift pass converges on it");
    }

    [Fact]
    public async Task Intent_with_null_target_specs_is_marked_RollbackFailed_and_persisted()
    {
        var intent = WorkloadUpdateIntent.Create(Guid.NewGuid(), InstanceId, "1.0.0", "1.1.0", T0);
        var repo = new FakeRepo(intent);
        var resumer = NewResumer(repo);

        await resumer.ResumeOpenIntentsAsync(CancellationToken.None);

        intent.Phase.Should().Be(WorkloadUpdatePhase.RollbackFailed);
        intent.Error.Should().NotBeNullOrWhiteSpace();
        repo.Updated.Should().Contain(intent);
    }

    [Fact]
    public async Task Intent_with_corrupt_target_specs_is_marked_RollbackFailed()
    {
        var intent = Staged(
            WorkloadUpdatePhase.ApplyingSidecar, TargetMap(), PreviousMap(), corruptTarget: true);
        var repo = new FakeRepo(intent);
        var resumer = NewResumer(repo);

        await resumer.ResumeOpenIntentsAsync(CancellationToken.None);

        intent.Phase.Should().Be(WorkloadUpdatePhase.RollbackFailed, "a corrupt target fails CLOSED (A4)");
        repo.Updated.Should().Contain(intent);
    }

    [Fact]
    public async Task Intent_with_null_previous_specs_is_marked_RollbackFailed()
    {
        var intent = Staged(WorkloadUpdatePhase.HealthGate, TargetMap(), previous: null);
        var repo = new FakeRepo(intent);
        var resumer = NewResumer(repo);

        await resumer.ResumeOpenIntentsAsync(CancellationToken.None);

        intent.Phase.Should().Be(WorkloadUpdatePhase.RollbackFailed);
        repo.Updated.Should().Contain(intent);
    }

    [Fact]
    public async Task Intent_with_empty_target_map_is_marked_RollbackFailed()
    {
        var intent = Staged(
            WorkloadUpdatePhase.ApplyingSidecar,
            new Dictionary<string, WorkloadDesiredSpec>(StringComparer.Ordinal),
            PreviousMap());
        var repo = new FakeRepo(intent);
        var resumer = NewResumer(repo);

        await resumer.ResumeOpenIntentsAsync(CancellationToken.None);

        intent.Phase.Should().Be(WorkloadUpdatePhase.RollbackFailed, "an empty target map has nothing to converge on");
        repo.Updated.Should().Contain(intent);
    }

    [Fact]
    public async Task Already_rolling_back_but_corrupt_intent_is_terminalized_directly_to_RollbackFailed()
    {
        var intent = Staged(
            WorkloadUpdatePhase.RollingBack, TargetMap(), PreviousMap(), corruptTarget: true);
        var repo = new FakeRepo(intent);
        var resumer = NewResumer(repo);

        await resumer.ResumeOpenIntentsAsync(CancellationToken.None);

        intent.Phase.Should().Be(WorkloadUpdatePhase.RollbackFailed);
        repo.Updated.Should().Contain(intent);
    }

    [Fact]
    public async Task SwappingModule_intent_with_corrupt_target_is_terminalized_to_RollbackFailed()
    {
        var intent = Staged(
            WorkloadUpdatePhase.SwappingModule, TargetMap(), PreviousMap(), corruptTarget: true);
        var repo = new FakeRepo(intent);
        var resumer = NewResumer(repo);

        var act = () => resumer.ResumeOpenIntentsAsync(CancellationToken.None);

        await act.Should().NotThrowAsync("terminalizing an unresumable swap must not crash the resume pass");
        intent.Phase.Should().Be(WorkloadUpdatePhase.RollbackFailed,
            "an unresumable corrupt-target swap is terminalized directly to RollbackFailed for a human decision (I-b)");
        intent.IsTerminal.Should().BeTrue();
        intent.Error.Should().NotBeNullOrWhiteSpace();
        repo.Updated.Should().Contain(intent, "the terminalization is persisted");
    }

    [Fact]
    public async Task SwappingModule_intent_with_a_valid_target_is_left_in_place_not_terminalized()
    {
        var intent = ActiveStagedIntent(WorkloadUpdatePhase.SwappingModule, target: true, previous: true);
        var repo = new FakeRepo(intent);
        var resumer = NewResumer(repo);

        await resumer.ResumeOpenIntentsAsync(CancellationToken.None);

        intent.Phase.Should().Be(WorkloadUpdatePhase.SwappingModule,
            "a resumable certified swap is never terminalized — it stays and retries");
        intent.IsActive.Should().BeTrue();
        repo.Updated.Should().NotContain(intent, "no persistence for a resumable swap");
    }

    [Fact]
    public async Task One_failing_intent_does_not_stop_the_others()
    {
        var poison = ActiveStagedIntent(WorkloadUpdatePhase.HealthGate, target: true, previous: true);
        var badTarget = WorkloadUpdateIntent.Create(Guid.NewGuid(), Guid.NewGuid(), "1.0.0", "1.1.0", T0);
        var good = ActiveStagedIntent(WorkloadUpdatePhase.ApplyingSidecar, target: true, previous: true);

        var repo = new FakeRepo(poison, badTarget, good) { ThrowOnUpdateFor = poison.Id };
        var resumer = NewResumer(repo);

        var act = () => resumer.ResumeOpenIntentsAsync(CancellationToken.None);

        await act.Should().NotThrowAsync("a fault on one intent must not abort boot");
        badTarget.Phase.Should().Be(WorkloadUpdatePhase.RollbackFailed, "the other intent is still processed");
        good.Phase.Should().Be(WorkloadUpdatePhase.ApplyingSidecar, "the resumable one is left alone");
    }

    [Fact]
    public async Task Empty_open_set_is_a_no_op()
    {
        var repo = new FakeRepo();
        var resumer = NewResumer(repo);

        var act = () => resumer.ResumeOpenIntentsAsync(CancellationToken.None);

        await act.Should().NotThrowAsync();
        repo.Updated.Should().BeEmpty();
    }

    [Fact]
    public async Task Rerunning_after_terminalizing_is_idempotent()
    {
        var intent = WorkloadUpdateIntent.Create(Guid.NewGuid(), InstanceId, "1.0.0", "1.1.0", T0);
        var repo = new FakeRepo(intent);
        var resumer = NewResumer(repo);

        await resumer.ResumeOpenIntentsAsync(CancellationToken.None);
        repo.Updated.Clear();

        await resumer.ResumeOpenIntentsAsync(CancellationToken.None);

        repo.Updated.Should().BeEmpty("terminal intents are excluded from GetOpenIntents ⇒ re-run is a no-op");
    }

    [Fact]
    public async Task Resumability_is_decided_from_the_target_document()
    {
        var intent = Staged(
            WorkloadUpdatePhase.ApplyingSidecar,
            new Dictionary<string, WorkloadDesiredSpec>(StringComparer.Ordinal),
            PreviousMap());
        WorkloadActiveIntentProviderTests.GraftTargetDocument(
            intent, donor: ActiveStagedIntent(WorkloadUpdatePhase.StagingParams, target: true, previous: true));
        var repo = new FakeRepo(intent);
        var resumer = NewResumer(repo);

        await resumer.ResumeOpenIntentsAsync(CancellationToken.None);

        intent.Phase.Should().Be(WorkloadUpdatePhase.ApplyingSidecar,
            "the document's target map is not empty ⇒ resumable ⇒ phase untouched");
        repo.Updated.Should().NotContain(intent, "a resumable intent is not persisted");
    }

    [Fact]
    public async Task An_unreadable_target_is_reported_as_corrupt_rather_than_as_nothing_recorded()
    {
        var intent = Staged(WorkloadUpdatePhase.HealthGate, TargetMap(), PreviousMap(), corruptTarget: true);
        var repo = new FakeRepo(intent);
        var resumer = NewResumer(repo);

        await resumer.ResumeOpenIntentsAsync(CancellationToken.None);

        intent.Phase.Should().Be(WorkloadUpdatePhase.RollbackFailed, "an unreadable target fails CLOSED (A4)");
        intent.Error.Should().Contain("corrupt");
        intent.Error.Should().NotContain("no target specs recorded");
        repo.Updated.Should().Contain(intent);
    }

    [Fact]
    public async Task An_unreadable_previous_map_is_reported_as_corrupt_rather_than_as_nothing_recorded()
    {
        var intent = Staged(WorkloadUpdatePhase.HealthGate, TargetMap(), PreviousMap(), corruptPrevious: true);
        var repo = new FakeRepo(intent);
        var resumer = NewResumer(repo);

        await resumer.ResumeOpenIntentsAsync(CancellationToken.None);

        intent.Phase.Should().Be(WorkloadUpdatePhase.RollbackFailed, "an unreadable previous map fails CLOSED (A4)");
        intent.Error.Should().Contain("corrupt");
        intent.Error.Should().NotContain("no previous specs recorded");
        repo.Updated.Should().Contain(intent);
    }

    [Fact]
    public async Task Cohort_member_without_a_completed_turn_is_resumable_without_a_target()
    {
        var intent = CohortIntent(target: false);
        var repo = new FakeRepo(intent);

        await NewCohortResumer(repo).ResumeOpenIntentsAsync();

        intent.IsActive.Should().BeTrue();
        repo.Updated.Should().NotContain(intent);
    }

    [Fact]
    public async Task Vetoed_waiting_member_is_resumable_while_params_is_working()
    {
        var intent = CohortIntent(target: false);
        intent.Fail("sibling failed", T0);
        intent.RecordStageJournal(new Reeve.Pipeline.PipelineJournal(
            [new Reeve.Pipeline.StageEntry(WorkloadUpdateStages.Params, T0,
                Reeve.Pipeline.PipelineDirection.Forward, Reeve.Pipeline.StageEntryState.Working, "waiting")],
            Reeve.Pipeline.PipelineDirection.Compensating,
            Reeve.Pipeline.PipelineState.Running));
        var repo = new FakeRepo(intent);

        await NewCohortResumer(repo).ResumeOpenIntentsAsync();

        intent.Phase.Should().Be(WorkloadUpdatePhase.RollingBack);
        repo.Updated.Should().NotContain(intent);
    }

    [Fact]
    public async Task Cohort_member_with_params_done_and_no_target_is_still_terminalized()
    {
        var intent = CohortIntent(target: false);
        intent.RecordStageJournal(ParamsDoneJournal());
        var repo = new FakeRepo(intent);

        await NewCohortResumer(repo).ResumeOpenIntentsAsync();

        intent.Phase.Should().Be(WorkloadUpdatePhase.RollbackFailed);
        repo.CohortDecisionSaves.Should().Be(1);
    }

    [Theory]
    [InlineData(WorkloadStateReason.HealthObserved, WorkloadInstanceState.Degraded)]
    [InlineData(WorkloadStateReason.SuspendedByHost, WorkloadInstanceState.Suspended)]
    public async Task A_stranded_switch_unwinds_to_managed_and_into_the_hosts_hold_when_the_host_withdrew_it(
        WorkloadStateReason mark, WorkloadInstanceState expected)
    {
        var stranded = WorkloadInstance.Create(Guid.NewGuid(), InstanceId, "group", "gateway", T0);
        stranded.TransitionTo(WorkloadInstanceState.Deploying, WorkloadStateReason.Provisioning, null, T0);
        stranded.TransitionTo(WorkloadInstanceState.Healthy, WorkloadStateReason.HealthObserved, null, T0);
        stranded.TransitionTo(WorkloadInstanceState.Switching, WorkloadStateReason.HealthObserved, null, T0);
        stranded.TransitionTo(WorkloadInstanceState.Switching, mark, null, T0);
        var instances = Substitute.For<IWorkloadInstanceRepository>();
        instances.ListByStateAsync(WorkloadInstanceState.Switching, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<WorkloadInstance>>([stranded]));
        var resumer = new WorkloadIntentResumer(new FakeRepo(), new MutableClock(T0 + WorkloadStrandedSwitchUnwind.StrandedAfter),
            NullLogger<WorkloadIntentResumer>.Instance, instances, Substitute.For<IWorkloadGroupRepository>(), NoProviders);

        await resumer.ResumeOpenIntentsAsync();

        stranded.State.Should().Be(expected, "the host's boot enforcement ran before this unwind and is not re-run");
        await instances.Received().UpdateAsync(stranded, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_switch_younger_than_the_bound_is_left_to_the_replica_running_it()
    {
        var switching = WorkloadInstance.Create(Guid.NewGuid(), InstanceId, "group", "gateway", T0);
        switching.TransitionTo(WorkloadInstanceState.Deploying, WorkloadStateReason.Provisioning, null, T0);
        switching.TransitionTo(WorkloadInstanceState.Healthy, WorkloadStateReason.HealthObserved, null, T0);
        switching.TransitionTo(WorkloadInstanceState.Switching, WorkloadStateReason.HealthObserved, null, T0);
        var instances = Substitute.For<IWorkloadInstanceRepository>();
        instances.ListByStateAsync(WorkloadInstanceState.Switching, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<WorkloadInstance>>([switching]));
        var resumer = new WorkloadIntentResumer(new FakeRepo(),
            new MutableClock(T0 + WorkloadStrandedSwitchUnwind.StrandedAfter - TimeSpan.FromSeconds(1)),
            NullLogger<WorkloadIntentResumer>.Instance, instances, Substitute.For<IWorkloadGroupRepository>(), NoProviders);

        await resumer.ResumeOpenIntentsAsync();

        switching.State.Should().Be(WorkloadInstanceState.Switching, "a leader booting while another replica switches must not unwind under it");
        await instances.DidNotReceive().UpdateAsync(Arg.Any<WorkloadInstance>(), Arg.Any<CancellationToken>());
    }

    private static readonly IWorkloadProviderRegistry NoProviders = new WorkloadProviderRegistry([]);

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Only_a_disowned_instance_ends_its_open_intent_not_a_connected_manual_one(bool materializes)
    {
        var intent = ActiveStagedIntent(WorkloadUpdatePhase.HealthGate, target: true, previous: true);
        var repo = new FakeRepo(intent);
        var providerId = WorkloadProviderId.Parse("hand");
        var provider = Substitute.For<IWorkloadProvider>();
        provider.Descriptor.Returns(new WorkloadProviderDescriptor { Id = providerId, Materializes = materializes });
        var row = WorkloadInstance.Create(Guid.NewGuid(), InstanceId, "group", "gateway", T0);
        row.TransitionTo(WorkloadInstanceState.Deploying, WorkloadStateReason.Provisioning, null, T0);
        row.TransitionTo(WorkloadInstanceState.ExternallyManaged, WorkloadStateReason.ExternalHealthObserved, null, T0);
        row.SetProvider(providerId, T0);
        var instances = Substitute.For<IWorkloadInstanceRepository>();
        instances.ListByPluginInstanceAsync(InstanceId, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<WorkloadInstance>>([row]));
        var resumer = new WorkloadIntentResumer(repo, new MutableClock(T0), NullLogger<WorkloadIntentResumer>.Instance,
            instances, Substitute.For<IWorkloadGroupRepository>(), new WorkloadProviderRegistry([provider]));

        await resumer.ResumeOpenIntentsAsync();

        intent.IsActive.Should().Be(!materializes,
            "a disowned row keeps its materializing provider and ends the update; a connected manual row is only observed");
    }

    private static WorkloadIntentResumer NewResumer(FakeRepo repo)
        => new(repo, new MutableClock(T0), NullLogger<WorkloadIntentResumer>.Instance,
            NSubstitute.Substitute.For<IWorkloadInstanceRepository>(), NSubstitute.Substitute.For<IWorkloadGroupRepository>(), NoProviders);

    private static WorkloadIntentResumer NewCohortResumer(FakeRepo repo)
    {
        var instances = Substitute.For<IWorkloadInstanceRepository>();
        instances.ListByPluginInstanceAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(call => Task.FromResult<IReadOnlyList<WorkloadInstance>>(
                [WorkloadInstance.Create(Guid.NewGuid(), call.Arg<Guid>(), "group", "gateway", T0)]));
        var groups = Substitute.For<IWorkloadGroupRepository>();
        groups.GetAsync("group", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<WorkloadGroup?>(WorkloadGroup.Record("group", "source", "1.0.0", T0)));
        return new WorkloadIntentResumer(repo, new MutableClock(T0), NullLogger<WorkloadIntentResumer>.Instance,
            instances, groups, NoProviders);
    }

    private static WorkloadUpdateIntent ActiveStagedIntent(WorkloadUpdatePhase phase, bool target, bool previous)
        => Staged(phase, target ? TargetMap() : null, previous ? PreviousMap() : null);

    private static Reeve.Pipeline.PipelineJournal ParamsDoneJournal()
        => new(
            [new Reeve.Pipeline.StageEntry(WorkloadUpdateStages.Params, T0,
                Reeve.Pipeline.PipelineDirection.Forward, Reeve.Pipeline.StageEntryState.Done, null)],
            Reeve.Pipeline.PipelineDirection.Forward,
            Reeve.Pipeline.PipelineState.Running);

    private static WorkloadUpdateIntent CohortIntent(bool target)
    {
        var intent = WorkloadUpdateIntent.Create(Guid.NewGuid(), InstanceId, "1.0.0", "1.1.0", T0,
            new WorkloadUpdateCohortPlace(Guid.NewGuid(), 0, 1));
        intent.StageSnapshots(new WorkloadUpdateSnapshots
        {
            TargetSpecs = target ? TargetMap() : null,
            PreviousSpecs = PreviousMap(),
        }, T0);
        intent.RecordStageJournal(Reeve.Pipeline.PipelineJournal.New());
        return intent;
    }

    private static WorkloadUpdateIntent Staged(
        WorkloadUpdatePhase phase,
        IReadOnlyDictionary<string, WorkloadDesiredSpec>? target,
        IReadOnlyDictionary<string, WorkloadDesiredSpec>? previous,
        bool corruptTarget = false,
        bool corruptPrevious = false)
    {
        var intent = WorkloadUpdateIntent.Create(Guid.NewGuid(), InstanceId, "1.0.0", "1.1.0", T0);
        intent.StageSnapshots(new WorkloadUpdateSnapshots { TargetSpecs = target, PreviousSpecs = previous }, T0);
        if (corruptTarget)
        {
            WorkloadActiveIntentProviderTests.MarkTargetDocumentUnreadable(intent);
        }

        if (corruptPrevious)
        {
            WorkloadActiveIntentProviderTests.MarkPreviousDocumentUnreadable(intent);
        }

        switch (phase)
        {
            case WorkloadUpdatePhase.StagingParams:
                break;
            case WorkloadUpdatePhase.ApplyingSidecar:
                intent.AdvanceTo(WorkloadUpdatePhase.ApplyingSidecar, T0);
                break;
            case WorkloadUpdatePhase.HealthGate:
                intent.AdvanceTo(WorkloadUpdatePhase.ApplyingSidecar, T0);
                intent.AdvanceTo(WorkloadUpdatePhase.HealthGate, T0);
                break;
            case WorkloadUpdatePhase.SwappingModule:
                intent.AdvanceTo(WorkloadUpdatePhase.ApplyingSidecar, T0);
                intent.AdvanceTo(WorkloadUpdatePhase.HealthGate, T0);
                intent.AdvanceTo(WorkloadUpdatePhase.SwappingModule, T0);
                break;
            case WorkloadUpdatePhase.RollingBack:
                intent.AdvanceTo(WorkloadUpdatePhase.ApplyingSidecar, T0);
                intent.Fail("mid-rollback crash", T0);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(phase), phase, "unsupported test phase");
        }

        return intent;
    }

    private static Dictionary<string, WorkloadDesiredSpec> TargetMap() => new(StringComparer.Ordinal)
    {
        ["gateway"] = WorkloadActiveIntentProviderTests.Spec("gateway"),
    };

    private static Dictionary<string, WorkloadDesiredSpec> PreviousMap() => new(StringComparer.Ordinal)
    {
        ["gateway"] = WorkloadActiveIntentProviderTests.Spec("gateway"),
    };

    private sealed class FakeRepo(params WorkloadUpdateIntent[] open) : IWorkloadUpdateIntentRepository
    {
        private readonly List<WorkloadUpdateIntent> _open = open.ToList();
        public List<WorkloadUpdateIntent> Updated { get; } = new();
        public int CohortDecisionSaves { get; private set; }

        public Task<IReadOnlyList<WorkloadUpdateIntent>> GetCohortAsync(Guid cohortId, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<WorkloadUpdateIntent>>(_open.Where(i => i.CohortId == cohortId).ToList());

        public Task SaveCohortDecisionAsync(WorkloadUpdateIntent intent, WorkloadGroup group, CancellationToken ct = default)
        {
            CohortDecisionSaves++;
            Updated.Add(intent);
            return Task.CompletedTask;
        }
        public Guid? ThrowOnUpdateFor { get; init; }

        public Task<IReadOnlyList<WorkloadUpdateIntent>> GetOpenIntentsAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<WorkloadUpdateIntent>>(_open.Where(i => i.IsActive).ToList());

        public Task UpdateAsync(WorkloadUpdateIntent intent, CancellationToken ct = default)
        {
            if (ThrowOnUpdateFor is { } id && intent.Id == id)
            {
                throw new InvalidOperationException("simulated store fault");
            }

            Updated.Add(intent);
            return Task.CompletedTask;
        }

        public Task<WorkloadUpdateIntent?> GetActiveForInstanceAsync(Guid pluginInstanceId, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<Guid?> GetActiveIntentIdAsync(Guid pluginInstanceId, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<WorkloadUpdateIntent?> GetLatestForInstanceAsync(Guid pluginInstanceId, CancellationToken ct = default)
            => Task.FromResult(_open.Where(i => i.GroupInstanceId == pluginInstanceId).LastOrDefault());

        public Task<WorkloadUpdateIntent?> GetByIdAsync(Guid id, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<IReadOnlyList<WorkloadUpdateIntent>> GetRecentForInstanceAsync(Guid pluginInstanceId, int limit, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task AddAsync(WorkloadUpdateIntent intent, CancellationToken ct = default)
            => throw new NotSupportedException();
    }

    private sealed class MutableClock(DateTimeOffset now) : IWorkloadClock
    {
        public DateTimeOffset UtcNow { get; private set; } = now;
        public void Set(DateTimeOffset value) => UtcNow = value;
    }
}
