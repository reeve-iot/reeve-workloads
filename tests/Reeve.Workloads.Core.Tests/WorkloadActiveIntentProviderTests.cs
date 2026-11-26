using FluentAssertions;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Reeve.Workloads.Abstractions;
using Reeve.Workloads.Abstractions.Contracts;
using Reeve.Workloads.Abstractions.Contracts.Deployment;
using Reeve.Workloads.Abstractions.Domain.DesiredSpec;
using Reeve.Workloads.Abstractions.Domain.Entities;
using Reeve.Workloads.Abstractions.Domain.Entities.Documents;
using Reeve.Workloads.Abstractions.Update;
using Reeve.Workloads.Core;
using Reeve.Workloads.Providers.Kubernetes;
using Xunit;

namespace Reeve.Workloads.Core.Tests;

public sealed class WorkloadActiveIntentProviderTests
{
    private static readonly Guid InstanceId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");

    [Fact]
    public async Task TryGetActiveReplay_returns_submitted_parameters_while_moving_forward()
    {
        var intent = StagedIntent(InstanceId, new Dictionary<string, string?> { ["password"] = "submitted" }, "gateway");
        var provider = new WorkloadActiveIntentProvider(new FakeRepo(intent));

        var replay = await provider.TryGetActiveReplayAsync(InstanceId, "gateway", CancellationToken.None);

        replay!.Direction.Should().Be(WorkloadUpdateDirection.Forward);
        replay.SubmittedParameters.Should().Contain("password", "submitted");
    }

    [Fact]
    public async Task TryGetActiveReplay_returns_candidate_password_names_even_without_submitted_values()
    {
        var intent = StagedIntent(InstanceId, "gateway");
        var provider = new WorkloadActiveIntentProvider(new FakeRepo(intent));
        intent.StageSnapshots(new WorkloadUpdateSnapshots
        {
            TargetSpecs = new Dictionary<string, WorkloadDesiredSpec> { ["gateway"] = Spec("gateway") },
            PreviousSpecs = new Dictionary<string, WorkloadDesiredSpec> { ["gateway"] = Spec("gateway") },
            CandidatePasswordParameterNames = new Dictionary<string, IReadOnlyList<string>>
            {
                ["gateway"] = ["networkKey"],
            },
        }, DateTimeOffset.UtcNow);

        var replay = await provider.TryGetActiveReplayAsync(InstanceId, "gateway", CancellationToken.None);

        replay!.SubmittedParameters.Should().BeEmpty();
        replay.PasswordParameterNames.Should().Contain("networkKey");
    }

    [Fact]
    public async Task TryGetActiveReplay_empties_submitted_parameters_when_the_intent_compensates()
    {
        var intent = StagedIntent(InstanceId, new Dictionary<string, string?> { ["password"] = "submitted" }, "gateway");
        intent.Fail("health gate failed", DateTimeOffset.UtcNow);
        intent.RecordStageJournal(new Reeve.Pipeline.PipelineJournal(
            [new Reeve.Pipeline.StageEntry("stage.params", DateTimeOffset.UtcNow, Reeve.Pipeline.PipelineDirection.Forward,
                Reeve.Pipeline.StageEntryState.Done, null)],
            Reeve.Pipeline.PipelineDirection.Compensating,
            Reeve.Pipeline.PipelineState.Running));
        var provider = new WorkloadActiveIntentProvider(new FakeRepo(intent));

        var replay = await provider.TryGetActiveReplayAsync(InstanceId, "gateway", CancellationToken.None);

        replay!.Direction.Should().Be(WorkloadUpdateDirection.RollingBack);
        replay.SubmittedParameters.Should().BeEmpty();
    }

    [Fact]
    public async Task TryGetActiveTargetSpec_returns_the_staged_target_for_that_workload()
    {
        var intent = StagedIntent(InstanceId, "gateway", "metrics");
        var provider = new WorkloadActiveIntentProvider(new FakeRepo(intent));

        var spec = await provider.TryGetActiveTargetSpecAsync(InstanceId, "gateway", CancellationToken.None);

        spec.Should().NotBeNull("an active intent with a staged target IS the desired spec (converge-to-target)");
        spec!.WorkloadId.Should().Be("gateway");
    }

    [Fact]
    public async Task TryGetActiveTargetSpec_returns_null_when_the_workloadId_is_absent_from_the_intent()
    {
        var intent = StagedIntent(InstanceId, "gateway");
        var provider = new WorkloadActiveIntentProvider(new FakeRepo(intent));

        var spec = await provider.TryGetActiveTargetSpecAsync(InstanceId, "metrics", CancellationToken.None);

        spec.Should().BeNull("a workload not part of this intent has no intent-target ⇒ resolver computes fresh");
    }

