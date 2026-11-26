using FluentAssertions;
using FluentAssertions.Execution;
using Microsoft.Extensions.Logging.Abstractions;
using Reeve.Workloads.Abstractions;
using Reeve.Workloads.Abstractions.Contracts.Deployment;
using Reeve.Workloads.Abstractions.Domain.DesiredSpec;
using Reeve.Workloads.Abstractions.Domain.Entities;
using Reeve.Workloads.Abstractions.Update;
using Reeve.Workloads.Core.Tests.Groups;
using Reeve.Workloads.Core.Update;
using Reeve.Workloads.Providers.Manual;
using Xunit;
using static Reeve.Workloads.Core.Tests.Update.WorkloadUpdatePlanBuilder;

namespace Reeve.Workloads.Core.Tests.Update;

public sealed class WorkloadUpdateRowEffectsTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 23, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid ModuleInstance = InstanceGuid;
    private static readonly Guid AddedId = new("aaaaaaaa-0000-8000-8000-000000000001");
    private static readonly Guid OtherId = new("bbbbbbbb-0000-4000-8000-000000000002");

    [Fact]
    public async Task A_workload_with_no_row_gets_one_under_the_id_the_plan_applied_it_with()
    {
        var instances = new FakeInstances();
        var plan = Plan(ModuleInstance, [Rowless("ui", AddedId)]);

        await Effects(instances).EnsureRowsAsync(Staged(plan), plan);

        var row = instances.Instances.Should().ContainSingle().Which;
        using var scope = new AssertionScope();
        row.Id.Should().Be(AddedId);
        row.GroupInstanceId.Should().Be(ModuleInstance);
        row.GroupId.Should().Be(plan.GroupId);
        row.WorkloadId.Should().Be("ui");
        row.Provider.Should().Be(ManualProvider.Id, "the row is bound to the provider the target was applied on");
        row.State.Should().Be(WorkloadInstanceState.PendingConfiguration);
        row.AppliedSpecHash.Should().BeNull();
        row.GetResolvedParameters().Should().BeEmpty("parameters commit only at Completed");
        row.CreatedAtUtc.Should().Be(Now);
    }

    [Fact]
    public async Task A_workload_that_has_a_row_is_left_alone()
    {
        var instances = new FakeInstances();
        var existing = WorkloadInstance.Create(AddedId, ModuleInstance, "zwave", "ui", Now.AddDays(-1));
        instances.Instances.Add(existing);

        var plan = Plan(ModuleInstance, [Rowless("ui", AddedId)]);
        await Effects(instances).EnsureRowsAsync(Staged(plan), plan);

        instances.Added.Should().BeEmpty();
        instances.Updated.Should().BeEmpty();
        instances.Instances.Should().ContainSingle().Which.Should().BeSameAs(existing);
    }

    [Fact]
    public async Task A_row_under_another_id_is_left_alone_rather_than_duplicated()
    {
        var instances = new FakeInstances();
        var existing = WorkloadInstance.Create(OtherId, ModuleInstance, "zwave", "ui", Now.AddDays(-1));
        instances.Instances.Add(existing);

        var plan = Plan(ModuleInstance, [Rowless("ui", AddedId)]);
        await Effects(instances).EnsureRowsAsync(Staged(plan), plan);

        instances.Added.Should().BeEmpty();
        instances.Instances.Should().ContainSingle().Which.Id.Should().Be(OtherId);
    }

    [Fact]
    public async Task A_row_of_another_module_instance_does_not_count()
    {
        var instances = new FakeInstances();
        instances.Instances.Add(WorkloadInstance.Create(OtherId, Guid.NewGuid(), "zwave", "ui", Now));

        var plan = Plan(ModuleInstance, [Rowless("ui", AddedId)]);
        await Effects(instances).EnsureRowsAsync(Staged(plan), plan);

        instances.Instances.Should().ContainSingle(row => row.GroupInstanceId == ModuleInstance)
            .Which.Id.Should().Be(AddedId);
    }

    [Fact]
    public async Task Only_the_workloads_without_a_row_get_one()
    {
        var instances = new FakeInstances();
        instances.Instances.Add(WorkloadInstance.Create(OtherId, ModuleInstance, "zwave", "controller", Now));
        var plan = Plan(ModuleInstance, [Rowless("controller", OtherId), Rowless("ui", AddedId)]);

        await Effects(instances).EnsureRowsAsync(Staged(plan), plan);

        instances.Added.Should().ContainSingle().Which.WorkloadId.Should().Be("ui");
    }

    [Fact]
    public async Task A_plan_that_lists_one_workload_twice_creates_one_row()
    {
        var instances = new FakeInstances();
        var item = Rowless("ui", AddedId);
        var plan = Plan(ModuleInstance, [item]) with { Workloads = [item, item] };

        await Effects(instances).EnsureRowsAsync(Staged(plan), plan);

        instances.Added.Should().ContainSingle();
    }

    [Fact]
    public async Task A_workload_staged_under_another_id_gets_no_row()
    {
        var instances = new FakeInstances();
        var plan = Plan(ModuleInstance, [Rowless("controller", AddedId)]);
        var intent = Staged(plan with
        {
            TargetSpecs = new Dictionary<string, WorkloadDesiredSpec>(StringComparer.Ordinal)
            {
                ["controller"] = plan.TargetSpecs["controller"] with { InstanceId = OtherId },
            },
        });

        await Effects(instances).EnsureRowsAsync(intent, plan);

        instances.Added.Should().BeEmpty(
            "only a workload staged under the id the plan still gives it is one this intent added");
    }

    [Fact]
    public async Task A_workload_the_intent_did_not_stage_gets_no_row()
    {
        var instances = new FakeInstances();
        var plan = Plan(ModuleInstance, [Rowless("ui", AddedId)]);
        var intent = Staged(plan with { TargetSpecs = new Dictionary<string, WorkloadDesiredSpec>(StringComparer.Ordinal) });

        await Effects(instances).EnsureRowsAsync(intent, plan);

        instances.Added.Should().BeEmpty();
    }

    [Fact]
    public async Task An_intent_with_no_target_snapshot_creates_no_row()
    {
        var instances = new FakeInstances();
        var plan = Plan(ModuleInstance, [Rowless("ui", AddedId)]);
        var intent = WorkloadUpdateIntent.Create(Guid.NewGuid(), ModuleInstance, "2.5.0", "2.6.0", Now);

        await Effects(instances).EnsureRowsAsync(intent, plan);

        instances.Added.Should().BeEmpty("with no record of what the intent added, nothing is known to be its");
    }

    [Fact]
    public async Task A_module_instance_being_uninstalled_gets_no_row()
    {
        var instances = new FakeInstances();
        var controller = WorkloadInstance.Create(OtherId, ModuleInstance, "zwave", "controller", Now);
        controller.TransitionTo(WorkloadInstanceState.Terminating, WorkloadStateReason.Uninstalling, null, Now);
        instances.Instances.Add(controller);
        var plan = Plan(ModuleInstance, [Rowless("ui", AddedId)], candidate: Declaring("controller", "ui"));

        await Effects(instances).EnsureRowsAsync(Staged(plan), plan);

        instances.Added.Should().BeEmpty();
    }

    [Fact]
    public async Task A_dropped_workload_tearing_down_does_not_read_as_an_uninstall()
    {
        var instances = new FakeInstances();
        var dropped = WorkloadInstance.Create(OtherId, ModuleInstance, "zwave", "legacy", Now);
        dropped.TransitionTo(WorkloadInstanceState.Terminating, WorkloadStateReason.Uninstalling, null, Now);
        instances.Instances.Add(dropped);
        var plan = Plan(ModuleInstance, [Rowless("ui", AddedId)], candidate: Declaring("ui"));

        await Effects(instances).EnsureRowsAsync(Staged(plan), plan);

        instances.Added.Should().ContainSingle().Which.Id.Should().Be(AddedId);
    }

    [Fact]
    public async Task Every_row_the_candidate_no_longer_declares_is_torn_down_with_its_last_declaration()
    {
        var instances = new FakeInstances();
        var kept = WorkloadInstance.Create(AddedId, ModuleInstance, "zwave", "controller", Now);
        var dropped = WorkloadInstance.Create(OtherId, ModuleInstance, "zwave", "legacy", Now);
        var alreadyGoing = WorkloadInstance.Create(Guid.NewGuid(), ModuleInstance, "zwave", "older", Now);
        alreadyGoing.TransitionTo(WorkloadInstanceState.Terminating, WorkloadStateReason.Uninstalling, null, Now);
        instances.Instances.AddRange([kept, dropped, alreadyGoing]);
        var previous = Declaring("controller", "legacy");
        var plan = Plan(ModuleInstance, [], previous: previous, candidate: Declaring("controller"));
        var teardown = new RecordingDroppedTeardown();

        await Effects(instances, teardown: teardown).TearDownDroppedRowsAsync(plan);

        var call = teardown.Calls.Should().ContainSingle("a declared row stays and one already tearing down is left to it")
            .Which;
        call.Row.Should().BeSameAs(dropped);
        call.Declaration.Should().BeSameAs(previous.Workloads[1], "its volumes are named by the version it runs");
        call.Version.Should().Be(plan.ToVersion);
    }

    [Fact]
    public async Task A_dropped_workload_with_an_unreadable_previous_manifest_is_torn_down_with_no_declaration()
    {
        var instances = new FakeInstances();
        instances.Instances.Add(WorkloadInstance.Create(OtherId, ModuleInstance, "zwave", "legacy", Now));
        var plan = Plan(ModuleInstance, [], previous: Declaring("legacy"), candidate: Declaring("controller"))
            with { PreviousUnreadable = true };
        var teardown = new RecordingDroppedTeardown();

        await Effects(instances, teardown: teardown).TearDownDroppedRowsAsync(plan);

        var call = teardown.Calls.Should().ContainSingle().Which;
        call.Declaration.Should().BeNull();
        call.WhyUndeclared.Should().Contain("cannot be read");
    }

    [Fact]
    public async Task A_row_an_earlier_update_left_behind_is_torn_down_and_named_for_what_it_is()
    {
        var instances = new FakeInstances();
        instances.Instances.Add(WorkloadInstance.Create(OtherId, ModuleInstance, "zwave", "orphan", Now));
        var plan = Plan(ModuleInstance, [], previous: Declaring("controller"), candidate: Declaring("controller"));
        var teardown = new RecordingDroppedTeardown();

        await Effects(instances, teardown: teardown).TearDownDroppedRowsAsync(plan);

        var call = teardown.Calls.Should().ContainSingle().Which;
        call.Declaration.Should().BeNull();
        call.WhyUndeclared.Should().Contain("does not declare it either").And.NotContain("cannot be read");
    }

    private static WorkloadUpdateIntent Staged(WorkloadUpdatePlan plan)
    {
        var intent = WorkloadUpdateIntent.Create(Guid.NewGuid(), plan.GroupInstanceId, plan.FromVersion, plan.ToVersion, Now);
        intent.StageSnapshots(new WorkloadUpdateSnapshots { TargetSpecs = plan.TargetSpecs }, Now);
        return intent;
    }

    private static WorkloadUpdateRowEffects Effects(
        FakeInstances instances,
        IWorkloadDiagnosticsSinkFactory? diagnostics = null,
        IWorkloadDroppedWorkloadTeardown? teardown = null)
        => new(instances, new MutableClock(Now), NullLogger<WorkloadUpdateRowEffects>.Instance,
            new Reeve.Workloads.Core.UnpinnedReferenceRolloutGuard(new Microsoft.AspNetCore.DataProtection.EphemeralDataProtectionProvider()),
            diagnostics ?? new NoWorkloadDiagnosticsSinkFactory(),
            teardown ?? new RecordingDroppedTeardown());

    private static DeploymentManifest Declaring(params string[] workloadIds) => new()
    {
        SchemaVersion = 1,
        Workloads = [.. workloadIds.Select(id => Reeve.Workloads.DeploymentFixtures.DeploymentManifestBuilder.Workload(id))],
    };

    private static WorkloadUpdatePlanItem Rowless(string workloadId, Guid instanceId)
    {
        var item = Item(workloadId, instanceId, "sha-new", previousHash: null, ManualProvider.Id);
        return item with { TargetSpec = item.TargetSpec with { InstanceId = instanceId } };
    }
}
