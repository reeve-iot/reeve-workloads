using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Docker.DotNet;
using Docker.DotNet.Models;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Reeve.Workloads.Abstractions;
using Reeve.Workloads.Abstractions.Contracts;
using Reeve.Workloads.Abstractions.Contracts.Deployment;
using Reeve.Workloads.Abstractions.Domain;
using Reeve.Workloads.Abstractions.Domain.DesiredSpec;
using Reeve.Workloads.Abstractions.Domain.Entities;
using Reeve.Workloads.Abstractions.Domain.Network;
using Reeve.Workloads.Abstractions.Network;
using Reeve.Workloads.Core.Network;
using Reeve.Workloads.Domain;
using Reeve.Workloads.Domain.Network;
using Reeve.Workloads.Providers.Docker;
using Reeve.Workloads.Providers.Kubernetes;
using Xunit.Abstractions;

using Reeve.Workloads.Abstractions.Providers;

using Reeve.Workloads.Core.Providers;

namespace Reeve.Workloads.Providers.Docker.IntegrationTests;

[Collection(DockerIntegrationCollection.Name)]
public sealed class DockerNetworkTests : IAsyncLifetime
{
    private readonly DockerDaemonFixture _docker;
    private readonly DockerNetworkManager _sut = new();

    private readonly List<string> _networksToRemove = new();

    private readonly List<string> _containersToRemove = new();

    private readonly ITestOutputHelper _output;

    public DockerNetworkTests(DockerDaemonFixture docker, ITestOutputHelper output)
    {
        _docker = docker;
        _output = output;
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        if (_docker.Client is not { } client)
        {
            return;
        }

        foreach (var id in _containersToRemove)
        {
            try
            {
                await client.Containers.RemoveContainerAsync(
                    id, new ContainerRemoveParameters { Force = true }, CancellationToken.None);
            }
            catch (DockerApiException)
            {
            }
        }

        foreach (var name in _networksToRemove)
        {
            await _docker.RemoveNetworkAsync(name);
        }
    }

    [SkippableFact]
    public async Task EnsureSharedNetwork_creates_the_bridge_when_absent_with_the_managed_by_label()
    {
        _docker.EnsureAvailable();
        var client = _docker.Client!;

        var networkName = UniqueNetworkName("create");
        var settings = SettingsWithNetwork(networkName);

        (await ExactNetworkAsync(client, networkName)).Should().BeNull("the unique network must not pre-exist");

        var result = await _sut.EnsureSharedNetworkAsync(client, settings, CancellationToken.None);

        result.Succeeded.Should().BeTrue();
        result.FailureKind.Should().Be(DockerFailureKind.None);
        result.NetworkName.Should().Be(networkName, "the resolved shared network name is returned");

        var created = await ExactNetworkAsync(client, networkName);
        created.Should().NotBeNull("the manager must have created the network");
        created!.Driver.Should().Be("bridge", "the shared workload network is a user-defined bridge");
        created.Labels.Should().Contain(
            new KeyValuePair<string, string>(
                DockerProviderConstants.ManagedByLabelKey,
                DockerProviderConstants.ManagedByLabelValue),
            "the shared network carries the reeve managed-by ownership marker");
    }

    [SkippableFact]
    public async Task EnsureSharedNetwork_is_idempotent_and_never_removes_the_network()
    {
        _docker.EnsureAvailable();
        var client = _docker.Client!;

        var networkName = UniqueNetworkName("idem");
        var settings = SettingsWithNetwork(networkName);

        var first = await _sut.EnsureSharedNetworkAsync(client, settings, CancellationToken.None);
        first.Succeeded.Should().BeTrue();

        var afterFirst = await ExactNetworkAsync(client, networkName);
        afterFirst.Should().NotBeNull();
        var originalId = afterFirst!.ID;

        var second = await _sut.EnsureSharedNetworkAsync(client, settings, CancellationToken.None);
        second.Succeeded.Should().BeTrue("an existing shared network is reused, not an error");
        second.NetworkName.Should().Be(networkName);

        var matches = await AllNetworksNamed(client, networkName);
        matches.Should().ContainSingle("the network is reused, never duplicated")
            .Which.ID.Should().Be(originalId, "the SAME network survives — it is never removed and recreated");
    }