    [Fact]
    public async Task TryGetActiveTargetSpec_returns_null_when_no_active_intent()
    {
        var provider = new WorkloadActiveIntentProvider(new FakeRepo(activeIntent: null));

        var spec = await provider.TryGetActiveTargetSpecAsync(InstanceId, "gateway", CancellationToken.None);

        spec.Should().BeNull();
    }

    [Fact]
    public async Task Cohort_member_without_a_turn_target_replays_nothing()
    {
        var intent = WorkloadUpdateIntent.Create(Guid.NewGuid(), InstanceId, "1.0.0", "1.1.0",
            DateTimeOffset.UtcNow, new WorkloadUpdateCohortPlace(Guid.NewGuid(), 1, 2));
        intent.StageSnapshots(new WorkloadUpdateSnapshots
        {
            PreviousSpecs = new Dictionary<string, WorkloadDesiredSpec>
            {
                ["gateway"] = Spec("gateway"),
            },
        }, DateTimeOffset.UtcNow);
        var provider = new WorkloadActiveIntentProvider(new FakeRepo(intent));

        var replay = await provider.TryGetActiveReplayAsync(InstanceId, "gateway", CancellationToken.None);
        var target = await provider.TryGetActiveTargetSpecAsync(InstanceId, "gateway", CancellationToken.None);

        replay.Should().BeNull("the member has not reached its turn and has no target snapshot");
        target.Should().BeNull();
    }

    [Fact]
    public async Task TryGetActiveTargetSpec_returns_null_when_intent_has_no_staged_target_yet()
    {
        var intent = WorkloadUpdateIntent.Create(Guid.NewGuid(), InstanceId, "1.0.0", "1.1.0", DateTimeOffset.UtcNow);
        var provider = new WorkloadActiveIntentProvider(new FakeRepo(intent));

        var spec = await provider.TryGetActiveTargetSpecAsync(InstanceId, "gateway", CancellationToken.None);

        spec.Should().BeNull("no target staged yet ⇒ no intent-target ⇒ correct to compute fresh");
    }

    [Fact]
    public async Task TryGetActiveTargetSpec_returns_null_when_the_active_intent_target_is_corrupt_and_does_not_throw()
    {
        var intent = StagedIntent(InstanceId, "gateway");
        MarkTargetDocumentUnreadable(intent);
        var provider = new WorkloadActiveIntentProvider(new FakeRepo(intent));

        var act = async () => await provider.TryGetActiveTargetSpecAsync(InstanceId, "gateway", CancellationToken.None);

        var spec = await act.Should().NotThrowAsync("a corrupt in-flight target must not crash the reconcile pass");
        spec.Which.Should().BeNull("a corrupt target ⇒ compute fresh, not a throw");
    }

