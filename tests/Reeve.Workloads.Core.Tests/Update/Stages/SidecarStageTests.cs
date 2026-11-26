using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using NSubstitute;
using Reeve.Pipeline;
using Reeve.Workloads.Abstractions;
using Reeve.Workloads.Abstractions.Contracts.Deployment;
using Reeve.Workloads.Abstractions.Domain.Entities;
using Reeve.Workloads.Abstractions.Providers;
using Reeve.Workloads.Abstractions.Update;
using Reeve.Workloads.Core;
using Reeve.Workloads.Core.Update;
using Reeve.Workloads.Core.Update.Stages;
using Reeve.Workloads.DeploymentFixtures;
using Reeve.Workloads.Providers.Kubernetes;
using Xunit;

namespace Reeve.Workloads.Core.Tests.Update.Stages;

public sealed class SidecarStageTests
{
    private static readonly WorkloadUpdateOrchestratorOptions Options = new() { RollbackTimeout = TimeSpan.FromMinutes(11) };
    private static readonly DateTimeOffset Now = new(2026, 9, 22, 12, 0, 0, TimeSpan.Zero);

    private sealed record Rig(
        SidecarStage Stage,
        RecordingProvider Provider,
        FakeProviderRegistry Registry,
        ScriptedHealthGate Gate,
        CallLog Log,
        MutableClock Clock,
        FakeWorkloadCredentialPort Credentials);

    private static Rig Build()
    {
        var log = new CallLog();
        var provider = new RecordingProvider(KubernetesProvider.Id, log);
        var registry = new FakeProviderRegistry(provider);
        var gate = new ScriptedHealthGate(log);
        var clock = new MutableClock(new DateTimeOffset(2026, 9, 22, 12, 0, 0, TimeSpan.Zero));
        var credentials = new FakeWorkloadCredentialPort();
        return new Rig(new SidecarStage(registry, gate, clock, Options,
            NSubstitute.Substitute.For<IWorkloadInstanceRepository>(),
            new UnpinnedReferenceRolloutGuard(new EphemeralDataProtectionProvider()), credentials),
            provider, registry, gate, log, clock, credentials);
    }

    private static WorkloadUpdatePlan DeclarationOrderPlan() =>
        WorkloadUpdatePlanBuilder.Plan(
            WorkloadUpdatePlanBuilder.InstanceGuid,
            [
                WorkloadUpdatePlanBuilder.Item("zulu", WorkloadUpdatePlanBuilder.InstanceGuid, "target-zulu", "previous-zulu"),
                WorkloadUpdatePlanBuilder.Item("alpha", WorkloadUpdatePlanBuilder.InstanceGuid, "target-alpha", "previous-alpha"),
            ]);

    [Fact]
    public void It_follows_the_parameters_and_is_bounded_by_the_rollback_window()
    {
        var rig = Build();

        rig.Stage.Id.Should().Be(WorkloadUpdateStages.Sidecar);
        rig.Stage.Placement.AfterId.Should().Be(WorkloadUpdateStages.Params);
        rig.Stage.Reversibility.IsReversible.Should().BeTrue();
        rig.Stage.Budget.Limit.Should().Be(Options.RollbackTimeout);
        rig.Stage.Budget.Policy.Should().Be(StageTimeoutPolicy.FailAndCompensate);
    }

    [Fact]
    public async Task Up_applies_every_workload_in_declaration_order()
    {
        var rig = Build();
        var context = await StageContextFactory.BuildAsync(DeclarationOrderPlan());

        var outcome = await rig.Stage.UpAsync(context, CancellationToken.None);

        outcome.Kind.Should().Be(StageOutcomeKind.Done);
        rig.Provider.AppliedInOrder.Should().Equal("zulu", "alpha");
    }

