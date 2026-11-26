using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Docker.DotNet;
using Docker.DotNet.Models;
using Reeve.Workloads.Abstractions.Contracts;
using Reeve.Workloads.Abstractions.Domain.DesiredSpec;

namespace Reeve.Workloads.Providers.Docker;

internal sealed class DockerNetworkManager : IDockerNetworkManager
{
    public async Task<DockerNetworkEnsureResult> EnsureSharedNetworkAsync(
        IDockerClient client,
        DockerProviderSettings settings,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(client);

        var name = ResolveNetworkName(settings);

        try
        {
            if (await FindNetworkByExactNameAsync(client, name, cancellationToken).ConfigureAwait(false) is not null)
            {
                return DockerNetworkEnsureResult.Ok(name);
            }

            await client.Networks.CreateNetworkAsync(
                new NetworksCreateParameters
                {
                    Name = name,
                    Driver = "bridge",
                    Labels = new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        [DockerProviderConstants.ManagedByLabelKey] = DockerProviderConstants.ManagedByLabelValue,
                    },
                },
                cancellationToken).ConfigureAwait(false);

            return DockerNetworkEnsureResult.Ok(name);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (DockerApiException api) when (api.StatusCode == HttpStatusCode.Conflict)
        {
            return DockerNetworkEnsureResult.Ok(name);
        }
        catch (Exception ex)
        {
            return DockerNetworkEnsureResult.Fail(
                ClassifyFailure(ex),
                $"ensuring the shared docker network '{name}' failed: {ex.Message}");
        }
    }

    public async Task<DockerAttachableNetworkListResult> ListAttachableNetworksAsync(
        IDockerClient client,
        DockerProviderSettings settings,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(client);

        try
        {
            var networks = await client.Networks
                .ListNetworksAsync(new NetworksListParameters(), cancellationToken)
                .ConfigureAwait(false);

            return DockerAttachableNetworkListResult.Ok(
                (networks ?? [])
                    .Where(n => n is not null && !string.IsNullOrWhiteSpace(n.Name))
                    .Select(n => new DockerAttachableNetwork { Name = n.Name })
                    .ToList());
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return DockerAttachableNetworkListResult.Fail(
                ClassifyFailure(ex),
                $"listing the attachable docker networks failed: {ex.Message}");
        }
    }

    public DockerNetworkingPlan RenderNetworking(WorkloadDesiredSpec spec, string sharedNetworkName)
    {
        ArgumentNullException.ThrowIfNull(spec);
        ArgumentException.ThrowIfNullOrWhiteSpace(sharedNetworkName);

        var network = spec.Network;
        var attachments = network?.Attachments ?? [];

        var mode = network?.Mode ?? WorkloadNetworkMode.Cluster;
        var hostNetworking = network?.HostNetwork == true || mode == WorkloadNetworkMode.HostNetwork;
        if (hostNetworking && attachments.Count > 0)
        {
            throw new InvalidOperationException(
                $"incoherent network render for '{spec.ResourceName}': mode 'hostNetwork' cannot carry network "
                + $"attachments ({string.Join(", ", attachments)}) — a host-networked container has no endpoint to attach.");
        }

        if (mode == WorkloadNetworkMode.OperatorNetwork && attachments.Count == 0)
        {
            throw new InvalidOperationException(
                $"incoherent network render for '{spec.ResourceName}': mode 'operatorNetwork' requires at least one "
                + "network attachment, and none was resolved.");
        }

        if (network?.HostNetwork == true)
        {
            return new DockerNetworkingPlan { HostConfig = new HostConfig { NetworkMode = "host" } };
        }

        var hostConfig = new HostConfig { NetworkMode = sharedNetworkName };

        var portBindings = BuildPortBindings(network);
        if (portBindings.Count > 0)
        {
            hostConfig.PortBindings = portBindings;
        }

        return new DockerNetworkingPlan
        {
            HostConfig = hostConfig,

            Attachments = [.. attachments],
        };
    }

    private static string ResolveNetworkName(DockerProviderSettings? settings)
        => string.IsNullOrWhiteSpace(settings?.NetworkName)
            ? DockerProviderConstants.DefaultNetworkName
            : settings.NetworkName!.Trim();

    private static async Task<NetworkResponse?> FindNetworkByExactNameAsync(
        IDockerClient client,
        string name,
        CancellationToken cancellationToken)
    {
        var networks = await client.Networks.ListNetworksAsync(
            new NetworksListParameters
            {
                Filters = new Dictionary<string, IDictionary<string, bool>>(StringComparer.Ordinal)
                {
                    ["name"] = new Dictionary<string, bool>(StringComparer.Ordinal) { [name] = true },
                },
            },
            cancellationToken).ConfigureAwait(false);

        return networks.FirstOrDefault(n => string.Equals(n.Name, name, StringComparison.Ordinal));
    }

    private static IDictionary<string, IList<PortBinding>> BuildPortBindings(ResolvedNetwork? network)
    {
        var bindings = new Dictionary<string, IList<PortBinding>>(StringComparer.Ordinal);
        if (network is null)
        {
            return bindings;
        }

        foreach (var port in network.Ports)
        {
            if (port.HostPort is not { } hostPort)
            {
                continue;
            }

            var key = $"{port.Port.ToString(CultureInfo.InvariantCulture)}/{NormalizeProtocol(port.Protocol)}";
            if (!bindings.TryGetValue(key, out var list))
            {
                list = new List<PortBinding>();
                bindings[key] = list;
            }

            list.Add(new PortBinding { HostPort = hostPort.ToString(CultureInfo.InvariantCulture) });
        }

        return bindings;
    }

    private static string NormalizeProtocol(string? protocol)
        => string.Equals(protocol, "udp", StringComparison.OrdinalIgnoreCase) ? "udp" : "tcp";

    private static DockerFailureKind ClassifyFailure(Exception ex) => ex switch
    {
        DockerApiException => DockerFailureKind.Error,
        _ => DockerFailureKind.Unreachable,
    };
}
