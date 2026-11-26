using FluentAssertions;
using Reeve.Workloads.Abstractions.Contracts.Deployment;
using Reeve.Workloads.Abstractions.Domain;
using Reeve.Workloads.Abstractions.Domain.Entities;
using Xunit;
using Reeve.Workloads.Domain;

namespace Reeve.Workloads.Abstractions.Tests;

public sealed class WorkloadHealthEvaluatorTests
{
    private const string Desired = "spec-hash-desired";

    [Fact]
    public void AdapterSession_provider_ready_with_matching_presence_is_Healthy()
    {
        var target = WorkloadHealthEvaluator.EvaluateTarget(
            new WorkloadHealthMode("adapter-session"),
            providerReady: true,
            presence: WorkloadHealthEvaluator.FromDefiniteRead(Desired, Desired),
            desiredSpecHash: Desired,
            currentState: WorkloadInstanceState.Deploying,
            currentReason: WorkloadStateReason.HealthObserved);

        target.Should().Be(WorkloadInstanceState.Healthy);
    }

    [Fact]
    public void AdapterSession_provider_ready_with_mismatched_presence_is_not_Healthy()
    {
        var target = WorkloadHealthEvaluator.EvaluateTarget(
            new WorkloadHealthMode("adapter-session"),
            providerReady: true,
            presence: WorkloadHealthEvaluator.FromDefiniteRead("spec-hash-OLD", Desired),
            desiredSpecHash: Desired,
            currentState: WorkloadInstanceState.Deploying,
            currentReason: WorkloadStateReason.HealthObserved);

        target.Should().Be(WorkloadInstanceState.Deploying);
    }

    [Fact]
    public void AdapterSession_provider_ready_but_no_presence_is_Deploying_not_Healthy()
    {
        var target = WorkloadHealthEvaluator.EvaluateTarget(
            new WorkloadHealthMode("adapter-session"),
            providerReady: true,
            presence: WorkloadHealthEvaluator.FromDefiniteRead(presenceSpecHash: null, Desired),
            desiredSpecHash: Desired,
            currentState: WorkloadInstanceState.Deploying,
            currentReason: WorkloadStateReason.HealthObserved);

        target.Should().Be(WorkloadInstanceState.Deploying);
    }

    [Fact]
    public void AdapterSession_provider_not_ready_from_Deploying_stays_Deploying()
    {
        var target = WorkloadHealthEvaluator.EvaluateTarget(
            new WorkloadHealthMode("adapter-session"),
            providerReady: false,
            presence: WorkloadHealthEvaluator.FromDefiniteRead(null, Desired),
            desiredSpecHash: Desired,
            currentState: WorkloadInstanceState.Deploying,
            currentReason: WorkloadStateReason.HealthObserved);

        target.Should().Be(WorkloadInstanceState.Deploying);
    }

    [Fact]
    public void AdapterSession_provider_not_ready_from_Healthy_degrades()
    {
        var target = WorkloadHealthEvaluator.EvaluateTarget(
            new WorkloadHealthMode("adapter-session"),
            providerReady: false,
            presence: WorkloadHealthEvaluator.FromDefiniteRead(Desired, Desired),
            desiredSpecHash: Desired,
            currentState: WorkloadInstanceState.Healthy,
            currentReason: WorkloadStateReason.HealthObserved);

        target.Should().Be(WorkloadInstanceState.Degraded);
    }

    [Fact]
    public void AdapterSession_healthy_then_session_disappears_degrades()
    {
        var target = WorkloadHealthEvaluator.EvaluateTarget(
            new WorkloadHealthMode("adapter-session"),
            providerReady: true,
            presence: WorkloadHealthEvaluator.FromDefiniteRead(null, Desired),
            desiredSpecHash: Desired,
            currentState: WorkloadInstanceState.Healthy,
            currentReason: WorkloadStateReason.HealthObserved);

        target.Should().Be(WorkloadInstanceState.Degraded);
    }

