using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Docker.DotNet;
using FluentAssertions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Reeve.Workloads.Abstractions.Contracts;
using Reeve.Workloads.Abstractions.Domain.Network;
using Xunit;

namespace Reeve.Workloads.Providers.Docker.Tests.Network;

public sealed class DockerNetworkAttachmentSourceListTests
{
    private static WorkloadOrchestratorSettings Settings() =>
        new WorkloadOrchestratorSettings { Provider = DockerProvider.Id }
            .WithProvider(DockerProvider.Id, new DockerProviderSettings { SocketUri = "unix:///var/run/docker.sock" });

    private static (DockerNetworkAttachmentSource Source, IDockerNetworkManager Networks) Build()
    {
        var clientFactory = Substitute.For<IDockerClientFactory>();
        clientFactory.CreateClient(Arg.Any<string?>()).Returns(_ => Substitute.For<IDockerClient>());
        var networks = Substitute.For<IDockerNetworkManager>();
        return (new DockerNetworkAttachmentSource(clientFactory, networks), networks);
    }

    private static void Answers(IDockerNetworkManager networks, DockerAttachableNetworkListResult result) =>
        networks
            .ListAttachableNetworksAsync(
                Arg.Any<IDockerClient>(), Arg.Any<DockerProviderSettings>(), Arg.Any<CancellationToken>())
            .Returns(result);

    [Theory]
    [InlineData(nameof(DockerFailureKind.Unreachable))]
    [InlineData(nameof(DockerFailureKind.Rejected))]
    [InlineData(nameof(DockerFailureKind.Error))]
    public async Task Every_docker_failure_kind_maps_to_provider_unavailable(string kindName)
    {
        var kind = Enum.Parse<DockerFailureKind>(kindName);
        var (source, networks) = Build();
        Answers(networks, DockerAttachableNetworkListResult.Fail(kind, "the daemon said no"));

        var response = await source.ListAsync(Settings(), CancellationToken.None);

        response.Provider.Should().Be(DockerProvider.Id);
        response.Items.Should().BeEmpty();
        response.UnavailableReason.Should().Be(WorkloadNetworkAttachmentUnavailableReasons.ProviderUnavailable);
    }

    [Fact]
    public async Task A_successful_enumeration_with_nothing_to_offer_carries_no_unavailable_reason()
    {
        var (source, networks) = Build();
        Answers(networks, DockerAttachableNetworkListResult.Ok([]));

        var response = await source.ListAsync(Settings(), CancellationToken.None);

        response.Items.Should().BeEmpty();
        response.UnavailableReason.Should().BeNull();
    }

    [Fact]
    public async Task The_projection_carries_the_name_only()
    {
        var (source, networks) = Build();
        Answers(networks, DockerAttachableNetworkListResult.Ok(
        [
            new DockerAttachableNetwork { Name = "reeve-workloads" },
            new DockerAttachableNetwork { Name = "iot-macvlan" },
        ]));

        var response = await source.ListAsync(Settings(), CancellationToken.None);

        response.UnavailableReason.Should().BeNull();
        response.Items.Select(i => i.Name).Should().Equal(["reeve-workloads", "iot-macvlan"]);
    }

    [Fact]
    public async Task A_throwing_seam_still_degrades_rather_than_escaping()
    {
        var (source, networks) = Build();
        networks
            .ListAttachableNetworksAsync(default!, default!, default)
            .ThrowsAsyncForAnyArgs(new HttpRequestException("the daemon hung up"));

        var response = await source.ListAsync(Settings(), CancellationToken.None);

        response.Items.Should().BeEmpty();
        response.UnavailableReason.Should().Be(WorkloadNetworkAttachmentUnavailableReasons.ProviderUnavailable);
    }

    [Fact]
    public void The_failure_kind_table_above_is_the_whole_enum_minus_none()
    {
        Enum.GetNames<DockerFailureKind>().Where(n => n != nameof(DockerFailureKind.None))
            .Should().BeEquivalentTo(
                nameof(DockerFailureKind.Unreachable),
                nameof(DockerFailureKind.Rejected),
                nameof(DockerFailureKind.Error));
    }

    [Fact]
    public async Task Caller_cancellation_still_propagates()
    {
        var (source, networks) = Build();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        networks
            .ListAttachableNetworksAsync(default!, default!, default)
            .ThrowsAsyncForAnyArgs(new OperationCanceledException(cts.Token));

        var act = async () => await source.ListAsync(Settings(), cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }
}
