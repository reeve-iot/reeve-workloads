using FluentAssertions;
using Reeve.Workloads.Abstractions.Contracts;
using Reeve.Workloads.Abstractions.Domain.Entities;
using Reeve.Workloads.Tests.Shared;

namespace Reeve.Workloads.Abstractions.Tests;

public sealed class WorkloadInstanceStateMachineSwitchingTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 12, 12, 0, 0, TimeSpan.Zero);

    private static WorkloadInstance NewInstance()
        => WorkloadInstance.Create(Guid.NewGuid(), Guid.NewGuid(), "matter", "matter-server", Now);

    private static WorkloadInstance Healthy()
    {
        var instance = NewInstance();
        instance.TransitionTo(WorkloadInstanceState.Deploying, WorkloadStateReason.Provisioning, null, Now);
        instance.TransitionTo(WorkloadInstanceState.Healthy, WorkloadStateReason.HealthObserved, null, Now);
        instance.State.Should().Be(WorkloadInstanceState.Healthy);
        return instance;
    }

    private static WorkloadInstance Degraded()
    {
        var instance = Healthy();
        instance.TransitionTo(WorkloadInstanceState.Degraded, WorkloadStateReason.HealthLost, null, Now);
        instance.State.Should().Be(WorkloadInstanceState.Degraded);
        return instance;
    }

    private static WorkloadInstance Switching()
    {
        var instance = Healthy();
        instance.TransitionTo(WorkloadInstanceState.Switching, WorkloadStateReason.OperatorPaused, null, Now);
        instance.State.Should().Be(WorkloadInstanceState.Switching);
        return instance;
    }

    [Fact]
    public void Healthy_can_begin_a_switch()
    {
        Healthy()
            .Invoking(i => i.TransitionTo(WorkloadInstanceState.Switching, WorkloadStateReason.OperatorPaused, null, Now))
            .Should().NotThrow("a managed-running Healthy instance may begin a managed→manual switch");
    }

    [Fact]
    public void Degraded_can_begin_a_switch()
    {
        Degraded()
            .Invoking(i => i.TransitionTo(WorkloadInstanceState.Switching, WorkloadStateReason.OperatorPaused, null, Now))
            .Should().NotThrow("a managed-running Degraded instance is still managed, so it may begin a switch too");
    }

    [Fact]
    public void Switching_to_externally_managed_is_valid_on_success()
    {
        var instance = Switching();

        instance.Invoking(i => i.TransitionTo(WorkloadInstanceState.ExternallyManaged, WorkloadStateReason.ExternalHealthObserved, null, Now))
            .Should().NotThrow("a successful switch hands ownership to the operator (Switching → ExternallyManaged)");
        instance.State.Should().Be(WorkloadInstanceState.ExternallyManaged);
    }

    [Fact]
    public void Switching_unwinds_to_the_managed_running_state_on_failure()
    {
        Switching()
            .Invoking(i => i.TransitionTo(WorkloadInstanceState.Healthy, WorkloadStateReason.HealthObserved, null, Now))
            .Should().NotThrow("an in-band failure or boot-unwind returns the instance to the managed-running state");

        Switching()
            .Invoking(i => i.TransitionTo(WorkloadInstanceState.Degraded, WorkloadStateReason.HealthLost, null, Now))
            .Should().NotThrow("unwind may restore Degraded (the other managed-running state)");
    }

    [Fact]
    public void Switching_to_terminating_is_valid()
    {
        Switching()
            .Invoking(i => i.TransitionTo(WorkloadInstanceState.Terminating, WorkloadStateReason.Uninstalling, null, Now))
            .Should().NotThrow("an uninstall may still tear down a mid-switch instance (universal Terminating escape hatch)");
    }

    [Fact]
    public void Switching_has_exactly_the_four_expected_exits()
    {
        WorkloadInstanceStateMachine.AllowedTargets(WorkloadInstanceState.Switching)
            .Should().BeEquivalentTo(new[]
            {
                WorkloadInstanceState.ExternallyManaged,
                WorkloadInstanceState.Healthy,
                WorkloadInstanceState.Degraded,
                WorkloadInstanceState.Terminating
            });
    }

    [Fact]
    public void Pending_configuration_cannot_jump_to_switching()
    {
        var instance = NewInstance();
        instance.State.Should().Be(WorkloadInstanceState.PendingConfiguration);

        instance.Invoking(i => i.TransitionTo(WorkloadInstanceState.Switching, WorkloadStateReason.OperatorPaused, null, Now))
            .Should().Throw<WorkloadStateTransitionException>()
            .Where(ex => ex.From == WorkloadInstanceState.PendingConfiguration && ex.To == WorkloadInstanceState.Switching);
    }

    [Fact]
    public void Externally_managed_cannot_re_enter_switching_double_owner_guard()
    {
        var instance = Healthy();
        instance.TransitionTo(WorkloadInstanceState.ExternallyManaged, WorkloadStateReason.ExternalHealthObserved, null, Now);

        instance.Invoking(i => i.TransitionTo(WorkloadInstanceState.Switching, WorkloadStateReason.OperatorPaused, null, Now))
            .Should().Throw<WorkloadStateTransitionException>()
            .Where(ex => ex.From == WorkloadInstanceState.ExternallyManaged && ex.To == WorkloadInstanceState.Switching);
    }

    [Fact]
    public void Switching_cannot_hop_to_an_unrelated_live_state()
    {
        Switching()
            .Invoking(i => i.TransitionTo(WorkloadInstanceState.Unknown, WorkloadStateReason.ProviderUnreachable, null, Now))
            .Should().Throw<WorkloadStateTransitionException>("Switching has no Unknown exit");

        Switching()
            .Invoking(i => i.TransitionTo(WorkloadInstanceState.Deploying, WorkloadStateReason.Redeploying, null, Now))
            .Should().Throw<WorkloadStateTransitionException>("Switching has no Deploying exit");
    }

    [Fact]
    public void Switching_persists_by_its_string_name_not_ordinal()
    {
        WorkloadInstanceState.Switching.ToString().Should().Be("Switching");

        Enum.Parse<WorkloadInstanceState>("Switching").Should().Be(WorkloadInstanceState.Switching);
        Enum.TryParse<WorkloadInstanceState>("Switching", out var parsed).Should().BeTrue();
        parsed.Should().Be(WorkloadInstanceState.Switching);
    }

    [Fact]
    public void The_provider_ids_are_the_lowercase_words_the_column_holds()
    {
        StandInProviders.KubernetesId.Value.Should().Be("kubernetes");
        StandInProviders.DockerId.Value.Should().Be("docker");
        StandInProviders.ManualId.Value.Should().Be("manual");

        WorkloadProviderId.Parse("Kubernetes").Should().Be(StandInProviders.KubernetesId);
    }

    [Fact]
    public void Self_transition_switching_to_switching_is_a_noop_that_only_refreshes_reason_and_message()
    {
        var instance = Switching();
        var later = Now.AddMinutes(5);
        var firstTransition = instance.LastTransitionAtUtc;

        instance.Invoking(i => i.TransitionTo(WorkloadInstanceState.Switching, WorkloadStateReason.OperatorPaused, "still switching", later))
            .Should().NotThrow("a self-transition never throws");

        instance.State.Should().Be(WorkloadInstanceState.Switching);
        instance.StateMessage.Should().Be("still switching");
        instance.LastTransitionAtUtc.Should().Be(firstTransition, "a self-transition is not a real state change");
        instance.UpdatedAtUtc.Should().Be(later);
    }

    [Fact]
    public void Switching_instance_cannot_be_freely_mutated()
    {
        Switching().CanMutate.Should().BeFalse("a mid-switch instance is owned by the switch and must not be re-configured");
    }
}
