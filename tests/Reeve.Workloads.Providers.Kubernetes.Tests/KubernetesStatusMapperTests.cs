using System;
using FluentAssertions;
using Reeve.Workloads.Abstractions.Providers;
using Reeve.Workloads.Providers.Kubernetes;
using Xunit;

namespace Reeve.Workloads.Providers.Kubernetes.Tests;

public sealed class KubernetesStatusMapperTests
{
    [Fact]
    public void Null_raw_maps_to_Unknown_not_ready()
    {
        var status = KubernetesStatusMapper.Map(null);

        status.Phase.Should().Be(WorkloadProviderPhase.Unknown);
        status.ProviderReady.Should().BeFalse();
    }

    [Fact]
    public void Not_found_raw_maps_to_NotFound()
    {
        var status = KubernetesStatusMapper.Map(KubernetesWorkloadRaw.NotFound());

        status.Phase.Should().Be(WorkloadProviderPhase.NotFound);
        status.ProviderReady.Should().BeFalse();
    }

    [Fact]
    public void Ready_when_ready_meets_desired_and_all_pods_running()
    {
        var status = KubernetesStatusMapper.Map(new KubernetesWorkloadRaw
        {
            Found = true, Kind = "Deployment", DesiredReplicas = 1, ReadyReplicas = 1,
            AllPodsRunning = true, HasPods = true, ObservedImage = "img@sha256:abc", PodRestartCount = 3,
        });

        status.Phase.Should().Be(WorkloadProviderPhase.Ready);
        status.ProviderReady.Should().BeTrue();
        status.RestartCount.Should().Be(3);
        status.ObservedImage.Should().Be("img@sha256:abc");
    }

    [Fact]
    public void Failed_takes_precedence_over_ready_counts()
    {
        var status = KubernetesStatusMapper.Map(new KubernetesWorkloadRaw
        {
            Found = true, Kind = "Deployment", DesiredReplicas = 1, ReadyReplicas = 1,
            AllPodsRunning = false, HasPods = true, AnyPodFailed = true, PodRestartCount = 9,
        });

        status.Phase.Should().Be(WorkloadProviderPhase.Failed);
        status.ProviderReady.Should().BeFalse();
        status.RestartCount.Should().Be(9);
    }

    [Fact]
    public void Running_when_pods_scheduled_but_not_all_ready()
    {
        var status = KubernetesStatusMapper.Map(new KubernetesWorkloadRaw
        {
            Found = true, Kind = "Deployment", DesiredReplicas = 1, ReadyReplicas = 0,
            AllPodsRunning = false, HasPods = true,
        });

        status.Phase.Should().Be(WorkloadProviderPhase.Running);
        status.ProviderReady.Should().BeFalse();
    }

    [Fact]
    public void Pending_when_object_exists_but_no_pods_scheduled()
    {
        var status = KubernetesStatusMapper.Map(new KubernetesWorkloadRaw
        {
            Found = true, Kind = "Deployment", DesiredReplicas = 1, ReadyReplicas = 0, HasPods = false,
        });

        status.Phase.Should().Be(WorkloadProviderPhase.Pending);
        status.ProviderReady.Should().BeFalse();
    }

    [Fact]
    public void Pending_scaled_to_zero_reports_scaled_message()
    {
        var status = KubernetesStatusMapper.Map(new KubernetesWorkloadRaw
        {
            Found = true, Kind = "Deployment", DesiredReplicas = 0, ReadyReplicas = 0, HasPods = false,
        });

        status.Phase.Should().Be(WorkloadProviderPhase.Pending);
        status.Message.Should().Contain("Scaled to 0");
    }

    [Fact]
    public void Ready_requires_desired_at_least_one_so_scaled_to_zero_is_never_ready()
    {
        var status = KubernetesStatusMapper.Map(new KubernetesWorkloadRaw
        {
            Found = true, Kind = "Deployment", DesiredReplicas = 0, ReadyReplicas = 0,
            AllPodsRunning = true, HasPods = false,
        });

        status.Phase.Should().NotBe(WorkloadProviderPhase.Ready);
    }