    [Fact]
    public async Task Up_holds_before_direct_provider_apply_when_unpinned_reference_changed()
    {
        var log = new CallLog();
        var provider = new RecordingProvider(KubernetesProvider.Id, log);
        var registry = new FakeProviderRegistry(provider);
        var clock = new MutableClock(Now);
        var instance = WorkloadInstance.Create(WorkloadUpdatePlanBuilder.InstanceGuid, Guid.NewGuid(), "zwave", "alpha", Now);
        var guard = new UnpinnedReferenceRolloutGuard(new EphemeralDataProtectionProvider());
        var refs = new Dictionary<string, string?> { ["dbPassword"] = "ref:vault:zwave#password" };
        guard.RecordApplied(instance, refs, new Dictionary<string, string?> { ["dbPassword"] = "old" }, ["dbPassword"], Now);
        var repository = Substitute.For<IWorkloadInstanceRepository>();
        repository.GetByIdAsync(instance.Id, Arg.Any<CancellationToken>()).Returns(instance);
        repository.UpdateAsync(instance, Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);

        var workload = DeploymentManifestBuilder.Workload(
            parameters: [DeploymentManifestBuilder.Param("dbPassword", required: false, type: WorkloadParameterType.Password)]) with { Id = "alpha" };
        var item = WorkloadUpdatePlanBuilder.Item("alpha", instance.Id, "target", "previous");
        var plan = WorkloadUpdatePlanBuilder.Plan(instance.Id, [item], candidate: new DeploymentManifest { SchemaVersion = 1, Workloads = [workload] }) with
        {
            CandidateStoredParameters = new Dictionary<string, IReadOnlyDictionary<string, string?>>
            {
                ["alpha"] = new Dictionary<string, string?> { ["dbPassword"] = refs["dbPassword"] },
            },
            CandidateResolvedParameters = new Dictionary<string, IReadOnlyDictionary<string, string?>>
            {
                ["alpha"] = new Dictionary<string, string?> { ["dbPassword"] = "new" },
            },
            CandidateUnpinnedReferences = new Dictionary<string, IReadOnlyDictionary<string, string?>> { ["alpha"] = refs },
        };
        var context = await StageContextFactory.BuildAsync(plan);
        var stage = new SidecarStage(registry, new ScriptedHealthGate(log), clock, Options, repository, guard, new NoCredentialPort());

        var outcome = await stage.UpAsync(context, CancellationToken.None);

        outcome.Kind.Should().Be(StageOutcomeKind.Working);
        provider.AppliedInOrder.Should().BeEmpty();
    }

