using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Reeve.Workloads.Abstractions.Contracts;
using Reeve.Workloads.Abstractions.Contracts.Deployment;
using Reeve.Workloads.Abstractions.Domain.DesiredSpec;
using Reeve.Workloads.Abstractions.Providers;
using Reeve.Workloads.Tests.Shared;

namespace Reeve.Workloads.Testing.Tests;

public sealed class InMemoryWorkloadProviderTests
{
    private static readonly Guid InstanceId = Guid.Parse("11111111-2222-3333-4444-555555555555");

    private static WorkloadDesiredSpec Spec(
        string specHash = "abc123def456",
        IReadOnlyList<ResolvedVolume>? volumes = null,
        string image = "ghcr.io/reeve/matter@sha256:1111111111111111111111111111111111111111111111111111111111111111")
        => new()
        {
            GroupInstanceId = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee"),
            GroupId = "com.reeve.matter",
            WorkloadId = "matter",
            InstanceId = InstanceId,
            Inst8 = "11111111",
            Provider = StandInProviders.KubernetesId,
            ResourceName = "reeve-wl-com-reeve-matter-matter-11111111",
            SecretName = "reeve-wl-com-reeve-matter-matter-11111111",
            Labels = new Dictionary<string, string> { ["reeve.io/workload-instance"] = InstanceId.ToString() },
            Image = image,
            Kind = WorkloadKind.SingleNode,
            Health = new WorkloadHealth { Mode = new WorkloadHealthMode("adapter-session"), HeartbeatSeconds = 30 },
            Volumes = volumes ?? [],
            SpecHash = specHash,
        };

    private static ResolvedVolume Volume(
        string id = "data",
        string resourceName = "reeve-wl-data-11111111",
        bool existingClaim = false,
        VolumeCriticality criticality = VolumeCriticality.Critical)
        => new()
        {
            Id = id,
            MountPath = "/var/lib/matter",
            Size = "64Mi",
            Criticality = criticality,
            ResourceName = resourceName,
            ExistingClaim = existingClaim,
        };

    private static WorkloadRef Ref() => new()
    {
        InstanceId = InstanceId,
        GroupId = "com.reeve.matter",
        WorkloadId = "matter",
        Inst8 = "11111111",
        Provider = StandInProviders.KubernetesId,
    };

