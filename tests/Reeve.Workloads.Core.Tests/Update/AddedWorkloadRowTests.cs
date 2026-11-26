using Microsoft.AspNetCore.DataProtection;
using System.Text.Json;
using FluentAssertions;
using FluentAssertions.Execution;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Reeve.Workloads.Abstractions;
using Reeve.Workloads.Abstractions.Contracts.Deployment;
using Reeve.Workloads.Abstractions.Domain;
using Reeve.Workloads.Abstractions.Domain.DesiredSpec;
using Reeve.Workloads.Abstractions.Domain.Entities;
using Reeve.Workloads.Abstractions.Providers;
using Reeve.Workloads.Abstractions.Update;
using Reeve.Workloads.Core.Groups;
using Reeve.Workloads.Core.Providers;
using Reeve.Workloads.Core.Tests.Groups;
using Reeve.Workloads.Core.Update;
using Reeve.Workloads.Core.Update.Stages;
using Reeve.Workloads.Providers.Kubernetes;
using Xunit;

namespace Reeve.Workloads.Core.Tests.Update;

public sealed class AddedWorkloadRowTests
{
    private const string GroupId = "acme.zwave";
    private const string Existing = "controller";
    private const string Added = "ui";
    private const string From = "1.0.0";
    private const string To = "1.1.0";
    private const string Next = "1.2.0";
    private const string Dropping = "1.3.0";
    private static readonly Guid GroupInstanceId = new("11111111-2222-3333-4444-555555555555");
    private static readonly DateTimeOffset At = new(2026, 9, 23, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task A_completed_update_leaves_a_row_for_the_workload_it_added()
    {
        var h = new Walk();

        var intent = await h.StageAsync(To, new Dictionary<string, string?> { ["theme"] = "light" });
        await h.RunToTerminalAsync(intent);

        intent.Phase.Should().Be(WorkloadUpdatePhase.Completed);
        var applied = h.AppliedAdded.Single();
        var row = h.Instances.Instances.Should().ContainSingle(row => row.WorkloadId == Added,
                "the engine, uninstall and the next update find a workload only through its row")
            .Which;
        using var scope = new AssertionScope();
        row.Id.Should().Be(applied.InstanceId, "the row must name the workload the update applied");
        row.GroupId.Should().Be(GroupId);
        row.Provider.Should().Be(applied.Provider,
            "a row with no provider reads as never deployed, and uninstall would skip tearing down a running workload");
        row.State.Should().Be(WorkloadInstanceState.PendingConfiguration,
            "the engine walks the row from where a materialized row starts; the update records no health on it");
        row.AppliedSpecHash.Should().BeNull("the update writes no row's applied hash; the engine records its own apply");
        row.GetResolvedParameters().Should().ContainKey("theme").WhoseValue.Should().Be("light",
            "the params commit at Completed writes the added workload's row like every other");
        row.ReferenceRolloutState.Should().NotBeNull("the update's successful direct apply establishes the unpinned-reference baseline even though the row is created after that apply");
        row.ReferenceRolloutState.Should().NotContain("network-key", "only the keyed fingerprint and protected HMAC key are persisted");
    }

    [Fact]
    public async Task The_added_workloads_row_keeps_no_author_default_as_the_operators_value()
    {
        var h = new Walk();

        await h.UpdateToCompletionAsync(To);

        h.Instances.Instances.Single(row => row.WorkloadId == Added).GetResolvedParameters().Should()
            .NotContainKey("theme", "as on a fresh install, a default the operator never set stays the author's");
    }

    [Fact]
    public async Task The_row_exists_while_the_swap_lags_and_the_plan_rebuilt_over_it_renders_the_applied_target()
    {
        var h = new Walk();
        h.HostStage.Enqueue(Reeve.Pipeline.StageOutcome.Working("waiting on replica-b"));
        var intent = await h.StageAsync(To, new Dictionary<string, string?> { ["theme"] = "light" });

        await h.AdvanceUntilAsync(intent, () => h.HostStage.Calls.Count == 1);

        intent.CurrentStage.Should().Be(ScriptedHostStage.StageId);
        var applied = h.AppliedAdded.Single();
        var row = h.Instances.Instances.Should().ContainSingle(row => row.WorkloadId == Added).Which;
        var rebuilt = await h.Planner.BuildPlanAsync(
            GroupInstanceId, To, intent.Id, intent.ReadOperatorParameters(), intent.GetAdoptedResourceWorkloadIds());
        using (new AssertionScope())
        {
            row.Id.Should().Be(applied.InstanceId);
            row.GetResolvedParameters().Should().BeEmpty("parameters commit only at Completed");
            rebuilt.Workloads.Single(w => w.WorkloadId == Added).TargetSpec.SpecHash.Should().Be(applied.SpecHash,
                "a target that moved once the row appeared would be a new spec for the engine to converge on");
        }

        await h.RunToTerminalAsync(intent);
        intent.Phase.Should().Be(WorkloadUpdatePhase.Completed);
        h.Instances.Added.Where(row => row.WorkloadId == Added).Should().ContainSingle(
            "a row that exists is never inserted again");
        row.GetResolvedParameters().Should().ContainKey("theme").WhoseValue.Should().Be("light",
            "the params commit at Completed writes the operator's value onto the new row");
    }

    [Fact]
    public async Task An_uninstall_before_the_commit_is_retried_gets_no_row_back()
    {
        var h = new Walk();
        h.Effects.FailNextCommit = true;
        var intent = await h.StageAsync(To);
        await h.AdvanceUntilAsync(intent, () => h.Effects.FailedCommits == 1);
        h.Instances.Instances.Should().Contain(row => row.WorkloadId == Added, "the row the test uninstalls must exist");

        foreach (var row in h.Instances.Instances)
        {
            row.TransitionTo(WorkloadInstanceState.Terminating, WorkloadStateReason.Uninstalling, null, At);
        }

        h.Instances.Instances.RemoveAll(row => row.WorkloadId == Added);
        await h.TickAsync(intent);

        h.Effects.CommitCount.Should().Be(1, "the retried commit ran");
        h.Instances.Added.Should().ContainSingle("the row the teardown deleted is not created again")
            .Which.WorkloadId.Should().Be(Added);
    }

    [Fact]
    public async Task A_workload_whose_row_is_deleted_mid_walk_gets_no_row_back()
    {
        var h = new Walk();
        h.Effects.FailNextCommit = true;
        var intent = await h.StageAsync(To);
        await h.AdvanceUntilAsync(intent, () => h.Effects.FailedCommits == 1);

        h.Instances.Instances.RemoveAll(row => row.WorkloadId == Existing);
        await h.TickAsync(intent);

        h.Effects.CommitCount.Should().Be(1, "the retried commit ran");
        h.Instances.Added.Should().ContainSingle().Which.WorkloadId.Should().Be(Added,
            "only the workload this intent added gets a row");
    }

    [Fact]
    public async Task A_group_commit_that_fails_after_the_rows_landed_is_retried_without_a_second_row()
    {
        var h = new Walk();
        h.Effects.FailNextCommit = true;

        var intent = await h.UpdateToCompletionAsync(To);

        intent.Phase.Should().Be(WorkloadUpdatePhase.Completed);
        h.Effects.FailedCommits.Should().Be(1, "the test must actually fail a group commit");
        h.Instances.Added.Where(row => row.WorkloadId == Added).Should().ContainSingle()
            .Which.Id.Should().Be(h.AppliedAdded.Single().InstanceId);
    }

    [Fact]
    public async Task A_later_update_changes_the_added_workload_in_place()
    {
        var h = new Walk();
        await h.UpdateToCompletionAsync(To);
        var first = h.AppliedAdded.Single().InstanceId;

        var later = await h.UpdateToCompletionAsync(Next);

        later.Phase.Should().Be(WorkloadUpdatePhase.Completed);
        using var scope = new AssertionScope();
        h.AppliedAdded.Select(spec => spec.InstanceId).Distinct()
            .Should().Equal([first], "an update that applies the workload under another id starts a second copy");
        h.Provider.TornDownReferences.Should().BeEmpty();
    }

    [Fact]
    public async Task A_later_update_that_drops_the_workload_tears_its_row_down_at_the_end_of_the_walk()
    {
        var h = new Walk();
        await h.UpdateToCompletionAsync(To);
        var row = h.Instances.Instances.Single(instance => instance.WorkloadId == Added);

        var dropping = await h.UpdateToCompletionAsync(Dropping);

        dropping.Phase.Should().Be(WorkloadUpdatePhase.Completed);
        var call = h.Teardown.Calls.Should().ContainSingle().Which;
        call.Row.Id.Should().Be(row.Id);
        call.Declaration!.Id.Should().Be(Added, "the volumes it keeps are the ones the version it ran declared");
        call.Version.Should().Be(Dropping);
        h.Instances.Instances.Single(instance => instance.WorkloadId == Existing).State
            .Should().NotBe(WorkloadInstanceState.Terminating, "the workload the version keeps is left alone");
    }

    [Fact]
    public async Task A_dropping_update_that_rolls_back_before_its_last_stage_tears_nothing_down()
    {
        var h = new Walk();
        await h.UpdateToCompletionAsync(To);
        h.Gate.VerdictsByWorkload[Existing] = new Queue<WorkloadHealthGateVerdict>([WorkloadHealthGateVerdict.Failed]);

        var dropping = await h.StageAsync(Dropping);
        await h.RunToTerminalAsync(dropping);

        dropping.Phase.Should().Be(WorkloadUpdatePhase.RolledBack);
        h.Teardown.Calls.Should().BeEmpty("the previous version still runs the workload, so a rollback keeps it");
    }

    [Fact]
    public async Task A_later_update_that_rolls_back_restores_the_added_workload_it_found()
    {
        var h = new Walk();
        await h.UpdateToCompletionAsync(To);
        var first = h.AppliedAdded.Single().InstanceId;
        h.Gate.VerdictsByWorkload[Added] = new Queue<WorkloadHealthGateVerdict>([WorkloadHealthGateVerdict.Failed]);

        var later = await h.StageAsync(Next);
        await h.RunToTerminalAsync(later);

        later.Outcome.Should().Be(WorkloadUpdateOutcome.RolledBack);
        h.AppliedAdded.Select(spec => spec.InstanceId).Distinct()
            .Should().Equal([first], "the rollback restores the workload that was running, not one it never had");
        h.Credentials.RevokedInstances.Should().BeEmpty("a restored workload keeps the credentials it runs with");
    }

    [Fact]
    public async Task A_rollback_from_the_health_gate_revokes_the_credentials_of_the_workload_it_added()
    {
        var h = new Walk();
        h.Gate.VerdictsByWorkload[Added] = new Queue<WorkloadHealthGateVerdict>([WorkloadHealthGateVerdict.Failed]);

        var intent = await h.UpdateToCompletionAsync(To);

        intent.Outcome.Should().Be(WorkloadUpdateOutcome.RolledBack);
        var applied = h.AppliedAdded.Single();
        using var scope = new AssertionScope();
        h.Instances.Added.Should().NotContain(row => row.WorkloadId == Added,
            "the walk never reached the commit stage, so no row holds the added workload's id");
        h.Provider.TornDownReferences.Should().Contain(WorkloadRef.FromDesiredSpec(applied));
        h.Credentials.RevokedInstances.Should().Equal([applied.InstanceId],
            "the apply minted under the derived id, and nothing else revokes an id no row holds");
    }

    [Fact]
    public async Task A_rollback_that_cannot_revoke_the_added_workloads_credentials_waits_for_the_revocation()
    {
        var h = new Walk();
        h.Gate.VerdictsByWorkload[Added] = new Queue<WorkloadHealthGateVerdict>([WorkloadHealthGateVerdict.Failed]);
        h.Credentials.Throws = true;
        var intent = await h.StageAsync(To);

        await h.AdvanceUntilAsync(intent, () => h.Provider.TornDownReferences.Count > 0);
        await h.TickAsync(intent);

        intent.IsTerminal.Should().BeFalse("a rollback that leaves a live credential behind is not finished");
        h.Credentials.Throws = false;
        await h.RunToTerminalAsync(intent);

        intent.Outcome.Should().Be(WorkloadUpdateOutcome.RolledBack);
        h.Credentials.RevokedInstances.Should().Equal([h.AppliedAdded.Single().InstanceId]);
    }

    [Fact]
    public async Task A_rollback_past_the_commit_removes_the_row_the_update_added()
    {
        var h = new Walk(reversibleHostCheck: true);
        var existing = h.Instances.Instances.Single(row => row.WorkloadId == Existing);

        var intent = await h.UpdateToCompletionAsync(To);

        intent.Outcome.Should().Be(WorkloadUpdateOutcome.RolledBack);
        var applied = h.AppliedAdded.Single();
        using var scope = new AssertionScope();
        h.Instances.Added.Should().ContainSingle(row => row.WorkloadId == Added, "the commit stage did create it");
        h.Instances.Deleted.Should().ContainSingle().Which.Id.Should().Be(applied.InstanceId);
        h.Instances.Instances.Should().ContainSingle().Which.Should().BeSameAs(existing);
        h.Groups.Rows.Single(group => group.GroupId == GroupId).InstalledVersion.Should().Be(From);
        h.Provider.TornDownReferences.Should().Contain(WorkloadRef.FromDesiredSpec(applied));
        h.Credentials.RevokedInstances.Should().Equal([applied.InstanceId],
            "the sidecar's unwind runs after the row is gone and revokes the id the apply minted under");
    }

    private sealed class Walk
    {
        private readonly InMemoryWorkloadGroupRepository modules = new();
        private readonly MutableClock clock = new(At);
        private readonly InMemoryIntentRepository intents = new();
        private readonly UnpinnedReferenceRolloutGuard rolloutGuard =
            new(new EphemeralDataProtectionProvider());

        public InMemoryWorkloadGroupRepository Groups => modules;

        public Walk(bool reversibleHostCheck = false)
        {
            var manifests = new FakeManifests();
            manifests.AtVersion[From] = new GroupManifestRead(Manifest(From, Existing), Refused: false);
            manifests.AtVersion[To] = new GroupManifestRead(Manifest(To, Existing, Added), Refused: false);
            manifests.AtVersion[Next] = new GroupManifestRead(Manifest(Next, Existing, Added), Refused: false);
            manifests.AtVersion[Dropping] = new GroupManifestRead(Manifest(Dropping, Existing), Refused: false);
            modules.Seed(GroupId, FakeManifests.SourceKey, From);
            Instances.Instances.Add(WorkloadInstance.Create(Guid.NewGuid(), GroupInstanceId, GroupId, Existing, At));

            Planner = new GroupManifestUpdatePlanner(
                manifests,
                modules,
                Instances,
                new DefaultSettings(KubernetesProvider.Id),
                new NoIdentity(),
                new WorkloadProviderRegistry([]),
                new WorkloadResourceNamerRegistry([]),
                new FakeHostBootstrapSource(),
                Options.Create(new ProductImageOptions()),
                NullLogger<GroupManifestUpdatePlanner>.Instance,
                [],
                new WorkloadParameterSecretCodec(new EphemeralDataProtectionProvider()),
                new AddedWorkloadReferenceResolver());

            var log = new CallLog();
            Provider = new RecordingProvider(KubernetesProvider.Id, log);
            Gate = new ScriptedHealthGate(log);
            HostStage = new ScriptedHostStage(log);
            Effects = new FlakyCommitEffects(new WorkloadUpdateEffects(
                new RowParameterWriter(Instances), modules, clock, NullLogger<WorkloadUpdateEffects>.Instance,
                new WorkloadParameterSecretCodec(new EphemeralDataProtectionProvider())));
            var registry = new FakeProviderRegistry(Provider);
            var options = new WorkloadUpdateOrchestratorOptions();
            var rows = new WorkloadUpdateRowEffects(Instances, clock, NullLogger<WorkloadUpdateRowEffects>.Instance,
                rolloutGuard, new NoWorkloadDiagnosticsSinkFactory(), Teardown);
            Reeve.Pipeline.IStage<WorkloadUpdateStageContext> host = reversibleHostCheck
                ? new FailingReversibleStage(log)
                : HostStage;
            Pipeline = WorkloadUpdatePipeline.Resolve(
            [
                new ParamsStage(Effects),
                new SidecarStage(registry, Gate, clock, options, Instances, rolloutGuard, Credentials),
                new HealthGateStage(Gate, clock, options),
                new OperatorRedeployStage(registry, new FakeObservedWorkloadVersionReader(), new Reeve.Workloads.Core.Health.WorkloadHealthContributorDispatch([]),
                    new SpyRepairsFactory(new SpyIssueRegistry()), clock, NullLogger<OperatorRedeployStage>.Instance),
                new CommitVersionStage(Effects, rows),
                host,
                new CommitParamsStage(Effects, rows),
            ]);
            Orchestrator = new WorkloadUpdateOrchestrator(
                intents, Planner, Pipeline, clock, NullLogger<WorkloadUpdateOrchestrator>.Instance,
                new Reeve.Workloads.Core.Tests.Hosting.FakeHostApplicationLifetime());
        }

        public FakeInstances Instances { get; } = new();

        public RecordingDroppedTeardown Teardown { get; } = new();

        public IWorkloadUpdatePlanner Planner { get; }

        public RecordingProvider Provider { get; }

        public FakeWorkloadCredentialPort Credentials { get; } = new();

        public ScriptedHealthGate Gate { get; }

        public ScriptedHostStage HostStage { get; }

        public FlakyCommitEffects Effects { get; }

        public WorkloadUpdateOrchestrator Orchestrator { get; }

        public Reeve.Pipeline.Pipeline<WorkloadUpdateStageContext> Pipeline { get; }

        public Exception? LastError { get; private set; }

        public IEnumerable<WorkloadDesiredSpec> AppliedAdded => Provider.AppliedSpecs.Where(spec => spec.WorkloadId == Added);

        public async Task<WorkloadUpdateIntent> StageAsync(
            string version, IReadOnlyDictionary<string, string?>? operatorParameters = null)
        {
            var judged = await Planner.BuildPlanAsync(GroupInstanceId, version, intentId: null, operatorParameters);
            var initiator = new WorkloadUpdateInitiator(
                intents, Planner, Instances, new RecordingNudge(), Pipeline, clock, NullLogger<WorkloadUpdateInitiator>.Instance);
            var result = await initiator.InitiateAsync(new WorkloadUpdateInitiationRequest
            {
                PluginInstanceId = GroupInstanceId,
                CandidateVersion = version,
                Precondition = new WorkloadWritePrecondition { RequirementsFingerprint = judged.RequirementsFingerprint },
                OperatorParameters = operatorParameters,
            });
            return (await intents.GetByIdAsync(result.IntentId!.Value))!;
        }

        public async Task TickAsync(WorkloadUpdateIntent intent)
        {
            try
            {
                await Orchestrator.AdvanceAsync(intent);
            }
            catch (InvalidOperationException ex)
            {
                LastError = ex;
            }

            clock.Advance(TimeSpan.FromSeconds(5));
        }

        public async Task AdvanceUntilAsync(WorkloadUpdateIntent intent, Func<bool> reached)
        {
            for (var tick = 0; tick < 30 && !reached(); tick++)
            {
                await TickAsync(intent);
            }

            reached().Should().BeTrue("the walk must reach the point the test looks at");
        }

        public async Task RunToTerminalAsync(WorkloadUpdateIntent intent)
        {
            for (var tick = 0; tick < 40 && !intent.IsTerminal; tick++)
            {
                await TickAsync(intent);
            }
        }

        public async Task<WorkloadUpdateIntent> UpdateToCompletionAsync(string version)
        {
            var intent = await StageAsync(version);
            await RunToTerminalAsync(intent);
            return intent;
        }
    }

    private sealed class FlakyCommitEffects(IWorkloadUpdateEffects inner) : IWorkloadUpdateEffects
    {
        public bool FailNextCommit { get; set; }

        public int FailedCommits { get; private set; }

        public int CommitCount { get; private set; }

        public Task CommitInstalledVersionAsync(string moduleId, string targetVersion, CancellationToken cancellationToken = default)
        {
            if (FailNextCommit)
            {
                FailNextCommit = false;
                FailedCommits++;
                throw new InvalidOperationException("The group commit was lost.");
            }

            CommitCount++;
            return inner.CommitInstalledVersionAsync(moduleId, targetVersion, cancellationToken);
        }

        public Task CommitParamsAsync(WorkloadUpdatePlan plan, CancellationToken cancellationToken = default)
            => inner.CommitParamsAsync(plan, cancellationToken);

        public Task RestorePreviousParamsAsync(WorkloadUpdateIntent intent, CancellationToken cancellationToken = default)
            => inner.RestorePreviousParamsAsync(intent, cancellationToken);
    }

    private sealed class RowParameterWriter(FakeInstances instances) : IWorkloadResolvedParameterWriter
    {
        public Task<int> SetResolvedParametersAsync(
            Guid moduleInstanceId,
            IReadOnlyDictionary<string, IReadOnlyDictionary<string, string?>> byWorkloadId,
            DateTimeOffset nowUtc,
            CancellationToken cancellationToken = default)
        {
            var written = 0;
            foreach (var row in instances.Instances.Where(row => row.GroupInstanceId == moduleInstanceId))
            {
                if (byWorkloadId.TryGetValue(row.WorkloadId, out var resolved))
                {
                    row.SetResolvedParameters(resolved, nowUtc);
                    written++;
                }
            }

            return Task.FromResult(written);
        }
    }

    private static DeploymentManifest Manifest(string version, params string[] workloadIds) => new()
    {
        SchemaVersion = 1,
        Workloads = [.. workloadIds.Select(id => Workload(id, version))],
    };

    private static WorkloadManifest Workload(string workloadId, string version) => new()
    {
        Id = workloadId,
        DisplayName = workloadId,
        Image = new WorkloadImage
        {
            Ref = "registry.example/zwave",
            Digest = new string(version[^1] switch { '0' => 'a', '1' => 'b', _ => 'c' }, 64),
        },
        Kind = WorkloadKind.Service,
        Health = new WorkloadHealth { Mode = WorkloadHealthMode.Container },
        Parameters = workloadId == Added
            ? [
                new WorkloadParameter { Name = "theme", Type = WorkloadParameterType.Text, Default = "dark" },
                new WorkloadParameter { Name = "dbPassword", Type = WorkloadParameterType.Password, Default = "ref:vault:zwave#network-key" },
            ]
            : [],
        Extensions = new WorkloadExtensions
        {
            ByNamespace = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
            {
                ["reeve"] = JsonDocument
                    .Parse("""{"protocol":{"version":"1","minSupported":"1"}}""").RootElement.Clone(),
            },
        },
    };


    private sealed class AddedWorkloadReferenceResolver : IWorkloadParameterReferenceResolver
    {
        public ValueTask<WorkloadReferenceResolution> ResolveAsync(string reference, CancellationToken cancellationToken = default)
            => ValueTask.FromResult<WorkloadReferenceResolution>(new WorkloadReferenceResolution.Success("network-key"));
    }
}
