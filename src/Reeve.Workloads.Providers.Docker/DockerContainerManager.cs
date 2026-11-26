using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Docker.DotNet;
using Docker.DotNet.Models;
using Reeve.Workloads.Abstractions.Domain.DesiredSpec;
using Reeve.Workloads.Abstractions.Providers;
using Reeve.Workloads.Domain.DesiredSpec;

namespace Reeve.Workloads.Providers.Docker;

internal interface IDockerAttachmentConnector
{
    Task<DockerContainerMutationResult> CreateAndStartAsync(
        IDockerClient client,
        CreateContainerParameters parameters,
        IReadOnlyList<string> attachments,
        CancellationToken cancellationToken);

    Task<DockerOperationResult> ReconcileAttachmentsAsync(
        IDockerClient client,
        string containerId,
        IReadOnlyList<string> attachments,
        CancellationToken cancellationToken);
}

internal static class DockerAttachmentSequence
{
    public static IReadOnlyList<string> DesiredAttachments(WorkloadDesiredSpec spec)
    {
        ArgumentNullException.ThrowIfNull(spec);
        return spec.Network?.Attachments ?? [];
    }

    public static Task<DockerContainerMutationResult> CreateConnectAndStartAsync(
        IDockerContainerManager containers,
        IDockerClient client,
        CreateContainerParameters parameters,
        IReadOnlyList<string> attachments,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(containers);
        ArgumentNullException.ThrowIfNull(attachments);

        if (containers is IDockerAttachmentConnector connector)
        {
            return connector.CreateAndStartAsync(client, parameters, attachments, cancellationToken);
        }

        return attachments.Count == 0
            ? containers.CreateAndStartAsync(client, parameters, cancellationToken)
            : Task.FromResult(DockerContainerMutationResult.Fail(DockerFailureKind.Rejected, Incapable(attachments)));
    }

    public static Task<DockerOperationResult> ReconcileAttachmentsAsync(
        IDockerContainerManager containers,
        IDockerClient client,
        string containerId,
        IReadOnlyList<string> attachments,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(containers);
        ArgumentNullException.ThrowIfNull(attachments);

        if (attachments.Count == 0)
        {
            return Task.FromResult(DockerOperationResult.Ok());
        }

        return containers is IDockerAttachmentConnector connector
            ? connector.ReconcileAttachmentsAsync(client, containerId, attachments, cancellationToken)
            : Task.FromResult(DockerOperationResult.Fail(DockerFailureKind.Rejected, Incapable(attachments)));
    }

    internal static string Incapable(IReadOnlyList<string> attachments)
        => $"this docker container manager cannot connect network attachments ({string.Join(", ", attachments)}), "
           + "so the workload would run WITHOUT them; refusing rather than starting an unattached container";
}

internal sealed class DockerContainerManager : IDockerContainerManager, IDockerAttachmentConnector
{
    public CreateContainerParameters BuildContainerParameters(DockerContainerRenderContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var spec = context.Spec;

        var labels = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var kv in context.Resource.MetricsLabels)
        {
            labels[kv.Key] = kv.Value;
        }

        foreach (var kv in DockerProviderConstants.BuildResourceLabels(spec))
        {
            labels[kv.Key] = kv.Value;
        }

        if (spec.RestartNonce != 0)
        {
            labels[DockerProviderConstants.RestartNonceLabelKey] =
                spec.RestartNonce.ToString(CultureInfo.InvariantCulture);
        }

        if (WorkloadSpecCredentials.Digest(context.Credentials) is { } credentialDigest)
        {
            labels[DockerProviderConstants.CredentialDigestLabelKey] = credentialDigest;
        }

        var env = BuildEnvironment(context);
        var exposedPorts = BuildExposedPorts(spec.Network);

        var privilegeHostConfig = context.Privilege.HostConfig;
        var resourceHostConfig = context.Resource.HostConfig;
        var networkHostConfig = context.NetworkHostConfig;

        var hostConfig = new HostConfig
        {
            CapDrop = privilegeHostConfig.CapDrop,
            CapAdd = privilegeHostConfig.CapAdd,
            Privileged = privilegeHostConfig.Privileged,
            Binds = privilegeHostConfig.Binds,
            Devices = privilegeHostConfig.Devices,
            GroupAdd = privilegeHostConfig.GroupAdd,

            Memory = resourceHostConfig.Memory,
            MemoryReservation = resourceHostConfig.MemoryReservation,
            NanoCPUs = resourceHostConfig.NanoCPUs,

            NetworkMode = networkHostConfig.NetworkMode,
            PortBindings = networkHostConfig.PortBindings,

            Mounts = BuildMounts(spec),

            RestartPolicy = new RestartPolicy { Name = RestartPolicyKind.UnlessStopped },
        };