    [Fact]
    public async Task Down_holds_before_provider_restore_when_previous_unpinned_reference_changed()
    {
        var log = new CallLog();
        var provider = new RecordingProvider(KubernetesProvider.Id, log);
        var registry = new FakeProviderRegistry(provider);
        var clock = new MutableClock(Now);
        var instance = WorkloadInstance.Create(WorkloadUpdatePlanBuilder.InstanceGuid, Guid.NewGuid(), "zwave", "alpha", Now);
        var guard = new UnpinnedReferenceRolloutGuard(new EphemeralDataProtectionProvider());
        var appliedReference = new Dictionary<string, string?> { ["dbPassword"] = "ref:vault:zwave#old" };
        guard.RecordApplied(instance, appliedReference, new Dictionary<string, string?> { ["dbPassword"] = "old-secret" }, ["dbPassword"], Now);
        var previousReference = new Dictionary<string, string?> { ["dbPassword"] = "ref:vault:zwave#previous" };
        var repository = Substitute.For<IWorkloadInstanceRepository>();
        repository.GetByIdAsync(instance.Id, Arg.Any<CancellationToken>()).Returns(instance);
        repository.UpdateAsync(instance, Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        var workload = DeploymentManifestBuilder.Workload(
            parameters: [DeploymentManifestBuilder.Param("dbPassword", required: false, type: WorkloadParameterType.Password)]) with { Id = "alpha" };
        var item = WorkloadUpdatePlanBuilder.Item("alpha", instance.Id, "target", "previous");
        var plan = WorkloadUpdatePlanBuilder.Plan(instance.Id, [item], previous: new DeploymentManifest { SchemaVersion = 1, Workloads = [workload] }) with
        {
            PreviousStoredParameters = new Dictionary<string, IReadOnlyDictionary<string, string?>>
            {
                ["alpha"] = previousReference,
            },
            PreviousResolvedParameters = new Dictionary<string, IReadOnlyDictionary<string, string?>>
            {
                ["alpha"] = new Dictionary<string, string?> { ["dbPassword"] = "previous-secret" },
            },
            PreviousUnpinnedReferences = new Dictionary<string, IReadOnlyDictionary<string, string?>>
            {
                ["alpha"] = previousReference,
            },
        };
        var context = await StageContextFactory.BuildCompensatingAsync(plan);
        var stage = new SidecarStage(registry, new ScriptedHealthGate(log), clock, Options, repository, guard, new NoCredentialPort());

        var outcome = await stage.DownAsync(context, CancellationToken.None);

        outcome.Kind.Should().Be(StageOutcomeKind.Working);
        outcome.Detail.Should().Contain("Password parameter 'dbPassword'");
        provider.AppliedInOrder.Should().BeEmpty("the rollback guard must run before restoring the previous spec");
    }

    private static WorkloadInstance HeldByHost()
    {
        var instance = WorkloadInstance.Create(WorkloadUpdatePlanBuilder.InstanceGuid, Guid.NewGuid(), "zwave", "alpha", Now);
        instance.TransitionTo(WorkloadInstanceState.Deploying, WorkloadStateReason.Provisioning, null, Now);
        instance.TransitionTo(WorkloadInstanceState.Healthy, WorkloadStateReason.HealthObserved, null, Now);
        instance.TransitionTo(WorkloadInstanceState.Suspended, WorkloadStateReason.SuspendedByHost, "withdrawn", Now);
        return instance;
    }

    [Fact]
    public async Task Up_waits_without_applying_a_workload_the_host_holds()
    {
        var log = new CallLog();
        var provider = new RecordingProvider(KubernetesProvider.Id, log);
        var instance = HeldByHost();
        var repository = Substitute.For<IWorkloadInstanceRepository>();
        repository.GetByIdAsync(instance.Id, Arg.Any<CancellationToken>()).Returns(instance);
        var stage = new SidecarStage(new FakeProviderRegistry(provider), new ScriptedHealthGate(log), new MutableClock(Now),
            Options, repository, new UnpinnedReferenceRolloutGuard(new EphemeralDataProtectionProvider()), new NoCredentialPort());
        var context = await StageContextFactory.BuildAsync(DeclarationOrderPlan());

        var outcome = await stage.UpAsync(context, CancellationToken.None);

        outcome.Kind.Should().Be(StageOutcomeKind.Working);
        provider.AppliedInOrder.Should().BeEmpty("an apply would recreate the runtime and mint it a credential");
        instance.IsHeldByHost.Should().BeTrue();
    }

    [Fact]
    public async Task Down_waits_without_restoring_a_workload_the_host_holds()
    {
        var log = new CallLog();
        var provider = new RecordingProvider(KubernetesProvider.Id, log);
        var instance = HeldByHost();
        var repository = Substitute.For<IWorkloadInstanceRepository>();
        repository.GetByIdAsync(instance.Id, Arg.Any<CancellationToken>()).Returns(instance);
        var stage = new SidecarStage(new FakeProviderRegistry(provider), new ScriptedHealthGate(log), new MutableClock(Now),
            Options, repository, new UnpinnedReferenceRolloutGuard(new EphemeralDataProtectionProvider()), new NoCredentialPort());
        var context = await StageContextFactory.BuildCompensatingAsync(DeclarationOrderPlan());

        var outcome = await stage.DownAsync(context, CancellationToken.None);

        outcome.Kind.Should().Be(StageOutcomeKind.Working);
        provider.AppliedInOrder.Should().BeEmpty("a restore would recreate the runtime and mint it a credential");
        instance.IsHeldByHost.Should().BeTrue();
    }

    [Fact]
    public async Task Up_force_adopts_rather_than_letting_a_stray_field_manager_wedge_the_update()
    {
        var rig = Build();
        var context = await StageContextFactory.BuildAsync();

        await rig.Stage.UpAsync(context, CancellationToken.None);

        rig.Provider.ApplyForce.Should().OnlyContain(apply => apply.Force);
    }

    [Fact]
    public async Task Up_skips_a_workload_a_previous_tick_already_applied()
    {
        var rig = Build();
        var context = await StageContextFactory.BuildAsync();

        await rig.Stage.UpAsync(context, CancellationToken.None);
        rig.Provider.AppliedInOrder.Clear();
        await rig.Stage.UpAsync(context, CancellationToken.None);

        rig.Provider.AppliedInOrder.Should().BeEmpty();
    }

    [Fact]
    public async Task Up_records_the_hash_the_provider_actually_applied()
    {
        var rig = Build();
        rig.Provider.ApplyBehaviour["alpha"] = _ => WorkloadApplyResult.Applied("hash-the-provider-chose");
        var context = await StageContextFactory.BuildAsync();

        await rig.Stage.UpAsync(context, CancellationToken.None);

        WorkloadUpdateSteps.Find(context.Internals!.Intent, "alpha")!
            .AppliedSpecHash.Should().Be("hash-the-provider-chose");
    }

    [Fact]
    public async Task Up_keeps_working_when_the_provider_is_not_registered_yet()
    {
        var rig = Build();
        rig.Registry.Provider = null;
        var context = await StageContextFactory.BuildAsync();

        var outcome = await rig.Stage.UpAsync(context, CancellationToken.None);

        outcome.Kind.Should().Be(StageOutcomeKind.Working);
        outcome.Detail.Should().Contain("alpha");
    }

    [Fact]
    public async Task Up_keeps_working_on_a_transient_apply_rather_than_unwinding()
    {
        var rig = Build();
        rig.Provider.ApplyBehaviour["alpha"] = _ =>
            WorkloadApplyResult.Failed(WorkloadApplyReason.ProviderUnreachable, "node is draining");
        var context = await StageContextFactory.BuildAsync();

        var outcome = await rig.Stage.UpAsync(context, CancellationToken.None);

        outcome.Kind.Should().Be(StageOutcomeKind.Working);
    }

    [Fact]
    public async Task Up_fails_on_an_apply_the_candidate_itself_is_responsible_for()
    {
        var rig = Build();
        rig.Provider.ApplyBehaviour["alpha"] = _ =>
            WorkloadApplyResult.Failed(WorkloadApplyReason.Rejected, "the image does not exist");
        var context = await StageContextFactory.BuildAsync();

        var outcome = await rig.Stage.UpAsync(context, CancellationToken.None);

        outcome.Kind.Should().Be(StageOutcomeKind.Failed);
        outcome.Detail.Should().Contain("alpha").And.Contain("the image does not exist");
    }

    [Fact]
    public async Task Down_captures_the_evidence_before_it_re_applies_anything()
    {
        var rig = Build();
        var context = await StageContextFactory.BuildCompensatingAsync();

        await rig.Stage.DownAsync(context, CancellationToken.None);

        var firstLogRead = rig.Log.IndexOfFirst(entry => entry.StartsWith("logs:", StringComparison.Ordinal));
        var firstReapply = rig.Log.IndexOfFirst(entry => entry.StartsWith("apply:", StringComparison.Ordinal));
        firstLogRead.Should().BeGreaterThanOrEqualTo(0);
        firstReapply.Should().BeGreaterThanOrEqualTo(0);
        firstLogRead.Should().BeLessThan(firstReapply);
    }

    [Fact]
    public async Task Down_captures_the_evidence_once_and_not_again_on_the_next_tick()
    {
        var rig = Build();
        rig.Gate.DefaultVerdict = WorkloadHealthGateVerdict.Pending;
        var context = await StageContextFactory.BuildCompensatingAsync();

        await rig.Stage.DownAsync(context, CancellationToken.None);
        var afterFirst = rig.Log.Entries.Count(entry => entry.StartsWith("logs:", StringComparison.Ordinal));
        await rig.Stage.DownAsync(context, CancellationToken.None);
        var afterSecond = rig.Log.Entries.Count(entry => entry.StartsWith("logs:", StringComparison.Ordinal));

        afterSecond.Should().Be(afterFirst, "the evidence is what the failure looked like, not what it looks like now");
    }

    [Fact]
    public async Task Down_re_applies_in_reverse_declaration_order_and_not_reverse_id_order()
    {
        var rig = Build();
        var context = await StageContextFactory.BuildCompensatingAsync(DeclarationOrderPlan());

        await rig.Stage.DownAsync(context, CancellationToken.None);

        rig.Provider.AppliedInOrder.Should().Equal("alpha", "zulu");
    }

    [Fact]
    public async Task Down_re_applies_the_previous_spec_and_never_the_candidate()
    {
        var rig = Build();
        var context = await StageContextFactory.BuildCompensatingAsync();

        await rig.Stage.DownAsync(context, CancellationToken.None);

        rig.Log.Entries.Should().Contain(entry => entry.StartsWith("apply:alpha:previous-alpha:", StringComparison.Ordinal));
        rig.Log.Entries.Should().NotContain(entry => entry.StartsWith("apply:alpha:target-alpha:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Down_tears_down_a_workload_the_candidate_added_and_keeps_its_volumes()
    {
        var rig = Build();
        var context = await StageContextFactory.BuildCompensatingAsync();

        await rig.Stage.DownAsync(context, CancellationToken.None);

        rig.Provider.TornDownInOrder.Should().Equal("beta");
        rig.Log.Entries.Should().Contain("teardown:beta:retain=True");
    }

    [Fact]
    public async Task Down_revokes_the_credentials_of_a_workload_the_candidate_added_and_of_no_other()
    {
        var rig = Build();
        var plan = StageContextFactory.DefaultPlan();
        var context = await StageContextFactory.BuildCompensatingAsync(plan);

        await rig.Stage.DownAsync(context, CancellationToken.None);

        rig.Credentials.RevokedInstances.Should().Equal(
            [plan.Workloads.Single(item => item.WorkloadId == "beta").Reference.InstanceId],
            "the apply minted under the added workload's id before any row held it; a restored workload keeps its own");
    }

    [Fact]
    public async Task Down_removes_an_added_workload_before_a_restore_that_fails_ends_the_rollback()
    {
        var rig = Build();
        var plan = AddedDeclaredFirstPlan();
        rig.Provider.ApplyBehaviour["alpha"] = _ =>
            WorkloadApplyResult.Failed(WorkloadApplyReason.Rejected, "the previous image is gone");
        var context = await StageContextFactory.BuildCompensatingAsync(plan);

        var outcome = await rig.Stage.DownAsync(context, CancellationToken.None);

        outcome.Kind.Should().Be(StageOutcomeKind.Failed);
        rig.Provider.TornDownInOrder.Should().Equal("beta");
        rig.Credentials.RevokedInstances.Should().Equal(
            [plan.Workloads.Single(item => item.WorkloadId == "beta").Reference.InstanceId],
            "a failed rollback is terminal, so nothing after it would remove the added workload or its credential");
    }

    [Fact]
    public async Task Down_removes_added_workloads_before_it_restores_and_keeps_reverse_declaration_order_within_each()
    {
        var rig = Build();
        var plan = WorkloadUpdatePlanBuilder.Plan(
            WorkloadUpdatePlanBuilder.InstanceGuid,
            [
                WorkloadUpdatePlanBuilder.Item("added-1", WorkloadUpdatePlanBuilder.InstanceGuid, "target-added-1", previousHash: null),
                WorkloadUpdatePlanBuilder.Item("kept-1", WorkloadUpdatePlanBuilder.InstanceGuid, "target-kept-1", "previous-kept-1"),
                WorkloadUpdatePlanBuilder.Item("added-2", WorkloadUpdatePlanBuilder.InstanceGuid, "target-added-2", previousHash: null),
                WorkloadUpdatePlanBuilder.Item("kept-2", WorkloadUpdatePlanBuilder.InstanceGuid, "target-kept-2", "previous-kept-2"),
            ]);
        var context = await StageContextFactory.BuildCompensatingAsync(plan);

        await rig.Stage.DownAsync(context, CancellationToken.None);

        rig.Log.Entries.Where(entry => entry.StartsWith("apply:", StringComparison.Ordinal)
                || entry.StartsWith("teardown:", StringComparison.Ordinal))
            .Select(entry => entry.Split(':')[1])
            .Should().Equal("added-2", "added-1", "kept-2", "kept-1");
    }

    [Fact]
    public async Task Down_that_cannot_revoke_an_added_workload_keeps_working_before_it_restores_anything()
    {
        var rig = Build();
        rig.Credentials.Throws = true;
        var context = await StageContextFactory.BuildCompensatingAsync(AddedDeclaredFirstPlan());

        var outcome = await rig.Stage.DownAsync(context, CancellationToken.None);

        outcome.Kind.Should().Be(StageOutcomeKind.Working);
        outcome.Detail.Should().StartWith("teardown or credential revocation of added workload 'beta' threw",
            "the stage names the step that failed, not a restore that never ran");
        rig.Provider.AppliedInOrder.Should().BeEmpty("the restores run once every added workload is removed");
    }

    private static WorkloadUpdatePlan AddedDeclaredFirstPlan() =>
        WorkloadUpdatePlanBuilder.Plan(
            WorkloadUpdatePlanBuilder.InstanceGuid,
            [
                WorkloadUpdatePlanBuilder.Item("beta", WorkloadUpdatePlanBuilder.InstanceGuid, "target-beta", previousHash: null),
                WorkloadUpdatePlanBuilder.Item("alpha", WorkloadUpdatePlanBuilder.InstanceGuid, "target-alpha", "previous-alpha"),
            ]);

    [Fact]
    public async Task Down_keeps_working_until_an_added_workloads_credentials_are_revoked()
    {
        var rig = Build();
        rig.Credentials.Throws = true;
        var context = await StageContextFactory.BuildCompensatingAsync();

        var refused = await rig.Stage.DownAsync(context, CancellationToken.None);
        rig.Credentials.Throws = false;
        var retried = await rig.Stage.DownAsync(context, CancellationToken.None);

        refused.Kind.Should().Be(StageOutcomeKind.Working, "the rollback window bounds the retry");
        refused.Detail.Should().StartWith("teardown or credential revocation of added workload 'beta' threw");
        retried.Kind.Should().Be(StageOutcomeKind.Done);
        rig.Credentials.RevokedInstances.Should().ContainSingle();
    }

    [Fact]
    public async Task Down_keeps_working_until_the_old_health_has_returned()
    {
        var rig = Build();
        rig.Gate.VerdictsByWorkload["alpha"] = new Queue<WorkloadHealthGateVerdict>(
            [WorkloadHealthGateVerdict.Pending, WorkloadHealthGateVerdict.Passed]);
        var context = await StageContextFactory.BuildCompensatingAsync();

        var first = await rig.Stage.DownAsync(context, CancellationToken.None);
        var second = await rig.Stage.DownAsync(context, CancellationToken.None);

        first.Kind.Should().Be(StageOutcomeKind.Working);
        second.Kind.Should().Be(StageOutcomeKind.Done);
    }

    [Fact]
    public async Task Down_gates_the_old_health_on_what_the_previous_release_declared()
    {
        var rig = Build();
        var plan = StageContextFactory.PlanWithDistinctSides();
        var context = await StageContextFactory.BuildCompensatingAsync(plan);

        await rig.Stage.DownAsync(context, CancellationToken.None);

        var request = rig.Gate.Requests.Single(r => r.Reference.WorkloadId == "alpha");
        request.ExpectedSpecHash.Should().Be("previous-alpha");
        request.CandidateExtensions.Should().BeSameAs(plan.Workloads[0].CurrentExtensions,
            "a rollback certifies against the old side on both sides");
        request.CurrentExtensions.Should().BeSameAs(plan.Workloads[0].CurrentExtensions);
        request.Certification.Should().Be(WorkloadHealthGateCertification.HostEvidence);
        request.ToVersion.Should().Be(plan.FromVersion);
        request.ApplyTimestampUtc.Should().Be(WorkloadUpdateOrchestrator.RollbackHealthNoApplyTimeGate,
            "a strict post-apply time check would wrongly reject an undisturbed old session that is already healthy");
    }

    [Fact]
    public async Task Down_does_not_wait_for_the_health_of_a_workload_it_tore_down()
    {
        var rig = Build();
        var context = await StageContextFactory.BuildCompensatingAsync();

        await rig.Stage.DownAsync(context, CancellationToken.None);

        rig.Gate.Requests.Should().NotContain(request => request.Reference.WorkloadId == "beta");
    }

    [Fact]
    public async Task Down_leaves_the_parameter_snapshot_to_the_stage_whose_job_it_is()
    {
        var rig = Build();
        var context = await StageContextFactory.BuildCompensatingAsync();

        await rig.Stage.DownAsync(context, CancellationToken.None);

        rig.Log.Entries.Should().NotContain("restore-params");
    }
}