    [SkippableFact]
    public async Task EnsureSharedNetwork_defaults_to_the_operator_supplied_name_verbatim()
    {
        _docker.EnsureAvailable();
        var client = _docker.Client!;

        var networkName = UniqueNetworkName("named");
        var settings = SettingsWithNetwork(networkName);

        var result = await _sut.EnsureSharedNetworkAsync(client, settings, CancellationToken.None);

        result.NetworkName.Should().Be(networkName);
        (await ExactNetworkAsync(client, networkName)).Should().NotBeNull();
    }

    [Fact]
    public void RenderNetworking_hostNetwork_yields_NetworkMode_host()
    {
        var spec = Spec(network: new ResolvedNetwork
        {
            HostNetwork = true,
            Ports = new[] { Port("api", 8080, "tcp", hostPort: 8080) },
        });

        var hostConfig = _sut.RenderNetworking(spec, "reeve-workloads").HostConfig;

        hostConfig.NetworkMode.Should().Be("host", "hostNetwork shares the host stack");
        (hostConfig.PortBindings ?? new Dictionary<string, IList<PortBinding>>())
            .Should().BeEmpty("host networking has no per-port publishing");
    }

    [Fact]
    public void RenderNetworking_without_hostNetwork_attaches_to_the_shared_network()
    {
        var spec = Spec(network: new ResolvedNetwork { HostNetwork = false });

        var hostConfig = _sut.RenderNetworking(spec, "reeve-workloads").HostConfig;

        hostConfig.NetworkMode.Should().Be("reeve-workloads", "a non-host workload joins the shared network");
    }

    [Fact]
    public void RenderNetworking_publishes_a_tcp_and_a_udp_hostPort_with_exact_keys_udp_not_downgraded()
    {
        var spec = Spec(network: new ResolvedNetwork
        {
            HostNetwork = false,
            Ports = new[]
            {
                Port("http", 8080, "tcp", hostPort: 18080),
                Port("mdns", 5353, "udp", hostPort: 5353),
            },
        });

        var hostConfig = _sut.RenderNetworking(spec, "reeve-workloads").HostConfig;

        hostConfig.PortBindings.Should().NotBeNull();
        hostConfig.PortBindings.Keys.Should().BeEquivalentTo(new[] { "8080/tcp", "5353/udp" });

        hostConfig.PortBindings["8080/tcp"].Should().ContainSingle()
            .Which.HostPort.Should().Be("18080");

        hostConfig.PortBindings.Should().NotContainKey("5353/tcp", "a UDP port is not downgraded to tcp");
        hostConfig.PortBindings["5353/udp"].Should().ContainSingle()
            .Which.HostPort.Should().Be("5353");
    }

    [Fact]
    public void RenderNetworking_defaults_absent_protocol_to_tcp()
    {
        var spec = Spec(network: new ResolvedNetwork
        {
            HostNetwork = false,
            Ports = new[] { Port("api", 9000, protocol: "", hostPort: 9000) },
        });

        var hostConfig = _sut.RenderNetworking(spec, "reeve-workloads").HostConfig;

        hostConfig.PortBindings.Keys.Should().BeEquivalentTo(new[] { "9000/tcp" },
            "an absent protocol defaults to tcp");
    }

    [Fact]
    public void RenderNetworking_does_not_publish_a_port_without_a_hostPort()
    {
        var spec = Spec(network: new ResolvedNetwork
        {
            HostNetwork = false,
            Ports = new[]
            {
                Port("cluster-only", 8080, "tcp", hostPort: null),
                Port("published", 9090, "tcp", hostPort: 19090),
            },
        });

        var hostConfig = _sut.RenderNetworking(spec, "reeve-workloads").HostConfig;

        hostConfig.PortBindings.Keys.Should().BeEquivalentTo(new[] { "9090/tcp" });
    }

    [Fact]
    public void RenderNetworking_with_no_ports_leaves_PortBindings_unset()
    {
        var spec = Spec(network: new ResolvedNetwork { HostNetwork = false });

        var hostConfig = _sut.RenderNetworking(spec, "reeve-workloads").HostConfig;

        (hostConfig.PortBindings ?? new Dictionary<string, IList<PortBinding>>())
            .Should().BeEmpty("no published ports ⇒ no PortBindings");
    }

    [Fact]
    public void RenderNetworking_mdns_or_ipv6_without_hostNetwork_render_nothing_special()
    {
        var spec = Spec(network: new ResolvedNetwork
        {
            HostNetwork = false,
            Mdns = true,
            Ipv6Required = true,
        });

        var hostConfig = _sut.RenderNetworking(spec, "reeve-workloads").HostConfig;

        hostConfig.NetworkMode.Should().Be("reeve-workloads");
        (hostConfig.PortBindings ?? new Dictionary<string, IList<PortBinding>>()).Should().BeEmpty();
    }