    [Fact]
    public async Task TryGetActiveTargetSpec_returns_the_previous_spec_while_the_intent_rolls_back()
    {
        var intent = UpdateIntent(InstanceId, target: ["gateway"], previous: ["gateway"]);
        intent.Fail("health gate failed", DateTimeOffset.UtcNow);
        var provider = new WorkloadActiveIntentProvider(new FakeRepo(intent));

        var spec = await provider.TryGetActiveTargetSpecAsync(InstanceId, "gateway", CancellationToken.None);

        spec.Should().NotBeNull("a rolling-back intent still owns the desired spec — the fresh compute is not it");
        spec!.SpecHash.Should().Be(PreviousHash("gateway"), "while RollingBack the desired spec is the one being restored");
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public async Task TryGetActiveTargetSpec_follows_the_journal_not_the_abandon_while_rolling_back(bool turned, bool previous)
    {
        var intent = UpdateIntent(InstanceId, target: ["gateway"], previous: ["gateway"]);
        intent.RecordStageJournal(new Reeve.Pipeline.PipelineJournal(
            [new Reeve.Pipeline.StageEntry("stage.params", DateTimeOffset.UtcNow, Reeve.Pipeline.PipelineDirection.Forward,
                Reeve.Pipeline.StageEntryState.Done, null)],
            turned ? Reeve.Pipeline.PipelineDirection.Compensating : Reeve.Pipeline.PipelineDirection.Forward,
            Reeve.Pipeline.PipelineState.Running));
        intent.Abandon("operator gave up", DateTimeOffset.UtcNow);
        var provider = new WorkloadActiveIntentProvider(new FakeRepo(intent));

        var spec = await provider.TryGetActiveTargetSpecAsync(InstanceId, "gateway", CancellationToken.None);

        spec!.SpecHash.Should().Be(previous ? PreviousHash("gateway") : Spec("gateway").SpecHash);
    }

    [Fact]
    public async Task TryGetActiveTargetSpec_returns_the_target_again_on_the_forward_phases()
    {
        var intent = UpdateIntent(InstanceId, target: ["gateway"], previous: ["gateway"]);
        intent.AdvanceTo(WorkloadUpdatePhase.ApplyingSidecar, DateTimeOffset.UtcNow);
        intent.AdvanceTo(WorkloadUpdatePhase.HealthGate, DateTimeOffset.UtcNow);
        var provider = new WorkloadActiveIntentProvider(new FakeRepo(intent));

        var spec = await provider.TryGetActiveTargetSpecAsync(InstanceId, "gateway", CancellationToken.None);

        spec!.SpecHash.Should().Be(Spec("gateway").SpecHash, "a long HealthGate must still converge on the target");
    }

    [Fact]
    public async Task TryGetActiveTargetSpec_returns_null_while_rolling_back_a_workload_the_candidate_added()
    {
        var intent = UpdateIntent(InstanceId, target: ["gateway", "metrics"], previous: ["gateway"]);
        intent.Fail("health gate failed", DateTimeOffset.UtcNow);
        var provider = new WorkloadActiveIntentProvider(new FakeRepo(intent));

        var spec = await provider.TryGetActiveTargetSpecAsync(InstanceId, "metrics", CancellationToken.None);

        spec.Should().BeNull("a candidate-added workload has nothing to restore");
    }

    [Fact]
    public async Task TryGetActiveTargetSpec_returns_null_not_the_target_when_the_previous_document_is_unreadable()
    {
        var intent = UpdateIntent(InstanceId, target: ["gateway"], previous: ["gateway"]);
        intent.Fail("health gate failed", DateTimeOffset.UtcNow);
        MarkPreviousDocumentUnreadable(intent);
        var provider = new WorkloadActiveIntentProvider(new FakeRepo(intent));

        var spec = await provider.TryGetActiveTargetSpecAsync(InstanceId, "gateway", CancellationToken.None);

        spec.Should().BeNull("an unreadable previous snapshot computes fresh, and never falls back to the target");
    }

    [Fact]
    public async Task GetActiveIntentId_delegates_to_the_repository()
    {
        var intent = StagedIntent(InstanceId, "gateway");
        var provider = new WorkloadActiveIntentProvider(new FakeRepo(intent));

        var id = await provider.GetActiveIntentIdAsync(InstanceId, CancellationToken.None);

        id.Should().Be(intent.Id);
    }

    [Fact]
    public async Task GetActiveIntentId_is_null_when_no_active_intent()
    {
        var provider = new WorkloadActiveIntentProvider(new FakeRepo(activeIntent: null));

        (await provider.GetActiveIntentIdAsync(InstanceId, CancellationToken.None)).Should().BeNull();
    }

    [Fact]
    public async Task TryGetActiveTargetSpec_reads_the_staged_target_document()
    {
        var intent = StagedIntent(InstanceId, "gateway");
        GraftTargetDocument(intent, donor: StagedIntent(InstanceId, "metrics"));
        var provider = new WorkloadActiveIntentProvider(new FakeRepo(intent));

        var spec = await provider.TryGetActiveTargetSpecAsync(InstanceId, "metrics", CancellationToken.None);

        spec.Should().NotBeNull("the staged target is read from the document");
        spec!.WorkloadId.Should().Be("metrics");
    }

    [Fact]
    public async Task TryGetActiveTargetSpec_returns_null_when_the_target_document_is_unreadable()
    {
        var intent = StagedIntent(InstanceId, "gateway");
        MarkTargetDocumentUnreadable(intent);
        var logger = Substitute.For<ILogger<WorkloadActiveIntentProvider>>();
        var provider = new WorkloadActiveIntentProvider(new FakeRepo(intent), logger);

        var spec = await provider.TryGetActiveTargetSpecAsync(InstanceId, "gateway", CancellationToken.None);

        spec.Should().BeNull("an unreadable target ⇒ compute the desired spec fresh rather than fail the pass");
        logger.ReceivedCalls().Should().Contain(call => call.GetMethodInfo().Name == "Log"
            && Equals(call.GetArguments()[0], LogLevel.Error),
            "the consolidated replay lookup still reports corrupt snapshots");
    }

    internal static void GraftTargetDocument(WorkloadUpdateIntent intent, WorkloadUpdateIntent donor)
        => SetTargetDocument(intent, new WorkloadSpecMapDocument
        {
            SourceFingerprint = intent.TargetSpecsDocument!.SourceFingerprint,
            Workloads = donor.TargetSpecsDocument!.Workloads,
        });

    internal static void MarkTargetDocumentUnreadable(WorkloadUpdateIntent intent)
        => SetTargetDocument(intent, new WorkloadSpecMapDocument { Unreadable = true });

    internal static void MarkPreviousDocumentUnreadable(WorkloadUpdateIntent intent)
        => typeof(WorkloadUpdateIntent)
            .GetProperty(nameof(WorkloadUpdateIntent.PreviousSpecsDocument))!
            .SetValue(intent, new WorkloadSpecMapDocument { Unreadable = true });

    private static void SetTargetDocument(WorkloadUpdateIntent intent, WorkloadSpecMapDocument document)
        => typeof(WorkloadUpdateIntent)
            .GetProperty(nameof(WorkloadUpdateIntent.TargetSpecsDocument))!
            .SetValue(intent, document);

    private static WorkloadUpdateIntent StagedIntent(Guid instanceId, params string[] workloadIds)
        => StagedIntent(instanceId, null, workloadIds);

    private static WorkloadUpdateIntent StagedIntent(
        Guid instanceId, IReadOnlyDictionary<string, string?>? operatorParameters, params string[] workloadIds)
    {
        var intent = WorkloadUpdateIntent.Create(Guid.NewGuid(), instanceId, "1.0.0", "1.1.0", DateTimeOffset.UtcNow);
        intent.StageSnapshots(
            new WorkloadUpdateSnapshots
            {
                TargetSpecs = workloadIds.ToDictionary(w => w, Spec, StringComparer.Ordinal),
                PreviousSpecs = workloadIds.ToDictionary(w => w, Spec, StringComparer.Ordinal),
                OperatorParameters = operatorParameters,
            },
            DateTimeOffset.UtcNow);
        return intent;
    }

    private static WorkloadUpdateIntent UpdateIntent(Guid instanceId, string[] target, string[] previous)
    {
        var intent = WorkloadUpdateIntent.Create(Guid.NewGuid(), instanceId, "1.0.0", "1.1.0", DateTimeOffset.UtcNow);
        intent.StageSnapshots(
            new WorkloadUpdateSnapshots
            {
                TargetSpecs = target.ToDictionary(w => w, Spec, StringComparer.Ordinal),
                PreviousSpecs = previous.ToDictionary(
                    w => w, w => Spec(w) with { SpecHash = PreviousHash(w) }, StringComparer.Ordinal),
            },
            DateTimeOffset.UtcNow);
        return intent;
    }

    private static string PreviousHash(string workloadId) => "previous-hash-" + workloadId;

    internal static WorkloadDesiredSpec Spec(string workloadId) => new()
    {
        GroupInstanceId = InstanceId,
        GroupId = "acme.thread",
        WorkloadId = workloadId,
        InstanceId = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"),
        Inst8 = "bbbbbbbb",
        Provider = KubernetesProvider.Id,
        ResourceName = $"reeve-{workloadId}",
        SecretName = $"reeve-{workloadId}-secret",
        Labels = new Dictionary<string, string>(),
        Image = "registry.example.com/acme/thread@sha256:deadbeef",
        Kind = WorkloadKind.Service,
        Health = new WorkloadHealth { Mode = WorkloadHealthMode.Container },
        SpecHash = "hash-" + workloadId,
    };

    private sealed class FakeRepo(WorkloadUpdateIntent? activeIntent) : IWorkloadUpdateIntentRepository
    {
        public Task<WorkloadUpdateIntent?> GetActiveForInstanceAsync(Guid pluginInstanceId, CancellationToken ct = default)
            => Task.FromResult(activeIntent is { IsActive: true } && activeIntent.GroupInstanceId == pluginInstanceId
                ? activeIntent
                : null);

        public Task<Guid?> GetActiveIntentIdAsync(Guid pluginInstanceId, CancellationToken ct = default)
            => Task.FromResult(activeIntent is { IsActive: true } && activeIntent.GroupInstanceId == pluginInstanceId
                ? activeIntent.Id
                : (Guid?)null);

        public Task<WorkloadUpdateIntent?> GetLatestForInstanceAsync(Guid pluginInstanceId, CancellationToken ct = default)
            => Task.FromResult(activeIntent is not null && activeIntent.GroupInstanceId == pluginInstanceId
                ? activeIntent
                : null);

        public Task<IReadOnlyList<WorkloadUpdateIntent>> GetOpenIntentsAsync(CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<WorkloadUpdateIntent?> GetByIdAsync(Guid id, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<IReadOnlyList<WorkloadUpdateIntent>> GetRecentForInstanceAsync(Guid pluginInstanceId, int limit, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task AddAsync(WorkloadUpdateIntent intent, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task UpdateAsync(WorkloadUpdateIntent intent, CancellationToken ct = default)
            => throw new NotSupportedException();
    }
}
