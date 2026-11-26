using Docker.DotNet;
using Docker.DotNet.Models;
using DotNet.Testcontainers.Configurations;

namespace Reeve.Workloads.Providers.Docker.IntegrationTests;

public sealed class DockerDaemonFixture : IAsyncLifetime
{
    public const string OwnerLabelName = "com.reeve.test.harness";

    public const string OwnerLabelValue = "docker-integration";

    public const string RunLabelName = "com.reeve.test.harness.run";

    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan TeardownTimeout = TimeSpan.FromSeconds(60);

    private readonly List<string> _createdContainerIds = new();
    private readonly List<string> _createdNetworkIds = new();
    private readonly List<string> _createdVolumeNames = new();

    public string RunId { get; } = Guid.NewGuid().ToString("N")[..12];

    public IDockerClient? Client { get; private set; }

    public bool IsDockerAvailable { get; private set; }

    public string SkipReason { get; private set; } = string.Empty;

    public string ServerVersion { get; private set; } = string.Empty;

    public string ApiVersion { get; private set; } = string.Empty;

    public const string MacvlanParentEnvironmentVariable = "REEVE_E2E_MACVLAN_PARENT";

    public const string MacvlanSubnetEnvironmentVariable = "REEVE_E2E_MACVLAN_SUBNET";

    public const string MacvlanIpRangeEnvironmentVariable = "REEVE_E2E_MACVLAN_IP_RANGE";

    public const string MacvlanDhcpPoolEnvironmentVariable = "REEVE_E2E_MACVLAN_DHCP_POOL";

    public const string MacvlanGatewayEnvironmentVariable = "REEVE_E2E_MACVLAN_GATEWAY";

    public sealed record MacvlanFixtureConfiguration(
        string ParentInterface,
        string Subnet,
        string IpRange,
        string? DhcpPool);

    public MacvlanFixtureConfiguration EnsureMacvlanParent(string? nic = null)
    {
        EnsureAvailable();

        var parentInterface = ReadEnvironment(MacvlanParentEnvironmentVariable);
        var subnet = ReadEnvironment(MacvlanSubnetEnvironmentVariable);
        var ipRange = ReadEnvironment(MacvlanIpRangeEnvironmentVariable);

        var gap = DescribeMacvlanConfigurationGap(nic, parentInterface, subnet, ipRange);
        Skip.If(gap is not null, gap ?? string.Empty);

        return new MacvlanFixtureConfiguration(
            parentInterface!,
            subnet!,
            ipRange!,
            ReadEnvironment(MacvlanDhcpPoolEnvironmentVariable));
    }