    [Fact]
    public void The_DHCP_pool_disjointness_check_detects_an_overlap_with_the_configured_range()
    {
        const string range = "198.51.100.240/30";

        DockerDaemonFixture.TryParseIpv4RangeBounds(range, out var first, out var last)
            .Should().BeTrue("a CIDR range must parse");
        (last - first + 1).Should().Be(4, "a /30 is four addresses");

        DockerDaemonFixture.IsRangeOutsideDhcpPool(range, "198.51.100.50-198.51.100.199").Should().BeTrue();
        DockerDaemonFixture.IsRangeOutsideDhcpPool(range, "198.51.100.0/25").Should().BeTrue();

        DockerDaemonFixture.IsRangeOutsideDhcpPool(range, "198.51.100.100-198.51.100.250")
            .Should().BeFalse("a pool overlapping the range must stop the run");
        DockerDaemonFixture.IsRangeOutsideDhcpPool(range, "198.51.100.0/24")
            .Should().BeFalse("a whole-/24 scope contains the range");
        DockerDaemonFixture.IsRangeOutsideDhcpPool(range, "198.51.100.240")
            .Should().BeFalse("a single lease inside the range is still a collision");
        DockerDaemonFixture.IsRangeOutsideDhcpPool(range, "not-a-pool")
            .Should().BeFalse("an unreadable scope is not evidence that the range is free");
        DockerDaemonFixture.IsRangeOutsideDhcpPool(range, null)
            .Should().BeFalse("an absent scope is not evidence that the range is free");
        DockerDaemonFixture.IsRangeOutsideDhcpPool(null, "198.51.100.0/25")
            .Should().BeFalse("an unreadable range is not evidence that it is free");
    }

    [Fact]
    public void Macvlan_live_cases_report_Skipped_not_Failed_when_the_parent_interface_or_the_ip_range_is_unconfigured()
    {
        const string subnet = "198.51.100.0/24";
        const string ipRange = "198.51.100.240/30";

        DockerDaemonFixture.DescribeMacvlanConfigurationGap(
                nic: "eth1.100", configuredParentInterface: null, configuredSubnet: subnet,
                configuredIpRange: ipRange)
            .Should().NotBeNull("an unconfigured parent interface SKIPS the case")
            .And.Contain(DockerDaemonFixture.MacvlanParentEnvironmentVariable,
                "the reason names the variable the operator has to set");

        DockerDaemonFixture.DescribeMacvlanConfigurationGap(
                nic: null, configuredParentInterface: "eth1.100", configuredSubnet: subnet,
                configuredIpRange: null)
            .Should().NotBeNull("an unconfigured ip range SKIPS the case")
            .And.Contain(DockerDaemonFixture.MacvlanIpRangeEnvironmentVariable);

        DockerDaemonFixture.DescribeMacvlanConfigurationGap(
                nic: null, configuredParentInterface: "eth1.100", configuredSubnet: null,
                configuredIpRange: ipRange)
            .Should().NotBeNull("an unconfigured subnet SKIPS the case")
            .And.Contain(DockerDaemonFixture.MacvlanSubnetEnvironmentVariable);

        DockerDaemonFixture.DescribeMacvlanConfigurationGap(
                nic: "eth0.100", configuredParentInterface: "eth1.100", configuredSubnet: subnet,
                configuredIpRange: ipRange)
            .Should().NotBeNull("a mismatched parent interface SKIPS rather than substituting a different segment");

        DockerDaemonFixture.DescribeMacvlanConfigurationGap(
                nic: "eth1.100", configuredParentInterface: "eth1.100", configuredSubnet: subnet,
                configuredIpRange: ipRange)
            .Should().BeNull("a complete configuration must RUN, or the live legs are unreachable by construction");
    }