    [Fact]
    public void Events_are_ordered_newest_last_and_capped_at_twenty()
    {
        var events = new (DateTimeOffset, string, string, bool)[25];
        var baseTime = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        for (var i = 0; i < 25; i++)
        {
            events[i] = (baseTime.AddMinutes(i), $"Reason{i}", $"Message{i}", i % 2 == 0);
        }

        var status = KubernetesStatusMapper.Map(new KubernetesWorkloadRaw
        {
            Found = true, Kind = "Deployment", DesiredReplicas = 1, ReadyReplicas = 1,
            AllPodsRunning = true, HasPods = true, Events = events,
        });

        status.RecentEvents.Should().HaveCount(20);
        status.RecentEvents[^1].Reason.Should().Be("Reason24");
        status.RecentEvents[0].Reason.Should().Be("Reason5");
    }

    [Fact]
    public void Pods_unreadable_but_ready_meets_desired_maps_to_Ready_not_false_Pending()
    {
        var status = KubernetesStatusMapper.Map(new KubernetesWorkloadRaw
        {
            Found = true, Kind = "Deployment", DesiredReplicas = 1, ReadyReplicas = 1,
            HasPods = false, PodsRead = false,
        });

        status.Phase.Should().NotBe(WorkloadProviderPhase.Pending,
            "a pods-read blip on a healthy 1/1 Deployment must not fabricate a Pending 'no pods scheduled'");
        status.Phase.Should().Be(WorkloadProviderPhase.Ready);
    }

    [Fact]
    public void Pods_unreadable_and_not_ready_maps_to_Unknown_not_false_Pending()
    {
        var status = KubernetesStatusMapper.Map(new KubernetesWorkloadRaw
        {
            Found = true, Kind = "Deployment", DesiredReplicas = 1, ReadyReplicas = 0,
            HasPods = false, PodsRead = false,
        });

        status.Phase.Should().NotBe(WorkloadProviderPhase.Pending);
        status.Phase.Should().Be(WorkloadProviderPhase.Unknown,
            "when pods are unreadable and readiness is not proven by readyReplicas, the phase is Unknown, not a fabricated Pending");
    }

    [Fact]
    public void Pods_readable_but_zero_still_maps_to_Pending()
    {
        var status = KubernetesStatusMapper.Map(new KubernetesWorkloadRaw
        {
            Found = true, Kind = "Deployment", DesiredReplicas = 1, ReadyReplicas = 0,
            HasPods = false, PodsRead = true,
        });

        status.Phase.Should().Be(WorkloadProviderPhase.Pending,
            "a reachable pods read with zero pods is a genuine 'no pods scheduled yet' Pending");
    }

    [Fact]
    public void Warning_events_map_to_warning_severity()
    {
        var status = KubernetesStatusMapper.Map(new KubernetesWorkloadRaw
        {
            Found = true, Kind = "Deployment", DesiredReplicas = 1, ReadyReplicas = 1,
            AllPodsRunning = true, HasPods = true,
            Events = new (DateTimeOffset, string, string, bool)[]
            {
                (DateTimeOffset.UtcNow, "BackOff", "Back-off restarting failed container", true),
            },
        });

        status.RecentEvents.Should().ContainSingle();
        status.RecentEvents[0].Severity.Should().Be(WorkloadEventSeverity.Warning);
    }

    [Fact]
    public void Scan_idle_with_no_active_jobs_maps_to_armed_Ready_provider_ready()
    {
        var status = KubernetesStatusMapper.Map(new KubernetesWorkloadRaw
        {
            Found = false,
            Jobs = new[]
            {
                new KubernetesJobRaw { Name = "reeve-wl-abcd-nodeA-run1", Succeeded = 0, Failed = 0, Active = 0 },
            },
        });

        status.Phase.Should().Be(WorkloadProviderPhase.Ready);
        status.ProviderReady.Should().BeTrue();
        status.Phase.Should().NotBe(WorkloadProviderPhase.NotFound);
        status.Phase.Should().NotBe(WorkloadProviderPhase.Unknown);
        status.Phase.Should().NotBe(WorkloadProviderPhase.Failed);
    }

