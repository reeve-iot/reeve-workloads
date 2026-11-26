using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Docker.DotNet;
using Docker.DotNet.Models;
using FluentAssertions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Reeve.Workloads.Abstractions.Contracts;
using Reeve.Workloads.Providers.Docker;
using Xunit;

namespace Reeve.Workloads.Providers.Docker.Tests;

public sealed class DockerAttachableNetworkEnumerationTests
{
    private static readonly IDockerNetworkManager Manager = new DockerNetworkManager();

    private static NetworkResponse Net(
        string name,
        string? driver,
        IDictionary<string, string>? labels = null,
        string scope = "local") =>
        new()
        {
            Name = name,
            ID = $"id-{name}",
            Driver = driver!,
            Scope = scope,
            Labels = labels!,
        };

    private static IReadOnlyList<NetworkResponse> FakedNetworks() =>
    [
        Net(
            DockerProviderConstants.DefaultNetworkName,
            "bridge",
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [DockerProviderConstants.ManagedByLabelKey] = DockerProviderConstants.ManagedByLabelValue,
            }),

        Net(
            "reeve-side-net",
            "bridge",
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [DockerProviderConstants.InstallationLabelKey] = "install-1",
            }),

        Net("reeve_default", "bridge"),

        Net("operator-lan", "bridge"),

        Net("iot-macvlan", "macvlan"),
        Net("iot-ipvlan", "ipvlan"),

        Net("host", "host"),
        Net("none", "none"),
        Net("null-driver-net", "null"),
        Net("driver-absent-net", null),
        Net("driver-blank-net", "   "),
    ];

    [Fact]
    public void IDockerNetworkManager_DeclaresTheEnumeration_WithNoDefaultInterfaceImplementation()
    {
        var declared = typeof(IDockerNetworkManager).GetMethods();

        declared.Should().OnlyContain(m => m.IsAbstract, "no member may carry a default implementation");
        declared.Select(m => m.Name).Should().BeEquivalentTo(
            [nameof(IDockerNetworkManager.EnsureSharedNetworkAsync),
             nameof(IDockerNetworkManager.ListAttachableNetworksAsync),
             nameof(IDockerNetworkManager.RenderNetworking)],
            "the widening adds exactly one member and leaves the other two alone");
    }

    [Fact]
    public void DockerProviderConstants_CarriesNoNetworkGeneratorMarker_InM1()
    {
        const BindingFlags publicStatic =
            BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy;

        var constantValues = typeof(DockerProviderConstants)
            .GetFields(publicStatic)
            .Where(f => f.FieldType == typeof(string))
            .Select(f => (string?)f.GetValue(null))
            .Concat(typeof(DockerProviderConstants)
                .GetProperties(publicStatic)
                .Where(p => p.PropertyType == typeof(string) && p.GetMethod is not null)
                .Select(p => (string?)p.GetValue(null)))
            .ToList();

        constantValues.Should().NotContain("network-generator");
        constantValues.Should().NotContain("io.reeve.created-by");
    }

    private static IDockerClient ClientReturning(IList<NetworkResponse> networks)
    {
        var client = Substitute.For<IDockerClient>();
        var networkOps = Substitute.For<INetworkOperations>();
        networkOps
            .ListNetworksAsync(Arg.Any<NetworksListParameters>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(networks));
        client.Networks.Returns(networkOps);
        client.Containers.Returns(Substitute.For<IContainerOperations>());
        return client;
    }

    private static IDockerClient ClientThrowing(Exception ex)
    {
        var client = Substitute.For<IDockerClient>();
        var networkOps = Substitute.For<INetworkOperations>();
        networkOps
            .ListNetworksAsync(Arg.Any<NetworksListParameters>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(ex);
        client.Networks.Returns(networkOps);
        client.Containers.Returns(Substitute.For<IContainerOperations>());
        return client;
    }

    [Fact]
    public async Task Every_named_network_is_listed_whatever_its_driver_labels_or_role()
    {
        var client = ClientReturning([.. FakedNetworks()]);

        var result = await Manager.ListAttachableNetworksAsync(client, new DockerProviderSettings(), CancellationToken.None);

        result.Succeeded.Should().BeTrue();
        result.FailureKind.Should().Be(DockerFailureKind.None);
        result.Networks.Select(n => n.Name).Should().Equal(
            [DockerProviderConstants.DefaultNetworkName, "reeve-side-net", "reeve_default", "operator-lan",
             "iot-macvlan", "iot-ipvlan", "host", "none", "null-driver-net", "driver-absent-net", "driver-blank-net"]);
    }

    [Fact]
    public async Task A_network_whose_driver_and_ipam_changed_lists_identically()
    {
        var before = Net("iot-lan", "macvlan");
        before.IPAM = new IPAM { Driver = "default", Config = [new IPAMConfig { Subnet = "192.0.2.0/24" }] };
        var after = Net("iot-lan", "bridge");
        after.IPAM = new IPAM { Driver = "other", Config = [new IPAMConfig { Subnet = "10.0.0.0/8" }] };

        var listedBefore = await Manager.ListAttachableNetworksAsync(
            ClientReturning([before]), new DockerProviderSettings(), CancellationToken.None);
        var listedAfter = await Manager.ListAttachableNetworksAsync(
            ClientReturning([after]), new DockerProviderSettings(), CancellationToken.None);

        listedAfter.Networks.Should().BeEquivalentTo(listedBefore.Networks);
    }

    [Fact]
    public async Task A_listed_network_carries_only_its_name()
    {
        typeof(DockerAttachableNetwork).GetProperties().Select(p => p.Name).Should().Equal(["Name"]);

        var result = await Manager.ListAttachableNetworksAsync(
            ClientReturning([Net("operator-lan", "bridge")]), new DockerProviderSettings(), CancellationToken.None);

        result.Networks.Should().ContainSingle().Which.Name.Should().Be("operator-lan");
    }

    [Fact]
    public async Task ListAttachableNetworksAsync_EmptyDaemonList_IsASuccessfulEmptyResult()
    {
        var client = ClientReturning([]);

        var result = await Manager.ListAttachableNetworksAsync(client, new DockerProviderSettings(), CancellationToken.None);

        result.Succeeded.Should().BeTrue();
        result.Networks.Should().BeEmpty();
    }

    [Fact]
    public async Task ListAttachableNetworksAsync_DaemonError_IsATypedFailure_NotAnEscapingException()
    {
        var client = ClientThrowing(new DockerApiException(HttpStatusCode.InternalServerError, "boom"));

        var result = await Manager.ListAttachableNetworksAsync(client, new DockerProviderSettings(), CancellationToken.None);

        result.Succeeded.Should().BeFalse();
        result.FailureKind.Should().Be(DockerFailureKind.Error);
        result.Message.Should().NotBeNullOrWhiteSpace();
        result.Networks.Should().BeEmpty();
    }

    [Fact]
    public async Task ListAttachableNetworksAsync_UnreachableDaemon_IsATypedFailure()
    {
        var client = ClientThrowing(new HttpRequestException("socket down"));

        var result = await Manager.ListAttachableNetworksAsync(client, new DockerProviderSettings(), CancellationToken.None);

        result.Succeeded.Should().BeFalse();
        result.FailureKind.Should().Be(DockerFailureKind.Unreachable);
    }

    [Fact]
    public async Task ListAttachableNetworksAsync_Cancellation_StillPropagates()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        var client = ClientThrowing(new OperationCanceledException(cts.Token));

        var act = async () => await Manager.ListAttachableNetworksAsync(client, new DockerProviderSettings(), cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }
}