    [SkippableFact]
    public async Task Live_enumeration_lists_every_named_network_including_the_shared_bridge_reeve_labelled_and_self_attached_ones()
    {
        var macvlan = _docker.EnsureMacvlanParent();
        var client = _docker.Client!;
        await EnsureProbeImageAsync(client);

        var macvlanId = await _docker.CreateLabeledMacvlanNetworkAsync(macvlan, "enum");
        var macvlanName = (await client.Networks.InspectNetworkAsync(macvlanId, CancellationToken.None)).Name;

        var sharedName = UniqueNetworkName("shared");
        var settings = SettingsWithNetwork(sharedName);
        (await _sut.EnsureSharedNetworkAsync(client, settings, CancellationToken.None))
            .Succeeded.Should().BeTrue();

        var reeveLabelledName = UniqueNetworkName("reeveowned");
        await client.Networks.CreateNetworkAsync(
            new NetworksCreateParameters
            {
                Name = reeveLabelledName,
                Driver = "bridge",
                Labels = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    [DockerProviderConstants.ManagedByLabelKey] = DockerProviderConstants.ManagedByLabelValue,
                },
            },
            CancellationToken.None);

        var backendNetworkId = await _docker.CreateLabeledNetworkAsync("selfatt");
        var backendNetworkName = (await client.Networks.InspectNetworkAsync(backendNetworkId, CancellationToken.None)).Name;
        var backendContainerId = await CreateHarnessContainerAsync(
            client, "backendstandin", ["sleep", "3600"], networkMode: backendNetworkName);
        await client.Containers.StartContainerAsync(backendContainerId, new ContainerStartParameters(), CancellationToken.None);

        var backendInspect = await client.Containers.InspectContainerAsync(backendContainerId, CancellationToken.None);
        var selfAttached = (backendInspect.NetworkSettings?.Networks?.Keys ?? [])
            .ToHashSet(StringComparer.Ordinal);
        selfAttached.Should().Contain(backendNetworkName,
            "the stand-in backend really is attached to that network, which is what makes the row live");

        var listed = await _sut.ListAttachableNetworksAsync(client, settings, CancellationToken.None);
        listed.Succeeded.Should().BeTrue(listed.Message);