    [Fact]
    public void Scan_active_job_maps_to_Running_not_ready_with_scanning_message()
    {
        var status = KubernetesStatusMapper.Map(new KubernetesWorkloadRaw
        {
            Found = false,
            Jobs = new[]
            {
                new KubernetesJobRaw { Name = "reeve-wl-abcd-nodeA-run1", Succeeded = 1, Failed = 0, Active = 0 },
                new KubernetesJobRaw { Name = "reeve-wl-abcd-nodeB-run1", Succeeded = 0, Failed = 0, Active = 1 },
            },
        });

        status.Phase.Should().Be(WorkloadProviderPhase.Running);
        status.ProviderReady.Should().BeFalse();
        status.Message.Should().Contain("Scanning");
    }

    [Fact]
    public void Scan_all_jobs_terminal_succeeded_maps_back_to_armed_Ready()
    {
        var completedAt = new DateTimeOffset(2026, 7, 19, 12, 0, 0, TimeSpan.Zero);
        var status = KubernetesStatusMapper.Map(new KubernetesWorkloadRaw
        {
            Found = false,
            Jobs = new[]
            {
                new KubernetesJobRaw { Name = "reeve-wl-abcd-nodeA-run1", Succeeded = 1, Failed = 0, Active = 0, CompletionTime = completedAt },
                new KubernetesJobRaw { Name = "reeve-wl-abcd-nodeB-run1", Succeeded = 1, Failed = 0, Active = 0, CompletionTime = completedAt },
            },
        });

        status.Phase.Should().Be(WorkloadProviderPhase.Ready);
        status.ProviderReady.Should().BeTrue();
    }

    [Fact]
    public void Scan_per_node_failure_does_not_stick_the_instance_in_Failed()
    {
        var status = KubernetesStatusMapper.Map(new KubernetesWorkloadRaw
        {
            Found = false,
            Jobs = new[]
            {
                new KubernetesJobRaw { Name = "reeve-wl-abcd-nodeA-run1", Succeeded = 1, Failed = 0, Active = 0 },
                new KubernetesJobRaw { Name = "reeve-wl-abcd-nodeB-run1", Succeeded = 0, Failed = 1, Active = 0 },
            },
        });

        status.Phase.Should().NotBe(WorkloadProviderPhase.Failed);
        status.Phase.Should().NotBe(WorkloadProviderPhase.NotFound);
        status.Phase.Should().Be(WorkloadProviderPhase.Ready);
        status.ProviderReady.Should().BeTrue();
    }

    [Fact]
    public void Scan_all_nodes_failed_still_does_not_map_to_sticky_Failed()
    {
        var status = KubernetesStatusMapper.Map(new KubernetesWorkloadRaw
        {
            Found = false,
            Jobs = new[]
            {
                new KubernetesJobRaw { Name = "reeve-wl-abcd-nodeA-run1", Succeeded = 0, Failed = 1, Active = 0 },
            },
        });

        status.Phase.Should().NotBe(WorkloadProviderPhase.Failed);
        status.Phase.Should().NotBe(WorkloadProviderPhase.NotFound);
        status.ProviderReady.Should().BeTrue();
    }

    [Fact]
    public void Deployment_read_with_empty_jobs_is_untouched_by_the_scan_branch()
    {
        var status = KubernetesStatusMapper.Map(new KubernetesWorkloadRaw
        {
            Found = true, Kind = "Deployment", DesiredReplicas = 1, ReadyReplicas = 1,
            AllPodsRunning = true, HasPods = true,
        });

        status.Phase.Should().Be(WorkloadProviderPhase.Ready);
        status.ProviderReady.Should().BeTrue();
    }

    [Fact]
    public void Scan_freshly_created_jobs_read_Ready_but_are_NOT_run_terminal()
    {
        var status = KubernetesStatusMapper.Map(new KubernetesWorkloadRaw
        {
            Found = false,
            Jobs = new[]
            {
                new KubernetesJobRaw { Name = "reeve-wl-abcd-nodeA-run1", Succeeded = 0, Failed = 0, Active = 0 },
                new KubernetesJobRaw { Name = "reeve-wl-abcd-nodeB-run1", Succeeded = 0, Failed = 0, Active = 0 },
            },
        });

        status.Phase.Should().Be(WorkloadProviderPhase.Ready);
        status.ScanRunTerminal.Should().BeFalse(
            "a freshly-created Job set has no completion evidence — it is armed, not run-terminal");
    }

