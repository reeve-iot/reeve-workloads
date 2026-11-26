using AuditEntry = Reeve.Workloads.Abstractions.WorkloadAuditEntry;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Reeve.Workloads.Abstractions;
using Reeve.Workloads.Abstractions.Contracts;
using Reeve.Workloads.Abstractions.Contracts.Deployment;
using Reeve.Workloads.Abstractions.Domain;
using Reeve.Workloads.Abstractions.Domain.DesiredSpec;
using Reeve.Workloads.Abstractions.Domain.Entities;
using Reeve.Workloads.Abstractions.Providers;
using Reeve.Workloads.Core;
using Reeve.Workloads.Core.Providers;
using Reeve.Workloads.Core.Tests;
using Reeve.Workloads.Domain;
using Reeve.Workloads.Domain.DesiredSpec;
using Reeve.Workloads.Providers.Kubernetes;
using Reeve.Workloads.Testing;
using Xunit;
using Reeve.Workloads.Providers.Docker;
using Reeve.Workloads.Providers.Manual;

namespace Reeve.Workloads.Core.Tests.Reconcile;

public sealed class WorkloadReconcileEngineTests
{
    private const string GroupId = "zwave";
    private const string WorkloadId = "zwave-js";

    [Fact]
    public async Task Null_inputs_is_WorkloadNotDeclared_no_op()
    {
        var instance = NewInstance();
        var harness = new Harness { Inputs = null };
        var engine = harness.Build();

        var outcome = await engine.ReconcileInstanceAsync(instance);

        outcome.Reason.Should().Be(WorkloadReconcileReason.WorkloadNotDeclared);
        outcome.Action.Should().Be(WorkloadReconcileAction.NoOp);
        harness.Provider.IsTracked(instance.Id).Should().BeFalse("no provider work happens when the workload is not declared");
        harness.Repository.SavedCount.Should().Be(0);
    }

    [Theory]
    [InlineData(WorkloadInstanceState.PendingConfiguration, WorkloadInstanceState.PendingConfiguration)]
    [InlineData(WorkloadInstanceState.Deploying, WorkloadInstanceState.PendingConfiguration)]
    [InlineData(WorkloadInstanceState.Failed, WorkloadInstanceState.Failed)]
    public async Task A_provider_binding_conflict_holds_the_instance_with_the_reason_as_its_state_message(
        WorkloadInstanceState from, WorkloadInstanceState held)
    {
        var instance = NewInstance(from);
        var harness = new Harness { ProviderBindingConflict = "bound to different providers (docker, manual)" };
        var engine = harness.Build();

        var outcome = await engine.ReconcileInstanceAsync(instance);

        outcome.ResultingState.Should().Be(held);
        outcome.Message.Should().Contain("different providers");
        instance.State.Should().Be(held);
        instance.StateMessage.Should().Contain("different providers",
            "a conflict only an operator can resolve must show on the instance, not only in a log");
        harness.Repository.SavedCount.Should().BeGreaterThan(0);
        harness.Provider.IsTracked(instance.Id).Should().BeFalse("nothing is applied on a provider nobody chose");
    }

    [Fact]
    public async Task A_host_refusal_is_a_HostRefusedBootstrap_no_op_that_changes_and_saves_nothing()
    {
        var instance = NewInstance();
        var stateBefore = instance.State;
        var reasonBefore = instance.StateReason;
        var logger = new Reeve.Workloads.Core.Tests.Hosting.RecordingLogger<WorkloadReconcileEngine>();
        var harness = new Harness { HostRefusal = "host says no", Logger = logger };
        var engine = harness.Build();

        var outcome = await engine.ReconcileInstanceAsync(instance);

        outcome.Action.Should().Be(WorkloadReconcileAction.NoOp);
        outcome.Reason.Should().Be(WorkloadReconcileReason.HostRefusedBootstrap);
        outcome.ResultingState.Should().Be(stateBefore);
        outcome.Message.Should().Contain("host says no");
        instance.State.Should().Be(stateBefore);
        instance.StateReason.Should().Be(reasonBefore);
        harness.Repository.SavedCount.Should().Be(0);
        harness.Provider.IsTracked(instance.Id).Should().BeFalse("nothing is applied under a refusal");
        logger.Entries.Should().Contain(e => e.Level == Microsoft.Extensions.Logging.LogLevel.Warning
            && e.Text.Contains("host says no"));
    }

    [Fact]
    public async Task No_registered_provider_is_NoProviderAvailable_no_op()
    {
        var instance = NewInstance();
        var harness = new Harness { RegisteredProvider = null };
        var engine = harness.Build();

        var outcome = await engine.ReconcileInstanceAsync(instance);

        outcome.Reason.Should().Be(WorkloadReconcileReason.NoProviderAvailable);
        outcome.Action.Should().Be(WorkloadReconcileAction.NoOp);
        outcome.ResultingState.Should().Be(instance.State, "state is left unchanged when no provider is available");
        harness.Repository.SavedCount.Should().Be(0);
    }

