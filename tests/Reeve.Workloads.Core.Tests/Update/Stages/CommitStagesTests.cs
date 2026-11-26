using FluentAssertions;
using Reeve.Pipeline;
using Reeve.Workloads.Abstractions.Update;
using Reeve.Workloads.Core.Update.Stages;
using Xunit;

namespace Reeve.Workloads.Core.Tests.Update.Stages;

public sealed class CommitStagesTests
{
    private static (CommitVersionStage Stage, RecordingEffects Effects, CallLog Log) Version()
    {
        var (stage, effects, _, log) = VersionWithRows();
        return (stage, effects, log);
    }

    private static (CommitVersionStage Stage, RecordingEffects Effects, RecordingRowEffects Rows, CallLog Log) VersionWithRows()
    {
        var log = new CallLog();
        var effects = new RecordingEffects(log);
        var rows = new RecordingRowEffects(log);
        return (new CommitVersionStage(effects, rows), effects, rows, log);
    }

    private static (CommitParamsStage Stage, RecordingEffects Effects, CallLog Log) Params()
    {
        var (stage, effects, _, log) = ParamsWithRows();
        return (stage, effects, log);
    }

    private static (CommitParamsStage Stage, RecordingEffects Effects, RecordingRowEffects Rows, CallLog Log) ParamsWithRows()
    {
        var log = new CallLog();
        var effects = new RecordingEffects(log);
        var rows = new RecordingRowEffects(log);
        return (new CommitParamsStage(effects, rows), effects, rows, log);
    }

    [Fact]
    public void The_version_commit_follows_the_health_gate()
    {
        var (stage, _, _) = Version();

        stage.Id.Should().Be(WorkloadUpdateStages.CommitVersion);
        stage.Placement.AfterId.Should().Be(WorkloadUpdateStages.HealthGate);
        stage.Reversibility.IsReversible.Should().BeTrue();
    }

    [Fact]
    public async Task Up_moves_the_group_to_the_version_the_operator_agreed_to()
    {
        var (stage, effects, log) = Version();
        var plan = StageContextFactory.DefaultPlan();
        var context = await StageContextFactory.BuildAsync(plan);

        var outcome = await stage.UpAsync(context, CancellationToken.None);

        outcome.Kind.Should().Be(StageOutcomeKind.Done);
        effects.CommitInstalledVersionCount.Should().Be(1);
        effects.LastCommittedGroupId.Should().Be(plan.GroupId);
        log.Entries.Should().Contain($"commit-installed-version:{plan.ToVersion}");
    }

    [Fact]
    public async Task Up_is_safe_to_replay()
    {
        var (stage, effects, _) = Version();
        var context = await StageContextFactory.BuildAsync();

        await stage.UpAsync(context, CancellationToken.None);
        var second = await stage.UpAsync(context, CancellationToken.None);

        second.Kind.Should().Be(StageOutcomeKind.Done);
        effects.CommitInstalledVersionCount.Should().Be(2);
    }

    [Fact]
    public async Task Down_puts_the_group_back_on_the_version_it_came_from()
    {
        var (stage, effects, log) = Version();
        var plan = StageContextFactory.DefaultPlan();
        var context = await StageContextFactory.BuildAsync(plan);

        var outcome = await stage.DownAsync(context, CancellationToken.None);

        outcome.Kind.Should().Be(StageOutcomeKind.Done);
        effects.CommitInstalledVersionCount.Should().Be(1);
        log.Entries.Should().Contain($"commit-installed-version:{plan.FromVersion}");
        log.Entries.Should().NotContain($"commit-installed-version:{plan.ToVersion}",
            "an unwind that committed the candidate again would be no unwind at all");
    }

    [Fact]
    public async Task Down_refuses_when_there_is_no_previous_version_to_return_to()
    {
        var (stage, effects, _) = Version();
        var plan = StageContextFactory.DefaultPlan() with { FromVersion = null };
        var context = await StageContextFactory.BuildAsync(plan);

        var outcome = await stage.DownAsync(context, CancellationToken.None);

        outcome.Kind.Should().Be(StageOutcomeKind.Failed);
        outcome.Detail.Should().Contain(plan.GroupId).And.Contain(plan.ToVersion);
        effects.CommitInstalledVersionCount.Should().Be(0, "there was nothing it could have committed");
    }