    [Fact]
    public void Scan_all_jobs_terminal_are_run_terminal()
    {
        var status = KubernetesStatusMapper.Map(new KubernetesWorkloadRaw
        {
            Found = false,
            Jobs = new[]
            {
                new KubernetesJobRaw { Name = "reeve-wl-abcd-nodeA-run1", Succeeded = 1, Failed = 0, Active = 0 },
                new KubernetesJobRaw { Name = "reeve-wl-abcd-nodeB-run1", Succeeded = 0, Failed = 1, Active = 0 },
            },
        });

        status.Phase.Should().Be(WorkloadProviderPhase.Ready);
        status.ScanRunTerminal.Should().BeTrue("every Job in the set completed (succeeded or failed) → run-terminal");
    }

    [Fact]
    public void Scan_terminal_set_names_the_node_whose_job_failed()
    {
        var status = KubernetesStatusMapper.Map(new KubernetesWorkloadRaw
        {
            Found = false,
            Jobs = new[]
            {
                new KubernetesJobRaw { Name = "reeve-wl-abcd-nodeA-run1", NodeName = "node-a", Succeeded = 1, Failed = 0, Active = 0 },
                new KubernetesJobRaw { Name = "reeve-wl-abcd-nodeB-run1", NodeName = "node-b", Succeeded = 0, Failed = 1, Active = 0 },
            },
        });

        status.ScanRunTerminal.Should().BeTrue("every Job in the set finished — the run is over either way");
        status.ScanFailedNodeNames.Should().BeEquivalentTo(new[] { "node-b" },
            "the failed node is named, and the succeeded one is not");
    }

    [Fact]
    public void Scan_terminal_set_with_every_job_succeeded_names_no_failed_node()
    {
        var status = KubernetesStatusMapper.Map(new KubernetesWorkloadRaw
        {
            Found = false,
            Jobs = new[]
            {
                new KubernetesJobRaw { Name = "reeve-wl-abcd-nodeA-run1", NodeName = "node-a", Succeeded = 1 },
                new KubernetesJobRaw { Name = "reeve-wl-abcd-nodeB-run1", NodeName = "node-b", Succeeded = 1 },
            },
        });

        status.ScanRunTerminal.Should().BeTrue();
        status.ScanFailedNodeNames.Should().BeEmpty("a clean run must stay distinguishable from a partial one");
    }

    [Fact]
    public void Scan_failed_job_without_a_node_label_falls_back_to_the_job_name()
    {
        var status = KubernetesStatusMapper.Map(new KubernetesWorkloadRaw
        {
            Found = false,
            Jobs = new[]
            {
                new KubernetesJobRaw { Name = "reeve-wl-abcd-nodeB-run1", NodeName = null, Succeeded = 0, Failed = 1 },
            },
        });

        status.ScanFailedNodeNames.Should().BeEquivalentTo(new[] { "reeve-wl-abcd-nodeB-run1" });
    }

    [Fact]
    public void Scan_job_that_failed_then_succeeded_on_retry_is_not_reported_as_a_failed_node()
    {
        var status = KubernetesStatusMapper.Map(new KubernetesWorkloadRaw
        {
            Found = false,
            Jobs = new[]
            {
                new KubernetesJobRaw { Name = "reeve-wl-abcd-nodeA-run1", NodeName = "node-a", Succeeded = 1, Failed = 2 },
            },
        });

        status.ScanFailedNodeNames.Should().BeEmpty("the node eventually scanned — a retried attempt is not a failed node");
    }

