using System;
using FluentAssertions;
using Reeve.Workloads.Abstractions.Providers;
using Reeve.Workloads.Providers.Manual;
using Xunit;

namespace Reeve.Workloads.Providers.Manual.Tests;

public sealed class ManualStatusMapperTests
{
    [Fact]
    public void Live_maps_to_ready_and_provider_ready()
    {
        var status = ManualStatusMapper.Map(live: true);

        status.Phase.Should().Be(WorkloadProviderPhase.Ready);
        status.ProviderReady.Should().BeTrue();
        status.Message.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void Not_live_maps_to_pending_and_not_ready()
    {
        var status = ManualStatusMapper.Map(live: false);

        status.Phase.Should().Be(WorkloadProviderPhase.Pending);
        status.ProviderReady.Should().BeFalse();
        status.Message.Should().NotBeNullOrWhiteSpace();
    }
}
