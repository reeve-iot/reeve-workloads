using FluentAssertions;
using Reeve.Workloads.Abstractions.Domain.Entities;

namespace Reeve.Workloads.Abstractions.Tests;

public sealed class WorkloadUpdateIntentStepProgressTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 23, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(nameof(WorkloadUpdateStepState.Pending), WorkloadUpdateStepProgress.Pending)]
    [InlineData(nameof(WorkloadUpdateStepState.Applying), WorkloadUpdateStepProgress.Working)]
    [InlineData(nameof(WorkloadUpdateStepState.GateWaiting), WorkloadUpdateStepProgress.Working)]
    [InlineData(nameof(WorkloadUpdateStepState.Swapping), WorkloadUpdateStepProgress.Working)]
    [InlineData(nameof(WorkloadUpdateStepState.Done), WorkloadUpdateStepProgress.Done)]
    [InlineData(nameof(WorkloadUpdateStepState.RolledBack), WorkloadUpdateStepProgress.Undone)]
    [InlineData(nameof(WorkloadUpdateStepState.Failed), WorkloadUpdateStepProgress.Failed)]
    public void Every_stored_state_reads_as_one_progress(string stateName, WorkloadUpdateStepProgress progress)
    {
        var step = WorkloadUpdateIntentStep.Create(Guid.NewGuid(), Guid.NewGuid(), "gateway", Now);
        step.Advance(WorkloadUpdatePhase.HealthGate, Enum.Parse<WorkloadUpdateStepState>(stateName), null, Now);

        step.Progress.Should().Be(progress);
    }

    [Fact]
    public void A_new_step_is_pending()
        => WorkloadUpdateIntentStep.Create(Guid.NewGuid(), Guid.NewGuid(), "gateway", Now).Progress
            .Should().Be(WorkloadUpdateStepProgress.Pending);
}
