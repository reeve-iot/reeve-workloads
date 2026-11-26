using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Reeve.Workloads.Core;
using Reeve.Workloads.Providers.Kubernetes;
using Xunit;

namespace Reeve.Workloads.Core.Tests;

public sealed class WorkloadReconcilerOptionsTests
{
    [Fact]
    public void Defaults_run_the_periodic_pass_every_minute_with_the_watch_on()
    {
        var options = new WorkloadReconcilerOptions();

        options.StatusResyncInterval.Should().Be(TimeSpan.FromSeconds(60));
        options.WatchEnabled.Should().BeTrue("provider-watch reconcile is on out of the box");
    }

    [Fact]
    public void Binds_the_cadence_and_watch_tunables_from_the_Workloads_Reconciler_section()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Workloads:Reconciler:WatchEnabled"] = "false",
                ["Workloads:Reconciler:StatusResyncSeconds"] = "120",
            })
            .Build();

        var services = new ServiceCollection();
        services.AddOptions<WorkloadReconcilerOptions>()
            .Bind(configuration.GetSection(WorkloadReconcilerOptions.SectionName))
            .ValidateDataAnnotations();
        using var provider = services.BuildServiceProvider();

        var options = provider.GetRequiredService<IOptions<WorkloadReconcilerOptions>>().Value;

        options.WatchEnabled.Should().BeFalse();
        options.StatusResyncInterval.Should().Be(TimeSpan.FromSeconds(120));
    }

    [Fact]
    public void The_update_walk_asks_a_working_stage_again_within_seconds_and_sleeps_a_minute_when_idle()
    {
        var options = new Reeve.Workloads.Core.Update.WorkloadUpdateWalkOptions();

        options.WorkingTickInterval.Should().Be(TimeSpan.FromSeconds(5),
            "A13: an update moves at its stages' pace, and the cadence a host stage is promised is this one");
        options.IdleFallbackInterval.Should().Be(TimeSpan.FromSeconds(60));
    }

    [Fact]
    public void Binds_the_update_walk_from_the_Workloads_UpdateWalk_section()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Workloads:UpdateWalk:WorkingTickSeconds"] = "2",
                ["Workloads:UpdateWalk:IdleFallbackSeconds"] = "30",
            })
            .Build();

        var services = new ServiceCollection();
        services.AddOptions<Reeve.Workloads.Core.Update.WorkloadUpdateWalkOptions>()
            .Bind(configuration.GetSection(Reeve.Workloads.Core.Update.WorkloadUpdateWalkOptions.SectionName))
            .ValidateDataAnnotations();
        using var provider = services.BuildServiceProvider();

        var options = provider.GetRequiredService<IOptions<Reeve.Workloads.Core.Update.WorkloadUpdateWalkOptions>>().Value;

        options.WorkingTickInterval.Should().Be(TimeSpan.FromSeconds(2));
        options.IdleFallbackInterval.Should().Be(TimeSpan.FromSeconds(30));
    }

    [Theory]
    [InlineData("WorkingTickSeconds", "0")]
    [InlineData("IdleFallbackSeconds", "0")]
    public void A_walk_interval_of_zero_is_refused(string key, string value)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { [$"Workloads:UpdateWalk:{key}"] = value })
            .Build();
        var services = new ServiceCollection();
        services.AddOptions<Reeve.Workloads.Core.Update.WorkloadUpdateWalkOptions>()
            .Bind(configuration.GetSection(Reeve.Workloads.Core.Update.WorkloadUpdateWalkOptions.SectionName))
            .ValidateDataAnnotations();
        using var provider = services.BuildServiceProvider();

        var read = () => provider.GetRequiredService<IOptions<Reeve.Workloads.Core.Update.WorkloadUpdateWalkOptions>>().Value;

        read.Should().Throw<OptionsValidationException>("a zero wait would spin the leader");
    }
}