    [Fact]
    public void AdapterSession_presence_unknown_holds_a_Healthy_instance_Healthy()
    {
        var target = WorkloadHealthEvaluator.EvaluateTarget(
            new WorkloadHealthMode("adapter-session"),
            providerReady: true,
            presence: WorkloadHealthEvaluator.Unknown,
            desiredSpecHash: Desired,
            currentState: WorkloadInstanceState.Healthy,
            currentReason: WorkloadStateReason.HealthObserved);

        target.Should().Be(WorkloadInstanceState.Healthy);
    }

    [Fact]
    public void AdapterSession_presence_unknown_holds_a_Deploying_instance_Deploying()
    {
        var target = WorkloadHealthEvaluator.EvaluateTarget(
            new WorkloadHealthMode("adapter-session"),
            providerReady: true,
            presence: WorkloadHealthEvaluator.Unknown,
            desiredSpecHash: Desired,
            currentState: WorkloadInstanceState.Deploying,
            currentReason: WorkloadStateReason.HealthObserved);

        target.Should().Be(WorkloadInstanceState.Deploying);
    }

    [Fact]
    public void Http_provider_ready_is_Healthy_without_any_session()
    {
        var target = WorkloadHealthEvaluator.EvaluateTarget(
            WorkloadHealthMode.Http,
            providerReady: true,
            presence: WorkloadHealthEvaluator.FromDefiniteRead(null, Desired),
            desiredSpecHash: Desired,
            currentState: WorkloadInstanceState.Deploying,
            currentReason: WorkloadStateReason.HealthObserved);

        target.Should().Be(WorkloadInstanceState.Healthy);
    }

    [Fact]
    public void Http_provider_not_ready_from_Healthy_degrades()
    {
        var target = WorkloadHealthEvaluator.EvaluateTarget(
            WorkloadHealthMode.Http,
            providerReady: false,
            presence: WorkloadHealthEvaluator.Unknown,
            desiredSpecHash: Desired,
            currentState: WorkloadInstanceState.Healthy,
            currentReason: WorkloadStateReason.HealthObserved);

        target.Should().Be(WorkloadInstanceState.Degraded);
    }

    [Fact]
    public void Http_provider_not_ready_from_Deploying_stays_Deploying()
    {
        var target = WorkloadHealthEvaluator.EvaluateTarget(
            WorkloadHealthMode.Http,
            providerReady: false,
            presence: WorkloadHealthEvaluator.Unknown,
            desiredSpecHash: Desired,
            currentState: WorkloadInstanceState.Deploying,
            currentReason: WorkloadStateReason.HealthObserved);

        target.Should().Be(WorkloadInstanceState.Deploying);
    }

    [Fact]
    public void Container_provider_ready_is_Healthy()
    {
        var target = WorkloadHealthEvaluator.EvaluateTarget(
            WorkloadHealthMode.Container,
            providerReady: true,
            presence: WorkloadHealthEvaluator.FromDefiniteRead(null, Desired),
            desiredSpecHash: Desired,
            currentState: WorkloadInstanceState.Deploying,
            currentReason: WorkloadStateReason.HealthObserved);

        target.Should().Be(WorkloadInstanceState.Healthy);
    }

    [Fact]
    public void Container_provider_not_ready_from_Healthy_degrades()
    {
        var target = WorkloadHealthEvaluator.EvaluateTarget(
            WorkloadHealthMode.Container,
            providerReady: false,
            presence: WorkloadHealthEvaluator.Unknown,
            desiredSpecHash: Desired,
            currentState: WorkloadInstanceState.Healthy,
            currentReason: WorkloadStateReason.HealthObserved);

        target.Should().Be(WorkloadInstanceState.Degraded);
    }

