using FluentAssertions;
using Reeve.Workloads.Abstractions;
using Reeve.Workloads.Providers.Docker;
using Xunit;

namespace Reeve.Workloads.Providers.Docker.Tests;

public sealed class DockerHostAddressContributorTests
{
    private readonly DockerHostAddressContributor contributor = new();

    [Fact]
    public void It_answers_for_the_docker_provider()
        => contributor.Provider.Should().Be(DockerProvider.Id);

    [Theory]
    [InlineData("reeve-backend", 8080, "reeve-backend:8080")]
    [InlineData("smarthome-api", 80, "smarthome-api:80")]
    public void It_answers_name_colon_port(string name, int port, string expected)
        => contributor.AddressOf(new HostEndpoint(name, port)).Should().Be(expected);

    [Fact]
    public void A_namespace_means_nothing_on_a_docker_network()
        => contributor.AddressOf(new HostEndpoint("reeve-backend", 8080, "home"))
            .Should().Be("reeve-backend:8080");
}