    [Fact]
    public async Task Drift_applies_the_desired_spec_and_transitions_toward_Deploying()
    {
        var instance = NewInstance(state: WorkloadInstanceState.PendingConfiguration);
        var harness = new Harness().WithDesiredHash("hash-v1");
        var engine = harness.Build();

        var outcome = await engine.ReconcileInstanceAsync(instance);

        harness.Provider.ApplyCountFor(instance.Id).Should().Be(1, "drift must drive an Apply");
        harness.Provider.LastAppliedSpec(instance.Id)!.SpecHash.Should().Be("hash-v1");
        instance.DesiredSpecHash.Should().Be("hash-v1");
        instance.AppliedSpecHash.Should().Be("hash-v1");
        instance.Provider.Should().Be(KubernetesProvider.Id);
        instance.State.Should().Be(WorkloadInstanceState.Deploying);
        harness.Repository.SavedCount.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task An_apply_never_rebinds_a_bound_instance_to_another_provider()
    {
        var instance = NewInstance(state: WorkloadInstanceState.PendingConfiguration);
        instance.SetProvider(KubernetesProvider.Id, DateTimeOffset.UtcNow);
        var harness = new Harness { EffectiveProvider = ManualProvider.Descriptor }.WithDesiredHash("hash-v1");
        var engine = harness.Build();

        await engine.ReconcileInstanceAsync(instance);

        harness.Provider.ApplyCountFor(instance.Id).Should().Be(1, "the pass did apply, so the binding was put to the test");
        instance.Provider.Should().Be(KubernetesProvider.Id,
            "only an operator action may move a deployed workload to another provider, never a later auto-detect answer");
    }

    [Fact]
    public async Task A_scan_fan_out_never_rebinds_a_bound_instance_to_another_provider()
    {
        var instance = NewInstance(state: WorkloadInstanceState.Deploying);
        instance.SetProvider(KubernetesProvider.Id, DateTimeOffset.UtcNow);
        instance.BeginScanRun("run-1", DateTimeOffset.UtcNow);
        var harness = new Harness
        {
            ManifestKind = WorkloadKind.ScanPerNode,
            EffectiveProvider = ManualProvider.Descriptor,
        }.WithDesiredHash("scan-hash");
        harness.ThrowingProvider.SetScanReadyNodes("node-a");
        var engine = harness.Build();

        await engine.ReconcileInstanceAsync(instance);

        harness.Provider.LastAppliedSpec(instance.Id).Should().NotBeNull("the scan fanned out, so the binding was put to the test");
        instance.Provider.Should().Be(KubernetesProvider.Id);
    }

    [Fact]
    public async Task Apply_progress_is_persisted_live_to_the_status_during_the_pull()
    {
        var instance = NewInstance(state: WorkloadInstanceState.PendingConfiguration);
        var harness = new Harness().WithDesiredHash("hash-pull");
        harness.ThrowingProvider.ProgressToReport =
            new WorkloadApplyProgress { Percent = 42, Message = "Pulling image… 42% (12/26 MB)" };
        var engine = harness.Build();

        await engine.ReconcileInstanceAsync(instance);

        harness.Repository.ProgressSnapshots.Should().Contain(
            snapshot => snapshot.Percent == 42 && snapshot.Message != null && snapshot.Message.Contains("Pulling image"),
            "the reconciler relays the provider's image-pull % to the live status during the apply");

        instance.State.Should().Be(WorkloadInstanceState.Deploying);
        instance.ProvisioningPercent.Should().BeNull();
    }

    [Fact]
    public async Task Successful_adoption_apply_marks_the_matching_retained_row_reattached()
    {
        const string adoptedPvc = "reeve-wl-zwave-zwave-js-state-deadbeef";
        var retainedRowId = Guid.NewGuid();
        var instance = NewInstance(state: WorkloadInstanceState.PendingConfiguration);
        var harness = new Harness
        {
            ManifestVolumes = new[]
            {
                new WorkloadVolume { Id = "state", MountPath = "/data", Size = "1Gi", Criticality = VolumeCriticality.Critical },
            },
            ResolvedParameters = new Dictionary<string, string?>(StringComparer.Ordinal) { ["existingClaim.state"] = adoptedPvc },
        }.WithDesiredHash("hash-restore");
        harness.RetainedVolumeService.SeedActive(retainedRowId, GroupId, WorkloadId, "state", adoptedPvc);
        var engine = harness.Build();

        var outcome = await engine.ReconcileInstanceAsync(instance);

        harness.Provider.ApplyCountFor(instance.Id).Should().Be(1, "drift must drive an Apply");
        outcome.Action.Should().Be(WorkloadReconcileAction.Applied);
        harness.RetainedVolumeService.ReattachedById.Should().Contain(retainedRowId,
            "a successful restore-adoption apply reattaches the matching active retained row (so it is not re-offered next reinstall)");
    }

    [Fact]
    public async Task A_fresh_apply_without_adopted_volumes_reattaches_nothing()
    {
        var instance = NewInstance(state: WorkloadInstanceState.PendingConfiguration);
        var harness = new Harness
        {
            ManifestVolumes = new[]
            {
                new WorkloadVolume { Id = "state", MountPath = "/data", Size = "1Gi", Criticality = VolumeCriticality.Critical },
            },
        }.WithDesiredHash("hash-fresh");
        harness.RetainedVolumeService.SeedActive(Guid.NewGuid(), GroupId, WorkloadId, "state", "reeve-wl-zwave-zwave-js-state-deadbeef");
        var engine = harness.Build();

        await engine.ReconcileInstanceAsync(instance);

        harness.RetainedVolumeService.ReattachedById.Should().BeEmpty("a fresh apply (no adopted volume) reattaches nothing");
    }

    [Fact]
    public async Task Manual_handoff_apply_does_not_mark_the_retained_row_reattached()
    {
        const string adoptedName = "reeve-zwave-state-deadbeef";
        var retainedRowId = Guid.NewGuid();
        var instance = NewInstance(state: WorkloadInstanceState.PendingConfiguration);
        var harness = new Harness
        {
            RegisteredProvider = ManualProvider.Id,
            EffectiveProvider = ManualProvider.Descriptor,
            ManifestVolumes = new[]
            {
                new WorkloadVolume { Id = "state", MountPath = "/data", Size = "1Gi", Criticality = VolumeCriticality.Critical },
            },
            ResolvedParameters = new Dictionary<string, string?>(StringComparer.Ordinal) { ["existingClaim.state"] = adoptedName },
        }.WithDesiredHash("hash-manual-restore");
        harness.RetainedVolumeService.SeedActive(retainedRowId, GroupId, WorkloadId, "state", adoptedName);
        var engine = harness.Build();

        var outcome = await engine.ReconcileInstanceAsync(instance);

        outcome.Action.Should().Be(WorkloadReconcileAction.Applied, "the manual handoff apply still succeeds");
        instance.Provider.Should().Be(ManualProvider.Id);
        harness.RetainedVolumeService.ReattachedById.Should().BeEmpty(
            "a manual handoff apply mounts nothing — the retained row must stay ACTIVE/offerable, not falsely reattached");
        harness.RetainedVolumeService.SeededActiveRow(retainedRowId)!.IsActive.Should().BeTrue(
            "the abandoned manual restore leaves the retained row active and offerable");
    }

    [Fact]
    public async Task No_drift_does_not_apply()
    {
        var instance = NewInstance(state: WorkloadInstanceState.Deploying);
        instance.SetDesiredSpecHash("hash-stable", DateTimeOffset.UtcNow);
        var harness = new Harness().WithDesiredHash("hash-stable");
        harness.PreApply(instance, "hash-stable");
        var engine = harness.Build();

        await engine.ReconcileInstanceAsync(instance);

        harness.Provider.ApplyCountFor(instance.Id).Should().Be(1, "no drift ⇒ no NEW apply (only the pre-apply setup counts)");
    }

    [Fact]
    public async Task ReconciliationPaused_records_pending_and_does_not_apply()
    {
        var instance = NewInstance(state: WorkloadInstanceState.PendingConfiguration);
        var harness = new Harness { ReconciliationPaused = true }.WithDesiredHash("hash-paused");
        var engine = harness.Build();

        var outcome = await engine.ReconcileInstanceAsync(instance);

        harness.Provider.ApplyCountFor(instance.Id).Should().Be(0, "a paused reconciler never mutates the runtime");
        outcome.Reason.Should().Be(WorkloadReconcileReason.ReconciliationPaused);
        outcome.Action.Should().Be(WorkloadReconcileAction.DeferredMutation);
        outcome.PendingApply.Should().BeTrue();
        instance.DesiredSpecHash.Should().Be("hash-paused", "the pending desired hash is recorded for the UX");
        instance.State.Should().Be(WorkloadInstanceState.PendingConfiguration, "state is not driven while paused");
    }

    [Fact]
    public async Task ObserveOnly_records_pending_and_does_not_apply()
    {
        var instance = NewInstance(state: WorkloadInstanceState.PendingConfiguration, observeOnly: true);
        var harness = new Harness().WithDesiredHash("hash-observe");
        var engine = harness.Build();

        var outcome = await engine.ReconcileInstanceAsync(instance);

        harness.Provider.ApplyCountFor(instance.Id).Should().Be(0, "observe-only skips drift correction");
        outcome.Reason.Should().Be(WorkloadReconcileReason.ObserveOnly);
        outcome.PendingApply.Should().BeTrue();
        instance.DesiredSpecHash.Should().Be("hash-observe");
        instance.State.Should().Be(WorkloadInstanceState.PendingConfiguration);
    }

    [Fact]
    public async Task ExternallyManaged_on_a_managed_provider_is_excluded_from_managed_apply_and_never_re_adopted()
    {
        var instance = NewInstance(state: WorkloadInstanceState.Healthy);
        var harness = new Harness { HealthMode = WorkloadHealthMode.Http }.WithDesiredHash("hash-desired");
        harness.PreApply(instance, "hash-applied");
        var now = DateTimeOffset.UtcNow;
        instance.TransitionTo(WorkloadInstanceState.Switching, WorkloadStateReason.HealthObserved, null, now);
        instance.TransitionTo(WorkloadInstanceState.ExternallyManaged, WorkloadStateReason.ExternalHealthObserved, null, now);
        instance.Provider.Should().Be(KubernetesProvider.Id, "the disown preserved the ORIGINAL managed provider — this is NOT the Manual provider");
        harness.Provider.SimulateReady(instance.Id);
        var engine = harness.Build();

        var outcome = await engine.ReconcileInstanceAsync(instance);

        harness.Provider.ApplyCountFor(instance.Id).Should().Be(1, "an ExternallyManaged instance is excluded from managed apply regardless of provider — only the pre-apply setup counts");
        harness.Provider.IsTracked(instance.Id).Should().BeTrue("the reconciler never tears down an ExternallyManaged (disowned) instance");
        instance.State.Should().Be(WorkloadInstanceState.ExternallyManaged, "a disowned instance is observe-only and is never re-adopted back to Healthy, whatever its retained Provider");
        instance.Provider.Should().Be(KubernetesProvider.Id, "the reconciler leaves the preserved provider untouched");
        outcome.Reason.Should().Be(WorkloadReconcileReason.ObserveOnly);
        outcome.PendingApply.Should().BeTrue();
        instance.DesiredSpecHash.Should().Be("hash-desired", "the pending desired hash is recorded for the UX / re-adoption");
    }

    [Fact]
    public async Task Switching_instance_with_a_provider_ready_read_stays_Switching_and_is_never_driven()
    {
        var instance = NewInstance(state: WorkloadInstanceState.Healthy);
        var harness = new Harness { HealthMode = WorkloadHealthMode.Http }.WithDesiredHash("hash-live");
        harness.PreApply(instance, "hash-live");
        var now = DateTimeOffset.UtcNow;
        instance.TransitionTo(WorkloadInstanceState.Switching, WorkloadStateReason.HealthObserved, null, now);
        instance.State.Should().Be(WorkloadInstanceState.Switching);
        harness.Provider.SimulateReady(instance.Id);
        var engine = harness.Build();

        await engine.ReconcileInstanceAsync(instance);

        instance.State.Should().Be(WorkloadInstanceState.Switching,
            "a mid-switch instance is ownership-frozen — the reconciler observes but never drives it");
        harness.Provider.IsTracked(instance.Id).Should().BeTrue("the reconciler never tears down a mid-switch instance");
        harness.Provider.ApplyCountFor(instance.Id).Should().Be(1, "a frozen instance is excluded from managed apply — only the pre-apply setup counts");
    }

    [Fact]
    public async Task Disowned_managed_adapter_reporting_a_version_skew_stays_ExternallyManaged_never_readopted()
    {
        var instance = NewInstance(state: WorkloadInstanceState.Healthy);
        var harness = new Harness
        {
            HealthMode = new WorkloadHealthMode("adapter-session"),
            InstalledGroupVersion = "2.0.0",
        }.WithDesiredHash("hash-live");
        harness.PreApply(instance, "hash-live");
        var now = DateTimeOffset.UtcNow;
        instance.TransitionTo(WorkloadInstanceState.Switching, WorkloadStateReason.HealthObserved, null, now);
        instance.TransitionTo(WorkloadInstanceState.ExternallyManaged, WorkloadStateReason.ExternalHealthObserved, null, now);
        instance.Provider.Should().Be(KubernetesProvider.Id, "the disown PRESERVED the managed provider — NOT Manual");
        harness.Provider.SimulateReady(instance.Id);
        harness.Presence.SeedWithAppVersion(instance.Id, "hash-live", appVersion: "1.0.0");
        var engine = harness.Build();

        await engine.ReconcileInstanceAsync(instance);

        instance.State.Should().Be(WorkloadInstanceState.ExternallyManaged,
            "a disowned managed instance is held ExternallyManaged BEFORE the version-skew check — never re-adopted to VersionSkew");
        instance.State.Should().NotBe(WorkloadInstanceState.VersionSkew);
        harness.Provider.ApplyCountFor(instance.Id).Should().Be(1, "no re-apply — the disowned runtime is observe-only");
    }

    [Fact]
    public async Task Frozen_instance_whose_provider_status_throws_stays_frozen_not_Unknown()
    {
        var instance = NewInstance(state: WorkloadInstanceState.Healthy);
        var harness = new Harness { HealthMode = WorkloadHealthMode.Http }.WithDesiredHash("hash-live");
        harness.PreApply(instance, "hash-live");
        var now = DateTimeOffset.UtcNow;
        instance.TransitionTo(WorkloadInstanceState.Switching, WorkloadStateReason.HealthObserved, null, now);
        instance.TransitionTo(WorkloadInstanceState.ExternallyManaged, WorkloadStateReason.ExternalHealthObserved, null, now);
        harness.ThrowingProvider.ThrowOnStatus = true;
        var engine = harness.Build();

        await engine.ReconcileInstanceAsync(instance);

        instance.State.Should().Be(WorkloadInstanceState.ExternallyManaged,
            "a provider blip must never move an ownership-frozen instance to Unknown (MarkUnreachable hold)");
    }

    [Fact]
    public async Task AdapterSession_provider_ready_with_matching_presence_becomes_Healthy()
    {
        var instance = NewInstance(state: WorkloadInstanceState.Deploying);
        instance.SetDesiredSpecHash("hash-live", DateTimeOffset.UtcNow);
        var harness = new Harness().WithDesiredHash("hash-live");
        harness.PreApply(instance, "hash-live");
        harness.Provider.SimulateReady(instance.Id);
        harness.Presence.SeedMatching(instance.Id, "hash-live");
        var engine = harness.Build();

        await engine.ReconcileInstanceAsync(instance);

        instance.State.Should().Be(WorkloadInstanceState.Healthy);
        instance.LastHealthyAtUtc.Should().NotBeNull("Healthy stamps LastHealthyAtUtc");
    }

    [Fact]
    public async Task AdapterSession_presence_spec_hash_mismatch_is_not_Healthy()
    {
        var instance = NewInstance(state: WorkloadInstanceState.Deploying);
        instance.SetDesiredSpecHash("hash-new", DateTimeOffset.UtcNow);
        var harness = new Harness().WithDesiredHash("hash-new");
        harness.PreApply(instance, "hash-new");
        harness.Provider.SimulateReady(instance.Id);
        harness.Presence.SeedMatching(instance.Id, "hash-OLD");
        var engine = harness.Build();

        await engine.ReconcileInstanceAsync(instance);

        instance.State.Should().Be(WorkloadInstanceState.Deploying, "a mismatched session is not the Healthy anchor");
        instance.LastHealthyAtUtc.Should().BeNull();
    }

    [Fact]
    public async Task Http_mode_becomes_Healthy_on_provider_ready_without_a_session()
    {
        var instance = NewInstance(state: WorkloadInstanceState.Deploying);
        instance.SetDesiredSpecHash("hash-http", DateTimeOffset.UtcNow);
        var harness = new Harness { HealthMode = WorkloadHealthMode.Http }.WithDesiredHash("hash-http");
        harness.PreApply(instance, "hash-http");
        harness.Provider.SimulateReady(instance.Id);
        var engine = harness.Build();

        await engine.ReconcileInstanceAsync(instance);

        instance.State.Should().Be(WorkloadInstanceState.Healthy);
        harness.Presence.ReadCount.Should().Be(0, "http mode never reads the presence store");
    }

    [Fact]
    public async Task Provider_status_throw_marks_Unknown_and_stops_mutation()
    {
        var instance = NewInstance(state: WorkloadInstanceState.Healthy);
        instance.SetDesiredSpecHash("hash-live", DateTimeOffset.UtcNow);
        var harness = new Harness().WithDesiredHash("hash-live");
        harness.PreApply(instance, "hash-live");
        harness.Provider.SimulateReady(instance.Id);
        harness.ThrowingProvider.ThrowOnStatus = true;
        var engine = harness.Build();

        var outcome = await engine.ReconcileInstanceAsync(instance);

        outcome.Action.Should().Be(WorkloadReconcileAction.MarkedUnknown);
        outcome.Reason.Should().Be(WorkloadReconcileReason.ProviderUnreachable);
        instance.State.Should().Be(WorkloadInstanceState.Unknown);
        instance.CanMutate.Should().BeFalse("no mutations are allowed while Unknown");
    }

    [Fact]
    public async Task Unknown_instance_with_drift_does_not_apply_but_reads_status()
    {
        var instance = NewInstance(state: WorkloadInstanceState.Deploying);
        instance.SetProvider(KubernetesProvider.Id, DateTimeOffset.UtcNow);
        instance.TransitionTo(WorkloadInstanceState.Unknown, WorkloadStateReason.ProviderUnreachable, "lost", DateTimeOffset.UtcNow);
        var harness = new Harness().WithDesiredHash("hash-drift");
        var engine = harness.Build();

        var outcome = await engine.ReconcileInstanceAsync(instance);

        harness.Provider.ApplyCountFor(instance.Id).Should().Be(0, "an Unknown instance cannot mutate");
        outcome.Reason.Should().Be(WorkloadReconcileReason.HealthEvaluated, "the pass reports the move it made");
        outcome.PendingApply.Should().BeTrue();
        instance.State.Should().NotBe(WorkloadInstanceState.Unknown,
            "a provider that answers ends Unknown; without the read a drifted instance stayed Unknown for good");
    }

    [Fact]
    public async Task Presence_store_fault_does_not_downgrade_a_Healthy_instance()
    {
        var instance = NewInstance(state: WorkloadInstanceState.Healthy);
        instance.SetDesiredSpecHash("hash-live", DateTimeOffset.UtcNow);
        var harness = new Harness().WithDesiredHash("hash-live");
        harness.PreApply(instance, "hash-live");
        harness.Provider.SimulateReady(instance.Id);
        harness.Presence.FaultReads = true;
        var engine = harness.Build();

        await engine.ReconcileInstanceAsync(instance);

        instance.State.Should().Be(WorkloadInstanceState.Healthy,
            "a presence-store fault is UNKNOWN, never offline — it must not flip Healthy → Degraded (C-recon)");
    }

    private static WorkloadStatus AttachmentMissingStatus(string networkName = "vlan100-dhcp")
        => KubernetesStatusMapper.Map(new KubernetesWorkloadRaw
        {
            Found = true,
            Kind = "Deployment",
            DesiredReplicas = 1,
            ReadyReplicas = 0,
            AllPodsRunning = false,
            HasPods = true,
            PodsRead = true,
            Events = new (DateTimeOffset, string, string, bool)[]
            {
                (new DateTimeOffset(2026, 7, 29, 10, 0, 0, TimeSpan.Zero), "FailedCreatePodSandBox",
                    "Failed to create pod sandbox: rpc error: code = Unknown desc = failed to setup network for "
                    + $"sandbox \"9f1c3a\": plugin type=\"multus-shim\" failed (add): error adding container to network \"{networkName}\": "
                    + "failed to send CNI request: dial unix /run/cni/dhcp.sock: connect: no such file or directory",
                    true),
            },
        });

    [Fact]
    public async Task AC6_attachment_missing_message_naming_the_network_reaches_the_instance_state_message()
    {
        var attachmentMissing = AttachmentMissingStatus();
        attachmentMissing.Phase.Should().Be(WorkloadProviderPhase.Failed, "guard: the mapper produces a Failed status");

        var instance = NewInstance(state: WorkloadInstanceState.Deploying);
        instance.SetDesiredSpecHash("hash-live", DateTimeOffset.UtcNow);
        var harness = new Harness().WithDesiredHash("hash-live");
        harness.PreApply(instance, "hash-live");
        harness.Provider.SetStatus(
            instance.Id, attachmentMissing.Phase, providerReady: false, message: attachmentMissing.Message);
        var engine = harness.Build();

        await engine.ReconcileInstanceAsync(instance);

        instance.State.Should().Be(WorkloadInstanceState.Failed,
            "the pod can never become ready — this is a failure, not an indefinite ContainerCreating");
        instance.StateMessage.Should().NotBe("Provider reports the workload failed.",
            "the generic fallback would erase the network name — the whole point of naming it");
        instance.StateMessage.Should().Contain("vlan100-dhcp", "the operator-facing state NAMES the network");

        var condition = KubernetesStatusMapper.TryReadAttachmentMissing(instance.StateMessage);
        condition.Should().NotBeNull("the TYPED condition survives the hop from the mapper to the instance");
        condition!.Id.Should().Be(KubernetesWorkloadCondition.AttachmentMissingId);
        condition.NetworkName.Should().Be("vlan100-dhcp");

        harness.Repository.ProgressSnapshots.Should().Contain(
            s => s.Message != null && s.Message.Contains("vlan100-dhcp", StringComparison.Ordinal),
            "the named message must be PERSISTED (that is what the operator's UI reads), not only computed");
    }

    [Fact]
    public async Task AC5_no_reconcile_pass_performs_an_attachment_state_read()
    {
        var instance = NewInstance(state: WorkloadInstanceState.PendingConfiguration);
        var harness = new Harness().WithDesiredHash("hash-live");
        var trap = new AttachmentObservationTrap(harness.ThrowingProvider);
        harness.ProviderOverride = trap;
        var engine = harness.Build();

        var applyPass = await engine.ReconcileInstanceAsync(instance);
        applyPass.Should().NotBeNull();
        instance.AppliedSpecHash.Should().Be("hash-live", "the apply really happened over the trap");

        harness.Provider.SimulateReady(instance.Id);
        harness.Presence.SeedMatching(instance.Id, "hash-live");
        var steadyPass = await engine.ReconcileInstanceAsync(instance);

        trap.ObservedMembers.Should().NotBeEmpty(
            "the trap must actually have been driven by the engine, or a 'was not called' assertion proves nothing");
        trap.AttachmentStateReads.Should().BeEmpty(
            "no pod network-status read, no NetworkSettings.Networks read, no per-attachment observation call");

        steadyPass.Should().NotBeNull("the pass COMPLETES — the posture is 'reconcile without observing', not 'do not reconcile'");
        instance.State.Should().Be(WorkloadInstanceState.Healthy);

        AttachmentObservationTrap.AttachmentStateReadMembersOn(typeof(IWorkloadProvider)).Should().BeEmpty(
            "the provider SPI the reconcile engine drives exposes no attachment-state observation");
        AttachmentObservationTrap.AttachmentStateReadMembersOn(typeof(WorkloadStatus)).Should().BeEmpty(
            "the provider status the engine consumes carries no observed attachment state");
    }

    [Fact]
    public async Task Provider_phase_Failed_drives_Failed()
    {
        var instance = NewInstance(state: WorkloadInstanceState.Deploying);
        instance.SetDesiredSpecHash("hash-live", DateTimeOffset.UtcNow);
        var harness = new Harness().WithDesiredHash("hash-live");
        harness.PreApply(instance, "hash-live");
        harness.Provider.SetStatus(instance.Id, WorkloadProviderPhase.Failed, providerReady: false, message: "CrashLoopBackOff");
        var engine = harness.Build();

        await engine.ReconcileInstanceAsync(instance);

        instance.State.Should().Be(WorkloadInstanceState.Failed);
    }

    [Fact]
    public async Task Failed_can_recover_to_Healthy_when_the_provider_reports_ready_and_health()
    {
        var instance = NewInstance(state: WorkloadInstanceState.Failed);
        instance.SetDesiredSpecHash("hash-live", DateTimeOffset.UtcNow);
        var harness = new Harness().WithDesiredHash("hash-live");
        harness.PreApply(instance, "hash-live");
        harness.Provider.SimulateReady(instance.Id);
        harness.Presence.SeedMatching(instance.Id, "hash-live");
        var engine = harness.Build();

        await engine.ReconcileInstanceAsync(instance);

        instance.State.Should().Be(WorkloadInstanceState.Healthy, "Failed is re-evaluated every pass — no terminal Failed");
    }

    [Fact]
    public async Task Operator_paused_instance_stays_Suspended_across_a_resync()
    {
        var instance = NewInstance(state: WorkloadInstanceState.Suspended);
        instance.SetDesiredSpecHash("hash-live", DateTimeOffset.UtcNow);
        var harness = new Harness
        {
            HealthMode = WorkloadHealthMode.Http,
            ManifestNetwork = new WorkloadNetwork { HostNetwork = true },
        }.WithDesiredHash("hash-live");
        harness.PreApply(instance, "hash-live");
        harness.Provider.SimulateReady(instance.Id);
        var engine = harness.Build();

        await engine.ReconcileInstanceAsync(instance);

        instance.State.Should().Be(WorkloadInstanceState.Suspended,
            "an operator pause must survive the health pass — the reconciler cannot cancel a deliberate pause");
        instance.StateReason.Should().Be(WorkloadStateReason.OperatorPaused);
    }

    [Fact]
    public async Task A_successful_apply_emits_Action_Applied()
    {
        var instance = NewInstance(state: WorkloadInstanceState.PendingConfiguration);
        var harness = new Harness().WithDesiredHash("hash-v1");
        var engine = harness.Build();

        var outcome = await engine.ReconcileInstanceAsync(instance);

        harness.Provider.ApplyCountFor(instance.Id).Should().Be(1);
        outcome.Action.Should().Be(WorkloadReconcileAction.Applied,
            "the owned-unit's staggered-rollout barrier keys on Action==Applied");
        instance.AppliedSpecHash.Should().Be("hash-v1");
    }

    [Fact]
    public async Task A_failed_apply_is_re_applied_on_the_next_pass()
    {
        var instance = NewInstance(state: WorkloadInstanceState.PendingConfiguration);
        var harness = new Harness().WithDesiredHash("hash-v1");
        harness.ThrowingProvider.FailApplyCount = 1;
        var engine = harness.Build();

        var pass1 = await engine.ReconcileInstanceAsync(instance);
        instance.AppliedSpecHash.Should().BeNull("a failed apply must NOT stamp AppliedSpecHash");
        instance.DesiredSpecHash.Should().Be("hash-v1", "the pending desired hash is recorded");
        pass1.Action.Should().NotBe(WorkloadReconcileAction.Applied);

        var pass2 = await engine.ReconcileInstanceAsync(instance);
        harness.ThrowingProvider.ApplyCallCount.Should().Be(2, "the failed apply is retried next pass");
        instance.AppliedSpecHash.Should().Be("hash-v1");
        pass2.Action.Should().Be(WorkloadReconcileAction.Applied);
    }

    [Fact]
    public async Task A_resumed_instance_re_applies_its_queued_spec()
    {
        var instance = NewInstance(state: WorkloadInstanceState.Deploying);
        instance.SetDesiredSpecHash("hash-live", DateTimeOffset.UtcNow);
        var harness = new Harness().WithDesiredHash("hash-live");
        harness.PreApply(instance, "hash-live");
        instance.SetAppliedSpecHash(null, DateTimeOffset.UtcNow);
        var engine = harness.Build();

        await engine.ReconcileInstanceAsync(instance);

        harness.Provider.ApplyCountFor(instance.Id).Should().Be(2,
            "clearing AppliedSpecHash forces a re-apply even though DesiredSpecHash is unchanged");
        instance.AppliedSpecHash.Should().Be("hash-live");
    }

    [Fact]
    public async Task A_config_compute_throw_holds_PendingConfiguration_without_crashing()
    {
        var instance = NewInstance(state: WorkloadInstanceState.Deploying);
        instance.SetDesiredSpecHash("hash-live", DateTimeOffset.UtcNow);
        var harness = new Harness().WithDesiredHash("hash-live");
        harness.PreApply(instance, "hash-live");
        harness.ResolverThrows = true;
        var engine = harness.Build();

        var outcome = await engine.ReconcileInstanceAsync(instance);

        outcome.Reason.Should().Be(WorkloadReconcileReason.ConfigurationRequired,
            "a config compute failure classifies as a resolvable-config problem, not a crash or a teardown");
        instance.State.Should().Be(WorkloadInstanceState.PendingConfiguration);
        harness.Provider.IsTracked(instance.Id).Should().BeTrue("a compute failure never tears the runtime down");
    }

    [Fact]
    public async Task A_non_config_compute_throw_marks_Failed_without_crashing()
    {
        var instance = NewInstance(state: WorkloadInstanceState.Deploying);
        instance.SetDesiredSpecHash("hash-live", DateTimeOffset.UtcNow);
        var harness = new Harness
        {
            ResolverThrows = true,
            ResolverException = new InvalidOperationException("unexpected derivation fault"),
        }.WithDesiredHash("hash-live");
        harness.PreApply(instance, "hash-live");
        var engine = harness.Build();

        var outcome = await engine.ReconcileInstanceAsync(instance);

        outcome.Reason.Should().Be(WorkloadReconcileReason.DesiredSpecComputeFailed);
        instance.State.Should().Be(WorkloadInstanceState.Failed, "Failed is re-evaluated every pass — not a crash");
    }

    [Fact]
    public async Task A_missing_required_parameter_holds_PendingConfiguration_and_does_not_apply()
    {
        var instance = NewInstance(state: WorkloadInstanceState.PendingConfiguration);
        var harness = new Harness
        {
            ManifestParameters = new[]
            {
                new WorkloadParameter { Name = "devicePath", Type = WorkloadParameterType.DevicePath, Required = true },
            },
            ResolvedParameters = new Dictionary<string, string?> { ["devicePath"] = null },
        }.WithDesiredHash("hash-v1");
        var engine = harness.Build();

        var outcome = await engine.ReconcileInstanceAsync(instance);

        harness.Provider.ApplyCountFor(instance.Id).Should().Be(0,
            "an under-configured instance must never apply a runtime without its required device mount");
        outcome.Reason.Should().Be(WorkloadReconcileReason.ConfigurationRequired);
        outcome.Message.Should().Contain("devicePath");
        instance.State.Should().Be(WorkloadInstanceState.PendingConfiguration);
    }

    [Fact]
    public async Task An_unresolved_restore_binding_holds_PendingConfiguration_even_with_no_required_params()
    {
        var instance = NewInstance(state: WorkloadInstanceState.PendingConfiguration);
        instance.SetRestoreBindingUnresolved(true, DateTimeOffset.UtcNow);
        var harness = new Harness().WithDesiredHash("hash-v1");
        var engine = harness.Build();

        var outcome = await engine.ReconcileInstanceAsync(instance);

        harness.Provider.ApplyCountFor(instance.Id).Should().Be(0,
            "an unresolved restore binding must NEVER deploy a fresh empty volume, even with no required params");
        outcome.Action.Should().Be(WorkloadReconcileAction.DeferredMutation);
        outcome.Reason.Should().Be(WorkloadReconcileReason.ConfigurationRequired);
        instance.State.Should().Be(WorkloadInstanceState.PendingConfiguration,
            "the instance stays parked until uninstall+reinstall (no re-supply path)");
        instance.AppliedSpecHash.Should().BeNull("nothing was applied — the runtime is never created");
    }

    [Fact]
    public async Task A_cleared_restore_binding_flag_does_not_hold_the_instance()
    {
        var instance = NewInstance(state: WorkloadInstanceState.PendingConfiguration);
        instance.RestoreBindingUnresolved.Should().BeFalse("the flag defaults false for a normal install");
        var harness = new Harness().WithDesiredHash("hash-v1");
        var engine = harness.Build();

        var outcome = await engine.ReconcileInstanceAsync(instance);

        harness.Provider.ApplyCountFor(instance.Id).Should().Be(1,
            "a normal (not-restore-blocked) instance still applies");
        outcome.Action.Should().Be(WorkloadReconcileAction.Applied);
    }

    [Fact]
    public async Task A_held_restore_claim_with_a_lost_flag_and_no_adopted_binding_is_RE_DERIVED_as_held()
    {
        const string claimedPvc = "reeve-wl-zwave-zwave-js-state-lost0001";
        var instance = NewInstance(state: WorkloadInstanceState.PendingConfiguration);
        instance.RestoreBindingUnresolved.Should().BeFalse("precondition: the flag-write was lost (false)");
        var harness = new Harness().WithDesiredHash("hash-v1");
        harness.RetainedVolumeService.SeedActiveClaimedBy(
            Guid.NewGuid(), GroupId, WorkloadId, "state", claimedPvc, instance.Id);
        var engine = harness.Build();

        var outcome = await engine.ReconcileInstanceAsync(instance);

        harness.Provider.ApplyCountFor(instance.Id).Should().Be(0,
            "a held-claim restore-in-flight with a lost flag must NOT deploy a fresh empty volume");
        outcome.Reason.Should().Be(WorkloadReconcileReason.ConfigurationRequired);
        instance.RestoreBindingUnresolved.Should().BeTrue(
            "the reconciler RE-SETS the durable flag so the hold self-heals across passes");
    }

    [Fact]
    public async Task A_held_claim_WITH_a_matching_adopted_binding_is_NOT_re_derived_as_held_and_applies()
    {
        const string adoptedPvc = "reeve-wl-zwave-zwave-js-state-ok000001";
        var instance = NewInstance(state: WorkloadInstanceState.PendingConfiguration);
        var harness = new Harness
        {
            ManifestVolumes = new[]
            {
                new WorkloadVolume { Id = "state", MountPath = "/data", Size = "1Gi", Criticality = VolumeCriticality.Critical },
            },
            ResolvedParameters = new Dictionary<string, string?>(StringComparer.Ordinal) { ["existingClaim.state"] = adoptedPvc },
        }.WithDesiredHash("hash-restore");
        harness.RetainedVolumeService.SeedActiveClaimedBy(
            Guid.NewGuid(), GroupId, WorkloadId, "state", adoptedPvc, instance.Id);
        var engine = harness.Build();

        await engine.ReconcileInstanceAsync(instance);

        harness.Provider.ApplyCountFor(instance.Id).Should().Be(1,
            "a legitimate in-flight restore (its adopted claim IS in the desired spec) applies — the backstop only holds a LOST binding");
        instance.RestoreBindingUnresolved.Should().BeFalse("a legitimate restore is never re-derived as held");
    }

    [Fact]
    public async Task A_resolved_required_parameter_lets_the_instance_apply()
    {
        var instance = NewInstance(state: WorkloadInstanceState.PendingConfiguration);
        var harness = new Harness
        {
            ManifestParameters = new[]
            {
                new WorkloadParameter { Name = "devicePath", Type = WorkloadParameterType.DevicePath, Required = true },
            },
            ResolvedParameters = new Dictionary<string, string?> { ["devicePath"] = "/dev/ttyUSB0" },
        }.WithDesiredHash("hash-v1");
        var engine = harness.Build();

        var outcome = await engine.ReconcileInstanceAsync(instance);

        harness.Provider.ApplyCountFor(instance.Id).Should().Be(1, "a fully-configured instance applies");
        outcome.Action.Should().Be(WorkloadReconcileAction.Applied);
    }

    [Fact]
    public async Task A_multi_device_slot_bound_at_one_index_satisfies_the_required_parameter_gate()
    {
        var instance = NewInstance(state: WorkloadInstanceState.PendingConfiguration);
        var harness = new Harness
        {
            ManifestParameters = new[]
            {
                new WorkloadParameter { Name = "acc", Type = WorkloadParameterType.DevicePath, Required = true },
            },
            ManifestPrivileges = new WorkloadPrivileges
            {
                Devices = new[] { new WorkloadDevice { Parameter = "acc", MaxCount = 3 } },
            },
            ResolvedParameters = new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["acc[1]"] = "/dev/dri/renderD128",
            },
        }.WithDesiredHash("hash-v1");
        var engine = harness.Build();

        var outcome = await engine.ReconcileInstanceAsync(instance);

        outcome.Reason.Should().NotBe(WorkloadReconcileReason.ConfigurationRequired,
            "an index the desired-spec factory renders satisfies the slot's required parameter");
        outcome.Message.Should().NotContain("Required parameter(s) not yet set");
        outcome.Action.Should().Be(WorkloadReconcileAction.Applied);
        harness.Provider.ApplyCountFor(instance.Id).Should().Be(1);
    }

