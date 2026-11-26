using FluentAssertions;
using Reeve.Workloads.Abstractions;
using Reeve.Workloads.Abstractions.Contracts;
using Reeve.Workloads.Core;
using Xunit;

namespace Reeve.Workloads.Core.Tests;

public sealed class WorkloadHostAddressesTests
{
    private static readonly WorkloadProviderId Alpha = WorkloadProviderId.Parse("alpha");
    private static readonly WorkloadProviderId Beta = WorkloadProviderId.Parse("beta");
    private static readonly WorkloadProviderId Unregistered = WorkloadProviderId.Parse("gamma");
    private static readonly HostEndpoint Backend = new("reeve-backend", 8080, "home");

    [Fact]
    public void It_dispatches_to_the_contributor_of_the_asked_provider()
    {
        var addresses = new WorkloadHostAddresses([new Tagged(Alpha, "a"), new Tagged(Beta, "b")]);

        addresses.AddressOf(Alpha, Backend).Should().Be("a:reeve-backend:8080:home");
        addresses.AddressOf(Beta, Backend).Should().Be("b:reeve-backend:8080:home");
    }

    [Fact]
    public void An_unregistered_provider_answers_null()
        => new WorkloadHostAddresses([new Tagged(Alpha, "a")]).AddressOf(Unregistered, Backend).Should().BeNull();

    [Fact]
    public void No_contributors_at_all_answers_null()
        => new WorkloadHostAddresses([]).AddressOf(Alpha, Backend).Should().BeNull();

    [Fact]
    public void A_contributor_that_cannot_say_answers_null()
        => new WorkloadHostAddresses([new Tagged(Alpha, tag: null)]).AddressOf(Alpha, Backend).Should().BeNull();

    [Fact]
    public void The_last_registered_contributor_for_a_provider_wins()
        => new WorkloadHostAddresses([new Tagged(Alpha, "first"), new Tagged(Alpha, "second")])
            .AddressOf(Alpha, Backend).Should().StartWith("second:");

    private sealed class Tagged(WorkloadProviderId provider, string? tag) : IWorkloadHostAddressContributor
    {
        public WorkloadProviderId Provider => provider;

        public string? AddressOf(HostEndpoint endpoint)
            => tag is null ? null : $"{tag}:{endpoint.Name}:{endpoint.Port}:{endpoint.Namespace}";
    }
}
