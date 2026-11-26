using System;
using System.Linq;
using System.Reflection;
using FluentAssertions;
using Reeve.Workloads.Providers.Docker;
using Xunit;

namespace Reeve.Workloads.Providers.Docker.Tests;

public sealed class SharedNetworkNameTests
{
    private const string MethodName = "ResolveNetworkName";

    public static TheoryData<string?, string> Cases() => new()
    {
        { null,                 DockerProviderConstants.DefaultNetworkName },
        { "",                   DockerProviderConstants.DefaultNetworkName },
        { "   ",                DockerProviderConstants.DefaultNetworkName },
        { "\t\n",               DockerProviderConstants.DefaultNetworkName },
        { "operator-bridge",    "operator-bridge" },
        { "  operator-bridge ", "operator-bridge" },
        { "iot-macvlan",        "iot-macvlan" },
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public void The_manager_resolves_the_shared_network_name(string? networkName, string expected)
    {
        var method = typeof(DockerNetworkManager)
            .GetMethods(BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Public | BindingFlags.DeclaredOnly)
            .SingleOrDefault(m =>
                m.Name == MethodName
                && m.ReturnType == typeof(string)
                && m.GetParameters() is [{ } only]
                && only.ParameterType == typeof(DockerProviderSettings));

        method.Should().NotBeNull(
            "DockerNetworkManager must still declare a static string {0}(DockerProviderSettings); a rename is a "
            + "change this file has to follow, not a behaviour change",
            MethodName);

        method!.Invoke(null, [new DockerProviderSettings { NetworkName = networkName }]).Should().Be(expected);
    }
}