    [Fact]
    public void Scan_partially_completed_set_is_not_run_terminal()
    {
        var status = KubernetesStatusMapper.Map(new KubernetesWorkloadRaw
        {
            Found = false,
            Jobs = new[]
            {
                new KubernetesJobRaw { Name = "reeve-wl-abcd-nodeA-run1", Succeeded = 1, Failed = 0, Active = 0 },
                new KubernetesJobRaw { Name = "reeve-wl-abcd-nodeB-run1", Succeeded = 0, Failed = 0, Active = 0 },
            },
        });

        status.Phase.Should().Be(WorkloadProviderPhase.Ready);
        status.ScanRunTerminal.Should().BeFalse(
            "a set with a not-yet-scheduled Job has no completion evidence for the whole set — not run-terminal");
    }

    [Fact]
    public void Scan_with_a_held_job_names_the_hold_without_moving_phase_or_readiness()
    {
        var status = KubernetesStatusMapper.Map(new KubernetesWorkloadRaw
        {
            Found = false,
            Jobs = new[]
            {
                new KubernetesJobRaw { Name = "reeve-wl-abcd-nodeA-run1", Succeeded = 0, Failed = 0, Active = 0, Suspended = true },
                new KubernetesJobRaw { Name = "reeve-wl-abcd-nodeB-run1", Succeeded = 0, Failed = 0, Active = 0, Suspended = true },
            },
        });

        status.Message.Should().Contain("Scan suspended", "the operator must be able to see WHY nothing is moving");
        status.Message.Should().Contain("2 node(s)", "the hold is reported per node, not as a bare flag");
        status.Phase.Should().Be(WorkloadProviderPhase.Ready);
        status.ProviderReady.Should().BeTrue();
        status.ScanRunTerminal.Should().BeFalse(
            "a held run is NOT finished — the pending scan-run marker has to survive the hold");
    }

    [Fact]
    public void Scan_with_a_suspended_but_terminal_job_does_not_report_a_hold()
    {
        var status = KubernetesStatusMapper.Map(new KubernetesWorkloadRaw
        {
            Found = false,
            Jobs = new[]
            {
                new KubernetesJobRaw { Name = "reeve-wl-abcd-nodeA-run1", Succeeded = 1, Failed = 0, Active = 0, Suspended = true },
            },
        });

        status.Message.Should().NotContain("Scan suspended", "a finished run holds nothing");
        status.Message.Should().Contain("1 node(s) completed");
        status.ScanRunTerminal.Should().BeTrue();
    }

    [Fact]
    public void Scan_hold_does_not_mask_an_actively_running_job()
    {
        var status = KubernetesStatusMapper.Map(new KubernetesWorkloadRaw
        {
            Found = false,
            Jobs = new[]
            {
                new KubernetesJobRaw { Name = "reeve-wl-abcd-nodeA-run1", Active = 1 },
                new KubernetesJobRaw { Name = "reeve-wl-abcd-nodeB-run1", Active = 0, Suspended = true },
            },
        });

        status.Phase.Should().Be(WorkloadProviderPhase.Running);
        status.Message.Should().Contain("Scanning");
    }

    [Fact]
    public void Scan_hold_outranks_a_completed_sibling_in_the_message()
    {
        var status = KubernetesStatusMapper.Map(new KubernetesWorkloadRaw
        {
            Found = false,
            Jobs = new[]
            {
                new KubernetesJobRaw { Name = "reeve-wl-abcd-nodeA-run1", Succeeded = 1, Failed = 0, Active = 0 },
                new KubernetesJobRaw { Name = "reeve-wl-abcd-nodeB-run1", Succeeded = 0, Failed = 0, Active = 0, Suspended = true },
            },
        });

        status.Message.Should().Contain("Scan suspended");
        status.Message.Should().Contain("1 node(s) held");
        status.Message.Should().NotContain("completed", "a hold beats last-run counts — the run is stopped, not idle");
        status.ScanRunTerminal.Should().BeFalse("one node is still held, so the run as a whole is not finished");
    }

