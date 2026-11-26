using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Reeve.Workloads.Abstractions;
using Reeve.Workloads.Abstractions.Providers;
using Reeve.Workloads.DependencyInjection;
using Xunit;

namespace Reeve.Workloads.Core.Tests.Seams;

public sealed class ValidateHostPortsTests
{
    private static readonly IConfiguration Configuration = new ConfigurationBuilder().Build();

    private sealed class NothingIsLive : IWorkloadExternalLiveness
    {
        public ValueTask<bool> IsLiveAsync(Guid instanceId, CancellationToken cancellationToken = default)
            => ValueTask.FromResult(false);
    }


    private static ReeveWorkloadsBuilder Engine(bool manual, bool liveness)
    {
        var builder = new ServiceCollection()
            .AddReeveWorkloads(Configuration, ports =>
            {
                if (liveness)
                {
                    ports.Use<IWorkloadExternalLiveness, NothingIsLive>(ServiceLifetime.Scoped);
                }
            })
            .AddEngine();
        return manual ? builder.AddManualProvider() : builder;
    }

    [Fact]
    public void A_manual_host_with_no_liveness_port_is_refused_naming_it()
    {
        var act = () => Engine(manual: true, liveness: false).ValidateHostPorts();

        act.Should().Throw<InvalidOperationException>()
            .Which.Message.Should().Contain(typeof(IWorkloadExternalLiveness).FullName)
            .And.Contain("ManualWorkloadProvider");
    }

    [Fact]
    public void A_manual_host_that_supplies_the_port_no_longer_owes_it_and_is_not_told_it_is_surplus()
    {
        var builder = Engine(manual: true, liveness: true);

        builder.FindMissingHostPorts().Select(missing => missing.Port)
            .Should().NotContain(typeof(IWorkloadExternalLiveness));
        builder.FindSuppliedPortsTheEngineDoesNotAskFor().Should().NotContain(typeof(IWorkloadExternalLiveness));
    }

    [Fact]
    public void A_host_without_the_manual_provider_does_not_owe_it()
    {
        Engine(manual: false, liveness: false).FindMissingHostPorts().Select(missing => missing.Port)
            .Should().NotContain(typeof(IWorkloadExternalLiveness));
    }
}
