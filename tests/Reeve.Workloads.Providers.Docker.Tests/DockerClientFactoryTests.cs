using System;
using System.Runtime.InteropServices;
using Docker.DotNet;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Reeve.Workloads.Providers.Docker;
using Xunit;

namespace Reeve.Workloads.Providers.Docker.Tests;

public sealed class DockerClientFactoryTests
{
    private static DockerClientFactory NewFactory() =>
        new(NullLogger<DockerClientFactory>.Instance);

    private static bool IsWindows => RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

    [Fact]
    public void ResolveEndpoint_returns_the_platform_default_when_socketUri_is_null()
    {
        DockerClientFactory.ResolveEndpoint(null)
            .Should().Be(DockerClientFactory.PlatformDefaultEndpoint());
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void ResolveEndpoint_returns_the_platform_default_when_socketUri_is_blank(string socketUri)
    {
        DockerClientFactory.ResolveEndpoint(socketUri)
            .Should().Be(DockerClientFactory.PlatformDefaultEndpoint());
    }

    [Fact]
    public void PlatformDefaultEndpoint_is_the_unix_socket_on_non_windows()
    {
        if (IsWindows)
        {
            DockerClientFactory.PlatformDefaultEndpoint()
                .Should().Be(new Uri(DockerProviderConstants.WindowsDefaultSocketUri));
        }
        else
        {
            DockerClientFactory.PlatformDefaultEndpoint()
                .Should().Be(new Uri(DockerProviderConstants.UnixDefaultSocketUri));
        }
    }

    [Fact]
    public void ResolveEndpoint_honors_an_explicit_unix_socket_uri()
    {
        DockerClientFactory.ResolveEndpoint("unix:///var/run/docker.sock")
            .Should().Be(new Uri("unix:///var/run/docker.sock"));
    }

    [Fact]
    public void ResolveEndpoint_honors_an_explicit_non_default_unix_socket_uri()
    {
        DockerClientFactory.ResolveEndpoint("unix:///run/user/1000/docker.sock")
            .Should().Be(new Uri("unix:///run/user/1000/docker.sock"));
    }

    [Fact]
    public void ResolveEndpoint_honors_a_custom_scheme_best_effort()
    {
        DockerClientFactory.ResolveEndpoint("tcp://192.0.2.10:2375")
            .Should().Be(new Uri("tcp://192.0.2.10:2375"));
    }

    [Theory]
    [InlineData("not a uri")]
    [InlineData("a b c")]
    public void ResolveEndpoint_throws_FormatException_for_a_malformed_uri(string socketUri)
    {
        var act = () => DockerClientFactory.ResolveEndpoint(socketUri);
        act.Should().Throw<FormatException>();
    }

    [Fact]
    public void CreateClient_null_builds_a_client_bound_to_the_platform_default_socket()
    {
        var factory = NewFactory();

        IDockerClient client = factory.CreateClient(null);

        client.Should().NotBeNull();
        client.Options.Endpoint.Should().Be(DockerClientFactory.PlatformDefaultEndpoint());
    }

    [Fact]
    public void CreateClient_honors_an_explicit_unix_socket_uri()
    {
        var factory = NewFactory();

        IDockerClient client = factory.CreateClient("unix:///run/user/1000/docker.sock");

        client.Options.Endpoint.Should().Be(new Uri("unix:///run/user/1000/docker.sock"));
    }

    [Fact]
    public void CreateClient_returns_the_SAME_cached_instance_for_the_same_endpoint()
    {
        var factory = NewFactory();

        IDockerClient first = factory.CreateClient("unix:///var/run/docker.sock");
        IDockerClient second = factory.CreateClient("unix:///var/run/docker.sock");

        second.Should().BeSameAs(first,
            "the Singleton factory must cache one long-lived client per endpoint and reuse it, not build a new disposable handler chain per call");
    }

    [Fact]
    public void CreateClient_rebuilds_when_the_endpoint_changes()
    {
        var factory = NewFactory();

        IDockerClient first = factory.CreateClient("unix:///var/run/docker.sock");
        IDockerClient rebuilt = factory.CreateClient("unix:///run/user/1000/docker.sock");

        rebuilt.Should().NotBeSameAs(first, "a changed socketUri must rebuild the client for the new endpoint");
        rebuilt.Options.Endpoint.Should().Be(new Uri("unix:///run/user/1000/docker.sock"));
    }

    [Fact]
    public void CreateClient_after_Dispose_throws_ObjectDisposedException()
    {
        var factory = NewFactory();
        _ = factory.CreateClient(null);

        factory.Dispose();

        var act = () => factory.CreateClient(null);
        act.Should().Throw<ObjectDisposedException>();
    }

    [Fact]
    public void Dispose_is_idempotent()
    {
        var factory = NewFactory();
        _ = factory.CreateClient(null);

        var act = () =>
        {
            factory.Dispose();
            factory.Dispose();
        };

        act.Should().NotThrow();
    }
}