    [Fact]
    public async Task A_required_parameter_with_a_manifest_default_applies_via_the_render_resolver()
    {
        var instance = NewInstance(state: WorkloadInstanceState.PendingConfiguration);
        var harness = new Harness
        {
            ManifestParameters = new[]
            {
                new WorkloadParameter { Name = "logLevel", Type = WorkloadParameterType.Text, Required = true, Default = "info" },
            },
            ResolvedParameters = new Dictionary<string, string?>(StringComparer.Ordinal),
            UseRealResolver = true,
        }.WithDesiredHash("hash-v1");
        var engine = harness.Build();

        var outcome = await engine.ReconcileInstanceAsync(instance);

        harness.Provider.ApplyCountFor(instance.Id).Should().Be(1,
            "a required param satisfied by its manifest default is NOT missing — the render resolver applies the default");
        outcome.Action.Should().Be(WorkloadReconcileAction.Applied);
    }

    [Fact]
    public async Task A_poisoned_stored_device_path_classifies_ConfigurationRequired_on_the_engine_path()
    {
        var instance = NewInstance(state: WorkloadInstanceState.Deploying);
        instance.SetDesiredSpecHash("hash-live", DateTimeOffset.UtcNow);
        var harness = new Harness
        {
            ManifestParameters = new[]
            {
                new WorkloadParameter { Name = "devicePath", Type = WorkloadParameterType.DevicePath, Required = true },
            },
            ManifestPrivileges = new WorkloadPrivileges { Devices = new[] { new WorkloadDevice { Parameter = "devicePath" } } },
            ResolvedParameters = new Dictionary<string, string?>(StringComparer.Ordinal) { ["devicePath"] = "/etc/shadow" },
            UseRealResolver = true,
        }.WithDesiredHash("hash-live");
        harness.PreApply(instance, "hash-live");
        var engine = harness.Build();

        var outcome = await engine.ReconcileInstanceAsync(instance);

        outcome.Reason.Should().Be(WorkloadReconcileReason.ConfigurationRequired,
            "a poisoned stored path (outside /dev/**) throws at render → the ConfigurationRequired classification is live, not dead code");
        instance.State.Should().Be(WorkloadInstanceState.PendingConfiguration);
        harness.Provider.IsTracked(instance.Id).Should().BeTrue("a config compute failure never tears the runtime down");
    }

    [Fact]
    public async Task A_password_reference_failure_holds_configuration_and_never_applies()
    {
        const string reference = "ref:provider:app#password";
        var instance = NewInstance(state: WorkloadInstanceState.Deploying);
        var harness = new Harness
        {
            ManifestParameters = [new WorkloadParameter { Name = "dbPassword", Type = WorkloadParameterType.Password }],
            ResolvedParameters = new Dictionary<string, string?>(StringComparer.Ordinal) { ["dbPassword"] = reference },
            ParameterReferenceResolver = new StaticReferenceResolver(new WorkloadReferenceResolution.Failure(
                WorkloadReferenceFailureKind.NotFound, "secret does not exist")),
        };
        var engine = harness.Build();

        var outcome = await engine.ReconcileInstanceAsync(instance);

        outcome.Reason.Should().Be(WorkloadReconcileReason.ConfigurationRequired);
        instance.State.Should().Be(WorkloadInstanceState.PendingConfiguration);
        outcome.Message.Should().Contain(reference).And.Contain("NotFound").And.Contain("secret does not exist");
        harness.Provider.ApplyCountFor(instance.Id).Should().Be(0);
        harness.Provider.IsTracked(instance.Id).Should().BeFalse();
    }

    [Fact]
    public async Task An_unavailable_secret_store_keeps_a_healthy_workload_healthy_and_saves_nothing()
    {
        var instance = NewInstance(state: WorkloadInstanceState.Healthy);
        var harness = new Harness
        {
            ManifestParameters = [new WorkloadParameter { Name = "dbPassword", Type = WorkloadParameterType.Password }],
            ResolvedParameters = new Dictionary<string, string?>(StringComparer.Ordinal) { ["dbPassword"] = "ref:provider:app#password" },
            ParameterReferenceResolver = new StaticReferenceResolver(new WorkloadReferenceResolution.Failure(
                WorkloadReferenceFailureKind.Unavailable, "secret store timed out")),
        }.WithDesiredHash("hash-live");
        harness.PreApply(instance, "hash-live");
        var engine = harness.Build();

        var outcome = await engine.ReconcileInstanceAsync(instance);

        outcome.Reason.Should().Be(WorkloadReconcileReason.ParameterReferenceUnavailable);
        outcome.Action.Should().Be(WorkloadReconcileAction.NoOp);
        outcome.Message.Should().Contain("secret store timed out");
        instance.State.Should().Be(WorkloadInstanceState.Healthy);
        harness.Repository.SavedCount.Should().Be(0);
    }

    [Fact]
    public async Task A_workload_held_for_a_missing_secret_leaves_PendingConfiguration_once_the_secret_resolves_again()
    {
        var instance = NewInstance(state: WorkloadInstanceState.Healthy);
        var resolver = new MutableReferenceResolver(new WorkloadReferenceResolution.Failure(
            WorkloadReferenceFailureKind.NotFound, "secret does not exist"));
        var harness = new Harness
        {
            ManifestParameters = [new WorkloadParameter { Name = "dbPassword", Type = WorkloadParameterType.Password }],
            ResolvedParameters = new Dictionary<string, string?>(StringComparer.Ordinal) { ["dbPassword"] = "ref:provider:app#password" },
            ParameterReferenceResolver = resolver,
        }.WithDesiredHash("hash-live");
        harness.PreApply(instance, "hash-live");
        harness.Provider.SimulateReady(instance.Id);
        harness.Presence.SeedMatching(instance.Id, "hash-live");
        var engine = harness.Build();

        await engine.ReconcileInstanceAsync(instance);
        instance.State.Should().Be(WorkloadInstanceState.PendingConfiguration);

        resolver.Result = new WorkloadReferenceResolution.Success("secret", IsVersionPinned: true);
        var applies = harness.ThrowingProvider.ApplyCallCount;
        await engine.ReconcileInstanceAsync(instance);

        instance.State.Should().Be(WorkloadInstanceState.Healthy);
        harness.ThrowingProvider.ApplyCallCount.Should().Be(applies, "the running spec is already the desired one");
    }

    [Fact]
    public async Task A_held_workload_stays_PendingConfiguration_while_reconciliation_is_paused()
    {
        var instance = NewInstance(state: WorkloadInstanceState.Healthy);
        var harness = new Harness { ReconciliationPaused = true }.WithDesiredHash("hash-live");
        harness.PreApply(instance, "hash-live");
        harness.Provider.SimulateReady(instance.Id);
        harness.Presence.SeedMatching(instance.Id, "hash-live");
        instance.TransitionTo(WorkloadInstanceState.PendingConfiguration, WorkloadStateReason.ConfigRequired, "held", DateTimeOffset.UtcNow);
        var engine = harness.Build();

        await engine.ReconcileInstanceAsync(instance);

        instance.State.Should().Be(WorkloadInstanceState.PendingConfiguration);
    }

    [Theory]
    [InlineData("unrelated parameter edit")]
    [InlineData("relocation")]
    public async Task Changed_unpinned_reference_holds_an_unrelated_reconcile_apply(string cause)
    {
        var instance = NewInstance(state: WorkloadInstanceState.Deploying);
        var resolver = new MutableReferenceResolver(new WorkloadReferenceResolution.Success("key-one"));
        var harness = new Harness
        {
            ManifestParameters = [
                new WorkloadParameter { Name = "dbPassword", Type = WorkloadParameterType.Password },
                new WorkloadParameter { Name = "logLevel", Type = WorkloadParameterType.Text },
            ],
            ResolvedParameters = new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["dbPassword"] = "ref:provider:app#password",
                ["logLevel"] = "info",
            },
            ParameterReferenceResolver = resolver,
        };
        var engine = harness.Build();

        var initial = await engine.ReconcileInstanceAsync(instance);
        initial.Action.Should().Be(WorkloadReconcileAction.Applied);
        var appliedCalls = harness.ThrowingProvider.ApplyCallCount;

        resolver.Result = new WorkloadReferenceResolution.Success("key-two");
        instance.SetAppliedSpecHash(null, DateTimeOffset.UtcNow);
        var held = await engine.ReconcileInstanceAsync(instance);

