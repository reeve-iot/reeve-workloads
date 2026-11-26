using FluentAssertions;
using Reeve.Workloads.Core.Update.Stages;
using Xunit;

namespace Reeve.Workloads.Core.Tests.Update.Stages;

public sealed class WorkloadUpdateStageContextsTests
{
    [Fact]
    public async Task It_carries_the_group_the_host_stage_addresses_its_work_by()
    {
        var plan = StageContextFactory.DefaultPlan();

        var context = await StageContextFactory.BuildAsync(plan);

        context.GroupId.Should().Be(plan.GroupId);
        context.GroupInstanceId.Should().Be(plan.GroupInstanceId);
        context.SourceKey.Should().Be(plan.SourceKey);
        context.FromVersion.Should().Be(plan.FromVersion);
        context.ToVersion.Should().Be(plan.ToVersion);
    }

    [Fact]
    public async Task It_keeps_the_workloads_in_manifest_declaration_order()
    {
        var context = await StageContextFactory.BuildAsync();

        context.Workloads.Select(workload => workload.WorkloadId).Should().Equal("alpha", "beta");
    }

    [Fact]
    public async Task A_workload_the_candidate_adds_has_no_previous_spec_to_go_back_to()
    {
        var context = await StageContextFactory.BuildAsync();

        context.Workloads[0].PreviousSpec.Should().NotBeNull();
        context.Workloads[1].PreviousSpec.Should().BeNull(
            "an added workload never existed at the previous version, so compensation tears it down rather "
            + "than restoring it");
    }

    [Fact]
    public async Task It_carries_the_intent_and_the_plan_where_only_this_library_can_read_them()
    {
        var plan = StageContextFactory.DefaultPlan();

        var context = await StageContextFactory.BuildAsync(plan);

        context.Internals.Should().NotBeNull();
        context.Internals!.Plan.Should().BeSameAs(plan);
        context.Internals.Intent.Id.Should().Be(context.IntentId);
    }
}