    public static string? DescribeMacvlanConfigurationGap(
        string? nic,
        string? configuredParentInterface,
        string? configuredSubnet,
        string? configuredIpRange)
    {
        if (string.IsNullOrWhiteSpace(configuredParentInterface))
        {
            return $"No macvlan parent interface is configured ({MacvlanParentEnvironmentVariable} is unset). "
                + "There is deliberately no default: guessing one would create a macvlan on whatever interface the "
                + "runner happens to have, which on the operator's workstation is their production LAN.";
        }

        if (!string.IsNullOrWhiteSpace(nic)
            && !string.Equals(nic.Trim(), configuredParentInterface.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            return $"This case asks for parent interface '{nic.Trim()}' but "
                + $"{MacvlanParentEnvironmentVariable} names '{configuredParentInterface.Trim()}'. Skipping rather "
                + "than substituting: the two are different network segments.";
        }

        if (string.IsNullOrWhiteSpace(configuredSubnet))
        {
            return $"No macvlan subnet is configured ({MacvlanSubnetEnvironmentVariable} is unset). Docker's macvlan "
                + "driver requires --subnet and its IPAM has no DHCP driver, so there is nothing safe to infer.";
        }

        if (string.IsNullOrWhiteSpace(configuredIpRange))
        {
            return $"No macvlan --ip-range is configured ({MacvlanIpRangeEnvironmentVariable} is unset). "
                + "Pick a range outside the segment's DHCP scope: without one Docker's IPAM could hand out an "
                + "address a device on the segment already leases.";
        }

        return null;
    }

    public static bool IsRangeOutsideDhcpPool(string? ipRange, string? dhcpPool)
    {
        if (!TryParseIpv4RangeBounds(ipRange, out var rangeFirst, out var rangeLast)
            || !TryParseIpv4RangeBounds(dhcpPool, out var poolFirst, out var poolLast))
        {
            return false;
        }

        return rangeLast < poolFirst || poolLast < rangeFirst;
    }

    public static bool TryParseIpv4RangeBounds(string? range, out uint first, out uint last)
    {
        first = 0;
        last = 0;
        if (string.IsNullOrWhiteSpace(range))
        {
            return false;
        }

        var text = range.Trim();

        var dash = text.IndexOf('-');
        if (dash > 0)
        {
            if (!TryParseIpv4(text[..dash], out first) || !TryParseIpv4(text[(dash + 1)..], out last))
            {
                return false;
            }

            return first <= last;
        }

        var slash = text.IndexOf('/');
        if (slash < 0)
        {
            if (!TryParseIpv4(text, out first))
            {
                return false;
            }

            last = first;
            return true;
        }

        if (!TryParseIpv4(text[..slash], out var address)
            || !int.TryParse(text[(slash + 1)..].Trim(), out var prefix)
            || prefix is < 0 or > 32)
        {
            return false;
        }

        var mask = prefix == 0 ? 0u : uint.MaxValue << (32 - prefix);
        first = address & mask;
        last = first | ~mask;
        return true;
    }

    private static bool TryParseIpv4(string? text, out uint value)
    {
        value = 0;
        if (string.IsNullOrWhiteSpace(text)
            || !System.Net.IPAddress.TryParse(text.Trim(), out var address)
            || address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
        {
            return false;
        }

        var bytes = address.GetAddressBytes();
        value = ((uint)bytes[0] << 24) | ((uint)bytes[1] << 16) | ((uint)bytes[2] << 8) | bytes[3];
        return true;
    }

    private static string? ReadEnvironment(string name)
    {
        var value = Environment.GetEnvironmentVariable(name);
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    public async Task<string> CreateLabeledMacvlanNetworkAsync(
        MacvlanFixtureConfiguration configuration,
        string? nameHint = null,
        CancellationToken cancellationToken = default,
        string? gateway = null)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        if (configuration.DhcpPool is { } pool && !IsRangeOutsideDhcpPool(configuration.IpRange, pool))
        {
            throw new InvalidOperationException(
                $"Refusing to create a macvlan: the range {configuration.IpRange} ({MacvlanIpRangeEnvironmentVariable}) "
                + $"is not outside the DHCP pool '{pool}' ({MacvlanDhcpPoolEnvironmentVariable}).");
        }

        var client = RequireClient();

        foreach (var stale in await ListHarnessMacvlansOnSubnetAsync(
                     client, configuration.Subnet, cancellationToken).ConfigureAwait(false))
        {
            await RemoveNetworkAsync(stale, cancellationToken).ConfigureAwait(false);
            _createdNetworkIds.Remove(stale);
        }

        var response = await client.Networks.CreateNetworkAsync(
            new NetworksCreateParameters
            {
                Name = ResourceName("macvlan", nameHint),
                Driver = "macvlan",
                Labels = BuildLabels(),
                Options = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["parent"] = configuration.ParentInterface,
                },
                IPAM = new IPAM
                {
                    Driver = "default",
                    Config = new List<IPAMConfig>
                    {
                        new()
                        {
                            Subnet = configuration.Subnet,
                            IPRange = configuration.IpRange,

                            Gateway = gateway ?? string.Empty,
                        },
                    },
                },
            },
            cancellationToken).ConfigureAwait(false);

        _createdNetworkIds.Add(response.ID);
        return response.ID;
    }

    private static async Task<IReadOnlyList<string>> ListHarnessMacvlansOnSubnetAsync(
        IDockerClient client, string subnet, CancellationToken cancellationToken)
    {
        var networks = await client.Networks.ListNetworksAsync(
            new NetworksListParameters
            {
                Filters = new Dictionary<string, IDictionary<string, bool>>(StringComparer.Ordinal)
                {
                    ["label"] = new Dictionary<string, bool>(StringComparer.Ordinal)
                    {
                        [$"{OwnerLabelName}={OwnerLabelValue}"] = true,
                    },
                    ["driver"] = new Dictionary<string, bool>(StringComparer.Ordinal) { ["macvlan"] = true },
                },
            },
            cancellationToken).ConfigureAwait(false);

        return networks
            .Where(n => n.IPAM?.Config?.Any(
                c => string.Equals(c.Subnet, subnet, StringComparison.OrdinalIgnoreCase)) == true)
            .Select(n => n.ID)
            .ToList();
    }

    public async Task InitializeAsync()
    {
        IDockerClient? client = null;
        try
        {
            var auth = TestcontainersSettings.OS.DockerEndpointAuthConfig;
            var builder = auth.GetDockerClientBuilder(Guid.NewGuid());
            client = builder.Build();

            using var cts = new CancellationTokenSource(ProbeTimeout);
            var version = await client.System.GetVersionAsync(cts.Token).ConfigureAwait(false);

            ServerVersion = version.Version ?? string.Empty;
            ApiVersion = version.APIVersion ?? string.Empty;
            Client = client;
            IsDockerAvailable = true;
        }
        catch (Exception ex)
        {
            IsDockerAvailable = false;
            SkipReason = $"Docker daemon not available ({ex.GetType().Name}: {ex.Message}).";
            client?.Dispose();
            Client = null;
        }
    }

    public async Task DisposeAsync()
    {
        if (Client is { } client)
        {
            using var cts = new CancellationTokenSource(TeardownTimeout);
            await CleanupAsync(client, cts.Token).ConfigureAwait(false);
            client.Dispose();
            Client = null;
        }
    }

    public void EnsureAvailable() => Skip.IfNot(IsDockerAvailable, SkipReason);

    public async Task<string> CreateLabeledNetworkAsync(string? nameHint = null, CancellationToken cancellationToken = default)
    {
        var client = RequireClient();
        var response = await client.Networks.CreateNetworkAsync(
            new NetworksCreateParameters
            {
                Name = ResourceName("net", nameHint),
                Driver = "bridge",
                Labels = BuildLabels(),
            },
            cancellationToken).ConfigureAwait(false);

        _createdNetworkIds.Add(response.ID);
        return response.ID;
    }

    public async Task<string> CreateLabeledVolumeAsync(string? nameHint = null, CancellationToken cancellationToken = default)
    {
        var client = RequireClient();
        var response = await client.Volumes.CreateAsync(
            new VolumesCreateParameters
            {
                Name = ResourceName("vol", nameHint),
                Labels = BuildLabels(),
            },
            cancellationToken).ConfigureAwait(false);

        _createdVolumeNames.Add(response.Name);
        return response.Name;
    }

    public async Task<string> CreateLabeledContainerAsync(
        string image,
        IList<string>? command = null,
        string? nameHint = null,
        CancellationToken cancellationToken = default)
    {
        var client = RequireClient();
        var parameters = new CreateContainerParameters
        {
            Image = image,
            Name = ResourceName("ctr", nameHint),
            Labels = BuildLabels(),
        };

        if (command is not null)
        {
            parameters.Cmd = command;
        }

        var response = await client.Containers.CreateContainerAsync(
            parameters,
            cancellationToken).ConfigureAwait(false);

        _createdContainerIds.Add(response.ID);
        return response.ID;
    }

    public async Task<IReadOnlyList<NetworkResponse>> ListLabeledNetworksAsync(CancellationToken cancellationToken = default)
    {
        var client = RequireClient();
        var networks = await client.Networks.ListNetworksAsync(
            new NetworksListParameters { Filters = RunLabelFilter() },
            cancellationToken).ConfigureAwait(false);
        return networks.ToList();
    }

    public async Task<IReadOnlyList<VolumeResponse>> ListLabeledVolumesAsync(CancellationToken cancellationToken = default)
    {
        var client = RequireClient();
        var response = await client.Volumes.ListAsync(
            new VolumesListParameters { Filters = RunLabelFilter() },
            cancellationToken).ConfigureAwait(false);
        return (response.Volumes ?? new List<VolumeResponse>()).ToList();
    }

    public Task RemoveNetworkAsync(string id, CancellationToken cancellationToken = default) =>
        SafeAsync(() => RequireClient().Networks.DeleteNetworkAsync(id, cancellationToken));

    public Task RemoveVolumeAsync(string name, CancellationToken cancellationToken = default) =>
        SafeAsync(() => RequireClient().Volumes.RemoveAsync(name, force: true, cancellationToken));

    public IDictionary<string, string> BuildLabels() => new Dictionary<string, string>
    {
        [OwnerLabelName] = OwnerLabelValue,
        [RunLabelName] = RunId,
    };

    private IDockerClient RequireClient() =>
        Client ?? throw new InvalidOperationException(
            "Docker daemon is not available; gate the test on EnsureAvailable() before creating resources.");

    private int _resourceSeq;

    private string ResourceName(string kind, string? hint)
    {
        var suffix = string.IsNullOrWhiteSpace(hint) ? kind : $"{kind}-{hint}";
        var seq = System.Threading.Interlocked.Increment(ref _resourceSeq);
        return $"reeve-it-{suffix}-{RunId}-{seq}";
    }

    private IDictionary<string, IDictionary<string, bool>> RunLabelFilter() =>
        new Dictionary<string, IDictionary<string, bool>>
        {
            ["label"] = new Dictionary<string, bool> { [$"{RunLabelName}={RunId}"] = true },
        };

    private async Task CleanupAsync(IDockerClient client, CancellationToken cancellationToken)
    {
        var containers = await SafeListAsync(
            () => client.Containers.ListContainersAsync(
                new ContainersListParameters { All = true, Filters = RunLabelFilter() },
                cancellationToken),
            Array.Empty<ContainerListResponse>()).ConfigureAwait(false);

        foreach (var id in containers.Select(c => c.ID).Concat(_createdContainerIds).Distinct())
        {
            await SafeAsync(() => client.Containers.RemoveContainerAsync(
                id,
                new ContainerRemoveParameters { Force = true, RemoveVolumes = true },
                cancellationToken)).ConfigureAwait(false);
        }

        var networks = await SafeListAsync(
            () => client.Networks.ListNetworksAsync(new NetworksListParameters { Filters = RunLabelFilter() }, cancellationToken),
            Array.Empty<NetworkResponse>()).ConfigureAwait(false);

        foreach (var id in networks.Select(n => n.ID).Concat(_createdNetworkIds).Distinct())
        {
            await SafeAsync(() => client.Networks.DeleteNetworkAsync(id, cancellationToken)).ConfigureAwait(false);
        }

        var volumes = await SafeListAsync(
            async () => (await client.Volumes.ListAsync(new VolumesListParameters { Filters = RunLabelFilter() }, cancellationToken).ConfigureAwait(false)).Volumes,
            (IList<VolumeResponse>)Array.Empty<VolumeResponse>()).ConfigureAwait(false);

        foreach (var name in volumes.Select(v => v.Name).Concat(_createdVolumeNames).Distinct())
        {
            await SafeAsync(() => client.Volumes.RemoveAsync(name, force: true, cancellationToken)).ConfigureAwait(false);
        }
    }

    private static async Task SafeAsync(Func<Task> action)
    {
        try
        {
            await action().ConfigureAwait(false);
        }
        catch (DockerApiException)
        {
        }
        catch (OperationCanceledException)
        {
        }
    }

    private static async Task<T> SafeListAsync<T>(Func<Task<T>> list, T fallback)
    {
        try
        {
            return await list().ConfigureAwait(false);
        }
        catch (DockerApiException)
        {
            return fallback;
        }
        catch (OperationCanceledException)
        {
            return fallback;
        }
    }
}
