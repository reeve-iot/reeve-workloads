using FluentAssertions;
using Reeve.Workloads.Abstractions.Domain.Entities;
using Reeve.Workloads.Core.HealthGate;
using Reeve.Workloads.Core.Update;
using Xunit;

namespace Reeve.Workloads.Core.Tests;

public sealed class GateClockDocumentTests
{
    private static readonly DateTimeOffset At = new(2026, 9, 11, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [MemberData(nameof(States))]
    public void The_application_twin_carries_the_engine_state_both_ways(HealthGateClockState state)
    {
        var twin = WorkloadUpdateHealthGateAdapter.ToState(state);

        WorkloadUpdateHealthGateAdapter.FromState(twin).Should().Be(state);
    }

    public static TheoryData<HealthGateClockState> States() => new()
    {
        HealthGateClockState.Initial,
        new HealthGateClockState
        {
            ClockStartedAtUtc = At,
            PausedSinceUtc = At.AddMinutes(2),
            AccumulatedPause = TimeSpan.FromSeconds(95),
            ReconnectGraceUntilUtc = At.AddMinutes(5),
            GraceConsumed = true,
        },
        new HealthGateClockState { PausedSinceUtc = At, AccumulatedPause = TimeSpan.FromMilliseconds(1500) },
    };

    [Theory]
    [MemberData(nameof(States))]
    public void The_document_holds_every_field_the_gate_wrote(HealthGateClockState state)
    {
        var step = WorkloadUpdateIntentStep.Create(Guid.NewGuid(), Guid.NewGuid(), "zigbee", At);

        step.SetGateClockState(WorkloadUpdateHealthGateAdapter.ToState(state), At);

        step.GateClockStateDocument.Should().BeEquivalentTo(new
        {
            Unreadable = false,
            state.ClockStartedAtUtc,
            state.PausedSinceUtc,
            state.AccumulatedPause,
            state.ReconnectGraceUntilUtc,
            state.GraceConsumed,
        });
    }

    [Theory]
    [MemberData(nameof(States))]
    public void The_document_reads_back_as_the_state_the_gate_wrote(HealthGateClockState state)
    {
        var step = WorkloadUpdateIntentStep.Create(Guid.NewGuid(), Guid.NewGuid(), "zigbee", At);
        step.SetGateClockState(WorkloadUpdateHealthGateAdapter.ToState(state), At);

        var read = step.ReadPriorClockState();

        read.Should().Be(WorkloadUpdateHealthGateAdapter.ToState(state));
        WorkloadUpdateHealthGateAdapter.FromState(read).Should().Be(state);
    }

    [Fact]
    public void A_step_with_no_clock_document_reads_as_the_initial_state()
    {
        var step = WorkloadUpdateIntentStep.Create(Guid.NewGuid(), Guid.NewGuid(), "zigbee", At);

        WorkloadUpdateHealthGateAdapter.FromState(step.ReadPriorClockState())
            .Should().Be(HealthGateClockState.Initial);
    }
}