    [Fact]
    public async Task Up_ensures_the_added_rows_before_it_moves_the_group()
    {
        var (stage, _, rows, log) = VersionWithRows();
        var plan = StageContextFactory.DefaultPlan();
        var context = await StageContextFactory.BuildAsync(plan);

        await stage.UpAsync(context, CancellationToken.None);
        await stage.UpAsync(context, CancellationToken.None);

        rows.EnsureCount.Should().Be(2);
        log.Entries.Should().Equal(
            "ensure-rows", $"commit-installed-version:{plan.ToVersion}",
            "ensure-rows", $"commit-installed-version:{plan.ToVersion}");
    }

    [Fact]
    public async Task Down_removes_the_added_rows_after_it_moves_the_group_back()
    {
        var (stage, _, rows, log) = VersionWithRows();
        var plan = StageContextFactory.DefaultPlan();
        var context = await StageContextFactory.BuildAsync(plan);

        var outcome = await stage.DownAsync(context, CancellationToken.None);

        outcome.Kind.Should().Be(StageOutcomeKind.Done);
        rows.RemoveCount.Should().Be(1);
        log.Entries.Should().Equal($"commit-installed-version:{plan.FromVersion}", "remove-rows");
    }

    [Fact]
    public async Task Down_with_no_previous_version_leaves_the_added_rows()
    {
        var (stage, _, rows, _) = VersionWithRows();
        var context = await StageContextFactory.BuildAsync(StageContextFactory.DefaultPlan() with { FromVersion = null });

        await stage.DownAsync(context, CancellationToken.None);

        rows.RemoveCount.Should().Be(0);
    }

    [Fact]
    public void The_parameter_commit_is_anchored_last_by_a_maximal_ordinal()
    {
        var (stage, _, _) = Params();

        stage.Id.Should().Be(WorkloadUpdateStages.CommitParams);
        stage.Placement.AfterId.Should().Be(WorkloadUpdateStages.CommitVersion);
        stage.Placement.Ordinal.Should().Be(WorkloadUpdateStages.CommitParamsOrdinal,
            "a host stage anchored at the same commit would otherwise win the tie alphabetically and run after "
            + "the parameters were committed");
    }

    [Fact]
    public async Task Up_commits_the_candidate_values_as_the_new_last_known_good()
    {
        var (stage, effects, _) = Params();
        var context = await StageContextFactory.BuildAsync();

        var outcome = await stage.UpAsync(context, CancellationToken.None);

        outcome.Kind.Should().Be(StageOutcomeKind.Done);
        effects.CommitParamsCount.Should().Be(1);
    }

    [Fact]
    public async Task Down_restores_the_snapshot_because_a_failed_commit_unwinds_itself_first()
    {
        var (stage, effects, _) = Params();
        var context = await StageContextFactory.BuildAsync();

        var outcome = await stage.DownAsync(context, CancellationToken.None);

        outcome.Kind.Should().Be(StageOutcomeKind.Done);
        effects.RestoreCount.Should().Be(1);
    }

    [Fact]
    public async Task Up_tears_the_dropped_workloads_down_only_once_the_parameters_are_committed()
    {
        var (stage, _, _, log) = ParamsWithRows();
        var context = await StageContextFactory.BuildAsync();

        await stage.UpAsync(context, CancellationToken.None);

        log.Entries.Should().Equal(["commit-params", "teardown-dropped"],
            "the teardown cannot be undone, so it comes after the last step a failure could still roll back");
    }

    [Fact]
    public void The_parameter_commit_is_irreversible_once_entered_because_it_tears_dropped_workloads_down()
    {
        var (stage, _, _) = Params();

        stage.Reversibility.IsReversible.Should().BeFalse(
            "an abandon past it would roll back to a version without the workloads whose teardown began");
        stage.Reversibility.OperatorReason.Should().Contain("torn down");
    }

    [Fact]
    public async Task Neither_commit_keeps_working_so_neither_declares_a_budget()
    {
        var (version, _, _) = Version();
        var (parameters, _, _) = Params();
        await Task.CompletedTask;

        version.Budget.Limit.Should().BeNull();
        parameters.Budget.Limit.Should().BeNull();
    }
}
