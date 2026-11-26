using FluentAssertions;
using Reeve.Workloads.Abstractions.Domain.Entities;

namespace Reeve.Workloads.Abstractions.Tests;

public sealed class WorkloadInstanceStateMachineTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 6, 12, 0, 0, TimeSpan.Zero);

    private static WorkloadInstance NewInstance()
        => WorkloadInstance.Create(Guid.NewGuid(), Guid.NewGuid(), "matter", "matter-server", Now);

    private static WorkloadInstance InState(WorkloadInstanceState target)
    {
        var instance = NewInstance();
        foreach (var (state, reason) in PathTo(target))
        {
            instance.TransitionTo(state, reason, null, Now);
        }

        instance.State.Should().Be(target);
        return instance;
    }

    private static IEnumerable<(WorkloadInstanceState, WorkloadStateReason)> PathTo(WorkloadInstanceState target)
        => target switch
        {
            WorkloadInstanceState.PendingConfiguration => Array.Empty<(WorkloadInstanceState, WorkloadStateReason)>(),
            WorkloadInstanceState.Deploying =>
            [
                (WorkloadInstanceState.Deploying, WorkloadStateReason.Provisioning)
            ],
            WorkloadInstanceState.Healthy =>
            [
                (WorkloadInstanceState.Deploying, WorkloadStateReason.Provisioning),
                (WorkloadInstanceState.Healthy, WorkloadStateReason.HealthObserved)
            ],
            WorkloadInstanceState.Degraded =>
            [
                (WorkloadInstanceState.Deploying, WorkloadStateReason.Provisioning),
                (WorkloadInstanceState.Healthy, WorkloadStateReason.HealthObserved),
                (WorkloadInstanceState.Degraded, WorkloadStateReason.HealthLost)
            ],
            WorkloadInstanceState.Failed =>
            [
                (WorkloadInstanceState.Deploying, WorkloadStateReason.Provisioning),
                (WorkloadInstanceState.Failed, WorkloadStateReason.HealthLost)
            ],
            WorkloadInstanceState.VersionSkew =>
            [
                (WorkloadInstanceState.Deploying, WorkloadStateReason.Provisioning),
                (WorkloadInstanceState.Healthy, WorkloadStateReason.HealthObserved),
                (WorkloadInstanceState.VersionSkew, WorkloadStateReason.RunningVersionBehind)
            ],
            WorkloadInstanceState.ExternallyManaged =>
            [
                (WorkloadInstanceState.Deploying, WorkloadStateReason.Provisioning),
                (WorkloadInstanceState.ExternallyManaged, WorkloadStateReason.ExternalHealthObserved)
            ],
            WorkloadInstanceState.Suspended =>
            [
                (WorkloadInstanceState.Deploying, WorkloadStateReason.Provisioning),
                (WorkloadInstanceState.Healthy, WorkloadStateReason.HealthObserved),
                (WorkloadInstanceState.Suspended, WorkloadStateReason.OperatorPaused)
            ],
            WorkloadInstanceState.Unknown =>
            [
                (WorkloadInstanceState.Deploying, WorkloadStateReason.Provisioning),
                (WorkloadInstanceState.Healthy, WorkloadStateReason.HealthObserved),
                (WorkloadInstanceState.Unknown, WorkloadStateReason.ProviderUnreachable)
            ],
            WorkloadInstanceState.AdoptionPending =>
            [
                (WorkloadInstanceState.AdoptionPending, WorkloadStateReason.ForeignResourcesDetected)
            ],
            WorkloadInstanceState.Terminating =>
            [
                (WorkloadInstanceState.Terminating, WorkloadStateReason.Uninstalling)
            ],
            _ => throw new ArgumentOutOfRangeException(nameof(target))
        };

    [Fact]
    public void New_instance_starts_waiting_for_its_configuration()
    {
        var instance = NewInstance();

        instance.State.Should().Be(WorkloadInstanceState.PendingConfiguration);
        instance.StateReason.Should().Be(WorkloadStateReason.ConfigRequired);
    }

    [Fact]
    public void Happy_path_configure_deploy_healthy()
    {
        var instance = NewInstance();

        instance.TransitionTo(WorkloadInstanceState.PendingConfiguration, WorkloadStateReason.ConfigRequired, null, Now);
        instance.TransitionTo(WorkloadInstanceState.Deploying, WorkloadStateReason.Provisioning, null, Now);
        instance.TransitionTo(WorkloadInstanceState.Healthy, WorkloadStateReason.HealthObserved, "up", Now);

        instance.State.Should().Be(WorkloadInstanceState.Healthy);
        instance.LastHealthyAtUtc.Should().Be(Now);
        instance.StateMessage.Should().Be("up");
    }

    [Fact]
    public void New_required_param_on_update_sends_healthy_back_to_pending_configuration()
    {
        var instance = InState(WorkloadInstanceState.Healthy);

        instance.TransitionTo(WorkloadInstanceState.PendingConfiguration, WorkloadStateReason.ConfigRequired, null, Now);

        instance.State.Should().Be(WorkloadInstanceState.PendingConfiguration);
    }

    [Fact]
    public void Failed_to_healthy_is_legal_no_terminal_failed()
    {
        var instance = InState(WorkloadInstanceState.Failed);

        var act = () => instance.TransitionTo(WorkloadInstanceState.Healthy, WorkloadStateReason.HealthObserved, null, Now);

        act.Should().NotThrow();
        instance.State.Should().Be(WorkloadInstanceState.Healthy);
    }

    [Fact]
    public void Healthy_and_degraded_cycle()
    {
        var instance = InState(WorkloadInstanceState.Healthy);

        instance.TransitionTo(WorkloadInstanceState.Degraded, WorkloadStateReason.HealthLost, null, Now);
        instance.TransitionTo(WorkloadInstanceState.Healthy, WorkloadStateReason.HealthObserved, null, Now);
        instance.TransitionTo(WorkloadInstanceState.Degraded, WorkloadStateReason.HealthLost, null, Now);
        instance.TransitionTo(WorkloadInstanceState.Failed, WorkloadStateReason.HealthLost, null, Now);

        instance.State.Should().Be(WorkloadInstanceState.Failed);
    }

    [Fact]
    public void Degraded_and_failed_can_redeploy()
    {
        InState(WorkloadInstanceState.Degraded)
            .Invoking(i => i.TransitionTo(WorkloadInstanceState.Deploying, WorkloadStateReason.Redeploying, null, Now))
            .Should().NotThrow();

        InState(WorkloadInstanceState.Failed)
            .Invoking(i => i.TransitionTo(WorkloadInstanceState.Deploying, WorkloadStateReason.Redeploying, null, Now))
            .Should().NotThrow();
    }

    [Fact]
    public void Healthy_to_versionskew_and_back()
    {
        var instance = InState(WorkloadInstanceState.Healthy);

        instance.TransitionTo(WorkloadInstanceState.VersionSkew, WorkloadStateReason.RunningVersionBehind, null, Now);
        instance.State.Should().Be(WorkloadInstanceState.VersionSkew);

        instance.TransitionTo(WorkloadInstanceState.Healthy, WorkloadStateReason.HealthObserved, null, Now);
        instance.State.Should().Be(WorkloadInstanceState.Healthy);
    }

    [Fact]
    public void A_healthy_row_may_be_torn_down_or_held_by_the_host()
    {
        InState(WorkloadInstanceState.Healthy)
            .Invoking(i => i.TransitionTo(WorkloadInstanceState.Terminating, WorkloadStateReason.Uninstalling, null, Now))
            .Should().NotThrow();

        InState(WorkloadInstanceState.Healthy)
            .Invoking(i => i.TransitionTo(WorkloadInstanceState.Suspended, WorkloadStateReason.SuspendedByHost, null, Now))
            .Should().NotThrow();
    }

    [Fact]
    public void Suspend_and_resume()
    {
        var instance = InState(WorkloadInstanceState.Suspended);

        instance.TransitionTo(WorkloadInstanceState.Deploying, WorkloadStateReason.OperatorResumed, null, Now);
        instance.State.Should().Be(WorkloadInstanceState.Deploying);
    }

    [Fact]
    public void Any_live_state_can_go_unknown_on_provider_unreachable()
    {
        foreach (var live in new[]
                 {
                     WorkloadInstanceState.Deploying,
                     WorkloadInstanceState.Healthy,
                     WorkloadInstanceState.Degraded,
                     WorkloadInstanceState.Failed,
                     WorkloadInstanceState.VersionSkew,
                     WorkloadInstanceState.ExternallyManaged
                 })
        {
            InState(live)
                .Invoking(i => i.TransitionTo(WorkloadInstanceState.Unknown, WorkloadStateReason.ProviderUnreachable, null, Now))
                .Should().NotThrow($"{live} → Unknown must be legal");
        }
    }

    [Fact]
    public void Unknown_forbids_mutations_but_allows_return_to_observed_or_terminating()
    {
        var instance = InState(WorkloadInstanceState.Unknown);
        instance.CanMutate.Should().BeFalse();

        InState(WorkloadInstanceState.Unknown)
            .Invoking(i => i.TransitionTo(WorkloadInstanceState.Healthy, WorkloadStateReason.HealthObserved, null, Now))
            .Should().NotThrow();

        InState(WorkloadInstanceState.Unknown)
            .Invoking(i => i.TransitionTo(WorkloadInstanceState.Terminating, WorkloadStateReason.Uninstalling, null, Now))
            .Should().NotThrow();

        InState(WorkloadInstanceState.Unknown)
            .Invoking(i => i.TransitionTo(WorkloadInstanceState.PendingConfiguration, WorkloadStateReason.ConfigRequired, null, Now))
            .Should().Throw<WorkloadStateTransitionException>();
    }

    [Fact]
    public void CanMutate_is_false_in_unknown_and_terminating_true_otherwise()
    {
        InState(WorkloadInstanceState.Unknown).CanMutate.Should().BeFalse();
        InState(WorkloadInstanceState.Terminating).CanMutate.Should().BeFalse();
        InState(WorkloadInstanceState.Healthy).CanMutate.Should().BeTrue();
        InState(WorkloadInstanceState.PendingConfiguration).CanMutate.Should().BeTrue();
        InState(WorkloadInstanceState.Degraded).CanMutate.Should().BeTrue();
    }

    [Fact]
    public void Adoption_pending_leads_to_deploying()
    {
        var instance = InState(WorkloadInstanceState.AdoptionPending);

        instance.TransitionTo(WorkloadInstanceState.Deploying, WorkloadStateReason.AdoptionAccepted, null, Now);
        instance.State.Should().Be(WorkloadInstanceState.Deploying);
    }

    [Fact]
    public void Uninstall_from_any_reachable_state_goes_terminating()
    {
        foreach (var from in new[]
                 {
                     WorkloadInstanceState.PendingConfiguration,
                     WorkloadInstanceState.Deploying,
                     WorkloadInstanceState.Healthy,
                     WorkloadInstanceState.Degraded,
                     WorkloadInstanceState.Failed,
                     WorkloadInstanceState.Suspended
                 })
        {
            InState(from)
                .Invoking(i => i.TransitionTo(WorkloadInstanceState.Terminating, WorkloadStateReason.Uninstalling, null, Now))
                .Should().NotThrow($"{from} → Terminating (uninstall) must be legal");
        }
    }

    [Fact]
    public void Terminating_is_terminal_apart_from_a_provider_blip()
    {
        WorkloadInstanceStateMachine.AllowedTargets(WorkloadInstanceState.Terminating)
            .Should().BeEquivalentTo(new[] { WorkloadInstanceState.Unknown });

        InState(WorkloadInstanceState.Terminating)
            .Invoking(i => i.TransitionTo(WorkloadInstanceState.Deploying, WorkloadStateReason.Provisioning, null, Now))
            .Should().Throw<WorkloadStateTransitionException>();
    }

    [Fact]
    public void A_row_the_host_holds_can_only_resume_be_torn_down_or_be_lost()
    {
        WorkloadInstanceStateMachine.AllowedTargets(WorkloadInstanceState.Suspended)
            .Should().BeEquivalentTo(new[]
            {
                WorkloadInstanceState.Deploying,
                WorkloadInstanceState.Healthy,
                WorkloadInstanceState.Terminating,
                WorkloadInstanceState.Unknown,
            });

        InState(WorkloadInstanceState.Suspended)
            .Invoking(i => i.TransitionTo(WorkloadInstanceState.Deploying, WorkloadStateReason.ResumedByHost, null, Now))
            .Should().NotThrow("only a resume lifts it, and a resume is what this edge is");
    }

    [Fact]
    public void The_state_enum_has_no_zero_member()
    {
        Enum.IsDefined(default(WorkloadInstanceState)).Should().BeFalse();
        WorkloadInstanceStateMachine.AllowedTargets(default).Should().BeEmpty("an unnamed state goes nowhere");
        Enum.GetValues<WorkloadInstanceState>().Should().OnlyContain(state => (int)state > 0);
    }

    [Fact]
    public void Illegal_transition_throws_naming_both_states()
    {
        var instance = InState(WorkloadInstanceState.PendingConfiguration);

        var act = () => instance.TransitionTo(WorkloadInstanceState.Healthy, WorkloadStateReason.HealthObserved, null, Now);

        act.Should().Throw<WorkloadStateTransitionException>()
            .Where(ex => ex.From == WorkloadInstanceState.PendingConfiguration && ex.To == WorkloadInstanceState.Healthy);
    }

    [Fact]
    public void Representative_illegal_transitions_throw()
    {
        InState(WorkloadInstanceState.Suspended)
            .Invoking(i => i.TransitionTo(WorkloadInstanceState.Degraded, WorkloadStateReason.HealthLost, null, Now))
            .Should().Throw<WorkloadStateTransitionException>();

        InState(WorkloadInstanceState.AdoptionPending)
            .Invoking(i => i.TransitionTo(WorkloadInstanceState.Healthy, WorkloadStateReason.HealthObserved, null, Now))
            .Should().Throw<WorkloadStateTransitionException>();
    }

    [Fact]
    public void Self_transition_refreshes_reason_and_message_without_advancing_transition_clock()
    {
        var instance = InState(WorkloadInstanceState.Healthy);
        var later = Now.AddMinutes(5);
        var firstTransition = instance.LastTransitionAtUtc;

        instance.TransitionTo(WorkloadInstanceState.Healthy, WorkloadStateReason.HealthObserved, "still up", later);

        instance.State.Should().Be(WorkloadInstanceState.Healthy);
        instance.StateMessage.Should().Be("still up");
        instance.LastTransitionAtUtc.Should().Be(firstTransition, "a self-transition is not a real state change");
        instance.UpdatedAtUtc.Should().Be(later);
    }

    [Fact]
    public void State_message_is_truncated_at_the_cap()
    {
        var instance = InState(WorkloadInstanceState.Failed);
        var longMessage = new string('x', WorkloadInstance.MaxStateMessageLength + 500);

        instance.TransitionTo(WorkloadInstanceState.Failed, WorkloadStateReason.HealthLost, longMessage, Now);

        instance.StateMessage!.Length.Should().Be(WorkloadInstance.MaxStateMessageLength);
    }
}
