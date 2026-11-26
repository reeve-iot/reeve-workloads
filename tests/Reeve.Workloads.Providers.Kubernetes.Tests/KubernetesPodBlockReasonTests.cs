using System.Collections.Generic;
using FluentAssertions;
using k8s.Models;
using Reeve.Workloads.Providers.Kubernetes;
using Xunit;

namespace Reeve.Workloads.Providers.Kubernetes.Tests;

public sealed class KubernetesPodBlockReasonTests
{
    private const string UnboundClaim =
        "0/1 nodes are available: pod has unbound immediate PersistentVolumeClaims. preemption: 0/1 nodes are available: 1 Preemption is not helpful for scheduling.";

    [Fact]
    public void An_unscheduled_pod_reports_the_scheduler_condition()
    {
        var pod = Pod("a", "Pending", conditions: [new V1PodCondition { Type = "PodScheduled", Status = "False", Reason = "Unschedulable", Message = UnboundClaim }]);

        KubernetesPodBlockReason.Describe([pod]).Should().Be("Unschedulable: " + UnboundClaim);
    }

    [Fact]
    public void A_waiting_container_reports_its_reason_and_message()
    {
        var pod = Pod("a", "Pending", containers: [Waiting("app", "CreateContainerConfigError", "secret \"s\" not found")]);

        KubernetesPodBlockReason.Describe([pod]).Should().Be("CreateContainerConfigError: secret \"s\" not found");
    }

    [Theory]
    [InlineData("ErrImagePull", "rpc error: code = NotFound desc = failed to pull and unpack image \"x:1\"")]
    [InlineData("ImagePullBackOff", "Back-off pulling image \"x:1\"")]
    public void An_image_pull_reads_the_same_whichever_way_the_kubelet_reports_it(string reason, string message)
    {
        var container = Waiting("app", reason, message);
        container.Image = "x:1";

        KubernetesPodBlockReason.Describe([Pod("a", "Pending", containers: [container])])
            .Should().Be("ImagePullBackOff: container app cannot pull image x:1");
    }

    [Fact]
    public void A_crash_loop_drops_the_changing_back_off_delay()
    {
        var container = Waiting("app", "CrashLoopBackOff", "back-off 2m40s restarting failed container=app pod=a");
        container.LastState = new V1ContainerState { Terminated = new V1ContainerStateTerminated { ExitCode = 3 } };

        KubernetesPodBlockReason.Describe([Pod("a", "Running", containers: [container])])
            .Should().Be("CrashLoopBackOff: container app exited with code 3");
    }

    [Fact]
    public void A_failing_pod_is_named_before_a_pod_that_is_only_not_ready()
    {
        var surge = Pod("a", "Pending", containers: [Waiting("app", "ContainerCreating", null)]);
        var crashing = Pod("b", "Running", containers: [Waiting("app", "CrashLoopBackOff", "back-off 10s")]);

        KubernetesPodBlockReason.Describe([surge, crashing]).Should().Be("CrashLoopBackOff: container app keeps restarting");
    }

    [Fact]
    public void A_waiting_init_container_is_reported_before_the_main_containers()
    {
        var pod = Pod("a", "Pending",
            containers: [Waiting("app", "PodInitializing", null)],
            initContainers: [Waiting("init", "CreateContainerConfigError", "secret \"s\" not found")]);

        KubernetesPodBlockReason.Describe([pod]).Should().Be("CreateContainerConfigError: secret \"s\" not found");
    }

    [Fact]
    public void A_running_pod_failing_its_readiness_reports_the_Ready_condition()
    {
        var pod = Pod("a", "Running",
            conditions: [new V1PodCondition { Type = "PodScheduled", Status = "True" }, new V1PodCondition { Type = "Ready", Status = "False", Reason = "ContainersNotReady", Message = "containers with unready status: [app]" }],
            containers: [new V1ContainerStatus { Name = "app", State = new V1ContainerState { Running = new V1ContainerStateRunning() } }]);

        KubernetesPodBlockReason.Describe([pod]).Should().Be("ContainersNotReady: containers with unready status: [app]");
    }

    [Fact]
    public void A_ready_pod_reports_nothing()
    {
        var pod = Pod("a", "Running", conditions: [new V1PodCondition { Type = "Ready", Status = "True" }]);

        KubernetesPodBlockReason.Describe([pod]).Should().BeNull();
    }

    [Fact]
    public void Pods_are_read_in_name_order_so_passes_agree()
    {
        var later = Pod("b", "Pending", containers: [Waiting("app", "CreateContainerError", null)]);
        var earlier = Pod("a", "Pending", containers: [Waiting("app", "ContainerCreating", null)]);

        KubernetesPodBlockReason.Describe([later, earlier]).Should().Be("ContainerCreating");
    }

    private static V1Pod Pod(
        string name, string phase, IList<V1PodCondition>? conditions = null,
        IList<V1ContainerStatus>? containers = null, IList<V1ContainerStatus>? initContainers = null) => new()
    {
        Metadata = new V1ObjectMeta { Name = name },
        Status = new V1PodStatus
        {
            Phase = phase,
            Conditions = conditions,
            ContainerStatuses = containers,
            InitContainerStatuses = initContainers,
        },
    };

    private static V1ContainerStatus Waiting(string name, string reason, string? message) => new()
    {
        Name = name,
        State = new V1ContainerState { Waiting = new V1ContainerStateWaiting { Reason = reason, Message = message } },
    };
}