    private const string MultusCniAddFailureMessage =
        "Failed to create pod sandbox: rpc error: code = Unknown desc = failed to setup network for sandbox "
        + "\"9f1c3a\": plugin type=\"multus-shim\" name=\"multus-cni-network\" failed (add): CmdAdd (shim): CNI request "
        + "failed with status 400: '[reeve/zwave-js-7c9d-2xk/8c0b:vlan100-dhcp]: error adding container to network "
        + "\"vlan100-dhcp\": failed to send CNI request: dial unix /run/cni/dhcp.sock: connect: no such file or directory'";

    private const string MultusNadMissingMessage =
        "Failed to create pod sandbox: rpc error: code = Unknown desc = [reeve/zwave-js-7c9d-2xk/8c0b]: error adding "
        + "container to network: Multus: failed to get network attachment definition vlan100-dhcp: "
        + "networkattachmentdefinitions.k8s.cni.cncf.io \"vlan100-dhcp\" not found";

    private static KubernetesWorkloadRaw SandboxFailureRaw(
        string eventMessage,
        string reason = "FailedCreatePodSandBox") => new()
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
                (new DateTimeOffset(2026, 7, 29, 10, 0, 0, TimeSpan.Zero), reason, eventMessage, true),
            },
        };

    [Fact]
    public void AC1_Multus_sandbox_failure_maps_to_the_named_attachment_missing_condition()
    {
        var status = KubernetesStatusMapper.Map(SandboxFailureRaw(MultusCniAddFailureMessage));

        status.Phase.Should().Be(WorkloadProviderPhase.Failed,
            "a pod that cannot get its sandbox created for an attachment reason is a named failure, not a spinner");
        status.ProviderReady.Should().BeFalse();
        status.Message.Should().Contain(KubernetesWorkloadCondition.AttachmentMissingId,
            "the operator-facing message carries the typed condition id");
        status.Message.Should().Contain("vlan100-dhcp", "the condition must NAME the network");

        var condition = KubernetesStatusMapper.TryReadAttachmentMissing(status);
        condition.Should().NotBeNull("the condition is typed, not an opaque string");
        condition!.Id.Should().Be(KubernetesWorkloadCondition.AttachmentMissingId);
        condition.NetworkName.Should().Be("vlan100-dhcp");
    }

    [Fact]
    public void AC1_deleted_or_renamed_NAD_also_maps_to_the_named_condition()
    {
        var status = KubernetesStatusMapper.Map(SandboxFailureRaw(MultusNadMissingMessage));

        status.Phase.Should().Be(WorkloadProviderPhase.Failed);
        KubernetesStatusMapper.TryReadAttachmentMissing(status)!.NetworkName.Should().Be("vlan100-dhcp");
    }

    [Fact]
    public void AC2_attachment_failure_does_NOT_map_to_the_silent_ContainerCreating_arm()
    {
        var status = KubernetesStatusMapper.Map(SandboxFailureRaw(MultusCniAddFailureMessage));

        status.Phase.Should().NotBe(WorkloadProviderPhase.Running,
            "the scheduled-but-not-ready arm is exactly the indefinite ContainerCreating spinner the condition replaces");
        status.Phase.Should().NotBe(WorkloadProviderPhase.Pending);
        status.Message.Should().NotContain("0/1 ready",
            "an operator looking at this instance must see a named attachment failure, not a readiness counter");
    }

    [Fact]
    public void AC3_sandbox_failure_for_a_NON_attachment_reason_does_not_claim_the_condition()
    {
        var status = KubernetesStatusMapper.Map(SandboxFailureRaw(
            "Failed to create pod sandbox: rpc error: code = Unknown desc = failed to reserve sandbox name "
            + "\"zwave-js_reeve_0\": name is reserved"));

        status.Phase.Should().Be(WorkloadProviderPhase.Running);
        status.Message.Should().Contain("0/1 ready");
        KubernetesStatusMapper.TryReadAttachmentMissing(status).Should().BeNull();
    }

    [Fact]
    public void AC3_cni_failure_naming_no_attachment_does_not_claim_the_condition()
    {
        var status = KubernetesStatusMapper.Map(SandboxFailureRaw(
            "Failed to create pod sandbox: rpc error: code = Unknown desc = failed to setup network for sandbox "
            + "\"9f1c3a\": plugin type=\"bridge\" failed (add): failed to allocate for range 0: no IP addresses "
            + "available in range set"));

        status.Phase.Should().Be(WorkloadProviderPhase.Running);
        KubernetesStatusMapper.TryReadAttachmentMissing(status).Should().BeNull(
            "a CNI failure naming no attachment is not an attachment-missing condition");
    }

    [Fact]
    public void AC3_crash_looping_pod_still_maps_to_Failed_with_its_restart_count()
    {
        var status = KubernetesStatusMapper.Map(new KubernetesWorkloadRaw
        {
            Found = true, Kind = "Deployment", DesiredReplicas = 1, ReadyReplicas = 0,
            AllPodsRunning = false, HasPods = true, AnyPodFailed = true, PodRestartCount = 9,
        });

        status.Phase.Should().Be(WorkloadProviderPhase.Failed);
        status.RestartCount.Should().Be(9);
        status.Message.Should().Contain("restarts: 9");
        KubernetesStatusMapper.TryReadAttachmentMissing(status).Should().BeNull();
    }

    [Fact]
    public void AC3_a_stale_attachment_event_does_not_hijack_a_now_healthy_workload()
    {
        var status = KubernetesStatusMapper.Map(new KubernetesWorkloadRaw
        {
            Found = true, Kind = "Deployment", DesiredReplicas = 1, ReadyReplicas = 1,
            AllPodsRunning = true, HasPods = true, PodsRead = true,
            Events = new (DateTimeOffset, string, string, bool)[]
            {
                (new DateTimeOffset(2026, 7, 29, 10, 0, 0, TimeSpan.Zero), "FailedCreatePodSandBox", MultusCniAddFailureMessage, true),
            },
        });

        status.Phase.Should().Be(WorkloadProviderPhase.Ready);
        status.ProviderReady.Should().BeTrue();
    }

    [Fact]
    public void AC3_an_attachment_message_under_a_non_sandbox_event_reason_is_ignored()
    {
        var status = KubernetesStatusMapper.Map(SandboxFailureRaw(MultusCniAddFailureMessage, reason: "Scheduled"));

        status.Phase.Should().Be(WorkloadProviderPhase.Running);
        KubernetesStatusMapper.TryReadAttachmentMissing(status).Should().BeNull();
    }

    [Fact]
    public void A_pod_blocked_from_scheduling_stays_Running_and_names_the_scheduler_reason()
    {
        var status = KubernetesStatusMapper.Map(new KubernetesWorkloadRaw
        {
            Found = true, Kind = "Deployment", DesiredReplicas = 1, ReadyReplicas = 0, HasPods = true,
            PodBlockReason = "Unschedulable: 0/1 nodes are available: pod has unbound immediate PersistentVolumeClaims.",
        });

        status.Phase.Should().Be(WorkloadProviderPhase.Running);
        status.ProviderReady.Should().BeFalse();
        status.Message.Should().Be(
            "0/1 ready. Pod not ready: Unschedulable: 0/1 nodes are available: pod has unbound immediate PersistentVolumeClaims.");
    }

    [Fact]
    public void A_failing_pod_names_its_container_reason()
    {
        var status = KubernetesStatusMapper.Map(new KubernetesWorkloadRaw
        {
            Found = true, Kind = "Deployment", DesiredReplicas = 1, ReadyReplicas = 0, HasPods = true,
            AnyPodFailed = true, PodBlockReason = "ImagePullBackOff: Back-off pulling image \"ghcr.io/x/y:1\"",
        });

        status.Phase.Should().Be(WorkloadProviderPhase.Failed);
        status.Message.Should().Be("Pod failing. Pod not ready: ImagePullBackOff: Back-off pulling image \"ghcr.io/x/y:1\"");
    }

    [Fact]
    public void A_not_ready_pod_with_no_reason_keeps_the_plain_count()
    {
        var status = KubernetesStatusMapper.Map(new KubernetesWorkloadRaw
        {
            Found = true, Kind = "Deployment", DesiredReplicas = 1, ReadyReplicas = 0, HasPods = true,
        });

        status.Message.Should().Be("0/1 ready.");
    }
}