    [Fact]
    public void The_fake_stands_in_for_exactly_the_descriptor_it_is_built_with()
    {
        new InMemoryWorkloadProvider(StandInProviders.Manual).Descriptor.Should().BeSameAs(StandInProviders.Manual);
        FluentActions.Invoking(() => new InMemoryWorkloadProvider(null!)).Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public async Task A_fresh_apply_is_running_and_never_provider_ready()
    {
        var provider = new InMemoryWorkloadProvider(StandInProviders.Kubernetes);

        var result = await provider.ApplyAsync(Spec(), progress: null);
        var status = await provider.GetStatusAsync(Ref());

        result.Succeeded.Should().BeTrue();
        result.AppliedSpecHash.Should().Be("abc123def456");
        status.Phase.Should().Be(WorkloadProviderPhase.Running);
        status.ProviderReady.Should().BeFalse();
    }

    [Fact]
    public async Task The_fanout_hook_creates_ready_but_never_run_terminal()
    {
        var provider = new InMemoryWorkloadProvider(StandInProviders.Kubernetes) { ScanFreshlyCreatedReadsReadyNotTerminal = true };

        await provider.ApplyAsync(Spec(), progress: null);
        var status = await provider.GetStatusAsync(Ref());

        status.Phase.Should().Be(WorkloadProviderPhase.Ready);
        status.ProviderReady.Should().BeTrue();
        status.ScanRunTerminal.Should().BeFalse();
    }

    [Fact]
    public async Task A_re_apply_converges_on_the_last_spec_and_still_counts()
    {
        var provider = new InMemoryWorkloadProvider(StandInProviders.Kubernetes);

        await provider.ApplyAsync(Spec(specHash: "first"), progress: null);
        await provider.ApplyAsync(Spec(specHash: "second"), progress: null);

        provider.ApplyCountFor(InstanceId).Should().Be(2);
        provider.LastAppliedSpec(InstanceId)!.SpecHash.Should().Be("second");
    }

    [Fact]
    public async Task A_re_apply_lifts_a_suspension()
    {
        var provider = new InMemoryWorkloadProvider(StandInProviders.Kubernetes);
        await provider.ApplyAsync(Spec(), progress: null);
        await provider.SuspendAsync(Ref());

        await provider.ApplyAsync(Spec(), progress: null);

        provider.IsSuspended(InstanceId).Should().BeFalse();
    }

    [Fact]
    public async Task A_fresh_apply_clears_a_retained_volume_marker()
    {
        var provider = new InMemoryWorkloadProvider(StandInProviders.Kubernetes);
        await provider.ApplyAsync(Spec(volumes: [Volume()]), progress: null);
        await provider.TeardownAsync(Ref(), retainVolumes: true);
        provider.RetainedVolumes.Should().ContainKey(InstanceId);

        await provider.ApplyAsync(Spec(volumes: [Volume()]), progress: null);

        provider.RetainedVolumes.Should().NotContainKey(InstanceId);
    }

    [Fact]
    public async Task Applying_a_null_spec_throws()
    {
        var provider = new InMemoryWorkloadProvider(StandInProviders.Kubernetes);

        await FluentActions.Awaiting(() => provider.ApplyAsync(null!, progress: null))
            .Should().ThrowAsync<ArgumentNullException>();
    }

    [Fact]
    public async Task An_adopted_claim_that_is_absent_is_rejected_not_silently_created()
    {
        var provider = new InMemoryWorkloadProvider(StandInProviders.Kubernetes) { FailAbsentAdoptedClaims = true };

        var result = await provider.ApplyAsync(
            Spec(volumes: [Volume(resourceName: "retained-pvc", existingClaim: true)]), progress: null);

        result.Succeeded.Should().BeFalse();
        result.Reason.Should().Be(WorkloadApplyReason.Rejected);
        result.Message.Should().Contain("retained-pvc");
        provider.IsTracked(InstanceId).Should().BeFalse("a refused apply must leave nothing behind");
    }

    [Fact]
    public async Task An_adopted_claim_that_is_present_applies()
    {
        var provider = new InMemoryWorkloadProvider(StandInProviders.Kubernetes) { FailAbsentAdoptedClaims = true };
        provider.PresentAdoptedClaimNames.Add("retained-pvc");

        var result = await provider.ApplyAsync(
            Spec(volumes: [Volume(resourceName: "retained-pvc", existingClaim: true)]), progress: null);

        result.Succeeded.Should().BeTrue();
    }

    [Fact]
    public async Task A_volume_that_adopts_nothing_is_unaffected_by_the_fail_closed_flag()
    {
        var provider = new InMemoryWorkloadProvider(StandInProviders.Kubernetes) { FailAbsentAdoptedClaims = true };

        var result = await provider.ApplyAsync(
            Spec(volumes: [Volume(resourceName: "fresh-pvc", existingClaim: false)]), progress: null);

        result.Succeeded.Should().BeTrue();
    }

    [Fact]
    public async Task The_fail_closed_arm_is_opt_in_and_absent_by_default()
    {
        var provider = new InMemoryWorkloadProvider(StandInProviders.Kubernetes);

        var result = await provider.ApplyAsync(
            Spec(volumes: [Volume(resourceName: "retained-pvc", existingClaim: true)]), progress: null);

        result.Succeeded.Should().BeTrue();
    }

    [Fact]
    public async Task Teardown_removes_the_instance_and_status_reads_not_found()
    {
        var provider = new InMemoryWorkloadProvider(StandInProviders.Kubernetes);
        await provider.ApplyAsync(Spec(), progress: null);

        await provider.TeardownAsync(Ref(), retainVolumes: false);
        var status = await provider.GetStatusAsync(Ref());

        provider.IsTracked(InstanceId).Should().BeFalse();
        status.Phase.Should().Be(WorkloadProviderPhase.NotFound);
        status.ProviderReady.Should().BeFalse();
    }

    [Fact]
    public async Task A_retaining_teardown_leaves_a_marker_carrying_the_physical_name()
    {
        var provider = new InMemoryWorkloadProvider(StandInProviders.Kubernetes);
        await provider.ApplyAsync(Spec(volumes: [Volume(id: "data", resourceName: "reeve-wl-data-11111111")]), progress: null);

        await provider.TeardownAsync(Ref(), retainVolumes: true);

        var retained = provider.RetainedVolumes[InstanceId].Should().ContainSingle().Subject;
        retained.VolumeId.Should().Be("data");
        retained.ResourceName.Should().Be("reeve-wl-data-11111111");
        retained.Size.Should().Be("64Mi");
        retained.Criticality.Should().Be(VolumeCriticality.Critical);
    }

    [Fact]
    public async Task A_non_retaining_teardown_leaves_no_marker()
    {
        var provider = new InMemoryWorkloadProvider(StandInProviders.Kubernetes);
        await provider.ApplyAsync(Spec(volumes: [Volume()]), progress: null);

        await provider.TeardownAsync(Ref(), retainVolumes: false);

        provider.RetainedVolumes.Should().BeEmpty();
    }

    [Fact]
    public async Task A_retaining_teardown_of_a_volumeless_workload_leaves_no_marker()
    {
        var provider = new InMemoryWorkloadProvider(StandInProviders.Kubernetes);
        await provider.ApplyAsync(Spec(), progress: null);

        await provider.TeardownAsync(Ref(), retainVolumes: true);

        provider.RetainedVolumes.Should().BeEmpty();
    }

    [Fact]
    public async Task Tearing_down_an_untracked_instance_is_a_clean_no_op()
    {
        var provider = new InMemoryWorkloadProvider(StandInProviders.Kubernetes);

        await FluentActions.Awaiting(() => provider.TeardownAsync(Ref(), retainVolumes: true))
            .Should().NotThrowAsync();
        provider.RetainedVolumes.Should().BeEmpty();
    }

    [Fact]
    public async Task A_suspended_instance_stays_tracked_but_never_reads_ready()
    {
        var provider = new InMemoryWorkloadProvider(StandInProviders.Kubernetes);
        await provider.ApplyAsync(Spec(), progress: null);
        provider.SimulateReady(InstanceId);

        await provider.SuspendAsync(Ref());
        var status = await provider.GetStatusAsync(Ref());

        provider.IsTracked(InstanceId).Should().BeTrue();
        status.Phase.Should().Be(WorkloadProviderPhase.Pending);
        status.ProviderReady.Should().BeFalse();
        status.Phase.Should().NotBe(WorkloadProviderPhase.NotFound, "a pause is not a teardown");
    }

    [Fact]
    public async Task Resume_restores_the_status_that_was_driven_before_the_pause()
    {
        var provider = new InMemoryWorkloadProvider(StandInProviders.Kubernetes);
        await provider.ApplyAsync(Spec(), progress: null);
        provider.SimulateReady(InstanceId);
        await provider.SuspendAsync(Ref());

        await provider.ResumeAsync(Ref());
        var status = await provider.GetStatusAsync(Ref());

        provider.IsSuspended(InstanceId).Should().BeFalse();
        status.Phase.Should().Be(WorkloadProviderPhase.Ready);
        status.ProviderReady.Should().BeTrue();
    }

    [Fact]
    public async Task Suspend_and_resume_are_idempotent_and_no_ops_when_untracked()
    {
        var provider = new InMemoryWorkloadProvider(StandInProviders.Kubernetes);

        await provider.SuspendAsync(Ref());
        await provider.SuspendAsync(Ref());
        await provider.ResumeAsync(Ref());

        provider.IsSuspended(InstanceId).Should().BeFalse();
        provider.IsTracked(InstanceId).Should().BeFalse("a suspend must not conjure an instance");
    }

    [Fact]
    public async Task An_instance_that_was_never_applied_reads_not_found()
    {
        var status = await new InMemoryWorkloadProvider(StandInProviders.Kubernetes).GetStatusAsync(Ref());

        status.Phase.Should().Be(WorkloadProviderPhase.NotFound);
        status.ProviderReady.Should().BeFalse();
    }

    [Fact]
    public async Task A_driven_status_is_what_a_subsequent_read_returns()
    {
        var provider = new InMemoryWorkloadProvider(StandInProviders.Kubernetes);
        await provider.ApplyAsync(Spec(), progress: null);

        provider.SetStatus(
            InstanceId, WorkloadProviderPhase.Failed, providerReady: false,
            restartCount: 7, message: "CrashLoopBackOff",
            scanRunTerminal: true, scanFailedNodeNames: ["node-a"]);
        var status = await provider.GetStatusAsync(Ref());

        status.Phase.Should().Be(WorkloadProviderPhase.Failed);
        status.RestartCount.Should().Be(7);
        status.Message.Should().Be("CrashLoopBackOff");
        status.ScanRunTerminal.Should().BeTrue();
        status.ScanFailedNodeNames.Should().Equal("node-a");
    }

    [Fact]
    public async Task A_negative_restart_count_is_clamped_to_zero()
    {
        var provider = new InMemoryWorkloadProvider(StandInProviders.Kubernetes);
        await provider.ApplyAsync(Spec(), progress: null);

        provider.SetStatus(InstanceId, WorkloadProviderPhase.Running, providerReady: false, restartCount: -3);

        (await provider.GetStatusAsync(Ref())).RestartCount.Should().Be(0);
    }

    [Fact]
    public async Task The_observed_image_falls_back_to_the_applied_spec()
    {
        var provider = new InMemoryWorkloadProvider(StandInProviders.Kubernetes);
        await provider.ApplyAsync(Spec(image: "ghcr.io/reeve/matter@sha256:abc"), progress: null);

        (await provider.GetStatusAsync(Ref())).ObservedImage.Should().Be("ghcr.io/reeve/matter@sha256:abc");
    }

    [Fact]
    public async Task Driving_the_status_of_an_untracked_instance_is_a_no_op()
    {
        var provider = new InMemoryWorkloadProvider(StandInProviders.Kubernetes);

        provider.SetStatus(InstanceId, WorkloadProviderPhase.Ready, providerReady: true);
        provider.SimulateReady(InstanceId);

        (await provider.GetStatusAsync(Ref())).Phase.Should().Be(WorkloadProviderPhase.NotFound);
    }

    [Fact]
    public async Task Recent_events_are_handed_out_as_a_copy()
    {
        var provider = new InMemoryWorkloadProvider(StandInProviders.Kubernetes);
        await provider.ApplyAsync(Spec(), progress: null);
        provider.AppendEvent(InstanceId, new WorkloadProviderEvent
        {
            AtUtc = DateTimeOffset.UnixEpoch, Reason = "Pulled", Message = "image pulled",
        });

        var first = await provider.GetStatusAsync(Ref());
        ((List<WorkloadProviderEvent>)first.RecentEvents).Clear();

        (await provider.GetStatusAsync(Ref())).RecentEvents.Should().ContainSingle();
    }

    [Fact]
    public async Task Preflight_is_all_clear_until_a_failure_is_injected()
    {
        var provider = new InMemoryWorkloadProvider(StandInProviders.Kubernetes);

        var clean = await provider.PreflightAsync(Spec());
        provider.InjectPreflightFailure(PreflightCheck.Fail("arch-affinity", PreflightSeverity.Error, "no arm64 node"));
        var dirty = await provider.PreflightAsync(Spec());

        clean.Checks.Should().BeEmpty();
        clean.HasBlockingError.Should().BeFalse();
        dirty.HasBlockingError.Should().BeTrue();
        dirty.Checks.Should().ContainSingle().Which.Id.Should().Be("arch-affinity");
    }

    [Fact]
    public async Task Clearing_the_injected_failures_returns_preflight_to_all_clear()
    {
        var provider = new InMemoryWorkloadProvider(StandInProviders.Kubernetes);
        provider.InjectPreflightFailure(PreflightCheck.Fail("arch-affinity", PreflightSeverity.Error, "no arm64 node"));

        provider.ClearPreflightFailures();

        (await provider.PreflightAsync(Spec())).HasBlockingError.Should().BeFalse();
    }

    [Fact]
    public async Task Injected_failures_accumulate_rather_than_replacing_each_other()
    {
        var provider = new InMemoryWorkloadProvider(StandInProviders.Kubernetes);

        provider.InjectPreflightFailure(PreflightCheck.Fail("arch-affinity", PreflightSeverity.Error, "no arm64 node"));
        provider.InjectPreflightFailure(PreflightCheck.Fail("psa-baseline", PreflightSeverity.Warning, "privileged"));

        var result = await provider.PreflightAsync(Spec());

        result.Checks.Select(c => c.Id).Should().Equal("arch-affinity", "psa-baseline");
    }

    [Fact]
    public async Task An_injected_failure_answers_every_subsequent_preflight()
    {
        var provider = new InMemoryWorkloadProvider(StandInProviders.Kubernetes);
        provider.InjectPreflightFailure(PreflightCheck.Fail("arch-affinity", PreflightSeverity.Error, "no arm64 node"));

        await provider.PreflightAsync(Spec());

        (await provider.PreflightAsync(Spec())).HasBlockingError.Should().BeTrue();
    }

    [Fact]
    public async Task Logs_for_an_untracked_instance_are_empty()
    {
        var logs = await new InMemoryWorkloadProvider(StandInProviders.Kubernetes).GetRecentLogsAsync(Ref(), tailLines: 100, previousContainer: false);

        logs.Lines.Should().BeEmpty();
        logs.Truncated.Should().BeFalse();
    }

    [Fact]
    public async Task Previous_container_output_leads_with_a_restart_marker()
    {
        var provider = new InMemoryWorkloadProvider(StandInProviders.Kubernetes);
        await provider.ApplyAsync(Spec(), progress: null);
        provider.SetLogs(InstanceId, [WorkloadLogLine.Line("now")], [WorkloadLogLine.Line("before")]);

        var logs = await provider.GetRecentLogsAsync(Ref(), tailLines: 0, previousContainer: true);

        logs.Lines[0].IsRestartMarker.Should().BeTrue();
        logs.Lines[1].Text.Should().Be("before");
    }

    [Fact]
    public async Task No_previous_output_means_no_restart_marker()
    {
        var provider = new InMemoryWorkloadProvider(StandInProviders.Kubernetes);
        await provider.ApplyAsync(Spec(), progress: null);
        provider.SetLogs(InstanceId, [WorkloadLogLine.Line("now")]);

        var logs = await provider.GetRecentLogsAsync(Ref(), tailLines: 0, previousContainer: true);

        logs.Lines.Should().BeEmpty();
    }

    [Fact]
    public async Task A_tail_keeps_the_newest_lines_and_reports_the_truncation()
    {
        var provider = new InMemoryWorkloadProvider(StandInProviders.Kubernetes);
        await provider.ApplyAsync(Spec(), progress: null);
        provider.SetLogs(InstanceId, [
            WorkloadLogLine.Line("one"), WorkloadLogLine.Line("two"), WorkloadLogLine.Line("three"),
        ]);

        var logs = await provider.GetRecentLogsAsync(Ref(), tailLines: 2, previousContainer: false);

        logs.Truncated.Should().BeTrue();
        logs.Lines.Select(l => l.Text).Should().Equal("two", "three");
    }

    [Fact]
    public async Task A_non_positive_tail_returns_everything_untruncated()
    {
        var provider = new InMemoryWorkloadProvider(StandInProviders.Kubernetes);
        await provider.ApplyAsync(Spec(), progress: null);
        provider.SetLogs(InstanceId, [WorkloadLogLine.Line("one"), WorkloadLogLine.Line("two")]);

        var logs = await provider.GetRecentLogsAsync(Ref(), tailLines: 0, previousContainer: false);

        logs.Lines.Should().HaveCount(2);
        logs.Truncated.Should().BeFalse();
    }

    [Fact]
    public async Task A_missing_attachment_is_reported_missing_by_the_existence_read_and_restored_with_it()
    {
        var provider = new InMemoryWorkloadProvider(StandInProviders.Kubernetes);
        provider.SetNetworkAttachmentMissing("iot0");

        var existence = await provider.GetAttachmentExistenceAsync(["lan0", "iot0"]);

        existence.Status.Should().Be(WorkloadAttachmentExistenceStatus.Read);
        existence.MissingAttachments.Should().Equal("iot0");

        provider.SetNetworkAttachmentMissing("iot0", missing: false);
        (await provider.GetAttachmentExistenceAsync(["lan0", "iot0"])).MissingAttachments.Should().BeEmpty();
    }

    [Fact]
    public async Task Every_existence_read_is_counted_and_the_override_replaces_the_answer()
    {
        var provider = new InMemoryWorkloadProvider(StandInProviders.Kubernetes);
        provider.AttachmentExistenceCallCount.Should().Be(0);

        await provider.GetAttachmentExistenceAsync(["lan0"]);
        provider.AttachmentExistenceOverride = _ => WorkloadAttachmentExistence.Unreadable("api down");
        var overridden = await provider.GetAttachmentExistenceAsync(["lan0"]);

        provider.AttachmentExistenceCallCount.Should().Be(2);
        overridden.Status.Should().Be(WorkloadAttachmentExistenceStatus.Unreadable);
        overridden.Detail.Should().Be("api down");
        await FluentActions.Awaiting(() => provider.GetAttachmentExistenceAsync(null!))
            .Should().ThrowAsync<ArgumentNullException>();
    }

    [Fact]
    public async Task Concurrent_applies_neither_tear_the_store_nor_lose_a_count()
    {
        var provider = new InMemoryWorkloadProvider(StandInProviders.Kubernetes);

        await Task.WhenAll(Enumerable.Range(0, 64)
            .Select(i => Task.Run(() => provider.ApplyAsync(Spec(specHash: $"hash-{i}"), progress: null))));

        provider.ApplyCountFor(InstanceId).Should().Be(64);
        provider.IsTracked(InstanceId).Should().BeTrue();
    }
}
