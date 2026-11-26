using FluentAssertions;
using Reeve.Workloads.Abstractions.Contracts;
using Reeve.Workloads.Abstractions.Domain.Entities;
using Reeve.Workloads.Abstractions.Providers;
using Reeve.Workloads.Tests.Shared;

namespace Reeve.Workloads.Abstractions.Tests;

public sealed class WorkloadInstanceEntityTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 6, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Create_populates_identity_and_defaults()
    {
        var id = Guid.NewGuid();
        var pluginInstanceId = Guid.NewGuid();

        var instance = WorkloadInstance.Create(id, pluginInstanceId, "matter", "matter-server", Now);

        instance.Id.Should().Be(id);
        instance.GroupInstanceId.Should().Be(pluginInstanceId);
        instance.GroupId.Should().Be("matter");
        instance.WorkloadId.Should().Be("matter-server");
        instance.State.Should().Be(WorkloadInstanceState.PendingConfiguration);
        instance.StateReason.Should().Be(WorkloadStateReason.ConfigRequired);
        instance.Provider.Should().BeNull();
        instance.ObserveOnly.Should().BeFalse();
        instance.RestartCount.Should().Be(0);
        instance.DesiredSpecHash.Should().BeNull();
        instance.AppliedSpecHash.Should().BeNull();
        instance.LastHealthyAtUtc.Should().BeNull();
        instance.CreatedAtUtc.Should().Be(Now);
    }

    [Fact]
    public void Inst8_is_first_8_lowercase_hex_chars_of_the_id()
    {
        var id = Guid.Parse("ABCDEF12-3456-7890-ABCD-EF1234567890");
        var instance = WorkloadInstance.Create(id, Guid.NewGuid(), "bt", "ble", Now);

        instance.Inst8.Should().Be("abcdef12");
        instance.Inst8.Should().HaveLength(8);
        WorkloadInstance.ComputeInst8(id).Should().Be("abcdef12");
    }

    [Fact]
    public void Two_instances_have_disjoint_inst8_the_two_zwave_sticks_case()
    {
        var a = WorkloadInstance.Create(Guid.NewGuid(), Guid.NewGuid(), "zwave", "zwave-js", Now);
        var b = WorkloadInstance.Create(Guid.NewGuid(), Guid.NewGuid(), "zwave", "zwave-js", Now);

        a.Inst8.Should().NotBe(b.Inst8);
    }

    [Fact]
    public void Create_rejects_empty_id_plugin_instance_and_blank_ids()
    {
        FluentActions.Invoking(() => WorkloadInstance.Create(Guid.Empty, Guid.NewGuid(), "matter", "w", Now))
            .Should().Throw<ArgumentException>().WithParameterName("id");

        FluentActions.Invoking(() => WorkloadInstance.Create(Guid.NewGuid(), Guid.Empty, "matter", "w", Now))
            .Should().Throw<ArgumentException>().WithParameterName("pluginInstanceId");

        FluentActions.Invoking(() => WorkloadInstance.Create(Guid.NewGuid(), Guid.NewGuid(), " ", "w", Now))
            .Should().Throw<ArgumentException>().WithParameterName("pluginTypeId");

        FluentActions.Invoking(() => WorkloadInstance.Create(Guid.NewGuid(), Guid.NewGuid(), "matter", "", Now))
            .Should().Throw<ArgumentException>().WithParameterName("workloadId");
    }

    [Fact]
    public void Setters_update_fields_and_stamp_updated_at()
    {
        var instance = WorkloadInstance.Create(Guid.NewGuid(), Guid.NewGuid(), "matter", "w", Now);
        var later = Now.AddMinutes(1);

        instance.SetProvider(StandInProviders.KubernetesId, later);
        instance.SetDesiredSpecHash("  abc  ", later);
        instance.SetAppliedSpecHash("def", later);
        instance.SetObserveOnly(true, later);
        instance.SetRestartCount(3, later);

        instance.Provider.Should().Be(StandInProviders.KubernetesId);
        instance.DesiredSpecHash.Should().Be("abc");
        instance.AppliedSpecHash.Should().Be("def");
        instance.ObserveOnly.Should().BeTrue();
        instance.RestartCount.Should().Be(3);
        instance.UpdatedAtUtc.Should().Be(later);
    }

    [Fact]
    public void SetProvisioningProgress_sets_percent_and_message_without_touching_state()
    {
        var instance = WorkloadInstance.Create(Guid.NewGuid(), Guid.NewGuid(), "matter", "w", Now);
        var stateBefore = instance.State;
        var reasonBefore = instance.StateReason;
        var later = Now.AddSeconds(2);

        instance.SetProvisioningProgress(45, "Pulling image… 45% (12/26 MB)", later);

        instance.ProvisioningPercent.Should().Be(45);
        instance.StateMessage.Should().Be("Pulling image… 45% (12/26 MB)");
        instance.State.Should().Be(stateBefore);
        instance.StateReason.Should().Be(reasonBefore);
        instance.UpdatedAtUtc.Should().Be(later);
    }

    [Fact]
    public void SetProvisioningProgress_clamps_and_accepts_null_indeterminate()
    {
        var instance = WorkloadInstance.Create(Guid.NewGuid(), Guid.NewGuid(), "matter", "w", Now);

        instance.SetProvisioningProgress(150, "over", Now);
        instance.ProvisioningPercent.Should().Be(100);

        instance.SetProvisioningProgress(null, "Pulling image…", Now);
        instance.ProvisioningPercent.Should().BeNull();
        instance.StateMessage.Should().Be("Pulling image…");
    }

    [Fact]
    public void TransitionTo_clears_provisioning_percent()
    {
        var instance = WorkloadInstance.Create(Guid.NewGuid(), Guid.NewGuid(), "matter", "w", Now);
        instance.SetProvisioningProgress(80, "Pulling image… 80%", Now);
        instance.ProvisioningPercent.Should().Be(80);

        instance.TransitionTo(instance.State, WorkloadStateReason.Provisioning, "Applying desired spec.", Now.AddSeconds(1));

        instance.ProvisioningPercent.Should().BeNull();
    }

    private static WorkloadInstance HeldByHost()
    {
        var instance = WorkloadInstance.Create(Guid.NewGuid(), Guid.NewGuid(), "matter", "w", Now);
        instance.TransitionTo(WorkloadInstanceState.Deploying, WorkloadStateReason.Provisioning, null, Now);
        instance.TransitionTo(WorkloadInstanceState.Healthy, WorkloadStateReason.HealthObserved, null, Now);
        instance.TransitionTo(WorkloadInstanceState.Suspended, WorkloadStateReason.SuspendedByHost, "withdrawn", Now);
        return instance;
    }

    [Theory]
    [InlineData(WorkloadInstanceState.Deploying, WorkloadStateReason.Redeploying)]
    [InlineData(WorkloadInstanceState.Healthy, WorkloadStateReason.HealthObserved)]
    [InlineData(WorkloadInstanceState.Unknown, WorkloadStateReason.ProviderUnreachable)]
    [InlineData(WorkloadInstanceState.Suspended, WorkloadStateReason.OperatorPaused)]
    public void A_host_held_instance_refuses_every_move_but_the_hosts_resume_and_a_teardown(
        WorkloadInstanceState target, WorkloadStateReason reason)
    {
        var instance = HeldByHost();

        FluentActions.Invoking(() => instance.TransitionTo(target, reason, null, Now))
            .Should().Throw<WorkloadStateTransitionException>();
        instance.IsHeldByHost.Should().BeTrue();
    }

    [Theory]
    [InlineData("manual", false)]
    [InlineData("kubernetes", true)]
    [InlineData("docker", true)]
    [InlineData("nomad", true)]
    [InlineData(null, true)]
    public void An_externally_managed_row_has_released_ownership_unless_its_provider_materializes_nothing(
        string? provider, bool released)
    {
        var instance = WorkloadInstance.Create(Guid.NewGuid(), Guid.NewGuid(), "matter", "w", Now);
        if (provider is not null)
        {
            instance.SetProvider(WorkloadProviderId.Parse(provider), Now);
        }

        instance.TransitionTo(WorkloadInstanceState.Deploying, WorkloadStateReason.Provisioning, null, Now);
        instance.TransitionTo(WorkloadInstanceState.ExternallyManaged, WorkloadStateReason.ExternalHealthObserved, null, Now);

        instance.IsReconcileFrozen.Should().BeTrue("Reeve never touches the runtime of either");
        instance.IsOwnershipReleased(new StandInRegistry()).Should().Be(released);
    }

    [Fact]
    public void A_mid_switch_or_held_externally_managed_row_has_released_ownership_and_a_live_one_has_not()
    {
        var switching = WorkloadInstance.Create(Guid.NewGuid(), Guid.NewGuid(), "matter", "w", Now);
        switching.TransitionTo(WorkloadInstanceState.Deploying, WorkloadStateReason.Provisioning, null, Now);
        switching.TransitionTo(WorkloadInstanceState.Healthy, WorkloadStateReason.HealthObserved, null, Now);
        switching.TransitionTo(WorkloadInstanceState.Switching, WorkloadStateReason.HealthObserved, null, Now);
        var healthy = WorkloadInstance.Create(Guid.NewGuid(), Guid.NewGuid(), "matter", "w", Now);
        healthy.TransitionTo(WorkloadInstanceState.Deploying, WorkloadStateReason.Provisioning, null, Now);
        healthy.TransitionTo(WorkloadInstanceState.Healthy, WorkloadStateReason.HealthObserved, null, Now);
        var held = HeldWhileExternallyManaged();
        held.SetProvider(StandInProviders.ManualId, Now);

        switching.IsOwnershipReleased(new StandInRegistry()).Should().BeTrue();
        held.IsOwnershipReleased(new StandInRegistry()).Should().BeTrue();
        healthy.IsOwnershipReleased(new StandInRegistry()).Should().BeFalse();
    }

    private sealed class StandInRegistry : IWorkloadProviderRegistry
    {
        public IWorkloadProvider? TryResolve(WorkloadProviderId provider) => null;

        public IWorkloadProvider Resolve(WorkloadProviderId provider)
            => throw new WorkloadProviderNotRegisteredException(provider);

        public IReadOnlyCollection<WorkloadProviderId> RegisteredProviders => [];

        public IReadOnlyCollection<WorkloadProviderDescriptor> Descriptors => [];

        public WorkloadProviderDescriptor Describe(WorkloadProviderId provider)
            => StandInProviders.For(provider);
    }

    [Theory]
    [InlineData(WorkloadInstanceState.Deploying, WorkloadStateReason.ResumedByHost)]
    [InlineData(WorkloadInstanceState.Terminating, WorkloadStateReason.Uninstalling)]
    public void The_hosts_resume_and_a_teardown_lift_the_hold(WorkloadInstanceState target, WorkloadStateReason reason)
    {
        var instance = HeldByHost();

        instance.TransitionTo(target, reason, null, Now);

        instance.State.Should().Be(target);
        instance.IsHeldByHost.Should().BeFalse();
    }

    private static WorkloadInstance HeldWhileExternallyManaged()
    {
        var instance = WorkloadInstance.Create(Guid.NewGuid(), Guid.NewGuid(), "matter", "w", Now);
        instance.TransitionTo(WorkloadInstanceState.Deploying, WorkloadStateReason.Provisioning, null, Now);
        instance.TransitionTo(WorkloadInstanceState.ExternallyManaged, WorkloadStateReason.ExternalHealthObserved, null, Now);
        instance.TransitionTo(WorkloadInstanceState.Suspended, WorkloadStateReason.SuspendedByHostExternallyManaged, null, Now);
        return instance;
    }

    [Fact]
    public void A_hold_over_an_externally_managed_row_is_a_host_hold_and_keeps_the_runtime_frozen()
    {
        var instance = HeldWhileExternallyManaged();

        instance.IsHeldByHost.Should().BeTrue("the hold machinery keys on it: per-pass revoke, reveal refusal");
        instance.IsReconcileFrozen.Should().BeTrue("the runtime stays the operator's while the host holds it");
    }

    [Theory]
    [InlineData(WorkloadInstanceState.Deploying, WorkloadStateReason.ResumedByHost)]
    [InlineData(WorkloadInstanceState.ExternallyManaged, WorkloadStateReason.ExternalHealthObserved)]
    [InlineData(WorkloadInstanceState.Suspended, WorkloadStateReason.SuspendedByHost)]
    [InlineData(WorkloadInstanceState.Healthy, WorkloadStateReason.HealthObserved)]
    public void A_hold_over_an_externally_managed_row_refuses_a_re_adoption_a_live_signal_and_losing_its_ownership(
        WorkloadInstanceState target, WorkloadStateReason reason)
    {
        var instance = HeldWhileExternallyManaged();

        FluentActions.Invoking(() => instance.TransitionTo(target, reason, null, Now))
            .Should().Throw<WorkloadStateTransitionException>();
        instance.IsReconcileFrozen.Should().BeTrue();
    }

    [Fact]
    public void The_hosts_resume_returns_an_externally_managed_row_to_the_operator()
    {
        var instance = HeldWhileExternallyManaged();

        instance.TransitionTo(WorkloadInstanceState.ExternallyManaged, WorkloadStateReason.ResumedByHost, null, Now);

        instance.State.Should().Be(WorkloadInstanceState.ExternallyManaged);
        instance.IsHeldByHost.Should().BeFalse();
    }

    [Fact]
    public void The_hosts_resume_cannot_hand_a_managed_row_to_the_operator()
    {
        var instance = HeldByHost();

        FluentActions.Invoking(() => instance.TransitionTo(
                WorkloadInstanceState.ExternallyManaged, WorkloadStateReason.ResumedByHost, null, Now))
            .Should().Throw<WorkloadStateTransitionException>();
    }

    [Theory]
    [InlineData(WorkloadInstanceState.Healthy)]
    [InlineData(WorkloadInstanceState.Failed)]
    public void Only_an_externally_managed_row_enters_the_externally_managed_hold(WorkloadInstanceState from)
    {
        var instance = WorkloadInstance.Create(Guid.NewGuid(), Guid.NewGuid(), "matter", "w", Now);
        instance.TransitionTo(WorkloadInstanceState.Deploying, WorkloadStateReason.Provisioning, null, Now);
        instance.TransitionTo(from, WorkloadStateReason.HealthObserved, null, Now);

        FluentActions.Invoking(() => instance.TransitionTo(
                WorkloadInstanceState.Suspended, WorkloadStateReason.SuspendedByHostExternallyManaged, null, Now))
            .Should().Throw<WorkloadStateTransitionException>("a managed runtime must not be handed to the operator");
    }

    [Fact]
    public void A_host_hold_marked_mid_switch_is_pending_not_held()
    {
        var instance = WorkloadInstance.Create(Guid.NewGuid(), Guid.NewGuid(), "matter", "w", Now);
        instance.TransitionTo(WorkloadInstanceState.Deploying, WorkloadStateReason.Provisioning, null, Now);
        instance.TransitionTo(WorkloadInstanceState.Healthy, WorkloadStateReason.HealthObserved, null, Now);
        instance.TransitionTo(WorkloadInstanceState.Switching, WorkloadStateReason.HealthObserved, null, Now);

        instance.TransitionTo(WorkloadInstanceState.Switching, WorkloadStateReason.SuspendedByHost, null, Now);

        instance.IsHostHoldPending.Should().BeTrue();
        instance.IsHeldByHost.Should().BeFalse("the switch still owns the row until it completes");
    }

    [Fact]
    public void SetRestartCount_floors_negative_at_zero()
    {
        var instance = WorkloadInstance.Create(Guid.NewGuid(), Guid.NewGuid(), "matter", "w", Now);

        instance.SetRestartCount(-5, Now);

        instance.RestartCount.Should().Be(0);
    }

    [Fact]
    public void Blank_spec_hash_normalizes_to_null()
    {
        var instance = WorkloadInstance.Create(Guid.NewGuid(), Guid.NewGuid(), "matter", "w", Now);

        instance.SetDesiredSpecHash("   ", Now);

        instance.DesiredSpecHash.Should().BeNull();
    }

    [Fact]
    public void Resolved_parameters_round_trip_through_ParametersJson()
    {
        var instance = WorkloadInstance.Create(Guid.NewGuid(), Guid.NewGuid(), "matter", "w", Now);
        var parameters = new Dictionary<string, string?>
        {
            ["port.serial.hostPort"] = "5000",
            ["dbusHostPath"] = "/var/run/dbus",
            ["optionalBlank"] = null,
        };

        instance.SetResolvedParameters(parameters, Now);

        instance.ParametersDocument.Should().NotBeNull();
        var read = instance.GetResolvedParameters();
        read.Should().HaveCount(3);
        read["port.serial.hostPort"].Should().Be("5000");
        read["dbusHostPath"].Should().Be("/var/run/dbus");
        read.Should().ContainKey("optionalBlank");
        read["optionalBlank"].Should().BeNull();
    }

    [Fact]
    public void GetResolvedParameters_is_empty_when_never_set()
    {
        var instance = WorkloadInstance.Create(Guid.NewGuid(), Guid.NewGuid(), "matter", "w", Now);

        instance.ParametersDocument.Should().BeNull();
        instance.GetResolvedParameters().Should().BeEmpty();
    }

    [Fact]
    public void SetResolvedParameters_with_empty_map_clears_the_document()
    {
        var instance = WorkloadInstance.Create(Guid.NewGuid(), Guid.NewGuid(), "matter", "w", Now);
        instance.SetResolvedParameters(new Dictionary<string, string?> { ["a"] = "1" }, Now);
        instance.ParametersDocument.Should().NotBeNull();

        instance.SetResolvedParameters(new Dictionary<string, string?>(), Now);

        instance.ParametersDocument.Should().BeNull();
        instance.GetResolvedParameters().Should().BeEmpty();
    }

    [Fact]
    public void Fresh_instance_has_no_pending_scan_run()
    {
        var instance = WorkloadInstance.Create(Guid.NewGuid(), Guid.NewGuid(), "discovery-host", "discovery", Now);

        instance.HasPendingScanRun.Should().BeFalse();
        instance.PendingScanRunId.Should().BeNull();
    }

    [Fact]
    public void A_self_transition_with_nothing_new_changes_nothing()
    {
        var instance = WorkloadInstance.Create(Guid.NewGuid(), Guid.NewGuid(), "matter", "matter-server", Now);
        instance.TransitionTo(WorkloadInstanceState.Deploying, WorkloadStateReason.Provisioning, null, Now);
        instance.TransitionTo(WorkloadInstanceState.Healthy, WorkloadStateReason.HealthObserved, "ready", Now);

        instance.TransitionTo(WorkloadInstanceState.Healthy, WorkloadStateReason.HealthObserved, "ready", Now.AddMinutes(1));
        instance.SetDesiredSpecHash(null, Now.AddMinutes(1));
        instance.SetAppliedSpecHash(null, Now.AddMinutes(1));

        instance.UpdatedAtUtc.Should().Be(Now);
        instance.LastHealthyAtUtc.Should().Be(Now);
    }

    [Fact]
    public void A_self_transition_with_a_new_message_refreshes_it()
    {
        var instance = WorkloadInstance.Create(Guid.NewGuid(), Guid.NewGuid(), "matter", "matter-server", Now);
        instance.TransitionTo(WorkloadInstanceState.Deploying, WorkloadStateReason.Provisioning, "one", Now);

        instance.TransitionTo(WorkloadInstanceState.Deploying, WorkloadStateReason.Provisioning, "two", Now.AddMinutes(1));

        instance.StateMessage.Should().Be("two");
        instance.UpdatedAtUtc.Should().Be(Now.AddMinutes(1));
        instance.LastTransitionAtUtc.Should().Be(Now);
    }

    [Fact]
    public void BeginScanRun_sets_marker_and_stamps_updated_at()
    {
        var instance = WorkloadInstance.Create(Guid.NewGuid(), Guid.NewGuid(), "discovery-host", "discovery", Now);
        var later = Now.AddMinutes(1);

        instance.BeginScanRun("run-abc123", later);

        instance.HasPendingScanRun.Should().BeTrue();
        instance.PendingScanRunId.Should().Be("run-abc123");
        instance.UpdatedAtUtc.Should().Be(later);
    }

    [Fact]
    public void ClearScanRun_resets_marker_to_empty()
    {
        var instance = WorkloadInstance.Create(Guid.NewGuid(), Guid.NewGuid(), "discovery-host", "discovery", Now);
        instance.BeginScanRun("run-abc123", Now.AddMinutes(1));
        var evenLater = Now.AddMinutes(2);

        instance.ClearScanRun(evenLater);

        instance.HasPendingScanRun.Should().BeFalse();
        instance.PendingScanRunId.Should().BeNull();
        instance.UpdatedAtUtc.Should().Be(evenLater);
    }

    [Fact]
    public void CompleteScanRun_clears_the_marker_and_records_the_nodes_that_failed()
    {
        var instance = WorkloadInstance.Create(Guid.NewGuid(), Guid.NewGuid(), "discovery-host", "discovery", Now);
        instance.BeginScanRun("run-abc123", Now.AddMinutes(1));
        var evenLater = Now.AddMinutes(2);

        instance.CompleteScanRun(new[] { "control-plane" }, evenLater);

        instance.HasPendingScanRun.Should().BeFalse("the run is over — a failed node never leaves the marker pending");
        instance.GetLastScanFailedNodes().Should().BeEquivalentTo(new[] { "control-plane" });
        instance.UpdatedAtUtc.Should().Be(evenLater);
    }

    [Fact]
    public void CompleteScanRun_with_no_failures_records_nothing()
    {
        var instance = WorkloadInstance.Create(Guid.NewGuid(), Guid.NewGuid(), "discovery-host", "discovery", Now);
        instance.BeginScanRun("run-abc123", Now.AddMinutes(1));

        instance.CompleteScanRun(Array.Empty<string>(), Now.AddMinutes(2));

        instance.HasPendingScanRun.Should().BeFalse();
        instance.GetLastScanFailedNodes().Should().BeEmpty();
        instance.LastScanFailedNodes.Should().BeNull("a clean run stores nothing, not an empty list");
    }

    [Fact]
    public void CompleteScanRun_recording_a_clean_outcome_replaces_a_recorded_failure()
    {
        var instance = WorkloadInstance.Create(Guid.NewGuid(), Guid.NewGuid(), "discovery-host", "discovery", Now);
        instance.BeginScanRun("run-1", Now.AddMinutes(1));
        instance.CompleteScanRun(new[] { "control-plane" }, Now.AddMinutes(2));

        instance.CompleteScanRun(Array.Empty<string>(), Now.AddMinutes(3));

        instance.GetLastScanFailedNodes().Should().BeEmpty();
        instance.LastScanFailedNodes.Should().BeNull();
    }

    [Fact]
    public void BeginScanRun_drops_the_previous_runs_recorded_failure()
    {
        var instance = WorkloadInstance.Create(Guid.NewGuid(), Guid.NewGuid(), "discovery-host", "discovery", Now);
        instance.BeginScanRun("run-1", Now.AddMinutes(1));
        instance.CompleteScanRun(new[] { "control-plane" }, Now.AddMinutes(2));

        instance.BeginScanRun("run-2", Now.AddMinutes(3));

        instance.GetLastScanFailedNodes().Should().BeEmpty(
            "a run in flight must not report the previous run's failure as its own");
    }

    [Fact]
    public void BeginScanRun_holds_at_most_one_pending_run_a_rebegin_replaces()
    {
        var instance = WorkloadInstance.Create(Guid.NewGuid(), Guid.NewGuid(), "discovery-host", "discovery", Now);

        instance.BeginScanRun("run-1", Now.AddMinutes(1));
        instance.BeginScanRun("run-2", Now.AddMinutes(2));

        instance.PendingScanRunId.Should().Be("run-2");
        instance.HasPendingScanRun.Should().BeTrue();
    }

    [Fact]
    public void BeginScanRun_rejects_blank_run_id()
    {
        var instance = WorkloadInstance.Create(Guid.NewGuid(), Guid.NewGuid(), "discovery-host", "discovery", Now);

        FluentActions.Invoking(() => instance.BeginScanRun(" ", Now))
            .Should().Throw<ArgumentException>().WithParameterName("runId");
    }

    [Fact]
    public void Scan_run_marker_is_independent_of_applied_spec_hash()
    {
        var instance = WorkloadInstance.Create(Guid.NewGuid(), Guid.NewGuid(), "discovery-host", "discovery", Now);

        instance.SetAppliedSpecHash("hash-1", Now.AddMinutes(1));
        instance.BeginScanRun("run-1", Now.AddMinutes(2));

        instance.AppliedSpecHash.Should().Be("hash-1");
        instance.PendingScanRunId.Should().Be("run-1");

        instance.ClearScanRun(Now.AddMinutes(3));
        instance.AppliedSpecHash.Should().Be("hash-1");

        instance.SetAppliedSpecHash("hash-2", Now.AddMinutes(4));
        instance.PendingScanRunId.Should().BeNull();
    }

    [Fact]
    public void GetResolvedParameters_tolerates_malformed_stored_json()
    {
        var instance = WorkloadInstance.Create(Guid.NewGuid(), Guid.NewGuid(), "matter", "w", Now);
        instance.SetResolvedParameters(new Dictionary<string, string?> { ["a"] = "1" }, Now);
        instance.GetResolvedParameters().Should().ContainKey("a");
    }
}