        held.Reason.Should().Be(WorkloadReconcileReason.ConfigurationRequired, cause);
        held.Message.Should().Contain("dbPassword").And.Contain("reference value changed; roll out to apply");
        instance.State.Should().Be(WorkloadInstanceState.PendingConfiguration);
        harness.ThrowingProvider.ApplyCallCount.Should().Be(appliedCalls, "a changed reference must not reach provider ApplyAsync");
    }

    [Fact]
    public async Task Version_pinned_reference_is_not_held_when_an_apply_is_otherwise_due()
    {
        var instance = NewInstance(state: WorkloadInstanceState.Deploying);
        var resolver = new MutableReferenceResolver(new WorkloadReferenceResolution.Success("key-one", IsVersionPinned: true));
        var harness = new Harness
        {
            ManifestParameters = [new WorkloadParameter { Name = "dbPassword", Type = WorkloadParameterType.Password }],
            ResolvedParameters = new Dictionary<string, string?> { ["dbPassword"] = "ref:provider:app#password@v1" },
            ParameterReferenceResolver = resolver,
        };
        var engine = harness.Build();
        (await engine.ReconcileInstanceAsync(instance)).Action.Should().Be(WorkloadReconcileAction.Applied);
        var appliedCalls = harness.ThrowingProvider.ApplyCallCount;

        resolver.Result = new WorkloadReferenceResolution.Success("different-value", IsVersionPinned: true);
        instance.SetAppliedSpecHash(null, DateTimeOffset.UtcNow);
        var next = await engine.ReconcileInstanceAsync(instance);

        next.Action.Should().Be(WorkloadReconcileAction.Applied);
        harness.ThrowingProvider.ApplyCallCount.Should().Be(appliedCalls + 1);
    }

    [Fact]
    public async Task A_device_param_default_applies_normally()
    {
        var instance = NewInstance(state: WorkloadInstanceState.PendingConfiguration);
        var harness = new Harness
        {
            ManifestParameters = new[]
            {
                new WorkloadParameter { Name = "devicePath", Type = WorkloadParameterType.DevicePath, Default = "/dev/ttyUSB0" },
            },
            ManifestPrivileges = new WorkloadPrivileges { Devices = new[] { new WorkloadDevice { Parameter = "devicePath" } } },
            ResolvedParameters = new Dictionary<string, string?>(StringComparer.Ordinal),
            UseRealResolver = true,
        }.WithDesiredHash("hash-live");
        var engine = harness.Build();

        var outcome = await engine.ReconcileInstanceAsync(instance);

        outcome.Action.Should().Be(WorkloadReconcileAction.Applied,
            "a default-mounted device path under /dev/** applies");
        harness.Provider.ApplyCountFor(instance.Id).Should().Be(1);
    }

    [Fact]
    public async Task An_observe_only_instance_still_reads_status_and_health_while_the_apply_is_gated()
    {
        var instance = NewInstance(state: WorkloadInstanceState.Healthy, observeOnly: true);
        instance.SetDesiredSpecHash("hash-old", DateTimeOffset.UtcNow);
        var harness = new Harness { HealthMode = WorkloadHealthMode.Http }.WithDesiredHash("hash-new");
        harness.PreApply(instance, "hash-old");
        harness.Provider.SetStatus(instance.Id, WorkloadProviderPhase.Running, providerReady: false, restartCount: 7);
        var engine = harness.Build();

        await engine.ReconcileInstanceAsync(instance);

        harness.Provider.ApplyCountFor(instance.Id).Should().Be(1, "observe-only never applies (only the pre-apply setup counts)");
        instance.RestartCount.Should().Be(7, "status keeps flowing while observe-only — only the APPLY is gated");
        instance.State.Should().Be(WorkloadInstanceState.Degraded, "a crash-looping ready→not-ready runtime degrades even while observe-only");
    }

    [Fact]
    public async Task A_globally_paused_instance_still_reads_status_while_the_apply_is_gated()
    {
        var instance = NewInstance(state: WorkloadInstanceState.Healthy);
        instance.SetDesiredSpecHash("hash-old", DateTimeOffset.UtcNow);
        var harness = new Harness { HealthMode = WorkloadHealthMode.Http, ReconciliationPaused = true }.WithDesiredHash("hash-new");
        harness.PreApply(instance, "hash-old");
        harness.Provider.SetStatus(instance.Id, WorkloadProviderPhase.Running, providerReady: false, restartCount: 3);
        var engine = harness.Build();

        await engine.ReconcileInstanceAsync(instance);

        harness.Provider.ApplyCountFor(instance.Id).Should().Be(1, "a paused reconciler never applies");
        instance.RestartCount.Should().Be(3, "status reads continue while reconciliation is paused — only mutations queue");
    }

    [Fact]
    public async Task An_operator_paused_instance_stays_paused_through_a_provider_blip()
    {
        var instance = NewInstance(state: WorkloadInstanceState.Suspended);
        instance.TransitionTo(WorkloadInstanceState.Suspended, WorkloadStateReason.OperatorPaused, "paused", DateTimeOffset.UtcNow);
        instance.SetDesiredSpecHash("hash-live", DateTimeOffset.UtcNow);
        var harness = new Harness { HealthMode = WorkloadHealthMode.Http }.WithDesiredHash("hash-live");
        harness.PreApply(instance, "hash-live");
        harness.ThrowingProvider.ThrowOnStatus = true;
        var engine = harness.Build();

        await engine.ReconcileInstanceAsync(instance);

        instance.State.Should().Be(WorkloadInstanceState.Suspended,
            "an operator-paused instance must not be flipped to Unknown by a provider blip — the pause must survive");
        instance.StateReason.Should().Be(WorkloadStateReason.OperatorPaused,
            "the pause reason must NOT be overwritten by ProviderUnreachable (else the hold evaporates on recovery)");
    }

    [Fact]
    public async Task A_non_config_compute_throw_marks_Failed_with_a_compute_failure_reason_not_HealthLost()
    {
        var instance = NewInstance(state: WorkloadInstanceState.Deploying);
        instance.SetDesiredSpecHash("hash-live", DateTimeOffset.UtcNow);
        var harness = new Harness
        {
            ResolverThrows = true,
            ResolverException = new InvalidOperationException("unexpected derivation fault"),
        }.WithDesiredHash("hash-live");
        harness.PreApply(instance, "hash-live");
        var engine = harness.Build();

        await engine.ReconcileInstanceAsync(instance);

        instance.State.Should().Be(WorkloadInstanceState.Failed);
        instance.StateReason.Should().Be(WorkloadStateReason.DesiredSpecComputeFailed,
            "a render-compute failure is NOT lost health — no health was ever observed");
    }

    [Fact]
    public async Task An_operator_paused_instance_is_suspended_on_the_provider_and_stays_suspended()
    {
        var instance = NewInstance(state: WorkloadInstanceState.Healthy);
        instance.TransitionTo(WorkloadInstanceState.Suspended, WorkloadStateReason.OperatorPaused, "paused", DateTimeOffset.UtcNow);
        instance.SetDesiredSpecHash("hash-live", DateTimeOffset.UtcNow);
        var harness = new Harness { HealthMode = WorkloadHealthMode.Http }.WithDesiredHash("hash-live");
        harness.PreApply(instance, "hash-live");
        harness.Provider.SimulateReady(instance.Id);
        var engine = harness.Build();

        await engine.ReconcileInstanceAsync(instance);

        harness.Provider.IsSuspended(instance.Id).Should().BeTrue(
            "an operator-paused instance must be scaled down on the provider, not left running while the badge says paused");
        instance.State.Should().Be(WorkloadInstanceState.Suspended, "the pause is held across the pass");
        instance.StateReason.Should().Be(WorkloadStateReason.OperatorPaused);
    }

    [Fact]
    public async Task An_already_suspended_operator_paused_instance_re_invokes_SuspendAsync_next_pass()
    {
        var instance = NewInstance(state: WorkloadInstanceState.Healthy);
        instance.TransitionTo(WorkloadInstanceState.Suspended, WorkloadStateReason.OperatorPaused, "paused", DateTimeOffset.UtcNow);
        instance.SetDesiredSpecHash("hash-live", DateTimeOffset.UtcNow);
        var harness = new Harness { HealthMode = WorkloadHealthMode.Http }.WithDesiredHash("hash-live");
        harness.PreApply(instance, "hash-live");
        harness.Provider.SimulateReady(instance.Id);
        harness.ThrowingProvider.ThrowSuspendCount = 1;
        var engine = harness.Build();

        var pass1 = await engine.ReconcileInstanceAsync(instance);
        pass1.Should().NotBeNull("a throwing SuspendAsync must never abort the reconcile pass");
        instance.State.Should().Be(WorkloadInstanceState.Suspended, "the pause is held even when the suspend attempt failed");
        harness.Provider.IsSuspended(instance.Id).Should().BeFalse("the first suspend attempt threw before marking suspended");

        await engine.ReconcileInstanceAsync(instance);
        harness.ThrowingProvider.SuspendCallCount.Should().BeGreaterOrEqualTo(2, "the failed suspend is retried on the next pass");
        harness.Provider.IsSuspended(instance.Id).Should().BeTrue("the retry actually scales the runtime down");
    }

    [Fact]
    public async Task An_operator_paused_instance_with_no_registered_provider_does_not_crash()
    {
        var instance = NewInstance(state: WorkloadInstanceState.Healthy);
        instance.TransitionTo(WorkloadInstanceState.Suspended, WorkloadStateReason.OperatorPaused, "paused", DateTimeOffset.UtcNow);
        instance.SetDesiredSpecHash("hash-live", DateTimeOffset.UtcNow);
        var harness = new Harness
        {
            HealthMode = WorkloadHealthMode.Http,
            RegisteredProvider = null,
        }.WithDesiredHash("hash-live");
        var engine = harness.Build();

        var act = async () => await engine.ReconcileInstanceAsync(instance);

        await act.Should().NotThrowAsync("a null provider must skip the suspend silently, never crash the loop");
        instance.State.Should().Be(WorkloadInstanceState.Suspended, "the pause is held regardless of provider availability");
    }

    [Fact]
    public async Task An_operator_resumed_instance_calls_ResumeAsync_before_re_applying()
    {
        var instance = NewInstance(state: WorkloadInstanceState.Healthy);
        instance.SetDesiredSpecHash("hash-live", DateTimeOffset.UtcNow);
        var harness = new Harness { HealthMode = WorkloadHealthMode.Http }.WithDesiredHash("hash-live");
        harness.PreApply(instance, "hash-live");
        await harness.Provider.SuspendAsync(WorkloadRef.FromInstance(instance));
        instance.TransitionTo(WorkloadInstanceState.Suspended, WorkloadStateReason.OperatorPaused, "paused", DateTimeOffset.UtcNow);
        instance.TransitionTo(WorkloadInstanceState.Deploying, WorkloadStateReason.OperatorResumed, "resumed", DateTimeOffset.UtcNow);
        harness.Provider.SimulateReady(instance.Id);
        var engine = harness.Build();

        await engine.ReconcileInstanceAsync(instance);

        harness.ThrowingProvider.ResumeCallCount.Should().BeGreaterThan(0,
            "leaving Suspended (operator resume) must ResumeAsync so the re-apply lands on an un-suspended object");
        harness.Provider.IsSuspended(instance.Id).Should().BeFalse("the runtime is resumed");
        instance.State.Should().Be(WorkloadInstanceState.Healthy, "a resumed, provider-ready http workload reaches Healthy");
    }

    [Fact]
    public async Task A_host_held_row_is_never_applied_however_many_passes_run_over_it()
    {
        var instance = NewInstance(state: WorkloadInstanceState.Healthy);
        instance.SetDesiredSpecHash("hash-live", DateTimeOffset.UtcNow);
        var harness = new Harness { HealthMode = WorkloadHealthMode.Http }.WithDesiredHash("hash-next");
        harness.PreApply(instance, "hash-live");
        instance.TransitionTo(WorkloadInstanceState.Suspended, WorkloadStateReason.SuspendedByHost, "withdrawn", DateTimeOffset.UtcNow);
        harness.Provider.SimulateReady(instance.Id);
        var engine = harness.Build();

        var first = await engine.ReconcileInstanceAsync(instance);
        var second = await engine.ReconcileInstanceAsync(instance);

        first.Reason.Should().Be(WorkloadReconcileReason.SuspendedByHost);
        second.Reason.Should().Be(WorkloadReconcileReason.SuspendedByHost);
        instance.State.Should().Be(WorkloadInstanceState.Suspended);
        instance.StateReason.Should().Be(WorkloadStateReason.SuspendedByHost);
        harness.Provider.IsSuspended(instance.Id).Should().BeTrue("a withdrawn workload is scaled down, not left running");
        harness.Provider.ApplyCountFor(instance.Id).Should().Be(1, "the drift is recorded as pending, never applied");
    }

    [Fact]
    public async Task A_token_minted_by_an_apply_the_hold_landed_during_is_revoked_by_the_next_pass()
    {
        var applying = NewInstance(state: WorkloadInstanceState.Healthy);
        applying.SetDesiredSpecHash("hash-live", DateTimeOffset.UtcNow);
        var harness = new Harness { HealthMode = WorkloadHealthMode.Http }.WithDesiredHash("hash-next");
        harness.PreApply(applying, "hash-live");
        var tokens = harness.CredentialPort;
        var stored = WorkloadInstance.Create(applying.Id, applying.GroupInstanceId, GroupId, WorkloadId, DateTimeOffset.UtcNow);
        stored.SetProvider(KubernetesProvider.Id, DateTimeOffset.UtcNow);
        stored.SetAppliedSpecHash("hash-live", DateTimeOffset.UtcNow);
        string? minted = null;
        harness.ThrowingProvider.DuringApply = async spec =>
        {
            harness.ThrowingProvider.DuringApply = null;
            stored.TransitionTo(WorkloadInstanceState.Suspended, WorkloadStateReason.SuspendedByHost, "withdrawn", DateTimeOffset.UtcNow);
            await tokens.RevokeAllAsync(stored.Id);
            minted = await tokens.ResolveOrMintAsync(spec.InstanceId, "gateway-token", existingMaterial: null);
        };
        var engine = harness.Build();
        await engine.ReconcileInstanceAsync(applying);
        tokens.LiveTokens(stored.Id).Should().Equal([minted!], "the pass that minted it saw the row before the hold");

        await engine.ReconcileInstanceAsync(stored);

        tokens.Calls.Should().Equal("revoke", $"mint:{minted}", "revoke");
        tokens.LiveTokens(stored.Id).Should().BeEmpty("a held workload keeps no live credential");
    }

    [Fact]
    public async Task A_revoke_that_failed_is_retried_by_the_next_pass_over_the_held_row()
    {
        var instance = NewInstance(state: WorkloadInstanceState.Healthy);
        instance.SetDesiredSpecHash("hash-live", DateTimeOffset.UtcNow);
        var harness = new Harness { HealthMode = WorkloadHealthMode.Http }.WithDesiredHash("hash-live");
        harness.PreApply(instance, "hash-live");
        var tokens = harness.CredentialPort;
        await tokens.ResolveOrMintAsync(instance.Id, "gateway-token", existingMaterial: null);
        instance.TransitionTo(WorkloadInstanceState.Suspended, WorkloadStateReason.SuspendedByHost, "withdrawn", DateTimeOffset.UtcNow);
        tokens.RevokeFails = true;
        var engine = harness.Build();

        await engine.ReconcileInstanceAsync(instance);

        instance.StateReason.Should().Be(WorkloadStateReason.SuspendedByHost);
        harness.Provider.IsSuspended(instance.Id).Should().BeTrue("a failed revoke does not stop the scale-down");
        tokens.LiveTokens(instance.Id).Should().ContainSingle();

        tokens.RevokeFails = false;
        await engine.ReconcileInstanceAsync(instance);

        tokens.LiveTokens(instance.Id).Should().BeEmpty("the next pass revokes again");
    }

    [Fact]
    public async Task A_held_disowned_row_is_revoked_every_pass_but_its_runtime_is_never_scaled_down_or_re_adopted()
    {
        var instance = NewInstance(state: WorkloadInstanceState.Healthy);
        var harness = new Harness { HealthMode = WorkloadHealthMode.Http }.WithDesiredHash("hash-desired");
        harness.PreApply(instance, "hash-applied");
        var now = DateTimeOffset.UtcNow;
        instance.TransitionTo(WorkloadInstanceState.Switching, WorkloadStateReason.HealthObserved, null, now);
        instance.TransitionTo(WorkloadInstanceState.ExternallyManaged, WorkloadStateReason.ExternalHealthObserved, null, now);
        var tokens = harness.CredentialPort;
        await tokens.ResolveOrMintAsync(instance.Id, "gateway-token", existingMaterial: null);
        instance.TransitionTo(WorkloadInstanceState.Suspended, WorkloadStateReason.SuspendedByHostExternallyManaged, "withdrawn", now);
        harness.Provider.SimulateReady(instance.Id);
        var engine = harness.Build();

        await engine.ReconcileInstanceAsync(instance);
        await engine.ReconcileInstanceAsync(instance);

        tokens.LiveTokens(instance.Id).Should().BeEmpty("a withdrawn workload keeps no live credential");
        harness.Provider.IsSuspended(instance.Id).Should().BeFalse("the runtime is the operator's, not the library's to scale down");
        harness.Provider.ApplyCountFor(instance.Id).Should().Be(1, "only the pre-apply setup counts");
        instance.State.Should().Be(WorkloadInstanceState.Suspended);
        instance.StateReason.Should().Be(WorkloadStateReason.SuspendedByHostExternallyManaged);
    }

    [Fact]
    public async Task A_held_manual_workload_that_still_reports_live_stays_held()
    {
        var instance = NewInstance(state: WorkloadInstanceState.Healthy);
        var harness = new Harness
        {
            RegisteredProvider = ManualProvider.Id,
            EffectiveProvider = ManualProvider.Descriptor,
        }.WithDesiredHash("hash-manual");
        var now = DateTimeOffset.UtcNow;
        await harness.Provider.ApplyAsync(SpecFor(instance.Id, "hash-manual", harness.HealthMode), progress: null);
        instance.SetProvider(ManualProvider.Id, now);
        instance.SetAppliedSpecHash("hash-manual", now);
        instance.TransitionTo(WorkloadInstanceState.ExternallyManaged, WorkloadStateReason.ExternalHealthObserved, null, now);
        instance.TransitionTo(WorkloadInstanceState.Suspended, WorkloadStateReason.SuspendedByHostExternallyManaged, "withdrawn", now);
        harness.Provider.SetStatus(instance.Id, WorkloadProviderPhase.Ready, providerReady: true);
        var engine = harness.Build();

        await engine.ReconcileInstanceAsync(instance);

        instance.State.Should().Be(WorkloadInstanceState.Suspended, "a live external signal is not the host's resume");
        instance.StateReason.Should().Be(WorkloadStateReason.SuspendedByHostExternallyManaged);
        harness.CredentialPort.Calls.Should().Contain("revoke");
    }

    [Fact]
    public async Task A_mid_switch_row_the_host_withdrew_is_revoked_every_pass()
    {
        var instance = NewInstance(state: WorkloadInstanceState.Healthy);
        var harness = new Harness { HealthMode = WorkloadHealthMode.Http }.WithDesiredHash("hash-live");
        harness.PreApply(instance, "hash-live");
        var now = DateTimeOffset.UtcNow;
        instance.TransitionTo(WorkloadInstanceState.Switching, WorkloadStateReason.HealthObserved, null, now);
        instance.TransitionTo(WorkloadInstanceState.Switching, WorkloadStateReason.SuspendedByHost, "withdrawn", now);
        var tokens = harness.CredentialPort;
        await tokens.ResolveOrMintAsync(instance.Id, "gateway-token", existingMaterial: null);
        var engine = harness.Build();

        await engine.ReconcileInstanceAsync(instance);

        tokens.LiveTokens(instance.Id).Should().BeEmpty("a takeover render may mint after the suspend's revoke");
        instance.State.Should().Be(WorkloadInstanceState.Switching);
    }

    [Fact]
    public async Task A_held_rows_token_is_revoked_even_on_a_pass_that_cannot_read_its_inputs()
    {
        var instance = NewInstance(state: WorkloadInstanceState.Healthy);
        var harness = new Harness { HostRefusal = "the gateway URL is unset" };
        await harness.CredentialPort.ResolveOrMintAsync(instance.Id, "gateway-token", existingMaterial: null);
        instance.TransitionTo(WorkloadInstanceState.Suspended, WorkloadStateReason.SuspendedByHost, "withdrawn", DateTimeOffset.UtcNow);

        await harness.Build().ReconcileInstanceAsync(instance);

        harness.CredentialPort.LiveTokens(instance.Id).Should().BeEmpty();
    }

    [Fact]
    public async Task An_operator_pause_keeps_its_credential_across_passes()
    {
        var instance = NewInstance(state: WorkloadInstanceState.Healthy);
        instance.SetDesiredSpecHash("hash-live", DateTimeOffset.UtcNow);
        var harness = new Harness { HealthMode = WorkloadHealthMode.Http }.WithDesiredHash("hash-live");
        harness.PreApply(instance, "hash-live");
        await harness.CredentialPort.ResolveOrMintAsync(instance.Id, "gateway-token", existingMaterial: null);
        instance.TransitionTo(WorkloadInstanceState.Suspended, WorkloadStateReason.OperatorPaused, "paused", DateTimeOffset.UtcNow);

        await harness.Build().ReconcileInstanceAsync(instance);

        harness.CredentialPort.LiveTokens(instance.Id).Should().ContainSingle();
    }

    [Fact]
    public async Task A_host_suspended_row_is_scaled_down_and_its_drift_waits()
    {
        var instance = NewInstance(state: WorkloadInstanceState.Healthy);
        instance.SetDesiredSpecHash("hash-live", DateTimeOffset.UtcNow);
        var harness = new Harness { HealthMode = WorkloadHealthMode.Http }.WithDesiredHash("hash-next");
        harness.PreApply(instance, "hash-live");
        instance.TransitionTo(WorkloadInstanceState.Suspended, WorkloadStateReason.SuspendedByHost, "withdrawn", DateTimeOffset.UtcNow);
        harness.Provider.SimulateReady(instance.Id);
        var engine = harness.Build();

        var outcome = await engine.ReconcileInstanceAsync(instance);

        outcome.Action.Should().Be(WorkloadReconcileAction.DeferredMutation);
        outcome.Reason.Should().Be(WorkloadReconcileReason.SuspendedByHost);
        outcome.PendingApply.Should().BeTrue();
        harness.Provider.IsSuspended(instance.Id).Should().BeTrue();
        harness.Provider.ApplyCountFor(instance.Id).Should().Be(1);
        instance.DesiredSpecHash.Should().Be("hash-next", "the drift is kept for the resume");
        instance.StateReason.Should().Be(WorkloadStateReason.SuspendedByHost, "a ready runtime does not heal the host's hold");
    }

    [Fact]
    public async Task A_host_suspend_survives_a_provider_blip()
    {
        var instance = NewInstance(state: WorkloadInstanceState.Healthy);
        instance.SetDesiredSpecHash("hash-live", DateTimeOffset.UtcNow);
        var harness = new Harness { HealthMode = WorkloadHealthMode.Http }.WithDesiredHash("hash-live");
        harness.PreApply(instance, "hash-live");
        instance.TransitionTo(WorkloadInstanceState.Suspended, WorkloadStateReason.SuspendedByHost, "withdrawn", DateTimeOffset.UtcNow);
        harness.ThrowingProvider.ThrowOnStatus = true;
        var engine = harness.Build();

        var outcome = await engine.ReconcileInstanceAsync(instance);

        outcome.Reason.Should().Be(WorkloadReconcileReason.SuspendedByHost);
        instance.State.Should().Be(WorkloadInstanceState.Suspended);
        instance.StateReason.Should().Be(WorkloadStateReason.SuspendedByHost, "an Unknown would drop the hold on recovery");
    }

    [Fact]
    public async Task A_host_resumed_row_lifts_the_suspend_before_re_applying()
    {
        var instance = NewInstance(state: WorkloadInstanceState.Healthy);
        instance.SetDesiredSpecHash("hash-live", DateTimeOffset.UtcNow);
        var harness = new Harness { HealthMode = WorkloadHealthMode.Http }.WithDesiredHash("hash-live");
        harness.PreApply(instance, "hash-live");
        await harness.Provider.SuspendAsync(WorkloadRef.FromInstance(instance));
        instance.TransitionTo(WorkloadInstanceState.Suspended, WorkloadStateReason.SuspendedByHost, "withdrawn", DateTimeOffset.UtcNow);
        instance.TransitionTo(WorkloadInstanceState.Deploying, WorkloadStateReason.ResumedByHost, "resumed", DateTimeOffset.UtcNow);
        instance.SetAppliedSpecHash(null, DateTimeOffset.UtcNow);
        harness.Provider.SimulateReady(instance.Id);
        var engine = harness.Build();

        await engine.ReconcileInstanceAsync(instance);

        harness.ThrowingProvider.ResumeCallCount.Should().BeGreaterThan(0, "the re-apply must land on a runtime that is not held down");
        harness.Provider.IsSuspended(instance.Id).Should().BeFalse();
        harness.Provider.ApplyCountFor(instance.Id).Should().Be(2, "the resume re-applies, which mints the runtime a fresh credential");
    }

    [Fact]
    public async Task A_Degraded_instance_with_hostRequirements_enriches_the_StateMessage()
    {
        var instance = NewInstance(state: WorkloadInstanceState.Healthy);
        instance.SetDesiredSpecHash("hash-live", DateTimeOffset.UtcNow);
        var harness = new Harness
        {
            HealthMode = WorkloadHealthMode.Http,
            HostRequirements = new[]
            {
                new HostRequirement { Kind = HostRequirementKind.DbusService, Name = "bluetoothd", Description = "BlueZ must run on the node" },
                new HostRequirement { Kind = HostRequirementKind.KernelModule, Name = "tun" },
            },
        }.WithDesiredHash("hash-live");
        harness.PreApply(instance, "hash-live");
        harness.Provider.SetStatus(instance.Id, WorkloadProviderPhase.Running, providerReady: false);
        var engine = harness.Build();

        await engine.ReconcileInstanceAsync(instance);

        instance.State.Should().Be(WorkloadInstanceState.Degraded, "provider-ready lost while running → Degraded");
        instance.StateMessage.Should().Contain("host prerequisites",
            "the Degraded message names the declared host prerequisites so it is diagnosable");
        instance.StateMessage.Should().Contain("bluetoothd", "the specific declared prerequisite is named");
        instance.StateMessage.Should().Contain("tun");
    }

    [Fact]
    public async Task A_Degraded_instance_without_hostRequirements_keeps_the_plain_message()
    {
        var instance = NewInstance(state: WorkloadInstanceState.Healthy);
        instance.SetDesiredSpecHash("hash-live", DateTimeOffset.UtcNow);
        var harness = new Harness { HealthMode = WorkloadHealthMode.Http }.WithDesiredHash("hash-live");
        harness.PreApply(instance, "hash-live");
        harness.Provider.SetStatus(instance.Id, WorkloadProviderPhase.Running, providerReady: false);
        var engine = harness.Build();

        await engine.ReconcileInstanceAsync(instance);

        instance.State.Should().Be(WorkloadInstanceState.Degraded);
        instance.StateMessage.Should().Be("Health signal lost while running.",
            "with no hostRequirements the base Degraded message is unchanged");
    }

    private static readonly DateTimeOffset StallT0 = new(2026, 7, 6, 12, 0, 0, TimeSpan.Zero);

    private const string UnboundClaimMessage =
        "0/1 ready. Pod not ready: Unschedulable: 0/1 nodes are available: pod has unbound immediate PersistentVolumeClaims.";

    private static (WorkloadInstance Instance, Harness Harness, StepClock Clock) DeployingSince(
        WorkloadReconcilerOptions? options = null, WorkloadProviderId? provider = null, WorkloadProviderDescriptor? descriptor = null,
        WorkloadHealthMode? healthMode = null)
    {
        var clock = new StepClock(StallT0);
        var harness = new Harness
        {
            HealthMode = healthMode ?? WorkloadHealthMode.Http,
            Clock = clock,
            ReconcilerOptions = options ?? new WorkloadReconcilerOptions(),
            RegisteredProvider = provider ?? KubernetesProvider.Id,
            EffectiveProvider = descriptor ?? KubernetesProvider.Descriptor,
        }.WithDesiredHash("hash-stall");
        var instance = WorkloadInstance.Create(Guid.NewGuid(), Guid.NewGuid(), GroupId, WorkloadId, StallT0);
        harness.Provider.ApplyAsync(SpecFor(instance.Id, "hash-stall", harness.HealthMode), progress: null).GetAwaiter().GetResult();
        instance.SetProvider(provider ?? KubernetesProvider.Id, StallT0);
        instance.SetDesiredSpecHash("hash-stall", StallT0);
        instance.SetAppliedSpecHash("hash-stall", StallT0);
        instance.TransitionTo(WorkloadInstanceState.Deploying, WorkloadStateReason.Provisioning, "Applying desired spec.", StallT0);
        return (instance, harness, clock);
    }

    [Fact]
    public async Task A_deploy_not_ready_past_the_stall_threshold_degrades_with_the_provider_reason()
    {
        var (instance, harness, clock) = DeployingSince();
        harness.Provider.SetStatus(instance.Id, WorkloadProviderPhase.Running, providerReady: false, message: UnboundClaimMessage);
        clock.Set(StallT0 + new WorkloadReconcilerOptions().DeployStalledAfter + TimeSpan.FromSeconds(1));

        await harness.Build().ReconcileInstanceAsync(instance);

        instance.State.Should().Be(WorkloadInstanceState.Degraded, "a deploy that never became ready must not read as in progress forever");
        instance.StateReason.Should().Be(WorkloadStateReason.Provisioning, "Degraded+Provisioning is the stalled-deploy pair");
        instance.StateMessage.Should().Contain("unbound immediate PersistentVolumeClaims", "the provider's reason reaches the operator");
    }

    [Fact]
    public async Task A_deploy_not_ready_within_the_stall_threshold_stays_Deploying()
    {
        var (instance, harness, clock) = DeployingSince();
        harness.Provider.SetStatus(instance.Id, WorkloadProviderPhase.Running, providerReady: false, message: UnboundClaimMessage);
        clock.Set(StallT0 + new WorkloadReconcilerOptions().DeployStalledAfter - TimeSpan.FromSeconds(1));

        await harness.Build().ReconcileInstanceAsync(instance);

        instance.State.Should().Be(WorkloadInstanceState.Deploying);
    }

    [Fact]
    public async Task The_stall_threshold_is_read_from_the_reconciler_options()
    {
        var (instance, harness, clock) = DeployingSince(new WorkloadReconcilerOptions { DeployStalledAfterSeconds = 120 });
        harness.Provider.SetStatus(instance.Id, WorkloadProviderPhase.Pending, providerReady: false, message: "Container created but not started yet.");
        clock.Set(StallT0.AddMinutes(3));

        await harness.Build().ReconcileInstanceAsync(instance);

        instance.State.Should().Be(WorkloadInstanceState.Degraded);
        instance.StateMessage.Should().Contain("2 min").And.Contain("Container created but not started yet.");
    }

    [Fact]
    public async Task A_stalled_deploy_stays_Degraded_on_the_next_pass_and_refreshes_the_reason()
    {
        var (instance, harness, clock) = DeployingSince();
        harness.Provider.SetStatus(instance.Id, WorkloadProviderPhase.Running, providerReady: false, message: UnboundClaimMessage);
        clock.Set(StallT0.AddMinutes(30));
        var engine = harness.Build();
        await engine.ReconcileInstanceAsync(instance);
        var degradedAt = instance.LastTransitionAtUtc;

        harness.Provider.SetStatus(instance.Id, WorkloadProviderPhase.Running, providerReady: false, message: "0/1 ready. Pod not ready: ContainerCreating");
        clock.Set(StallT0.AddMinutes(31));
        await engine.ReconcileInstanceAsync(instance);

        instance.State.Should().Be(WorkloadInstanceState.Degraded, "a pass with no ready evidence must not reset a stalled deploy to Deploying");
        instance.StateReason.Should().Be(WorkloadStateReason.Provisioning);
        instance.StateMessage.Should().Contain("ContainerCreating");
        instance.LastTransitionAtUtc.Should().Be(degradedAt, "the stuck clock keeps counting from the stall");
    }

    [Fact]
    public async Task A_stalled_deploy_recovers_to_Healthy_once_ready()
    {
        var (instance, harness, clock) = DeployingSince();
        harness.Provider.SetStatus(instance.Id, WorkloadProviderPhase.Running, providerReady: false, message: UnboundClaimMessage);
        clock.Set(StallT0.AddMinutes(30));
        var engine = harness.Build();
        await engine.ReconcileInstanceAsync(instance);

        harness.Provider.SetStatus(instance.Id, WorkloadProviderPhase.Ready, providerReady: true);
        await engine.ReconcileInstanceAsync(instance);

        instance.State.Should().Be(WorkloadInstanceState.Healthy);
    }

    [Fact]
    public async Task A_lost_health_Degraded_is_not_reset_to_Deploying_while_still_not_ready()
    {
        var (instance, harness, clock) = DeployingSince();
        instance.TransitionTo(WorkloadInstanceState.Healthy, WorkloadStateReason.HealthObserved, null, StallT0);
        harness.Provider.SetStatus(instance.Id, WorkloadProviderPhase.Running, providerReady: false);
        clock.Set(StallT0.AddMinutes(1));
        var engine = harness.Build();
        await engine.ReconcileInstanceAsync(instance);
        instance.State.Should().Be(WorkloadInstanceState.Degraded);

        clock.Set(StallT0.AddMinutes(2));
        await engine.ReconcileInstanceAsync(instance);

        instance.State.Should().Be(WorkloadInstanceState.Degraded, "a workload that lost readiness and has not regained it is not deploying");
        instance.StateReason.Should().Be(WorkloadStateReason.HealthLost);
    }

    [Fact]
    public async Task A_manual_provider_deploy_waiting_for_the_operator_never_stalls()
    {
        var (instance, harness, clock) = DeployingSince(provider: ManualProvider.Id, descriptor: ManualProvider.Descriptor);
        harness.Provider.SetStatus(instance.Id, WorkloadProviderPhase.Pending, providerReady: false);
        clock.Set(StallT0.AddHours(5));

        await harness.Build().ReconcileInstanceAsync(instance);

        instance.State.Should().Be(WorkloadInstanceState.Deploying, "Reeve deploys nothing on the manual provider; the operator's pace is not a stall");
    }

    [Fact]
    public async Task A_workload_that_lost_health_carries_the_provider_reason()
    {
        var (instance, harness, clock) = DeployingSince();
        instance.TransitionTo(WorkloadInstanceState.Healthy, WorkloadStateReason.HealthObserved, null, StallT0.AddMinutes(1));
        harness.Provider.SetStatus(instance.Id, WorkloadProviderPhase.Running, providerReady: false, message: UnboundClaimMessage);
        var engine = harness.Build();

        clock.Set(StallT0.AddMinutes(2));
        await engine.ReconcileInstanceAsync(instance);
        clock.Set(StallT0.AddMinutes(30));
        await engine.ReconcileInstanceAsync(instance);

        instance.State.Should().Be(WorkloadInstanceState.Degraded);
        instance.StateReason.Should().Be(WorkloadStateReason.HealthLost, "it was healthy since its deploy started: not a stall");
        instance.StateMessage.Should().Be("Health signal lost while running. " + UnboundClaimMessage);
    }

    [Fact]
    public async Task A_provider_blip_does_not_restart_the_stall_clock()
    {
        var (instance, harness, clock) = DeployingSince();
        var engine = harness.Build();
        harness.Provider.SetStatus(instance.Id, WorkloadProviderPhase.Unknown, providerReady: false);
        clock.Set(StallT0.AddMinutes(5));
        await engine.ReconcileInstanceAsync(instance);
        instance.State.Should().Be(WorkloadInstanceState.Unknown);

        harness.Provider.SetStatus(instance.Id, WorkloadProviderPhase.Running, providerReady: false, message: UnboundClaimMessage);
        clock.Set(StallT0.AddMinutes(6));
        await engine.ReconcileInstanceAsync(instance);
        instance.State.Should().Be(WorkloadInstanceState.Deploying);

        clock.Set(StallT0.AddMinutes(11));
        await engine.ReconcileInstanceAsync(instance);

        instance.State.Should().Be(WorkloadInstanceState.Degraded, "the deploy started at T0; the blip at minute 5 started nothing");
        instance.StateReason.Should().Be(WorkloadStateReason.Provisioning);
        instance.DeployStartedAtUtc.Should().Be(StallT0);
    }

    [Fact]
    public async Task A_stalled_deploy_stays_stalled_across_a_provider_blip()
    {
        var (instance, harness, clock) = DeployingSince();
        var engine = harness.Build();
        harness.Provider.SetStatus(instance.Id, WorkloadProviderPhase.Running, providerReady: false, message: UnboundClaimMessage);
        clock.Set(StallT0.AddMinutes(30));
        await engine.ReconcileInstanceAsync(instance);

        harness.Provider.SetStatus(instance.Id, WorkloadProviderPhase.Unknown, providerReady: false);
        clock.Set(StallT0.AddMinutes(31));
        await engine.ReconcileInstanceAsync(instance);
        harness.Provider.SetStatus(instance.Id, WorkloadProviderPhase.Running, providerReady: false, message: UnboundClaimMessage);
        clock.Set(StallT0.AddMinutes(32));
        await engine.ReconcileInstanceAsync(instance);

        instance.State.Should().Be(WorkloadInstanceState.Degraded, "a blip must not turn a stalled deploy back into an in-progress one");
        instance.StateReason.Should().Be(WorkloadStateReason.Provisioning);
    }

    [Fact]
    public async Task A_Failed_read_does_not_restart_the_stall_clock()
    {
        var (instance, harness, clock) = DeployingSince();
        var engine = harness.Build();
        harness.Provider.SetStatus(instance.Id, WorkloadProviderPhase.Failed, providerReady: false, message: "Pod failing.");
        clock.Set(StallT0.AddMinutes(5));
        await engine.ReconcileInstanceAsync(instance);
        instance.State.Should().Be(WorkloadInstanceState.Failed);

        harness.Provider.SetStatus(instance.Id, WorkloadProviderPhase.Running, providerReady: false, message: UnboundClaimMessage);
        clock.Set(StallT0.AddMinutes(6));
        await engine.ReconcileInstanceAsync(instance);
        instance.State.Should().Be(WorkloadInstanceState.Deploying);

        clock.Set(StallT0.AddMinutes(11));
        await engine.ReconcileInstanceAsync(instance);

        instance.State.Should().Be(WorkloadInstanceState.Degraded, "a pod alternating between failing and not ready is not a new deploy");
        instance.StateReason.Should().Be(WorkloadStateReason.Provisioning);
    }

    [Fact]
    public async Task A_new_spec_applied_while_Deploying_restarts_the_stall_clock()
    {
        var (instance, harness, clock) = DeployingSince();
        harness.Provider.SetStatus(instance.Id, WorkloadProviderPhase.Running, providerReady: false, message: UnboundClaimMessage);
        clock.Set(StallT0.AddMinutes(9));
        harness.WithDesiredHash("hash-fixed");
        var engine = harness.Build();
        await engine.ReconcileInstanceAsync(instance);
        instance.DeployStartedAtUtc.Should().Be(StallT0.AddMinutes(9), "the operator's fix at minute 9 starts a new deploy");

        clock.Set(StallT0.AddMinutes(11));
        await engine.ReconcileInstanceAsync(instance);
        instance.State.Should().Be(WorkloadInstanceState.Deploying, "a fix one minute old has not stalled");

        clock.Set(StallT0.AddMinutes(20));
        await engine.ReconcileInstanceAsync(instance);
        instance.State.Should().Be(WorkloadInstanceState.Degraded);
        instance.StateReason.Should().Be(WorkloadStateReason.Provisioning);
    }

    [Fact]
    public async Task A_new_spec_whose_first_apply_fails_over_a_cleared_applied_hash_still_starts_the_clock()
    {
        var (instance, harness, clock) = DeployingSince();
        harness.Provider.SetStatus(instance.Id, WorkloadProviderPhase.Running, providerReady: false, message: UnboundClaimMessage);
        instance.SetAppliedSpecHash(null, StallT0.AddMinutes(5));
        harness.WithDesiredHash("hash-new");
        harness.ThrowingProvider.FailApplyCount = 1;
        var engine = harness.Build();

        clock.Set(StallT0.AddMinutes(8));
        await engine.ReconcileInstanceAsync(instance);
        instance.AppliedSpecHash.Should().BeNull("the first apply of the new spec failed");
        clock.Set(StallT0.AddMinutes(9));
        await engine.ReconcileInstanceAsync(instance);
        instance.AppliedSpecHash.Should().Be("hash-new");

        instance.DeployStartedAtUtc.Should().Be(StallT0.AddMinutes(8), "the new spec's deploy began with its first attempt");
        clock.Set(StallT0.AddMinutes(12));
        await engine.ReconcileInstanceAsync(instance);
        instance.State.Should().Be(WorkloadInstanceState.Deploying, "a deploy four minutes old has not stalled");
    }

    [Fact]
    public async Task A_second_failed_attempt_of_the_new_spec_does_not_restart_the_clock()
    {
        var (instance, harness, clock) = DeployingSince();
        harness.Provider.SetStatus(instance.Id, WorkloadProviderPhase.Running, providerReady: false, message: UnboundClaimMessage);
        instance.SetAppliedSpecHash(null, StallT0.AddMinutes(5));
        harness.WithDesiredHash("hash-new");
        harness.ThrowingProvider.FailApplyCount = 2;
        var engine = harness.Build();

        clock.Set(StallT0.AddMinutes(8));
        await engine.ReconcileInstanceAsync(instance);
        clock.Set(StallT0.AddMinutes(9));
        await engine.ReconcileInstanceAsync(instance);

        instance.AppliedSpecHash.Should().BeNull("both attempts failed");
        instance.DeployStartedAtUtc.Should().Be(StallT0.AddMinutes(8), "a retry loop runs the stall timer from the first attempt");
    }

    [Fact]
    public async Task A_new_spec_whose_first_apply_fails_on_a_stalled_row_still_starts_the_clock()
    {
        var (instance, harness, clock) = DeployingSince();
        harness.Provider.SetStatus(instance.Id, WorkloadProviderPhase.Running, providerReady: false, message: UnboundClaimMessage);
        var engine = harness.Build();
        clock.Set(StallT0.AddMinutes(30));
        await engine.ReconcileInstanceAsync(instance);
        instance.State.Should().Be(WorkloadInstanceState.Degraded);

        instance.SetAppliedSpecHash(null, StallT0.AddMinutes(31));
        harness.WithDesiredHash("hash-new");
        harness.ThrowingProvider.FailApplyCount = 1;
        engine = harness.Build();
        clock.Set(StallT0.AddMinutes(32));
        await engine.ReconcileInstanceAsync(instance);
        clock.Set(StallT0.AddMinutes(33));
        await engine.ReconcileInstanceAsync(instance);

        instance.AppliedSpecHash.Should().Be("hash-new");
        instance.DeployStartedAtUtc.Should().Be(StallT0.AddMinutes(32));
        instance.State.Should().Be(WorkloadInstanceState.Deploying, "the new spec's deploy is one minute old");
    }

    [Fact]
    public async Task A_redeploy_of_a_stalled_deploy_restarts_the_stall_clock()
    {
        var (instance, harness, clock) = DeployingSince();
        harness.Provider.SetStatus(instance.Id, WorkloadProviderPhase.Running, providerReady: false, message: UnboundClaimMessage);
        var engine = harness.Build();
        clock.Set(StallT0.AddMinutes(30));
        await engine.ReconcileInstanceAsync(instance);
        instance.State.Should().Be(WorkloadInstanceState.Degraded);

        instance.SetAppliedSpecHash(null, StallT0.AddMinutes(31));
        instance.TransitionTo(WorkloadInstanceState.Deploying, WorkloadStateReason.Redeploying, "Redeploying by operator request.", StallT0.AddMinutes(31));
        clock.Set(StallT0.AddMinutes(32));
        await engine.ReconcileInstanceAsync(instance);

        instance.State.Should().Be(WorkloadInstanceState.Deploying);
        instance.AppliedSpecHash.Should().Be("hash-stall", "the redeploy re-applied");
        instance.DeployStartedAtUtc.Should().Be(StallT0.AddMinutes(31));

        clock.Set(StallT0.AddMinutes(42));
        await engine.ReconcileInstanceAsync(instance);
        instance.State.Should().Be(WorkloadInstanceState.Degraded);
    }

    [Fact]
    public async Task A_volume_recreated_on_every_pass_stalls_and_stays_stalled()
    {
        var (instance, harness, clock) = DeployingSince();
        harness.Provider.SetStatus(instance.Id, WorkloadProviderPhase.Running, providerReady: false, message: UnboundClaimMessage);
        var engine = harness.Build();
        var states = new List<(WorkloadInstanceState State, WorkloadStateReason Reason)>();

        for (var minute = 2; minute <= 30; minute += 2)
        {
            instance.SetAppliedSpecHash(null, StallT0.AddMinutes(minute - 1));
            clock.Set(StallT0.AddMinutes(minute));
            await engine.ReconcileInstanceAsync(instance);
            states.Add((instance.State, instance.StateReason));
        }

        harness.Provider.ApplyCountFor(instance.Id).Should().BeGreaterThan(10, "every pass re-applied");
        states.Take(4).Should().AllSatisfy(s => s.State.Should().Be(WorkloadInstanceState.Deploying));
        states.Skip(5).Should().AllSatisfy(s => s.Should().Be((WorkloadInstanceState.Degraded, WorkloadStateReason.Provisioning)),
            "a re-apply of the held spec neither moves a stalled deploy back to Deploying nor restarts its clock");
        instance.DeployStartedAtUtc.Should().Be(StallT0);
        instance.StateMessage.Should().Contain("unbound immediate PersistentVolumeClaims");
    }

    [Fact]
    public async Task A_host_mode_deploy_whose_runtime_is_up_but_never_reports_stalls_with_that_detail()
    {
        var (instance, harness, clock) = DeployingSince(healthMode: RecordingRolloutCertifier.SessionMode);
        harness.Provider.SetStatus(instance.Id, WorkloadProviderPhase.Ready, providerReady: true);
        clock.Set(StallT0.AddMinutes(11));

        await harness.Build().ReconcileInstanceAsync(instance);

        instance.State.Should().Be(WorkloadInstanceState.Degraded);
        instance.StateReason.Should().Be(WorkloadStateReason.Provisioning);
        instance.StateMessage.Should().Be("Deploy stalled for over 10 min: the runtime is up, but no qualifying health signal arrived.");
    }

    [Fact]
    public async Task An_unknown_presence_holds_a_stalled_deploy_as_it_is()
    {
        var (instance, harness, clock) = DeployingSince(healthMode: RecordingRolloutCertifier.SessionMode);
        harness.Provider.SetStatus(instance.Id, WorkloadProviderPhase.Ready, providerReady: true);
        var engine = harness.Build();
        clock.Set(StallT0.AddMinutes(11));
        await engine.ReconcileInstanceAsync(instance);
        var message = instance.StateMessage;

        harness.Presence.FaultReads = true;
        clock.Set(StallT0.AddMinutes(12));
        await engine.ReconcileInstanceAsync(instance);

        instance.State.Should().Be(WorkloadInstanceState.Degraded);
        instance.StateReason.Should().Be(WorkloadStateReason.Provisioning, "a presence-store fault is no news about the workload");
        instance.StateMessage.Should().Be(message);
    }

    private sealed class StepClock(DateTimeOffset now) : IWorkloadClock
    {
        public DateTimeOffset UtcNow { get; private set; } = now;

        public void Set(DateTimeOffset value) => UtcNow = value;
    }

    private static Harness NewScanHarness() => new Harness
    {
        ManifestKind = WorkloadKind.ScanPerNode,
    }.WithDesiredHash("scan-hash");

    [Fact]
    public async Task Scan_marker_with_ready_nodes_fans_out_then_clears_and_rests_Healthy()
    {
        var instance = NewInstance(state: WorkloadInstanceState.Deploying);
        instance.BeginScanRun("run-1", DateTimeOffset.UtcNow);
        var harness = NewScanHarness();
        harness.ThrowingProvider.SetScanReadyNodes("node-a", "node-b", "node-c");
        var engine = harness.Build();

        await engine.ReconcileInstanceAsync(instance);

        var applied = harness.Provider.LastAppliedSpec(instance.Id);
        applied.Should().NotBeNull();
        applied!.PendingScanRunId.Should().Be("run-1", "the engine threads the marker's run-id onto the apply carrier");
        applied.ScanReadyNodeNames.Should().BeEquivalentTo(new[] { "node-a", "node-b", "node-c" },
            "the engine-time Ready set is fanned out one Job per node");
        instance.HasPendingScanRun.Should().BeTrue("the run is still in flight (Jobs not yet terminal) — marker retained");
        instance.State.Should().Be(WorkloadInstanceState.Deploying, "a Job is active → Deploying (Scanning)");

        harness.Provider.SetStatus(instance.Id, WorkloadProviderPhase.Ready, providerReady: true, scanRunTerminal: true);

        await engine.ReconcileInstanceAsync(instance);

        instance.HasPendingScanRun.Should().BeFalse("the required node-Job set is fully terminal → marker cleared");
        instance.State.Should().Be(WorkloadInstanceState.Healthy, "a completed run rests Healthy (armed), never stuck Degraded");
        harness.Provider.LastAppliedSpec(instance.Id)!.ScanReadyNodeNames.Should().HaveCount(3);
    }

    [Fact]
    public async Task Scan_drift_without_marker_is_a_no_op_apply_no_jobs_and_settles_Healthy()
    {
        var instance = NewInstance(state: WorkloadInstanceState.Deploying);
        instance.HasPendingScanRun.Should().BeFalse();
        var harness = NewScanHarness();
        var engine = harness.Build();

        await engine.ReconcileInstanceAsync(instance);

        var applied = harness.Provider.LastAppliedSpec(instance.Id);
        applied.Should().NotBeNull();
        applied!.PendingScanRunId.Should().BeNull("no marker ⇒ the engine feeds the provider NO run-id ⇒ its scan branch is a no-op");
        (applied.ScanReadyNodeNames is null || applied.ScanReadyNodeNames.Count == 0).Should().BeTrue("no marker ⇒ no fan-out set");
        instance.AppliedSpecHash.Should().Be("scan-hash", "the drift settles to desired WITHOUT creating any scan Job");
        instance.HasPendingScanRun.Should().BeFalse("a bare drift never sets a scan marker");
        harness.ThrowingProvider.PreflightCallCount.Should().Be(0, "no enumeration is attempted without a marker");

        harness.Provider.SetStatus(instance.Id, WorkloadProviderPhase.Ready, providerReady: true);
        await engine.ReconcileInstanceAsync(instance);
        instance.State.Should().Be(WorkloadInstanceState.Healthy, "a scan instance with no run in flight rests Healthy (armed)");
    }

    [Fact]
    public async Task Scan_marker_with_zero_eligible_nodes_is_NoTargets_and_clears_the_marker_vacuously()
    {
        var instance = NewInstance(state: WorkloadInstanceState.Healthy);
        instance.BeginScanRun("run-1", DateTimeOffset.UtcNow);
        var harness = NewScanHarness();
        harness.ThrowingProvider.SetScanReadyNodes();
        var engine = harness.Build();

        var outcome = await engine.ReconcileInstanceAsync(instance);

        harness.Provider.ApplyCountFor(instance.Id).Should().Be(0, "zero eligible nodes ⇒ zero Jobs created");
        instance.HasPendingScanRun.Should().BeFalse("the marker is cleared VACUOUSLY in the SAME pass");
        instance.State.Should().Be(WorkloadInstanceState.Healthy, "the instance rests Healthy (armed) after a NoTargets run");
        outcome.Message.Should().Contain("no eligible", "the run is surfaced as NoTargets, not an empty success");

        var applied = await engine.ReconcileInstanceAsync(instance);

        var lastApplied = harness.Provider.LastAppliedSpec(instance.Id);
        lastApplied.Should().NotBeNull();
        lastApplied!.PendingScanRunId.Should().BeNull("the stale marker did not survive to turn drift into an unsolicited scan");
        applied.ResultingState.Should().NotBe(WorkloadInstanceState.Failed);
    }

    [Fact]
    public async Task Scan_per_node_failure_does_not_stick_the_instance_in_Degraded()
    {
        var instance = NewInstance(state: WorkloadInstanceState.Deploying);
        instance.BeginScanRun("run-1", DateTimeOffset.UtcNow);
        var harness = NewScanHarness();
        harness.ThrowingProvider.SetScanReadyNodes("node-a", "node-b", "node-c");
        var engine = harness.Build();

        await engine.ReconcileInstanceAsync(instance);

        harness.Provider.SetStatus(instance.Id, WorkloadProviderPhase.Ready, providerReady: true, scanRunTerminal: true,
            message: "scan complete: 2/3 nodes succeeded, node-b Job failed");

        await engine.ReconcileInstanceAsync(instance);

        instance.HasPendingScanRun.Should().BeFalse("the run terminated (all Jobs terminal) → marker cleared");
        instance.State.Should().Be(WorkloadInstanceState.Healthy,
            "a per-node failure is reported in the run outcome, NEVER a persistent instance-Degraded/Failed");

        harness.Provider.SetStatus(instance.Id, WorkloadProviderPhase.Failed, providerReady: false, message: "a scan Job failed");
        await engine.ReconcileInstanceAsync(instance);
        instance.State.Should().Be(WorkloadInstanceState.Healthy, "a scan instance never lands in a persistent Failed state");
    }

    [Fact]
    public async Task Scan_terminal_run_with_a_failed_node_records_and_names_that_node()
    {
        var instance = NewInstance(state: WorkloadInstanceState.Deploying);
        instance.BeginScanRun("run-1", DateTimeOffset.UtcNow);
        var harness = NewScanHarness();
        harness.ThrowingProvider.SetScanReadyNodes("node-a", "node-b", "node-c");
        var engine = harness.Build();

        await engine.ReconcileInstanceAsync(instance);

        harness.Provider.SetStatus(instance.Id, WorkloadProviderPhase.Ready, providerReady: true, scanRunTerminal: true,
            scanFailedNodeNames: new[] { "node-b" });

        var outcome = await engine.ReconcileInstanceAsync(instance);

        instance.HasPendingScanRun.Should().BeFalse("the run really did terminate — the marker is still consumed");
        instance.GetLastScanFailedNodes().Should().BeEquivalentTo(new[] { "node-b" },
            "the run must record WHICH node contributed nothing, not merely that it ended");
        instance.StateMessage.Should().Contain("node-b",
            "the instance must not read as an idle clean completion after a partial run");
        outcome.Message.Should().Contain("node-b", "the run outcome names the failed node too");
    }

    [Fact]
    public async Task Scan_clean_run_clears_the_failure_the_previous_run_recorded()
    {
        var instance = NewInstance(state: WorkloadInstanceState.Deploying);
        instance.BeginScanRun("run-1", DateTimeOffset.UtcNow);
        var harness = NewScanHarness();
        harness.ThrowingProvider.SetScanReadyNodes("node-a", "node-b");
        var engine = harness.Build();

        await engine.ReconcileInstanceAsync(instance);
        harness.Provider.SetStatus(instance.Id, WorkloadProviderPhase.Ready, providerReady: true, scanRunTerminal: true,
            scanFailedNodeNames: new[] { "node-b" });
        await engine.ReconcileInstanceAsync(instance);
        instance.GetLastScanFailedNodes().Should().NotBeEmpty("the partial run was recorded");

        instance.BeginScanRun("run-2", DateTimeOffset.UtcNow);
        harness.Provider.SetStatus(instance.Id, WorkloadProviderPhase.Ready, providerReady: true, scanRunTerminal: true);
        await engine.ReconcileInstanceAsync(instance);

        instance.GetLastScanFailedNodes().Should().BeEmpty("every node scanned this time");
        instance.State.Should().Be(WorkloadInstanceState.Healthy);
        instance.StateMessage.Should().NotContain("node-b", "the stale failure must not outlive the run that caused it");
    }

    [Fact]
    public async Task Scan_apply_retry_resumes_the_same_run_id_and_clears_only_when_the_set_is_present()
    {
        var instance = NewInstance(state: WorkloadInstanceState.Deploying);
        instance.BeginScanRun("run-1", DateTimeOffset.UtcNow);
        var harness = NewScanHarness();
        harness.ThrowingProvider.SetScanReadyNodes("node-a", "node-b");
        harness.ThrowingProvider.ThrowApplyCount = 1;
        var engine = harness.Build();

        await engine.ReconcileInstanceAsync(instance);
        instance.HasPendingScanRun.Should().BeTrue("a throwing apply retains the marker so the run retries");
        harness.Provider.ApplyCountFor(instance.Id).Should().Be(0, "the throwing apply created nothing server-side");

        await engine.ReconcileInstanceAsync(instance);
        instance.HasPendingScanRun.Should().BeTrue("the required set is present but Jobs are active — not yet cleared");
        harness.Provider.LastAppliedSpec(instance.Id)!.PendingScanRunId.Should().Be("run-1",
            "the retry resumes the SAME run-id — no new run minted");

        harness.Provider.SetStatus(instance.Id, WorkloadProviderPhase.Ready, providerReady: true, scanRunTerminal: true);

        await engine.ReconcileInstanceAsync(instance);
        instance.HasPendingScanRun.Should().BeFalse("the marker clears once the full required set is terminal");
        instance.State.Should().Be(WorkloadInstanceState.Healthy);
    }

    [Fact]
    public async Task Scan_failover_new_leader_resumes_the_same_run_id()
    {
        var instance = NewInstance(state: WorkloadInstanceState.Deploying);
        instance.BeginScanRun("run-1", DateTimeOffset.UtcNow);
        var harness = NewScanHarness();
        harness.ThrowingProvider.SetScanReadyNodes("node-a", "node-b");

        var leader1 = harness.Build();
        await leader1.ReconcileInstanceAsync(instance);
        instance.HasPendingScanRun.Should().BeTrue();

        harness.Provider.SetStatus(instance.Id, WorkloadProviderPhase.Ready, providerReady: true, scanRunTerminal: true);

        var leader2 = harness.Build();
        await leader2.ReconcileInstanceAsync(instance);

        harness.Provider.LastAppliedSpec(instance.Id)!.PendingScanRunId.Should().Be("run-1",
            "the new leader resumes the SAME persisted run-id (no double-run, no lost run)");
        instance.HasPendingScanRun.Should().BeFalse("the resumed run completes and the marker is cleared");
        instance.State.Should().Be(WorkloadInstanceState.Healthy);
    }

    [Fact]
    public async Task Scan_classify_maps_idle_active_and_terminal_to_the_scan_instance_state()
    {
        var now = DateTimeOffset.UtcNow;
        var harness = NewScanHarness();
        var engine = harness.Build();

        var idle = NewInstance(state: WorkloadInstanceState.Healthy);
        idle.SetProvider(KubernetesProvider.Id, now);
        idle.SetAppliedSpecHash("scan-hash", now);
        await engine.ReconcileInstanceAsync(idle);
        idle.State.Should().Be(WorkloadInstanceState.Healthy, "an idle scan instance (zero objects) rests Healthy (armed)");
        idle.StateReason.Should().Be(WorkloadStateReason.HealthObserved);

        var active = NewInstance(state: WorkloadInstanceState.Healthy);
        harness.PreApply(active, "scan-hash");
        harness.Provider.SetStatus(active.Id, WorkloadProviderPhase.Running, providerReady: false);
        await engine.ReconcileInstanceAsync(active);
        active.State.Should().Be(WorkloadInstanceState.Deploying, "a scan with an active Job is Deploying");
        active.StateMessage.Should().Contain("Scanning");

        var terminal = NewInstance(state: WorkloadInstanceState.Healthy);
        harness.PreApply(terminal, "scan-hash");
        harness.Provider.SetStatus(terminal.Id, WorkloadProviderPhase.Ready, providerReady: true);
        await engine.ReconcileInstanceAsync(terminal);
        terminal.State.Should().Be(WorkloadInstanceState.Healthy, "a completed scan run rests Healthy (armed)");
    }

    [Fact]
    public async Task Scan_nodes_403_is_a_loud_failure_that_retains_the_marker()
    {
        var instance = NewInstance(state: WorkloadInstanceState.Healthy);
        instance.BeginScanRun("run-1", DateTimeOffset.UtcNow);
        var harness = NewScanHarness();
        harness.ThrowingProvider.SetScanNodesUnreadable();
        var engine = harness.Build();

        var outcome = await engine.ReconcileInstanceAsync(instance);

        harness.Provider.ApplyCountFor(instance.Id).Should().Be(0, "a 403 must never create Jobs");
        instance.HasPendingScanRun.Should().BeTrue("the marker is RETAINED so a restored-RBAC pass can complete the run");
        outcome.Message.Should().NotContain("no eligible", "a 403 is a loud failure, NOT a NoTargets");
        outcome.Message.Should().Contain("retry", "the failure surfaces that the run remains pending");
    }

    [Fact]
    public async Task Scan_freshly_created_jobs_reading_Ready_do_NOT_consume_the_marker_on_the_fan_out_pass()
    {
        var instance = NewInstance(state: WorkloadInstanceState.Deploying);
        instance.BeginScanRun("run-1", DateTimeOffset.UtcNow);
        var harness = NewScanHarness();
        harness.ThrowingProvider.SetScanReadyNodes("node-a", "node-b", "node-c");
        harness.Provider.ScanFreshlyCreatedReadsReadyNotTerminal = true;
        var engine = harness.Build();

        await engine.ReconcileInstanceAsync(instance);

        harness.Provider.LastAppliedSpec(instance.Id)!.PendingScanRunId.Should().Be("run-1", "the run fanned out this pass");
        instance.HasPendingScanRun.Should().BeTrue(
            "a Ready(armed) read with no completion evidence is NOT run-terminal — the marker must survive the fan-out pass");

        harness.Provider.SetStatus(instance.Id, WorkloadProviderPhase.Ready, providerReady: true, scanRunTerminal: true);
        await engine.ReconcileInstanceAsync(instance);

        instance.HasPendingScanRun.Should().BeFalse(
            "once the set reports positive completion evidence (all Jobs succeeded/failed) the marker is consumed");
        instance.State.Should().Be(WorkloadInstanceState.Healthy, "a completed run rests Healthy (armed)");
    }

    [Fact]
    public async Task Scan_Ready_armed_without_completion_evidence_never_consumes_across_passes()
    {
        var instance = NewInstance(state: WorkloadInstanceState.Deploying);
        instance.BeginScanRun("run-1", DateTimeOffset.UtcNow);
        var harness = NewScanHarness();
        harness.ThrowingProvider.SetScanReadyNodes("node-a", "node-b");
        var engine = harness.Build();

        await engine.ReconcileInstanceAsync(instance);
        instance.HasPendingScanRun.Should().BeTrue();

        for (var pass = 0; pass < 3; pass++)
        {
            harness.Provider.SetStatus(instance.Id, WorkloadProviderPhase.Ready, providerReady: true, scanRunTerminal: false);
            await engine.ReconcileInstanceAsync(instance);
            instance.HasPendingScanRun.Should().BeTrue(
                "a Ready(armed) read with no completion evidence is never run-terminal — the marker is retained");
        }

        harness.Provider.SetStatus(instance.Id, WorkloadProviderPhase.Ready, providerReady: true, scanRunTerminal: true);
        await engine.ReconcileInstanceAsync(instance);
        instance.HasPendingScanRun.Should().BeFalse("positive completion evidence finally clears the marker");
    }

    [Fact]
    public async Task Suspended_ScanPerNode_instance_stays_Suspended_across_a_resync()
    {
        var instance = NewInstance(state: WorkloadInstanceState.Suspended);
        instance.SetDesiredSpecHash("scan-hash", DateTimeOffset.UtcNow);
        var harness = NewScanHarness();
        harness.PreApply(instance, "scan-hash");
        harness.Provider.SetStatus(instance.Id, WorkloadProviderPhase.Ready, providerReady: true);
        var engine = harness.Build();

        await engine.ReconcileInstanceAsync(instance);

        instance.State.Should().Be(WorkloadInstanceState.Suspended,
            "a suspended scan instance must be held exactly like every other kind — the reconciler cannot silently lift a suspend");
    }

    [Fact]
    public async Task Suspended_ScanPerNode_instance_preserves_its_OperatorPaused_StateReason()
    {
        var instance = NewInstance(state: WorkloadInstanceState.Suspended);
        instance.SetDesiredSpecHash("scan-hash", DateTimeOffset.UtcNow);
        instance.StateReason.Should().Be(WorkloadStateReason.OperatorPaused, "sanity: DriveTo(Suspended) holds via an operator pause");
        var harness = NewScanHarness();
        harness.PreApply(instance, "scan-hash");
        harness.Provider.SetStatus(instance.Id, WorkloadProviderPhase.Ready, providerReady: true);
        var engine = harness.Build();

        await engine.ReconcileInstanceAsync(instance);

        instance.StateReason.Should().Be(WorkloadStateReason.OperatorPaused,
            "the hold must stay attributed to the operator pause — not SuspendedByHost, and not HealthObserved");
    }

    [Fact]
    public async Task Suspended_ScanPerNode_instance_leads_with_the_operators_hold_reason_and_appends_the_providers_report()
    {
        var instance = NewInstance(state: WorkloadInstanceState.Suspended);
        instance.TransitionTo(WorkloadInstanceState.Suspended, WorkloadStateReason.OperatorPaused, "Paused by operator.", DateTimeOffset.UtcNow);
        instance.SetDesiredSpecHash("scan-hash", DateTimeOffset.UtcNow);
        var harness = NewScanHarness();
        harness.PreApply(instance, "scan-hash");
        harness.Provider.SetStatus(instance.Id, WorkloadProviderPhase.Ready, providerReady: true,
            message: "Scan suspended — 2 node(s) held.");
        var engine = harness.Build();

        await engine.ReconcileInstanceAsync(instance);

        instance.StateMessage.Should().NotBeNull();
        instance.StateMessage.Should().Contain("Paused by operator.", "the operator's own hold reason must survive, not be overwritten by the provider's live report");
        instance.StateMessage.Should().Contain("Scan suspended — 2 node(s) held.", "the provider's report is the only channel that reaches an operator for a held scan instance, so it must still appear");
        instance.StateMessage!.IndexOf("Paused by operator.", StringComparison.Ordinal)
            .Should().BeLessThan(instance.StateMessage.IndexOf("Scan suspended — 2 node(s) held.", StringComparison.Ordinal),
                "the operator's hold reason LEADS — it is the stable fact of who held this and why, and the provider's report is appended after it");
    }

    [Fact]
    public async Task Suspended_ScanPerNode_instance_keeps_only_the_operators_hold_reason_when_the_provider_reports_nothing()
    {
        var instance = NewInstance(state: WorkloadInstanceState.Suspended);
        instance.TransitionTo(WorkloadInstanceState.Suspended, WorkloadStateReason.OperatorPaused, "Paused by operator.", DateTimeOffset.UtcNow);
        instance.SetDesiredSpecHash("scan-hash", DateTimeOffset.UtcNow);
        var harness = NewScanHarness();
        harness.PreApply(instance, "scan-hash");
        harness.Provider.SetStatus(instance.Id, WorkloadProviderPhase.Ready, providerReady: true, message: string.Empty);
        var engine = harness.Build();

        await engine.ReconcileInstanceAsync(instance);

        instance.StateMessage.Should().Be("Paused by operator.",
            "with no provider report to append, the operator's hold reason survives verbatim and alone");
    }

    [Fact]
    public async Task Resumed_Suspended_ScanPerNode_instance_lifts_the_provider_hold_then_fans_out_in_the_same_pass()
    {
        var instance = NewInstance(state: WorkloadInstanceState.Deploying);
        instance.SetDesiredSpecHash("scan-hash", DateTimeOffset.UtcNow);
        instance.BeginScanRun("run-1", DateTimeOffset.UtcNow);
        var harness = NewScanHarness();
        harness.PreApply(instance, "scan-hash");
        await harness.Provider.SuspendAsync(WorkloadRef.FromInstance(instance));
        instance.TransitionTo(WorkloadInstanceState.Suspended, WorkloadStateReason.OperatorPaused, "paused", DateTimeOffset.UtcNow);
        instance.TransitionTo(WorkloadInstanceState.Deploying, WorkloadStateReason.OperatorResumed, "resumed", DateTimeOffset.UtcNow);
        harness.ThrowingProvider.SetScanReadyNodes("node-a");
        var recorder = new CallOrderRecordingProvider(harness.ThrowingProvider);
        harness.ProviderOverride = recorder;
        var engine = harness.Build();

        await engine.ReconcileInstanceAsync(instance);

        recorder.Calls.Should().ContainInOrder(new[] { nameof(IWorkloadProvider.ResumeAsync), nameof(IWorkloadProvider.ApplyAsync) },
            "ResumeAsync must lift the provider hold BEFORE the fan-out's ApplyAsync lands, in this same pass");
        harness.ThrowingProvider.ResumeCallCount.Should().Be(1, "leaving Suspended must ResumeAsync exactly once this pass");
        harness.Provider.IsSuspended(instance.Id).Should().BeFalse("the provider-side suspend mechanism is lifted");
        var applied = harness.Provider.LastAppliedSpec(instance.Id);
        applied.Should().NotBeNull("the fan-out DID happen on the very same pass as the resume");
        applied!.PendingScanRunId.Should().Be("run-1", "the queued run is the one that fans out — not a fresh/different run");
        instance.HasPendingScanRun.Should().BeTrue("the Job just created is not yet terminal — the marker is correctly still pending, not lost or prematurely cleared");
    }

    [Fact]
    public async Task Suspended_ScanPerNode_instance_with_a_pending_scan_run_does_not_fan_out()
    {
        var instance = NewInstance(state: WorkloadInstanceState.Suspended);
        instance.SetDesiredSpecHash("scan-hash", DateTimeOffset.UtcNow);
        instance.BeginScanRun("run-1", DateTimeOffset.UtcNow);
        var harness = NewScanHarness();
        harness.PreApply(instance, "scan-hash");
        harness.ThrowingProvider.SetScanReadyNodes("node-a");
        var engine = harness.Build();

        await engine.ReconcileInstanceAsync(instance);

        harness.ThrowingProvider.ApplyCallCount.Should().Be(0,
            "a held instance must never fan out — the scan-orchestration branch must not apply for a Suspended instance");
        instance.HasPendingScanRun.Should().BeTrue("the pending marker is RETAINED, not consumed, while the instance is held");
        instance.PendingScanRunId.Should().Be("run-1", "the queued run simply waits for resume — it is not cleared or replaced");
        instance.State.Should().Be(WorkloadInstanceState.Suspended);
    }

    [Fact]
    public async Task NonSuspended_ScanPerNode_instance_still_classifies_armed_Healthy()
    {
        var instance = NewInstance(state: WorkloadInstanceState.Deploying);
        var harness = NewScanHarness();
        var engine = harness.Build();

        await engine.ReconcileInstanceAsync(instance);
        harness.Provider.SetStatus(instance.Id, WorkloadProviderPhase.Ready, providerReady: true);
        await engine.ReconcileInstanceAsync(instance);

        instance.State.Should().Be(WorkloadInstanceState.Healthy,
            "an unheld scan instance rests Healthy(armed) exactly as before — the Suspended hold must not leak onto normal passes");
    }

    [Fact]
    public async Task NonSuspended_ScanPerNode_instance_with_a_pending_scan_run_still_fans_out()
    {
        var instance = NewInstance(state: WorkloadInstanceState.Deploying);
        instance.BeginScanRun("run-1", DateTimeOffset.UtcNow);
        var harness = NewScanHarness();
        harness.ThrowingProvider.SetScanReadyNodes("node-a", "node-b");
        var engine = harness.Build();

        await engine.ReconcileInstanceAsync(instance);

        var applied = harness.Provider.LastAppliedSpec(instance.Id);
        applied.Should().NotBeNull("the suspended-instance guard must not block fan-out for a non-suspended instance");
        applied!.PendingScanRunId.Should().Be("run-1");
        applied.ScanReadyNodeNames.Should().BeEquivalentTo(new[] { "node-a", "node-b" });
    }

    [Fact]
    public async Task ExternallyManaged_ScanPerNode_instance_is_never_re_adopted_by_the_scan_arm()
    {
        var instance = NewInstance(state: WorkloadInstanceState.Healthy);
        var harness = NewScanHarness();
        harness.PreApply(instance, "scan-hash");
        var now = DateTimeOffset.UtcNow;
        instance.TransitionTo(WorkloadInstanceState.Switching, WorkloadStateReason.HealthObserved, null, now);
        instance.TransitionTo(WorkloadInstanceState.ExternallyManaged, WorkloadStateReason.ExternalHealthObserved, null, now);
        instance.Provider.Should().Be(KubernetesProvider.Id, "sanity: the disown preserved the MANAGED provider — this is not the Manual one");
        harness.Provider.SetStatus(instance.Id, WorkloadProviderPhase.Ready, providerReady: true);
        var engine = harness.Build();

        await engine.ReconcileInstanceAsync(instance);

        instance.State.Should().Be(WorkloadInstanceState.ExternallyManaged,
            "a disowned scan instance is observe-only — the scan arm must hold it exactly as the default arm holds every other kind, never re-adopt it to Healthy");
    }

    [Fact]
    public async Task ExternallyManaged_ScanPerNode_instance_keeps_its_ExternalHealthObserved_reason()
    {
        var instance = NewInstance(state: WorkloadInstanceState.Healthy);
        var harness = NewScanHarness();
        harness.PreApply(instance, "scan-hash");
        var now = DateTimeOffset.UtcNow;
        instance.TransitionTo(WorkloadInstanceState.Switching, WorkloadStateReason.HealthObserved, null, now);
        instance.TransitionTo(WorkloadInstanceState.ExternallyManaged, WorkloadStateReason.ExternalHealthObserved, null, now);
        harness.Provider.SetStatus(instance.Id, WorkloadProviderPhase.Ready, providerReady: true);
        var engine = harness.Build();

        await engine.ReconcileInstanceAsync(instance);

        instance.StateReason.Should().Be(WorkloadStateReason.ExternalHealthObserved,
            "the hold must stay attributed to the released ownership — not HealthObserved, which is what an armed scan rests on");
    }

    [Fact]
    public async Task ExternallyManaged_ScanPerNode_instance_with_a_pending_scan_run_does_not_fan_out()
    {
        var instance = NewInstance(state: WorkloadInstanceState.Healthy);
        var harness = NewScanHarness();
        harness.PreApply(instance, "scan-hash");
        var now = DateTimeOffset.UtcNow;
        instance.TransitionTo(WorkloadInstanceState.Switching, WorkloadStateReason.HealthObserved, null, now);
        instance.TransitionTo(WorkloadInstanceState.ExternallyManaged, WorkloadStateReason.ExternalHealthObserved, null, now);
        instance.BeginScanRun("run-1", now);
        harness.ThrowingProvider.SetScanReadyNodes("node-a");
        var engine = harness.Build();

        await engine.ReconcileInstanceAsync(instance);

        harness.ThrowingProvider.ApplyCallCount.Should().Be(0,
            "a disowned instance must never fan out — the scan-orchestration branch must not apply for an ExternallyManaged instance");
        instance.HasPendingScanRun.Should().BeTrue("the pending marker is RETAINED, not consumed, while ownership is released");
        instance.State.Should().Be(WorkloadInstanceState.ExternallyManaged);
    }

    [Fact]
    public async Task ExternallyManaged_ScanPerNode_instance_on_a_NON_materialising_provider_is_not_held_by_the_scan_arm()
    {
        var instance = NewInstance(state: WorkloadInstanceState.Healthy);
        var harness = new Harness
        {
            ManifestKind = WorkloadKind.ScanPerNode,
            RegisteredProvider = ManualProvider.Id,
            EffectiveProvider = ManualProvider.Descriptor,
        }.WithDesiredHash("scan-hash");
        var now = DateTimeOffset.UtcNow;
        await harness.Provider.ApplyAsync(SpecFor(instance.Id, "scan-hash", harness.HealthMode), progress: null);
        instance.SetProvider(ManualProvider.Id, now);
        instance.SetAppliedSpecHash("scan-hash", now);
        instance.TransitionTo(WorkloadInstanceState.ExternallyManaged, WorkloadStateReason.ExternalHealthObserved, null, now);
        harness.Provider.SetStatus(instance.Id, WorkloadProviderPhase.Ready, providerReady: true);
        var engine = harness.Build();

        await engine.ReconcileInstanceAsync(instance);

        instance.State.Should().Be(WorkloadInstanceState.Healthy,
            "a manual-provider scan instance is not a disowned managed one — the scan arm's hold is scoped to providers that actually materialise a runtime");
    }

    [Fact]
    public async Task Switching_ScanPerNode_instance_with_a_pending_scan_run_does_not_fan_out()
    {
        var instance = NewInstance(state: WorkloadInstanceState.Healthy);
        var harness = NewScanHarness();
        harness.PreApply(instance, "scan-hash");
        var now = DateTimeOffset.UtcNow;
        instance.TransitionTo(WorkloadInstanceState.Switching, WorkloadStateReason.HealthObserved, null, now);
        instance.BeginScanRun("run-1", now);
        harness.ThrowingProvider.SetScanReadyNodes("node-a");
        var engine = harness.Build();

        await engine.ReconcileInstanceAsync(instance);

        harness.ThrowingProvider.ApplyCallCount.Should().Be(0,
            "a mid-switch instance is ownership-frozen — the scan-orchestration branch must not apply for it either");
        instance.HasPendingScanRun.Should().BeTrue("the queued run is retained until the switch settles");
        instance.State.Should().Be(WorkloadInstanceState.Switching);
    }

    [Fact]
    public async Task Switching_ScanPerNode_instance_is_held_by_the_arm_above_the_scan_arm()
    {
        var instance = NewInstance(state: WorkloadInstanceState.Healthy);
        var harness = NewScanHarness();
        harness.PreApply(instance, "scan-hash");
        instance.TransitionTo(WorkloadInstanceState.Switching, WorkloadStateReason.HealthObserved, null, DateTimeOffset.UtcNow);
        harness.Provider.SetStatus(instance.Id, WorkloadProviderPhase.Ready, providerReady: true);
        var engine = harness.Build();

        await engine.ReconcileInstanceAsync(instance);

        instance.State.Should().Be(WorkloadInstanceState.Switching,
            "a mid-switch scan instance is ownership-frozen by the arm above the scan arm — the scan arm must never preempt it");
    }

    [Fact]
    public async Task Scan_fan_out_gates_on_the_manifest_Kind_not_on_an_intent_targets_Kind()
    {
        var instance = NewInstance(state: WorkloadInstanceState.Deploying);
        instance.BeginScanRun("run-1", DateTimeOffset.UtcNow);
        var harness = new Harness { ManifestKind = WorkloadKind.SingleNode }.WithDesiredHash("intent-hash");
        harness.ActiveIntentTarget = SpecFor(instance.Id, "intent-hash", harness.HealthMode) with { Kind = WorkloadKind.ScanPerNode };
        harness.ThrowingProvider.SetScanReadyNodes("node-a");
        var engine = harness.Build();

        await engine.ReconcileInstanceAsync(instance);

        harness.ThrowingProvider.PreflightCallCount.Should().Be(0,
            "the fan-out must not even enumerate nodes for an instance the authoritative manifest says is not a scan");
        harness.Provider.LastAppliedSpec(instance.Id)!.PendingScanRunId.Should().BeNull(
            "no scan run is threaded onto the carrier — the ordinary drift apply runs instead");
    }

    [Fact]
    public async Task Scan_fan_out_refuses_a_carrier_spec_that_is_not_itself_a_scan_spec()
    {
        var instance = NewInstance(state: WorkloadInstanceState.Deploying);
        instance.BeginScanRun("run-1", DateTimeOffset.UtcNow);
        var harness = NewScanHarness();
        harness.ActiveIntentTarget = SpecFor(instance.Id, "scan-hash", harness.HealthMode) with { Kind = WorkloadKind.SingleNode };
        harness.ThrowingProvider.SetScanReadyNodes("node-a");
        var engine = harness.Build();

        await engine.ReconcileInstanceAsync(instance);

        harness.ThrowingProvider.PreflightCallCount.Should().Be(0,
            "a carrier that is not a scan spec cannot serve the run — no enumeration is attempted");
        harness.Provider.LastAppliedSpec(instance.Id)!.PendingScanRunId.Should().BeNull(
            "the run-id must never be threaded onto a carrier of another kind");
        instance.HasPendingScanRun.Should().BeTrue(
            "the marker is RETAINED — a mid-update kind change is transient, and the queued run resumes once the two agree again");
    }

    [Fact]
    public async Task Suspended_ScanPerNode_instance_with_a_pending_scan_run_and_DRIFT_does_not_fan_out()
    {
        var instance = NewInstance(state: WorkloadInstanceState.Suspended);
        var harness = NewScanHarness();
        harness.PreApply(instance, "stale-hash");
        instance.BeginScanRun("run-1", DateTimeOffset.UtcNow);
        harness.ThrowingProvider.SetScanReadyNodes("node-a");
        var engine = harness.Build();

        var outcome = await engine.ReconcileInstanceAsync(instance);

        harness.ThrowingProvider.ApplyCallCount.Should().Be(0,
            "with drift the ordinary gate returns DeferredButRead for a Suspended instance — the apply is gated, nothing is created");
        outcome.PendingApply.Should().BeTrue("the drift is recorded as pending, to be applied on resume");
        instance.HasPendingScanRun.Should().BeTrue("the queued run waits for the resume — the marker is not consumed");
        instance.State.Should().Be(WorkloadInstanceState.Suspended);
    }

    private static WorkloadInstance NewInstance(
        WorkloadInstanceState state = WorkloadInstanceState.PendingConfiguration,
        bool observeOnly = false)
    {
        var instance = WorkloadInstance.Create(Guid.NewGuid(), Guid.NewGuid(), GroupId, WorkloadId, DateTimeOffset.UtcNow, observeOnly);
        DriveTo(instance, state);
        return instance;
    }

    private static void DriveTo(WorkloadInstance instance, WorkloadInstanceState target)
    {
        if (instance.State == target)
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;
        switch (target)
        {
            case WorkloadInstanceState.PendingConfiguration:
                return;
            case WorkloadInstanceState.Deploying:
                instance.TransitionTo(WorkloadInstanceState.Deploying, WorkloadStateReason.Provisioning, null, now);
                return;
            case WorkloadInstanceState.Healthy:
                instance.TransitionTo(WorkloadInstanceState.Deploying, WorkloadStateReason.Provisioning, null, now);
                instance.TransitionTo(WorkloadInstanceState.Healthy, WorkloadStateReason.HealthObserved, null, now);
                return;
            case WorkloadInstanceState.Failed:
                instance.TransitionTo(WorkloadInstanceState.Deploying, WorkloadStateReason.Provisioning, null, now);
                instance.TransitionTo(WorkloadInstanceState.Failed, WorkloadStateReason.HealthLost, null, now);
                return;
            case WorkloadInstanceState.Suspended:
                instance.TransitionTo(WorkloadInstanceState.Deploying, WorkloadStateReason.Provisioning, null, now);
                instance.TransitionTo(WorkloadInstanceState.Suspended, WorkloadStateReason.OperatorPaused, null, now);
                return;
            default:
                throw new InvalidOperationException($"Test helper cannot drive to {target}.");
        }
    }

    private sealed class Harness
    {
        public WorkloadHealthMode HealthMode { get; init; } = new WorkloadHealthMode("adapter-session");
        public bool ReconciliationPaused { get; init; }

        public WorkloadKind ManifestKind { get; init; } = WorkloadKind.SingleNode;

        public WorkloadPrivileges? ManifestPrivileges { get; set; }

        public WorkloadNetwork? ManifestNetwork { get; set; }

        public IReadOnlyList<WorkloadParameter> ManifestParameters { get; set; } = Array.Empty<WorkloadParameter>();

        public IReadOnlyList<WorkloadVolume> ManifestVolumes { get; set; } = Array.Empty<WorkloadVolume>();

        public IReadOnlyList<HostRequirement> HostRequirements { get; set; } = Array.Empty<HostRequirement>();

        public IReadOnlyDictionary<string, string?> ResolvedParameters { get; set; } =
            new Dictionary<string, string?>(StringComparer.Ordinal);

        public bool UseRealResolver { get; set; }

        public IWorkloadParameterReferenceResolver? ParameterReferenceResolver { get; set; }

        public FakeRetainedVolumeService RetainedVolumeService { get; } = new();

        public string? InstalledGroupVersion { get; set; }

        public WorkloadDesiredSpec? ActiveIntentTarget { get; set; }

        public bool ResolverThrows { get; set; }

        public Exception ResolverException { get; set; } =
            new WorkloadParameterResolutionException("devicePath", "device path must be under /dev/**");

        public WorkloadProviderId? RegisteredProvider { get; init; } = KubernetesProvider.Id;

        public WorkloadProviderDescriptor EffectiveProvider { get; init; } = KubernetesProvider.Descriptor;

        public bool? Inputs { get; init; } = true;

        public string? HostRefusal { get; init; }

        public string? ProviderBindingConflict { get; init; }

        public Microsoft.Extensions.Logging.ILogger<WorkloadReconcileEngine> Logger { get; init; } =
            NullLogger<WorkloadReconcileEngine>.Instance;

        public ThrowingStatusProvider ThrowingProvider { get; } = new();
        public InMemoryWorkloadProvider Provider => ThrowingProvider.Inner;

        public MintingCredentialPort CredentialPort { get; } = new();

        public IWorkloadProvider? ProviderOverride { get; set; }
        public FakeHostHealthContributor Presence { get; } = new();
        public RecordingRepository Repository { get; } = new();

        public IWorkloadClock Clock { get; init; } = new FixedClock();

        public WorkloadReconcilerOptions ReconcilerOptions { get; init; } = new();
        public UnpinnedReferenceRolloutGuard ReferenceRolloutGuard { get; } = new(new Microsoft.AspNetCore.DataProtection.EphemeralDataProtectionProvider());

        private string desiredHash = "hash-default";

        public Harness WithDesiredHash(string hash)
        {
            desiredHash = hash;
            return this;
        }

        public void PreApply(WorkloadInstance instance, string hash)
        {
            var spec = SpecFor(instance.Id, hash, HealthMode);
            Provider.ApplyAsync(spec, progress: null).GetAwaiter().GetResult();
            instance.SetProvider(KubernetesProvider.Id, DateTimeOffset.UtcNow);
            instance.SetAppliedSpecHash(hash, DateTimeOffset.UtcNow);
        }

        public WorkloadReconcileEngine Build()
        {
            var inputsProvider = new FakeInputsProvider(_ =>
            {
                if (HostRefusal is { } refusal)
                {
                    return Task.FromResult(WorkloadReconcileInputsResult.HostRefused(refusal));
                }

                if (ProviderBindingConflict is { } conflict)
                {
                    return Task.FromResult(WorkloadReconcileInputsResult.ProviderBindingConflict(conflict));
                }

                if (Inputs is null)
                {
                    return Task.FromResult(WorkloadReconcileInputsResult.NotDeclared);
                }

                var inputs = BuildInputs();
                return Task.FromResult(WorkloadReconcileInputsResult.Declared(inputs));
            });
            IDesiredSpecResolver resolver = ResolverThrows
                ? new ThrowingDesiredSpecResolver(ResolverException)
                : UseRealResolver
                    ? new PassthroughDesiredSpecResolver()
                    : new StubDesiredSpecResolver(desiredHash);
            ThrowingProvider.Descriptor = EffectiveProvider;
            var registry = new StubRegistry(RegisteredProvider, ProviderOverride ?? ThrowingProvider);

            return new WorkloadReconcileEngine(
                inputsProvider,
                resolver,
                registry,
                Repository,
                Presence.Dispatch(),
                Clock,
                Logger,
                RetainedVolumeService,
                ActiveIntentTarget is null
                    ? new Reeve.Workloads.Abstractions.NullActiveWorkloadIntentProvider()
                    : new StubActiveIntentProvider(ActiveIntentTarget),
                ParameterReferenceResolver ?? new NoWorkloadParameterReferenceResolver(),
                new WorkloadParameterSecretCodec(new Microsoft.AspNetCore.DataProtection.EphemeralDataProtectionProvider()),
                null,
                ReferenceRolloutGuard, CredentialPort,
                Microsoft.Extensions.Options.Options.Create(ReconcilerOptions));
        }

        private WorkloadReconcileInputs BuildInputs() => new()
        {
            Namer = DefaultWorkloadResourceNamer.Instance,
            WorkloadManifest = ManifestFor(HealthMode, ManifestPrivileges, ManifestNetwork, ManifestParameters, ManifestVolumes, HostRequirements, ManifestKind),
            ResolvedParameters = ResolvedParameters,
            Settings = new WorkloadOrchestratorSettings { ReconciliationPaused = ReconciliationPaused },
            Bootstrap = new WorkloadBootstrapContext { InstanceId = Guid.NewGuid(), SecretName = "reeve-wl-secret" },
            EffectiveProvider = EffectiveProvider,
            InstallationId = "install-1",
            InstalledGroupVersion = InstalledGroupVersion,
        };

    }

    private static WorkloadManifest ManifestFor(
        WorkloadHealthMode mode,
        WorkloadPrivileges? privileges = null,
        WorkloadNetwork? network = null,
        IReadOnlyList<WorkloadParameter>? parameters = null,
        IReadOnlyList<WorkloadVolume>? volumes = null,
        IReadOnlyList<HostRequirement>? hostRequirements = null,
        WorkloadKind kind = WorkloadKind.SingleNode) => new()
    {
        Id = WorkloadId,
        DisplayName = "Z-Wave JS",
        Image = new WorkloadImage { Ref = "registry.example/zwave-js", Digest = "sha256:" + new string('a', 64) },
        Kind = kind,
        Network = network,
        Privileges = privileges,
        Parameters = parameters ?? Array.Empty<WorkloadParameter>(),
        Volumes = volumes ?? Array.Empty<WorkloadVolume>(),
        HostRequirements = hostRequirements ?? Array.Empty<HostRequirement>(),
        Health = new WorkloadHealth { Mode = mode, HeartbeatSeconds = 30 },
    };

    private static WorkloadDesiredSpec SpecFor(Guid instanceId, string hash, WorkloadHealthMode mode)
    {
        var inst8 = WorkloadInstance.ComputeInst8(instanceId);
        return new WorkloadDesiredSpec
        {
            GroupInstanceId = Guid.NewGuid(),
            GroupId = GroupId,
            WorkloadId = WorkloadId,
            InstanceId = instanceId,
            Inst8 = inst8,
            Provider = KubernetesProvider.Id,
            ResourceName = DefaultWorkloadResourceNamer.Instance.ObjectName(new WorkloadNamingSubject
                        {
                            GroupId = GroupId,
                            WorkloadId = WorkloadId,
                            Inst8 = inst8,
                        }),
            SecretName = DefaultWorkloadResourceNamer.Instance.SecretName(new WorkloadNamingSubject
                        {
                            GroupId = GroupId,
                            WorkloadId = WorkloadId,
                            Inst8 = inst8,
                        }),
            Labels = DefaultWorkloadResourceNamer.Instance.Labels(new WorkloadLabelSubject
                     {
                         InstanceId = instanceId,
                         InstallationId = "install-1",
                         SpecHash = hash,
                     }),
            Image = "registry.example/zwave-js@sha256:" + new string('a', 64),
            Kind = WorkloadKind.SingleNode,
            Health = new WorkloadHealth { Mode = mode, HeartbeatSeconds = 30 },
            SpecHash = hash,
        };
    }

    private sealed class FakeInputsProvider(Func<CancellationToken, Task<WorkloadReconcileInputsResult>> inputsFactory) : IWorkloadReconcileInputsProvider
    {
        public Task<WorkloadReconcileInputsResult> GetInputsAsync(WorkloadInstance instance, CancellationToken cancellationToken = default)
            => inputsFactory(cancellationToken);
    }

    private sealed class StaticReferenceResolver(WorkloadReferenceResolution result) : IWorkloadParameterReferenceResolver
    {
        public ValueTask<WorkloadReferenceResolution> ResolveAsync(string reference, CancellationToken cancellationToken = default)
            => ValueTask.FromResult(result);
    }

    private sealed class MutableReferenceResolver(WorkloadReferenceResolution result) : IWorkloadParameterReferenceResolver
    {
        public WorkloadReferenceResolution Result { get; set; } = result;
        public ValueTask<WorkloadReferenceResolution> ResolveAsync(string reference, CancellationToken cancellationToken = default)
            => ValueTask.FromResult(Result);
    }

    private sealed class StubDesiredSpecResolver(string desiredHash) : IDesiredSpecResolver
    {
        public Task<WorkloadDesiredSpec> ResolveAsync(
            Guid pluginInstanceId, string workloadId, Func<WorkloadDesiredSpec> computeFresh, CancellationToken cancellationToken)
            => Task.FromResult(computeFresh() with { SpecHash = desiredHash });
    }

    private sealed class StubActiveIntentProvider(WorkloadDesiredSpec target) : IActiveWorkloadIntentProvider
    {
        public Task<WorkloadDesiredSpec?> TryGetActiveTargetSpecAsync(Guid pluginInstanceId, string workloadId, CancellationToken cancellationToken)
            => Task.FromResult<WorkloadDesiredSpec?>(target);
    }

    private sealed class ThrowingDesiredSpecResolver(Exception exception) : IDesiredSpecResolver
    {
        public Task<WorkloadDesiredSpec> ResolveAsync(
            Guid pluginInstanceId, string workloadId, Func<WorkloadDesiredSpec> computeFresh, CancellationToken cancellationToken)
            => throw exception;
    }

    private sealed class PassthroughDesiredSpecResolver : IDesiredSpecResolver
    {
        public Task<WorkloadDesiredSpec> ResolveAsync(
            Guid pluginInstanceId, string workloadId, Func<WorkloadDesiredSpec> computeFresh, CancellationToken cancellationToken)
            => Task.FromResult(computeFresh());
    }

    private sealed class FakeRetainedVolumeService : IRetainedVolumeService
    {
        public List<Guid> ReattachedForInstance { get; } = new();
        public bool Throws { get; set; }

        public int ReattachRowCount { get; set; } = 1;

        public Task<int> MarkReattachedForInstanceAsync(Guid sourceWorkloadInstanceId, DateTimeOffset nowUtc, CancellationToken cancellationToken = default)
        {
            if (Throws)
            {
                throw new InvalidOperationException("retained-volume store unavailable (test)");
            }

            ReattachedForInstance.Add(sourceWorkloadInstanceId);
            return Task.FromResult(ReattachRowCount);
        }

        public Task<int> RecordRetainedAsync(WorkloadInstance instance, IReadOnlyList<RetainedVolumeInput> volumes, DateTimeOffset nowUtc, CancellationToken cancellationToken = default)
            => Task.FromResult(0);
        public Task<IReadOnlyList<Reeve.Workloads.Abstractions.Domain.Entities.RetainedVolume>> ListAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<Reeve.Workloads.Abstractions.Domain.Entities.RetainedVolume>>(Array.Empty<Reeve.Workloads.Abstractions.Domain.Entities.RetainedVolume>());
        public Task<IReadOnlyList<Reeve.Workloads.Abstractions.Domain.Entities.RetainedVolume>> ListActiveAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<Reeve.Workloads.Abstractions.Domain.Entities.RetainedVolume>>(Array.Empty<Reeve.Workloads.Abstractions.Domain.Entities.RetainedVolume>());
        private readonly List<Reeve.Workloads.Abstractions.Domain.Entities.RetainedVolume> activeByPluginType = new();

        public List<Guid> ReattachedById { get; } = new();

        public void SeedActive(Guid id, string pluginTypeId, string workloadId, string volumeId, string resourceName)
            => activeByPluginType.Add(Reeve.Workloads.Abstractions.Domain.Entities.RetainedVolume.Record(
                id, pluginTypeId, workloadId, volumeId, resourceName, "1Gi",
                VolumeCriticality.Critical, description: null, KubernetesProvider.Id,
                Reeve.Workloads.Abstractions.Domain.Entities.RetainedVolumeSchemeId.Kubernetes,
                sourceGroupInstanceId: Guid.NewGuid(), sourceWorkloadInstanceId: Guid.NewGuid(),
                retainedAtUtc: new DateTimeOffset(2026, 7, 6, 12, 0, 0, TimeSpan.Zero)));

        public Reeve.Workloads.Abstractions.Domain.Entities.RetainedVolume? SeededActiveRow(Guid id)
            => activeByPluginType.FirstOrDefault(r => r.Id == id);

        public void SeedActiveClaimedBy(Guid id, string pluginTypeId, string workloadId, string volumeId, string resourceName, Guid claimingInstanceId)
        {
            SeedActive(id, pluginTypeId, workloadId, volumeId, resourceName);
            activeByPluginType.First(r => r.Id == id).TryClaimForRestore(claimingInstanceId);
        }

        public Task<IReadOnlyList<Reeve.Workloads.Abstractions.Domain.Entities.RetainedVolume>> ListActiveByPluginTypeAsync(string pluginTypeId, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<Reeve.Workloads.Abstractions.Domain.Entities.RetainedVolume>>(
                activeByPluginType.Where(r => r.GroupId == pluginTypeId && r.IsActive).ToList());
        public Task<IReadOnlyList<Reeve.Workloads.Abstractions.Domain.Entities.RetainedVolume>> ListOfferableByPluginTypeAsync(string pluginTypeId, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<Reeve.Workloads.Abstractions.Domain.Entities.RetainedVolume>>(
                activeByPluginType.Where(r => r.GroupId == pluginTypeId && r.IsActive && r.ClaimedByWorkloadInstanceId is null).ToList());
        public Task<IReadOnlyList<Reeve.Workloads.Abstractions.Domain.Entities.RetainedVolume>> ListActiveByPluginTypeForInstanceAsync(string pluginTypeId, Guid forWorkloadInstanceId, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<Reeve.Workloads.Abstractions.Domain.Entities.RetainedVolume>>(
                activeByPluginType.Where(r => r.GroupId == pluginTypeId && r.IsActive
                    && (r.ClaimedByWorkloadInstanceId is null || r.ClaimedByWorkloadInstanceId == forWorkloadInstanceId)).ToList());
        public Task<bool> TryClaimForRestoreAsync(Guid retainedVolumeId, Guid claimingWorkloadInstanceId, CancellationToken cancellationToken = default)
            => Task.FromResult(activeByPluginType.FirstOrDefault(r => r.Id == retainedVolumeId)?.TryClaimForRestore(claimingWorkloadInstanceId) ?? false);
        public Task<int> ReleaseClaimsForInstanceAsync(Guid claimingWorkloadInstanceId, CancellationToken cancellationToken = default)
            => Task.FromResult(activeByPluginType.Count(r => r.ReleaseClaimFor(claimingWorkloadInstanceId)));
        public Task<bool> AnyActiveClaimForResourceNameAsync(string resourceName, Guid? excludingClaimingInstanceId, CancellationToken cancellationToken = default)
            => Task.FromResult(activeByPluginType.Any(r => r.ResourceName == (resourceName ?? string.Empty).Trim()
                                                           && r.IsActive
                                                           && r.ClaimedByWorkloadInstanceId is not null
                                                           && r.ClaimedByWorkloadInstanceId != excludingClaimingInstanceId));
        public Task<bool> AnyActiveClaimByInstanceAsync(Guid claimingWorkloadInstanceId, CancellationToken cancellationToken = default)
            => Task.FromResult(activeByPluginType.Any(r => r.IsActive && r.ClaimedByWorkloadInstanceId == claimingWorkloadInstanceId));
        public Task<IReadOnlyList<Reeve.Workloads.Abstractions.Domain.Entities.RetainedVolume>> ListActiveBySourceInstanceAsync(Guid sourceWorkloadInstanceId, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<Reeve.Workloads.Abstractions.Domain.Entities.RetainedVolume>>(
                activeByPluginType.Where(r => r.SourceWorkloadInstanceId == sourceWorkloadInstanceId && r.IsActive).ToList());
        public Task<IReadOnlyList<Reeve.Workloads.Abstractions.Domain.Entities.RetainedVolume>> ListOlderThanAsync(TimeSpan age, DateTimeOffset nowUtc, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<Reeve.Workloads.Abstractions.Domain.Entities.RetainedVolume>>(Array.Empty<Reeve.Workloads.Abstractions.Domain.Entities.RetainedVolume>());
        public Task MarkReattachedAsync(Guid id, DateTimeOffset nowUtc, CancellationToken cancellationToken = default)
        {
            ReattachedById.Add(id);
            activeByPluginType.FirstOrDefault(r => r.Id == id)?.MarkReattached(nowUtc);
            return Task.CompletedTask;
        }
        public Task PurgeAsync(Guid id, DateTimeOffset nowUtc, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<bool> TryMarkReattachedAsync(Guid id, DateTimeOffset nowUtc, CancellationToken cancellationToken = default)
            => Task.FromResult(false);
        public Task<RetainedVolumePurgeResult> TryPurgeAsync(Guid id, DateTimeOffset nowUtc, CancellationToken cancellationToken = default)
            => Task.FromResult(RetainedVolumePurgeResult.NotFound());
        public Task<int> MarkPurgedForInstanceAsync(Guid sourceWorkloadInstanceId, DateTimeOffset nowUtc, CancellationToken cancellationToken = default)
            => Task.FromResult(0);
    }

    private sealed class StubRegistry(WorkloadProviderId? registered, IWorkloadProvider provider) : IWorkloadProviderRegistry
    {
        public IWorkloadProvider? TryResolve(WorkloadProviderId kind)
            => registered is { } r && r == kind ? provider : null;

        public IWorkloadProvider Resolve(WorkloadProviderId kind)
            => TryResolve(kind) ?? throw new WorkloadProviderNotRegisteredException(kind);

        public IReadOnlyCollection<WorkloadProviderId> RegisteredProviders
            => registered is { } r ? new[] { r } : Array.Empty<WorkloadProviderId>();

        public IReadOnlyCollection<WorkloadProviderDescriptor> Descriptors
            => [.. RegisteredProviders.Select(Describe)];

        public WorkloadProviderDescriptor Describe(WorkloadProviderId kind)
            => TryResolve(kind)?.Descriptor ?? ShippedProviders.For(kind);
    }

    private sealed class ThrowingStatusProvider : IWorkloadProvider
    {
        public InMemoryWorkloadProvider Inner { get; } = new(KubernetesProvider.Descriptor);
        public bool ThrowOnStatus { get; set; }
        public bool ThrowOnTeardown { get; set; }

        public int FailApplyCount { get; set; }

        public int ThrowApplyCount { get; set; }

        public WorkloadApplyProgress? ProgressToReport { get; set; }

        public int ApplyCallCount { get; private set; }

        public WorkloadProviderDescriptor Descriptor { get; set; } = KubernetesProvider.Descriptor;

        public Func<WorkloadDesiredSpec, Task>? DuringApply { get; set; }

        public async Task<WorkloadApplyResult> ApplyAsync(WorkloadDesiredSpec spec, IProgress<WorkloadApplyProgress>? progress, CancellationToken cancellationToken = default, bool forceConflicts = false)
        {
            if (DuringApply is { } during)
            {
                await during(spec);
            }

            return await ApplyCoreAsync(spec, progress, cancellationToken, forceConflicts);
        }

        private Task<WorkloadApplyResult> ApplyCoreAsync(WorkloadDesiredSpec spec, IProgress<WorkloadApplyProgress>? progress, CancellationToken cancellationToken, bool forceConflicts)
        {
            ApplyCallCount++;
            if (ThrowApplyCount > 0)
            {
                ThrowApplyCount--;
                throw new InvalidOperationException("apply threw (test) — provider unreachable/timeout mid-apply");
            }

            if (FailApplyCount > 0)
            {
                FailApplyCount--;
                return Task.FromResult(WorkloadApplyResult.Failed(
                    WorkloadApplyReason.ProviderUnreachable, "apply failed (test)"));
            }

            if (ProgressToReport is { } snapshot)
            {
                progress?.Report(snapshot);
            }

            return Inner.ApplyAsync(spec, progress, cancellationToken, forceConflicts);
        }

        public Task TeardownAsync(WorkloadRef reference, bool retainVolumes, CancellationToken cancellationToken = default)
            => ThrowOnTeardown
                ? throw new InvalidOperationException("provider unreachable during teardown (test)")
                : Inner.TeardownAsync(reference, retainVolumes, cancellationToken);

        public int ThrowSuspendCount { get; set; }

        public int SuspendCallCount { get; private set; }
        public int ResumeCallCount { get; private set; }

        public Task SuspendAsync(WorkloadRef reference, CancellationToken cancellationToken = default)
        {
            SuspendCallCount++;
            if (ThrowSuspendCount > 0)
            {
                ThrowSuspendCount--;
                throw new InvalidOperationException("provider unreachable during suspend (test)");
            }

            return Inner.SuspendAsync(reference, cancellationToken);
        }

        public Task ResumeAsync(WorkloadRef reference, CancellationToken cancellationToken = default)
        {
            ResumeCallCount++;
            return Inner.ResumeAsync(reference, cancellationToken);
        }

        public Task<WorkloadStatus> GetStatusAsync(WorkloadRef reference, CancellationToken cancellationToken = default)
            => ThrowOnStatus
                ? throw new InvalidOperationException("provider unreachable (test)")
                : Inner.GetStatusAsync(reference, cancellationToken);

        private IReadOnlyList<string>? scanReadyNodes;

        private bool scanNodesUnreadable;

        private bool overrideScanPreflight;

        public int ThrowPreflightCount { get; set; }

        public int PreflightCallCount { get; private set; }

        public void SetScanReadyNodes(params string[] nodeNames)
        {
            scanReadyNodes = nodeNames;
            scanNodesUnreadable = false;
            overrideScanPreflight = true;
        }

        public void SetScanNodesUnreadable()
        {
            scanReadyNodes = null;
            scanNodesUnreadable = true;
            overrideScanPreflight = true;
        }

        public Task<WorkloadPreflightResult> PreflightAsync(WorkloadDesiredSpec spec, CancellationToken cancellationToken = default)
        {
            PreflightCallCount++;
            if (ThrowPreflightCount > 0)
            {
                ThrowPreflightCount--;
                throw new InvalidOperationException("preflight threw (test) — node enumeration faulted");
            }

            if (!overrideScanPreflight)
            {
                return Inner.PreflightAsync(spec, cancellationToken);
            }

            var checks = scanNodesUnreadable
                ? new[]
                {
                    new PreflightCheck
                    {
                        Id = "scan-nodes-readable",
                        Severity = PreflightSeverity.Error,
                        Passed = false,
                        Message = "the cluster node list is not readable (RBAC 403, test)",
                    },
                }
                : Array.Empty<PreflightCheck>();

            return Task.FromResult(new WorkloadPreflightResult
            {
                Checks = checks,
                ReadyNodeNames = scanNodesUnreadable ? null : scanReadyNodes,
            });
        }

        public Task<WorkloadLogs> GetRecentLogsAsync(WorkloadRef reference, int tailLines, bool previousContainer, CancellationToken cancellationToken = default)
            => Inner.GetRecentLogsAsync(reference, tailLines, previousContainer, cancellationToken);

        public Task<WorkloadAttachmentExistence> GetAttachmentExistenceAsync(
            IReadOnlyList<string> attachments, CancellationToken cancellationToken = default)
            => Inner.GetAttachmentExistenceAsync(attachments, cancellationToken);
    }

    private sealed class CallOrderRecordingProvider(IWorkloadProvider inner) : IWorkloadProvider
    {
        public List<string> Calls { get; } = new();

        public WorkloadProviderDescriptor Descriptor => inner.Descriptor;

        public Task<WorkloadApplyResult> ApplyAsync(WorkloadDesiredSpec spec, IProgress<WorkloadApplyProgress>? progress, CancellationToken cancellationToken = default, bool forceConflicts = false)
        {
            Calls.Add(nameof(ApplyAsync));
            return inner.ApplyAsync(spec, progress, cancellationToken, forceConflicts);
        }

        public Task TeardownAsync(WorkloadRef reference, bool retainVolumes, CancellationToken cancellationToken = default)
        {
            Calls.Add(nameof(TeardownAsync));
            return inner.TeardownAsync(reference, retainVolumes, cancellationToken);
        }

        public Task SuspendAsync(WorkloadRef reference, CancellationToken cancellationToken = default)
        {
            Calls.Add(nameof(SuspendAsync));
            return inner.SuspendAsync(reference, cancellationToken);
        }

        public Task ResumeAsync(WorkloadRef reference, CancellationToken cancellationToken = default)
        {
            Calls.Add(nameof(ResumeAsync));
            return inner.ResumeAsync(reference, cancellationToken);
        }

        public Task<WorkloadStatus> GetStatusAsync(WorkloadRef reference, CancellationToken cancellationToken = default)
        {
            Calls.Add(nameof(GetStatusAsync));
            return inner.GetStatusAsync(reference, cancellationToken);
        }

        public Task<WorkloadPreflightResult> PreflightAsync(WorkloadDesiredSpec spec, CancellationToken cancellationToken = default)
        {
            Calls.Add(nameof(PreflightAsync));
            return inner.PreflightAsync(spec, cancellationToken);
        }

        public Task<WorkloadLogs> GetRecentLogsAsync(WorkloadRef reference, int tailLines, bool previousContainer, CancellationToken cancellationToken = default)
            => inner.GetRecentLogsAsync(reference, tailLines, previousContainer, cancellationToken);

        public Task<WorkloadAttachmentExistence> GetAttachmentExistenceAsync(
            IReadOnlyList<string> attachments, CancellationToken cancellationToken = default)
            => inner.GetAttachmentExistenceAsync(attachments, cancellationToken);
    }

    private sealed class AttachmentObservationTrap(IWorkloadProvider inner) : IWorkloadProvider
    {
        public List<string> ObservedMembers { get; } = new();

        public List<string> AttachmentStateReads { get; } = new();

        public const string AttachmentExistenceMember = "GetAttachmentExistenceAsync";

        public static bool IsAttachmentStateRead(string memberName)
            => !string.Equals(memberName, AttachmentExistenceMember, StringComparison.Ordinal)
            && (memberName.Contains("Network", StringComparison.OrdinalIgnoreCase)
                || memberName.Contains("Attachment", StringComparison.OrdinalIgnoreCase)
                || memberName.Contains("NetworkStatus", StringComparison.OrdinalIgnoreCase));

        public static IReadOnlyList<string> AttachmentStateReadMembersOn(Type type)
            => type.GetMembers().Select(m => m.Name).Where(IsAttachmentStateRead).Distinct(StringComparer.Ordinal).ToList();

        private void Observe(string memberName)
        {
            ObservedMembers.Add(memberName);
            if (!IsAttachmentStateRead(memberName))
            {
                return;
            }

            AttachmentStateReads.Add(memberName);
            Assert.Fail(
                $"the reconcile pass performed an attachment-state read ('{memberName}'). Reeve does not poll "
                + "observed attachment state between applies — the SpecHash carries the desired names only.");
        }

        public Task ReadPodNetworkStatusAsync(WorkloadRef reference)
        {
            Observe(nameof(ReadPodNetworkStatusAsync));
            return Task.CompletedTask;
        }

        public Task ReadContainerNetworkSettingsNetworksAsync(WorkloadRef reference)
        {
            Observe(nameof(ReadContainerNetworkSettingsNetworksAsync));
            return Task.CompletedTask;
        }

        public Task ObserveAttachmentAsync(WorkloadRef reference, string attachmentName)
        {
            Observe(nameof(ObserveAttachmentAsync));
            return Task.CompletedTask;
        }

        public WorkloadProviderDescriptor Descriptor
        {
            get
            {
                Observe(nameof(Descriptor));
                return inner.Descriptor;
            }
        }

        public Task<WorkloadApplyResult> ApplyAsync(WorkloadDesiredSpec spec, IProgress<WorkloadApplyProgress>? progress, CancellationToken cancellationToken = default, bool forceConflicts = false)
        {
            Observe(nameof(ApplyAsync));
            return inner.ApplyAsync(spec, progress, cancellationToken, forceConflicts);
        }

        public Task TeardownAsync(WorkloadRef reference, bool retainVolumes, CancellationToken cancellationToken = default)
        {
            Observe(nameof(TeardownAsync));
            return inner.TeardownAsync(reference, retainVolumes, cancellationToken);
        }

        public Task SuspendAsync(WorkloadRef reference, CancellationToken cancellationToken = default)
        {
            Observe(nameof(SuspendAsync));
            return inner.SuspendAsync(reference, cancellationToken);
        }

        public Task ResumeAsync(WorkloadRef reference, CancellationToken cancellationToken = default)
        {
            Observe(nameof(ResumeAsync));
            return inner.ResumeAsync(reference, cancellationToken);
        }

        public Task<WorkloadStatus> GetStatusAsync(WorkloadRef reference, CancellationToken cancellationToken = default)
        {
            Observe(nameof(GetStatusAsync));
            return inner.GetStatusAsync(reference, cancellationToken);
        }

        public Task<WorkloadPreflightResult> PreflightAsync(WorkloadDesiredSpec spec, CancellationToken cancellationToken = default)
        {
            Observe(nameof(PreflightAsync));
            return inner.PreflightAsync(spec, cancellationToken);
        }

        public Task<WorkloadLogs> GetRecentLogsAsync(WorkloadRef reference, int tailLines, bool previousContainer, CancellationToken cancellationToken = default)
        {
            Observe(nameof(GetRecentLogsAsync));
            return inner.GetRecentLogsAsync(reference, tailLines, previousContainer, cancellationToken);
        }

        public Task<WorkloadAttachmentExistence> GetAttachmentExistenceAsync(
            IReadOnlyList<string> attachments, CancellationToken cancellationToken = default)
        {
            Observe(nameof(GetAttachmentExistenceAsync));
            return inner.GetAttachmentExistenceAsync(attachments, cancellationToken);
        }
    }

    private sealed class RecordingRepository : IWorkloadInstanceRepository
    {
        public int SavedCount { get; private set; }

        public List<(int? Percent, string? Message)> ProgressSnapshots { get; } = new();

        public Task<WorkloadInstance?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) => Task.FromResult<WorkloadInstance?>(null);
        public Task<WorkloadInstance?> GetByWorkloadAsync(Guid pluginInstanceId, string workloadId, CancellationToken cancellationToken = default) => Task.FromResult<WorkloadInstance?>(null);
        public Task<IReadOnlyList<WorkloadInstance>> ListByPluginInstanceAsync(Guid pluginInstanceId, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<WorkloadInstance>>(Array.Empty<WorkloadInstance>());
        public Task<IReadOnlyList<WorkloadInstance>> ListByStateAsync(WorkloadInstanceState state, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<WorkloadInstance>>(Array.Empty<WorkloadInstance>());
        public Task<IReadOnlyList<WorkloadInstance>> ListAllAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<WorkloadInstance>>(Array.Empty<WorkloadInstance>());
        public Task<IReadOnlyDictionary<WorkloadInstanceState, int>> CountByStateAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyDictionary<WorkloadInstanceState, int>>(new Dictionary<WorkloadInstanceState, int>());
        public Task AddAsync(WorkloadInstance instance, CancellationToken cancellationToken = default) { SavedCount++; return Task.CompletedTask; }
        public Task UpdateAsync(WorkloadInstance instance, CancellationToken cancellationToken = default) { SavedCount++; ProgressSnapshots.Add((instance.ProvisioningPercent, instance.StateMessage)); return Task.CompletedTask; }
        public Task<bool> TrySetRestoreBindingUnresolvedAsync(Guid workloadInstanceId, DateTimeOffset nowUtc, CancellationToken cancellationToken = default) => Task.FromResult(false);
        public Task DeleteAsync(WorkloadInstance instance, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class FixedClock : IWorkloadClock
    {
        public DateTimeOffset UtcNow { get; } = new(2026, 7, 6, 12, 0, 0, TimeSpan.Zero);
    }
}