        var parameters = new CreateContainerParameters
        {
            Image = spec.Image,
            Name = context.ContainerName,
            Labels = labels,
            Env = env,
            ExposedPorts = exposedPorts,
            HostConfig = hostConfig,
        };

        if (context.Privilege.User is { } user)
        {
            parameters.User = user;
        }

        return parameters;
    }

    public async Task<DockerContainerRef?> FindContainerAsync(
        IDockerClient client,
        WorkloadRef reference,
        CancellationToken cancellationToken)
    {
        var candidates = await ListInstanceContainersAsync(client, reference, cancellationToken).ConfigureAwait(false);
        if (candidates.Count == 0)
        {
            return null;
        }

        var chosen = candidates.FirstOrDefault(c => IsRunningState(c.State)) ?? candidates[0];
        return ToRef(chosen);
    }

    public async Task<IReadOnlyList<DockerContainerRef>> FindContainersAsync(
        IDockerClient client,
        WorkloadRef reference,
        CancellationToken cancellationToken)
    {
        var candidates = await ListInstanceContainersAsync(client, reference, cancellationToken).ConfigureAwait(false);
        return candidates.Select(ToRef).ToList();
    }

    private static async Task<List<ContainerListResponse>> ListInstanceContainersAsync(
        IDockerClient client,
        WorkloadRef reference,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(client);

        var instanceId = reference.InstanceId.ToString("D");

        var listed = await client.Containers.ListContainersAsync(
            new ContainersListParameters
            {
                All = true,
                Filters = new Dictionary<string, IDictionary<string, bool>>(StringComparer.Ordinal)
                {
                    ["label"] = new Dictionary<string, bool>(StringComparer.Ordinal)
                    {
                        [$"{DockerProviderConstants.InstanceLabelKey}={instanceId}"] = true,
                    },
                },
            },
            cancellationToken).ConfigureAwait(false);

        return (listed ?? new List<ContainerListResponse>())
            .Where(c => c.Labels is not null
                && c.Labels.TryGetValue(DockerProviderConstants.InstanceLabelKey, out var owner)
                && string.Equals(owner, instanceId, StringComparison.Ordinal))
            .ToList();
    }

    public async Task<IReadOnlyDictionary<string, string>> ReadHeldEnvAsync(
        IDockerClient client,
        DockerContainerRef container,
        IReadOnlyCollection<string> names,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(container);
        ArgumentNullException.ThrowIfNull(names);

        var held = new Dictionary<string, string>(StringComparer.Ordinal);
        if (names.Count == 0)
        {
            return held;
        }

        ContainerInspectResponse inspected;
        try
        {
            inspected = await client.Containers.InspectContainerAsync(container.Id, cancellationToken).ConfigureAwait(false);
        }
        catch (DockerApiException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            return held;
        }

        foreach (var entry in inspected.Config?.Env ?? [])
        {
            var separator = entry?.IndexOf('=', StringComparison.Ordinal) ?? -1;
            if (separator <= 0)
            {
                continue;
            }

            var name = entry![..separator];
            if (names.Contains(name, StringComparer.Ordinal))
            {
                held.TryAdd(name, entry[(separator + 1)..]);
            }
        }

        return held;
    }

    public Task<DockerContainerMutationResult> CreateAndStartAsync(
        IDockerClient client,
        CreateContainerParameters parameters,
        CancellationToken cancellationToken)
        => CreateAndStartAsync(client, parameters, [], cancellationToken);

    public async Task<DockerContainerMutationResult> CreateAndStartAsync(
        IDockerClient client,
        CreateContainerParameters parameters,
        IReadOnlyList<string> attachments,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(parameters);
        ArgumentNullException.ThrowIfNull(attachments);

        CreateContainerResponse created;
        try
        {
            created = await client.Containers.CreateContainerAsync(parameters, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return DockerContainerMutationResult.Fail(Classify(ex), ex.Message);
        }

        var createdRef = new DockerContainerRef
        {
            Id = created.ID,
            Name = parameters.Name ?? created.ID,
            SpecHash = LabelValue(parameters.Labels, DockerProviderConstants.SpecHashLabelKey),
            RestartNonce = LabelValue(parameters.Labels, DockerProviderConstants.RestartNonceLabelKey),
            IsRunning = false,
        };

        for (var i = 0; i < attachments.Count; i++)
        {
            var connected = await ConnectAttachmentAsync(client, created.ID, attachments[i], cancellationToken)
                .ConfigureAwait(false);
            if (connected.Succeeded)
            {
                continue;
            }

            var removed = await RemoveContainerAsync(client, created.ID, cancellationToken).ConfigureAwait(false);
            return DockerContainerMutationResult.Fail(
                connected.FailureKind,
                connected.Message,
                removed.Succeeded ? null : createdRef);
        }

        try
        {
            await client.Containers
                .StartContainerAsync(created.ID, new ContainerStartParameters(), cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return DockerContainerMutationResult.Fail(Classify(ex), ex.Message, createdRef);
        }

        return DockerContainerMutationResult.Ok(createdRef with { IsRunning = true });
    }

    public async Task<DockerOperationResult> ReconcileAttachmentsAsync(
        IDockerClient client,
        string containerId,
        IReadOnlyList<string> attachments,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentException.ThrowIfNullOrWhiteSpace(containerId);
        ArgumentNullException.ThrowIfNull(attachments);

        for (var i = 0; i < attachments.Count; i++)
        {
            var connected = await ConnectAttachmentAsync(client, containerId, attachments[i], cancellationToken)
                .ConfigureAwait(false);
            if (!connected.Succeeded)
            {
                return connected;
            }
        }

        return DockerOperationResult.Ok();
    }

    private static async Task<DockerOperationResult> ConnectAttachmentAsync(
        IDockerClient client,
        string containerId,
        string attachment,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(attachment))
        {
            return DockerOperationResult.Fail(
                DockerFailureKind.Rejected,
                "a blank attachment network name reached the docker connect sequence");
        }

        try
        {
            await client.Networks
                .ConnectNetworkAsync(
                    attachment,
                    new NetworkConnectParameters
                    {
                        Container = containerId,

                        EndpointConfig = new EndpointSettings
                        {
                            GwPriority = DockerProviderConstants.AttachmentGatewayPriority,
                        },
                    },
                    cancellationToken)
                .ConfigureAwait(false);
            return DockerOperationResult.Ok();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return ClassifyConnectFailure(ex, attachment);
        }
    }

    internal static DockerOperationResult ClassifyConnectFailure(Exception ex, string attachment)
    {
        var message = ex.Message ?? string.Empty;

        if (ex is DockerApiException { StatusCode: HttpStatusCode.Conflict }
            || message.Contains("endpoint already exists", StringComparison.OrdinalIgnoreCase)
            || message.Contains("already exists in network", StringComparison.OrdinalIgnoreCase))
        {
            return DockerOperationResult.Ok(
                $"the container is already connected to the attachment network '{attachment}' (idempotent connect)");
        }

        if (ex is DockerApiException { StatusCode: HttpStatusCode.NotFound })
        {
            return DockerOperationResult.Fail(
                DockerFailureKind.Rejected,
                $"the attachment network '{attachment}' does not exist on the docker daemon (404); "
                + "it was removed or re-created out of band — recreate it, or edit the workload's network attachments");
        }

        var refused = ex is DockerApiException { StatusCode: >= HttpStatusCode.BadRequest and < HttpStatusCode.InternalServerError }
            && ex is not DockerApiException { StatusCode: HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests };

        return DockerOperationResult.Fail(
            refused ? DockerFailureKind.Rejected : DockerFailureKind.Unreachable,
            $"the container could not be connected to the attachment network '{attachment}': {message}");
    }

    public async Task<DockerOperationResult> StartContainerAsync(
        IDockerClient client,
        string containerId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentException.ThrowIfNullOrWhiteSpace(containerId);

        try
        {
            await client.Containers
                .StartContainerAsync(containerId, new ContainerStartParameters(), cancellationToken)
                .ConfigureAwait(false);
            return DockerOperationResult.Ok();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return DockerOperationResult.Fail(Classify(ex), ex.Message);
        }
    }

    public async Task<DockerOperationResult> StopContainerAsync(
        IDockerClient client,
        string containerId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentException.ThrowIfNullOrWhiteSpace(containerId);

        try
        {
            await client.Containers
                .StopContainerAsync(containerId, new ContainerStopParameters(), cancellationToken)
                .ConfigureAwait(false);
            return DockerOperationResult.Ok();
        }
        catch (DockerApiException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            return DockerOperationResult.Ok();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return DockerOperationResult.Fail(Classify(ex), ex.Message);
        }
    }

    public async Task<DockerOperationResult> RemoveContainerAsync(
        IDockerClient client,
        string containerId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentException.ThrowIfNullOrWhiteSpace(containerId);

        try
        {
            await client.Containers
                .RemoveContainerAsync(containerId, new ContainerRemoveParameters { Force = true }, cancellationToken)
                .ConfigureAwait(false);
            return DockerOperationResult.Ok();
        }
        catch (DockerApiException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            return DockerOperationResult.Ok();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return DockerOperationResult.Fail(Classify(ex), ex.Message);
        }
    }

    private static IList<string> BuildEnvironment(DockerContainerRenderContext context)
    {
        var spec = context.Spec;
        var credentials = WorkloadSpecCredentials.Of(spec);
        var env = new List<string>(spec.Env.Count + spec.ProvisionedCredentials.Count);
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var e in spec.Env)
        {
            var value = e.Kind switch
            {
                ResolvedEnvKind.SecretRef => ResolveSecretRef(spec, credentials, context, e),
                ResolvedEnvKind.SpecHash => spec.SpecHash,
                _ => e.Value ?? string.Empty,
            };

            env.Add($"{e.Name}={value}");
            seen.Add(e.Name);
        }

        foreach (var credential in credentials.Credentials)
        {
            foreach (var name in credential.Names.Where(seen.Add))
            {
                env.Add($"{name}={CredentialValue(context, credential.CredentialId, name)}");
            }
        }

        return env;
    }

    private static string ResolveSecretRef(
        WorkloadDesiredSpec spec, WorkloadSpecCredentials credentials, DockerContainerRenderContext context, ResolvedEnvVar env)
    {
        if (spec.SecretMaterial.TryGetValue(env.Name, out var material))
        {
            return material;
        }

        if (credentials.CredentialFor(env) is { } credentialId)
        {
            return CredentialValue(context, credentialId, env.Name);
        }

        throw new WorkloadSecretMaterialMissingException(
            $"env '{env.Name}' is a secret reference but no material was resolved for it");
    }

    private static string CredentialValue(DockerContainerRenderContext context, string credentialId, string name)
        => context.Credentials.TryGetValue(credentialId, out var value)
            ? value
            : throw new WorkloadSecretMaterialMissingException(
                $"env '{name}' carries credential '{credentialId}', which was not resolved for this apply");

    private static IDictionary<string, EmptyStruct>? BuildExposedPorts(ResolvedNetwork? network)
    {
        var ports = network?.Ports;
        if (ports is null || ports.Count == 0)
        {
            return null;
        }

        var exposed = new Dictionary<string, EmptyStruct>(StringComparer.Ordinal);
        foreach (var port in ports)
        {
            var key = $"{port.Port.ToString(CultureInfo.InvariantCulture)}/{NormalizeProtocol(port.Protocol)}";
            exposed[key] = default;
        }

        return exposed;
    }

    private static string NormalizeProtocol(string? protocol)
        => string.Equals(protocol, "udp", StringComparison.OrdinalIgnoreCase) ? "udp" : "tcp";

    private static IList<Mount>? BuildMounts(WorkloadDesiredSpec spec)
    {
        var volumes = spec.Volumes;
        if (volumes is null || volumes.Count == 0)
        {
            return null;
        }

        var mounts = new List<Mount>(volumes.Count);
        foreach (var v in volumes)
        {
            mounts.Add(new Mount
            {
                Type = "volume",
                Source = v.ResourceName,
                Target = v.MountPath,
                ReadOnly = IsReadOnlyAccessMode(v.AccessMode),
            });
        }

        return mounts;
    }

    internal static bool IsReadOnlyAccessMode(string? accessMode)
        => accessMode is not null && accessMode.StartsWith("ro", StringComparison.OrdinalIgnoreCase);

    private static DockerContainerRef ToRef(ContainerListResponse container) => new()
    {
        Id = container.ID,
        Name = NormalizeContainerName(container.Names),
        SpecHash = LabelValue(container.Labels, DockerProviderConstants.SpecHashLabelKey),
        RestartNonce = LabelValue(container.Labels, DockerProviderConstants.RestartNonceLabelKey),
        IsRunning = IsRunningState(container.State),
    };

    private static string NormalizeContainerName(IList<string>? names)
    {
        var first = names?.FirstOrDefault();
        if (string.IsNullOrEmpty(first))
        {
            return string.Empty;
        }

        return first.StartsWith('/') ? first[1..] : first;
    }

    private static bool IsRunningState(string? state)
        => string.Equals(state, "running", StringComparison.OrdinalIgnoreCase);

    private static string? LabelValue(IDictionary<string, string>? labels, string key)
        => labels is not null && labels.TryGetValue(key, out var value) ? value : null;

    internal static DockerFailureKind ClassifyForTests(Exception ex) => Classify(ex);

    private static DockerFailureKind Classify(Exception ex) => ex switch
    {
        DockerApiException api when IsPortAlreadyAllocated(api) => DockerFailureKind.Rejected,
        DockerApiException api => api.StatusCode >= HttpStatusCode.InternalServerError
            ? DockerFailureKind.Error
            : DockerFailureKind.Rejected,
        HttpRequestException => DockerFailureKind.Unreachable,
        SocketException => DockerFailureKind.Unreachable,
        IOException => DockerFailureKind.Unreachable,
        TimeoutException => DockerFailureKind.Unreachable,
        OperationCanceledException => DockerFailureKind.Unreachable,
        _ => DockerFailureKind.Error,
    };

    private static bool IsPortAlreadyAllocated(DockerApiException api)
        => api.Message.Contains("port is already allocated", StringComparison.OrdinalIgnoreCase)
        || api.Message.Contains("address already in use", StringComparison.OrdinalIgnoreCase);
}