        var names = listed.Networks.Select(n => n.Name).ToList();
        names.Should().Contain([macvlanName, sharedName, reeveLabelledName, backendNetworkName],
            "every named network lists: an attachment is judged by its name and existence only");
        names.Should().Contain(["host", "none"]);
    }

    [SkippableFact]
    public async Task Live_operatorNetwork_workload_has_both_endpoints_a_bridge_default_route_a_reachable_gateway_a_published_hostPort_and_survives_teardown()
    {
        var macvlan = _docker.EnsureMacvlanParent();
        var client = _docker.Client!;
        await EnsureProbeImageAsync(client);

        var sharedName = UniqueNetworkName("wl");
        var settings = SettingsWithNetwork(sharedName);
        (await _sut.EnsureSharedNetworkAsync(client, settings, CancellationToken.None)).Succeeded.Should().BeTrue();

        var macvlanId = await _docker.CreateLabeledMacvlanNetworkAsync(macvlan, "wl");
        var macvlanName = (await client.Networks.InspectNetworkAsync(macvlanId, CancellationToken.None)).Name;

        string.CompareOrdinal(macvlanName, sharedName).Should().BeNegative(
            $"the attachment name ('{macvlanName}') must sort BEFORE the shared bridge ('{sharedName}') for this case "
            + "to exercise the gateway election it exists to pin");

        var gatewayContainerId = await CreateHarnessContainerAsync(
            client,
            "gateway",
            ["sh", "-c", $"echo {GatewayBody} > /tmp/index.html; httpd -f -p {GatewayPort} -h /tmp"],
            networkMode: sharedName);
        await client.Containers.StartContainerAsync(gatewayContainerId, new ContainerStartParameters(), CancellationToken.None);

        var gatewayInspect = await client.Containers.InspectContainerAsync(gatewayContainerId, CancellationToken.None);
        var gatewayAddress = gatewayInspect.NetworkSettings?.Networks?[sharedName].IPAddress;
        gatewayAddress.Should().NotBeNullOrWhiteSpace("the gateway stand-in must have a bridge address to be reached at");
        var gatewayUrl = $"http://{gatewayAddress}:{GatewayPort}/";

        var hostPort = FreeHostPort();
        var spec = Spec(
            network: new ResolvedNetwork
            {
                HostNetwork = false,
                Mode = WorkloadNetworkMode.OperatorNetwork,
                Attachments = [macvlanName],
                Ports = [Port("http", GatewayPort, "tcp", hostPort)],
            },
            image: ProbeImage);

        var plan = _sut.RenderNetworking(spec, sharedName);
        plan.HostConfig.NetworkMode.Should().Be(sharedName, "the shared bridge is retained ALWAYS");
        plan.Attachments.Should().Equal([macvlanName], "the attachments are the connect-time half, in declared order");

        var containerName = $"reeve-wl-it-t45-{_docker.RunId}";
        var parameters = new DockerContainerManager().BuildContainerParameters(new DockerContainerRenderContext
        {
            Spec = spec,
            ContainerName = containerName,
            SharedNetworkName = sharedName,
            Privilege = new DockerPrivilegeRenderer().RenderPrivileges(spec),
            Resource = new DockerResourceRenderer().RenderResources(spec),
            NetworkHostConfig = plan.HostConfig,
        });

        parameters.Cmd =
        [
            "sh", "-c",
            $"echo {GatewayBody} > /tmp/index.html; "
            + $"echo '{RouteMarker}'; ip -4 route; "
            + $"echo '{GatewayMarker}'; wget -q -T 5 -O - {gatewayUrl} || echo GATEWAY_UNREACHABLE; "
            + $"echo '{DoneMarker}'; httpd -f -p {GatewayPort} -h /tmp",
        ];

        var containerManager = new DockerContainerManager();
        var created = await containerManager.CreateAndStartAsync(
            client, parameters, plan.Attachments, CancellationToken.None);
        if (created.Container is not null)
        {
            _containersToRemove.Add(created.Container.Id);
        }

        created.Succeeded.Should().BeTrue(created.Message);
        var containerId = created.Container!.Id;

        var inspect = await client.Containers.InspectContainerAsync(containerId, CancellationToken.None);
        var endpoints = (inspect.NetworkSettings?.Networks?.Keys ?? []).ToList();
        endpoints.Should().Contain(sharedName, "the Reeve shared bridge endpoint — how the workload reaches the gateway");
        endpoints.Should().Contain(macvlanName, "the operator attachment endpoint");

        var macvlanInspect = await client.Networks.InspectNetworkAsync(macvlanId, CancellationToken.None);
        var ipam = macvlanInspect.IPAM?.Config?.FirstOrDefault();
        var parentOption = macvlanInspect.Options is { } opts && opts.TryGetValue("parent", out var pv) ? pv : "(none)";
        var labelText = macvlanInspect.Labels is { } lbls
            ? string.Join(",", lbls.Select(kv => $"{kv.Key}={kv.Value}"))
            : "(none)";
        _output.WriteLine($"macvlan network: name={macvlanName} driver={macvlanInspect.Driver} "
            + $"parent={parentOption} "
            + $"subnet={ipam?.Subnet} ipRange={ipam?.IPRange} gateway='{ipam?.Gateway}' "
            + $"labels=[{labelText}]");
        _output.WriteLine($"shared bridge:   name={sharedName}");
        _output.WriteLine($"name ordering (docker's gateway election tie-break): "
            + $"'{macvlanName}' < '{sharedName}' = {string.CompareOrdinal(macvlanName, sharedName) < 0}");
        _output.WriteLine($"container {containerName} endpoints in NetworkSettings.Networks: "
            + $"[{string.Join(", ", endpoints)}]");

        (await WaitForLogSubstringAsync(client, containerId, DoneMarker, TimeSpan.FromSeconds(30)))
            .Should().BeTrue("the container must reach its own DONE marker before its output is read");
        var output = await ReadAllLogsAsync(client, containerId, CancellationToken.None);

        var routeSection = Section(output, RouteMarker, GatewayMarker);
        var defaultRoute = routeSection
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault(l => l.StartsWith("default", StringComparison.Ordinal));
        defaultRoute.Should().NotBeNull(
            "attaching an operator network must not cost the workload its default route. The attachment here is "
            + "gateway-less AND sorts first, so with equal gw-priority it wins docker's default-gateway election and "
            + "installs nothing — the container is left with only link-scope routes. "
            + $"Route section read from inside the container was:\n{routeSection}\nFull container output:\n{output}");
        var bridgeAddress = inspect.NetworkSettings?.Networks?[sharedName].IPAddress;
        bridgeAddress.Should().NotBeNullOrWhiteSpace("the bridge endpoint must have an address to be identified by");

        var bridgeInterface = routeSection
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(l => l.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
            .Where(f => Array.IndexOf(f, "src") is var s && s >= 0 && s + 1 < f.Length && f[s + 1] == bridgeAddress)
            .Select(f => Array.IndexOf(f, "dev") is var d && d >= 0 && d + 1 < f.Length ? f[d + 1] : null)
            .FirstOrDefault(name => name is not null);

        bridgeInterface.Should().NotBeNull(
            $"the route table must show the bridge address {bridgeAddress} on some interface.\n{routeSection}");

        defaultRoute.Should().Contain($"dev {bridgeInterface}",
            "the default route egresses the shared BRIDGE endpoint, never the operator attachment");

        _output.WriteLine("--- ip -4 route, read from INSIDE the workload container ---");
        _output.WriteLine(routeSection.Trim());
        _output.WriteLine($"bridge endpoint address {bridgeAddress} resolves to interface '{bridgeInterface}'; "
            + $"the default route is '{defaultRoute}' — the question is whether it egresses the BRIDGE, not a literal eth0");

        Section(output, GatewayMarker, DoneMarker).Should().Contain(GatewayBody,
            "the backend gateway URL is still reachable from inside the container with the macvlan attached");

        _output.WriteLine($"--- GET {gatewayUrl} from inside the workload (the backend gateway stand-in) ---");
        _output.WriteLine(Section(output, GatewayMarker, DoneMarker).Trim());

        inspect.NetworkSettings?.Ports.Should().ContainKey($"{GatewayPort}/tcp");
        using (var socket = new System.Net.Sockets.TcpClient())
        {
            var connect = socket.ConnectAsync(System.Net.IPAddress.Loopback, hostPort);
            (await Task.WhenAny(connect, Task.Delay(TimeSpan.FromSeconds(10)))).Should().Be(connect,
                "the published hostPort must accept a connection from the host");
            await connect;
        }

        var portText = inspect.NetworkSettings?.Ports is { } ports
            ? string.Join(", ", ports.Select(kv =>
                $"{kv.Key} -> {(kv.Value is null ? "(unbound)" : string.Join("/", kv.Value.Select(b => $"{b.HostIP}:{b.HostPort}")))}"))
            : "(none)";
        _output.WriteLine($"published ports: [{portText}]"
            + $"; a TCP connect to 127.0.0.1:{hostPort} from the host succeeded");

        (await containerManager.StopContainerAsync(client, containerId, CancellationToken.None))
            .Succeeded.Should().BeTrue();
        (await containerManager.RemoveContainerAsync(client, containerId, CancellationToken.None))
            .Succeeded.Should().BeTrue();

        (await ExactNetworkAsync(client, macvlanName))
            .Should().NotBeNull("Reeve never removes an operator attachment network, not even on teardown");
        (await ExactNetworkAsync(client, sharedName))
            .Should().NotBeNull("the shared bridge is shared across workloads and outlives any one of them");

        _output.WriteLine($"after stop+remove of {containerName}: attachment '{macvlanName}' still present = "
            + $"{await ExactNetworkAsync(client, macvlanName) is not null}, shared bridge '{sharedName}' still "
            + $"present = {await ExactNetworkAsync(client, sharedName) is not null}");
    }

    private const int GatewayPort = 8080;
    private const string GatewayBody = "reeve-gateway-ok";
    private const string RouteMarker = "###NETWORK-TEST-ROUTE###";
    private const string GatewayMarker = "###NETWORK-TEST-GATEWAY###";
    private const string DoneMarker = "###NETWORK-TEST-DONE###";

    private async Task<string> CreateHarnessContainerAsync(
        IDockerClient client,
        string nameHint,
        IList<string> command,
        string networkMode)
    {
        var response = await client.Containers.CreateContainerAsync(
            new CreateContainerParameters
            {
                Image = ProbeImage,
                Name = $"reeve-it-t45-{nameHint}-{_docker.RunId}-{Guid.NewGuid():N}"[..40],
                Cmd = command,
                Labels = _docker.BuildLabels(),
                HostConfig = new HostConfig { NetworkMode = networkMode },
            },
            CancellationToken.None);

        _containersToRemove.Add(response.ID);

        return response.ID;
    }

    private const string ProbeImage = "busybox:1.36";

    private static async Task EnsureProbeImageAsync(IDockerClient client)
    {
        try
        {
            await client.Images.InspectImageAsync(ProbeImage, CancellationToken.None);
            return;
        }
        catch (DockerApiException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
        }

        var separator = ProbeImage.LastIndexOf(':');
        await client.Images.CreateImageAsync(
            new ImagesCreateParameters
            {
                FromImage = separator > 0 ? ProbeImage[..separator] : ProbeImage,
                Tag = separator > 0 ? ProbeImage[(separator + 1)..] : "latest",
            },
            authConfig: null,
            progress: new Progress<JSONMessage>(),
            CancellationToken.None);
    }

    private static int FreeHostPort()
    {
        var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static string Section(string output, string startMarker, string endMarker)
    {
        var start = output.IndexOf(startMarker, StringComparison.Ordinal);
        if (start < 0)
        {
            return string.Empty;
        }

        start += startMarker.Length;
        var end = output.IndexOf(endMarker, start, StringComparison.Ordinal);
        return end < 0 ? output[start..] : output[start..end];
    }

    private static async Task<bool> WaitForLogSubstringAsync(
        IDockerClient client, string containerId, string expected, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (true)
        {
            if ((await ReadAllLogsAsync(client, containerId, CancellationToken.None))
                .Contains(expected, StringComparison.Ordinal))
            {
                return true;
            }

            if (DateTime.UtcNow >= deadline)
            {
                return false;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(200));
        }
    }

    private static async Task<string> ReadAllLogsAsync(
        IDockerClient client, string containerId, CancellationToken cancellationToken)
    {
        using var stream = await client.Containers
            .GetContainerLogsAsync(
                containerId,
                new ContainerLogsParameters
                {
                    ShowStdout = true,
                    ShowStderr = true,
                    Follow = false,
                    Timestamps = false,
                    Tail = "all",
                },
                cancellationToken)
            .ConfigureAwait(false);

        using var merged = new MemoryStream();
        var buffer = new byte[8192];
        while (true)
        {
            var read = await stream.ReadOutputAsync(buffer, 0, buffer.Length, cancellationToken).ConfigureAwait(false);
            if (read.EOF)
            {
                break;
            }

            if (read.Count > 0)
            {
                merged.Write(buffer, 0, read.Count);
            }
        }

        return System.Text.Encoding.UTF8.GetString(merged.ToArray());
    }

    private string UniqueNetworkName(string hint)
    {
        var name = $"reeve-wl-it-{hint}-{_docker.RunId}-{Guid.NewGuid():N}"[..40];
        _networksToRemove.Add(name);
        return name;
    }

    private static DockerProviderSettings SettingsWithNetwork(string networkName) =>
        new() { NetworkName = networkName };

    private static async Task<NetworkResponse?> ExactNetworkAsync(IDockerClient client, string name) =>
        (await AllNetworksNamed(client, name)).SingleOrDefault();

    private static async Task<IReadOnlyList<NetworkResponse>> AllNetworksNamed(IDockerClient client, string name)
    {
        var networks = await client.Networks.ListNetworksAsync(
            new NetworksListParameters
            {
                Filters = new Dictionary<string, IDictionary<string, bool>>
                {
                    ["name"] = new Dictionary<string, bool> { [name] = true },
                },
            });

        return networks.Where(n => string.Equals(n.Name, name, StringComparison.Ordinal)).ToList();
    }

    private static ResolvedPort Port(string name, int port, string protocol, int? hostPort) =>
        new() { Name = name, Port = port, Protocol = protocol, HostPort = hostPort };

    private static WorkloadDesiredSpec Spec(ResolvedNetwork? network = null, string? image = null)
    {
        const string specHash = "abc123def456";
        var instanceId = Guid.Parse("11111111-2222-3333-4444-555555555555");
        var labels = DefaultWorkloadResourceNamer.Instance.Labels(new WorkloadLabelSubject
                     {
                         InstanceId = instanceId,
                         InstallationId = "install-1",
                         SpecHash = specHash,
                     });

        return new WorkloadDesiredSpec
        {
            GroupInstanceId = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee"),
            GroupId = "com.reeve.matter",
            WorkloadId = "matter",
            InstanceId = instanceId,
            Inst8 = "11111111",
            Provider = DockerProvider.Id,
            ResourceName = "reeve-wl-com-reeve-matter-matter-11111111",
            SecretName = "reeve-wl-com-reeve-matter-matter-11111111",
            Labels = labels,
            Image = image
                ?? "ghcr.io/reeve/matter@sha256:1111111111111111111111111111111111111111111111111111111111111111",
            Kind = WorkloadKind.SingleNode,
            Network = network,
            Health = new WorkloadHealth { Mode = WorkloadHealthMode.Container },
            SpecHash = specHash,
        };
    }
}