    [Fact]
    public void OperatorPaused_instance_holds_Suspended_even_when_the_pod_is_ready()
    {
        var target = WorkloadHealthEvaluator.EvaluateTarget(
            WorkloadHealthMode.Container,
            providerReady: true,
            presence: WorkloadHealthEvaluator.FromDefiniteRead(Desired, Desired),
            desiredSpecHash: Desired,
            currentState: WorkloadInstanceState.Suspended,
            currentReason: WorkloadStateReason.OperatorPaused);

        target.Should().Be(WorkloadInstanceState.Suspended,
            "the health pass must never auto-resume an operator-paused instance");
    }

    [Fact]
    public void OperatorPaused_adapter_session_instance_holds_Suspended_even_with_a_matching_session()
    {
        var target = WorkloadHealthEvaluator.EvaluateTarget(
            new WorkloadHealthMode("adapter-session"),
            providerReady: true,
            presence: WorkloadHealthEvaluator.FromDefiniteRead(Desired, Desired),
            desiredSpecHash: Desired,
            currentState: WorkloadInstanceState.Suspended,
            currentReason: WorkloadStateReason.OperatorPaused);

        target.Should().Be(WorkloadInstanceState.Suspended);
    }

    [Theory]
    [InlineData(WorkloadStateReason.SuspendedByHost)]
    [InlineData(WorkloadStateReason.OperatorPaused)]
    [InlineData(WorkloadStateReason.ProviderUnreachable)]
    public void Every_suspend_is_held_whatever_suspended_it(WorkloadStateReason reason)
    {
        var target = WorkloadHealthEvaluator.EvaluateTarget(
            WorkloadHealthMode.Container,
            providerReady: true,
            presence: WorkloadHealthEvaluator.FromDefiniteRead(Desired, Desired),
            desiredSpecHash: Desired,
            currentState: WorkloadInstanceState.Suspended,
            currentReason: reason);

        target.Should().Be(WorkloadInstanceState.Suspended);
    }

    [Fact]
    public void A_spec_rejected_workload_is_held_even_though_its_old_runtime_still_reports_ready()
    {
        var target = WorkloadHealthEvaluator.EvaluateTarget(
            WorkloadHealthMode.Container,
            providerReady: true,
            presence: WorkloadHealthEvaluator.FromDefiniteRead(Desired, Desired),
            desiredSpecHash: Desired,
            currentState: WorkloadInstanceState.Failed,
            currentReason: WorkloadStateReason.SpecRejected);

        target.Should().Be(WorkloadInstanceState.Failed,
            "the operator must be shown the refusal, not a spinner that never resolves");
    }

    [Fact]
    public void A_workload_failed_for_any_other_reason_still_follows_its_runtime()
    {
        var target = WorkloadHealthEvaluator.EvaluateTarget(
            WorkloadHealthMode.Container,
            providerReady: true,
            presence: WorkloadHealthEvaluator.FromDefiniteRead(Desired, Desired),
            desiredSpecHash: Desired,
            currentState: WorkloadInstanceState.Failed,
            currentReason: WorkloadStateReason.HealthLost);

        target.Should().Be(WorkloadInstanceState.Healthy);
    }

    [Fact]
    public void FromDefiniteRead_null_is_Absent()
        => WorkloadHealthEvaluator.FromDefiniteRead(null, Desired).Kind
            .Should().Be(WorkloadPresenceSignalKind.Absent);

    [Fact]
    public void FromDefiniteRead_equal_hash_is_PresentMatching()
        => WorkloadHealthEvaluator.FromDefiniteRead(Desired, Desired).Kind
            .Should().Be(WorkloadPresenceSignalKind.PresentMatching);

    [Fact]
    public void FromDefiniteRead_different_hash_is_PresentMismatched()
        => WorkloadHealthEvaluator.FromDefiniteRead("other", Desired).Kind
            .Should().Be(WorkloadPresenceSignalKind.PresentMismatched);

    [Fact]
    public void Unknown_is_the_fault_signal()
        => WorkloadHealthEvaluator.Unknown.Kind.Should().Be(WorkloadPresenceSignalKind.Unknown);
}
