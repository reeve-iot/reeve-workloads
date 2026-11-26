using FluentAssertions;
using Reeve.Pipeline;
using Reeve.Workloads.Abstractions.Update;
using Reeve.Workloads.Core.Update.Stages;
using Xunit;

namespace Reeve.Workloads.Core.Tests.Update.Stages;

public sealed class ParamsStageTests
{
    private static ParamsStage Stage(out RecordingEffects effects, CallLog? log = null)
    {
        effects = new RecordingEffects(log ?? new CallLog());
        return new ParamsStage(effects);
    }

    [Fact]
    public void It_is_the_head_of_the_walk()
    {
        var stage = Stage(out _);

        stage.Id.Should().Be(WorkloadUpdateStages.Params);
        stage.Placement.IsFirst.Should().BeTrue(
            "nothing may run before the diff is computed and the snapshots are taken");
        stage.Reversibility.IsReversible.Should().BeTrue();
    }

    [Fact]
    public void It_declares_no_budget_for_a_waiting_turn()
    {
        Stage(out _).Budget.Limit.Should().BeNull();
    }

    [Fact]
    public async Task Up_finishes_without_re_staging_what_the_initiator_already_committed()
    {
        var stage = Stage(out var effects);
        var context = await StageContextFactory.BuildAsync();

        var outcome = await stage.UpAsync(context, CancellationToken.None);

        outcome.Kind.Should().Be(StageOutcomeKind.Done);
        effects.RestoreCount.Should().Be(0);
        effects.CommitParamsCount.Should().Be(0);
        effects.CommitInstalledVersionCount.Should().Be(0);
    }

    [Fact]
    public async Task Up_of_a_single_intent_needs_no_internals()
    {
        var stage = Stage(out _);
        var context = (await StageContextFactory.BuildAsync()) with { Internals = null };

        var outcome = await stage.UpAsync(context, CancellationToken.None);

        outcome.Kind.Should().Be(StageOutcomeKind.Done);
    }

    [Fact]
    public async Task Down_restores_the_parameter_snapshot_recorded_on_the_intent()
    {
        var stage = Stage(out var effects);
        var context = await StageContextFactory.BuildAsync();

        var outcome = await stage.DownAsync(context, CancellationToken.None);

        outcome.Kind.Should().Be(StageOutcomeKind.Done);
        effects.RestoreCount.Should().Be(1);
    }

    [Fact]
    public async Task Down_is_safe_on_a_stage_that_never_began_and_on_a_replayed_tick()
    {
        var stage = Stage(out var effects);
        var context = await StageContextFactory.BuildAsync();

        var first = await stage.DownAsync(context, CancellationToken.None);
        var second = await stage.DownAsync(context, CancellationToken.None);

        first.Kind.Should().Be(StageOutcomeKind.Done);
        second.Kind.Should().Be(StageOutcomeKind.Done);
        effects.RestoreCount.Should().Be(2, "the restore is idempotent, so a replayed tick simply repeats it");
    }

    [Fact]
    public async Task A_context_built_without_the_carrier_is_refused_and_names_the_intent()
    {
        var stage = Stage(out _);
        var real = await StageContextFactory.BuildAsync();
        var stripped = real with { Internals = null };

        var down = async () => await stage.DownAsync(stripped, CancellationToken.None);

        (await down.Should().ThrowAsync<InvalidOperationException>())
            .WithMessage($"*{real.IntentId}*")
            .WithMessage("*WorkloadUpdateOrchestrator*");
    }
}
